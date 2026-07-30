using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Weaver.Controllers;
using Weaver.IntegrationTests.Fakes;
using Weaver.Services;
using Xunit;

namespace Weaver.IntegrationTests;

/// <summary>
/// Proves the noReplan + plan-merge fix (AgentController.cs ~line 8700) holds inside a
/// real Orchestrate() run: real routing, a real CommandExecutionPipeline loop driving a
/// real TerminalService against real files on disk, and a real chaining decision — with
/// only the LLM (via FakeLlmHandler) and StepResolutionPipeline's own internals (via
/// TestableAgentController's override) substituted. See the "Orchestrator chaining test
/// harness" scoping discussion for why StepResolutionPipeline itself is stubbed rather than
/// driven for real.
/// </summary>
public class OrchestratorChainingTests : IDisposable
{
    readonly string _scratchDir;
    readonly TerminalService _terminal;
    readonly ConfigFileService _configFile;

    public OrchestratorChainingTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "weaver-orch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);

        var env = new TestWebHostEnvironment { ContentRootPath = _scratchDir };
        _configFile = new ConfigFileService(env);
        _terminal = new TerminalService(_configFile);
    }

    public void Dispose()
    {
        _terminal.Dispose();
        try { Directory.Delete(_scratchDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Prompt deliberately avoids ClassifyTask's simple-intent fast path, the
    /// "fix the build" fast path, and every hasCodeInPrompt/mentionsCodeFiles/
    /// mentionsCodeLogic keyword — otherwise Orchestrate skips the LLM routing-verify
    /// call this test needs to script.</summary>
    const string Prompt = "Run the data export command and read the terminal output to produce a summary results file.";

    [Fact]
    public async Task Orchestrate_ChainedCommandExecutionToCodeEdit_TagsChainedStepsReplanAndMergesPlanCounts()
    {
        // ── Script the only two real LLM calls this scenario makes: the routing-verify
        //    call, then CommandExecutionPipeline's plan -> cmd -> done loop. StepResolutionPipeline
        //    itself is stubbed below, so it makes none.
        var routingVerifyResponse =
            """{"decision":"chain","stages":[{"pipeline":"CommandExecution","summary":"run export command"},{"pipeline":"UnifiedPipeline","summary":"update summary file"}]}""";
        var planResponse =
            """{"plan":[{"file":"scratch_result.txt","change":"create export result file"}]}""";
        // SendCommandAsync Set-Location's into projectRoot first, so a relative path is fine.
        var cmdResponse =
            """{"cmd":"New-Item -ItemType File -Path 'scratch_result.txt' -Force | Out-Null; Set-Content 'scratch_result.txt' -Value 'exported-data'"}""";
        var doneResponse = """{"done":true,"summary":"Export command completed"}""";

        var handler = new FakeLlmHandler(new[] { routingVerifyResponse, planResponse, cmdResponse, doneResponse });
        var httpClientFactory = new FakeHttpClientFactory(handler);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Model"] = "test-model"
        }).Build();

        var env = new TestWebHostEnvironment { ContentRootPath = _scratchDir };
        var fileHints = new FileHintsManager(_scratchDir);
        var emailService = new EmailService(_configFile);
        var boardData = new BoardDataService(
            Path.Combine(_scratchDir, "board.json"), NullLogger<BoardDataService>.Instance);
        var push = new PushNotificationService(_scratchDir);

        var controller = new TestableAgentController(
            httpClientFactory, config, env, _terminal, fileHints, _configFile, emailService, boardData, push);

        // Skip the real TCP connectivity probe entirely — it's orthogonal to what this
        // test verifies and would otherwise spawn an extra shell process per run and
        // depend on network state. _lastConnectionCheckResult already defaults true;
        // this only needs to make the 5-minute cache look populated.
        typeof(AgentController)
            .GetField("_nextConnectivityCheck", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, DateTime.UtcNow.AddMinutes(5));

        controller.StepResolutionPipelineStub = (prompt, projectRoot, attachedFiles) =>
        {
            // Prove the chained stage actually receives what stage 1 produced.
            Assert.NotNull(attachedFiles);
            Assert.Contains(attachedFiles!, f => f.EndsWith("scratch_result.txt", StringComparison.OrdinalIgnoreCase));

            var editedPath = Path.Combine(projectRoot, "scratch_result.txt");
            File.AppendAllText(editedPath, "\nedited-by-stage-2");

            var steps = new List<object>
            {
                new Dictionary<string, object?> { ["type"] = "edit", ["status"] = "done", ["path"] = "scratch_result.txt" },
                new Dictionary<string, object?> { ["type"] = "done_signal", ["status"] = "done" },
            };
            var plan = new AgentPlan
            {
                Plan = new List<PlanStep> { new() { File = "scratch_result.txt", Change = "append stage-2 content" } }
            };
            return (steps, plan, true);
        };

        var (allSteps, plan, complete) = await controller.Orchestrate(
            Prompt, _scratchDir, emitSse: false, ct: CancellationToken.None, cardId: null);

        // ── The real file created by stage 1 and edited by the stubbed stage 2 exists
        //    and reflects both writes — proves this ran for real, not just returned
        //    scripted shapes.
        var resultPath = Path.Combine(_scratchDir, "scratch_result.txt");
        Assert.True(File.Exists(resultPath));
        var resultContent = await File.ReadAllTextAsync(resultPath);
        Assert.Contains("exported-data", resultContent);
        Assert.Contains("edited-by-stage-2", resultContent);

        // ── Fix #1: every step the chained stage contributed is tagged "replan".
        var stepDicts = allSteps.OfType<Dictionary<string, object?>>().ToList();
        var editStep = stepDicts.Single(d => (string?)d.GetValueOrDefault("type") == "edit");
        Assert.Equal("replan", editStep["origin"]);
        var chainedDoneSignals = stepDicts.Where(d => (string?)d.GetValueOrDefault("type") == "done_signal").ToList();
        Assert.Equal(2, chainedDoneSignals.Count); // one from CommandExecutionPipeline, one from the stub
        Assert.Contains(chainedDoneSignals, d => (string?)d.GetValueOrDefault("origin") == "replan");
        // The stage-1 done_signal (from the real CommandExecutionPipeline loop) must NOT
        // be retagged — it really was part of the original plan.
        Assert.Contains(chainedDoneSignals, d => !d.ContainsKey("origin"));

        // ── Fix #2: plan step counts are combined, not overwritten — stage 1 planned 1
        //    step (scratch_result.txt create), stage 2 (stubbed) planned 1 step
        //    (scratch_result.txt append). Both must survive.
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Plan.Count);

        Assert.True(complete);
    }
}
