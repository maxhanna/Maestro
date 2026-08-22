using System.Net;
using System.Text;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks AiServerDiscoveryService — the local AI server detection + model enumeration
/// that powers the Settings model dropdown. The service must merge Ollama (/api/tags) and
/// OpenAI (/v1/models) model lists, deduplicate, handle unreachable servers gracefully,
/// and classify servers by which API surfaces they answer.
/// </summary>
public class AiServerDiscoveryServiceTests
{
    // ── Test infrastructure ──────────────────────────────────────────────────

    /// <summary>A handler that serves different JSON bodies based on the request path,
    /// simulating a real AI server answering /api/tags and /v1/models.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public string? TagsResponse { get; set; }
        public HttpStatusCode TagsStatus { get; set; } = HttpStatusCode.OK;
        public string? ModelsResponse { get; set; }
        public HttpStatusCode ModelsStatus { get; set; } = HttpStatusCode.OK;
        public List<string> RequestedPaths { get; } = new();
        public Func<string, string, HttpResponseMessage>? PostResponder { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            lock (RequestedPaths) RequestedPaths.Add(path);

            if (request.Method == HttpMethod.Post && PostResponder != null)
            {
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                return Task.FromResult(PostResponder(path, body));
            }

            if (path.EndsWith("/api/tags", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(TagsStatus)
                {
                    Content = new StringContent(TagsResponse ?? "{}", Encoding.UTF8, "application/json")
                });
            }
            if (path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(ModelsStatus)
                {
                    Content = new StringContent(ModelsResponse ?? "{}", Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        public HttpClient CreateClient() => CreateClient("default");
    }

    private static AiServerDiscoveryService Service(ScriptedHandler handler)
        => new(new FakeHttpClientFactory(handler));

    // ── ListModelsAsync: merging Ollama + OpenAI ─────────────────────────────

    [Fact]
    public async Task ListModels_BothEndpointsAnswer_MergesAndDeduplicates()
    {
        // Lemonade Server answers both /api/tags (Ollama names with :latest) and
        // /v1/models (OpenAI IDs without :latest). The same model appears in both lists
        // under slightly different names — both should be surfaced (the user picks either).
        var handler = new ScriptedHandler
        {
            TagsResponse = """{"models":[{"name":"Qwen2.5-Coder-7B:latest"},{"name":"llama3:latest"}]}""",
            ModelsResponse = """{"data":[{"id":"Qwen2.5-Coder-7B"},{"id":"phi3"}]}"""
        };

        var models = await Service(handler).ListModelsAsync("http://localhost:8080");

        // 4 unique names: Qwen2.5-Coder-7B:latest, llama3:latest, Qwen2.5-Coder-7B, phi3
        Assert.Equal(4, models.Count);
        Assert.Contains(models, m => m.Id == "Qwen2.5-Coder-7B:latest" && m.Format == "ollama");
        Assert.Contains(models, m => m.Id == "llama3:latest" && m.Format == "ollama");
        Assert.Contains(models, m => m.Id == "Qwen2.5-Coder-7B" && m.Format == "openai");
        Assert.Contains(models, m => m.Id == "phi3" && m.Format == "openai");
    }

    [Fact]
    public async Task ListModels_OnlyOllamaEndpoint_ReturnsOllamaModels()
    {
        var handler = new ScriptedHandler
        {
            TagsResponse = """{"models":[{"name":"mistral:latest"}]}""",
            ModelsStatus = HttpStatusCode.NotFound
        };

        var models = await Service(handler).ListModelsAsync("http://localhost:11434");

        Assert.Single(models);
        Assert.Equal("mistral:latest", models[0].Id);
        Assert.Equal("ollama", models[0].Format);
    }

    [Fact]
    public async Task ListModels_OnlyOpenAiEndpoint_ReturnsOpenAiModels()
    {
        var handler = new ScriptedHandler
        {
            TagsStatus = HttpStatusCode.NotFound,
            ModelsResponse = """{"data":[{"id":"gpt-oss-120b"}]}"""
        };

        var models = await Service(handler).ListModelsAsync("http://localhost:1234");

        Assert.Single(models);
        Assert.Equal("gpt-oss-120b", models[0].Id);
        Assert.Equal("openai", models[0].Format);
    }

    [Fact]
    public async Task ListModels_ServerUnreachable_ReturnsEmptyList()
    {
        // A handler that 404s everything simulates a dead/wrong server.
        var handler = new ScriptedHandler
        {
            TagsStatus = HttpStatusCode.NotFound,
            ModelsStatus = HttpStatusCode.NotFound
        };

        var models = await Service(handler).ListModelsAsync("http://localhost:9999");

        Assert.Empty(models);
    }

    [Fact]
    public async Task ListModels_EmptyModelArrays_ReturnsEmptyList()
    {
        var handler = new ScriptedHandler
        {
            TagsResponse = """{"models":[]}""",
            ModelsResponse = """{"data":[]}"""
        };

        var models = await Service(handler).ListModelsAsync("http://localhost:8080");

        Assert.Empty(models);
    }

    [Fact]
    public async Task ListModels_QueriesBothEndpoints()
    {
        var handler = new ScriptedHandler
        {
            TagsResponse = """{"models":[{"name":"a:latest"}]}""",
            ModelsResponse = """{"data":[{"id":"a"}]}"""
        };

        await Service(handler).ListModelsAsync("http://localhost:8080");

        Assert.Contains("/api/tags", handler.RequestedPaths);
        Assert.Contains("/v1/models", handler.RequestedPaths);
    }

    // ── SwapModelAsync: unload + load ─────────────────────────────────────────

    [Fact]
    public async Task SwapModel_UnloadAllThenLoad_ReturnsSuccess()
    {
        // Lemonade Server: POST /api/v1/unload (all) then POST /api/v1/load → success.
        var handler = new ScriptedHandler
        {
            PostResponder = (path, body) =>
            {
                if (path.EndsWith("/api/v1/unload", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":"success","message":"All models unloaded"}""", Encoding.UTF8, "application/json")
                    };
                if (path.EndsWith("/api/v1/load", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":"success","model_name":"Qwen3-8B-GGUF"}""", Encoding.UTF8, "application/json")
                    };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var result = await Service(handler).SwapModelAsync("http://localhost:8080", "Qwen3-8B-GGUF");

        Assert.True(result.Success);
        Assert.Equal("Qwen3-8B-GGUF", result.LoadedModel);
        Assert.Contains("/api/v1/unload", handler.RequestedPaths);
        Assert.Contains("/api/v1/load", handler.RequestedPaths);
    }

    [Fact]
    public async Task SwapModel_LoadFails_ReturnsFailureWithError()
    {
        var handler = new ScriptedHandler
        {
            PostResponder = (path, body) =>
            {
                if (path.EndsWith("/api/v1/unload", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":"success"}""", Encoding.UTF8, "application/json")
                    };
                if (path.EndsWith("/api/v1/load", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":{"code":"model_not_found"}}""", Encoding.UTF8, "application/json")
                    };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var result = await Service(handler).SwapModelAsync("http://localhost:8080", "bad-model");

        Assert.False(result.Success);
        Assert.Contains("Load failed", result.Error);
        Assert.Null(result.LoadedModel);
    }

    [Fact]
    public async Task SwapModel_ServerHasNoLoadApi_ReturnsSuccess_AutoLoad()
    {
        // Ollama/LM Studio don't implement /api/v1/load — they auto-load on first request.
        // The swap should return success (not an error) so the caller proceeds to generate.
        var handler = new ScriptedHandler
        {
            PostResponder = (path, body) =>
                new HttpResponseMessage(HttpStatusCode.NotFound) // no /api/v1/unload or /api/v1/load
        };

        var result = await Service(handler).SwapModelAsync("http://localhost:11434", "mistral:latest");

        Assert.True(result.Success);
        Assert.Equal("mistral:latest", result.LoadedModel);
    }

    [Fact]
    public async Task SwapModel_EmptyModelId_ReturnsFailure()
    {
        var handler = new ScriptedHandler();
        var result = await Service(handler).SwapModelAsync("http://localhost:8080", "");
        Assert.False(result.Success);
        Assert.Contains("No model ID", result.Error);
    }

    [Fact]
    public async Task SwapModel_UnloadNotFound_ContinuesToLoad()
    {
        // Lemonade returns 404 "Model not loaded" when nothing is loaded — the swap must
        // still proceed to the load step (404 on unload is not a fatal error).
        var loadCalled = false;
        var handler = new ScriptedHandler
        {
            PostResponder = (path, body) =>
            {
                if (path.EndsWith("/api/v1/unload", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("""{"error":"Model not loaded"}""", Encoding.UTF8, "application/json")
                    };
                if (path.EndsWith("/api/v1/load", StringComparison.OrdinalIgnoreCase))
                {
                    loadCalled = true;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":"success"}""", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var result = await Service(handler).SwapModelAsync("http://localhost:8080", "Qwen3-8B-GGUF");

        Assert.True(result.Success);
        Assert.True(loadCalled, "Load must be called even when unload returns 404.");
    }

    [Fact]
    public async Task SwapModel_ModelIdWithSpecialCharacters_ProducesValidJson()
    {
        // Model IDs with quotes/backslashes must be JSON-escaped, not raw-interpolated.
        // Without proper serialization, a " in the model name would produce invalid JSON
        // and the load request would fail with a 400 from the server.
        var receivedLoadBody = "";
        var handler = new ScriptedHandler
        {
            PostResponder = (path, body) =>
            {
                if (path.EndsWith("/api/v1/load", StringComparison.OrdinalIgnoreCase))
                {
                    receivedLoadBody = body;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":"success"}""", Encoding.UTF8, "application/json")
                    };
                }
                if (path.EndsWith("/api/v1/unload", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":"success"}""", Encoding.UTF8, "application/json")
                    };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var trickyModel = """model"with\"quotes""";
        var result = await Service(handler).SwapModelAsync("http://localhost:8080", trickyModel);

        Assert.True(result.Success);
        // The body must be valid JSON with the model name properly escaped.
        Assert.Contains("model_name", receivedLoadBody);
        using var doc = System.Text.Json.JsonDocument.Parse(receivedLoadBody);
        Assert.Equal(trickyModel, doc.RootElement.GetProperty("model_name").GetString());
    }
}
