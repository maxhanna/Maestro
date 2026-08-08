using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Weaver.Services;

using static Weaver.Services.AgentTokenMetrics;
using static Weaver.Services.AgentEditHeuristics;
using static Weaver.Services.AgentPlanParsing;
using static Weaver.Services.AgentMethodInventory;
using static Weaver.Services.AgentProjectUtilities;
using static Weaver.Services.AgentDiscovery;
using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentCodeFormatting;
using static Weaver.Services.AgentSkeleton;
using static Weaver.Services.AgentDiffUtilities;
using static Weaver.Services.AgentJsonUtilities;

/// <summary>Part of the split of the former AgentUtilities monolith.</summary>
public static class AgentTokenMetrics
{
    /// <summary>
    /// Conversation compaction threshold, derived from the LLM endpoint's context window
    /// (config: contextWindowTokens). The terminal agent's conversation is rolled up once
    /// it reaches 60% of the window: that leaves room for the system prompt and the
    /// response budget (defaultMaxTokens) inside the window at the moment of firing, and
    /// after compaction the conversation drops well below the threshold again. Floors at
    /// 2560 tokens (base instructions + a couple of turns — never compact on the very
    /// first call of a small-window server) and caps at 16000 (matches the accumulated
    /// thinking-budget ceiling; the terminal pipeline is designed to keep conversations
    /// lean via temp files, so a giant window should not balloon the prompt).
    /// </summary>
    private const int CompactionRatioPct = 60;
    private const int CompactThresholdFloor = 2560;
    private const int CompactThresholdCap = 16000;

    /// <summary>
    /// Approximate token count without a real tokenizer (no BPE tables). Mimics the
    /// behaviour of tiktoken-class vocabularies closely enough for display and
    /// compaction decisions (~±20% on mixed code/prose, vs up to 2-3× off for the old
    /// flat chars/4 rule on symbol-dense or whitespace-heavy text). Rules, each chosen
    /// to mirror how real BPE merges behave:
    ///  - Single spaces between tokens are FREE (BPE fuses " word" into one token and
    ///    never emits a standalone single-space token). Indentation/blank-line runs
    ///    (≥2 whitespace chars) cost ~1 token per 4 chars.
    ///  - Words/identifiers: split at lower→Upper case boundaries (camelCase/PascalCase
    ///    breaks a compound into pieces BPE also splits), then parts ≤10 chars are 1
    ///    token (common English words and short identifiers are single vocab entries),
    ///    longer parts cost ~1 token per 4 chars (rare/technical words get broken up).
    ///  - Digit runs: 1 token per 3 digits past the first three.
    ///  - Punctuation/symbols: 1 token per run of an identical char ("...", "==", "//"
    ///    are usually single vocab entries); adjacent different symbols count separately.
    ///  - Quoted literals (', ", `): counted as one blob at ~4 chars/token including the
    ///    quotes, which keeps JSON/HTML attribute-heavy text from overcounting.
    ///  - Non-ASCII letters (CJK etc.): 1 token per char, matching BPE density for CJK.
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var total = 0;
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (c == '"' || c == '\'' || c == '`')
            {
                // Quoted literal — consume to the matching close quote (honoring escapes)
                // and charge the whole blob (quotes included) at ~4 chars/token.
                var start = i;
                i++;
                while (i < n)
                {
                    if (text[i] == '\\') { i += 2; continue; }
                    if (text[i] == c) { i++; break; }
                    i++;
                }
                total += Math.Max(1, (i - start) / 4);
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var run = text[start..i];
                total += char.IsDigit(run[0]) ? DigitTokens(run.Length) : WordTokens(run);
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                var start = i;
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                var len = i - start;
                // Single inter-word spaces fuse into the neighbour word (free);
                // indentation / blank lines are real tokens.
                if (len >= 2) total += len / 4;
                continue;
            }
            // Punctuation/symbols: one token per run of the same character.
            while (i < n && text[i] == c) i++;
            total++;
        }
        return total;
    }

    private static int WordTokens(string word)
    {
        // Non-ASCII text (CJK etc.): ~1 token per char, close to BPE density for CJK.
        foreach (var ch in word)
            if (ch > 127) return word.Length;
        // Split identifiers at lower→Upper boundaries so compounds break the way BPE
        // vocab does ("EstimateTokens" → "Estimate" + "Tokens", not one blob).
        var total = 0;
        var start = 0;
        for (var k = 1; k < word.Length; k++)
        {
            if (char.IsUpper(word[k]) && char.IsLower(word[k - 1]))
            {
                total += PartTokens(word[start..k]);
                start = k;
            }
        }
        total += PartTokens(word[start..]);
        return Math.Max(1, total);
    }

    private static int PartTokens(string part)
    {
        // Common words / short identifiers (≤10 chars) are single vocab entries;
        // longer parts cost ~1 token per 4 chars.
        return part.Length <= 10 ? 1 : (part.Length + 3) / 4;
    }

    private static int DigitTokens(int len) =>
        len <= 3 ? 1 : (len + 2) / 3;

    public static int CompactThresholdForContextWindow(int contextWindowTokens)
    {
        if (contextWindowTokens <= 0) return CompactThresholdCap;
        var raw = (int)(contextWindowTokens * (CompactionRatioPct / 100.0));
        return Math.Clamp(raw, CompactThresholdFloor, CompactThresholdCap);
    }

    public static void CompactConversation(StringBuilder conversation, int contextWindowTokens, int keepLastTurns = 3)
    {
        if (conversation == null || conversation.Length == 0) return;
        var text = conversation.ToString();
        if (EstimateTokens(text) < CompactThresholdForContextWindow(contextWindowTokens)) return;
        var turns = text.Split("Command [", StringSplitOptions.None);
        if (turns.Length <= keepLastTurns + 1) return;
        var sb = new StringBuilder();
        sb.AppendLine("## Prior context (compacted)");
        sb.AppendLine("Earlier commands and their results are summarized below.");
        sb.AppendLine("The last " + keepLastTurns + " turns are preserved in full after this.");
        sb.AppendLine();
        var lines = new List<string>();
        for (var i = 1; i < turns.Length - keepLastTurns; i++)
        {
            var cmdText = turns[i];
            var nl = cmdText.IndexOf('\n');
            // The split delimiter "Command [" is consumed, so the chunk starts at
            // "N]: ..." — re-add the prefix so the summary reads "Command [N]: ...".
            lines.Add("  Command [" + (nl > 0 ? cmdText[..nl].Trim() : cmdText.Trim()));
        }
        if (lines.Count > 0)
        {
            sb.AppendLine("Executed:");
            foreach (var l in lines) sb.AppendLine(l);
        }
        sb.AppendLine();
        sb.Append(turns[0]);
        for (var i = Math.Max(1, turns.Length - keepLastTurns); i < turns.Length; i++)
            sb.Append("Command [").Append(turns[i]);
        conversation.Clear();
        conversation.Append(sb.ToString());
    }
}
