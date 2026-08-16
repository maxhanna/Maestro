using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks BrowserAutomationService — the deterministic live web-test runner. These tests
/// exercise the full service end-to-end against the in-process static file server with
/// the HTTP/AngleSharp fallback (no browser, no network beyond loopback), plus every
/// failure path: no-server project, launch failure, absent target. The browser-mode path
/// is covered live in ServerLaunchE2ETests (real headless Edge/Chrome when installed).
/// </summary>
[Collection("LiveProcessTests")]
public class BrowserAutomationServiceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "weaver-batests-" + Guid.NewGuid().ToString("N"));

    public BrowserAutomationServiceTests()
    {
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private string NewProject(string indexHtml)
    {
        var dir = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), indexHtml);
        return dir;
    }

    private static BrowserAutomationService ServiceFor(string projectRoot, bool allowBrowser = true) => new()
    {
        Launcher = new ServerLauncherService(),
        BrowserFactory = null, // force the HTTP fallback: deterministic in tests
        AllowBrowser = allowBrowser,
        ServerTimeout = TimeSpan.FromSeconds(60)
    };

    // ── Benchmark 22 end-to-end: the agent's subfolder server must run and its
    //    rendered page must stream back as webtest progress (the payload the agent
    //    section's "Test Browser" tab renders when it auto-opens). ───────────────────

    private const string Benchmark22Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="UTF-8"><title>Benchmark 22</title></head>
        <body>
          <h1>Benchmark22</h1>
          <p>Click the button to raise the score.</p>
          <div id="score">Score: 0</div>
          <button onclick="document.getElementById('score').textContent='Score: 1'">Click Me!</button>
        </body>
        </html>
        """;

    // The benchmark-22 server contract: read PORT from the environment (the launcher's
    // free port — never the 8765 default), serve index.html at /, /api/health → 200 JSON.
    private const string Benchmark22ServerJs = """
        const http = require('http');
        const fs = require('fs');
        const PORT = parseInt(process.env.PORT) || 8765;
        const server = http.createServer((req, res) => {
          if (req.url === '/api/health') {
            res.writeHead(200, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ status: 'ok' }));
          } else {
            res.writeHead(200, { 'Content-Type': 'text/html' });
            res.end(fs.readFileSync('./index.html'));
          }
        });
        server.listen(PORT, () => console.log('Server running at http://localhost:' + PORT + '/'));
        """;

    /// <summary>Builds the exact benchmark-22 sandbox layout: index.html + server.js in a
    /// benchmark_test_22 subfolder, with NO package.json at the root.</summary>
    private string NewBenchmark22Project()
    {
        var root = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        var bench = Path.Combine(root, "benchmark_test_22");
        Directory.CreateDirectory(bench);
        File.WriteAllText(Path.Combine(bench, "index.html"), Benchmark22Html);
        File.WriteAllText(Path.Combine(bench, "server.js"), Benchmark22ServerJs);
        return root;
    }

    [Fact]
    public async Task Benchmark22_SubfolderServer_LaunchesOnFreePort_AndStreamsRenderedPage()
    {
        // The end-to-end contract behind the "Test Browser" tab: the live web test must
        // (a) RUN the agent's own server.js (not fall back to the in-process static file
        //     server, which would 404 /api/health), (b) start it on a launcher-chosen
        //     free port — DIFFERENT from the 8765 default the server.js hardcodes — with
        //     PORT injected via env, and (c) stream a snapshot of the rendered page as
        //     webtest progress so the UI can open a tab showing what the browser saw.
        var root = NewBenchmark22Project();
        var events = new List<BrowserTestEvent>();
        var service = ServiceFor(root); // HTTP fallback — deterministic, no browser needed
        service.OnProgress = (e, _) => { lock (events) events.Add(e); return Task.CompletedTask; };

        var report = await service.RunUiTestAsync(root, "benchmark 22", "test the game page");

        Assert.True(report.Passed, report.ToString());
        // (a) The agent's server RAN — mode "http" here only because the test forces the
        //     deterministic probe; ServerKind is what proves which process served the page.
        Assert.Equal("node", report.ServerKind);
        // (b) A different port: the server.js hardcodes 8765, the launcher must override it.
        Assert.NotNull(report.ServerUrl);
        var port = new Uri(report.ServerUrl!).Port;
        Assert.NotEqual(8765, port);
        // (c) The rendered page streamed back as a snapshot progress event — exactly what
        //     the agent section's "Test Browser" tab displays (title/headings/body).
        var snapEvent = events.FirstOrDefault(e => e.Snapshot != null);
        Assert.NotNull(snapEvent);
        Assert.Equal("Benchmark 22", snapEvent!.Snapshot!.Title);
        Assert.Contains("Benchmark22", snapEvent.Snapshot.Headings);
        Assert.Contains("Score: 0", snapEvent.Snapshot.BodyText);
    }

    [Fact]
    public async Task Benchmark22_SubfolderServer_ApiHealth_ReturnsRealJson()
    {
        // The /api/health endpoint only exists on the agent's REAL server — the static
        // fallback would serve index.html for it and this would fail. Proves the launched
        // process is the server.js, not a file server.
        var root = NewBenchmark22Project();

        var report = await ServiceFor(root).RunApiTestAsync(root, "/api/health");

        Assert.True(report.Passed, report.ToString());
        Assert.Equal("node", report.ServerKind);
        Assert.Contains("ok", report.BodyTextExcerpt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Benchmark22_SubfolderServer_LiveBrowser_StreamsScreenshotOfTheSite()
    {
        // The "send a visual of the site to the user" half: with a real Chromium installed
        // the inspection runs headless and the snapshot streams a JPEG screenshot data URL
        // — the exact payload the Test Browser tab's <img> renders. Skipped on hosts with
        // no browser (the HTTP fallback still streams title/headings/body).
        var root = NewBenchmark22Project();
        var events = new List<BrowserTestEvent>();
        var service = new BrowserAutomationService
        {
            Launcher = new ServerLauncherService(),
            BrowserFactory = CdpBrowserDriver.TryCreateAsync,
            AllowBrowser = true,
            ServerTimeout = TimeSpan.FromSeconds(60)
        };
        service.OnProgress = (e, _) => { lock (events) events.Add(e); return Task.CompletedTask; };

        var report = await service.RunUiTestAsync(root, "benchmark 22", "test the game page");
        if (report.Mode != "browser") return; // no Chromium on this host — HTTP fallback covered above

        Assert.True(report.Passed, report.ToString());
        var snapEvent = events.FirstOrDefault(e => e.Snapshot?.ScreenshotDataUrl != null);
        Assert.NotNull(snapEvent);
        Assert.StartsWith("data:image/jpeg;base64,", snapEvent!.Snapshot!.ScreenshotDataUrl);
        Assert.Contains("Benchmark22", snapEvent.Snapshot.Headings);
    }

    // ── Benchmark 23: LIVE JS tests on an animated canvas ──────────────────

    // The benchmark-23 game contract: an ANIMATED canvas spider exposing its live leg
    // count as window.legCount (the same value the draw loop uses) so the test suite can
    // read the REAL rendered state — the "count the spider's legs in the browser" check.
    private const string Benchmark23Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="UTF-8"><title>Benchmark 23</title></head>
        <body>
          <h1>Benchmark 23</h1>
          <p>An animated spider on a canvas.</p>
          <canvas id="spider" width="300" height="200"></canvas>
          <script>
            window.legCount = 6;
            var canvas = document.getElementById('spider');
            var ctx = canvas.getContext('2d');
            function draw() {
              ctx.clearRect(0, 0, 300, 200);
              // draw the spider body
              ctx.beginPath(); ctx.arc(150, 100, 30, 0, Math.PI * 2); ctx.stroke();
              // draw window.legCount legs (this is what the test reads)
              for (var i = 0; i < window.legCount; i++) {
                ctx.beginPath();
                ctx.moveTo(150, 100);
                ctx.lineTo(120 + i * 6, 160);
                ctx.stroke();
              }
              requestAnimationFrame(draw);
            }
            draw();
          </script>
        </body>
        </html>
        """;

    /// <summary>Builds the benchmark-23 sandbox layout: animated-canvas index.html + the
    /// same node server contract as benchmark 22 (PORT env, serves index.html).</summary>
    private string NewBenchmark23Project()
    {
        var root = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        var bench = Path.Combine(root, "benchmark_test_23");
        Directory.CreateDirectory(bench);
        File.WriteAllText(Path.Combine(bench, "index.html"), Benchmark23Html);
        File.WriteAllText(Path.Combine(bench, "server.js"), Benchmark22ServerJs);
        return root;
    }

    [Fact]
    public async Task RunJsTestAsync_NoBrowser_FailsWithClearMessage()
    {
        // The HTTP/AngleSharp probe CANNOT evaluate JS — a JS check must fail loudly,
        // never silently false-pass on the static source.
        var root = NewBenchmark23Project();
        var report = await ServiceFor(root).RunJsTestAsync(root, "window.legCount === 6");

        Assert.False(report.Passed);
        Assert.Contains("requires a real browser", report.ToString());
    }

    [Fact]
    public async Task Benchmark23_AnimatedCanvas_LiveBrowser_ReadsLegCountFromTheRenderedPage()
    {
        // The benchmark-23 core: the browser must LOAD the animated canvas page and the JS
        // check must read the REAL rendered state (window.legCount = 6), not the source.
        // Skipped on hosts with no Chromium — the no-browser failure above covers those.
        var root = NewBenchmark23Project();
        var events = new List<BrowserTestEvent>();
        var service = new BrowserAutomationService
        {
            Launcher = new ServerLauncherService(),
            BrowserFactory = CdpBrowserDriver.TryCreateAsync,
            AllowBrowser = true,
            ServerTimeout = TimeSpan.FromSeconds(60)
        };
        service.OnProgress = (e, _) => { lock (events) events.Add(e); return Task.CompletedTask; };

        var report = await service.RunJsTestAsync(root, "window.legCount === 6");
        if (report.Mode != "browser") return; // no Chromium on this host — covered above

        Assert.True(report.Passed, report.ToString());
        Assert.Equal("node", report.ServerKind);
        // The live canvas page streamed back so the Test Browser tab can show the spider.
        var snapEvent = events.FirstOrDefault(e => e.Snapshot != null);
        Assert.NotNull(snapEvent);
        Assert.Contains("Benchmark 23", snapEvent!.Snapshot!.Headings);
    }

    [Fact]
    public async Task Benchmark23_AnimatedCanvas_LiveBrowser_WrongLegCount_Fails()
    {
        // The leg-count check must be exact: a 6-leg spider must NOT satisfy "=== 4".
        // Proves the JS evaluation reads the live value, not a source grep.
        var root = NewBenchmark23Project();
        var service = new BrowserAutomationService
        {
            Launcher = new ServerLauncherService(),
            BrowserFactory = CdpBrowserDriver.TryCreateAsync,
            AllowBrowser = true,
            ServerTimeout = TimeSpan.FromSeconds(60)
        };

        var report = await service.RunJsTestAsync(root, "window.legCount === 4");
        if (report.Mode != "browser") return; // no Chromium on this host

        Assert.False(report.Passed);
        Assert.Contains("evaluated false", report.ToString());
    }

    private const string SiteHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>Fixture</title></head>
        <body>
          <nav><a href="/kanban.html">Kanban Board</a></nav>
          <h1>Fixture Home</h1>
          <p>Welcome to the fixture site.</p>
        </body>
        </html>
        """;

    private const string KanbanHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>Kanban Board</title></head>
        <body><h1>Kanban Board</h1><p>Cards live here.</p></body>
        </html>
        """;

    // ── UI tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunUiTestAsync_FindsSectionAndNavigates_Passes()
    {
        var root = NewProject(SiteHtml);
        File.WriteAllText(Path.Combine(root, "kanban.html"), KanbanHtml);

        var report = await ServiceFor(root).RunUiTestAsync(root, "kanban board", "test the kanban board");

        Assert.True(report.Passed, report.ToString());
        Assert.Equal("http", report.Mode);
        Assert.Equal("static", report.ServerKind);
        Assert.False(string.IsNullOrEmpty(report.ServerUrl));
        Assert.Equal("Kanban Board", report.SectionLabel);
        Assert.Equal(1, report.Navigations); // navigated from / to /kanban.html
        Assert.Contains(report.Findings, f => f.Kind == "pass" && f.Message.Contains("Found section"));
        Assert.Contains(report.Findings, f => f.Kind == "pass" && f.Message.Contains("Page title"));
        Assert.Contains("Cards live here", report.BodyTextExcerpt);
    }

    [Fact]
    public async Task RunUiTestAsync_AbsentTarget_FailsWithWarningAndVerdict()
    {
        var root = NewProject(SiteHtml);

        var report = await ServiceFor(root).RunUiTestAsync(root, "quantum flux capacitor", null);

        // Section discovery warns; the deterministic verification then fails the test —
        // the page genuinely has nothing matching the target.
        Assert.False(report.Passed);
        Assert.Contains(report.Findings, f => f.Kind == "warning" && f.Message.Contains("quantum flux capacitor"));
        Assert.Contains(report.Findings, f => f.Kind == "fail" && f.Message.Contains("quantum flux capacitor"));
    }

    [Fact]
    public async Task RunUiTestAsync_ApiTarget_ViaUiIsValidatedOnPage()
    {
        // A "/api/..." target through the UI path is just a target string — verify the
        // pipeline still launches the server and inspects *something* deterministically.
        var root = NewProject(SiteHtml);
        var report = await ServiceFor(root).RunUiTestAsync(root, "/api/agent", null);
        Assert.False(string.IsNullOrEmpty(report.ServerUrl));
        Assert.Contains(report.Findings, f => f.Kind == "pass");
    }

    // ── API tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunApiTestAsync_ExistingPage_Passes()
    {
        var root = NewProject(SiteHtml);
        File.WriteAllText(Path.Combine(root, "kanban.html"), KanbanHtml);

        var report = await ServiceFor(root).RunApiTestAsync(root, "/kanban.html");

        Assert.True(report.Passed, report.ToString());
        Assert.Equal("http", report.Mode);
        Assert.Contains(report.Findings, f => f.Kind == "pass" && f.Message.Contains("HTTP 200"));
        Assert.Contains(report.Findings, f => f.Kind == "pass" && f.Message.Contains("Response body"));
    }

    [Fact]
    public async Task RunApiTestAsync_RelativeTarget_ResolvesAgainstServer()
    {
        var root = NewProject(SiteHtml);
        File.WriteAllText(Path.Combine(root, "kanban.html"), KanbanHtml);
        var report = await ServiceFor(root).RunApiTestAsync(root, "kanban.html");
        Assert.True(report.Passed, report.ToString());
        Assert.EndsWith("/kanban.html", report.SectionUrl);
    }

    // ── failure paths ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunUiTestAsync_NoServerProject_FailsWithLaunchError()
    {
        var empty = Path.Combine(_tmp, "empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);

        var report = await ServiceFor(empty).RunUiTestAsync(empty, "anything", null);

        Assert.False(report.Passed);
        Assert.Equal("failed", report.Mode);
        Assert.Contains("No web server", report.LaunchError);
        Assert.Contains(report.Findings, f => f.Kind == "fail");
    }

    [Fact]
    public async Task RunApiTestAsync_LaunchCrash_ReportsFailureNotException()
    {
        // A node project (process-based plan) whose process factory throws → the launch
        // must fail cleanly into the report, never throw out of the service.
        var root = Path.Combine(_tmp, "node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), "{\"scripts\":{\"start\":\"node server.js\"}}");
        File.WriteAllText(Path.Combine(root, "server.js"), "require('http').createServer((req,res)=>{res.end('ok')}).listen(3000);");
        var service = ServiceFor(root);
        service.Launcher = new ServerLauncherService { ProcessFactory = _ => throw new InvalidOperationException("boom") };

        var report = await service.RunApiTestAsync(root, "/api/x");

        Assert.False(report.Passed);
        Assert.Equal("failed", report.Mode);
        Assert.Contains("boom", report.LaunchError);
    }

    [Fact]
    public async Task RunUiTestAsync_ServerStoppedAfterRun_NoLeak()
    {
        var root = NewProject(SiteHtml);
        var report = await ServiceFor(root).RunUiTestAsync(root, "fixture home", null);
        Assert.True(report.Passed, report.ToString());
        // The static host must be gone after the run: the port answers nothing.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await Assert.ThrowsAnyAsync<Exception>(async () => await http.GetAsync(report.ServerUrl + "/"));
    }

    [Fact]
    public async Task RunUiTestAsync_StreamsSnapshotEvents_WithRenderedPage()
    {
        // The OnProgress stream must surface WHAT RENDERED (title + headings), not just
        // the URL — this is what the Test Browser panel reads to show the live page.
        var root = NewProject(SiteHtml);
        var service = ServiceFor(root);
        var events = new List<BrowserTestEvent>();
        service.OnProgress = (e, ct) => { events.Add(e); return Task.CompletedTask; };

        var report = await service.RunUiTestAsync(root, "fixture home", null);

        Assert.True(report.Passed, report.ToString());
        var snap = events.FirstOrDefault(e => e.Phase == "snapshot");
        Assert.NotNull(snap);
        Assert.NotNull(snap!.Snapshot);
        Assert.Equal("Fixture", snap.Snapshot!.Title);
        Assert.Contains("Fixture Home", snap.Snapshot.Headings);
        // The stream ends with a done verdict.
        Assert.Contains(events, e => e.Phase == "done");
    }

    // ── ResolveUrl / helpers ─────────────────────────────────────────────────

    [Theory]
    [InlineData("http://h:1/", "/api/x", "http://h:1/api/x")]
    [InlineData("http://h:1/", "api/x", "http://h:1/api/x")]
    [InlineData("http://h:1/", "sub/page.html", "http://h:1/sub/page.html")]
    [InlineData("http://h:1/base", "/x", "http://h:1/x")]
    [InlineData("http://h:1/", "http://other/x", "http://other/x")]
    public void ResolveUrl_Variants(string baseUrl, string target, string expected)
    {
        Assert.Equal(expected, BrowserAutomationService.ResolveUrl(baseUrl, target));
    }
}