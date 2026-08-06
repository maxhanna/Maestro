using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Weaver.Services;
using static Weaver.Services.AgentTokenMetrics;
using static Weaver.Services.AgentEditHeuristics;
using static Weaver.Services.AgentPlanParsing;
using static Weaver.Services.AgentMethodInventory;
using static Weaver.Services.AgentProjectUtilities;
using static Weaver.Services.AgentDiscovery;
using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentCodeFormatting;
using static Weaver.Services.AgentSkeleton;
using static Weaver.Services.AgentDiffUtilities;
using static Weaver.Services.AgentJsonUtilities;
using Weaver;
namespace Weaver.Controllers;

[ApiController]
[Route("api/agent")]
public partial class AgentController : ControllerBase
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly TerminalService _terminal;
    private readonly FileHintsManager _fileHints;
    private readonly ConfigFileService _configFile;
    private readonly EmailService _emailService;
    private readonly BoardDataService _boardData;
    private readonly EditKnowledgeService _editKnowledge;
    private readonly PushNotificationService _push;
    private readonly DatabaseService _db;
    private FrontendConfig? _cfgCache;
    private DateTime _cfgCacheTime = DateTime.MinValue;
    private async Task<FrontendConfig> LoadConfigAsync()
    {
        if (_cfgCache == null || (DateTime.UtcNow - _cfgCacheTime).TotalSeconds > 3)
        {
            _cfgCache = await _configFile.LoadConfigAsync();
            _cfgCacheTime = DateTime.UtcNow;
            // LLM request timeout is user-configurable: 0 (or <5) = infinite, else minutes.
            var timeoutMinutes = _cfgCache.llmTimeoutMinutes;
            _infiniteTimeout = timeoutMinutes <= 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMinutes(Math.Max(5, timeoutMinutes));
        }
        return _cfgCache;
    }
    private bool _lastConnectionCheckResult = true;
    private bool _gracefulStop;
    private static DateTime _nextConnectivityCheck = DateTime.MinValue;
    private static TimeSpan _infiniteTimeout = Timeout.InfiniteTimeSpan;
    private static readonly ConcurrentDictionary<string, PendingQuestion> _pendingQuestions = new();
    private static readonly ConcurrentDictionary<string, PendingContextReview> _pendingContextReviews = new();
    private static readonly ConcurrentDictionary<string, HashSet<int>> _cancelledSteps = new();
    private static readonly ConcurrentDictionary<string, StringBuilder> _stepThinkingStore = new();
    private static readonly ConcurrentDictionary<string, int> _complexityScores = new();
    private static readonly ConcurrentDictionary<string, int> _atomicStepEstimates = new();
    public AgentController(
        IHttpClientFactory cf, IConfiguration config,
        IWebHostEnvironment env, TerminalService terminal, FileHintsManager fileHints,
        ConfigFileService configFile, EmailService emailService, BoardDataService boardData,
        PushNotificationService push, DatabaseService db)
    {
        _clientFactory = cf; _config = config; _env = env; _terminal = terminal;
        _fileHints = fileHints; _configFile = configFile; _emailService = emailService;
        _boardData = boardData; _push = push; _db = db;
        // Wire the per-endpoint stream-health tracker to SQLite so badges reflect
        // reliability across app restarts, not just the current session. The static
        // hooks capture this controller's DatabaseService (same singleton in DI).
        EndpointHealthService.RegisterPersistence(
            loader: () =>
            {
                try { return _db.GetValue("weaver_config", EndpointHealthService.DbKey); }
                catch { return null; }
            },
            saver: json =>
            {
                try { _db.SetValue("weaver_config", EndpointHealthService.DbKey, json); }
                catch { }
            });
        _editKnowledge = new EditKnowledgeService(
            db,
            llmCaller: async (sys, usr, ct) =>
            {
                var (raw, _, err) = await CallLlmRawStreaming(sys, usr, false, ct,
                    requestTimeout: _infiniteTimeout, maxTokens: 512);
                return (raw, err);
            },
            logger: (lvl, msg) =>
            {
                Task.Run(async () =>
                {
                    try { await EmitLog(false, lvl, msg, ct: CancellationToken.None); }
                    catch { }
                });
            });
    }
    private async Task EmitLog(bool emit, string level, string message, object? detail = null, CancellationToken ct = default)
    {
        if (!emit) return;
        await SendSse(Response, "log", new { ts = DateTime.UtcNow.ToString("o"), level, message, detail }, ct);
    }

    /// <summary>
    /// Emits a distinct 'rejected' log entry for a rejected step/proposal. Renders as its
    /// own styled row in the card's agent log with the corrective feedback attached as
    /// detail, so users see WHY a step was blocked instead of a generic warn/skip line.
    /// </summary>
    private async Task EmitRejectedLog(bool emitSse, string message, string feedback, CancellationToken ct)
    {
        if (!emitSse) return;
        await EmitLog(true, "rejected", message, feedback, ct);
    }
    private static readonly SemaphoreSlim SseWriteLock = new(1, 1);
    private static async Task SendSse(HttpResponse response, string eventName, object data, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(data);
            // Serialize writes so the concurrent /slots progress poller can't
            // interleave partial frames with in-flight token/log events.
            await SseWriteLock.WaitAsync(ct);
            try
            {
                await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
            finally { SseWriteLock.Release(); }
        }
        catch (OperationCanceledException e) { Console.WriteLine($"ERROR, OperationCanceledException. Message: {e.Message}"); }
        catch (ObjectDisposedException e) { Console.WriteLine($"ERROR, ObjectDisposedException. Message: {e.Message}"); }
        catch (IOException e) { Console.WriteLine($"ERROR, IOException. Message: {e.Message}"); }
        catch (Exception e) { Console.WriteLine($"ERROR, Exception. Message: {e.Message}"); }
    }
    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody] AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt)) return BadRequest("Prompt is required");
        var projectRoot = AgentProjectUtilities.GetProjectRoot(req.Project, _config, _env);
        var (runBaseUrl2, runModel2, runEndpointName2) = await ResolveRunEndpointAsync(req.EndpointId);
        _runBaseUrl = runBaseUrl2;
        _runModel = runModel2;
        await EmitLog(true, "info", "Orchestrating Request.", new { projectRoot, task = req.Prompt, endpoint = runEndpointName2 });
        var (allSteps, plan, complete) = await Orchestrate(req.Prompt, projectRoot, emitSse: false);
        return Ok(new
        {
            summary = plan?.Summary ?? "",
            thinking = plan?.Thinking ?? "",
            complete,
            steps = allSteps,
            filesEdited = ExtractFilesEdited(allSteps)
        });
    }
    [HttpPost("apply")]
    public async Task<IActionResult> ApplyEdits([FromBody] ApplyEditsRequest req)
    {
        if (req.Edits == null || req.Edits.Count == 0) return BadRequest(new { error = "No edits provided" });
        var projectRoot = AgentProjectUtilities.GetProjectRoot(req.Project, _config, _env);
        var editResults = await ApplyEditsDirect(req.Edits, projectRoot);
        var commandResults = new List<object>();
        if (req.Commands != null && req.Commands.Count > 0)
        {
            _terminal.Start();
            foreach (var cmd in req.Commands)
            {
                try
                {
                    // Commands run right after edits often hit the same transient blips (a file
                    // briefly locked by the editor/daemon, a feed that dropped) — retry once.
                    var output = await RunTerminalCommandWithRetryAsync(cmd.Command, projectRoot, false, CancellationToken.None);
                    commandResults.Add(new { command = cmd.Command, status = "done", output });
                }
                catch (Exception ex) { commandResults.Add(new { command = cmd.Command, status = "error", error = ex.Message }); }
            }
        }
        return Ok(new { edits = editResults, commands = commandResults });
    }
    [HttpPost("apply-diff")]
    public async Task<IActionResult> ApplyDiff([FromBody] ApplyDiffRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DiffPath)) return BadRequest(new { error = "No diff path provided" });
        var projectRoot = AgentProjectUtilities.GetProjectRoot(req.Project, _config, _env);
        var fullDiffPath = Path.GetFullPath(Path.Combine(projectRoot, req.DiffPath.TrimStart('/', '\\')));
        if (!System.IO.File.Exists(fullDiffPath))
            return NotFound(new { error = "Diff file not found", path = fullDiffPath });
        try
        {
            var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"apply \"{fullDiffPath}\"",
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit(10000);
            if (proc.ExitCode != 0)
                return Ok(new { success = false, error = $"git apply failed: {error}", output, diffPath = req.DiffPath });
            return Ok(new { success = true, output, diffPath = req.DiffPath });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    [HttpGet("diff-content")]
    public async Task<IActionResult> DiffContent([FromQuery] string project, [FromQuery] string diffPath)
    {
        if (string.IsNullOrWhiteSpace(diffPath)) return BadRequest(new { error = "No diff path provided" });
        var projectRoot = AgentProjectUtilities.GetProjectRoot(project, _config, _env);
        var fullDiffPath = Path.GetFullPath(Path.Combine(projectRoot, diffPath.TrimStart('/', '\\')));
        if (!System.IO.File.Exists(fullDiffPath))
            return NotFound(new { error = "Diff file not found", path = fullDiffPath });
        try
        {
            var content = await System.IO.File.ReadAllTextAsync(fullDiffPath);
            return Ok(new { success = true, content });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("delete-diff")]
    public async Task<IActionResult> DeleteDiff([FromBody] ApplyDiffRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DiffPath)) return BadRequest(new { error = "No diff path provided" });
        var projectRoot = AgentProjectUtilities.GetProjectRoot(req.Project, _config, _env);
        var fullDiffPath = Path.GetFullPath(Path.Combine(projectRoot, req.DiffPath.TrimStart('/', '\\')));
        if (!System.IO.File.Exists(fullDiffPath))
            return NotFound(new { error = "Diff file not found", path = fullDiffPath });
        try
        {
            System.IO.File.Delete(fullDiffPath);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("verify-diffs")]
    public async Task<IActionResult> VerifyDiffs([FromBody] VerifyDiffsRequest req)
    {
        if (req.DiffPaths == null || req.DiffPaths.Count == 0)
            return Ok(new { existing = new List<string>(), missing = new List<string>() });
        var projectRoot = AgentProjectUtilities.GetProjectRoot(req.Project, _config, _env);
        var existing = new List<string>();
        var missing = new List<string>();
        foreach (var diffPath in req.DiffPaths)
        {
            if (string.IsNullOrWhiteSpace(diffPath)) continue;
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, diffPath.TrimStart('/', '\\')));
            if (System.IO.File.Exists(fullPath))
                existing.Add(diffPath);
            else
                missing.Add(diffPath);
        }
        return Ok(new { existing, missing });
    }
    [HttpPost("execute-stream")]
    public async Task ExecuteStream([FromBody] AgentRequest req)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers["Connection"] = "keep-alive";
        var bufferingFeature = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        bufferingFeature?.DisableBuffering();
        await Response.StartAsync(Response.HttpContext.RequestAborted);
        var keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(Response.HttpContext.RequestAborted);
        var keepaliveTask = Task.Run(async () =>
        {
            while (!keepaliveCts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(15000, keepaliveCts.Token); await Response.WriteAsync(":\n\n", keepaliveCts.Token); await Response.Body.FlushAsync(keepaliveCts.Token); }
                catch { break; }
            }
        }, keepaliveCts.Token);
        var streamLogPath = Path.Combine(AppContext.BaseDirectory, "weaver-stream-errors.log");
        try { System.IO.File.AppendAllText(streamLogPath, $"[{DateTime.Now:HH:mm:ss}] stream start cardId={req.CardId}\n"); } catch { }
        try
        {
            await ExecuteStreamCore(req);
        }
        catch (OperationCanceledException)
        {
            try { System.IO.File.AppendAllText(streamLogPath, $"[{DateTime.Now:HH:mm:ss}] ABORTED (RequestAborted={Response.HttpContext.RequestAborted.IsCancellationRequested}): {req.CardId}\n"); } catch { }
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText(streamLogPath, $"[{DateTime.Now:HH:mm:ss}] FATAL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n"); } catch { }
            await EmitLog(true, "error", $"⛔ Stream terminated unexpectedly: {ex.Message}", ct: Response.HttpContext.RequestAborted);
        }
        finally
        {
            keepaliveCts.Cancel();
            try { await keepaliveTask; } catch { }
            try { System.IO.File.AppendAllText(streamLogPath, $"[{DateTime.Now:HH:mm:ss}] stream end cardId={req.CardId}\n"); } catch { }
        }
    }

    private async Task ExecuteStreamCore(AgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            await SendSse(Response, "error", new { message = "Prompt is required" });
            await SendSse(Response, "done", new { });
            return;
        }
        try
        {
            var projectRoot = AgentProjectUtilities.GetProjectRoot(req.Project, _config, _env);
            await SendSse(Response, "phase", new { phase = "start", projectRoot });
            // Resolve the card's chosen LLM endpoint so this run talks to that endpoint, and
            // announce it over SSE so the frontend can label this agent section with its LLM/endpoint.
            var (runBaseUrl, runModel, runEndpointName) = await ResolveRunEndpointAsync(req.EndpointId);
            _runBaseUrl = runBaseUrl;
            _runModel = runModel;
            await SendSse(Response, "run-start", new
            {
                cardId = req.CardId,
                runId = req.RunId,
                endpointName = runEndpointName,
                endpointUrl = runBaseUrl,
                endpointModel = runModel
            }, ct: Response.HttpContext.RequestAborted);
            await EmitLog(true, "info", "Agent started", new { projectRoot, task = req.Prompt, endpoint = runEndpointName });
            AgentPlan? existingPlan = null;
            HashSet<int>? completedIndices = null;
            bool isBenchmark = req.IsBenchmark;
            if (!string.IsNullOrWhiteSpace(req.CardId))
            {
                var (loadedPlan, loadedCompleted, loadedBenchmark) = await LoadPlanFromBoardDataAsync(req.CardId);
                existingPlan = loadedPlan;
                completedIndices = loadedCompleted;
                if (loadedBenchmark) isBenchmark = true;
            }
            if (isBenchmark)
            {
                projectRoot = !string.IsNullOrWhiteSpace(req.BenchmarkProjectRoot)
                    ? Path.GetFullPath(req.BenchmarkProjectRoot)
                    : AgentProjectUtilities.GetBenchmarkSandboxPath();
                await EmitLog(true, "info", "Benchmark sandbox active", new { sandbox = projectRoot });
                await SendSse(Response, "phase", new { phase = "sandbox", sandbox = projectRoot }, ct: Response.HttpContext.RequestAborted);
            }
            var (allSteps, plan, complete) = await Orchestrate(
                req.Prompt, projectRoot, emitSse: true,
                ct: Response.HttpContext.RequestAborted,
                attachedFiles: req.Files?.Count > 0 ? req.Files : null,
                steeringContext: req.SteeringContext,
                existingPlan: existingPlan,
                completedStepIndices: completedIndices,
                cardId: req.CardId,
                createTests: req.CreateTests,
                buildCommands: req.BuildCommands);
            var filesEdited = ExtractFilesEdited(allSteps);
            var editsApplied = AgentProjectUtilities.HasSuccessfulEdits(allSteps);
            if (isBenchmark)
            {
                var anyStepsAttempted = allSteps.OfType<Dictionary<string, object?>>()
                    .Any(s => s.TryGetValue("type", out var t) &&
                              t?.ToString() is "plan_step" or "command" or "edit" or "create");
                var planAlreadyDone = existingPlan != null && completedIndices != null && completedIndices.Count >= existingPlan.Plan.Count;
                complete = anyStepsAttempted || planAlreadyDone;
                editsApplied = true;
            }
            await SendSse(Response, "done", new
            {
                summary = plan?.Summary ?? "",
                thinking = plan?.Thinking ?? "",
                complete,
                editsApplied,
                incomplete = AgentPlanParsing.TaskExpectsFileChanges(req.Prompt) && !complete,
                warning = !complete && AgentPlanParsing.TaskExpectsFileChanges(req.Prompt)
                    ? (editsApplied ? "Task may be incomplete. Please review."
                                    : "No files were modified.")
                    : (string?)null,
                steps = allSteps,
                filesEdited
            });
            if (req.SelfImproving)
            {
                try { await RunSelfImprovingPipeline(req.Prompt, projectRoot, allSteps, plan, complete, editsApplied); }
                catch (Exception siEx) { await EmitLog(true, "warn", $"Self-improving error: {siEx.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[AGENT CRASH] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            await SendSse(Response, "error", new { message = $"{ex.GetType().Name}: {ex.Message}" });
            _ = _push.SendNotificationAsync("Weaver", "Agent task failed");
        }
        finally
        {
            _runBaseUrl = null;
            _runModel = null;
            if (!string.IsNullOrWhiteSpace(req.CardId))
            {
                _cancelledSteps.TryRemove(req.CardId, out _);
                _stepThinkingStore.TryRemove(req.CardId, out _);
            }
        }
    }
    [HttpGet("questions/pending")]
    public IActionResult GetPendingQuestions()
    {
        var list = _pendingQuestions.Values.OrderBy(q => q.CreatedUtc)
            .Select(q => new { q.Id, q.Question, q.Fields, q.CreatedUtc }).ToList();
        return Ok(new { questions = list });
    }
    [HttpPost("questions/answer")]
    public async Task<IActionResult> AnswerQuestion([FromBody] QuestionAnswerRequest req)
    {
        if (!_pendingQuestions.TryRemove(req.Id, out var pending))
            return NotFound("Question not found or already answered");
        pending.Answer.TrySetResult(req.Answers);
        return Ok(new { status = "answered" });
    }
    [HttpPost("context-review/confirm")]
    public IActionResult ConfirmContextReview([FromBody] ContextReviewAnswer req)
    {
        if (!_pendingContextReviews.TryRemove(req.Id, out var pending))
            return NotFound("Context review not found or already answered");
        pending.Answer.TrySetResult(req.Files ?? pending.Files);
        return Ok(new { status = "confirmed" });
    }
    [HttpPost("cancel-step")]
    public IActionResult CancelPlanStep([FromBody] CancelStepRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CardId))
            return BadRequest("cardId is required");
        var steps = _cancelledSteps.GetOrAdd(req.CardId, _ => new HashSet<int>());
        lock (steps) { steps.Add(req.StepIndex); }
        return Ok(new { status = "cancelled", cardId = req.CardId, stepIndex = req.StepIndex });
    }

    // ── Per-endpoint stream health for the endpoint picker ───────────────
    // Stream reliability counters accumulated by EndpointHealthService.RecordCall in
    // the LLM wrappers (transport/stream/truncation failures vs total calls). Rendered
    // as a badge in the endpoint picker so flaky endpoints are easy to spot and swap.
    [HttpGet("endpoint-health")]
    public IActionResult GetEndpointHealth()
    {
        EndpointHealthService.HydrateFromDisk();
        // Flush the latest counters to disk so an app crash or normal restart can
        // never lose the session's accumulated evidence (debounced on the hot path;
        // this explicit call guarantees the write happens while the user is looking).
        EndpointHealthService.Persist();
        var now = DateTime.UtcNow;
        // Prune endpoints the user has removed or that went idle: entries whose last
        // activity (success or stream error) is older than the shared 24h window are
        // dropped so the tracker matches what was just persisted (same staleness rule).
        EndpointHealthService.PruneStale();
        var items = EndpointHealthService.Entries
            .Select(kv => new
            {
                baseUrl = kv.Key,
                calls = kv.Value.Calls,
                streamErrors = kv.Value.StreamErrors,
                recovered = kv.Value.Recovered,
                recoveryFailed = kv.Value.RecoveryFailed,
                errorRate = kv.Value.Calls > 0
                    ? Math.Round(100.0 * kv.Value.StreamErrors / kv.Value.Calls, 1)
                    : 0,
                lastStreamErrorAt = kv.Value.LastStreamErrorUtc == default
                    ? null : kv.Value.LastStreamErrorUtc.ToString("o"),
                lastSuccessAt = kv.Value.LastSuccessUtc == default
                    ? null : kv.Value.LastSuccessUtc.ToString("o"),
                stale = kv.Value.LastSuccessUtc != default &&
                       kv.Value.LastStreamErrorUtc != default &&
                       (now - kv.Value.LastSuccessUtc).TotalMinutes > 60 &&
                       kv.Value.LastStreamErrorUtc > kv.Value.LastSuccessUtc
            })
            .OrderByDescending(x => x.streamErrors)
            .ThenByDescending(x => x.errorRate)
            .ToList();
        return Ok(items);
    }

    // ── LLM reachability probe for the benchmark panel ────────────────────
    // The front end disables the Run All / Run benchmark buttons (with a
    // tooltip explaining why) while the configured LLM endpoint is
    // unreachable, so a user isn't told "benchmark running" only to have the
    // run fail a minute later at the connectivity check. This is a lightweight
    // HTTP probe (no PowerShell TCP check) so the panel opens fast.
    [HttpGet("llm-reachable")]
    public async Task<IActionResult> GetLlmReachable()
    {
        var baseUrl = await GetLlamaBaseUrl();
        var reachable = await ProbeLlmReachableAsync(baseUrl);
        return Ok(new { reachable, url = baseUrl });
    }
    private async Task<bool> ProbeLlmReachableAsync(string baseUrl)
    {
        try
        {
            using var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var resp = await client.GetAsync(baseUrl.TrimEnd('/') + "/api/tags");
            return resp.IsSuccessStatusCode || (int)resp.StatusCode < 500;
        }
        catch { return false; }
    }

    // ── Improvement suggestions for a completed card ──────────────────────
    // When a card finishes successfully, the front end calls this endpoint to
    // generate 0-3 LLM suggestions (each with file attachments for discovery
    // context). Suggestions are persisted onto the card in board data
    // (_suggestions) so they survive reloads and render as a "Suggestions"
    // section on the finished card. Never creates new kanban cards itself.
    [HttpPost("suggest-improvements")]
    public async Task<IActionResult> SuggestImprovements([FromBody] JsonElement payload)
    {
        string project = "", cardId = "", cardText = "", summary = "", thinking = "";
        List<string> filesEdited = new();
        List<string> stepLog = new();
        List<string> planLog = new();
        if (payload.TryGetProperty("project", out var projEl)) project = projEl.GetString() ?? "";
        if (payload.TryGetProperty("cardId", out var cidEl)) cardId = cidEl.GetString() ?? "";
        if (payload.TryGetProperty("cardText", out var txtEl)) cardText = txtEl.GetString() ?? "";
        if (payload.TryGetProperty("summary", out var sumEl)) summary = sumEl.GetString() ?? "";
        if (payload.TryGetProperty("thinking", out var thEl)) thinking = thEl.GetString() ?? "";
        if (payload.TryGetProperty("filesEdited", out var feEl) && feEl.ValueKind == JsonValueKind.Array)
            foreach (var f in feEl.EnumerateArray())
                if (f.ValueKind == JsonValueKind.String) filesEdited.Add(f.GetString() ?? "");
        if (payload.TryGetProperty("steps", out var stEl) && stEl.ValueKind == JsonValueKind.Array)
            foreach (var s in stEl.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                    stepLog.Add(s.GetString()!);
        if (payload.TryGetProperty("planItems", out var piEl) && piEl.ValueKind == JsonValueKind.Array)
            foreach (var p in piEl.EnumerateArray())
                if (p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                    planLog.Add(p.GetString()!);
        // "More like this" top-up: the card already has suggestions and the user
        // asked for more. topup=true routes into the topping-up branch; existing
        // is the front-end's live copy of the current suggestions, used as
        // context so the LLM EXTENDS the set instead of repeating it.
        bool topup = false;
        var existingDescs = new List<string>();
        if (payload.TryGetProperty("topup", out var topEl)) topup = topEl.ValueKind == JsonValueKind.True;
        if (payload.TryGetProperty("existing", out var exEl) && exEl.ValueKind == JsonValueKind.Array)
            foreach (var e in exEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("description", out var dEl)) continue;
                var d = dEl.GetString();
                if (!string.IsNullOrWhiteSpace(d)) existingDescs.Add(d.Trim());
            }
        if (string.IsNullOrWhiteSpace(cardId) || string.IsNullOrWhiteSpace(project))
            return BadRequest(new { error = "cardId and project are required" });

        try
        {
            var existing = await ReadCardSuggestionsAsync(cardId);
            if (topup)
            {
                // "More like this" only tops up while the stored set is under the
                // 3-cap; at the cap there's nothing left to add.
                if (existing != null && existing.Count >= 3)
                    return Ok(new { suggestions = existing });
                // Robustness: if the front-end's existing list is empty but the
                // board still carries suggestions, re-derive descriptions from
                // the stored objects so dedupe still has context.
                if (existingDescs.Count == 0 && existing != null)
                {
                    foreach (var ex in existing)
                    {
                        try
                        {
                            using var d = JsonDocument.Parse(JsonSerializer.Serialize(ex));
                            if (d.RootElement.TryGetProperty("description", out var dd))
                            {
                                var t = dd.GetString();
                                if (!string.IsNullOrWhiteSpace(t)) existingDescs.Add(t.Trim());
                            }
                        }
                        catch { }
                    }
                }
            }
            // Initial generation: an existing stored set (even an empty one) means
            // the LLM already ran — return it without re-running.
            else if (existing != null)
            {
                return Ok(new { suggestions = existing });
            }

            // Only top-ups budget slots from the existing set (defensive: a plain
            // initial request always asks for the full 0-3 even if it somehow
            // carried an `existing` array).
            var slots = topup ? Math.Max(1, 3 - (existing?.Count ?? existingDescs.Count)) : 3;
            var projectRoot = Path.GetFullPath(project);
            var sb = new StringBuilder();
            if (topup)
            {
                sb.AppendLine("A kanban card's Suggestions section is being topped up by a \"More like this\" request.");
                sb.AppendLine($"The card already has {existingDescs.Count} suggestion(s). Generate up to {slots} NEW, DISTINCT follow-up suggestion(s) that the existing set missed — same spirit and grounding, but NOT repeats or paraphrases of what is already listed.");
                sb.AppendLine("Ground every suggestion in what the agent actually did, thought, and the real files it touched ");
                sb.AppendLine("— prefer the single most valuable next increment over generic advice. Do NOT suggest work that ");
                sb.AppendLine("the card already completed or that is unrelated to its task.");
                sb.AppendLine("Each suggestion MUST include file attachments (existing project-relative paths) that would serve as ");
                sb.AppendLine("discovery context for implementing it — pick real files that actually exist in the project.");
                if (existingDescs.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("EXISTING SUGGESTIONS (do not repeat or paraphrase these):");
                    foreach (var d in existingDescs) sb.AppendLine("  - " + d);
                }
            }
            else
            {
                sb.AppendLine("A card on the kanban board was just completed successfully.");
                sb.AppendLine("Generate up to 3 tightly-scoped, on-point follow-up suggestions for THIS card's work.");
                sb.AppendLine("Ground every suggestion in what the agent actually did, thought, and the real files it touched ");
                sb.AppendLine("— prefer the single most valuable next increment over generic advice. Do NOT suggest work that ");
                sb.AppendLine("the card already completed or that is unrelated to its task.");
                sb.AppendLine("Each suggestion MUST include file attachments (existing project-relative paths) that would serve as ");
                sb.AppendLine("discovery context for implementing it — pick real files that actually exist in the project.");
            }
            sb.AppendLine();
            sb.AppendLine($"CARD TASK:\n{cardText}");
            if (!string.IsNullOrWhiteSpace(thinking)) sb.AppendLine($"\nAGENT THINKING (reasoning that drove the work):\n{thinking}");
            if (planLog.Count > 0) sb.AppendLine($"\nPLAN ITEMS:\n{string.Join("\n", planLog.Select(x => "  - " + x))}");
            if (stepLog.Count > 0) sb.AppendLine($"\nSTEPS EXECUTED:\n{string.Join("\n", stepLog.Select(x => "  - " + x))}");
            if (!string.IsNullOrWhiteSpace(summary)) sb.AppendLine($"\nCOMPLETION SUMMARY:\n{summary}");
            if (filesEdited.Count > 0) sb.AppendLine($"\nFILES CHANGED:\n{string.Join("\n", filesEdited.Select(f => "  - " + f))}");
            sb.AppendLine();
            sb.AppendLine($"Reply ONLY with a JSON array of 0-{(topup ? slots : 3)} objects, each shaped:");
            sb.AppendLine(@"[{""description"": ""<suggestion text>"", ""files"": [""rel/path/file.ts"", ""rel/path/other.cs""]}]
If nothing meaningful remains, reply with an empty array [] — never invent work.");

            var (raw, _, err) = await CallLlmRaw(
                "You are an expert product engineer. Output ONLY valid JSON. No markdown, no explanation.",
                sb.ToString(), CancellationToken.None, requestTimeout: _infiniteTimeout, maxTokens: 1024);
            var suggestions = new List<object>();
            // NOTE: CallLlmRaw's ParseAgentResponse mangles JSON arrays (it slices
            // from first { to last }, destroying the array wrapper), so it reports
            // "JSON parse failed" for array payloads. The raw content is still
            // returned intact — we parse it ourselves here, so ignore err.
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var cleaned = raw.Trim();
                if (cleaned.StartsWith("```"))
                {
                    var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                    if (m.Success) cleaned = m.Groups[1].Value.Trim();
                }
                var fb = cleaned.IndexOf('[');
                var lb = cleaned.LastIndexOf(']');
                if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];
                try
                {
                    using var doc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
                    var newDescs = new List<string>();
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (suggestions.Count >= slots) break;
                        if (el.ValueKind != JsonValueKind.Object) continue;
                        var desc = el.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(desc)) continue;
                        desc = desc.Trim();
                        // Top-ups must EXTEND the set: drop anything that parrots an
                        // existing suggestion (or another one from this same batch).
                        if (topup && IsDuplicateSuggestion(desc, existingDescs.Concat(newDescs))) continue;
                        newDescs.Add(desc);
                        var files = new List<string>();
                        if (el.TryGetProperty("files", out var fEl) && fEl.ValueKind == JsonValueKind.Array)
                            foreach (var fp in fEl.EnumerateArray())
                            {
                                var path = fp.GetString()?.Replace('\\', '/').Trim().TrimStart('/');
                                if (string.IsNullOrWhiteSpace(path)) continue;
                                // Only attach paths that actually exist in the project.
                                var full = Path.GetFullPath(Path.Combine(projectRoot, path));
                                if (full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(full))
                                    files.Add(path);
                            }
                        suggestions.Add(new
                        {
                            id = Guid.NewGuid().ToString("N")[..8],
                            description = desc,
                            files = files,
                            createdAt = DateTime.UtcNow.ToString("o")
                        });
                    }
                }
                catch { /* partial or malformed JSON — keep what we have */ }
            }

            // Top-up: keep the existing set first, then append the new (already
            // deduped) suggestions, never exceeding the 3-suggestion cap.
            if (topup && existing != null)
            {
                var merged = new List<object>(existing);
                foreach (var s in suggestions)
                {
                    if (merged.Count >= 3) break;
                    merged.Add(s);
                }
                suggestions = merged;
            }

            await PersistCardSuggestionsAsync(cardId, suggestions);
            // Persist the context that grounded this generation alongside the
            // suggestions so a future 'explain this suggestion' tooltip can show
            // WHY each idea was proposed, even after a reload. Written on every
            // generation (initial + top-up) since the card context is the same.
            await PersistCardSuggestionsContextAsync(cardId, new
            {
                summary,
                thinking,
                steps = stepLog,
                planItems = planLog,
                filesEdited,
                generatedAt = DateTime.UtcNow.ToString("o")
            });
            var addedNothing = suggestions.Count == 0 || (topup && existing != null && suggestions.Count == existing.Count);
            if (addedNothing)
                Console.WriteLine($"[SUGGEST IMPROVEMENTS] {(topup ? "top-up added nothing new" : "no suggestions")} for card {cardId} (rawlen={(raw?.Length ?? 0)} err={(string.IsNullOrWhiteSpace(err) ? "none" : err)})");
            return Ok(new { suggestions });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SUGGEST IMPROVEMENTS] {ex.Message}");
            return Ok(new { suggestions = new List<object>() });
        }
    }
    private async Task<List<object>?> ReadCardSuggestionsAsync(string cardId)
    {
        var raw = await _boardData.LoadRawAsync();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var jsonDoc = JsonDocument.Parse(raw);
        var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
        if (root == null) return null;
        var columns = new[] { "todo", "doing", "done", "archived", "selfImproving" };
        foreach (var column in columns)
        {
            if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                continue;
            foreach (var item in columnItems)
            {
                if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                    continue;
                // A stored _suggestions property (even an empty array) means
                // generation already completed — return it so the LLM is never
                // re-run for a card that legitimately earned 0 suggestions.
                if (cardObj["_suggestions"] is JsonArray arr)
                    return JsonSerializer.Deserialize<List<object>>(arr.ToJsonString()) ?? new List<object>();
                return null;
            }
        }
        return null;
    }
    private async Task PersistCardSuggestionsAsync(string cardId, List<object> suggestions)
    {
        await TryUpdateCardAsync(cardId, card => card["_suggestions"] = JsonNode.Parse(JsonSerializer.Serialize(suggestions)));
    }

    /// <summary>
    /// Persists the context that grounded suggestion generation (summary, thinking,
    /// steps, plan items, files edited) onto the card as _suggestionsContext, written
    /// alongside _suggestions so a future 'explain this suggestion' tooltip can show
    /// WHY each suggestion was proposed — even after a reload.
    /// </summary>
    private async Task PersistCardSuggestionsContextAsync(string cardId, object context)
    {
        await TryUpdateCardAsync(cardId, card => card["_suggestionsContext"] = JsonNode.Parse(JsonSerializer.Serialize(context)));
    }

    /// <summary>
    /// Shared board write: loads the board, finds the card in any column, applies
    /// the mutation, and persists. Used by all the _suggestions/_suggestionsContext
    /// writers so the find-and-save scan lives in exactly one place.
    /// </summary>
    private async Task TryUpdateCardAsync(string cardId, Action<JsonObject> mutate)
    {
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;
            var columns = new[] { "todo", "doing", "done", "archived", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;
                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;
                    mutate(cardObj);
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SUGGEST IMPROVEMENTS PERSIST] {ex.Message}");
        }
    }

    /// <summary>
    /// Normalizes a suggestion description for similarity comparison: lowercase,
    /// non-alphanumerics become spaces, whitespace collapsed. Shared by the
    /// "More like this" top-up dedupe so "Add error handling" and
    /// "add error handling." compare equal.
    /// </summary>
    internal static string NormalizeSuggestionText(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in (s ?? "").ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// True when a new suggestion duplicates one already in the set: an exact
    /// normalized match, a meaningful containment (one description is embedded
    /// in the other), or a high token overlap. Used by the "More like this"
    /// top-up so the LLM extends the card's suggestions instead of repeating them.
    /// </summary>
    internal static bool IsDuplicateSuggestion(string desc, IEnumerable<string> known)
    {
        var norm = NormalizeSuggestionText(desc);
        if (string.IsNullOrEmpty(norm)) return true;
        var knownNorms = known.Select(NormalizeSuggestionText).Where(n => !string.IsNullOrEmpty(n)).ToList();
        var tokens = norm.Split(' ').ToHashSet();
        foreach (var k in knownNorms)
        {
            if (k == norm) return true;
            // Containment only counts when the shorter side is a real phrase
            // (>= 8 chars) — a one-word tag like "add" shouldn't veto everything.
            var minLen = Math.Min(k.Length, norm.Length);
            if (minLen >= 8 && (norm.Contains(k) || k.Contains(norm))) return true;
            var kTokens = k.Split(' ');
            var overlap = kTokens.Count(t => tokens.Contains(t));
            var denom = tokens.Count + kTokens.Length - overlap;
            if (denom > 0 && (double)overlap / denom >= 0.7) return true;
        }
        return false;
    }
    private async Task<bool> CheckLlmConnectivity(string projectRoot, bool emitSse, CancellationToken ct)
    {
        if (_nextConnectivityCheck != DateTime.MinValue &&
            DateTime.UtcNow - _nextConnectivityCheck < TimeSpan.FromMinutes(5))
        {
            await EmitLog(emitSse, "info", "Skipping connectivity check (cached)", ct: ct);
            return _lastConnectionCheckResult;
        }
        var baseUrl = await GetLlamaBaseUrl();
        _lastConnectionCheckResult = await CheckForConnectivity(projectRoot, emitSse, baseUrl, ct);
        _nextConnectivityCheck = DateTime.UtcNow.AddMinutes(5);
        return _lastConnectionCheckResult;
    }
    private async Task<bool> CheckForConnectivity(
        string projectRoot, bool emitSse, string baseUrl, CancellationToken ct)
    {
        var uri = new Uri(baseUrl);
        await EmitLog(emitSse, "info", $"Connectivity check: {uri.Host}:{uri.Port}", ct: ct);
        var tcpCmd = OperatingSystem.IsWindows()
            ? $"powershell -Command \"Test-NetConnection {uri.Host} -Port {uri.Port} -WarningAction SilentlyContinue | Select-Object TcpTestSucceeded | Format-List\""
            : $"nc -zv -w 2 {uri.Host} {uri.Port} 2>&1";
        var step = new AgentStep { Index = 0, Type = "command", Command = tcpCmd, Description = "TCP Check" };
        var results = await ExecuteSteps(new List<AgentStep> { step }, projectRoot, 0, emitSse, ct);
        var first = results.FirstOrDefault() as Dictionary<string, object?>;
        var output = first?.TryGetValue("output", out var o) == true ? o?.ToString() ?? "" : "";
        var succeeded = output.Contains("TcpTestSucceeded : True", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("HTTP 200", StringComparison.Ordinal);
        if (succeeded) { await EmitLog(emitSse, "info", $"LLM reachable", ct: ct); return true; }
        try
        {
            var client = _clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var resp = await client.GetAsync(baseUrl + "/api/tags", ct);
            if (resp.IsSuccessStatusCode || (int)resp.StatusCode < 500)
            { await EmitLog(emitSse, "info", $"LLM reachable via HTTP", ct: ct); return true; }
        }
        catch { }
        await EmitLog(emitSse, "error", $"LLM unreachable at {uri.Host}:{uri.Port}", ct: ct);
        return false;
    }
    private static List<object> ExtractFilesEdited(List<object> steps)
    {
        var result = steps.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && (t?.ToString() == "edit" || t?.ToString() == "rename") &&
                        s.TryGetValue("status", out var st) && st?.ToString() == "done")
            .Select(s => (object)new
            {
                path = s.GetValueOrDefault("path"),
                action = s.GetValueOrDefault("editAction"),
                toPath = s.GetValueOrDefault("toPath"),
                linesAdded = s.GetValueOrDefault("linesAdded"),
                linesRemoved = s.GetValueOrDefault("linesRemoved"),
                preview = s.GetValueOrDefault("diffPreview")
            }).ToList();
        if (result.Count > 0) return result;
        foreach (var step in steps)
        {
            if (step is Dictionary<string, object?>) continue;
            try
            {
                var json = JsonSerializer.Serialize(step);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                var status = root.TryGetProperty("status", out var st) ? st.GetString() : "";
                if ((type == "edit" || type == "rename") && status == "done")
                    result.Add(new { path = root.TryGetProperty("path", out var p) ? p.GetString() : null, action = (string?)null, toPath = (string?)null, linesAdded = 0, linesRemoved = 0, preview = (string?)null });
            }
            catch { }
        }
        return result;
    }
}
