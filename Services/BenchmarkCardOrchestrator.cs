using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Weaver.Services;

/// <summary>
/// What a self-improving card asks the orchestrator to do. <see cref="CardText"/> is the
/// benchmark card's prompt (what the fresh instance's agent runs); <see cref="Level"/> is the
/// benchmark whose acceptance checks prove the change worked.
/// </summary>
public sealed record BenchmarkOrchestrationRequest(
    string CardText,
    int Level,
    string Column = "selfImproving",
    string? WorkspaceRoot = null,
    string? EndpointId = null);

/// <summary>A progress event emitted by the orchestrator, streamed to the UI as a log/webtest event.</summary>
public sealed record BenchmarkOrchestrationEvent(string Stage, string Message, string? Url = null);

/// <summary>The outcome of running a card on a fresh instance.</summary>
public sealed record BenchmarkRunOutcome(bool Complete, string? Summary, int StepCount, string? Error);

/// <summary>The full orchestration result: launch, injection, run, and verification outcomes.</summary>
public sealed class BenchmarkOrchestrationResult
{
    public bool Launched { get; set; }
    public string? InstanceUrl { get; set; }
    public string WorkspaceRoot { get; set; } = "";
    public string? CardId { get; set; }
    public bool CardInjected { get; set; }
    public bool RunStarted { get; set; }
    public bool RunComplete { get; set; }
    public string? RunSummary { get; set; }
    public int StepCount { get; set; }
    public List<BenchmarkCheckResult> Checks { get; set; } = new();
    public bool Verified => Checks.Count > 0 && Checks.All(c => c.Passed);
    public string? Error { get; set; }
    public bool Succeeded => Launched && RunStarted && RunComplete && Verified;
}

/// <summary>
/// A fresh, isolated Weaver instance the orchestrator drives over HTTP. The real implementation
/// spawns the running Weaver executable on a free port; tests substitute a fake so the full
/// stage → launch → inject → run → verify flow is provable without a real process or LLM.
/// </summary>
public interface IBenchmarkInstanceHost : IAsyncDisposable
{
    /// <summary>The instance's HTTP base URL (e.g. http://127.0.0.1:51123).</summary>
    string BaseUrl { get; }
    /// <summary>The isolated workspace the instance's agent edits.</summary>
    string WorkspaceRoot { get; }
    /// <summary>Injects the benchmark card into the instance's board (the named column), returning the new card id.</summary>
    Task<string> InjectCardAsync(string cardText, string column, CancellationToken ct);
    /// <summary>Runs the card's prompt on the instance against <paramref name="workspaceRoot"/>, blocking until done.</summary>
    Task<BenchmarkRunOutcome> RunCardAsync(string cardText, string workspaceRoot, string? cardId, string? endpointId, CancellationToken ct);
}

/// <summary>
/// The benchmark-card orchestration flow: a self-improving card can spin up a FRESH Weaver
/// instance, inject a benchmark card, run it, and verify the result end-to-end (filesystem
/// checks + the live web-test suite). Every mechanical step is a seam (host factory, verifier)
/// so the sequence is unit-testable hermetically, while the real defaults spawn an actual
/// second Weaver process and drive it over HTTP.
/// </summary>
public class BenchmarkCardOrchestrator
{
    private readonly DatabaseService _db;
    private readonly ILogger<BenchmarkCardOrchestrator>? _logger;
    private readonly BenchmarkService _benchmarks;

    /// <summary>Creates and launches a fresh instance on the given port, editing the given workspace.
    /// Default: spawn the running Weaver executable with --urls/--no-open-browser and poll until it answers.</summary>
    public Func<string /*workspaceRoot*/, int /*port*/, CancellationToken, Task<IBenchmarkInstanceHost>> HostFactory { get; set; }

    /// <summary>Runs the benchmark's acceptance checks against the workspace. Default: the real
    /// <see cref="BenchmarkService.EvaluateChecksAsync"/> (filesystem + live web test).</summary>
    public Func<int /*level*/, string /*workspaceRoot*/, CancellationToken, Task<List<BenchmarkCheckResult>>> VerifyAsync { get; set; }

    public BenchmarkCardOrchestrator(DatabaseService db, ILogger<BenchmarkCardOrchestrator>? logger = null)
    {
        _db = db;
        _logger = logger;
        _benchmarks = new BenchmarkService(db);
        HostFactory = LocalWeaverInstanceHost.LaunchAsync;
        VerifyAsync = (level, root, ct) => _benchmarks.EvaluateChecksAsync(level, root, ct);
    }

    /// <summary>
    /// Runs the full flow: stage an isolated workspace → launch a fresh instance → inject the
    /// card → run it → verify. Never throws for a failed benchmark (the result carries the
    /// failure); only cancellation and staging/launch catastrophes bubble up.
    /// </summary>
    public async Task<BenchmarkOrchestrationResult> OrchestrateAsync(
        BenchmarkOrchestrationRequest request,
        Func<BenchmarkOrchestrationEvent, CancellationToken, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        var result = new BenchmarkOrchestrationResult();
        var emit = onProgress ?? ((_, _) => Task.CompletedTask);
        try
        {
            // 1 — stage an isolated workspace (a fresh temp dir by default) so the fresh
            // instance's edits never touch the parent's real benchmark sandbox.
            var workspace = string.IsNullOrWhiteSpace(request.WorkspaceRoot)
                ? StageWorkspace()
                : Path.GetFullPath(request.WorkspaceRoot);
            Directory.CreateDirectory(workspace);
            result.WorkspaceRoot = workspace;
            await emit(new BenchmarkOrchestrationEvent("staging", $"Staged isolated workspace at {workspace}"), ct);

            // 2 — launch the fresh instance on a free port (the launcher's port-fallback means
            // a busy preferred port is never a dead end).
            var port = ServerLauncherService.FindFreePort(5100);
            await using var instance = await HostFactory(workspace, port, ct);
            result.Launched = true;
            result.InstanceUrl = instance.BaseUrl;
            await emit(new BenchmarkOrchestrationEvent("launch", $"Fresh Weaver instance up at {instance.BaseUrl}", instance.BaseUrl), ct);

            // 3 — inject the benchmark card into the instance's board.
            result.CardId = await instance.InjectCardAsync(request.CardText, request.Column, ct);
            result.CardInjected = true;
            await emit(new BenchmarkOrchestrationEvent("inject", $"Injected benchmark card {result.CardId}"), ct);

            // 4 — run the card on the instance.
            result.RunStarted = true;
            await emit(new BenchmarkOrchestrationEvent("run", "Running the benchmark card on the fresh instance…"), ct);
            var run = await instance.RunCardAsync(request.CardText, workspace, result.CardId, request.EndpointId, ct);
            result.RunComplete = run.Complete;
            result.RunSummary = run.Summary;
            result.StepCount = run.StepCount;
            if (!run.Complete && string.IsNullOrWhiteSpace(result.Error)) result.Error = run.Error;
            await emit(new BenchmarkOrchestrationEvent("run-done",
                run.Complete ? $"Card run completed ({run.StepCount} step(s))" : "Card run did not complete"), ct);

            // 5 — verify end-to-end: filesystem checks + the live web-test suite. Progress from
            // the browser automation is re-emitted as a "webtest" stage so the Test Browser
            // panel watches the verification like any other live test.
            _benchmarks.BrowserTest.OnProgress = (e, ct2) =>
                emit(new BenchmarkOrchestrationEvent("webtest", e.Message, e.Url), ct2);
            try
            {
                result.Checks = await VerifyAsync(request.Level, workspace, ct);
            }
            finally
            {
                _benchmarks.BrowserTest.OnProgress = null;
            }
            var passed = result.Checks.Count(c => c.Passed);
            await emit(new BenchmarkOrchestrationEvent("verify",
                $"Verification: {passed}/{result.Checks.Count} check(s) passed"), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (string.IsNullOrWhiteSpace(result.Error)) result.Error = ex.Message;
            _logger?.LogWarning(ex, "Benchmark card orchestration failed: {Message}", ex.Message);
            await emit(new BenchmarkOrchestrationEvent("error", ex.Message), ct);
        }
        return result;
    }

    /// <summary>Creates a fresh temp workspace for an isolated run.</summary>
    private static string StageWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-benchmark-orchestrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>
/// The REAL fresh-instance host: spawns the running Weaver executable (or a configured binary)
/// on a free port with --urls/--no-open-browser, polls until its HTTP API answers, then drives
/// the board + agent endpoints directly. The spawned process tree is killed on dispose.
/// </summary>
public sealed class LocalWeaverInstanceHost : IBenchmarkInstanceHost
{
    private readonly HttpClient _http;
    private readonly Process? _process;

    public string BaseUrl { get; }
    public string WorkspaceRoot { get; }

    /// <summary>Optional override for the executable to spawn (defaults to the running Weaver).</summary>
    public static string? ExecutableOverride { get; set; }

    private LocalWeaverInstanceHost(string baseUrl, string workspaceRoot, Process? process, HttpClient http)
    {
        BaseUrl = baseUrl;
        WorkspaceRoot = workspaceRoot;
        _process = process;
        _http = http;
    }

    public static async Task<IBenchmarkInstanceHost> LaunchAsync(string workspaceRoot, int port, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}";
        var exe = ExecutableOverride ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new InvalidOperationException(
                $"Cannot locate the Weaver executable to launch a fresh instance (path: \"{exe}\").");

        var psi = new ProcessStartInfo(exe, $"--urls {url} --no-open-browser")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start a fresh Weaver instance ({exe}).");

        var log = new StringBuilder();
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                    lock (log) { if (log.Length < 20000) log.AppendLine(line); }
            }
            catch { }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    lock (log) { if (log.Length < 20000) log.AppendLine(line); }
            }
            catch { }
        });

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        string? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
            {
                string tail;
                lock (log) tail = log.ToString();
                throw new InvalidOperationException(
                    $"Fresh Weaver instance exited before becoming ready (code {proc.ExitCode}). Output:\n{tail}");
            }
            try
            {
                using var probe = await http.GetAsync(url + "/api/boarddata/load", ct);
                if (probe.IsSuccessStatusCode)
                    return new LocalWeaverInstanceHost(url, workspaceRoot, proc, http);
                lastError = $"HTTP {(int)probe.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                lastError = ex.Message;
            }
            await Task.Delay(400, ct);
        }

        ServerLauncherService.KillTree(proc);
        throw new InvalidOperationException(
            $"Fresh Weaver instance never became ready at {url} (last error: {lastError}).");
    }

    public async Task<string> InjectCardAsync(string cardText, string column, CancellationToken ct)
    {
        var raw = await _http.GetStringAsync(BaseUrl + "/api/boarddata/load", ct);
        var board = JsonNode.Parse(raw)?.AsObject()
            ?? throw new InvalidOperationException("Board data is not a JSON object.");
        if (string.IsNullOrWhiteSpace(column)) column = "selfImproving";
        var arr = board[column] as JsonArray;
        if (arr == null)
        {
            arr = new JsonArray();
            board[column] = arr;
        }

        var cardId = Guid.NewGuid().ToString("N");
        arr.Add(new JsonObject
        {
            ["id"] = cardId,
            ["text"] = cardText,
            ["filePath"] = WorkspaceRoot,
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            ["priority"] = "medium",
            ["attached"] = new JsonArray(),
            ["selfImproving"] = string.Equals(column, "selfImproving", StringComparison.OrdinalIgnoreCase),
            ["isDecomposing"] = false,
            ["_benchmark"] = true
        });

        var body = board.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(BaseUrl + "/api/boarddata/save", content, ct);
        resp.EnsureSuccessStatusCode();
        return cardId;
    }

    public async Task<BenchmarkRunOutcome> RunCardAsync(string cardText, string workspaceRoot, string? cardId, string? endpointId, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            prompt = cardText,
            project = workspaceRoot,
            cardId,
            endpointId,
            maxIterations = 5
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(BaseUrl + "/api/agent/execute", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new BenchmarkRunOutcome(false, null, 0, $"HTTP {(int)resp.StatusCode}: {Truncate(body)}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var complete = root.TryGetProperty("complete", out var c) && c.GetBoolean();
            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var steps = root.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Array ? st.GetArrayLength() : 0;
            return new BenchmarkRunOutcome(complete, summary, steps, null);
        }
        catch (JsonException)
        {
            return new BenchmarkRunOutcome(false, null, 0, $"Unparseable execute response: {Truncate(body)}");
        }
    }

    private static string Truncate(string s, int max = 2000) => s.Length <= max ? s : s[..max] + "…";

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_process != null && !_process.HasExited)
        {
            ServerLauncherService.KillTree(_process);
            try { _process.WaitForExit(3000); } catch { }
            _process.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
