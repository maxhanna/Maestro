using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks in LlmCssCleaner.Clean()'s promise: it repairs LLM squishes (all0.2s,
/// width:40px, split hex) but must NEVER corrupt valid CSS outside the edited
/// region. Regression for a real bug where the type selector `h4` was rewritten
/// to `h 4` (and `a:hover` to `a: hover`, `#000` to `#0 0`, `a:hover,` to `a;`)
/// purely by running the cleaner.
/// </summary>
public class LlmCssCleanerTests
{
    // ── Valid CSS must pass through untouched ────────────────────────────────

    [Fact]
    public void TypeSelector_WithDigit_IsNotSplit()
    {
        var css = ".difficulty-group h4 {\n    margin: 8px 0;\n}";
        Assert.Equal(css, LlmCssCleaner.Clean(css));
    }

    [Fact]
    public void PseudoClassSelector_IsNotRewritten()
    {
        var css = "a:hover {\n    color: red;\n}";
        Assert.Equal(css, LlmCssCleaner.Clean(css));
    }

    [Fact]
    public void PseudoElementSelector_IsNotRewritten()
    {
        var css = "a::before {\n    content: \"x\";\n}";
        Assert.Equal(css, LlmCssCleaner.Clean(css));
    }

    [Fact]
    public void HexColor_AllZeros_IsNotCorrupted()
    {
        var css = "color: #000;";
        Assert.Equal(css, LlmCssCleaner.Clean(css));
        var rgba = "color: #00000080;";
        Assert.Equal(rgba, LlmCssCleaner.Clean(rgba));
    }

    [Fact]
    public void HexColor_ZeroNotAdjacentToHash_IsNotCorrupted()
    {
        // Regression: the zero-run in these is NOT preceded by '#' — it follows a
        // letter (f/8). The cleaner must still leave the color intact.
        Assert.Equal("color: #f00;", LlmCssCleaner.Clean("color: #f00;"));
        Assert.Equal("color: #ff0000;", LlmCssCleaner.Clean("color: #ff0000;"));
        Assert.Equal("color: #800000;", LlmCssCleaner.Clean("color: #800000;"));
        Assert.Equal("color: #f0f0f0;", LlmCssCleaner.Clean("color: #f0f0f0;"));
    }

    [Fact]
    public void NumberWithEmbeddedZeros_IsNotCorrupted()
    {
        // z-index: 1000 — the zero-run is preceded by a digit, not whitespace/colon.
        Assert.Equal("z-index: 1000;", LlmCssCleaner.Clean("z-index: 1000;"));
    }

    [Fact]
    public void KeyframesName_AndAnimationName_WithTimeSuffix_AreNotSplit()
    {
        // A duration-suffixed identifier is a NAME, not a squished keyword-number.
        var keyframes = "@keyframes spin1s {\n    from { transform: rotate(0); }\n}";
        Assert.Equal(keyframes, LlmCssCleaner.Clean(keyframes));
        Assert.Equal("animation-name: pulse1s;", LlmCssCleaner.Clean("animation-name: pulse1s;"));
    }

    [Fact]
    public void MultiLineSelectorList_WithTrailingCommas_IsNotCorrupted()
    {
        var css = "a:hover,\na:focus,\nbutton {\n    color: red;\n}";
        Assert.Equal(css, LlmCssCleaner.Clean(css));
    }

    [Fact]
    public void ClassSelector_DigitsInName_IsNotSplit()
    {
        var css = ".item2 {\n    margin: 4px;\n}";
        Assert.Equal(css, LlmCssCleaner.Clean(css));
    }

    [Fact]
    public void DeclarationValue_LetterAfterColon_IsNotRewritten()
    {
        var css = "transition: all 0.2s;";
        Assert.Equal(css, LlmCssCleaner.Clean(css));
    }

    // ── Legit LLM-squish repairs must still happen ──────────────────────────

    [Fact]
    public void SquishedTransitionKeyword_StillRepaired()
    {
        Assert.Equal("transition: all 0.2s;", LlmCssCleaner.Clean("transition: all0.2s;"));
        Assert.Equal("transition: all 0.2s ease;", LlmCssCleaner.Clean("transition: all0.2s ease;"));
        Assert.Equal("transition: ease-in-out 0.4s;", LlmCssCleaner.Clean("transition: ease-in-out0.4s;"));
    }

    [Fact]
    public void MissingSpaceAfterColon_StillRepaired()
    {
        Assert.Equal("width: 40px;", LlmCssCleaner.Clean("width:40px;"));
    }

    [Fact]
    public void SquishedUnits_StillRepaired()
    {
        Assert.Equal("padding: 6px 14px;", LlmCssCleaner.Clean("padding: 6px14px;"));
    }

    [Fact]
    public void TrailingComma_OnDeclaration_StillRepaired_AndValueKept()
    {
        Assert.Equal("width: 40px;", LlmCssCleaner.Clean("width:40px,"));
    }

    [Fact]
    public void SplitHex_StillMerged()
    {
        Assert.Equal("color: #abcdef;", LlmCssCleaner.Clean("color: #ab cd ef;"));
    }
}
