using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the drift-recovery behavior of the edit matcher (AgentEditHeuristics.TryReplaceSafe's
/// blank-line tolerant fuzzy fallback) and the identifier-grounded re-anchor
/// (TryIdentifierAnchoredReanchor + FindIdentifierGroundedLines) that covers the harder drift
/// it cannot absorb. The benchmark-22/live navigation.component.ts-shaped failure: the LLM (or
/// plan) supplies an oldString like `musicTodoCount: number | null = null;` that fails the
/// verbatim ordinal match because of whitespace/line drift — dropped indentation, an omitted
/// blank line, or a phantom EMPTY line the model inserted between two real lines. The fuzzy
/// fallback absorbs BLANK-line drift directly (phantom blanks skipped on both sides, real
/// lines compared exactly), while INDENTATION drift is deliberately left to the re-anchor
/// chain — the deterministic-batch G1 contract requires a drifted anchor to fail at apply
/// time so it re-anchors against the CURRENT file text. TryIdentifierAnchoredReanchor finds
/// where the oldString's OWN identifier actually lives in the file and rebuilds the block
/// from the real text (real indentation, real surrounding lines), so an edit can never land
/// on an unrelated block like `tradeNotifsCount`.
/// </summary>
public class AnchorReanchorTests
{
    private const string ComponentFile =
        "export class NavigationComponent {\n" +
        "    tradeNotifsCount = 0;\n" +
        "\n" +
        "    musicTodoCount: number | null = null;\n" +
        "\n" +
        "    arrayActivePlayers: number | null = null;\n" +
        "\n" +
        "    ngOnInit() { }\n" +
        "}\n";

    [Fact]
    public void IndentDrift_TwoLineAnchor_ReanchorsByIdentifier()
    {
        // The model dropped the indentation AND omitted the blank line between the two
        // declarations (the file has one). The verbatim ordinal match fails, and the fuzzy
        // fallback (exact per-line, blank-line only) correctly refuses indentation drift —
        // the re-anchor chain's job.
        var oldStr = "musicTodoCount: number | null = null;\narrayActivePlayers: number | null = null;";
        var newStr = "    musicTodoCount: number | null = null;\n    movieTodoCount: number | null = null;\n\n    arrayActivePlayers: number | null = null;";

        // Verbatim ordinal match fails — this is the failure class the log showed.
        var (replaced, _, matchError, _) = AgentEditHeuristics.TryReplaceSafe(ComponentFile, oldStr, newStr);
        Assert.False(replaced);
        Assert.NotNull(matchError);

        // The identifier-grounded re-anchor finds the REAL lines (with real indentation,
        // real blank) — grounded on the oldString's OWN word, never the neighbor.
        var reanchor = AgentEditHeuristics.TryIdentifierAnchoredReanchor(ComponentFile, oldStr);
        Assert.NotNull(reanchor);
        Assert.Equal("    musicTodoCount: number | null = null;\n\n    arrayActivePlayers: number | null = null;",
            reanchor!.Value.correctedBlock);
        Assert.Equal(4, reanchor.Value.startLineIdx + 1); // musicTodoCount is file line 4 (0-based 3)
        Assert.DoesNotContain("tradeNotifsCount", reanchor.Value.correctedBlock);

        // Applying with the corrected block succeeds and inserts movieTodoCount right after.
        var (replaced2, newContent, _, _) =
            AgentEditHeuristics.TryReplaceSafe(ComponentFile, reanchor.Value.correctedBlock, newStr);
        Assert.True(replaced2, "the corrected (real-indentation) block must apply");
        Assert.Contains("    musicTodoCount: number | null = null;\n    movieTodoCount: number | null = null;", newContent);
        Assert.Single(RegexMatches(newContent, @"arrayActivePlayers"));
    }

    [Fact]
    public void IndentDrift_MultiLineAnchor_ReanchorsWholeWindow_NoDuplicates()
    {
        // 3-line oldString (with a blank middle) drifted — no leading indentation anywhere.
        var oldStr = "musicTodoCount: number | null = null;\n\narrayActivePlayers: number | null = null;";
        var newStr = "    musicTodoCount: number | null = null;\n    movieTodoCount: number | null = null;\n\n    arrayActivePlayers: number | null = null;";

        // The phantom blank alone would be absorbed by the fuzzy fallback, but the
        // INDENTATION drift refuses it — the re-anchor chain rebuilds from the real text.
        var (replaced, _, _, _) = AgentEditHeuristics.TryReplaceSafe(ComponentFile, oldStr, newStr);
        Assert.False(replaced, "indentation-drifted oldString must not match the fuzzy fallback");

        var reanchor = AgentEditHeuristics.TryIdentifierAnchoredReanchor(ComponentFile, oldStr);
        Assert.NotNull(reanchor);
        Assert.Equal("    musicTodoCount: number | null = null;\n\n    arrayActivePlayers: number | null = null;",
            reanchor!.Value.correctedBlock);
        Assert.DoesNotContain("tradeNotifsCount", reanchor.Value.correctedBlock);

        var (replaced2, newContent, _, _) =
            AgentEditHeuristics.TryReplaceSafe(ComponentFile, reanchor.Value.correctedBlock, newStr);
        Assert.True(replaced2, "the corrected window must apply");
        // movieTodoCount inserted between the two real lines; arrayActivePlayers appears EXACTLY once.
        Assert.Contains("    musicTodoCount: number | null = null;\n    movieTodoCount: number | null = null;", newContent);
        Assert.Single(RegexMatches(newContent, @"arrayActivePlayers"));
    }

    [Fact]
    public void PhantomBlankLine_FuzzyMatcherRecoversDirectly()
    {
        // The live log shape: the LLM emitted oldString as a 3-line array with a phantom
        // EMPTY line between the two declarations — ["musicTodoCount: number | null = null;",
        // "", "arrayActivePlayers: number | null = null;"] — which failed "oldString not
        // found verbatim in file" on every retry. With matching indentation, the fuzzy
        // matcher skips the phantom blank and recovers deterministically.
        var oldStr = "    musicTodoCount: number | null = null;\n\n    arrayActivePlayers: number | null = null;";
        var newStr = "    musicTodoCount: number | null = null;\n    movieTodoCount: number | null = null;\n\n    arrayActivePlayers: number | null = null;";

        var (replaced, newContent, matchError, _) = AgentEditHeuristics.TryReplaceSafe(ComponentFile, oldStr, newStr);
        Assert.True(replaced, $"phantom-blank anchor must be recovered by the fuzzy matcher: {matchError}");
        // movieTodoCount inserted right after musicTodoCount (never after tradeNotifsCount),
        // and arrayActivePlayers appears EXACTLY once.
        Assert.Contains("    musicTodoCount: number | null = null;\n    movieTodoCount: number | null = null;", newContent);
        Assert.Single(RegexMatches(newContent, @"arrayActivePlayers"));
    }

    [Fact]
    public void PhantomLeadingBlankLine_FuzzyMatcherRecoversDirectly()
    {
        // The LLM emitted oldString with a phantom EMPTY line at the START — ["",
        // "    musicTodoCount: number | null = null;", "    arrayActivePlayers: number | null = null;"]
        // — which failed verbatim. The fuzzy fallback drops the leading blank and recovers.
        var oldStr = "\n    musicTodoCount: number | null = null;\n    arrayActivePlayers: number | null = null;";
        var newStr = "    musicTodoCount: number | null = null;\n    movieTodoCount: number | null = null;\n\n    arrayActivePlayers: number | null = null;";

        var (replaced, newContent, matchError, _) = AgentEditHeuristics.TryReplaceSafe(ComponentFile, oldStr, newStr);
        Assert.True(replaced, $"leading-phantom-blank anchor must be recovered by the fuzzy matcher: {matchError}");
        Assert.Contains("    musicTodoCount: number | null = null;\n    movieTodoCount: number | null = null;", newContent);
        Assert.Single(RegexMatches(newContent, @"arrayActivePlayers"));
        Assert.StartsWith("export class NavigationComponent {", newContent);
    }

    [Fact]
    public void PhantomTrailingBlankLine_FuzzyMatcherRecoversDirectly()
    {
        // The LLM emitted oldString with a phantom EMPTY line at the END — ["    musicTodoCount:
        // number | null = null;", "    arrayActivePlayers: number | null = null;", ""] — which
        // failed verbatim. The fuzzy fallback drops the trailing blank, skips the file's
        // interior blank, and recovers.
        var oldStr = "    musicTodoCount: number | null = null;\n    arrayActivePlayers: number | null = null;\n";
        var newStr = "    musicTodoCount: number | null = null;\n    movieTodoCount: number | null = null;\n\n    arrayActivePlayers: number | null = null;";

        var (replaced, newContent, matchError, _) = AgentEditHeuristics.TryReplaceSafe(ComponentFile, oldStr, newStr);
        Assert.True(replaced, $"trailing-phantom-blank anchor must be recovered by the fuzzy matcher: {matchError}");
        Assert.Contains("    musicTodoCount: number | null = null;\n    movieTodoCount: number | null = null;", newContent);
        Assert.Single(RegexMatches(newContent, @"arrayActivePlayers"));
        // The content AFTER the anchor (ngOnInit + closing brace) is preserved untouched.
        Assert.Contains("\n\n    ngOnInit() { }\n}\n", newContent);
    }

    [Fact]
    public void WrongNeighborLine_NeverSelected()
    {
        // The tolerant matcher's failure mode in the log: it could point at an UNRELATED
        // neighbor ("tradeNotifsCount = 0;"). The identifier-grounded re-anchor is anchored on
        // the oldString's OWN word (musicTodoCount), so it can never select that line — even
        // when the neighbor sits directly adjacent.
        const string tightFile =
            "    tradeNotifsCount = 0;\n" +
            "    musicTodoCount: number | null = null;\n" +
            "    arrayActivePlayers: number | null = null;\n";
        var oldStr = "musicTodoCount: number | null = null;\narrayActivePlayers: number | null = null;";

        var reanchor = AgentEditHeuristics.TryIdentifierAnchoredReanchor(tightFile, oldStr);
        Assert.NotNull(reanchor);
        Assert.StartsWith("    musicTodoCount", reanchor!.Value.correctedBlock);
        Assert.DoesNotContain("tradeNotifsCount", reanchor.Value.correctedBlock);
    }

    [Fact]
    public void InventedSiblingLine_ReturnsNull_ForLlmFallback()
    {
        // The oldString references a sibling line ("movieActivePlayers") that does NOT exist
        // in the file — the model fabricated it. No deterministic re-anchor can map it, so the
        // helper returns null and the resolver gets the real-content hint instead of re-
        // emitting the same fabricated anchor 3×.
        var oldStr = "musicTodoCount: number | null = null;\n\nmovieActivePlayers: number | null = null;";
        Assert.Null(AgentEditHeuristics.TryIdentifierAnchoredReanchor(ComponentFile, oldStr));
    }

    [Fact]
    public void AmbiguousToken_WithoutLineHint_ReturnsNull()
    {
        // The anchor identifier appears in TWO identical declarations and there is no line
        // hint — genuinely ambiguous, must not guess.
        const string dupFile =
            "export class A {\n" +
            "    musicTodoCount: number | null = null;\n" +
            "}\n" +
            "export class B {\n" +
            "    musicTodoCount: number | null = null;\n" +
            "}\n";
        Assert.Null(AgentEditHeuristics.TryIdentifierAnchoredReanchor(dupFile, "musicTodoCount: number | null = null;"));

        // With a line hint near the second declaration, the nearest wins.
        var reanchor = AgentEditHeuristics.TryIdentifierAnchoredReanchor(
            dupFile, "musicTodoCount: number | null = null;", targetLine: 6);
        Assert.NotNull(reanchor);
        Assert.Equal(5, reanchor!.Value.startLineIdx + 1); // class B's declaration (file line 5)
    }

    [Fact]
    public void FindIdentifierGroundedLines_ShowsRealLines_EvenWhenSiblingFabricated()
    {
        // Display probe: the model fabricated the sibling line, so the apply-path re-anchor
        // returns null — but the probe still shows WHERE the anchor lives so the model can
        // copy the real text and fix its own anchor.
        var oldStr = "musicTodoCount: number | null = null;\n\nmovieActivePlayers: number | null = null;";
        var lines = AgentEditHeuristics.FindIdentifierGroundedLines(ComponentFile, oldStr);
        Assert.NotNull(lines);
        Assert.Contains("musicTodoCount", lines!);
        Assert.DoesNotContain("movieActivePlayers", lines);
    }

    [Fact]
    public void ExtractAnchorIdentifierTokens_ExcludesTypeWords()
    {
        var tokens = AgentEditHeuristics.ExtractAnchorIdentifierTokens(
            "musicTodoCount: number | null = null;");
        Assert.Contains("musicTodoCount", tokens);
        Assert.DoesNotContain("number", tokens);
        Assert.DoesNotContain("null", tokens);

        var camel = AgentEditHeuristics.ExtractAnchorIdentifierTokens(
            "private _movieActivePlayers: number | null = null;");
        Assert.Contains("_movieActivePlayers", camel);
        Assert.DoesNotContain("private", camel);
    }

    private static System.Text.RegularExpressions.MatchCollection RegexMatches(string text, string pattern) =>
        System.Text.RegularExpressions.Regex.Matches(text, pattern);
}
