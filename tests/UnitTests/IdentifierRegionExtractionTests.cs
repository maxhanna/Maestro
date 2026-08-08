using System.Text.RegularExpressions;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks in AgentDiscovery.ExtractIdentifierRegions — the focused discovery read:
/// when an identifier from the task prompt matches inside a LARGE file, only the
/// enclosing method/class/block around each match (with line numbers) is surfaced
/// instead of the whole file. Brace languages get brace-balanced regions,
/// indentation languages get their def/block scope, others get a window.
/// </summary>
public class IdentifierRegionExtractionTests
{
    private const string CsFile = @"using System;

public class Calculator
{
    public int Add(int a, int b)
    {
        var result = a + b;
        return result;
    }
}
";

    // ── Brace languages ─────────────────────────────────────────────────────

    [Fact]
    public void BraceLanguage_MethodBodyIdentifier_ExtractsEnclosingMethod()
    {
        var region = AgentDiscovery.ExtractIdentifierRegions(CsFile, new List<string> { "result" }, ".cs");

        Assert.Contains("public int Add(int a, int b)", region); // signature above the '{'
        Assert.Contains("var result = a + b;", region);
        Assert.Contains("return result;", region);
        Assert.Contains("// ▼ 'result' — lines 5–9", region);     // 1-based line numbers
        // The class's closing brace must NOT be included — the method is the unit.
        Assert.DoesNotContain("public class Calculator", region);
        Assert.Single(Regex.Matches(region, "▼"));
    }

    [Fact]
    public void BraceLanguage_DeclarationLineIdentifier_SpansOwningBlock()
    {
        var region = AgentDiscovery.ExtractIdentifierRegions(CsFile, new List<string> { "Add" }, ".cs");

        // 'Add' is the declaration line — the region covers the whole method through its close brace.
        Assert.Contains("public int Add(int a, int b)", region);
        Assert.Contains("return result;", region);
        Assert.Contains("}", region);
    }

    [Fact]
    public void BraceLanguage_CrlfInput_NormalizesLineEndings()
    {
        var region = AgentDiscovery.ExtractIdentifierRegions(
            CsFile.Replace("\n", "\r\n"), new List<string> { "result" }, ".cs");

        Assert.Contains("public int Add(int a, int b)", region);
        Assert.Contains("// ▼ 'result' — lines 5–9", region);
    }

    [Fact]
    public void BraceLanguage_TwoIdentifiersInSeparateMethods_ProducesTwoOrderedRegions()
    {
        var content = @"using System;

public class Service
{
    public void First()
    {
        alpha();
    }

    public void Second()
    {
        beta();
    }
}
";
        var region = AgentDiscovery.ExtractIdentifierRegions(
            content, new List<string> { "beta", "alpha" }, ".cs");

        Assert.Equal(2, Regex.Matches(region, "▼").Count);
        // Regions are ordered by line, not by the input identifier order.
        Assert.True(region.IndexOf("'alpha'", StringComparison.Ordinal) < region.IndexOf("'beta'", StringComparison.Ordinal));
        Assert.Contains("First", region);
        Assert.Contains("Second", region);
    }

    [Fact]
    public void BraceLanguage_TwoIdentifiersInSameMethod_ProducesOneRegion()
    {
        var content = @"public class S
{
    public void Go()
    {
        var alpha = 1;
        var beta = alpha + 1;
    }
}
";
        var region = AgentDiscovery.ExtractIdentifierRegions(
            content, new List<string> { "alpha", "beta" }, ".cs");

        Assert.Single(Regex.Matches(region, "▼"));
        Assert.Contains("Go", region);
    }

    [Fact]
    public void BraceLanguage_IdentifierInsideIf_ClimbsToDefiningMethod()
    {
        var content = @"public void Foo()
{
    if (x)
    {
        result();
    }
}
";
        var region = AgentDiscovery.ExtractIdentifierRegions(content, new List<string> { "result" }, ".cs");

        // The innermost block is the if — the region must climb to the method that
        // defines the identifier's context.
        Assert.Contains("public void Foo()", region);
        Assert.Contains("if (x)", region);
        Assert.Contains("result();", region);
        Assert.Contains("// ▼ 'result' — lines 1–7", region);
    }

    [Fact]
    public void BraceLanguage_BraceInsideString_DoesNotFakeCloseRegion()
    {
        var content = @"public void Format()
{
    var s = ""}"" ;
    or_this();
}
";
        var region = AgentDiscovery.ExtractIdentifierRegions(content, new List<string> { "or_this" }, ".cs");

        // The "}" inside the string must not truncate the method early.
        Assert.Contains("public void Format()", region);
        Assert.Contains("or_this()", region);
    }

    // ── Indentation languages ───────────────────────────────────────────────

    [Fact]
    public void IndentLanguage_PythonDef_SpawnsWholeDefScope()
    {
        var py = @"class Calculator:
    def add(self, a, b):
        result = a + b
        return result
";
        var region = AgentDiscovery.ExtractIdentifierRegions(py, new List<string> { "result" }, ".py");

        Assert.Contains("def add", region);
        Assert.Contains("result = a + b", region);
        Assert.Contains("return result", region);
        Assert.DoesNotContain("class Calculator", region); // scope ends at the def
        Assert.Contains("// ▼ 'result' — lines 2–4", region);
    }

    // ── Fallback window (html/css/json/unknown) ─────────────────────────────

    [Fact]
    public void NonCodeLanguage_WindowAroundMatch()
    {
        var html = "<div>\n  <span>or_this</span>\n</div>\n";
        var region = AgentDiscovery.ExtractIdentifierRegions(html, new List<string> { "or_this" }, ".html");

        Assert.Contains("or_this", region);
        // 1-based window around the match line, clamped to the content bounds.
        Assert.Contains("// ▼ 'or_this' — lines 1–4", region);
    }

    [Fact]
    public void NoIdentifierMatch_ReturnsEmpty()
    {
        Assert.Equal("", AgentDiscovery.ExtractIdentifierRegions(CsFile, new List<string> { "nope_nothing" }, ".cs"));
        Assert.Equal("", AgentDiscovery.ExtractIdentifierRegions(CsFile, new List<string>(), ".cs"));
        Assert.Equal("", AgentDiscovery.ExtractIdentifierRegions("", new List<string> { "x_y" }, ".cs"));
    }

    [Fact]
    public void RegionLength_IsCappedPerRegion()
    {
        var big = new string('x', 100) + " or_this\n" + string.Concat(Enumerable.Repeat("y", 5000)) + "\n";
        var region = AgentDiscovery.ExtractIdentifierRegions(big, new List<string> { "or_this" }, ".cs", maxCharsPerRegion: 100);

        Assert.Contains("(region truncated)", region);
        // The region text itself (excluding the header) stays under the cap (100 + marker).
        var body = region[(region.IndexOf('\n') + 1)..];
        Assert.True(body.Length <= 130, $"expected capped region, got {body.Length} chars");
    }

    [Fact]
    public void CaseInsensitiveMatch_StillFocused()
    {
        var content = "public void Go()\n{\n    var Or_This = 1;\n}\n";
        var region = AgentDiscovery.ExtractIdentifierRegions(content, new List<string> { "or_this" }, ".cs");

        Assert.Contains("Or_This", region);
    }

    // ── Section lookup tolerates the focused suffix ──────────────────────────

    [Fact]
    public void ExtractFileSectionFromContext_MatchesFocusedHeader()
    {
        // Sections run until the NEXT "### " header — realistic discovery context
        // always has following sections, so the focused section is findable and
        // its body (the region) is handed to downstream consumers.
        var ctx = "ONLY use paths that appear below.\n\n" +
                  "### read src/BigFile.cs (focused: or_this; full file via _explore)\n```\n" +
                  "// ▼ 'or_this' — lines 12–40\npublic void or_this() { }\n```\n" +
                  "### read src/Other.cs\n```\npublic void other() { }\n```\n";

        var section = AgentDiscovery.ExtractFileSectionFromContext(ctx, "src/BigFile.cs");

        Assert.NotNull(section);
        Assert.Contains("or_this()", section);
        Assert.Contains("focused", section); // the suffix survives in the returned section
        Assert.DoesNotContain("other()", section); // stops at the next section
    }

    [Fact]
    public void ExtractFileSectionFromContext_PlainHeader_StillMatches()
    {
        var ctx = "### read src/Small.cs\n```\npublic void x() { }\n```\n" +
                  "### read src/Next.cs\n```\npublic void n() { }\n```\n";

        var section = AgentDiscovery.ExtractFileSectionFromContext(ctx, "src/Small.cs");

        Assert.NotNull(section);
        Assert.Contains("public void x()", section);
        Assert.DoesNotContain("n()", section);
    }
}
