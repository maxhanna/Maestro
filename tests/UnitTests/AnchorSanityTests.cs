using Xunit;
using Weaver;
using Weaver.Services;
using static Weaver.Services.AgentEditHeuristics;

namespace Weaver.UnitTests;

/// <summary>
/// Deterministic tests for the anchor sanity helpers — <see cref="IsBarePunctuationAnchor"/>
/// and <see cref="IsLoneClosingBraceFirstLine"/> — the guard that bounces garbage "}"-class
/// oldStrings (bare punctuation, or an anchor starting with the previous block's closing
/// brace) before any LLM round-trip or apply machinery runs.
/// </summary>
public class AnchorSanityTests
{
    // ── IsBarePunctuationAnchor ─────────────────────────────────────────────

    [Theory]
    [InlineData("}")]
    [InlineData("{")]
    [InlineData(";")]
    [InlineData(")")]
    [InlineData("})")]
    [InlineData("};")]
    [InlineData("},")]
    [InlineData("(\"}]\"")]
    public void BarePunctuation_IsRejected(string anchor)
    {
        Assert.True(IsBarePunctuationAnchor(anchor));
    }

    [Fact]
    public void BarePunctuation_SurroundingWhitespace_IsRejected()
    {
        Assert.True(IsBarePunctuationAnchor("  }  "));
        Assert.True(IsBarePunctuationAnchor("\t}\n"));
    }

    [Theory]
    [InlineData("public class RecordBody")]
    [InlineData("    public string? Query { get; set; }")]
    [InlineData("    return Ok();")]
    [InlineData("const retryCount = 3;")]
    [InlineData("// a comment line")]
    [InlineData("} // trailing close brace with real context")]
    public void RealCodeAnchor_IsAccepted(string anchor)
    {
        Assert.False(IsBarePunctuationAnchor(anchor));
    }

    [Fact]
    public void EmptyOrWhitespace_NotFlagged()
    {
        Assert.False(IsBarePunctuationAnchor(null));
        Assert.False(IsBarePunctuationAnchor(""));
        Assert.False(IsBarePunctuationAnchor("   "));
    }

    [Fact]
    public void LongPunctuationOnly_NotFlagged()
    {
        // 7+ chars of punctuation isn't the classic bare-anchor shape — leave it to the
        // normal match-count machinery instead of over-blocking.
        Assert.False(IsBarePunctuationAnchor("}}}}}}}"));
    }

    [Fact]
    public void DeterministicBatchMarker_NotFlagged()
    {
        // The no-interference guarantee: a deterministic batch's marker string (which travels
        // in step.OldString/NewString) must never be mistaken for a bare-punctuation anchor,
        // or the pre-loop guard would eat deterministic steps.
        Assert.False(IsBarePunctuationAnchor("(deterministic batch: 2 edits, applied 2/2 occurrences)"));
        Assert.False(IsLoneClosingBraceFirstLine("(deterministic batch: 2 edits, applied 2/2 occurrences)"));
    }

    // ── IsLoneClosingBraceFirstLine ─────────────────────────────────────────

    [Fact]
    public void LoneClosingBrace_MultiLineAnchor_Flagged()
    {
        // The exact maxhanna failure shape: the model starts its oldString with the previous
        // block's closing brace before the real declaration.
        const string garbage =
            "}\n" +
            "\n" +
            "public class RecordBody\n" +
            "{\n" +
            " public string? Query { get; set; }\n" +
            " }";
        Assert.True(IsLoneClosingBraceFirstLine(garbage));
        // Not *only* punctuation — the two guards cover different shapes.
        Assert.False(IsBarePunctuationAnchor(garbage));
    }

    [Theory]
    [InlineData("}")]
    [InlineData("  }")]
    [InlineData("})")]
    [InlineData("};")]
    [InlineData("}\npublic class Foo {")]
    [InlineData("\n\n}\npublic class Foo {")]
    public void LoneClosingBraceFirstLine_Flagged(string anchor)
    {
        Assert.True(IsLoneClosingBraceFirstLine(anchor));
    }

    [Theory]
    [InlineData("public class RecordBody")]
    [InlineData("    return Ok();")]
    [InlineData("  private void Helper() {")]
    [InlineData("  private void Helper();")]
    [InlineData("")]
    [InlineData("   ")]
    public void NonBraceFirstLine_NotFlagged(string anchor)
    {
        Assert.False(IsLoneClosingBraceFirstLine(anchor));
    }

    [Fact]
    public void NullOrBlank_NotFlagged()
    {
        Assert.False(IsLoneClosingBraceFirstLine(null));
        Assert.False(IsLoneClosingBraceFirstLine("\n\n  \n"));
    }

    [Fact]
    public void FirstNonBlankLine_ReturnsTrimmedFirstRealLine()
    {
        Assert.Equal("public class Foo", FirstNonBlankLine("\n  public class Foo\n}"));
        Assert.Equal("}", FirstNonBlankLine("\r\n}"));
        Assert.Null(FirstNonBlankLine("   "));
        Assert.Null(FirstNonBlankLine(null));
    }
}
