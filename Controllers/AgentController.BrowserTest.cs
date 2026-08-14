using Microsoft.AspNetCore.Mvc;
using System.Text;
using Weaver.Services;
using Weaver;

namespace Weaver.Controllers;

/// <summary>
/// LIVE WEB TEST pipeline — the deterministic answer to "test the kanban board" /
/// "verify the calendar page loads" style prompts. No LLM is involved anywhere:
/// the classifier decides the intent, the launcher detects and starts the project's
/// own server, the browser (or HTTP probe) opens it, section discovery finds the
/// thing the prompt named, and the verifier reports what is actually there. A very
/// basic model gets exactly the same result as a strong one.
///
/// Wired two ways:
///  • STRICT TEST INTENT — OrchestrateCore short-circuits the whole planning loop when
///    the prompt is strictly a test task (TestIntentClassifier), so the run never
///    burns tokens on planning an edit nobody asked for.
///  • "_browser_test" STEP TOOL — planners can also propose a live test mid-plan
///    (e.g. after an edit: "test the button you just added"), executed like any
///    other step tool.
/// </summary>
partial class AgentController
{
    // Swappable in tests (mirrors _scraperService/_runtimeProbe). Real default: launch
    // a headless Chromium when one is installed, else the HTTP/AngleSharp fallback.
    private BrowserAutomationService _browserTestService = new()
    {
        BrowserFactory = CdpBrowserDriver.TryCreateAsync
    };

    /// <summary>
    /// The strict-test-intent short-circuit: spin up the project's server and verify
    /// the named feature, then return the steps + verdict. Zero LLM calls.
    /// </summary>
    private async Task<(List<object> allSteps, AgentPlan plan, bool complete)> RunLiveWebTestPipeline(
        string prompt, string projectRoot, bool emitSse, TestIntentClassifier.TestIntent testIntent, CancellationToken ct)
    {
        var allSteps = new List<object>();
        var kind = testIntent.Intent == TestIntentClassifier.Kind.Api ? "API" : "UI";
        await EmitLog(emitSse, "info",
            $"🔬 Strict test intent detected ({kind}) — running the live web test pipeline for \"{testIntent.Target}\"", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new
            {
                phase = "test",
                message = $"Live web test: {testIntent.Target}",
                target = testIntent.Target,
                intent = testIntent.Intent.ToString()
            }, ct);

        BrowserTestReport report;
        try
        {
            report = testIntent.Intent == TestIntentClassifier.Kind.Api
                ? await _browserTestService.RunApiTestAsync(projectRoot, testIntent.Target, ct)
                : await _browserTestService.RunUiTestAsync(projectRoot, testIntent.Target, prompt, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            report = new BrowserTestReport
            {
                Target = testIntent.Target,
                Mode = "failed",
                LaunchError = ex.Message,
                Findings = { new TestFinding("fail", $"Live web test crashed: {ex.Message}") }
            };
        }

        var stepIndex = 0;
        if (!string.IsNullOrWhiteSpace(report.ServerUrl))
        {
            allSteps.Add(new Dictionary<string, object?>
            {
                ["index"] = stepIndex++,
                ["type"] = "server",
                ["status"] = "done",
                ["path"] = "server",
                ["url"] = report.ServerUrl,
                ["description"] = $"Server up at {report.ServerUrl}"
            });
            if (emitSse)
                await SendSse(Response, "step", new
                {
                    index = stepIndex - 1,
                    type = "server",
                    status = "done",
                    path = "server",
                    url = report.ServerUrl,
                    description = $"Server up at {report.ServerUrl}",
                    message = $"Live server started ({report.ServerKind})"
                }, ct);
        }
        allSteps.Add(new Dictionary<string, object?>
        {
            ["index"] = stepIndex++,
            ["type"] = "browse",
            ["status"] = report.Mode == "failed" ? "error" : "done",
            ["path"] = "browser",
            ["mode"] = report.Mode,
            ["description"] = $"Inspecting \"{report.Target}\" (mode: {report.Mode})",
            ["section"] = report.SectionLabel,
            ["navigations"] = report.Navigations
        });
        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = stepIndex - 1,
                type = "browse",
                status = report.Mode == "failed" ? "error" : "done",
                path = "browser",
                mode = report.Mode,
                description = $"Inspecting \"{report.Target}\" (mode: {report.Mode})",
                section = report.SectionLabel,
                navigations = report.Navigations,
                message = report.SectionLabel != null
                    ? $"Found section \"{report.SectionLabel}\""
                    : "No matching section found — verified the current page"
            }, ct);

        allSteps.Add(new Dictionary<string, object?>
        {
            ["index"] = stepIndex++,
            ["type"] = "verify",
            ["status"] = report.Passed ? "done" : "error",
            ["path"] = "test",
            ["description"] = report.Passed
                ? $"Live web test PASSED — {report.Findings.Count(f => f.Kind == "pass")} checks passed"
                : $"Live web test FAILED — {report.Findings.Count(f => f.Kind == "fail")} check(s) failed",
            ["findings"] = report.Findings.Select(f => new { f.Kind, f.Message }).ToList()
        });
        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = stepIndex - 1,
                type = "verify",
                status = report.Passed ? "done" : "error",
                path = "test",
                description = report.Passed ? "Live web test PASSED" : "Live web test FAILED",
                findings = report.Findings.Select(f => new { f.Kind, f.Message }).ToList()
            }, ct);

        await EmitLog(emitSse, report.Passed ? "success" : "error", report.ToString(), ct: ct);

        var plan = new AgentPlan
        {
            Summary = report.Passed
                ? $"Live web test passed: {report.Target} ({report.Mode}, {report.Findings.Count(f => f.Kind == "pass")} checks)"
                : $"Live web test failed: {report.Target}",
            Thinking = report.ToString()
        };
        return (allSteps, plan, report.Passed);
    }

    /// <summary>Executes a "_browser_test" plan step: runs the live web test for the
    /// feature named in the step's change and appends the report as step results.</summary>
    private async Task<int> ExecuteBrowserTestStep(
        string changeDesc, string prompt, string projectRoot, bool emitSse,
        CancellationToken ct, List<object> allResults, int stepIndex)
    {
        await EmitLog(emitSse, "info", $"_browser_test: live-testing \"{changeDesc}\" …", ct: ct);
        var target = changeDesc.Trim();
        var testIntent = new TestClassifierTarget(target);
        BrowserTestReport report;
        try
        {
            report = testIntent.IsApi
                ? await _browserTestService.RunApiTestAsync(projectRoot, target, ct)
                : await _browserTestService.RunUiTestAsync(projectRoot, target, prompt, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            report = new BrowserTestReport
            {
                Target = target, Mode = "failed", LaunchError = ex.Message,
                Findings = { new TestFinding("fail", $"Live web test crashed: {ex.Message}") }
            };
        }

        allResults.Add(new Dictionary<string, object?>
        {
            ["index"] = stepIndex,
            ["type"] = "browser_test",
            ["status"] = report.Passed ? "done" : "error",
            ["path"] = "browser_test",
            ["url"] = report.ServerUrl,
            ["mode"] = report.Mode,
            ["section"] = report.SectionLabel,
            ["description"] = $"Live web test \"{target}\": {(report.Passed ? "PASSED" : "FAILED")}",
            ["output"] = report.ToString()
        });
        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = stepIndex,
                type = "browser_test",
                status = report.Passed ? "done" : "error",
                path = "browser_test",
                url = report.ServerUrl,
                mode = report.Mode,
                section = report.SectionLabel,
                description = $"Live web test \"{target}\": {(report.Passed ? "PASSED" : "FAILED")}",
                message = report.ToString()
            }, ct);
        await EmitLog(emitSse, report.Passed ? "success" : "error", report.ToString(), ct: ct);
        return stepIndex + 1;
    }

    /// <summary>Whether a "_browser_test" step targets an API endpoint vs the UI.
    /// Mirrors the classifier's API rule for plan steps whose change names a route.</summary>
    private sealed class TestClassifierTarget
    {
        public bool IsApi { get; }

        public TestClassifierTarget(string change)
        {
            var lower = change.ToLowerInvariant();
            IsApi = lower.Contains("/api/") || lower.StartsWith("api ") || lower.StartsWith("api/") ||
                    lower.Contains(" endpoint") || lower.Contains("endpoint ") || lower.Contains(" route ");
        }
    }
}