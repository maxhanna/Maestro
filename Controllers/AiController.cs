using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using Weaver.Services;
namespace Weaver.Controllers;
[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly IConfiguration _config;
    private readonly ConfigFileService _configFile;
    private readonly AiServerDiscoveryService _discovery;
    public AiController(IHttpClientFactory cf, IConfiguration config, ConfigFileService configFile, AiServerDiscoveryService discovery)
    {
        _clientFactory = cf;
        _config = config;
        _configFile = configFile;
        _discovery = discovery;
    }
    private async Task<string> GetBaseURL()
    {
        var cfg = await _configFile.LoadConfigAsync();
        return string.IsNullOrWhiteSpace(cfg.llamaUrl)
            ? (_config.GetValue<string>("Ai:BaseUrl") ?? "http://localhost:8080")
            : cfg.llamaUrl;
    }
    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] JsonElement payload)
    {
        string baseUrl = await GetBaseURL();
        var target = baseUrl.TrimEnd('/') + "/v1/chat/completions";
        var client = _clientFactory.CreateClient("llama");
        // Determine model: prefer payload.model, then config file, then fallback
        var cfgModel = await _configFile.LoadConfigAsync();
        string model = cfgModel.llamaModel ?? "medgemma:4b";
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
        {
            var m = modelProp.GetString();
            if (!string.IsNullOrWhiteSpace(m)) model = m!;
        }
        try
        {
            string contentJson;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("messages", out var messagesProp))
            {
                // Use provided messages but ensure model is present
                var messagesRaw = messagesProp.GetRawText();
                var toolsRaw = payload.TryGetProperty("tools", out var toolsProp) ? $",\"tools\":{toolsProp.GetRawText()}" : "";
                contentJson = $"{{\"model\":\"{model}\",\"messages\":{messagesRaw}{toolsRaw},\"stream\":false}}";
            }
            else if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("prompt", out var promptProp))
            {
                var prompt = promptProp.GetString() ?? string.Empty;
                var messagesText = JsonSerializer.Serialize(new[] { new { role = "user", content = prompt } });
                contentJson = $"{{\"model\":\"{model}\",\"messages\":{messagesText},\"stream\":false}}";
            }
            else if (payload.ValueKind == JsonValueKind.String)
            {
                var prompt = payload.GetString() ?? string.Empty;
                var messagesText = JsonSerializer.Serialize(new[] { new { role = "user", content = prompt } });
                contentJson = $"{{\"model\":\"{model}\",\"messages\":{messagesText},\"stream\":false}}";
            }
            else
            {
                // Fallback: forward the body as-is
                contentJson = JsonSerializer.Serialize(payload);
            }
            var content = new StringContent(contentJson, Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(target, content);
            var text = await resp.Content.ReadAsStringAsync();
            return Content(text, resp.Content.Headers.ContentType?.ToString() ?? "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
    [HttpPost("proxy")]
    public async Task<IActionResult> Proxy([FromQuery] string path)
    {
        string baseUrl = await GetBaseURL();
        var target = baseUrl.TrimEnd('/') + "/" + (path ?? string.Empty).TrimStart('/');
        var client = _clientFactory.CreateClient("llama");
        var body = await new StreamReader(Request.Body).ReadToEndAsync();
        var mediaType = string.IsNullOrWhiteSpace(Request.ContentType)
            ? "application/json"
            : Request.ContentType.Split(';', 2)[0].Trim();
        try
        {
            var resp = await client.PostAsync(target, new StringContent(body, Encoding.UTF8, mediaType));
            var text = await resp.Content.ReadAsStringAsync();
            return Content(text, resp.Content.Headers.ContentType?.ToString() ?? "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
    /// <summary>
    /// Lists the models available on the configured (or query-specified) LLM server.
    /// Queries both Ollama (/api/tags) and OpenAI (/v1/models) endpoints, merging and
    /// deduplicating. Returns the unified model list so the Settings UI can show a
    /// dropdown of models the user ACTUALLY has loaded — instead of a free-text field.
    /// An optional <paramref name="url"/> query param probes a different server without
    /// changing the saved config (used by the "detect servers" flow).
    /// </summary>
    [HttpGet("models")]
    public async Task<IActionResult> GetModels([FromQuery] string? url = null, CancellationToken ct = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(url)
            ? await GetBaseURL()
            : url.Trim();
        try
        {
            var models = await _discovery.ListModelsAsync(baseUrl, ct);
            return Ok(new { url = baseUrl, models, count = models.Count });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { url = baseUrl, models = Array.Empty<object>(), count = 0, error = ex.Message });
        }
    }

    /// <summary>
    /// Scans common local ports (8080, 11434, 1234, 13305, 8000, 7860, 5000, 9997) for a
    /// running AI model server. Returns every server that responds with at least one model,
    /// with its URL, detected server type (ollama/openai/ollama+openai), and full model
    /// list — so the user can pick from detected servers in the Settings panel instead of
    /// guessing a URL. The scan runs all ports in parallel and completes in ~1-2 seconds.
    /// </summary>
    [HttpGet("detect-servers")]
    public async Task<IActionResult> DetectServers(CancellationToken ct = default)
    {
        var servers = await _discovery.DetectServersAsync(ct);
        return Ok(new { servers, count = servers.Count });
    }

    /// <summary>
    /// Swaps the loaded model on the specified (or configured) AI server. On servers that
    /// pin models in VRAM (Lemonade Server), selecting a different model in Settings does
    /// not take effect until the pinned model is unloaded and the new one is loaded. This
    /// endpoint performs that swap: unload all → load requested → verify. Returns the
    /// swap result so the frontend can confirm the model is actually active. Servers that
    /// auto-load on request (Ollama) return success immediately.
    /// </summary>
    [HttpPost("swap-model")]
    public async Task<IActionResult> SwapModel([FromBody] JsonElement payload, CancellationToken ct = default)
    {
        string? url = null, model = null;
        if (payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                url = urlEl.GetString();
            if (payload.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                model = modelEl.GetString();
        }
        var baseUrl = string.IsNullOrWhiteSpace(url) ? await GetBaseURL() : url.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return BadRequest("No model specified.");

        var result = await _discovery.SwapModelAsync(baseUrl, model!, ct);
        if (result.Success)
            return Ok(new { success = true, url = baseUrl, model = result.LoadedModel });
        return StatusCode(502, new { success = false, url = baseUrl, error = result.Error });
    }

    /// <summary>
    /// Checks whether the specified model is currently loaded and usable on the server by
    /// sending a minimal generation request. Returns true on HTTP 200, false on 409
    /// (slots_pinned_error — model not loaded) or any other error. Used by the Settings
    /// UI to show whether the user's selected model is actually active.
    /// </summary>
    [HttpGet("model-status")]
    public async Task<IActionResult> GetModelStatus([FromQuery] string? url = null, [FromQuery] string? model = null, CancellationToken ct = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(url) ? await GetBaseURL() : url.Trim();
        var modelId = string.IsNullOrWhiteSpace(model)
            ? (await _configFile.LoadConfigAsync()).llamaModel ?? "medgemma:4b"
            : model.Trim();
        var loaded = await _discovery.IsModelLoadedAsync(baseUrl, modelId, ct);
        return Ok(new { url = baseUrl, model = modelId, loaded });
    }
}
