using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the one-shot stream-recovery logic that keeps a good partial LLM response from
/// being discarded when a network read error or max-token truncation kills the stream
/// (e.g. the correct refactor streamed just before "Stream read error: network error").
/// IsRecoverableStreamFailure (Services/TransientFailureDetector.cs) must only fire for
/// genuine transport/truncation failures with substantive partial output — never for
/// semantic failures (JSON parse, hallucination, repetition, empty) which belong to the
/// existing retry/rejection paths, and never for plain timeouts (usually futile to re-run).
/// AppendPartialContinuationHint must frame the partial as a continuation task and cap
/// pathological partials while keeping the tail.
/// </summary>
public class StreamRecoveryRetryTests
{
    private static bool IsRecoverableStreamFailure(string? partial, string? error)
        => TransientFailureDetector.IsRecoverableStreamFailure(partial, error);

    private static string AppendPartialContinuationHint(string userMessage, string partial)
    {
        var method = typeof(AgentController).GetMethod(
            "AppendPartialContinuationHint", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { userMessage, partial })!;
    }

    private static bool IsTransientTransportFailure(string? error)
        => TransientFailureDetector.IsTransientTransportFailure(error);

    private const string GoodPartial =
        "{\"planComplete\": false, \"thinking\": \"The task requires unifying getEventIcon and getEventDescription\", " +
        "\"step\": {\"file\": \"user-events.component.ts\", \"change\": \"Add combined getEventData method\"";

    // ── Recoverable: transport / stream / truncation failures with real partial content ──

    [Fact]
    public void ReadFailure_WithSubstantialPartial_IsRecoverable()
        => Assert.True(IsRecoverableStreamFailure(GoodPartial, "The read operation failed. See inner exception for details."));

    [Fact]
    public void NetworkError_WithSubstantialPartial_IsRecoverable()
        => Assert.True(IsRecoverableStreamFailure(GoodPartial, "An error occurred while sending the request. Connection reset by peer."));

    [Fact]
    public void StreamError_WithSubstantialPartial_IsRecoverable()
        => Assert.True(IsRecoverableStreamFailure(GoodPartial, "Error while copying content to a stream."));

    [Fact]
    public void Timeout_IsNotRecoverable()
        => Assert.False(IsRecoverableStreamFailure(GoodPartial, "LLM request timed out"));

    [Fact]
    public void MaxTokenTruncation_WithSubstantialPartial_IsRecoverable()
        => Assert.True(IsRecoverableStreamFailure(GoodPartial, "Response truncated at max_tokens — partial kept for recovery hint."));

    [Fact]
    public void PrematureEnd_WithSubstantialPartial_IsRecoverable()
        => Assert.True(IsRecoverableStreamFailure(GoodPartial, "The response ended prematurely."));

    // ── Not recoverable: semantic failures or too little partial to continue from ──

    [Fact]
    public void JsonParseFailure_IsNotRecoverable()
        => Assert.False(IsRecoverableStreamFailure(GoodPartial, "JSON parse failed"));

    [Fact]
    public void Hallucination_IsNotRecoverable()
        => Assert.False(IsRecoverableStreamFailure(GoodPartial, "Hallucination (wall of text): 3200 chars with 2 line breaks"));

    [Fact]
    public void RepetitionLoop_IsNotRecoverable()
        => Assert.False(IsRecoverableStreamFailure(GoodPartial, "Repetition loop detected after 800 chars — aborted early."));

    [Fact]
    public void EmptyResponse_IsNotRecoverable()
        => Assert.False(IsRecoverableStreamFailure(GoodPartial, "Empty LLM response"));

    [Fact]
    public void NoPartialContent_IsNotRecoverable()
        => Assert.False(IsRecoverableStreamFailure("", "The read operation failed."));

    [Fact]
    public void TrivialPartial_IsNotRecoverable()
        => Assert.False(IsRecoverableStreamFailure("short", "The read operation failed."));

    [Fact]
    public void NullPartial_IsNotRecoverable()
        => Assert.False(IsRecoverableStreamFailure(null, "The read operation failed."));

    [Fact]
    public void NullError_IsNotRecoverable()
        => Assert.False(IsRecoverableStreamFailure(GoodPartial, null));

    [Fact]
    public void HttpServerError_IsNotRecoverable()
        => Assert.False(IsRecoverableStreamFailure(GoodPartial, "HTTP 500"));

    // ── Non-streaming path: one silent retry for transient transport failures ──

    [Fact]
    public void NonStreaming_NetworkError_IsTransient()
        => Assert.True(IsTransientTransportFailure("An error occurred while sending the request. Connection reset by peer."));

    [Fact]
    public void NonStreaming_ReadError_IsTransient()
        => Assert.True(IsTransientTransportFailure("The read operation failed."));

    [Fact]
    public void NonStreaming_PrematureClose_IsTransient()
        => Assert.True(IsTransientTransportFailure("The response ended prematurely."));

    [Fact]
    public void NonStreaming_Timeout_IsTransient()
        => Assert.True(IsTransientTransportFailure("LLM request timed out"));

    [Fact]
    public void NonStreaming_JsonParse_IsNotTransient()
        => Assert.False(IsTransientTransportFailure("JSON parse failed"));

    [Fact]
    public void NonStreaming_Hallucination_IsNotTransient()
        => Assert.False(IsTransientTransportFailure("Hallucination (wall of text): 3200 chars with 2 line breaks"));

    [Fact]
    public void NonStreaming_Repetition_IsNotTransient()
        => Assert.False(IsTransientTransportFailure("Repetition loop detected after 800 chars — aborted early."));

    [Fact]
    public void NonStreaming_Empty_IsNotTransient()
        => Assert.False(IsTransientTransportFailure("Empty LLM response"));

    [Fact]
    public void NonStreaming_HttpError_IsNotTransient()
        => Assert.False(IsTransientTransportFailure("HTTP 500"));

    [Fact]
    public void NonStreaming_NullError_IsNotTransient()
        => Assert.False(IsTransientTransportFailure(null));

    // ── Continuation hint framing ──

    [Fact]
    public void Hint_ContainsPartialAndContinuationInstructions()
    {
        var hint = AppendPartialContinuationHint("### TASK ###\nrefactor", GoodPartial);
        Assert.Contains(GoodPartial, hint);
        Assert.Contains("STREAM ERROR", hint);
        Assert.Contains("CONTINUE", hint);
        Assert.Contains("COMPLETE", hint);
        Assert.Contains("### TASK ###\nrefactor", hint); // original prompt preserved at the front
    }

    [Fact]
    public void Hint_CapsPathologicalPartials_KeepingHeadAndTail()
    {
        var huge = new string('x', 50000);
        var hint = AppendPartialContinuationHint("task", huge);
        Assert.True(hint.Length < 20000, "retry prompt must stay bounded");
        Assert.Contains("…(partial truncated, middle omitted)…", hint);
        // The continuation point (the tail) must survive the cap.
        Assert.Contains(new string('x', 4000), hint);
        // The head must survive too.
        Assert.StartsWith("task\n\n### YOUR PREVIOUS RESPONSE", hint);
    }

    // ── Visible truncation marker on the prose path (pre-plan thinking) ──
    // CallLlmRawText deliberately returns a budget-capped cut as-is (partial reasoning is
    // still usable), so the cap used to be SILENT — a mid-sentence stop that looked like a
    // transport hang. Callers opt in via appendTruncationMarker:true (the pre-plan thinking
    // call does) and get an explicit marker instead. These tests drive the real transport
    // against a scripted fake LLM that returns finish_reason:"length" and assert the marker
    // appears exactly when (a) the response was token-capped AND (b) the caller opted in.

    private static async Task<(string raw, string? error)> CallRawText(
        string system, string user, bool emitSse, int? maxTokens, bool appendTruncationMarker)
    {
        var method = typeof(AgentController).GetMethod(
            "CallLlmRawText", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task<(string raw, string? error)>)method!.Invoke(
            RawTextController(), new object?[]
            {
                system, user, emitSse, CancellationToken.None,
                TimeSpan.FromSeconds(30), maxTokens, appendTruncationMarker, /*llmRoundLabel*/ null
            })!;
        return await task;
    }

    private const string TruncationMarker = "reasoning truncated — hit the per-response token budget";

    private sealed class RawTextHandler : HttpMessageHandler
    {
        public string FinishReason { get; init; } = "length";
        public string Content { get; init; } = "The plan is to add a getItems method to the demo component and also wire it into the template and then…";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            var data = JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = Content }, finish_reason = FinishReason } }
            });
            var body = $"data: {data}\n\n\ndata: [DONE]\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            });
        }
    }

    private sealed class RawTextFactory : IHttpClientFactory, IDisposable
    {
        public string FinishReason { get; init; } = "length";
        public HttpClient CreateClient(string name) => new(new RawTextHandler { FinishReason = FinishReason });
        public HttpClient CreateClient() => CreateClient("default");
        public void Dispose() { }
    }

    private static AgentController? _rawTextController;

    private static AgentController RawTextController()
    {
        if (_rawTextController != null) return _rawTextController;
        var baseDir = Path.Combine(Path.GetTempPath(), "weaver_marker_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(baseDir, "data"));
        var db = new DatabaseService(
            Path.Combine(baseDir, "data", "weaver.db"),
            Path.Combine(baseDir, "data"),
            Path.Combine(baseDir, "data", "weaverconfig.json"));
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        typeof(AgentController).GetField("_configFile", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(controller, new ConfigFileService(db));
        _rawTextController = controller;
        return controller;
    }

    [Fact]
    public async Task TokenCappedResponse_WithOptInMarker_AppendsExplicitMarker()
    {
        var factory = new RawTextFactory { FinishReason = "length" };
        typeof(AgentController).GetField("_clientFactory", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(RawTextController(), factory);

        var (raw, error) = await CallRawText("sys", "user", false, 64, appendTruncationMarker: true);

        Assert.Null(error);
        Assert.Contains(TruncationMarker, raw);
        Assert.Contains("getItems", raw); // partial reasoning survives, marker is appended after it
        Assert.EndsWith("…[reasoning truncated — hit the per-response token budget]…", raw.TrimEnd());
    }

    [Fact]
    public async Task TokenCappedResponse_WithoutOptIn_StaysSilent()
    {
        var factory = new RawTextFactory { FinishReason = "length" };
        typeof(AgentController).GetField("_clientFactory", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(RawTextController(), factory);

        var (raw, error) = await CallRawText("sys", "user", false, 64, appendTruncationMarker: false);

        Assert.Null(error);
        Assert.DoesNotContain(TruncationMarker, raw);
        Assert.Contains("getItems", raw);
    }

    [Fact]
    public async Task CompleteResponse_WithOptIn_DoesNotAppendMarker()
    {
        var factory = new RawTextFactory { FinishReason = "stop" };
        typeof(AgentController).GetField("_clientFactory", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(RawTextController(), factory);

        var (raw, error) = await CallRawText("sys", "user", false, 64, appendTruncationMarker: true);

        Assert.Null(error);
        Assert.DoesNotContain(TruncationMarker, raw);
        Assert.Contains("getItems", raw);
    }

    // ── "Finish this" continuation (max-token truncation of an oversized edit) ──

    private static bool IsMaxTokenTruncation(string? error)
    {
        var method = typeof(AgentController).GetMethod(
            "IsMaxTokenTruncation", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[] { error })!;
    }

    private static bool LooksLikeCompleteJson(string text)
    {
        var method = typeof(AgentController).GetMethod(
            "LooksLikeCompleteJson", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[] { text })!;
    }

    private static string BuildFinishThisPrompt(string userMessage, string partial)
    {
        var method = typeof(AgentController).GetMethod(
            "BuildFinishThisPrompt", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { userMessage, partial })!;
    }

    private static string StitchContinuation(string accumulated, string chunk, int overlapChars = 80)
    {
        var method = typeof(AgentController).GetMethod(
            "StitchContinuation", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { accumulated, chunk, overlapChars })!;
    }

    [Fact]
    public void MaxTokenTruncation_IsDistinctFromTransport()
    {
        Assert.True(IsMaxTokenTruncation("Response truncated at max_tokens — partial kept for recovery hint."));
        Assert.False(IsMaxTokenTruncation("The read operation failed."));
        Assert.False(IsMaxTokenTruncation("Connection reset by peer."));
        Assert.False(IsMaxTokenTruncation(null));
        Assert.False(IsMaxTokenTruncation(""));
    }

    [Fact]
    public void CompleteJson_IsRecognized()
    {
        Assert.True(LooksLikeCompleteJson("{\"a\": 1}"));
        Assert.True(LooksLikeCompleteJson("```json\n{\"a\": 1}\n```"));
        Assert.True(LooksLikeCompleteJson("prefix\n{\"a\": {\"b\": 2}}\nsuffix"));
    }

    [Fact]
    public void IncompleteJson_IsNotRecognized()
    {
        Assert.False(LooksLikeCompleteJson("{\"a\": 1"));                 // unclosed brace
        Assert.False(LooksLikeCompleteJson("{\"a\": \"unterminated"));    // mid-string
        Assert.False(LooksLikeCompleteJson(""));
        Assert.False(LooksLikeCompleteJson("not json"));
        Assert.False(LooksLikeCompleteJson("[]"));
        Assert.True(LooksLikeCompleteJson("{}"));                          // empty object still parses as complete
    }

    [Fact]
    public void FinishPrompt_AsksForOnlyTheTail_AndPreservesPartial()
    {
        var partial = "{\"step\": {\"newString\": \"private getEventData(eventType: string) {";
        var prompt = BuildFinishThisPrompt("### TASK ###\nrefactor", partial);
        Assert.Contains("FINISH THIS OUTPUT", prompt);
        Assert.Contains("ONLY the REMAINING characters", prompt);
        Assert.Contains("APPENDED verbatim", prompt);
        Assert.Contains("do NOT restart", prompt);
        Assert.Contains(partial, prompt);
        Assert.Contains("### TASK ###\nrefactor", prompt);
        Assert.DoesNotContain("COMPLETE response to the original request", prompt); // that's the transport hint's framing
    }

    [Fact]
    public void FinishPrompt_KeepsTheContinuationAnchorTail()
    {
        var partial = new string('x', 1000) + "END_MARKER";
        var prompt = BuildFinishThisPrompt("task", partial);
        // The exact continuation point must be in the prompt even if the partial is huge.
        Assert.Contains("END_MARKER", prompt);
        Assert.True(prompt.Length < 14000, "finish-this prompt must stay bounded");
    }

    [Fact]
    public void Stitch_AppendsWithoutOverlap_WhenChunkRepeatsTail()
    {
        var accumulated = "{\"newString\": \"private getEventData(eventType: string) {";
        var chunk = "private getEventData(eventType: string) { const icon = 1; }";
        var stitched = StitchContinuation(accumulated, chunk);
        // The repeated prefix (the accumulated tail) must be trimmed, so the method body is not duplicated.
        Assert.Contains("string) { const icon = 1; }", stitched);
        Assert.Equal(1, CountOccurrences(stitched, "private getEventData"));
        Assert.StartsWith(accumulated, stitched);
    }

    [Fact]
    public void Stitch_AppendsWholeChunk_WhenNoOverlap()
    {
        var accumulated = "{\"a\": 1";
        var stitched = StitchContinuation(accumulated, ", \"b\": 2}");
        Assert.Equal("{\"a\": 1, \"b\": 2}", stitched);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

}
