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