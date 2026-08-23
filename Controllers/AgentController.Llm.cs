using System.Linq;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.RegularExpressions;
using Weaver.Services;
namespace Weaver.Controllers;
partial class AgentController
{
    private const int StreamWindowChars = 400;
    private const int StreamChunkLen = 40;
    private const int StreamRepeatThreshold = 4;

    /// <summary>
    /// Records the OUTCOME of a recovery retry (finish-this continuation, hint retry, or
    /// non-streaming transport retry) for the endpoint health tracker, and emits a 📊 metric
    /// log entry so users can see in the agent log whether their endpoints' recovery feature
    /// is actually working. Called once per retry after it completes. The counter mutation
    /// lives in EndpointHealthService; this wrapper adds the SSE/log surface.
    /// </summary>
    private async Task RecordRecoveryOutcomeAsync(string? baseUrl, bool recovered, string retryKind,
        bool emitSse = false, CancellationToken ct = default)
    {
        var counts = EndpointHealthService.RecordRecoveryOutcome(baseUrl, recovered);
        // The service increments a counter BEFORE returning, so the sum is always >= 1 for
        // a real endpoint and only 0 when the baseUrl normalized to empty — i.e. this is a
        // proxy for "the endpoint was tracked" (equivalent to the old key.Length > 0 check).
        if (counts.Recovered + counts.RecoveryFailed > 0)
        {
            await EmitLog(emitSse, "metric",
                $"📊 Recovery ({retryKind}): {(recovered ? "recovered ✓" : "still failed ✗")} — " +
                $"{counts.Recovered} recovered / {counts.RecoveryFailed} failed retries on this endpoint", ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "metric",
                $"📊 Recovery ({retryKind}): {(recovered ? "recovered ✓" : "still failed ✗")}", ct: ct);
        }
    }

    /// <summary>
    /// Editor:DisableLLMRetries — when true, ALL recovery retries are skipped: the
    /// non-streaming transport retry, the streaming hint retry, the prose retry, the
    /// finish-this continuation loop, AND the terminal/build transient-blip retry
    /// (RunTerminalCommandWithRetryAsync gates on this same flag). Failed calls/commands
    /// return their partial/error immediately instead. For users whose flaky endpoints
    /// make the retry delay worse than the failure itself (a 300ms pause + full re-stream
    /// per call adds up fast).
    /// </summary>
    private bool LlmRetriesDisabled() => LlmRetriesDisabled(_config);
    internal static bool LlmRetriesDisabled(IConfiguration? config)
    {
        try { return config?.GetValue<bool>("Editor:DisableLLMRetries") ?? false; }
        catch { return false; }
    }

    private string? _runBaseUrl;
    private string? _runModel;

    private async Task<string> GetLlamaBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_runBaseUrl)) return _runBaseUrl;
        var cfg = await _configFile.LoadConfigAsync();
        return (cfg.llamaUrl ?? "http://localhost:8080").TrimEnd('/');
    }
    private async Task<string> GetLlamaModel()
    {
        if (!string.IsNullOrWhiteSpace(_runModel)) return _runModel;
        var cfg = await _configFile.LoadConfigAsync();
        return cfg.llamaModel ?? "medgemma:4b";
    }

    /// <summary>
    /// Resolves the LLM endpoint a card asked for (AgentRequest.EndpointId) so this run talks to that
    /// endpoint instead of the default. Falls back to the default llamaUrl/llamaModel for unknown/empty ids.
    /// </summary>
    private async Task<(string baseUrl, string model, string name)> ResolveRunEndpointAsync(string? endpointId)
    {
        var cfg = await _configFile.LoadConfigAsync();
        var defaultBaseUrl = (cfg.llamaUrl ?? "http://localhost:8080").TrimEnd('/');
        var defaultModel = cfg.llamaModel ?? "medgemma:4b";
        if (!string.IsNullOrWhiteSpace(endpointId) && cfg.llamaEndpoints != null)
        {
            var ep = cfg.llamaEndpoints.FirstOrDefault(e => e.id == endpointId);
            if (ep != null)
            {
                var baseUrl = (string.IsNullOrWhiteSpace(ep.url) ? cfg.llamaUrl : ep.url)?.TrimEnd('/') ?? "";
                var model = (string.IsNullOrWhiteSpace(ep.model) ? cfg.llamaModel : ep.model) ?? "";
                return (baseUrl, model, string.IsNullOrWhiteSpace(ep.name) ? (ep.url ?? "Default") : ep.name);
            }
        }
        return (defaultBaseUrl, defaultModel, "Default");
    }

    /// <summary>
    /// Polls the llama.cpp server's /slots endpoint while an LLM call streams and
    /// forwards the same "progress" value the server prints to its console as SSE
    /// "progress" events (percent 0-100), so the frontend can show a real loading
    /// bar instead of a spinner. Falls back silently for backends without /slots
    /// (e.g. Ollama), in which case the poller simply stops.
    /// </summary>
    private async Task PollLlamaProgressAsync(string baseUrl, CancellationToken ct)
    {
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = TimeSpan.FromSeconds(3);
        double lastSent = -1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var resp = await client.GetAsync(baseUrl + "/slots", ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Backend has no /slots endpoint (e.g. Ollama) — stop polling.
                    break;
                }
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        double best = 0;
                        var processing = false;
                        foreach (var slot in doc.RootElement.EnumerateArray())
                        {
                            var st = slot.TryGetProperty("state", out var stEl) ? stEl.GetString() : "idle";
                            if (st == "processing" && slot.TryGetProperty("progress", out var prEl))
                            {
                                processing = true;
                                best = Math.Max(best, prEl.GetDouble());
                            }
                        }
                        var pct = (int)Math.Round(best * 100);
                        if (processing && pct != (int)lastSent)
                        {
                            lastSent = pct;
                            await SendSse(Response, "progress", new { progress = best, percent = pct }, ct);
                        }
                    }
                }
            }
            catch { /* transient error (timeout, server busy) — skip this tick */ }
            try { await Task.Delay(500, ct); } catch { break; }
        }
    }
    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRaw(
        string systemPrompt, string userMessage, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null, string? llmRoundLabel = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = await GetLlamaModel();
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _infiniteTimeout;
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userMessage  }
        };
        var timeout = requestTimeout ?? _infiniteTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var first = await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens);
        EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        // AUTO-SWAP: when the server returns 409 slots_pinned_error (Lemonade Server with a
        // different model pinned in VRAM), unload the pinned model, load the requested one,
        // and retry once. The swap takes 10-30s for large models — the user sees a log line.
        if (IsSlotsPinnedError(first.raw) && await TryAutoSwapModelAsync(baseUrl, model, false, ct))
        {
            using var swapTimeoutCts = new CancellationTokenSource(timeout);
            using var swapLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, swapTimeoutCts.Token);
            first = await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, swapLinkedCts.Token, maxTokens);
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        }
        // Non-streaming calls can't reuse partial output, but a one-shot retry still
        // recovers a transient transport blip (network drop, connection reset, premature
        // close, timeout) — the prompt is intact and the second attempt often lands.
        // A tiny delay lets a momentary blip clear before re-issuing. Skipped entirely
        // when Editor:DisableLLMRetries is set (retry delay hurts more than it helps).
        if (!LlmRetriesDisabled() && TransientFailureDetector.IsTransientTransportFailure(first.error))
        {
            try { await Task.Delay(300, ct); } catch { return first; }
            using var retryTimeoutCts = new CancellationTokenSource(timeout);
            using var retryLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, retryTimeoutCts.Token);
            first = await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, retryLinkedCts.Token, maxTokens);
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
            await RecordRecoveryOutcomeAsync(baseUrl, string.IsNullOrWhiteSpace(first.error), "non-streaming retry", ct: ct);
        }
        try { await RecordLlmRoundMetricsAsync(llmRoundLabel, systemPrompt, userMessage, first.raw, false, ct); }
        catch { /* metrics must never break the pipeline */ }
        return first;
    }
    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRawStreaming(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null, string? llmRoundLabel = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = await GetLlamaModel();
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _infiniteTimeout;
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userMessage  }
        };
        var timeout = requestTimeout ?? _infiniteTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var first = await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens, emitSse);
        EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        // AUTO-SWAP: same 409 slots_pinned_error handling as the non-streaming path — swap
        // the pinned model and retry once. Must run BEFORE the truncation/transport recovery
        // because a 409 is not a recoverable stream failure — it's a model-not-loaded error.
        if (IsSlotsPinnedError(first.raw) && await TryAutoSwapModelAsync(baseUrl, model, emitSse, ct))
        {
            using var swapTimeoutCts = new CancellationTokenSource(timeout);
            using var swapLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, swapTimeoutCts.Token);
            first = await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model, messages, swapLinkedCts.Token, maxTokens, emitSse);
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        }
        // A stream read error / truncated response can kill an otherwise-good response mid-run
        // (e.g. the planner had already produced the correct edit before the connection dropped).
        // Two distinct recoveries, chosen by WHY it failed:
        //   1. Max-token truncation → the requested content (e.g. a whole method in newString) is
        //      bigger than one response's token budget. Re-asking for the FULL response would just
        //      truncate again at the same point, so we run a "finish this" continuation loop that
        //      asks the model to output ONLY the missing tail and appends it until the JSON parses.
        //   2. Transport/stream drop → one retry with the partial as a continuation hint.
        // Editor:DisableLLMRetries skips ALL of this — the partial + error return as-is.
        if (TransientFailureDetector.IsRecoverableStreamFailure(first.raw, first.error) && LlmRetriesDisabled())
        {
            await EmitLog(emitSse, "warn",
                "⚠ LLM retries disabled (Editor:DisableLLMRetries) — failed call returned without recovery.",
                detail: RecoveryDetail(first.raw), ct: ct);
        }
        else if (TransientFailureDetector.IsRecoverableStreamFailure(first.raw, first.error))
        {
            string retryKind;
            if (IsMaxTokenTruncation(first.error))
            {
                retryKind = "finish-this";
                first = await FinishThisTruncatedJsonAsync(
                    client, baseUrl, model, systemPrompt, userMessage, first.raw,
                    linkedCts.Token, maxTokens, emitSse);
            }
            else
            {
                retryKind = "stream retry";
                await EmitLog(emitSse, "recovering",
                    string.Format(StreamRecoveryRetryMessage, first.raw.Length),
                    detail: RecoveryDetail(first.raw), ct: ct);
                using var retryTimeoutCts = new CancellationTokenSource(timeout);
                using var retryLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, retryTimeoutCts.Token);
                var hintMessages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = AppendPartialContinuationHint(userMessage, first.raw) }
                };
                first = await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model, hintMessages, retryLinkedCts.Token, maxTokens, emitSse: false);
            }
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
            await RecordRecoveryOutcomeAsync(baseUrl, string.IsNullOrWhiteSpace(first.error), retryKind, emitSse, ct);
        }
        try { await RecordLlmRoundMetricsAsync(llmRoundLabel, systemPrompt, userMessage, first.raw, emitSse, ct); }
        catch { /* metrics must never break the pipeline */ }
        return first;
    }
    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmNonStreaming(
      HttpClient client, string target, string model, object messages,
      CancellationToken ct = default, int? maxTokens = null)
    {
        var cfg6 = await LoadConfigAsync();
        var mt = maxTokens ?? cfg6.defaultMaxTokens;
        var reqBody = new
        {
            model,
            messages,
            stream = false,
            temperature = 0.05,
            max_tokens = mt,
            repeat_penalty = 1.3,
            repeat_last_n = 256
        };
        var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
        try
        {
            var resp = await client.PostAsync(target, httpContent, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            var llmContent = ExtractLlmContent(respText);
            if (string.IsNullOrWhiteSpace(llmContent)) return (respText, null, "Empty LLM response");
            var hallError = DetectHallucination(llmContent);
            if (hallError != null) return (llmContent, null, hallError);
            var parsed = ParseAgentResponse(llmContent);
            return (llmContent, parsed, parsed == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException) { return ("", null, "LLM request timed out"); }
        catch (Exception ex) { return ("", null, ex.Message); }
    }
    private async Task<(string raw, AgentResponse? parsed, string? error)> CallLlmStreaming(
    HttpClient client, string target, string model, object messages,
    CancellationToken ct = default, int? maxTokens = null, bool emitSse = false)
    {
        var cfg7 = await LoadConfigAsync();
        var mt = maxTokens ?? cfg7.defaultMaxTokens;
        var reqBody = new
        {
            model,
            messages,
            stream = true,
            temperature = 0.05,
            max_tokens = mt,
            repeat_penalty = 1.3,
            repeat_last_n = 256
        };
        var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
        // Derive the llama base URL from the target endpoint and start the
        // /slots progress poller so the frontend sees real loading progress.
        var llmBaseUrl = target.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? target.Substring(0, target.Length - "/v1/chat/completions".Length)
            : target;
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (emitSse) _ = PollLlamaProgressAsync(llmBaseUrl, progressCts.Token);
        var sb = new StringBuilder();
        var truncatedByTokenLimit = false;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = httpContent };
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            { var t2 = await resp.Content.ReadAsStringAsync(ct); return (t2, null, $"HTTP {resp.StatusCode}"); }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var reader = new StreamReader(stream);
            using var repeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                repeatCts.Token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().WaitAsync(repeatCts.Token);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("[DONE]")) break;
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..].Trim();
                if (string.IsNullOrWhiteSpace(data)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                            finishReason.ValueKind == JsonValueKind.String &&
                            string.Equals(finishReason.GetString(), "length", StringComparison.OrdinalIgnoreCase))
                            truncatedByTokenLimit = true;
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                if (emitSse) await SendSse(Response, "token", new { token }, ct);
                                sb.Append(token);
                                // Check for repetition loop (chunk-level)
                                if (sb.Length >= StreamChunkLen * (StreamRepeatThreshold + 1) &&
                                    IsRepeatingLoop(sb, StreamWindowChars, StreamChunkLen, StreamRepeatThreshold))
                                {
                                    try { resp.Dispose(); } catch { }
                                    var truncated = sb.ToString();
                                    return (truncated, null,
                                        $"Repetition loop detected after {truncated.Length} chars — aborted early. " +
                                        "The model got stuck re-emitting the same block. Retry with a smaller, more targeted anchor.");
                                }
                                // Check for wall-of-text hallucination (every ~500 chars beyond threshold)
                                if (sb.Length % 500 < 10) // check roughly every 500 chars
                                {
                                    var wallError = CheckStreamingHallucination(sb);
                                    if (wallError != null)
                                    {
                                        try { resp.Dispose(); } catch { }
                                        return (sb.ToString(), null, wallError);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            var raw = sb.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return ("", null, "Empty LLM response");
            // Post-hoc hallucination check (safety net)
            var hallError2 = DetectHallucination(raw);
            if (hallError2 != null) return (raw, null, hallError2);
            var braceCount = 0;
            var topLevelOpens = 0;
            foreach (var c in raw)
            {
                if (c == '{') { braceCount++; if (braceCount == 1) topLevelOpens++; }
                else if (c == '}') braceCount--;
            }
            if (topLevelOpens > 1)
            {
                // Model emitted multiple JSON objects — extract only the first
                raw = AgentJsonUtilities.ExtractFirstJsonObject(raw);
            }
            var parsed2 = ParseAgentResponse(raw);
            // Only treat a max-token cut as unrecoverable when the JSON failed to parse — a
            // truncated-but-parseable response is returned as-is to avoid spurious retries.
            if (parsed2 == null && truncatedByTokenLimit)
                return (raw, null, "Response truncated at max_tokens — partial kept for recovery hint.");
            return (raw, parsed2, parsed2 == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException) { return (sb.ToString(), null, "LLM request timed out"); }
        catch (Exception ex) { return (sb.ToString(), null, ex.Message); }
        finally { try { progressCts.Cancel(); } catch { } }
    }
    private static string? ExtractNewlyAddedMethodName(string? stepChange, string? newStr)
    {
        if (string.IsNullOrWhiteSpace(stepChange)) return null;
        var isAddition = Regex.IsMatch(stepChange,
            @"\b(add|create|insert|implement|define)\b.{0,40}\b(method|function)\b", RegexOptions.IgnoreCase);
        if (!isAddition) return null;
        if (!string.IsNullOrWhiteSpace(newStr))
        {
            var m = AgentMethodInventory.MethodDeclRegex.Match(newStr);
            if (m.Success) return m.Groups[1].Value;
            var tsMatch = Regex.Match(newStr,
                @"\b(?:private|public|protected)?\s*(?:async\s+)?([A-Za-z_]\w*)\s*\([^)]*\)\s*(?::\s*[^{]+)?\s*\{");
            if (tsMatch.Success) return tsMatch.Groups[1].Value;
        }
        var dm = Regex.Match(stepChange, @"(?:method|function)\s+'?([A-Za-z_]\w*)'?", RegexOptions.IgnoreCase);
        if (dm.Success) return dm.Groups[1].Value;
        dm = Regex.Match(stepChange, @"\b([A-Za-z_]\w*)\s*\(\s*\)", RegexOptions.IgnoreCase);
        return dm.Success ? dm.Groups[1].Value : null;
    }
    /// <summary>
    /// Deterministic (non-LLM) check: does a call to this newly-added method exist ANYWHERE
    /// in the project outside its own declaration? No LLM opinion needed — grep either finds
    /// a call site or it doesn't. This is what catches "added but never wired up," which an
    /// LLM step-verifier reliably misses because it's judging the step in isolation.
    /// </summary>
    private async Task<(bool wired, string? reason)> CheckNewMethodIsWiredUpAsync(
        string methodName, string relPath, string projectRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(methodName) || methodName.Length < 3) return (true, null);
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var searchPatterns = ext switch
        {
            ".ts" or ".tsx" => new[] { "*.ts", "*.tsx", "*.html" },
            ".cs" => new[] { "*.cs" },
            ".js" or ".jsx" => new[] { "*.js", "*.jsx", "*.html" },
            _ => new[] { "*" + ext }
        };
        var callPattern = new Regex($@"\b{Regex.Escape(methodName)}\s*\(", RegexOptions.Compiled);
        var declLinePattern = new Regex(
            $@"\b(?:private|public|protected|internal|static|async|get|set)\b[^\n]*\b{Regex.Escape(methodName)}\s*\(",
            RegexOptions.Compiled);
        foreach (var pattern in searchPatterns)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(projectRoot, pattern, SearchOption.AllDirectories)
                    .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                             && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\") && !f.Contains("\\dist\\"));
            }
            catch { continue; }
            foreach (var file in files)
            {
                string content;
                try { content = await System.IO.File.ReadAllTextAsync(file, Encoding.UTF8, ct); }
                catch { continue; }
                foreach (Match m in callPattern.Matches(content))
                {
                    var lineStart = content.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                    var lineEndIdx = content.IndexOf('\n', m.Index);
                    var line = content[lineStart..(lineEndIdx < 0 ? content.Length : lineEndIdx)];
                    if (declLinePattern.IsMatch(line)) continue; // this hit is the declaration itself
                    return (true, null); // found a real call site
                }
            }
        }
        return (false,
            $"Method '{methodName}' was just added to {relPath} but has ZERO call sites anywhere else in the " +
            "project — only its own declaration exists. It needs to be wired up (called from wherever the " +
            "feature it implements is supposed to run).");
    }
    private async Task<string> RunCausalReasoningAsync(string taskDesc, string relPath, string fileContent, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileContent)) return string.Empty;
        var classifyPrompt = $"Is the following task a bug fix or a feature/enhancement? Reply with exactly one word: BUG or FEATURE.\n\nTask: {taskDesc}";
        var (classification, _, _) = await CallLlmRawStreaming(
            "You classify tasks as BUG or FEATURE. Reply with exactly one word.",
            classifyPrompt, false, ct, requestTimeout: _infiniteTimeout, maxTokens: 10);
        await EmitLog(emitSse, "info", $"Causal reasoning classification: '{classification?.Trim()}' for task: {taskDesc}", ct: ct);
        if (string.IsNullOrWhiteSpace(classification) || !classification.Trim().Equals("BUG", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var sysPrompt = "You are an expert software debugger. " +
                        "Given a bug report and the full file content, trace the execution flow to identify the ROOT CAUSE. " +
                        "Small details matter: check if callbacks are missed, state isn't updated, or variables are out of sync. " +
                        "Do NOT write the fix. Output ONLY JSON: " +
                        "{\"rootCause\": \"detailed explanation\", \"affectedMethods\": [\"method1\", \"method2\"]}";
        var userPrompt = $"### BUG REPORT / TASK ###\n{taskDesc}\n\n" +
                         $"### FILE: {relPath} ###\n" +
                         $"```\n{fileContent}\n```\n\n" +
                         "Trace the logic. What is the exact root cause of the issue, and which methods are affected?";
        try
        {
            var (raw, _, err) = await CallLlmRawStreaming(sysPrompt, userPrompt, emitSse, ct, requestTimeout: _infiniteTimeout, maxTokens: 500);
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var cleaned = AgentJsonUtilities.ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var rootCause = doc.RootElement.TryGetProperty("rootCause", out var rc) ? rc.GetString() : "Failed to parse root cause.";
            var affected = new List<string>();
            if (doc.RootElement.TryGetProperty("affectedMethods", out var am) && am.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in am.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) affected.Add(item.GetString() ?? "");
            }
            var sb = new StringBuilder();
            sb.AppendLine("### ⚠️ CAUSAL REASONING ANALYSIS (CRITICAL — READ BEFORE EDITING) ###");
            sb.AppendLine($"Root Cause Identified: {rootCause}");
            sb.AppendLine($"Affected Methods: {string.Join(", ", affected)}");
            sb.AppendLine("Your edit MUST address the root cause above and ensure the affected methods do not break.");
            sb.AppendLine();
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
    /// <summary>
    /// Wall-of-text floor: fewer than 1 newline per 1000 chars. Dense single-paragraph prose
    /// from smaller models legitimately lands at ~1 newline per 200-500 chars, so anything
    /// above this floor is normal prose — only genuinely break-free walls trip the check.
    /// </summary>
    private const double WallOfTextMinNewlineRatio = 0.001;

    /// <summary>
    /// Global hallucination detection — call on full LLM output (post-streaming or non-streaming).
    /// Returns an error string if hallucination is detected, null otherwise.
    /// Checks: wall-of-text (very low newline density), semantic repetition of long substrings.
    /// </summary>
    private static string? DetectHallucination(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 1000) return null;

        // 1. Wall-of-text check: very low newline density. Deliberately EXTREME
        //    (fewer than 1 newline per 1000 chars) — dense single-paragraph prose from
        //    smaller models legitimately lands at ~1 newline per 200-500 chars, and
        //    treating that band as hallucination kept aborting useful pre-plan reasoning
        //    (e.g. "2249 chars with 8 line breaks" on a web-search task). Real stuck-ramble
        //    output runs thousands of chars with near-zero breaks.
        var newlineCount = raw.Count(c => c == '\n');
        var newlineRatio = (double)newlineCount / raw.Length;
        if (raw.Length > 2000 && newlineRatio < WallOfTextMinNewlineRatio)
        {
            return $"Hallucination (wall of text): {raw.Length} chars with {newlineCount} line breaks " +
                   $"(ratio {newlineRatio:F4}). The model is rambling without structure. Output truncated.";
        }

        // 2. Semantic repetition: same 120+ char substring appearing 3+ times (not just chunk repetition)
        if (raw.Length > 800)
        {
            const int subLen = 120;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i <= raw.Length - subLen; i += 40) // step by 40 for performance
            {
                var sub = raw.Substring(i, subLen);
                var trimmed = sub.Trim();
                if (trimmed.Length < 60) continue; // skip near-empty/short substrings
                seen.TryGetValue(trimmed, out var count);
                count++;
                if (count >= 3)
                    return $"Hallucination (semantic repetition): same {trimmed.Length}-char block repeated {count}+ times. " +
                           "The model is stuck in a repetition loop.";
                seen[trimmed] = count;
            }
        }

        return null;
    }

    /// <summary>
    /// Real-time newline-density check for streaming output.
    /// Returns an error if the model has produced a large output with almost no line breaks.
    /// </summary>
    private static string? CheckStreamingHallucination(StringBuilder sb)
    {
        var len = sb.Length;
        if (len < 2500) return null;

        // Check full accumulated text for newline density
        var newlineCount = 0;
        for (var i = 0; i < len; i++)
            if (sb[i] == '\n') newlineCount++;

        var ratio = (double)newlineCount / len;
        if (ratio < WallOfTextMinNewlineRatio) // extreme, not just dense prose
        {
            return $"Hallucination (wall of text): {len} chars with {newlineCount} line breaks (ratio {ratio:F4}) — aborted early.";
        }

        return null;
    }

    private static bool IsRepeatingLoop(StringBuilder sb, int windowChars, int chunkLen, int repeatThreshold)
    {
        var len = sb.Length;
        var start = Math.Max(0, len - windowChars);
        var window = sb.ToString(start, len - start);
        if (window.Length < chunkLen * repeatThreshold) return false;
        var tail = window[^chunkLen..];
        if (string.IsNullOrWhiteSpace(tail.Trim())) return false;
        var pos = window.Length - chunkLen;
        var consecutive = 1;
        pos -= chunkLen;
        while (pos >= 0)
        {
            var candidate = window.Substring(pos, chunkLen);
            if (candidate == tail)
            {
                consecutive++;
                if (consecutive >= repeatThreshold) return true;
                pos -= chunkLen;
            }
            else break;
        }
        return false;
    }
    private async Task<(string raw, string? error)> CallLlmRawText(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null, bool appendTruncationMarker = false,
        string? llmRoundLabel = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = await GetLlamaModel();
        var timeout = requestTimeout ?? _infiniteTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var first = await CallLlmRawTextOnce(systemPrompt, userMessage, emitSse, linkedCts.Token, maxTokens, appendTruncationMarker);
        EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        // AUTO-SWAP: same 409 slots_pinned_error handling as the JSON paths — swap the pinned
        // model and retry once before attempting transport-failure recovery. Must run first
        // because a 409 is a model-not-loaded error, not a recoverable stream failure.
        if (IsSlotsPinnedError(first.raw) && await TryAutoSwapModelAsync(baseUrl, model, emitSse, ct))
        {
            using var swapTimeoutCts = new CancellationTokenSource(timeout);
            using var swapLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, swapTimeoutCts.Token);
            first = await CallLlmRawTextOnce(systemPrompt, userMessage, emitSse, swapLinkedCts.Token, maxTokens, appendTruncationMarker);
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
        }
        // Same recovery as CallLlmRawStreaming: a dropped connection or max-token cut must
        // not discard a good partial response — retry once with the partial as a hint.
        // Editor:DisableLLMRetries skips this — the partial + error return as-is.
        if (TransientFailureDetector.IsRecoverableStreamFailure(first.raw, first.error) && LlmRetriesDisabled())
        {
            await EmitLog(emitSse, "warn",
                "⚠ LLM retries disabled (Editor:DisableLLMRetries) — failed call returned without recovery.",
                detail: RecoveryDetail(first.raw), ct: ct);
        }
        else if (!LlmRetriesDisabled() && TransientFailureDetector.IsRecoverableStreamFailure(first.raw, first.error))
        {
            await EmitLog(emitSse, "recovering",
                string.Format(StreamRecoveryRetryMessage, first.raw.Length),
                detail: RecoveryDetail(first.raw), ct: ct);
            using var retryTimeoutCts = new CancellationTokenSource(timeout);
            using var retryLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, retryTimeoutCts.Token);
            first = await CallLlmRawTextOnce(systemPrompt, AppendPartialContinuationHint(userMessage, first.raw), emitSse: false, retryLinkedCts.Token, maxTokens, appendTruncationMarker);
            EndpointHealthService.RecordCall(baseUrl, first.raw, first.error);
            await RecordRecoveryOutcomeAsync(baseUrl, string.IsNullOrWhiteSpace(first.error), "prose retry", emitSse, ct);
        }
        try { await RecordLlmRoundMetricsAsync(llmRoundLabel, systemPrompt, userMessage, first.raw, emitSse, ct); }
        catch { /* metrics must never break the pipeline */ }
        return first;
    }
    private async Task<(string raw, string? error)> CallLlmRawTextOnce(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct, int? maxTokens = null,
        bool appendTruncationMarker = false)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = await GetLlamaModel();
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _infiniteTimeout;
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userMessage  }
        };
        var cfg = await LoadConfigAsync();
        var mt = maxTokens ?? cfg.defaultMaxTokens;
        var reqBody = new
        {
            model,
            messages,
            stream = true,
            temperature = 0.0,
            max_tokens = mt,
            repeat_penalty = 1.3,
            repeat_last_n = 256
        };
        var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (emitSse) _ = PollLlamaProgressAsync(baseUrl, progressCts.Token);
        var sb = new StringBuilder();
        var truncatedByTokenLimit = false;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions") { Content = httpContent };
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            { var t2 = await resp.Content.ReadAsStringAsync(ct); return (t2, $"HTTP {resp.StatusCode}"); }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var reader = new StreamReader(stream);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().WaitAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("[DONE]")) break;
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..].Trim();
                if (string.IsNullOrWhiteSpace(data)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        // Mirror CallLlmStreaming: a finish_reason of "length" means max_tokens
                        // cut the response — without this, a budget-capped cut is silent.
                        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                            finishReason.ValueKind == JsonValueKind.String &&
                            string.Equals(finishReason.GetString(), "length", StringComparison.OrdinalIgnoreCase))
                            truncatedByTokenLimit = true;
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                if (emitSse) await SendSse(Response, "token", new { token }, ct);
                                sb.Append(token);
                                // Check for repetition loop
                                if (sb.Length >= StreamChunkLen * (StreamRepeatThreshold + 1) &&
                                    IsRepeatingLoop(sb, StreamWindowChars, StreamChunkLen, StreamRepeatThreshold))
                                {
                                    try { resp.Dispose(); } catch { }
                                    return (sb.ToString(), "Repetition loop detected — aborted early. The model got stuck re-emitting the same block.");
                                }
                                // Check for wall-of-text hallucination
                                if (sb.Length % 500 < 10)
                                {
                                    var wallError = CheckStreamingHallucination(sb);
                                    if (wallError != null)
                                    {
                                        try { resp.Dispose(); } catch { }
                                        return (sb.ToString(), wallError);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            var raw = sb.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return ("", "Empty LLM response");
            var hallError = DetectHallucination(raw);
            if (hallError != null) return (raw, hallError);
            // NOTE: max_tokens truncation on the PROSE path is intentionally NOT an error —
            // pre-plan thinking and compaction are budget-capped and their partial output is
            // still usable reasoning (the partial is returned as-is, no retry). Only the JSON
            // path treats a truncated response as an error, because an unparseable step/edit
            // is genuinely unrecoverable. Callers that WANT the cut visible (pre-plan reasoning
            // streamed to the panel) opt in and get an explicit marker instead of a silent
            // mid-sentence stop that looks like a transport bug.
            if (truncatedByTokenLimit && appendTruncationMarker)
                return (raw + "\n\n…[reasoning truncated — hit the per-response token budget]…", null);
            return (raw, null);
        }
        catch (TaskCanceledException) { return (sb.ToString(), "LLM request timed out"); }
        catch (Exception ex) { return (sb.ToString(), ex.Message); }
        finally { try { progressCts.Cancel(); } catch { } }
    }
    private const string StreamRecoveryRetryMessage =
        "⚠ Stream interrupted — retrying the same call once with the partial response ({0} chars) as a continuation hint.";

    /// <summary>
    /// True when a failed streaming LLM call should be retried once with its partial output
    /// as a continuation hint. Only genuine transport/stream failures or max-token truncation
    /// qualify — a substantive partial response was received but the call did not complete.
    /// Pure semantic failures (JSON parse, hallucination, repetition loops, empty) are NOT
    /// recoverable by re-running and must flow through their existing retry/rejection paths.
    /// </summary>
    private static string? RecoveryDetail(string? partial)
        => string.IsNullOrWhiteSpace(partial)
            ? null
            : (partial.Length > 600 ? partial[..600] + "\n…(partial preview)…" : partial);

    /// <summary>
    /// Builds the user message for the one-shot retry: the original prompt plus the partial
    /// response the model already produced, framed as a continuation task so the retry keeps
    /// the good work (e.g. the correct refactor that was streamed before the connection died)
    /// instead of regenerating from scratch. Capped to keep the retry context sane.
    /// </summary>
    private static string AppendPartialContinuationHint(string userMessage, string partial)
    {
        // Keep the HEAD (structure/context) and the TAIL (the exact continuation point) of a
        // long partial; dropping the middle is safe, dropping the end would orphan the retry.
        var cap = partial;
        if (partial.Length > 16000)
            cap = partial[..12000] + "\n…(partial truncated, middle omitted)…\n" + partial[^4000..];
        return userMessage + "\n\n" +
               "### YOUR PREVIOUS RESPONSE WAS INTERRUPTED BY A STREAM ERROR ###\n" +
               "Your previous attempt produced the partial response below but the connection dropped before it " +
               "finished. The partial work is good — CONTINUE from exactly where it left off and output the " +
               "COMPLETE response to the original request. Preserve everything already written below; finish any " +
               "truncated JSON (close braces/brackets, complete field values) and return the FULL valid response.\n" +
               "\nPARTIAL RESPONSE (already produced — continue from this point):\n```\n" +
               cap + "\n```\n";
    }

    /// <summary>
    /// True when a streaming failure was caused by hitting the max_tokens output cap — the
    /// requested edit is bigger than one response can hold, NOT a network blip. This is the
    /// case that needs the "finish this" continuation loop (re-asking for the full response
    /// would just truncate again at the same point).
    /// </summary>
    private static bool IsMaxTokenTruncation(string? error)
        => !string.IsNullOrWhiteSpace(error) &&
           error.Contains("truncated at max_tokens", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the accumulated response text is now a structurally complete JSON object.</summary>
    private static bool LooksLikeCompleteJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.StartsWith("```"))
        {
            var m = Regex.Match(t, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) t = m.Groups[1].Value.Trim();
        }
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        try
        {
            using var doc = JsonDocument.Parse(t[start..(end + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    /// <summary>
    /// Builds the prompt for a "finish this" continuation pass. Unlike the transport-retry
    /// hint (which asks for the FULL response), this asks the model to output ONLY the missing
    /// tail that continues the partial EXACTLY where it stopped, so the appended result grows
    /// the response instead of regenerating it (which would truncate again at the same size).
    /// The ORIGINAL user message is kept in full at the front so the model still has the task
    /// context (file, change required) it needs to finish the edit correctly. The last ~160
    /// chars of the partial are given as an explicit continuation anchor so the model can see
    /// the exact character it must follow.
    /// </summary>
    private static string BuildFinishThisPrompt(string userMessage, string partial)
    {
        // The tail is what matters (the exact continuation point); the head just gives context.
        var cap = partial;
        if (partial.Length > 12000)
            cap = partial[..8000] + "\n…(partial truncated, middle omitted)…\n" + partial[^4000..];
        var anchor = partial.Length > 160 ? partial[^160..] : partial;
        return userMessage + "\n\n" +
               "### FINISH THIS OUTPUT ###\n" +
               "Your previous response was cut off because it exceeded the token limit. The partial below is " +
               "GOOD WORK and must be preserved — do NOT restart, do NOT repeat it, do NOT summarize it.\n" +
               "Output ONLY the REMAINING characters that continue the partial from the exact point it stopped, " +
               "so that when the original partial is followed by your output, the result is the COMPLETE valid " +
               "JSON response to the ORIGINAL request above. Continue mid-string exactly as the model was writing " +
               "it (e.g. finish the truncated method body, then close any open quotes/braces/brackets).\n" +
               "Your output will be APPENDED verbatim to the partial — it must start with the very next character.\n" +
               "Do NOT emit any preamble, explanation, markdown fences, or JSON keys/objects that the partial " +
               "already contains. Output ONLY the continuation text.\n\n" +
               "### EXACT CONTINUATION POINT (the partial ends right after this) ###\n" +
               "```\n" + anchor + "\n```\n" +
               "### PARTIAL OUTPUT (already produced — continue after its last character) ###\n" +
               "```\n" + cap + "\n```";
    }

    /// <summary>
    /// Stitches a continuation chunk onto the partial, defensively trimming any leading overlap
    /// that repeats the continuation-point tail (models sometimes re-emit the anchor). Finds
    /// the LONGEST suffix of the accumulated text (20..160 chars) that the chunk starts with
    /// and strips it, so a model that re-emitted part of the method declaration doesn't create
    /// a duplicated block.
    /// </summary>
    private static string StitchContinuation(string accumulated, string chunk, int maxOverlapChars = 160)
    {
        var c = (chunk ?? "").TrimStart();
        if (string.IsNullOrWhiteSpace(c)) return accumulated;
        var maxOverlap = Math.Min(maxOverlapChars, Math.Min(accumulated.Length, c.Length));
        for (var len = maxOverlap; len >= 20; len--)
        {
            var tail = accumulated[^len..];
            if (c.StartsWith(tail, StringComparison.Ordinal))
            {
                c = c[len..];
                break;
            }
        }
        return accumulated + c;
    }

    /// <summary>
    /// "Finish this" continuation loop: when a response is truncated by max_tokens (the edit is
    /// bigger than one response), run up to 5 passes that each ask the model to output ONLY the
    /// missing tail, stitch it onto the partial, and stop as soon as the accumulated text parses
    /// as complete JSON. This lets a step proposal carrying a whole method survive a budget that
    /// is smaller than the final payload. Passes are bounded so a stuck model can't loop forever.
    /// </summary>
    private async Task<(string raw, AgentResponse? parsed, string? error)> FinishThisTruncatedJsonAsync(
        HttpClient client, string baseUrl, string model, string systemPrompt, string userMessage,
        string partial, CancellationToken ct, int? maxTokens = null, bool emitSse = false)
    {
        const int maxPasses = 5;
        var accumulated = partial;
        // If the partial already parses as complete JSON (e.g. the object finished and only
        // trailing prose got cut), don't burn a continuation pass — hand it back as-is.
        if (LooksLikeCompleteJson(accumulated))
            return (accumulated, null, null);
        for (var pass = 0; pass < maxPasses; pass++)
        {
            var finishMessages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = BuildFinishThisPrompt(userMessage, accumulated) }
            };
            var cont = await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model,
                finishMessages, ct, maxTokens, emitSse: false);
            if (string.IsNullOrWhiteSpace(cont.raw))
            {
                await EmitLog(emitSse, "recovering",
                    $"✂️ Finish-this pass {pass + 1} returned empty ({cont.error}) — stopping", ct: ct);
                break;
            }
            var before = accumulated.Length;
            accumulated = StitchContinuation(accumulated, cont.raw);
            await EmitLog(emitSse, "recovering",
                $"✂️ Edit exceeded token budget — finish-this pass {pass + 1} appended {accumulated.Length - before} chars (total {accumulated.Length})", ct: ct);
            if (LooksLikeCompleteJson(accumulated))
            {
                // The finished text parses — but leave the caller to decide semantics; the raw
                // is returned whole with a null error so the step proposal flow proceeds normally.
                await EmitLog(emitSse, "recovering",
                    $"✂️ Finished truncated response after {pass + 1} continuation pass(es)", ct: ct);
                return (accumulated, null, null);
            }
            // If a pass added almost nothing, the model is stuck — don't burn more passes.
            if (accumulated.Length - before < 20) break;
        }
        return (accumulated, null,
            $"Response truncated at max_tokens — {maxPasses} finish-this passes could not complete it (final {accumulated.Length} chars)");
    }

    private static string ExtractLlmContent(string respText)
    {
        try
        {
            using var doc = JsonDocument.Parse(respText);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                    return content.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }
    private static AgentResponse? ParseAgentResponse(string raw)
    {
        var jsonStr = raw.Trim();
        if (jsonStr.StartsWith("```")) { var m = Regex.Match(jsonStr, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) jsonStr = m.Groups[1].Value.Trim(); }
        var start = jsonStr.IndexOf('{'); var end = jsonStr.LastIndexOf('}');
        if (start >= 0 && end > start) jsonStr = jsonStr[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var steps = JsonSerializer.Deserialize<List<AgentStep>>(jsonStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (steps?.Count > 0) return new AgentResponse { Steps = steps, Summary = "Parsed array" };
            }
            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                var steps = JsonSerializer.Deserialize<List<AgentStep>>(stepsEl.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (steps?.Count > 0)
                {
                    var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "";
                    var summary = root.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
                    var complete = root.TryGetProperty("complete", out var cp) && cp.ValueKind == JsonValueKind.True;
                    return new AgentResponse { Thinking = thinking, Summary = summary, Complete = complete, Steps = steps };
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Detects the Lemonade-specific "slots_pinned_error" in an LLM response body.
    /// The server returns HTTP 409 with <c>{"error":{"code":"slots_pinned_error",...}}</c>
    /// when the requested model is not loaded in VRAM. The raw body is in
    /// <c>first.raw</c> (non-streaming) or <c>first.raw</c> (streaming) — both paths return
    /// the error JSON body even though the error string differs ("Empty LLM response" vs
    /// "HTTP Conflict").</summary>
    private static bool IsSlotsPinnedError(string raw) =>
        !string.IsNullOrEmpty(raw) && raw.Contains("slots_pinned_error", StringComparison.OrdinalIgnoreCase);

    /// <summary>Attempts to swap the loaded model on the server when a 409
    /// slots_pinned_error is detected. Unloads all pinned models and loads the requested
    /// one via the server's /api/v1/load + /api/v1/unload API (Lemonade Server). Returns
    /// true when the swap succeeded and the caller should retry the LLM call. Emits a log
    /// line so the user sees what happened. Called once per LLM round — never retries the
    /// swap itself.</summary>
    private async Task<bool> TryAutoSwapModelAsync(string baseUrl, string model, bool emitSse, CancellationToken ct)
    {
        if (_aiDiscovery == null) return false;
        await EmitLog(emitSse, "warn",
            $"⚠ Model '{model}' not loaded — swapping (unloading pinned model, loading '{model}')…", ct: ct);
        try
        {
            var result = await _aiDiscovery.SwapModelAsync(baseUrl, model, ct);
            if (result.Success)
            {
                await EmitLog(emitSse, "info", $"✓ Model '{model}' loaded successfully.", ct: ct);
                return true;
            }
            await EmitLog(emitSse, "error", $"✗ Model swap failed: {result.Error}", ct: ct);
        }
        catch (Exception ex)
        {
            await EmitLog(emitSse, "error", $"✗ Model swap error: {ex.Message}", ct: ct);
        }
        return false;
    }
}