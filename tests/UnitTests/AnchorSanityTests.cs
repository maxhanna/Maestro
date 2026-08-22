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

    // ── ShouldBounceGarbageAnchor ───────────────────────────────────────────

    [Fact]
    public void BarePunctuation_AlwaysBounced_EvenForDeterministic()
    {
        // A lone "}" matches the FIRST close brace anywhere in the file — never a safe
        // anchor, even when server-authored. The deterministic exemption covers only the
        // multi-line lone-brace-FIRST shape, never this one.
        Assert.True(ShouldBounceGarbageAnchor("}", isDeterministic: true));
        Assert.True(ShouldBounceGarbageAnchor("}", isDeterministic: false));
        Assert.True(ShouldBounceGarbageAnchor("  }  ", isDeterministic: true));
        Assert.True(ShouldBounceGarbageAnchor(";", isDeterministic: true));
    }

    [Fact]
    public void LoneBraceFirst_MultiLine_BouncedForLlm_AllowedForDeterministic()
    {
        // The exact live shape: the last method's close brace immediately followed by the
        // class's close brace. LLM-authored → bounce (classic garbage shape).
        // Deterministic → the generator synthesized this as a contiguous unique slice of
        // the real file, so it is a legitimate end-of-class insert anchor.
        const string anchor = "  }\n}";
        Assert.True(ShouldBounceGarbageAnchor(anchor, isDeterministic: false));
        Assert.False(ShouldBounceGarbageAnchor(anchor, isDeterministic: true));
    }

    [Theory]
    [InlineData("\n}")]
    [InlineData("\n\n  }\n}")]
    [InlineData("  }\n}\n")]
    public void LoneBraceFirst_MultiLine_Variants_AllowedForDeterministic(string anchor)
    {
        // Leading/trailing blanks don't change the shape: still a contiguous end-of-class
        // anchor when server-authored, still garbage when LLM-authored. (A bare "}" with
        // NO line break is single-line punctuation and stays bounced for both — see
        // BarePunctuation_AlwaysBounced_EvenForDeterministic.)
        Assert.True(ShouldBounceGarbageAnchor(anchor, isDeterministic: false));
        Assert.False(ShouldBounceGarbageAnchor(anchor, isDeterministic: true));
    }

    [Theory]
    [InlineData("    musicTodoCount: number | null = null;\n}")]
    [InlineData("    return Ok();\n}")]
    [InlineData("public class Foo\n{\n}")]
    public void RealCodeFirst_NotBounced(string anchor)
    {
        Assert.False(ShouldBounceGarbageAnchor(anchor, isDeterministic: false));
        Assert.False(ShouldBounceGarbageAnchor(anchor, isDeterministic: true));
    }

    [Fact]
    public void NullOrBlank_NotBounced()
    {
        Assert.False(ShouldBounceGarbageAnchor(null, isDeterministic: false));
        Assert.False(ShouldBounceGarbageAnchor("", isDeterministic: false));
        Assert.False(ShouldBounceGarbageAnchor("   ", isDeterministic: true));
    }
}
