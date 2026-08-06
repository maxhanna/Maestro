using System.Reflection;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Deterministic tests for <see cref="CodeFormatterService"/> that never spawn an
/// external formatter binary (prettier/clang-format/gofmt/…). Covers the pure
/// extension gate and the guard clauses of <see cref="CodeFormatterService.FormatAsync"/>:
/// unsupported extensions and empty input must pass through untouched. The .cs path
/// is exercised via the in-process Roslyn formatter (no external process).
/// </summary>
public class CodeFormatterServiceTests
{
    // ── CanFormat — pure extension gate ──────────────────────────────────────

    [Theory]
    [InlineData("foo.ts", true)]
    [InlineData("foo.tsx", true)]
    [InlineData("foo.js", true)]
    [InlineData("foo.css", true)]
    [InlineData("foo.scss", true)]
    [InlineData("foo.cs", true)]
    [InlineData("foo.py", true)]
    [InlineData("foo.go", true)]
    [InlineData("foo.rs", true)]
    [InlineData("foo.html", true)]
    [InlineData("foo.json", true)]
    [InlineData("foo.xyz", false)]
    [InlineData("README", false)]
    [InlineData("Makefile", false)]
    public void CanFormat_ByExtension(string path, bool expected)
    {
        Assert.Equal(expected, CodeFormatterService.CanFormat(path));
    }

    [Fact]
    public void CanFormat_IsCaseInsensitive()
    {
        Assert.True(CodeFormatterService.CanFormat("FOO.TS"));
        Assert.True(CodeFormatterService.CanFormat("Foo.Css"));
    }

    // ── FormatAsync guard clauses (no binary spawn) ──────────────────────────

    [Fact]
    public async Task FormatAsync_UnsupportedExtension_ReturnsContentUnchanged()
    {
        var content = "some plain content that must not be touched";
        var result = await CodeFormatterService.FormatAsync("notes.txt", content);

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task FormatAsync_EmptyContent_ReturnsUnchanged()
    {
        var result = await CodeFormatterService.FormatAsync("foo.ts", "");

        Assert.Equal("", result);
    }

    // ── .cs path — in-process Roslyn, deterministic ──────────────────────────

    [Fact]
    public async Task FormatAsync_CSharp_FormatsViaInProcessRoslyn()
    {
        var input = "public class A{public int X(){return 1;}}";
        var result = await CodeFormatterService.FormatAsync("foo.cs", input);

        Assert.NotEqual(input, result);
        Assert.Contains("public class A", result);
        Assert.Contains("public int X", result);
    }

    // ── FixCssSpacing — private post-format pass, reached via reflection ─────
    // It only runs after a prettier spawn in production, which we never trigger in
    // tests — so invoke the private method directly to lock in the spacing rule.

    private static string InvokeFixCssSpacing(string content)
    {
        var method = typeof(CodeFormatterService).GetMethod(
            "FixCssSpacing", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FixCssSpacing not found");
        return (string)method.Invoke(null, new object[] { content })!;
    }

    [Theory]
    [InlineData("padding: 6px14px;", "padding: 6px 14px;")]
    [InlineData("margin: 10px20px 0;", "margin: 10px 20px 0;")]
    [InlineData("width: 1.5rem2rem;", "width: 1.5rem 2rem;")]
    [InlineData("font-size: 12px; line-height: 1.5;", "font-size: 12px; line-height: 1.5;")]
    [InlineData("top: 5vh10vh;", "top: 5vh 10vh;")]
    public void FixCssSpacing_SeparatesSquishedUnits_WithoutTouchingValidCss(string input, string expected)
    {
        Assert.Equal(expected, InvokeFixCssSpacing(input));
    }
}
