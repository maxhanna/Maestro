using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// A real-browser driver that speaks the Chrome DevTools Protocol directly over
/// WebSocket — zero external packages, zero browser downloads. Any installed Chromium
/// browser (Edge/Chrome/Chromium) is launched headless with a remote-debugging port and
/// driven programmatically: navigate, wait for load, extract the page snapshot, click
/// the element whose text matches a keyword. This is how the live-test pipeline
/// "visually inspects" a running web app for a blind model — the page's real rendered
/// DOM (title, headings, links, buttons, inputs, visible text) is pulled out of the
/// browser and verified deterministically.
///
/// If no Chromium browser is installed, <see cref="CdpBrowserDriver.TryCreate"/> returns
/// null and the pipeline falls back to the HTTP/AngleSharp probe.
/// </summary>
public sealed class CdpBrowserDriver : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ClientWebSocket _ws;
    private readonly string _userDataDir;
    private string _sessionId;
    private string _url = "";
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _nextId;
    private bool _closed;

    private CdpBrowserDriver(Process process, ClientWebSocket ws, string userDataDir, string sessionId)
    {
        _process = process;
        _ws = ws;
        _userDataDir = userDataDir;
        _sessionId = sessionId;
    }

    /// <summary>The current page's rendered snapshot (the "visual inspection" for a
    /// blind model).</summary>
    public async Task<PageSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var raw = await EvaluateAsync(SnapshotJs, ct);
        var text = raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? "" : raw.ToString();
        var snapshot = ParseSnapshotJson(text, _url);
        snapshot.ScreenshotDataUrl = await TryCaptureScreenshotDataUrlAsync(ct);
        return snapshot;
    }

    /// <summary>Navigates to a URL and waits for the page's readyState to reach
    /// "complete" (or the timeout).</summary>
    public async Task NavigateAsync(string url, CancellationToken ct = default)
    {
        _url = url;
        await SendAsync("Page.navigate", new JsonObject { ["url"] = url }, ct, _sessionId);
        await WaitReadyStateAsync(ct);
    }

    /// <summary>Waits briefly for client-side rendering to settle (SPA apps paint after
    /// load).</summary>
    public async Task SettleAsync(TimeSpan? duration = null, CancellationToken ct = default)
    {
        await Task.Delay(duration ?? TimeSpan.FromSeconds(1.5), ct);
    }

    /// <summary>
    /// Clicks the first anchor/button whose visible text contains <paramref name="keyword"/>
    /// (case-insensitive). Returns the element kind clicked ("anchor"/"button"), or null when
    /// no element matched. Anchor clicks navigate the page; the caller then re-snapshots.
    /// </summary>
    public async Task<string?> ClickByTextAsync(string keyword, CancellationToken ct = default)
    {
        var js = ClickJs.Replace("__KW__", JsonSerializer.Serialize(keyword.ToLowerInvariant()));
        var raw = await EvaluateAsync(js, ct);
        var result = raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
        return result is "CLICKED" or "NAVIGATING" ? result : null;
    }

    /// <summary>Evaluates arbitrary JS in the page and returns the JSON value.</summary>
    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken ct = default)
    {
        var resp = await SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true
        }, ct, _sessionId);
        if (!resp.TryGetProperty("result", out var result) ||
            result.TryGetProperty("exceptionDetails", out _))
        {
            throw new InvalidOperationException("Page script failed to evaluate.");
        }
        return result.GetProperty("value");
    }

    private async Task<string?> TryCaptureScreenshotDataUrlAsync(CancellationToken ct)
    {
        try
        {
            var resp = await SendAsync("Page.captureScreenshot", new JsonObject
            {
                ["format"] = "jpeg",
                ["quality"] = 60,
                ["fromSurface"] = true
            }, ct, _sessionId);
            return resp.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String
                ? "data:image/jpeg;base64," + dataEl.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Launches a headless Chromium (Edge/Chrome/Chromium) and returns a driver bound to
    /// a fresh page target, or null when no browser binary can be found.
    /// </summary>
    public static async Task<CdpBrowserDriver?> TryCreateAsync(CancellationToken ct = default)
    {
        var exe = FindExecutable();
        if (exe == null) return null;

        var userDataDir = Path.Combine(Path.GetTempPath(), "weaver-cdp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDir);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments =
                "--headless=new --disable-gpu --no-sandbox --no-first-run --no-default-browser-check " +
                "--disable-extensions --disable-background-networking --disable-dev-shm-usage " +
                "--remote-debugging-port=0 --user-data-dir=\"" + userDataDir + "\" about:blank",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi);
        if (process == null)
        {
            try { Directory.Delete(userDataDir, true); } catch { }
            return null;
        }

        // The DevTools websocket URL is printed to stderr: "DevTools listening on ws://…".
        // The browser keeps running (and its stderr pipe stays open), so the URL must be
        // discovered line-by-line — a blocking ReadToEndAsync would never complete.
        var wsTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                {
                    var match = Regex.Match(line, @"DevTools listening on (ws://\S+)");
                    if (match.Success) { wsTcs.TrySetResult(match.Groups[1].Value); return; }
                }
                wsTcs.TrySetResult(null);
            }
            catch { wsTcs.TrySetResult(null); }
        }, CancellationToken.None);
        // Drain stdout so the browser's output pipe never fills and blocks it.
        _ = process.StandardOutput.ReadToEndAsync();
        var wsUrl = await wsTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (wsUrl == null)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(userDataDir, true); } catch { }
            return null;
        }

        ClientWebSocket ws;
        try
        {
            ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await ws.ConnectAsync(new Uri(wsUrl), ct);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(userDataDir, true); } catch { }
            return null;
        }

        var driver = new CdpBrowserDriver(process, ws, userDataDir, "");
        try
        {
            driver.StartReceiveLoop();
            var targetId = await driver.CreateTargetAsync(ct);
            var sessionId = await driver.AttachTargetAsync(targetId, ct);
            driver._sessionId = sessionId;
            await driver.SendAsync("Page.enable", new JsonObject(), ct, sessionId);
            await driver.SendAsync("Runtime.enable", new JsonObject(), ct, sessionId);
            return driver;
        }
        catch
        {
            await driver.DisposeAsync();
            return null;
        }
    }

    /// <summary>Finds an installed Chromium-family browser binary, or null.</summary>
    public static string? FindExecutable()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(pfX86, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(pf, "Chromium", "Application", "chrome.exe"));
        }
        else if (OperatingSystem.IsLinux())
        {
            candidates.AddRange(new[]
            {
                "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome",
                "/usr/bin/google-chrome-stable", "/snap/bin/chromium"
            });
        }
        else
        {
            candidates.AddRange(new[]
            {
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Chromium.app/Contents/MacOS/Chromium"
            });
        }
        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        // PATH lookup: msedge / chrome / chromium / google-chrome.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            foreach (var name in new[] { "msedge.exe", "chrome.exe", "chromium.exe", "google-chrome", "chromium", "chromium-browser" })
            {
                var full = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        _ws.Dispose();
        if (_process != null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        try { Directory.Delete(_userDataDir, true); } catch { }
    }

    // ── CDP plumbing ─────────────────────────────────────────────────────────

    private async Task<string> CreateTargetAsync(CancellationToken ct)
    {
        var resp = await SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, ct);
        return resp.GetProperty("targetId").GetString()!;
    }

    private async Task<string> AttachTargetAsync(string targetId, CancellationToken ct)
    {
        var resp = await SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, ct);
        return resp.GetProperty("sessionId").GetString()!;
    }

    private Task<JsonElement> SendAsync(string method, JsonObject? parameters, CancellationToken ct) =>
        SendAsync(method, parameters, ct, null);

    private async Task<JsonElement> SendAsync(string method, JsonObject? parameters, CancellationToken ct, string? sessionId)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject()
        };
        if (sessionId != null) msg["sessionId"] = sessionId;
        await SendFrameAsync(JsonSerializer.Serialize(msg), ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        var resp = await tcs.Task.WaitAsync(timeoutCts.Token);
        if (resp.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"CDP {method} failed: {err.GetProperty("message").GetString()}");
        return resp.GetProperty("result");
    }

    private async Task SendFrameAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private void StartReceiveLoop()
    {
        _ = Task.Run(async () =>
        {
            var buffer = new byte[1024 * 1024];
            try
            {
                while (!_closed && _ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                    {
                        if (_pending.TryRemove(idEl.GetInt32(), out var tcs))
                            tcs.TrySetResult(doc.RootElement.Clone());
                    }
                    // Events are intentionally ignored — the driver polls readyState.
                }
            }
            catch { }
        }, CancellationToken.None);
    }

    private async Task WaitReadyStateAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var raw = await EvaluateAsync("document.readyState", ct);
                if (raw.ValueKind == JsonValueKind.String && raw.GetString() == "complete")
                    return;
            }
            catch { }
            await Task.Delay(200, ct);
        }
    }

    private static PageSnapshot ParseSnapshotJson(string json, string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Str(string prop) =>
                root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString() : "";
            var headings = new List<string>();
            var links = new List<PageLink>();
            var buttons = new List<string>();
            var inputs = new List<string>();
            if (root.TryGetProperty("headings", out var hEl))
                foreach (var h in hEl.EnumerateArray())
                    if (h.ValueKind == JsonValueKind.String) headings.Add(h.GetString() ?? "");
            if (root.TryGetProperty("links", out var lEl))
                foreach (var l in lEl.EnumerateArray())
                    if (l.ValueKind == JsonValueKind.Object)
                    {
                        var text = l.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                        var href = l.TryGetProperty("href", out var hr) ? hr.GetString() : null;
                        links.Add(new PageLink(text, href));
                    }
            if (root.TryGetProperty("buttons", out var bEl))
                foreach (var b in bEl.EnumerateArray())
                    if (b.ValueKind == JsonValueKind.String) buttons.Add(b.GetString() ?? "");
            if (root.TryGetProperty("inputs", out var iEl))
                foreach (var i in iEl.EnumerateArray())
                    if (i.ValueKind == JsonValueKind.String) inputs.Add(i.GetString() ?? "");
            return new PageSnapshot
            {
                Url = url,
                Title = Str("title") ?? "",
                Headings = headings,
                Links = links,
                Buttons = buttons,
                Inputs = inputs,
                BodyText = Str("body") ?? ""
            };
        }
        catch
        {
            return new PageSnapshot { Url = url, BodyText = "" };
        }
    }

    private const string SnapshotJs = """
        (() => {
          const norm = t => (t || '').replace(/\s+/g, ' ').trim();
          const headings = [...document.querySelectorAll('h1,h2,h3,h4,h5,h6')].map(h => norm(h.innerText)).filter(Boolean);
          const links = [...document.querySelectorAll('a[href]')].map(a => ({
            text: norm(a.innerText) || norm(a.getAttribute('aria-label')) || (a.getAttribute('href') || '').slice(0, 60),
            href: a.getAttribute('href')
          })).filter(l => l.text || l.href);
          const buttons = [...document.querySelectorAll('button, input[type=submit], input[type=button], [role=button]')]
            .map(b => norm(b.innerText) || norm(b.getAttribute('aria-label')) || norm(b.getAttribute('value')) || '')
            .filter(Boolean);
          const inputs = [...document.querySelectorAll('input, textarea, select')]
            .filter(i => !['hidden', 'password'].includes((i.getAttribute('type') || 'text').toLowerCase()))
            .map(i => {
              const n = i.getAttribute('name') || i.getAttribute('id') || '';
              const type = (i.getAttribute('type') || 'text').toLowerCase();
              return type + (n ? ' "' + n + '"' : '');
            });
          const body = norm(document.body ? document.body.innerText : '');
          return JSON.stringify({
            title: document.title,
            headings, links, buttons, inputs,
            body: body.slice(0, 30000)
          });
        })()
        """;

    private const string ClickJs = """
        (() => {
          const kw = __KW__;
          const els = [...document.querySelectorAll('a[href], button, [role=button], input[type=submit], input[type=button]')];
          const el = els.find(e => (e.innerText || e.getAttribute('aria-label') || e.getAttribute('value') || '').toLowerCase().includes(kw));
          if (!el) return 'NOT_FOUND';
          if (el.tagName === 'A') { location.href = el.getAttribute('href'); return 'NAVIGATING'; }
          el.click();
          return 'CLICKED';
        })()
        """;
}
