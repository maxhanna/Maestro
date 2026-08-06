using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Pure transient-failure detection for both the terminal/build executor and the LLM stream
/// path. Extracted from AgentController.Terminal.cs and AgentController.Llm.cs so the retry
/// gates are unit-testable without reflecting into the controller.
/// </summary>
public static class TransientFailureDetector
{
    /// <summary>
    /// Detects transient terminal/build failures worth ONE retry — a file briefly locked by
    /// another process (lingering build daemon, IDE) or a momentary network/feed blip (NuGet
    /// restore, git fetch, npm). Mirrors <see cref="IsTransientTransportFailure"/> for the LLM
    /// path. Genuine failures (compile errors, test failures) are NOT matched — retrying those
    /// would only waste a build cycle.
    /// </summary>
    public static bool IsTransientCommandFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var o = output.ToLowerInvariant();
        // File-lock signatures (Windows): the same process/file got briefly held by someone
        // else — a stale build daemon or IDE holding obj/bin is the classic case.
        if (o.Contains("being used by another process", StringComparison.Ordinal) ||
            o.Contains("the process cannot access the file", StringComparison.Ordinal) ||
            o.Contains("sharing violation", StringComparison.Ordinal) ||
            o.Contains("file is locked", StringComparison.Ordinal) ||
            o.Contains("access to the path", StringComparison.Ordinal))
            return true;
        // Transient network / package-feed signatures.
        if (o.Contains("unable to connect", StringComparison.Ordinal) ||
            o.Contains("connection refused", StringComparison.Ordinal) ||
            o.Contains("connection reset", StringComparison.Ordinal) ||
            o.Contains("timed out", StringComparison.Ordinal) ||
            o.Contains("the remote name could not be resolved", StringComparison.Ordinal) ||
            o.Contains("network is unreachable", StringComparison.Ordinal) ||
            o.Contains("temporary failure", StringComparison.Ordinal) ||
            o.Contains("econnreset", StringComparison.Ordinal) ||
            o.Contains("econnrefused", StringComparison.Ordinal) ||
            o.Contains("etimedout", StringComparison.Ordinal) ||
            o.Contains("socket hang up", StringComparison.Ordinal) ||
            o.Contains("failed to restore", StringComparison.Ordinal) ||
            o.Contains("nu1101", StringComparison.Ordinal) ||
            o.Contains("unable to load the service index", StringComparison.Ordinal))
            return true;
        // 'retrying' alone is NOT enough — NuGet prints "Retrying 'FindPackagesByIdAsync'…"
        // and then recovers internally, so a SUCCESSFUL restore can contain the word. Only
        // treat it as transient when it sits next to a real failure indicator: error CODES
        // (CS0103, NU1101, MSB3021) or 'error:' — never the bare word 'error', which also
        // appears in the success line "0 Error(s)". Also match failed/unable/could not.
        if (o.Contains("retrying", StringComparison.Ordinal) &&
            (Regex.IsMatch(o, @"error\s+[a-z]{2,4}\d{3,4}") ||
             o.Contains("error:", StringComparison.Ordinal) ||
             o.Contains("failed", StringComparison.Ordinal) ||
             o.Contains("unable", StringComparison.Ordinal) ||
             o.Contains("could not", StringComparison.Ordinal)))
            return true;
        return false;
    }

    /// <summary>
    /// True when command output looks like a HARD failure (not a transient blip) — a compile
    /// error, test failure, or a definitive error line. Used to judge whether a retry actually
    /// recovered the command: a retry whose output is no longer transient AND no longer shows a
    /// hard failure counts as recovered. Distinguishes error CODES / 'error:' from the benign
    /// "0 Error(s)" success line that contains the bare word 'error'.
    /// </summary>
    public static bool LooksLikeCommandFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var o = output.ToLowerInvariant();
        return Regex.IsMatch(o, @"error\s+[a-z]{2,4}\d{3,4}") ||
               o.Contains("error:", StringComparison.Ordinal) ||
               o.Contains("exception", StringComparison.Ordinal) ||
               o.Contains("failed", StringComparison.Ordinal) ||
               o.Contains("not recognized", StringComparison.Ordinal) ||
               o.Contains("cannot find", StringComparison.Ordinal) ||
               o.Contains("not found", StringComparison.Ordinal) ||
               o.Contains("access denied", StringComparison.Ordinal) ||
               o.Contains("permission denied", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when an LLM error string looks like a transient transport blip (read/stream/
    /// network/connection/reset/premature close) rather than a definitive model or parse
    /// failure. Deliberately returns false for HTTP status lines, JSON parse errors,
    /// hallucination / repetition-loop / empty-response messages — those are not worth a retry.
    /// </summary>
    public static bool IsTransientTransportFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        if (error.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("JSON parse", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("hallucination", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Repetition loop", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Empty LLM response", StringComparison.OrdinalIgnoreCase))
            return false;
        return error.Contains("read", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("network", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("prematurely", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a streamed LLM response dropped mid-stream but carried enough partial content
    /// to be worth one retry with that partial as a continuation hint. Requires a real partial
    /// (≥40 chars) plus a transport-style error — not a JSON-parse / hallucination /
    /// repetition-loop / empty-response failure, which retrying won't fix.
    /// </summary>
    public static bool IsRecoverableStreamFailure(string? partial, string? error)
    {
        if (string.IsNullOrWhiteSpace(partial) || partial.Length < 40) return false;
        if (string.IsNullOrWhiteSpace(error)) return false;
        if (error.Contains("JSON parse", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("hallucination", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Repetition loop", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Empty LLM response", StringComparison.OrdinalIgnoreCase))
            return false;
        return error.Contains("read", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("network", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
               // HttpIOException for a server closing the body early: "The response ended prematurely."
               error.Contains("prematurely", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("truncated at max_tokens", StringComparison.OrdinalIgnoreCase);
    }
}
