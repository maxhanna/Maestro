using System.Reflection;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the fix for the "Search the web for an interesting and relevant AI article…" run:
/// the _web_search step executed successfully, but its results were never fed back into the
/// next thinking round's context, so the model re-invented a Selenium scraper instead of
/// using what the search returned. ExecuteWebPlanStep accumulates results into a local webCtx
/// that is only flushed via ReplanRemainingSteps when the SAME plan has further steps — in the
/// interleaved loop each step runs as its own single-step plan, so the output evaporated.
/// AppendWebResultsToDiscoveryContext now harvests _web_search/_web_fetch outputs from step
/// results into the discovery context, and ExtractWebResultSectionsForThinking pulls them back
/// out for the deep pre-plan reasoning engine (which otherwise only sees file sections).
/// </summary>
public class WebResultsIntoContextTests
{
    private static readonly MethodInfo AppendMethod = typeof(AgentController).GetMethod(
        "AppendWebResultsToDiscoveryContext", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ExtractMethod = typeof(AgentController).GetMethod(
        "ExtractWebResultSectionsForThinking", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string Append(string ctx, List<Dictionary<string, object?>> results)
        => (string)AppendMethod.Invoke(null, new object?[] { ctx, results, 20000, 60000 })!;

    private static string Extract(string ctx)
        => (string)ExtractMethod.Invoke(null, new object[] { ctx })!;

    private static Dictionary<string, object?> WebSearchResult(string query, string output)
        => new()
        {
            ["type"] = "_web_search",
            ["query"] = query,
            ["status"] = "done",
            ["output"] = output
        };

    [Fact]
    public void WebSearchOutput_IsAppendedToDiscoveryContext()
    {
        var output = "## Summary\nAI research breakthroughs this month include…\n## Results\n  - Article one (https://example.com/1)\n  - Article two (https://example.com/2)";
        var updated = Append("", new List<Dictionary<string, object?>> { WebSearchResult("AI breakthroughs", output) });
        Assert.Contains("### WEB RESULTS [AI breakthroughs] ###", updated);
        Assert.Contains("Article one", updated);
        Assert.Contains("https://example.com/1", updated);
    }

    [Fact]
    public void WebFetchOutput_IsAppendedWithUrlLabel()
    {
        var updated = Append("", new List<Dictionary<string, object?>>
        {
            new()
            {
                ["type"] = "_web_fetch",
                ["url"] = "https://example.com/article",
                ["status"] = "done",
                ["output"] = "HTTP 200\nA very long article body about neural networks that exceeds the 80-char minimum threshold for inclusion."
            }
        });
        Assert.Contains("### WEB RESULTS [https://example.com/article] ###", updated);
        Assert.Contains("neural networks", updated);
    }

    [Fact]
    public void NonWebResults_AreIgnored()
    {
        var results = new List<Dictionary<string, object?>>
        {
            new() { ["type"] = "edit", ["status"] = "done", ["path"] = "src/a.ts", ["newStringPreview"] = "x" },
            new() { ["type"] = "create", ["status"] = "done", ["path"] = "readme.md" }
        };
        var updated = Append("### read existing.md\n```\nkeep me\n```\n", results);
        Assert.DoesNotContain("WEB RESULTS", updated);
        Assert.Contains("keep me", updated);
    }

    [Fact]
    public void ShortOrEmptyOutput_IsNotAppended()
    {
        var updated = Append("base", new List<Dictionary<string, object?>> { WebSearchResult("q", "tiny") });
        Assert.Equal("base", updated);
    }

    [Fact]
    public void ExistingContext_IsPreservedAndExtended()
    {
        var baseCtx = "### read a.cs\n```\ncode\n```\n";
        var updated = Append(baseCtx, new List<Dictionary<string, object?>> { WebSearchResult("q", new string('r', 300)) });
        Assert.StartsWith(baseCtx, updated);
        Assert.Contains("### WEB RESULTS [q] ###", updated);
    }

    [Fact]
    public void FailedWebStep_WithNonEmptyErrorText_IsNotAppended()
    {
        // A failed search returns ("", errorMessage) so output is empty — but if a future
        // caller ever puts text on an error result, it must NOT be surfaced as web data.
        var results = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["type"] = "_web_search",
                ["query"] = "q",
                ["status"] = "error",
                ["error"] = "timeout",
                ["output"] = new string('e', 300)
            }
        };
        Assert.Equal("base", Append("base", results));
    }

    [Fact]
    public void OversizedOutput_IsCappedAtTwentyThousandChars()
    {
        var big = new string('x', 30000);
        var updated = Append("", new List<Dictionary<string, object?>> { WebSearchResult("q", big) });
        Assert.Contains("…", updated);
        Assert.Contains("### WEB RESULTS [q] ###", updated);
        // 20000 chars + header; the 30000-char body must be truncated.
        Assert.True(updated.Length < 21000, $"Expected capped section, got {updated.Length} chars");
    }

    [Fact]
    public void MultipleWebSteps_EachBecomeTheirOwnSection()
    {
        var updated = Append("", new List<Dictionary<string, object?>>
        {
            WebSearchResult("first", new string('a', 200)),
            WebSearchResult("second", new string('b', 200))
        });
        Assert.Contains("### WEB RESULTS [first] ###", updated);
        Assert.Contains("### WEB RESULTS [second] ###", updated);
    }

    [Fact]
    public void ExtractWebResultSections_ReturnsOnlyWebBlocks()
    {
        var ctx = "### read file.cs\n```\ncode\n```\n\n### WEB RESULTS [AI] ###\nSearch summary here\n\n### WEB RESULTS [other] ###\nMore results\n";
        var extracted = Extract(ctx);
        Assert.Contains("Search summary here", extracted);
        Assert.Contains("More results", extracted);
        // File sections must NOT leak into the web extraction.
        Assert.DoesNotContain("code\n", extracted);
    }

    [Fact]
    public void ExtractWebResultSections_EmptyContext_ReturnsEmpty()
    {
        Assert.Equal("", Extract(""));
        Assert.Equal("", Extract("### read a.cs\n```\ncode\n```\n"));
    }

    [Fact]
    public void ExtractWebResultSections_StopsAtFileSectionNotBareMarkdownHeading()
    {
        // Web content containing its own "### " markdown heading must NOT truncate the section
        // (a bare "(?=\n### )" lookahead would). Only real section boundaries stop the match.
        var ctx = "### WEB RESULTS [q] ###\nSummary line\n### Nested heading inside article\nMore content\n\n### read after.cs\n```\ncode\n```\n";
        var extracted = Extract(ctx);
        Assert.Contains("Nested heading inside article", extracted);
        Assert.Contains("More content", extracted);
        Assert.DoesNotContain("### read after.cs", extracted);
        Assert.DoesNotContain("code\n", extracted);
    }

    [Fact]
    public void ExtractWebResultSections_QueryContainingClosingBracket_StillMatches()
    {
        var ctx = "### WEB RESULTS [C# [8.0] features] ###\nSome content about C# 8.0\n";
        var extracted = Extract(ctx);
        Assert.Contains("Some content about C# 8.0", extracted);
    }
}
