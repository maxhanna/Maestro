using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Weaver.Controllers;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public sealed class AgentControllerBenchmarkRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weaver-agent-routing-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DeterministicBenchmarkRoutingResolvesScalarAndSectionEdits()
    {
        var method = typeof(AgentController).GetMethod(
            "TryBuildDeterministicEdit", BindingFlags.Static | BindingFlags.NonPublic)!;

        var property = method.Invoke(null, [
            "namespace Fixture;\npublic sealed class CacheOptions\n{\n    public int MaxEntries { get; set; } = 100;\n}\n",
            new PlanStep { Change = "Update MaxEntries to 250 in CacheOptions.cs." },
            "edit_strategy/property-update/CacheOptions.cs"]);
        Assert.NotNull(property);
        var propertyTuple = property!.GetType();
        Assert.Contains("= 100;", (string)propertyTuple.GetField("Item1")!.GetValue(property)!);
        Assert.Contains("= 250;", (string)propertyTuple.GetField("Item2")!.GetValue(property)!);

        var html = method.Invoke(null, [
            "<section id=\"general\"><button>Save</button></section>\n<section id=\"users\"><button>Save</button></section>\n",
            new PlanStep { Change = "Change only the Save button inside the users section to say Save Users." },
            "edit_strategy/ambiguous-section/settings.html"]);
        Assert.NotNull(html);
        var htmlOld = (string)html!.GetType().GetField("Item1")!.GetValue(html)!;
        var htmlNew = (string)html.GetType().GetField("Item2")!.GetValue(html)!;
        Assert.Contains("<section id=\"users\">", htmlOld);
        Assert.DoesNotContain("<section id=\"general\">", htmlOld);
        Assert.Contains("<section id=\"users\">", htmlNew);
        Assert.Contains("Save Users", htmlNew);
        Assert.DoesNotContain("Save Users", htmlOld);

        var insertionMethod = typeof(AgentController).GetMethod(
            "TryBuildDeterministicMethodInsertion", BindingFlags.Static | BindingFlags.NonPublic)!;
        var insertion = insertionMethod.Invoke(null, [
            "export class UserService {\n  getName(id: number): string {\n    return `user-${id}`;\n  }\n}\n",
            new PlanStep { Change = "Add an isValidId(id: number): boolean method that returns true only for positive IDs." },
            "edit_strategy/typescript/user-service.ts",
            null]);
        Assert.NotNull(insertion);
        Assert.Contains("isValidId(id: number): boolean", (string)insertion!.GetType().GetField("Item2")!.GetValue(insertion)!);

        var csharpInsertion = insertionMethod.Invoke(null, [
            "namespace Fixture;\n\npublic sealed class PriceService\n{\n    public decimal ApplyTax(decimal price)\n    {\n        return price * 1.2m;\n    }\n\n    public decimal ApplyDiscount(decimal price, decimal discount)\n    {\n        return price - discount;\n    }\n}\n",
            new PlanStep { Change = "Add a public decimal ClampToZero(decimal price) method after ApplyTax. It must return 0 when price is negative and otherwise return price." },
            "edit_strategy/method-insertion/PriceService.cs",
            "Insert a complete method into an existing C# service" ]);
        Assert.NotNull(csharpInsertion);
        var csharpReplacement = (string)csharpInsertion!.GetType().GetField("Item2")!.GetValue(csharpInsertion)!;
        Assert.Contains("decimal ClampToZero(decimal price)", csharpReplacement);
        Assert.Contains("price < 0", csharpReplacement);
    }

    [Fact]
    public async Task BenchmarkCommand_UsesSandboxRunnerWithoutStartingHostTerminal()
    {
        Directory.CreateDirectory(_root);
        var environment = new FakeEnvironment(_root);
        var configFile = new ConfigFileService(environment);
        using var terminal = new TerminalService(configFile);
        var runner = new RecordingBenchmarkTerminalRunner();
        var controller = new AgentController(
            null!, null!, environment, terminal, new FileHintsManager(_root), configFile,
            new EmailService(configFile), new BoardDataService(Path.Combine(_root, "board.json"), null!), runner);

        typeof(AgentController).GetField("_isBenchmark", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, true);
        var method = typeof(AgentController).GetMethod("RunAgentCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<CommandCheckOutcome>)method.Invoke(
            controller, ["touch /workspace/result.txt", "/workspace", 30, CancellationToken.None])!;

        var outcome = await task;

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("touch /workspace/result.txt", runner.Command);
        Assert.Equal("/workspace", runner.WorkingDirectory);
        Assert.False(terminal.IsRunning);
    }

    [Fact]
    public async Task PrepareAgentApplyEvaluate_CompletesBenchmarkWorkflow()
    {
        Directory.CreateDirectory(_root);
        var environment = new FakeEnvironment(_root);
        var benchmarkService = new BenchmarkService(Path.Combine(_root, "data"));
        var prepared = await benchmarkService.PrepareAsync(1, benchmarkService.SandboxRoot);
        var configFile = new ConfigFileService(environment);
        using var terminal = new TerminalService(configFile);
        var runner = new RecordingBenchmarkTerminalRunner();
        var agent = new AgentController(
            null!, new ConfigurationBuilder().Build(), environment, terminal, new FileHintsManager(_root), configFile,
            new EmailService(configFile), new BoardDataService(Path.Combine(_root, "board.json"), null!), runner)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var applied = await agent.ApplyEdits(new ApplyEditsRequest
        {
            IsBenchmark = true,
            BenchmarkRunId = prepared.RunId,
            Edits = [new EditAction
            {
                Path = "benchmark_test_1/test.md",
                NewString = "Hello world\nThe capital of France is Paris"
            }]
        });
        Assert.IsType<OkObjectResult>(applied);

        var benchmark = new BenchmarkController(environment);
        var evaluated = await benchmark.Evaluate(new BenchmarkEvaluationRequest
        {
            Level = 1,
            RunId = prepared.RunId,
            ModelUsed = "workflow-test"
        }, CancellationToken.None);
        var score = Assert.IsType<BenchmarkScore>(Assert.IsType<OkObjectResult>(evaluated).Value);

        Assert.Equal(100, score.ScorePercent);
        Assert.Equal("workflow-test", score.ModelUsed);
        Assert.Single(Assert.IsType<List<BenchmarkScore>>(Assert.IsType<OkObjectResult>(benchmark.GetScores()).Value));
    }

    [Fact]
    public async Task BenchmarkApply_UsesSandboxRunnerForSubmittedCommands()
    {
        Directory.CreateDirectory(_root);
        var benchmarkService = new BenchmarkService(Path.Combine(_root, "data"));
        var prepared = await benchmarkService.PrepareAsync(1, benchmarkService.SandboxRoot);
        var environment = new FakeEnvironment(_root);
        var configFile = new ConfigFileService(environment);
        using var terminal = new TerminalService(configFile);
        var runner = new RecordingBenchmarkTerminalRunner();
        var controller = new AgentController(
            null!, new ConfigurationBuilder().Build(), environment, terminal, new FileHintsManager(_root), configFile,
            new EmailService(configFile), new BoardDataService(Path.Combine(_root, "board.json"), null!), runner)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = await controller.ApplyEdits(new ApplyEditsRequest
        {
            IsBenchmark = true,
            BenchmarkRunId = prepared.RunId,
            Edits = [new EditAction { Path = "agent.txt", NewString = "created" }],
            Commands = [new CommandAction { Command = "touch /workspace/command.txt" }]
        });

        var ok = Assert.IsType<OkObjectResult>(response);
        Assert.NotNull(ok.Value);
        Assert.Equal("touch /workspace/command.txt", runner.Command);
        Assert.Equal(prepared.RunRoot, runner.WorkingDirectory);
        Assert.False(terminal.IsRunning);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingBenchmarkTerminalRunner : IBenchmarkTerminalRunner
    {
        public string? Command { get; private set; }
        public string? WorkingDirectory { get; private set; }

        public Task<CommandCheckOutcome> RunAsync(string command, string workingDirectory, CancellationToken ct)
        {
            Command = command;
            WorkingDirectory = workingDirectory;
            return Task.FromResult(new CommandCheckOutcome(0, false, 1, "sandbox output", "", "ok"));
        }
    }

    private sealed class FakeEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Weaver.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
