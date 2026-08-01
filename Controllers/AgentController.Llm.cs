using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Weaver.Services;
namespace Weaver.Controllers;
partial class AgentController
{
    private const int StreamWindowChars = 400;
    private const int StreamChunkLen = 40;
    private const int StreamRepeatThreshold = 4;

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
                var baseUrl = (string.IsNullOrWhiteSpace(ep.url) ? cfg.llamaUrl : ep.url).TrimEnd('/');
                var model = string.IsNullOrWhiteSpace(ep.model) ? cfg.llamaModel : ep.model;
                return (baseUrl, model, string.IsNullOrWhiteSpace(ep.name) ? ep.url : ep.name);
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
        TimeSpan? requestTimeout = null, int? maxTokens = null)
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
        var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await CallLlmNonStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens);
    }
    private async Task<(string raw, AgentResponse? response, string? error)> CallLlmRawStreaming(
        string systemPrompt, string userMessage, bool emitSse, CancellationToken ct = default,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
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
        var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        return await CallLlmStreaming(client, baseUrl + "/v1/chat/completions", model, messages, linkedCts.Token, maxTokens, emitSse);
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
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = httpContent };
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            { var t2 = await resp.Content.ReadAsStringAsync(ct); return (t2, null, $"HTTP {resp.StatusCode}"); }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var reader = new StreamReader(stream);
            var sb = new StringBuilder();
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
                raw = AgentUtilities.ExtractFirstJsonObject(raw);
            }
            var parsed2 = ParseAgentResponse(raw);
            return (raw, parsed2, parsed2 == null ? "JSON parse failed" : null);
        }
        catch (TaskCanceledException) { return ("", null, "LLM request timed out"); }
        catch (Exception ex) { return ("", null, ex.Message); }
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
            var m = AgentUtilities.MethodDeclRegex.Match(newStr);
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
            var (raw, _, err) = await CallLlmRawStreaming(sysPrompt, userPrompt, emitSse, ct, requestTimeout: TimeSpan.FromSeconds(45), maxTokens: 500);
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var cleaned = AgentUtilities.ExtractFirstJsonObject(raw);
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
    /// Global hallucination detection — call on full LLM output (post-streaming or non-streaming).
    /// Returns an error string if hallucination is detected, null otherwise.
    /// Checks: wall-of-text (very low newline density), semantic repetition of long substrings.
    /// </summary>
    private static string? DetectHallucination(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 1000) return null;

        // 1. Wall-of-text check: very low newline density (fewer than 1 newline per 200 chars)
        //    Legitimate JSON/code has ~1 newline per 50-100 chars. Hallucinated rambling has almost none.
        var newlineCount = raw.Count(c => c == '\n');
        var newlineRatio = (double)newlineCount / raw.Length;
        if (raw.Length > 2000 && newlineRatio < 0.005) // fewer than 1 newline per 200 chars
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
        if (ratio < 0.005) // fewer than 1 newline per 200 chars
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
        TimeSpan? requestTimeout = null, int? maxTokens = null)
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
        var timeout = requestTimeout ?? TimeSpan.FromMinutes(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
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
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions") { Content = httpContent };
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            if (!resp.IsSuccessStatusCode)
            { var t2 = await resp.Content.ReadAsStringAsync(linkedCts.Token); return (t2, $"HTTP {resp.StatusCode}"); }
            var stream = await resp.Content.ReadAsStreamAsync(linkedCts.Token);
            var reader = new StreamReader(stream);
            var sb = new StringBuilder();
            while (true)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().WaitAsync(linkedCts.Token);
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
            return (raw, null);
        }
        catch (TaskCanceledException) { return ("", "LLM request timed out"); }
        catch (Exception ex) { return ("", ex.Message); }
        finally { try { progressCts.Cancel(); } catch { } }
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
}