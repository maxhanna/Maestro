using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>The deterministic report of one live web-app test: everything a blind
/// model (or user) needs to judge the outcome — server kind, mode, what was found,
/// every observation, and a pass/fail verdict.</summary>
public sealed class BrowserTestReport
{
    public bool Passed { get; set; }
    public string ServerUrl { get; set; } = "";
    public string ServerKind { get; set; } = "";
    public string Mode { get; set; } = "";          // "browser" | "http" | "failed"
    public string Target { get; set; } = "";
    public string? SectionLabel { get; set; }
    public string? SectionUrl { get; set; }
    public int Navigations { get; set; }
    public List<TestFinding> Findings { get; set; } = new();
    public string? LaunchError { get; set; }
    public string BodyTextExcerpt { get; set; } = "";

    public bool HasFailures => Findings.Any(f => f.Kind == "fail");

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Passed
            ? $"✓ LIVE WEB TEST PASSED (target: \"{Target}\", {Mode}, server: {ServerKind} @ {ServerUrl})"
            : $"✗ LIVE WEB TEST FAILED (target: \"{Target}\", {Mode}, server: {ServerKind} @ {ServerUrl})");
        if (SectionLabel != null) sb.AppendLine($"   Section found: \"{SectionLabel}\"{("" + (SectionUrl != null ? $" → {SectionUrl}" : ""))}");
        if (Navigations > 0) sb.AppendLine($"   Navigations: {Navigations}");
        foreach (var f in Findings)
            sb.AppendLine($"   [{f.Kind}] {f.Message}");
        if (!string.IsNullOrWhiteSpace(BodyTextExcerpt))
            sb.AppendLine($"   Page excerpt: \"{BodyTextExcerpt}\"");
        return sb.ToString();
    }
}

/// <summary>One live progress event from a running web test — streamed to the UI so a
/// human can watch where the browser is navigating and what it is verifying in real time.
/// A <c>snapshot</c>-phase event carries the rendered page (title/headings/visible text)
/// so the UI can show what actually painted on screen, not just the URL.</summary>
public sealed record BrowserTestEvent(string Phase, string? Url, string Message, PageSnapshot? Snapshot = null);

/// <summary>
/// The deterministic LIVE WEB TEST runner. Given a project root and the feature the
/// prompt asked to test, it: (1) detects how the project's server starts,
/// (2) launches it and waits for HTTP, (3) opens it in a real headless browser (or the
/// HTTP/AngleSharp probe when no browser is installed), (4) finds the section the
/// prompt names via DOM discovery, (5) navigates to it, and (6) verifies what is
/// actually rendered — all programmatically, with zero LLM involvement, so a very
/// basic model gets identical, reliable results.
/// </summary>
public class BrowserAutomationService
{
    /// <summary>Injected launcher (real by default).</summary>
    public ServerLauncherService Launcher { get; set; } = new();

    /// <summary>Injected browser factory; null/returning null → HTTP probe fallback.
    /// Tests substitute a fake driver here.</summary>
    public Func<CancellationToken, Task<CdpBrowserDriver?>>? BrowserFactory { get; set; }

    /// <summary>Set false to force the HTTP fallback even when a browser is available.</summary>
    public bool AllowBrowser { get; set; } = true;

    public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public TimeSpan BrowserSettleTime { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Optional progress sink — streamed live to the UI so a human can watch
    /// where the browser is navigating and what it is verifying. Null (default) = silent.</summary>
    public Func<BrowserTestEvent, CancellationToken, Task>? OnProgress { get; set; }

    /// <summary>When true, the service reuses a running server for subsequent test calls
    /// against the SAME project root instead of launching a new one each time. This avoids
    /// re-spawning the server process for benchmarks with multiple live checks (e.g. level
    /// 22 has both a LiveUiTest and a LiveApiTest against the same directory). Call
    /// <see cref="StopSharedServer"/> to tear down the cached server when done. The caller
    /// MUST stop the shared server — the Run* methods skip their per-call Stop when this is
    /// set.</summary>
    public bool ReuseServer { get; set; }

    private RunningServer? _sharedServer;
    private string? _sharedServerProjectRoot;

    /// <summary>Stops and clears the cached shared server (when <see cref="ReuseServer"/>
    /// was used). Safe to call when no server is cached.</summary>
    public void StopSharedServer()
    {
        var server = _sharedServer;
        _sharedServer = null;
        _sharedServerProjectRoot = null;
        if (server != null && !server.IsStopped)
            server.Stop();
    }

    /// <summary>True when the cached shared server is still alive and matches the project
    /// root — i.e. it can be reused instead of launching a fresh process.</summary>
    private bool IsSharedServerUsable(string projectRoot)
    {
        return _sharedServer != null
            && string.Equals(_sharedServerProjectRoot, projectRoot, StringComparison.OrdinalIgnoreCase)
            && !_sharedServer.IsStopped
            && (_sharedServer.Process == null || !_sharedServer.Process.HasExited);
    }

    private async Task Progress(string phase, string? url, string message, CancellationToken ct)
    {
        if (OnProgress == null) return;
        try { await OnProgress(new BrowserTestEvent(phase, url, message), ct); } catch { }
    }

    /// <summary>Emits the just-captured page snapshot so the UI can show what actually
    /// rendered (title, headings, visible text) after each navigation.</summary>
    private async Task EmitSnapshot(PageSnapshot snapshot, string? url, CancellationToken ct)
    {
        if (OnProgress == null) return;
        try
        {
            await OnProgress(new BrowserTestEvent("snapshot", url,
                $"Rendered: \"{snapshot.Title}\"", snapshot), ct);
        }
        catch { }
    }

    /// <summary>
    /// Runs a LIVE JS test: launch the project's server, open the page in a REAL browser,
    /// evaluate <paramref name="expression"/> against the RENDERED page, and pass only when
    /// it returns boolean true. This is how a benchmark verifies canvas/animation state that
    /// static HTML probing cannot see (e.g. an animated spider's live leg count). Unlike the
    /// UI/API tests there is NO HTTP-probe fallback — evaluating JS requires a real browser,
    /// so a browserless host gets a clear failure rather than a false pass.
    /// </summary>
    public virtual async Task<BrowserTestReport> RunJsTestAsync(
        string projectRoot, string expression, CancellationToken ct = default)
    {
        var report = new BrowserTestReport { Target = expression };
        var server = await LaunchServerAsync(projectRoot, report, ct);
        if (server == null)
        {
            await Progress("done", null, "Live web test could not start (" + (report.LaunchError ?? "no server") + ")", ct);
            return report;
        }
        try
        {
            var driver = AllowBrowser && BrowserFactory != null ? await BrowserFactory(ct) : null;
            if (driver == null)
            {
                report.Mode = "http";
                report.Findings.Add(new TestFinding("fail",
                    "Live JS test requires a real browser — the HTTP/AngleSharp probe cannot evaluate \"" + expression + "\" on the rendered page."));
                await Progress("done", report.ServerUrl, "Live JS test FAILED (no browser available)", ct);
                return report;
            }

            report.Mode = "browser";
            await driver.NavigateAsync(server.Url, ct);
            await Progress("navigating", server.Url, $"Browser navigated to {server.Url}", ct);
            await driver.SettleAsync(BrowserSettleTime, ct);
            var raw = await driver.EvaluateAsync(expression, ct);
            var passed = raw.ValueKind == System.Text.Json.JsonValueKind.True;
            report.Findings.Add(passed
                ? new TestFinding("pass", $"JS `{expression}` evaluated true on the rendered page.")
                : new TestFinding("fail", $"JS `{expression}` evaluated {raw.GetRawText()} — expected true."));
            var snapshot = await driver.GetSnapshotAsync(ct);
            await EmitSnapshot(snapshot, server.Url, ct);
            report.BodyTextExcerpt = Excerpt(snapshot.BodyText);
        }
        catch (Exception ex)
        {
            report.Findings.Add(new TestFinding("fail", $"Browser/JS error: {ex.Message}"));
        }
        finally
        {
            if (!ReuseServer) server.Stop();
        }
        report.Passed = !report.HasFailures;
        await Progress("done", report.ServerUrl, report.Passed
            ? $"Live web test PASSED — `{expression}` is true ({report.Mode})"
            : "Live web test FAILED", ct);
        return report;
    }

    /// <summary>Runs a UI test: launch the project's server and inspect the section the
    /// prompt names in a real browser (or HTTP probe fallback).</summary>
    public virtual async Task<BrowserTestReport> RunUiTestAsync(
        string projectRoot, string target, string? prompt, CancellationToken ct = default)
    {
        var report = new BrowserTestReport { Target = target };
        var server = await LaunchServerAsync(projectRoot, report, ct);
        if (server == null)
        {
            await Progress("done", null, "Live web test could not start (" + (report.LaunchError ?? "no server") + ")", ct);
            return report;
        }
        try
        {
            await InspectUiAsync(server, target, prompt, report, ct);
        }
        catch (Exception ex)
        {
            report.Findings.Add(new TestFinding("fail", $"Browser/probe error: {ex.Message}"));
        }
        finally
        {
            if (!ReuseServer) server.Stop();
        }
        report.Passed = !report.HasFailures;
        await Progress("done", report.ServerUrl, report.Passed
            ? $"Live web test PASSED — {report.Findings.Count(f => f.Kind == "pass")} checks"
            : "Live web test FAILED", ct);
        return report;
    }

    /// <summary>Runs an API test: launch the project's server and call the named
    /// endpoint over HTTP, verifying status and (optionally) content.</summary>
    public virtual async Task<BrowserTestReport> RunApiTestAsync(
        string projectRoot, string target, CancellationToken ct = default)
    {
        var report = new BrowserTestReport { Target = target, Mode = "http" };
        var server = await LaunchServerAsync(projectRoot, report, ct);
        if (server == null)
        {
            await Progress("done", null, "Live web test could not start (" + (report.LaunchError ?? "no server") + ")", ct);
            return report;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var url = ResolveUrl(server.Url, target);
            report.SectionUrl = url;
            report.Navigations = 1;
            await Progress("navigating", url, $"GET {url}", ct);
            using var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            report.Findings.Add((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 400
                ? new TestFinding("pass", $"GET {url} → HTTP {(int)resp.StatusCode}")
                : new TestFinding("fail", $"GET {url} → HTTP {(int)resp.StatusCode} (expected 2xx)"));
            if (body.Length > 0)
                report.Findings.Add(new TestFinding("pass", $"Response body: {body.Length} chars"));
            if (body.Length > 2000) body = body[..2000];
            report.BodyTextExcerpt = Collapse(body);
        }
        catch (Exception ex)
        {
            report.Findings.Add(new TestFinding("fail", $"API call failed: {ex.Message}"));
        }
        finally
        {
            if (!ReuseServer) server.Stop();
        }
        report.Passed = !report.HasFailures;
        await Progress("done", report.ServerUrl, report.Passed ? "API test PASSED" : "API test FAILED", ct);
        return report;
    }

    // ── internals ────────────────────────────────────────────────────────────

    private async Task<RunningServer?> LaunchServerAsync(string projectRoot, BrowserTestReport report, CancellationToken ct)
    {
        // Server reuse: when ReuseServer is set and a cached server for this project root
        // is still alive, hand it back instead of spawning a new process. This is what lets
        // a benchmark with multiple live checks (level 22's LiveUiTest + LiveApiTest) share
        // one server instead of launching/tearing down a process per check.
        if (ReuseServer && IsSharedServerUsable(projectRoot))
        {
            report.ServerKind = _sharedServer!.Kind;
            report.ServerUrl = _sharedServer.Url;
            report.Findings.Add(new TestFinding("pass", $"Server reused: {_sharedServer.Kind} → {_sharedServer.Url}"));
            return _sharedServer;
        }
        // If reusing but the project root changed (different benchmark dir), stop the stale
        // cached server before launching a fresh one — otherwise it would leak.
        if (ReuseServer && _sharedServer != null && !IsSharedServerUsable(projectRoot))
            StopSharedServer();

        var plan = ServerLauncherService.DetectLaunchPlan(projectRoot);
        if (plan == null)
        {
            report.LaunchError = "No web server or index.html found in this project.";
            report.Mode = "failed";
            report.Findings.Add(new TestFinding("fail", report.LaunchError));
            return null;
        }
        report.ServerKind = plan.Kind;
        try
        {
            var server = await Launcher.LaunchAsync(plan, ServerTimeout, ct);
            report.ServerUrl = server.Url;
            report.Findings.Add(new TestFinding("pass", $"Server started: {plan.Description} → {server.Url}"));
            await Progress("server", server.Url, $"Server started: {plan.Kind} → {server.Url}", ct);
            if (ReuseServer)
            {
                _sharedServer = server;
                _sharedServerProjectRoot = projectRoot;
            }
            return server;
        }
        catch (Exception ex)
        {
            report.LaunchError = ex.Message;
            report.Mode = "failed";
            report.Findings.Add(new TestFinding("fail", $"Server failed to start: {ex.Message}"));
            return null;
        }
    }

    private async Task InspectUiAsync(RunningServer server, string target, string? prompt, BrowserTestReport report, CancellationToken ct)
    {
        PageSnapshot snapshot;
        var section = (SectionMatch?)null;
        if (AllowBrowser && BrowserFactory != null)
        {
            var driver = await BrowserFactory(ct);
            if (driver != null)
            {
                await RunBrowserInspectionAsync(driver, server.Url, target, prompt, report, ct);
                return;
            }
        }

        // HTTP fallback: fetch + AngleSharp parse.
        report.Mode = "http";
        snapshot = await WebPageProbeService.FetchSnapshotAsync(server.Url, ct);
        await Progress("navigating", server.Url, $"Opened {server.Url} (HTTP probe)", ct);
        await EmitSnapshot(snapshot, server.Url, ct);
        report.Findings.Add(new TestFinding("info", "No browser available — inspected the page over HTTP (server-rendered/static content only; JavaScript-rendered apps need a browser)."));
        section = WebPageProbeService.FindTargetSection(snapshot, target, prompt);
        if (section != null)
        {
            report.SectionLabel = section.Label;
            report.SectionUrl = section.Url;
            report.Findings.Add(new TestFinding("pass",
                $"Found section \"{section.Label}\" ({(section.Url != null ? "link → " + section.Url : section.Kind)})"));
            await Progress("section", section.Url, $"Found section \"{section.Label}\"", ct);
        }
        if (section?.Url != null && !string.IsNullOrWhiteSpace(section.Url) && IsNavigableHref(section.Url))
        {
            var sectionUrl = ResolveUrl(server.Url, section.Url);
            await Progress("navigating", sectionUrl, $"Navigating to \"{section.Label}\" → {sectionUrl}", ct);
            snapshot = await WebPageProbeService.FetchSnapshotAsync(sectionUrl, ct);
            await EmitSnapshot(snapshot, sectionUrl, ct);
            report.Navigations++;
        }
        else if (section == null && !string.IsNullOrWhiteSpace(target))
        {
            report.Findings.Add(new TestFinding("warning",
                $"No heading/link/button matching \"{target}\" was found — verifying the current page instead."));
        }
        report.BodyTextExcerpt = Excerpt(snapshot.BodyText);
        report.Findings.AddRange(WebPageProbeService.Verify(snapshot, target));
        var probes = ExtractWindowStateProbes(prompt);
        if (probes.Count > 0)
            report.Findings.Add(new TestFinding("info",
                $"No browser available — cannot evaluate the live state ({string.Join(", ", probes.Select(p => "window." + p))}) over HTTP; JavaScript-rendered canvas/animation state needs a real browser."));
    }

    private async Task RunBrowserInspectionAsync(CdpBrowserDriver driver, string baseUrl, string target, string? prompt,
        BrowserTestReport report, CancellationToken ct)
    {
        report.Mode = "browser";
        await driver.NavigateAsync(baseUrl, ct);
        await Progress("navigating", baseUrl, $"Browser navigated to {baseUrl}", ct);
        await driver.SettleAsync(BrowserSettleTime, ct);
        var snapshot = await driver.GetSnapshotAsync(ct);
        await EmitSnapshot(snapshot, baseUrl, ct);
        var section = WebPageProbeService.FindTargetSection(snapshot, target, prompt);
        if (section != null)
        {
            report.SectionLabel = section.Label;
            report.SectionUrl = section.Url;
            report.Findings.Add(new TestFinding("pass",
                $"Found section \"{section.Label}\" ({(section.Url != null ? "link → " + section.Url : section.Kind)})"));
            await Progress("section", section.Url, $"Found section \"{section.Label}\"", ct);
        }
        if (section != null && section.Url != null && IsNavigableHref(section.Url))
        {
            // Navigate to the section the prompt named.
            var sectionUrl = ResolveUrl(baseUrl, section.Url);
            await Progress("navigating", sectionUrl, $"Navigating to \"{section.Label}\" → {sectionUrl}", ct);
            await driver.NavigateAsync(sectionUrl, ct);
            await driver.SettleAsync(BrowserSettleTime, ct);
            report.Navigations++;
            snapshot = await driver.GetSnapshotAsync(ct);
            await EmitSnapshot(snapshot, sectionUrl, ct);
        }
        else if (section != null && section.Kind == "button")
        {
            // A button section: click it and re-snapshot to see what happens.
            var clicked = await driver.ClickByTextAsync(section.Label, ct);
            if (clicked != null)
            {
                await driver.SettleAsync(BrowserSettleTime, ct);
                report.Navigations++;
                snapshot = await driver.GetSnapshotAsync(ct);
                await EmitSnapshot(snapshot, baseUrl, ct);
                report.Findings.Add(new TestFinding("pass", $"Clicked button \"{section.Label}\" — re-snapshotted the page."));
            }
        }
        else if (section == null && !string.IsNullOrWhiteSpace(target))
        {
            report.Findings.Add(new TestFinding("warning",
                $"No heading/link/button matching \"{target}\" was found — verifying the current page instead."));
        }
        report.BodyTextExcerpt = Excerpt(snapshot.BodyText);
        report.Findings.AddRange(WebPageProbeService.Verify(snapshot, target));
        // Read the benchmark's live canvas/animation state (e.g. `window.legCount`) off the
        // rendered page — the actual leg count, not just a heading match. This is what makes
        // the pass/fail and the agent's feedback reflect what is really on the canvas.
        await AppendLiveStateProbesAsync(prompt, driver.EvaluateAsync, report.Findings, ct);
    }

    /// <summary>
    /// Unique `window.&lt;name&gt;` state globals the prompt names (e.g. `window.legCount`).
    /// These are the benchmark contract's readable animation/canvas state — the page exposes
    /// a global so the test can read the REAL rendered value instead of guessing from a
    /// heading. Only the plain `window.name` form is matched (never `window.name.sub`), so
    /// probes stay simple and safe to evaluate.
    /// </summary>
    private static readonly Regex WindowStateRegex = new(
        @"\bwindow\.([A-Za-z_$][\w$]*)", RegexOptions.Compiled);

    internal static IReadOnlyList<string> ExtractWindowStateProbes(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (Match m in WindowStateRegex.Matches(prompt))
        {
            var name = m.Groups[1].Value;
            if (seen.Add(name)) names.Add(name);
        }
        return names;
    }

    private static string DescribeValue(JsonElement raw) => raw.ValueKind switch
    {
        JsonValueKind.String => raw.GetString() ?? "",
        JsonValueKind.Number => raw.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "undefined",
        _ => raw.GetRawText()
    };

    /// <summary>
    /// Reads every `window.&lt;name&gt;` state global the prompt names off the RENDERED page
    /// and reports its LIVE value — the canvas/animation state a benchmark exposes
    /// (e.g. `window.legCount`), which heading/section matching alone cannot see. A
    /// readable value is reported so the agent sees the actual count (4 legs vs 6 legs);
    /// a missing global FAILS, because these benchmarks promise "the page MUST expose
    /// `window.legCount`" — an absent global is a broken contract, not a neutral detail.
    /// </summary>
    internal static async Task AppendLiveStateProbesAsync(
        string? prompt,
        Func<string, CancellationToken, Task<JsonElement>> evaluate,
        List<TestFinding> findings,
        CancellationToken ct)
    {
        foreach (var name in ExtractWindowStateProbes(prompt))
        {
            var expr = "window." + name;
            try
            {
                var raw = await evaluate(expr, ct);
                findings.Add(new TestFinding("info",
                    $"{expr} = {DescribeValue(raw)} (live canvas/animation state)"));
            }
            catch (Exception)
            {
                findings.Add(new TestFinding("fail",
                    $"{expr} is not defined on the rendered page — the required state is missing."));
            }
        }
    }

    /// <summary>Resolves a possibly-relative target/href against the server base URL.
    /// RFC 3986 semantics: an absolute-path target ("/x") roots at the host, a relative
    /// one ("sub/page.html") resolves against the base. "api/…" (no leading slash) is
    /// treated as host-rooted since it is a common shorthand for route tests.</summary>
    public static string ResolveUrl(string baseUrl, string target)
    {
        target = target.Trim();
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return target;
        if (target.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            return baseUrl.TrimEnd('/') + "/" + target;
        try { return new Uri(new Uri(baseUrl), target).ToString(); }
        catch { return baseUrl.TrimEnd('/') + "/" + target; }
    }

    private static bool IsNavigableHref(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return false;
        var lower = href.ToLowerInvariant();
        return !lower.StartsWith("javascript:") && !lower.StartsWith("#") &&
               !lower.StartsWith("mailto:") && !lower.StartsWith("tel:") && lower != "about:blank";
    }

    private static string Excerpt(string bodyText)
    {
        var collapsed = Collapse(bodyText);
        return collapsed.Length > 400 ? collapsed[..400] + "…" : collapsed;
    }

    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pending = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { pending = true; continue; }
            if (pending && sb.Length > 0) sb.Append(' ');
            sb.Append(c);
            pending = false;
        }
        return sb.ToString().Trim();
    }
}