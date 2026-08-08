using System.Text;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

public class AgentTokenMetricsTests
{
    // ── EstimateTokens reference cases ───────────────────────────────────────
    // Expected values are hand-computed from the documented heuristic (single spaces
    // free, words ≤10 chars = 1 token with case-boundary splitting, digits ~1/3,
    // punctuation 1 per same-char run, quoted literals ~4 chars/token, non-ASCII 1/char).
    // These approximate tiktoken-class counts: "Hello world" is 2, a JSON blob is ~1
    // token per key/value, symbol-dense code tracks closer than the old flat chars/4.

    [Theory]
    [InlineData("", 0)]
    [InlineData("Hello world", 2)]
    [InlineData("hello, world", 3)]
    [InlineData("the quick brown fox jumps over the lazy dog", 9)]
    [InlineData("public static int EstimateTokens(string text) =>", 11)]
    [InlineData("CompactThresholdForContextWindow", 5)]
    [InlineData("TrySurroundingLineReanchor", 6)]
    [InlineData("IsNullOrWhiteSpace", 5)]
    [InlineData("benchmark", 1)]
    [InlineData("1234567890", 4)]
    [InlineData("123", 1)]
    [InlineData("{\"key\":\"value\"}", 5)]
    [InlineData("...", 1)]
    [InlineData("===", 1)]
    [InlineData(");", 2)]
    [InlineData("\"Command [\"", 2)]
    [InlineData("\u4f60\u597d\u4e16\u754c", 4)]
    [InlineData("    var x = 1;", 6)]
    // ngFor is camelCase → splits to ng + For (2 tokens); quotes fuse their content.
    [InlineData("<div *ngFor=\"let b of benchmarks\" class=\"benchmark-item\">", 18)]
    public void EstimateTokens_ReferenceCases(string input, int expected)
    {
        Assert.Equal(expected, AgentTokenMetrics.EstimateTokens(input));
    }

    [Fact]
    public void EstimateTokens_QuoteWithEscapes_CountsWholeLiteral()
    {
        // Runtime string is: " a b \ " c d "  (8 chars — the \" is an escaped quote,
        // NOT the end of the literal). Whole literal → 8/4 = 2. If the escape were
        // mishandled the literal would close early at 4 chars → 1.
        var input = "\"ab\\\"cd\"";
        Assert.Equal(2, AgentTokenMetrics.EstimateTokens(input));
    }

    // ── CompactThresholdForContextWindow derivation ──────────────────────────

    [Theory]
    [InlineData(8192, 4915)]   // default window → 60%
    [InlineData(16384, 9830)]  // larger window → 60%
    [InlineData(4096, 2560)]   // small window → floored at 2560
    [InlineData(2048, 2560)]   // tiny window → floored at 2560
    [InlineData(32768, 16000)] // huge window → capped at 16000
    public void CompactThreshold_ScalesWithContextWindow(int window, int expected)
    {
        Assert.Equal(expected, AgentTokenMetrics.CompactThresholdForContextWindow(window));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CompactThreshold_InvalidWindow_FallsBackToCap(int window)
    {
        Assert.Equal(16000, AgentTokenMetrics.CompactThresholdForContextWindow(window));
    }

    // ── CompactConversation behavior ─────────────────────────────────────────

    [Fact]
    public void CompactConversation_BelowThreshold_LeavesConversationUntouched()
    {
        var conversation = new StringBuilder();
        conversation.AppendLine("You are a terminal agent.");
        conversation.AppendLine("Command [1]: echo hello");
        conversation.AppendLine("Output:");
        conversation.AppendLine("hello");

        var before = conversation.ToString();

        AgentTokenMetrics.CompactConversation(conversation, contextWindowTokens: 8192);

        Assert.Equal(before, conversation.ToString());
    }

    [Fact]
    public void CompactConversation_OverThreshold_SummarizesOldTurnsKeepsRecentThreeInFull()
    {
        var conversation = new StringBuilder();
        conversation.AppendLine("### BASE INSTRUCTIONS ###");
        // Each turn is ~4250 chars (~1060 tokens); five turns pushes the estimated
        // conversation (~5300 tokens) past the 4915-token threshold for an 8192 window.
        for (var i = 1; i <= 5; i++)
        {
            conversation.AppendLine($"Command [{i}]: rg pattern{i}");
            conversation.AppendLine("Output:");
            conversation.AppendLine(new string('x', 4200) + $" result{i}");
        }

        var before = conversation.ToString();
        Assert.True(AgentTokenMetrics.EstimateTokens(before) >=
            AgentTokenMetrics.CompactThresholdForContextWindow(8192));

        AgentTokenMetrics.CompactConversation(conversation, contextWindowTokens: 8192);

        var after = conversation.ToString();
        // Instructions (everything before the first "Command [") survive intact.
        Assert.Contains("### BASE INSTRUCTIONS ###", after);
        // Header + summary of rolled-up commands present.
        Assert.Contains("## Prior context (compacted)", after);
        Assert.Contains("Executed:", after);
        // The earliest command is summarized by name only — its output is gone.
        Assert.Contains("Command [1]: rg pattern1", after);
        Assert.DoesNotContain("result1", after);
        // The last three turns remain verbatim, including their outputs.
        Assert.Contains("result3", after);
        Assert.Contains("result4", after);
        Assert.Contains("result5", after);
        // The compacted conversation is strictly smaller.
        Assert.True(after.Length < before.Length);
    }

    [Fact]
    public void CompactConversation_TooFewCommandTurns_LeavesConversationUntouched()
    {
        // A conversation dominated by web turns (no "Command [" markers) does not
        // have the turn structure the roll-up needs — it is intentionally left as-is
        // rather than mangling the instruction block. Sized well past the threshold
        // (~15k tokens vs 4915) so the guard being exercised is the turn-count check,
        // not the token-threshold early return.
        var conversation = new StringBuilder();
        conversation.AppendLine("You are a terminal agent.");
        conversation.AppendLine("Web search [1]: AI news");
        conversation.AppendLine("Results:");
        conversation.AppendLine(new string('y', 60000));

        var before = conversation.ToString();
        Assert.True(AgentTokenMetrics.EstimateTokens(before) >=
            AgentTokenMetrics.CompactThresholdForContextWindow(8192));

        AgentTokenMetrics.CompactConversation(conversation, contextWindowTokens: 8192);

        Assert.Equal(before, conversation.ToString());
    }
}
