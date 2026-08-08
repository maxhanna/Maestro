using System.Reflection;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the client-side web results fix: a _web_search/_web_fetch step's output was sent to
/// the browser in full over SSE. A _web_fetch returns an entire page's text after tag-stripping
/// (potentially megabytes), which bloated the step card and could choke JSON parsing. The
/// backend now caps web step output before it reaches the client (CapWebStepOutputForClient),
/// while the agent's own context path (AppendWebResultsToDiscoveryContext) still receives the
/// full output. The frontend renders the capped output in a collapsible '🌐 Web results' block
/// on the step card, flagged as truncated when the cap fired.
/// </summary>
public class WebResultsUiTests
{
    private static readonly MethodInfo CapMethod = typeof(AgentController).GetMethod(
        "CapWebStepOutputForClient", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo AppendMethod = typeof(AgentController).GetMethod(
        "AppendWebResultsToDiscoveryContext", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo HarvestMethod = typeof(AgentController).GetMethod(
        "HarvestWebResultsForEditContext", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static (string capped, bool truncated) Cap(string? output)
    {
        var result = ((string, bool))CapMethod.Invoke(null, new object[] { output })!;
        return result;
    }

    private static string Append(string ctx, List<Dictionary<string, object?>> results)
        => (string)AppendMethod.Invoke(null, new object[] { ctx, results })!;

    private static string Harvest(List<Dictionary<string, object?>> results)
        => (string)HarvestMethod.Invoke(null, new object[] { results })!;

    [Fact]
    public void ShortOutput_PassesThroughUntruncated()
    {
        var output = "## Summary\nAI article title\nSource: https://example.com/a";
        var (capped, truncated) = Cap(output);
        Assert.Equal(output, capped);
        Assert.False(truncated);
    }

    [Fact]
    public void LongOutput_IsCappedAndFlaggedTruncated()
    {
        var output = new string('x', 25000);
        var (capped, truncated) = Cap(output);
        Assert.True(truncated);
        Assert.True(capped.Length <= 12500, "capped output should stay near the cap");
        Assert.Contains("truncated", capped);
        // The full text must not leak to the client.
        Assert.DoesNotContain(new string('x', 20000), capped);
    }

    [Fact]
    public void NullAndEmptyOutput_ReturnEmptyNotTruncated()
    {
        var (capped1, t1) = Cap(null);
        Assert.Equal("", capped1);
        Assert.False(t1);
        var (capped2, t2) = Cap("");
        Assert.Equal("", capped2);
        Assert.False(t2);
    }

    [Fact]
    public void CapBoundary_ExactLimitIsNotTruncated()
    {
        // 12,000 chars is the exact limit — must pass through untouched.
        var output = new string('y', 12000);
        var (capped, truncated) = Cap(output);
        Assert.Equal(output, capped);
        Assert.False(truncated);
    }

    [Fact]
    public void CapBoundary_OnePastLimitIsTruncated()
    {
        var output = new string('z', 12001);
        var (capped, truncated) = Cap(output);
        Assert.True(truncated);
        Assert.Contains("truncated", capped);
    }

    [Fact]
    public void FullOutput_StillReachesTheAgentsContextDespiteClientCap()
    {
        // Regression lock: the client SSE payload is capped at 12k, but allResults must keep the
        // FULL output — AppendWebResultsToDiscoveryContext re-harvests it for the interleaved
        // loop's thinking context (the "web results into thinking" feature). Capping allResults
        // too would starve the model of a fetched article's body.
        var longArticle = "HTTP 200\n" + string.Concat(Enumerable.Repeat("The agent needs every sentence of this fetched article body. ", 600));
        Assert.True(longArticle.Length > 12000, "test fixture must exceed the client cap");

        var (_, truncated) = Cap(longArticle);
        Assert.True(truncated);

        var updated = Append("", new List<Dictionary<string, object?>>
        {
            new()
            {
                ["type"] = "_web_fetch",
                ["url"] = "https://example.com/article",
                ["status"] = "done",
                ["output"] = longArticle
            }
        });
        // Content well past the 12k client cap must still be present in the context.
        Assert.Contains("### WEB RESULTS [https://example.com/article] ###", updated);
        Assert.Contains(longArticle[^200..], updated);
    }

    [Fact]
    public void Harvest_ReturnsDoneWebResultsForEditContext()
    {
        var harvested = Harvest(new List<Dictionary<string, object?>>
        {
            new()
            {
                ["type"] = "_web_search",
                ["query"] = "interesting AI article",
                ["status"] = "done",
                ["output"] = "## Results\n  - Attention Is All You Need (https://arxiv.org/abs/1706.03762)\n  - Deep learning survey (https://example.com/dl)\n"
            }
        });
        Assert.Contains("### WEB RESULTS [interesting AI article] ###", harvested);
        Assert.Contains("https://arxiv.org/abs/1706.03762", harvested);
    }

    [Fact]
    public void Harvest_ReturnsEmptyForErrorWebResults()
    {
        // An errored search carries an exception message, not web data — must never leak into
        // the edit prompt as if it were fetched results.
        var harvested = Harvest(new List<Dictionary<string, object?>>
        {
            new()
            {
                ["type"] = "_web_search",
                ["query"] = "AI article",
                ["status"] = "error",
                ["output"] = "Something timed out"
            }
        });
        Assert.Equal("", harvested);
    }

    [Fact]
    public void Harvest_ReturnsEmptyWhenNoWebResultsExist()
    {
        var harvested = Harvest(new List<Dictionary<string, object?>>
        {
            new() { ["type"] = "edit", ["status"] = "done", ["path"] = "foo.cs" }
        });
        Assert.Equal("", harvested);
    }
}
