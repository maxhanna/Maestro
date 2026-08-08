using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for AgentEditHeuristics.TrySurroundingLineReanchor — the deterministic,
/// zero-LLM retry for SMALL plan oldStrings (2-3 lines) that failed to match verbatim
/// at apply time. Instead of escalating to a full LLM re-resolve (which risks the
/// whole-section rewrite failure mode), it tries re-anchoring the anchor against each
/// surrounding line: shifted up/down, extended by a line the file gained, or trimmed of
/// a stale line the plan included. Only a UNIQUE, confident alignment is returned.
/// </summary>
public class SurroundingLineReanchorTests
{
    [Fact]
    public void FileGainedLineBetweenAnchor_ExtendsToFullBlock()
    {
        // Plan anchor [A, C] but the file gained line X between them.
        var file = "A\nX\nC";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "A\nC", targetLine: 1);

        Assert.NotNull(result);
        Assert.Equal("A\nX\nC", result.Value.correctedBlock);
        Assert.Equal(0, result.Value.startLineIdx);
        Assert.Equal(2, result.Value.score);
    }

    [Fact]
    public void FileGainedLine_ExtendsAtTargetLine()
    {
        // Same drift, but the planner stated line 3 (A) — the search stays near it.
        var file = "X\nY\nA\nM\nC";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "A\nC", targetLine: 3);

        Assert.NotNull(result);
        Assert.Equal("A\nM\nC", result.Value.correctedBlock);
        Assert.Equal(2, result.Value.startLineIdx);
        Assert.Equal(2, result.Value.score);
    }

    [Fact]
    public void PlanIncludedStaleLastLine_TrimsToFileBlock()
    {
        // Plan anchor [A, X, C] — X is stale (no longer in the file); the file is [A, C].
        var file = "A\nC";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "A\nX\nC", targetLine: 1);

        Assert.NotNull(result);
        Assert.Equal("A\nC", result.Value.correctedBlock);
        Assert.Equal(0, result.Value.startLineIdx);
        Assert.Equal(2, result.Value.score);
    }

    [Fact]
    public void PlanIncludedStaleFirstLine_TrimsToFileBlock()
    {
        // Plan anchor [X, B, C] — stale X first; the file is [B, C].
        var file = "B\nC";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "X\nB\nC", targetLine: 1);

        Assert.NotNull(result);
        Assert.Equal("B\nC", result.Value.correctedBlock);
        Assert.Equal(0, result.Value.startLineIdx);
        Assert.Equal(2, result.Value.score);
    }

    [Fact]
    public void OneLineDrifted_SameLengthShiftRecovers()
    {
        // A real token drifted on the second anchor line (C → C2) — same-length re-anchor
        // recovers the file-exact block instead of escalating.
        var file = "A\nC2";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "A\nC", targetLine: 1);

        Assert.NotNull(result);
        Assert.Equal("A\nC2", result.Value.correctedBlock);
        Assert.Equal(0, result.Value.startLineIdx);
        Assert.Equal(1, result.Value.score);
    }

    [Fact]
    public void WhitespaceDriftOnOneLine_StillMatches()
    {
        // Indentation/whitespace drift is absorbed by the tolerant per-line comparison.
        var file = "    A\n    C";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "  A\n  C", targetLine: 1);

        Assert.NotNull(result);
        Assert.Equal("    A\n    C", result.Value.correctedBlock);
    }

    [Fact]
    public void AnchorAlsoMatchesVerbatim_IsAmbiguous_ReturnsNull()
    {
        // The anchor [A, C] exists verbatim AND extends cleanly over [A, C, M] — two
        // equally-scored alignments means the re-anchor would be guesswork. (In the real
        // apply loop this never reaches the failure path — the verbatim match applies first.)
        var file = "A\nC\nM";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "A\nC", targetLine: 1);

        Assert.Null(result);
    }

    [Fact]
    public void TwoDifferentHighScoringAlignments_IsAmbiguous_ReturnsNull()
    {
        // A full match at line 1 AND a clean extend at line 4 — same score, different blocks.
        var file = "A\nC\nM\nA\nN\nC";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "A\nC", targetLine: 1);

        Assert.Null(result);
    }

    [Fact]
    public void NoSurroundingAlignment_ReturnsNull()
    {
        // Neither anchor line exists anywhere near the position.
        var file = "B\nC\nD";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "A\nZ", targetLine: 1);

        Assert.Null(result);
    }

    [Fact]
    public void SingleLineAnchor_IsNotReanchored_ReturnsNull()
    {
        // 1-line anchors have no surrounding-line structure to exploit — the apply loop's
        // whole-file tolerant matcher plus BuildExactMatchBlock already cover their drift.
        var file = "A\nB\nC";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "B", targetLine: 2);

        Assert.Null(result);
    }

    [Fact]
    public void AnchorLargerThanMax_IsNotReanchored_ReturnsNull()
    {
        // Only SMALL (≤3 line) anchors get the surrounding-line retry.
        var file = "A\nB\nC\nD";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "A\nB\nC\nD", targetLine: 1);

        Assert.Null(result);
    }

    [Fact]
    public void EmptyOrBlankAnchor_ReturnsNull()
    {
        Assert.Null(AgentEditHeuristics.TrySurroundingLineReanchor("A\nB", "", targetLine: 1));
        Assert.Null(AgentEditHeuristics.TrySurroundingLineReanchor("A\nB", "   ", targetLine: 1));
    }

    [Fact]
    public void TargetLineNotGiven_StillFindsBestPosition()
    {
        // No planner line number: the helper scans for the best anchor position itself.
        var file = "M\nN\nA\nX\nC";
        var result = AgentEditHeuristics.TrySurroundingLineReanchor(file, "A\nC");

        Assert.NotNull(result);
        Assert.Equal("A\nX\nC", result.Value.correctedBlock);
        Assert.Equal(2, result.Value.startLineIdx);
    }
}
