using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.RegularExpressions;
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

    // Lazy-created orchestrator (needs the injected _db, so it can't be a field initializer).
    // Tests swap this via reflection with a fake-hosted orchestrator.
    private BenchmarkCardOrchestrator? _benchmarkOrchestrator;
    private BenchmarkCardOrchestrator BenchmarkOrchestrator =>
        _benchmarkOrchestrator ??= new BenchmarkCardOrchestrator(_db);

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
        _browserTestService.OnProgress = MakeWebtestProgressSink(emitSse);
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
        finally
        {
            _browserTestService.OnProgress = null;
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
        _browserTestService.OnProgress = MakeWebtestProgressSink(emitSse);
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
        finally
        {
            _browserTestService.OnProgress = null;
        }

        allResults.Add(new Dictionary<string, object?>
        {
            ["index"] = stepIndex,
            ["type"] = "browser_test",
            ["status"] = report.Passed ? "done" : "error",
            ["path"] = "browser_test",
            ["url"] = report.ServerUrl,
            ["mode"] = report.Mode,
            ["target"] = target,
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

    /// <summary>Executes a "_benchmark_verify" plan step: runs the named benchmark's
    /// acceptance checks (filesystem + live web test) end-to-end — the deterministic way a
    /// self-improving card proves a benchmark change actually works, by reading the screen
    /// AND checking the filesystem.</summary>
    private async Task<int> ExecuteBenchmarkVerifyStep(
        string changeDesc, string projectRoot, bool emitSse,
        CancellationToken ct, List<object> allResults, int stepIndex)
    {
        var level = ExtractBenchmarkLevel(changeDesc);
        if (level == null)
        {
            await EmitLog(emitSse, "error", $"_benchmark_verify: could not parse a benchmark level from \"{changeDesc}\"", ct: ct);
            return stepIndex;
        }

        var benchmark = new BenchmarkService(_db);
        var custom = benchmark.LoadCustomSystemInfo();
        var benchRoot = BenchmarkService.ResolveBenchmarkRoot(custom?.BenchmarkProjectRoot);
        benchmark.BrowserTest.OnProgress = MakeWebtestProgressSink(emitSse);

        List<BenchmarkCheckResult> results;
        try
        {
            results = await benchmark.EvaluateChecksAsync(level.Value, benchRoot, ct);
        }
        catch (Exception ex)
        {
            results = new List<BenchmarkCheckResult>
            {
                new BenchmarkCheckResult { Name = "verify", Passed = false, Message = ex.Message }
            };
        }
        finally
        {
            benchmark.BrowserTest.OnProgress = null;
        }

        var passed = results.Count(r => r.Passed);
        var failed = results.Where(r => !r.Passed).ToList();
        var allPassed = results.Count > 0 && failed.Count == 0;
        var summary = allPassed
            ? $"Benchmark {level} verified — {passed}/{results.Count} checks passed"
            : $"Benchmark {level} NOT verified — {failed.Count} of {results.Count} check(s) failed";

        var checksPayload = results.Select(r => new { r.Name, r.Passed, r.Message }).ToList();
        allResults.Add(new Dictionary<string, object?>
        {
            ["index"] = stepIndex,
            ["type"] = "benchmark_verify",
            ["status"] = allPassed ? "done" : "error",
            ["path"] = "benchmark_verify",
            ["level"] = level,
            ["description"] = summary,
            ["checks"] = checksPayload
        });
        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = stepIndex,
                type = "benchmark_verify",
                status = allPassed ? "done" : "error",
                path = "benchmark_verify",
                level = level,
                description = summary,
                checks = checksPayload
            }, ct);
        await EmitLog(emitSse, allPassed ? "success" : "error", summary, new { level = level, checks = results }, ct: ct);
        return stepIndex + 1;
    }

    /// <summary>Executes a "_benchmark_orchestrate" plan step: spins up a FRESH Weaver instance,
    /// injects the benchmark card, runs it, and verifies the result end-to-end. This is the
    /// self-improving column's full loop — the deterministic core (launch/inject/run/verify)
    /// with zero extra planning, so a basic model gets the same result as a strong one.</summary>
    private async Task<int> ExecuteBenchmarkOrchestrateStep(
        string changeDesc, string projectRoot, bool emitSse,
        CancellationToken ct, List<object> allResults, int stepIndex)
    {
        var level = ExtractBenchmarkLevel(changeDesc);
        if (level == null)
        {
            await EmitLog(emitSse, "error", $"_benchmark_orchestrate: could not parse a benchmark level from \"{changeDesc}\"", ct: ct);
            return stepIndex;
        }

        var plan = BenchmarkService.GetBenchmarkPlans().FirstOrDefault(p => p.Level == level.Value);
        if (plan == null)
        {
            await EmitLog(emitSse, "error", $"_benchmark_orchestrate: unknown benchmark level {level.Value}", ct: ct);
            return stepIndex;
        }

        await EmitLog(emitSse, "info", $"_benchmark_orchestrate: spinning up a fresh instance to run benchmark {level.Value} end-to-end…", ct: ct);

        var orchestrator = BenchmarkOrchestrator;
        Func<BenchmarkOrchestrationEvent, CancellationToken, Task> onProgress = async (e, ct2) =>
        {
            await EmitLog(emitSse, "info", $"🧪 {e.Stage}: {e.Message}", ct: ct2);
            if (emitSse)
                await SendSse(Response, "webtest", new { phase = e.Stage, url = e.Url, message = e.Message }, ct2);
        };

        var result = await orchestrator.OrchestrateAsync(
            new BenchmarkOrchestrationRequest(plan.Description, level.Value, "selfImproving", EndpointId: null),
            onProgress, ct);

        var summary = result.Succeeded
            ? $"Benchmark {level} orchestrated end-to-end — fresh instance ran the card and {result.Checks.Count(c => c.Passed)}/{result.Checks.Count} checks passed"
            : result.Verified
                ? $"Benchmark {level} verified but the run flagged a problem: {result.Error}"
                : $"Benchmark {level} orchestration did not verify — {result.Checks.Count(c => !c.Passed)} of {result.Checks.Count} check(s) failed{(string.IsNullOrWhiteSpace(result.Error) ? "" : $" ({result.Error})")}";

        var checksPayload = result.Checks.Select(r => new { r.Name, r.Passed, r.Message }).ToList();
        allResults.Add(new Dictionary<string, object?>
        {
            ["index"] = stepIndex,
            ["type"] = "benchmark_orchestrate",
            ["status"] = result.Succeeded ? "done" : "error",
            ["path"] = "benchmark_orchestrate",
            ["level"] = level,
            ["instanceUrl"] = result.InstanceUrl,
            ["cardId"] = result.CardId,
            ["workspace"] = result.WorkspaceRoot,
            ["description"] = summary,
            ["checks"] = checksPayload
        });
        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = stepIndex,
                type = "benchmark_orchestrate",
                status = result.Succeeded ? "done" : "error",
                path = "benchmark_orchestrate",
                level = level,
                instanceUrl = result.InstanceUrl,
                cardId = result.CardId,
                workspace = result.WorkspaceRoot,
                description = summary,
                checks = checksPayload
            }, ct);
        await EmitLog(emitSse, result.Succeeded ? "success" : "error", summary,
            new { level = level, instanceUrl = result.InstanceUrl, checks = result.Checks }, ct: ct);
        return stepIndex + 1;
    }

    /// <summary>Extracts the benchmark level from a "_benchmark_verify" step's change text
    /// (e.g. "benchmark 22", "benchmark_test_22", "verify level 4").</summary>
    private static int? ExtractBenchmarkLevel(string changeDesc)
    {
        if (string.IsNullOrWhiteSpace(changeDesc)) return null;
        var m = Regex.Match(changeDesc, @"\d{1,3}");
        return m.Success && int.TryParse(m.Value, out var level) ? level : null;
    }

    /// <summary>Builds the per-run progress sink that streams live browser navigation
    /// ("where is it going, what is it checking") to the UI as a "webtest" SSE event.</summary>
    private Func<BrowserTestEvent, CancellationToken, Task> MakeWebtestProgressSink(bool emitSse) =>
        async (e, ct2) =>
        {
            if (!emitSse) return;
            // Snapshot-phase events carry the rendered page — stream a compact excerpt
            // (title + a few headings + visible text) so the Test Browser panel can show
            // what actually painted, without shipping the full 30k-char body over SSE.
            object? snap = null;
            if (e.Snapshot != null)
            {
                var body = e.Snapshot.BodyText ?? "";
                if (body.Length > 400) body = body[..400] + "…";
                snap = new
                {
                    title = e.Snapshot.Title,
                    headings = e.Snapshot.Headings.Take(6).ToList(),
                    body,
                    imageDataUrl = e.Snapshot.ScreenshotDataUrl
                };
            }
            await SendSse(Response, "webtest", new { phase = e.Phase, url = e.Url, message = e.Message, snapshot = snap }, ct2);
        };

    /// <summary>True when the MOST RECENT _browser_test result failed — the deterministic
    /// gate that blocks completion: a live web test that failed (server never started /
    /// page never rendered) is ground truth the run is not done, no matter what the
    /// file-level verifier says about the source. A fresh passing re-run replaces the
    /// failure.</summary>
    private static bool MostRecentBrowserTestFailed(IEnumerable<object> allResults)
    {
        var last = allResults.OfType<Dictionary<string, object?>>()
            .LastOrDefault(r => r.GetValueOrDefault("type")?.ToString() == "browser_test");
        return last != null && last.GetValueOrDefault("status")?.ToString() == "error";
    }

    /// <summary>
    /// Re-runs the live web test when the most recent _browser_test result failed — the
    /// deterministic way the repair loop confirms a server fix actually starts the server.
    /// The file-level verifier reads FILES, not a running server: a missing module
    /// (express), a broken PORT read, or a syntax error all look fine on disk, so only the
    /// browser test itself can prove the server starts. Appends a fresh browser_test result
    /// (target preserved) so PostExecuteVerify's deterministic check sees the CURRENT state;
    /// the next repair pass / completion decision reads the fresh verdict.
    /// </summary>
    private async Task ReRunFailedBrowserTestAsync(
        string prompt, string projectRoot, bool emitSse,
        List<object> allSteps, CancellationToken ct)
    {
        var last = allSteps.OfType<Dictionary<string, object?>>()
            .LastOrDefault(r => r.GetValueOrDefault("type")?.ToString() == "browser_test");
        if (last == null || last.GetValueOrDefault("status")?.ToString() != "error") return;
        var target = last.GetValueOrDefault("target")?.ToString();
        if (string.IsNullOrWhiteSpace(target)) return;
        await EmitLog(emitSse, "info",
            $"_browser_test: re-running live web test \"{target}\" after the repair — verifying the server fix…", ct: ct);
        var testIntent = new TestClassifierTarget(target);
        BrowserTestReport report;
        _browserTestService.OnProgress = MakeWebtestProgressSink(emitSse);
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
        finally
        {
            _browserTestService.OnProgress = null;
        }

        allSteps.Add(new Dictionary<string, object?>
        {
            ["index"] = allSteps.Count,
            ["type"] = "browser_test",
            ["status"] = report.Passed ? "done" : "error",
            ["path"] = "browser_test",
            ["url"] = report.ServerUrl,
            ["mode"] = report.Mode,
            ["target"] = target,
            ["section"] = report.SectionLabel,
            ["description"] = $"Live web test re-run \"{target}\": {(report.Passed ? "PASSED" : "FAILED")}",
            ["output"] = report.ToString()
        });
        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = allSteps.Count - 1,
                type = "browser_test",
                status = report.Passed ? "done" : "error",
                path = "browser_test",
                url = report.ServerUrl,
                mode = report.Mode,
                target,
                section = report.SectionLabel,
                description = $"Live web test re-run \"{target}\": {(report.Passed ? "PASSED" : "FAILED")}",
                message = report.ToString()
            }, ct);
        await EmitLog(emitSse, report.Passed ? "success" : "error", report.ToString(), ct: ct);
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
