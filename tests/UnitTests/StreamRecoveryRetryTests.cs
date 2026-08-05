using System.Reflection;
using Xunit;
using Weaver;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the one-shot stream-recovery logic that keeps a good partial LLM response from
/// being discarded when a network read error or max-token truncation kills the stream
/// (e.g. the correct refactor streamed just before "Stream read error: network error").
/// IsRecoverableStreamFailure must only fire for genuine transport/truncation failures
/// with substantive partial output — never for semantic failures (JSON parse, hallucination,
/// repetition, empty) which belong to the existing retry/rejection paths, and never for
/// plain timeouts (usually futile to re-run). AppendPartialContinuationHint must frame the
/// partial as a continuation task and cap pathological partials while keeping the tail.
/// </summary>
public class StreamRecoveryRetryTests
{
    private static bool IsRecoverableStreamFailure(string? partial, string? error)
    {
        var method = typeof(AgentController).GetMethod(
            "IsRecoverableStreamFailure", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[] { partial, error })!;
    }

    private static string AppendPartialContinuationHint(string userMessage, string partial)
    {
        var method = typeof(AgentController).GetMethod(
            "AppendPartialContinuationHint", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { userMessage, partial })!;
    }

    private static bool IsTransientTransportFailure(string? error)
    {
        var method = typeof(AgentController).GetMethod(
            "IsTransientTransportFailure", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[] { error })!;
    }

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

}
