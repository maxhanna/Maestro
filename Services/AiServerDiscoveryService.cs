using System.Net;
using System.Text.Json;

namespace Weaver.Services;

/// <summary>
/// Detects local AI model servers and enumerates the models they have loaded.
///
/// The local-LLM ecosystem has two dominant API conventions:
///  • Ollama / Lemonade — <c>/api/tags</c> returns <c>{ models: [{ name, details }] }</c>
///  • OpenAI-compatible (llama.cpp, vLLM, LM Studio, Lemonade) — <c>/v1/models</c> returns
///    <c>{ data: [{ id }] }</c>
///
/// Many servers answer BOTH (Lemonade Server does). This service queries both endpoints,
/// merges results (deduplicating by normalized name), and returns a unified list so the
/// Settings UI can show a dropdown of models the user ACTUALLY has loaded — instead of a
/// free-text field where the default (<c>medgemma:4b</c>) may not match anything.
/// </summary>
public class AiServerDiscoveryService
{
    private readonly IHttpClientFactory _clientFactory;

    /// <summary>Common ports local AI servers listen on. Scanned in parallel during
    /// detection. The list covers the well-known defaults for every major local runtime so
    /// a user who just started a server gets auto-discovered without editing config.</summary>
    public static readonly int[] CommonPorts =
    [
        8080,    // llama.cpp server, Lemonade Server (default)
        11434,   // Ollama
        1234,    // LM Studio
        13305,   // Lemonade Server (alternate)
        8000,    // vLLM, generic FastAPI
        7860,    // Gradio / text-generation-webui
        5000,    // Ollama (alt), generic
        9997,    // llama.cpp server (alt)
    ];

    public AiServerDiscoveryService(IHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    /// <summary>One model discovered on a server.</summary>
    public record DiscoveredModel(string Id, string Format);

    /// <summary>A detected AI server with its available models.</summary>
    public record DetectedServer(string Url, string ServerType, List<DiscoveredModel> Models);

    /// <summary>
    /// Queries a specific server URL for its model list. Tries both the Ollama
    /// (<c>/api/tags</c>) and OpenAI (<c>/v1/models</c>) endpoints, merging and
    /// deduplicating results. Returns an empty list (not an error) when the server is
    /// unreachable or has no models — the caller decides what to show the user.
    /// </summary>
    public async Task<List<DiscoveredModel>> ListModelsAsync(string baseUrl, CancellationToken ct = default)
    {
        var url = baseUrl.TrimEnd('/');
        var models = new Dictionary<string, DiscoveredModel>(StringComparer.OrdinalIgnoreCase);

        // Try Ollama-format /api/tags first (most local servers implement this).
        try
        {
            var client = MakeClient(TimeSpan.FromSeconds(5));
            using var resp = await client.GetAsync(url + "/api/tags", ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("models", out var modelsEl) && modelsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in modelsEl.EnumerateArray())
                    {
                        var name = m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name))
                            models.TryAdd(name!, new DiscoveredModel(name!, "ollama"));
                    }
                }
            }
        }
        catch { /* server may not implement /api/tags — try OpenAI format next */ }

        // Try OpenAI-format /v1/models (llama.cpp, vLLM, LM Studio, Lemonade all answer this).
        try
        {
            var client = MakeClient(TimeSpan.FromSeconds(5));
            using var resp = await client.GetAsync(url + "/v1/models", ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in dataEl.EnumerateArray())
                    {
                        var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(id))
                            models.TryAdd(id!, new DiscoveredModel(id!, "openai"));
                    }
                }
            }
        }
        catch { /* unreachable or not OpenAI-compatible */ }

        return models.Values.ToList();
    }

    /// <summary>
    /// Scans common local ports for a running AI model server. For each port that responds,
    /// probes <c>/api/tags</c> and <c>/v1/models</c> to confirm it's an AI server (not just
    /// any HTTP server) and to collect its models. Returns only servers that actually have
    /// at least one model loaded — a server with zero models is not useful.
    ///
    /// The scan is parallel (all ports probed concurrently) with a short per-port timeout
    /// so the whole detection completes in ~1-2 seconds even when most ports are dead.
    /// </summary>
    public async Task<List<DetectedServer>> DetectServersAsync(CancellationToken ct = default)
    {
        var tasks = CommonPorts.Select(port => ProbePortAsync(port, ct));
        var results = await Task.WhenAll(tasks);
        return results
            .Where(s => s != null && s.Models.Count > 0)
            .Cast<DetectedServer>()
            .OrderBy(s => s.Url)
            .ToList();
    }

    private async Task<DetectedServer?> ProbePortAsync(int port, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}";
        // Quick TCP connect check — skip the HTTP probes entirely if nothing is listening,
        // so dead ports cost only the connect timeout (200ms), not a full HTTP timeout.
        if (!ServerLauncherService.IsPortFree(port))
        {
            var models = await ListModelsAsync(url, ct);
            if (models.Count > 0)
                return new DetectedServer(url, ClassifyServer(url, models), models);
        }
        return null;
    }

    /// <summary>Best-effort server-type label from the model format mix. A server answering
    /// <c>/api/tags</c> is Ollama-compatible; one answering only <c>/v1/models</c> is
    /// OpenAI-compatible; both means it's a multi-protocol server (Lemonade).</summary>
    private static string ClassifyServer(string url, List<DiscoveredModel> models)
    {
        var hasOllama = models.Any(m => m.Format == "ollama");
        var hasOpenai = models.Any(m => m.Format == "openai");
        if (hasOllama && hasOpenai) return "ollama+openai";
        if (hasOllama) return "ollama";
        if (hasOpenai) return "openai";
        return "unknown";
    }

    private HttpClient MakeClient(TimeSpan timeout)
    {
        var client = _clientFactory.CreateClient();
        client.Timeout = timeout;
        return client;
    }

    /// <summary>Result of a model swap attempt.</summary>
    public record SwapResult(bool Success, string? Error, string? LoadedModel);

    /// <summary>
    /// Swaps the loaded model on a local AI server. On servers that pin models in VRAM
    /// (Lemonade Server, and potentially others), a <c>/v1/chat/completions</c> request for
    /// a different model fails with HTTP 409 <c>slots_pinned_error</c>. This method unloads
    /// the currently-loaded model(s) and loads the requested one via the server's own
    /// load/unload API, so the user's model selection in Settings actually takes effect.
    ///
    /// The swap is best-effort and server-agnostic: servers that auto-load on request
    /// (Ollama, LM Studio) will simply return success without needing the unload step.
    /// Only servers that implement <c>/api/v1/load</c> + <c>/api/v1/unload</c> (Lemonade)
    /// actually perform the VRAM swap. The method works by:
    ///  1. Unloading ALL currently-loaded models (empty body → "all unloaded") — avoids
    ///     needing to know exactly which model is pinned.
    ///  2. Loading the requested model via <c>POST /api/v1/load</c>.
    ///  3. Verifying the load succeeded.
    /// </summary>
    public async Task<SwapResult> SwapModelAsync(string baseUrl, string modelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return new SwapResult(false, "No model ID specified.", null);

        var url = baseUrl.TrimEnd('/');
        var loadClient = MakeClient(TimeSpan.FromSeconds(120)); // loading a model into VRAM can take 10-30s

        // 1. Unload all currently-loaded models. The Lemonade unload endpoint accepts
        //    {"model":"<id>"} for a specific model, or an empty/null body to unload all.
        //    Unloading all avoids needing to detect which model is currently pinned —
        //    the "pinned" flag in /api/v1/models is a recipe config, not runtime state.
        try
        {
            var unloadBody = JsonSerializer.Serialize(new { model = (string?)null });
            var unloadContent = new StringContent(unloadBody, System.Text.Encoding.UTF8, "application/json");
            using var unloadResp = await loadClient.PostAsync(url + "/api/v1/unload", unloadContent, ct);
            // 404 "Model not loaded" is fine — nothing was loaded to begin with.
            if (!unloadResp.IsSuccessStatusCode && (int)unloadResp.StatusCode != 404)
            {
                var err = await unloadResp.Content.ReadAsStringAsync(ct);
                // Don't abort — some servers don't implement unload at all (Ollama auto-loads).
                // If load succeeds without unload, the swap still works.
            }
        }
        catch { /* server may not implement /api/v1/unload — try load anyway */ }

        // 2. Load the requested model.
        try
        {
            var loadBody = JsonSerializer.Serialize(new { model_name = modelId });
            var loadContent = new StringContent(loadBody, System.Text.Encoding.UTF8, "application/json");
            using var loadResp = await loadClient.PostAsync(url + "/api/v1/load", loadContent, ct);
            var respBody = await loadResp.Content.ReadAsStringAsync(ct);
            if (loadResp.IsSuccessStatusCode)
                return new SwapResult(true, null, modelId);
            // 404 on /api/v1/load means the server doesn't implement an explicit load API
            // (Ollama, LM Studio auto-load on first request). This is not an error — the
            // model will load on the next /v1/chat/completions call. Only non-404 failures
            // (400 model_not_found, 500 internal error) are real load failures.
            if ((int)loadResp.StatusCode == 404)
                return new SwapResult(true, null, modelId);
            return new SwapResult(false, $"Load failed: HTTP {(int)loadResp.StatusCode} — {respBody}", null);
        }
        catch
        {
            // Server doesn't implement /api/v1/load (Ollama/LM Studio auto-load on first
            // request). This is not an error for those servers — the model will load on
            // the next /v1/chat/completions call. Return success so the caller proceeds.
            return new SwapResult(true, null, modelId);
        }
    }

    /// <summary>
    /// Attempts a generation request to determine if the specified model is currently
    /// loaded and usable. Returns true when the server responds 200; false when it
    /// returns the Lemonade-specific 409 <c>slots_pinned_error</c> (model not loaded
    /// and pinned) or any other error. Used by the frontend to show the user whether
    /// their selected model is actually active before they run a benchmark.
    /// </summary>
    public async Task<bool> IsModelLoadedAsync(string baseUrl, string modelId, CancellationToken ct = default)
    {
        var url = baseUrl.TrimEnd('/');
        try
        {
            var client = MakeClient(TimeSpan.FromSeconds(30));
            var body = JsonSerializer.Serialize(new
            {
                model = modelId,
                messages = new[] { new { role = "user", content = "ping" } },
                stream = false,
                max_tokens = 1
            });
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(url + "/v1/chat/completions", content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
