using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the LIVE WEB TEST benchmark checks — the bridge between the benchmark harness and
/// the BrowserAutomationService testing suite. A benchmark can now assert that an agent's
/// output is verified by ACTUALLY running it (spin up the folder's server and check the
/// screen / hit the endpoint), not just by grepping source files. Also locks benchmark 22's
/// shape (platform-agnostic server + port fallback + live verification) and the launcher's
/// port-conflict behavior.
/// </summary>
[Collection("LiveProcessTests")]
public class BenchmarkLiveWebTestTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "weaver-liveweb-" + Guid.NewGuid().ToString("N"));

    public BenchmarkLiveWebTestTests()
    {
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static DatabaseService SubstituteDb()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "weaver_liveweb_db_" + Guid.NewGuid().ToString("N"));
        return new DatabaseService(basePath + ".db", basePath + "_data", basePath + "_cfg.json");
    }

    private static BenchmarkCheckResult RunCheck(BenchmarkService service, BenchmarkAcceptanceCheck check, string root)
    {
        var task = (Task<BenchmarkCheckResult>)typeof(BenchmarkService)
            .GetMethod("EvaluateCheckAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object[] { check, root, CancellationToken.None })!;
        return task.GetAwaiter().GetResult();
    }

    private string NewStaticSite(string targetHeading)
    {
        var dir = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"""
            <!DOCTYPE html>
            <html><head><title>Fixture</title></head>
            <body><h1>{targetHeading}</h1><p>Hello from the benchmark server.</p></body></html>
            """);
        return dir;
    }

    // A canned runner so check-level pass/fail mapping is testable without spawning anything.
    private sealed class FakeBrowserAutomationService : BrowserAutomationService
    {
        public BrowserTestReport? NextUiReport { get; set; }
        public BrowserTestReport? NextApiReport { get; set; }
        public BrowserTestReport? NextJsReport { get; set; }
        public string? LastUiTarget { get; private set; }
        public string? LastApiTarget { get; private set; }
        public string? LastJsExpression { get; private set; }

        public override Task<BrowserTestReport> RunUiTestAsync(string projectRoot, string target, string? prompt, CancellationToken ct = default)
        {
            LastUiTarget = target;
            return Task.FromResult(NextUiReport ?? new BrowserTestReport { Target = target, Mode = "http", Passed = true });
        }

        public override Task<BrowserTestReport> RunApiTestAsync(string projectRoot, string target, CancellationToken ct = default)
        {
            LastApiTarget = target;
            return Task.FromResult(NextApiReport ?? new BrowserTestReport { Target = target, Mode = "http", Passed = true });
        }

        public override Task<BrowserTestReport> RunJsTestAsync(string projectRoot, string expression, CancellationToken ct = default)
        {
            LastJsExpression = expression;
            return Task.FromResult(NextJsReport ?? new BrowserTestReport { Target = expression, Mode = "browser", Passed = true });
        }
    }

    // ── Benchmark 22 shape ─────────────────────────────────────────────────

    [Fact]
    public void Level22_Description_IsPlatformAgnosticAndPortAware()
    {
        var desc = BenchmarkService.GetBenchmarkPlans().First(p => p.Level == 22).Description;
        Assert.Contains("PORT", desc);
        Assert.Contains("8765", desc);
        Assert.Contains("/api/health", desc);
        Assert.Contains("test", desc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server.py", desc, StringComparison.OrdinalIgnoreCase);
        // Benchmark 22 must specifically demand VISUAL inspection of the rendered page
        // ("check my game for visual bugs" shape), which is what triggers the live
        // browser test and the Test Browser tab — not just a source-file grep.
        Assert.Contains("visual bugs", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOOK at the rendered page", desc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Level22_Checks_IncludeLiveVerification()
    {
        var checks = BenchmarkService.GetBenchmarkPlans().First(p => p.Level == 22).AcceptanceChecks;
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.DirectoryExists && c.Path == "benchmark_test_22");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileExists && c.Path == "benchmark_test_22/index.html");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.DirectoryContains && c.Value == "8765");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.DirectoryContains && c.Value == "/api/health");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.LiveUiTest && c.Value == "Benchmark 22");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.LiveApiTest && c.Value == "/api/health");
    }

    // ── LiveUiTest check ───────────────────────────────────────────────────

    [Fact]
    public void LiveUiTest_TargetPresent_RealHttpFallback_Passes()
    {
        var root = NewStaticSite("Benchmark 22");
        var service = new BenchmarkService(SubstituteDb())
        {
            BrowserTest = new BrowserAutomationService
            {
                Launcher = new ServerLauncherService(),
                BrowserFactory = null, // deterministic HTTP fallback — no browser in unit tests
                ServerTimeout = TimeSpan.FromSeconds(60)
            }
        };
        var check = Check.LiveUiTest("heading", ".", "Benchmark 22");
        var result = RunCheck(service, check, root);
        Assert.True(result.Passed, result.Message);
    }

    [Fact]
    public void LiveUiTest_TargetAbsent_Fails()
    {
        var root = NewStaticSite("Benchmark 22");
        var service = new BenchmarkService(SubstituteDb())
        {
            BrowserTest = new BrowserAutomationService
            {
                Launcher = new ServerLauncherService(),
                BrowserFactory = null,
                ServerTimeout = TimeSpan.FromSeconds(60)
            }
        };
        var check = Check.LiveUiTest("heading", ".", "quantum flux capacitor");
        var result = RunCheck(service, check, root);
        Assert.False(result.Passed);
    }

    [Fact]
    public void LiveUiTest_MissingDirectory_Fails()
    {
        var service = new BenchmarkService(SubstituteDb())
        {
            BrowserTest = new FakeBrowserAutomationService()
        };
        var result = RunCheck(service, Check.LiveUiTest("heading", ".", "Benchmark 22"), Path.Combine(_tmp, "nope"));
        Assert.False(result.Passed);
    }

    // ── LiveApiTest check (canned runner — mapping, not a real server) ────

    [Fact]
    public void LiveApiTest_ReportPassed_MapsToPass()
    {
        var fake = new FakeBrowserAutomationService
        {
            NextApiReport = new BrowserTestReport { Target = "/api/health", Mode = "http", Passed = true }
        };
        var root = NewStaticSite("x");
        var service = new BenchmarkService(SubstituteDb()) { BrowserTest = fake };
        var result = RunCheck(service, Check.LiveApiTest("endpoint", ".", "/api/health"), root);
        Assert.True(result.Passed, result.Message);
        Assert.Equal("/api/health", fake.LastApiTarget);
    }

    [Fact]
    public void LiveApiTest_ReportFailed_MapsToFail()
    {
        var fake = new FakeBrowserAutomationService
        {
            NextApiReport = new BrowserTestReport
            {
                Target = "/api/health", Mode = "http", Passed = false,
                Findings = { new TestFinding("fail", "HTTP 404") }
            }
        };
        var root = NewStaticSite("x");
        var service = new BenchmarkService(SubstituteDb()) { BrowserTest = fake };
        var result = RunCheck(service, Check.LiveApiTest("endpoint", ".", "/api/health"), root);
        Assert.False(result.Passed);
        Assert.Contains("HTTP 404", result.Message);
    }

    [Fact]
    public void LiveCheck_EmptyTarget_Fails()
    {
        var service = new BenchmarkService(SubstituteDb()) { BrowserTest = new FakeBrowserAutomationService() };
        var root = NewStaticSite("x");
        var result = RunCheck(service, Check.LiveUiTest("heading", ".", ""), root);
        Assert.False(result.Passed);
    }

    // ── Benchmark 23 shape (animated canvas spider, 4 → 6 legs) ───────────

    [Fact]
    public void Level23_Description_DemandsAnimatedCanvasAndLegCountProgression()
    {
        var desc = BenchmarkService.GetBenchmarkPlans().First(p => p.Level == 23).Description;
        // The canvas animation is the point: it must be ANIMATED (requestAnimationFrame),
        // not a static image — that is what proves the browser can load live canvases.
        Assert.Contains("canvas", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requestAnimationFrame", desc, StringComparison.OrdinalIgnoreCase);
        // The two-stage fix: 4 legs first, then a separate step adds 2 more (6 total).
        Assert.Contains("4 legs", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("6 legs", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add 2 more legs", desc, StringComparison.OrdinalIgnoreCase);
        // The count must be exposed to the test suite so it can be read from the LIVE page.
        Assert.Contains("window.legCount", desc);
        Assert.Contains("legs_report.txt", desc, StringComparison.OrdinalIgnoreCase);
        // Visual inspection is demanded (the LLM must LOOK at the canvas) — the trigger
        // that routes this benchmark through the live browser test.
        Assert.Contains("LOOK at the rendered page", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("visually confirm", desc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Level23_Checks_IncludeLiveJsVerification()
    {
        var checks = BenchmarkService.GetBenchmarkPlans().First(p => p.Level == 23).AcceptanceChecks;
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.DirectoryExists && c.Path == "benchmark_test_23");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileExists && c.Path == "benchmark_test_23/index.html");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.DirectoryContains && c.Value == "<canvas");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.DirectoryContains && c.Value == "requestAnimationFrame");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.DirectoryContains && c.Value == "window.legCount");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileExists && c.Path == "benchmark_test_23/legs_report.txt");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileContains && c.Value == "LEGS: 4");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileContains && c.Value == "LEGS: 6");
        // The decisive check: the live page's window.legCount must equal 6 in a real browser.
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.LiveJsTest && c.Value == "window.legCount === 6");
    }

    // ── LiveJsTest check (canned runner — mapping, not a real browser) ─────

    [Fact]
    public void LiveJsTest_ReportPassed_MapsToPass()
    {
        var fake = new FakeBrowserAutomationService
        {
            NextJsReport = new BrowserTestReport { Target = "window.legCount === 6", Mode = "browser", Passed = true }
        };
        var root = NewStaticSite("x");
        var service = new BenchmarkService(SubstituteDb()) { BrowserTest = fake };
        var result = RunCheck(service, Check.LiveJsTest("legs", ".", "window.legCount === 6"), root);
        Assert.True(result.Passed, result.Message);
        Assert.Equal("window.legCount === 6", fake.LastJsExpression);
    }

    [Fact]
    public void LiveJsTest_ReportFailed_MapsToFail()
    {
        var fake = new FakeBrowserAutomationService
        {
            NextJsReport = new BrowserTestReport
            {
                Target = "window.legCount === 6", Mode = "browser", Passed = false,
                Findings = { new TestFinding("fail", "JS `window.legCount === 6` evaluated false — expected true.") }
            }
        };
        var root = NewStaticSite("x");
        var service = new BenchmarkService(SubstituteDb()) { BrowserTest = fake };
        var result = RunCheck(service, Check.LiveJsTest("legs", ".", "window.legCount === 6"), root);
        Assert.False(result.Passed);
        Assert.Contains("evaluated false", result.Message);
    }

    // ── LiveJsTest needs a REAL browser (no silent HTTP fallback) ─────────

    [Fact]
    public void LiveJsTest_NoBrowserAvailable_FailsWithClearMessage()
    {
        var root = NewStaticSite("x");
        var service = new BenchmarkService(SubstituteDb())
        {
            BrowserTest = new BrowserAutomationService
            {
                Launcher = new ServerLauncherService(),
                BrowserFactory = null, // no Chromium → the JS check must NOT false-pass
                ServerTimeout = TimeSpan.FromSeconds(60)
            }
        };
        var result = RunCheck(service, Check.LiveJsTest("legs", ".", "window.legCount === 6"), root);
        Assert.False(result.Passed);
        Assert.Contains("requires a real browser", result.Message);
    }

    // ── End-to-end: benchmark 22's full check list (the "testing suite" bridge) ──

    [Fact]
    public async Task EvaluateChecksAsync_Level22_StaticFixture_AllChecksPass()
    {
        var root = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        var bench = Path.Combine(root, "benchmark_test_22");
        Directory.CreateDirectory(bench);
        File.WriteAllText(Path.Combine(bench, "index.html"),
            "<!-- server: PORT=8765 /api/health -->\n<!DOCTYPE html><html><body><h1>Benchmark 22</h1><p>Hello.</p></body></html>");

        var service = new BenchmarkService(SubstituteDb())
        {
            BrowserTest = new BrowserAutomationService
            {
                Launcher = new ServerLauncherService(),
                BrowserFactory = null, // deterministic HTTP fallback
                ServerTimeout = TimeSpan.FromSeconds(60)
            }
        };

        var results = await service.EvaluateChecksAsync(22, root);

        Assert.Equal(7, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, r.Name + ": " + r.Message));
        // The live checks must actually have run the testing suite (server + screen probe).
        Assert.Contains(results, r => r.Type == BenchmarkCheckType.LiveUiTest && r.Passed);
        Assert.Contains(results, r => r.Type == BenchmarkCheckType.LiveApiTest && r.Passed);
    }

    // ── Port conflict fallback ─────────────────────────────────────────────

    [Fact]
    public void FindFreePort_PreferredPortBusy_FallsBackToDifferentPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var busyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var chosen = ServerLauncherService.FindFreePort(busyPort);
            Assert.NotEqual(busyPort, chosen);

            // The fallback port must itself be free.
            var probe = new TcpListener(IPAddress.Loopback, chosen);
            probe.Start();
            probe.Stop();
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void FindFreePort_PreferredPortFree_ReturnsPreferredPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var freePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Equal(freePort, ServerLauncherService.FindFreePort(freePort));
    }
}
