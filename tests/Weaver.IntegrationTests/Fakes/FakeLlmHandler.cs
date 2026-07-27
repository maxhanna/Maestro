using System.Net;
using System.Text;
using System.Text.Json;

namespace Weaver.IntegrationTests.Fakes;

/// <summary>
/// Stands in for the real LLM endpoint. Every LLM call in AgentController — streaming
/// or not — funnels through IHttpClientFactory.CreateClient("llama") POSTing to
/// {baseUrl}/v1/chat/completions, distinguished only by a "stream" field in the request
/// body. This single handler covers that entire surface: it dequeues the next scripted
/// response body in call order and wraps it as either a plain chat-completion JSON body
/// (non-streaming) or an OpenAI-style SSE stream (streaming), matching what
/// CallLlmNonStreaming/CallLlmStreaming expect to parse.
/// </summary>
public sealed class FakeLlmHandler : HttpMessageHandler
{
    readonly Queue<string> _script;

    /// <summary>Raw request bodies seen so far, in order — useful for diagnosing a failed test.</summary>
    public List<string> ReceivedRequestBodies { get; } = new();

    public FakeLlmHandler(IEnumerable<string> scriptedResponseContents)
    {
        _script = new Queue<string>(scriptedResponseContents);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bodyText = request.Content != null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : "";
        ReceivedRequestBodies.Add(bodyText);

        if (_script.Count == 0)
        {
            throw new InvalidOperationException(
                $"FakeLlmHandler script exhausted after {ReceivedRequestBodies.Count} call(s) — " +
                $"an unexpected LLM call was made. Last request body: {bodyText}");
        }

        var content = _script.Dequeue();
        var isStreaming = false;
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            if (doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True)
                isStreaming = true;
        }
        catch (JsonException) { /* malformed request body — treat as non-streaming */ }

        return isStreaming ? StreamingResponse(content) : NonStreamingResponse(content);
    }

    static HttpResponseMessage NonStreamingResponse(string content)
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    static HttpResponseMessage StreamingResponse(string content)
    {
        var chunk = JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content } } }
        });
        var sse = $"data: {chunk}\n\ndata: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
    }
}
