using System.Reflection;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// MEASUREMENT: how harvested web results shrink as they pass between planning steps, and
/// WHERE the truncation actually happens. Drives the REAL pipeline functions with a
/// realistic big fetch (a 1.2 MB tag-stripped article — typical of a long web page) and
/// records the size at every stage:
///
///   Stage 0  raw result in allResults      — FULL output, uncapped (the agent's memory)
///   Stage 1  client SSE (CapWebStepOutputForClient)          — 12,000-char display cap
///   Stage 2  discovery context (AppendWebResultsToDiscoveryContext) — 20,000-char PER-SECTION
///                                                               cap + shared 60k TOTAL budget
///   Stage 3  planner prompt (BuildPlannerDiscoveryContext)    — web sections passed VERBATIM
///   Stage 4  thinking prompt (ExtractWebResultSectionsForThinking) — web sections verbatim
///   Stage 5  edit-resolution (HarvestWebResultsForEditContext)     — LONGER 60k/section
///                                                               ON-DEMAND prefix from allResults
///
/// Conclusion locked by the assertions: the discovery context (what the planner/thinking
/// see) keeps a compact 20k-per-section budget under a shared 60k total — that's where the
/// decision-critical result URLs live — while the edit-resolution, the prompt that
/// actually generates file content, pulls a ~3x longer prefix straight from allResults on
/// demand. allResults always keeps the FULL output, so nothing is permanently lost. The
/// categorical drops (≤80-char outputs, failed fetches) and the 12k display-only client
/// cap are unchanged.
/// </summary>
public class WebResultsContextShrinkageTests
{
    private readonly ITestOutputHelper _out;

    public WebResultsContextShrinkageTests(ITestOutputHelper output) => _out = output;

    private static readonly MethodInfo CapForClient = typeof(AgentController).GetMethod(
        "CapWebStepOutputForClient", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("CapWebStepOutputForClient not found");
    private static readonly MethodInfo AppendWebResults = typeof(AgentController).GetMethod(
        "AppendWebResultsToDiscoveryContext", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AppendWebResultsToDiscoveryContext not found");
    private static readonly MethodInfo BuildPlannerCtx = typeof(AgentController).GetMethod(
        "BuildPlannerDiscoveryContext", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildPlannerDiscoveryContext not found");
    private static readonly MethodInfo ExtractForThinking = typeof(AgentController).GetMethod(
        "ExtractWebResultSectionsForThinking", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ExtractWebResultSectionsForThinking not found");
    private static readonly MethodInfo HarvestForEdit = typeof(AgentController).GetMethod(
        "HarvestWebResultsForEditContext", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("HarvestWebResultsForEditContext not found");

    private static (string capped, bool truncated) CapClient(string? output)
        => ((ValueTuple<string, bool>)CapForClient.Invoke(null, new object?[] { output })!);

    private static string ToDiscovery(string ctx, List<Dictionary<string, object?>> results)
        => (string)AppendWebResults.Invoke(null, new object?[] { ctx, results, 20000, 60000 })!;

    private static string ToPlanner(string discovery)
        => (string)BuildPlannerCtx.Invoke(null, new object?[] { discovery })!;

    private static string WebForThinking(string discovery)
        => (string)ExtractForThinking.Invoke(null, new object?[] { discovery })!;

    private static string ForEditResolution(List<Dictionary<string, object?>> results)
        => (string)HarvestForEdit.Invoke(null, new object?[] { results })!;

    private static Dictionary<string, object?> SearchResult(string query, string output) => new()
    {
        ["type"] = "_web_search",
        ["status"] = "done",
        ["query"] = query,
        ["output"] = output
    };

    private static Dictionary<string, object?> FetchResult(string url, string output) => new()
    {
        ["type"] = "_web_fetch",
        ["status"] = "done",
        ["url"] = url,
        ["output"] = output
    };

    // ── Scenario A: the real user case — a big fetched article + a search digest ───────

    [Fact]
    public void BigFetch_ShrinkageTable_AndCapsHold()
    {
        var search = SearchResult("AI research breakthroughs latest",
            "A survey of recent AI research breakthroughs covering large language models, " +
            "multimodal systems and protein-folding advances published this quarter. " +
            "AlphaFold 3 predicts protein structures with atom-level accuracy. " +
            "A new open-weight LLM benchmarks above GPT-4 on reasoning tasks.");
        // A realistic tag-stripped article page: ~1.2 MB of prose. The head (30k x's) sits
        // before the 20k boundary; the tail (1.17M y's) is what must NOT survive the cap —
        // distinct characters make the cut provable.
        var fetch = FetchResult("https://example.com/alphafold3",
            "HTTP 200\n" + new string('x', 30_000) + new string('y', 1_170_000));
        var results = new List<Dictionary<string, object?>> { search, fetch };

        var rawSearch = search["output"]!.ToString()!.Length;
        var rawFetch = fetch["output"]!.ToString()!.Length;
        var rawTotal = rawSearch + rawFetch;

        var (clientSearch, _) = CapClient(search["output"]!.ToString());
        var (clientFetch, clientFetchTruncated) = CapClient(fetch["output"]!.ToString());
        var clientTotal = clientSearch.Length + clientFetch.Length;

        var discovery = ToDiscovery("", results);
        var planner = ToPlanner(discovery);
        var thinking = WebForThinking(discovery);
        var edit = ForEditResolution(results);

        // The client cap is display-only and must never be the agent's view.
        Assert.True(clientFetchTruncated);
        Assert.True(clientFetch.Length <= 12_000 + 80, $"client cap violated: {clientFetch.Length}");

        // Stage 2: the fetch section survives with its FIRST 20k chars (compact discovery
        // budget) — content past 20k (a y-run) is absent from the discovery context and from
        // the planner/thinking passes, which carry it verbatim.
        var fetchHead = fetch["output"]!.ToString()![..20_000];
        var yRun = new string('y', 100);
        Assert.Contains(fetchHead, discovery, StringComparison.Ordinal);
        Assert.DoesNotContain(yRun, discovery, StringComparison.Ordinal);
        foreach (var stage in new[] { planner, thinking })
        {
            Assert.Contains("### WEB RESULTS [https://example.com/alphafold3] ###", stage);
            Assert.Contains(fetchHead, stage, StringComparison.Ordinal);
            Assert.DoesNotContain(yRun, stage, StringComparison.Ordinal);
            Assert.Contains("AlphaFold 3 predicts protein structures", stage); // search digest too
        }

        // Stage 5 — the EDIT-RESOLUTION is the ON-DEMAND consumer: it pulls a LONGER 60k
        // prefix straight from allResults, so a y-run (which lives between 20k and 60k of
        // the fetch) IS present there even though the compact consumers never see it.
        Assert.Contains("### WEB RESULTS [https://example.com/alphafold3] ###", edit);
        Assert.Contains(yRun, edit, StringComparison.Ordinal);
        Assert.Contains(new string('x', 25_000), edit, StringComparison.Ordinal); // x's past 20k
        Assert.Contains("AlphaFold 3 predicts protein structures", edit);

        // Stage 0 — allResults ALWAYS keeps the full uncapped output (the longer prefix is
        // derived from it on demand; nothing is permanently lost).
        Assert.Equal(fetch["output"]!.ToString()!.Length, rawFetch);
        Assert.Contains(new string('y', 500_000), fetch["output"]!.ToString());

        _out.WriteLine("=== Web-results shrinkage: 1.2 MB fetched article + 0.3 kB search digest ===");
        _out.WriteLine($"  Stage 0  allResults (raw, uncapped)      : {rawTotal,10:N0} chars  (search {rawSearch:N0} + fetch {rawFetch:N0}) — FULL output always kept");
        _out.WriteLine($"  Stage 1  client SSE display cap          : {clientTotal,10:N0} chars  (12,000 cap — display only, agent context unaffected)");
        _out.WriteLine($"  Stage 2  discovery context (20k/sec, 60k total): {discovery.Length,10:N0} chars  survival {(100.0 * discovery.Length / rawTotal):F2}%");
        _out.WriteLine($"  Stage 3  planner prompt (verbatim pass)  : {planner.Length,10:N0} chars  (web sections unchanged, 0% further loss)");
        _out.WriteLine($"  Stage 4  thinking prompt (verbatim)      : {thinking.Length,10:N0} chars  (0% further loss)");
        _out.WriteLine($"  Stage 5  edit-resolution (60k ON DEMAND) : {edit.Length,10:N0} chars  — ~3x the discovery prefix, pulled from allResults");
        _out.WriteLine($"  → Compact budget feeds the planner/thinking (20k/section); the edit-resolution gets the longer on-demand prefix; the full output survives in allResults.");
    }

    // ── Scenario B: categorical drops — small and failed outputs never reach context ────

    [Fact]
    public void SmallOutputs_DroppedFromContextEntirely()
    {
        // A done fetch whose output is ≤ 80 chars is EXCLUDED from the discovery context —
        // a categorical loss, not a size-based truncation.
        var tiny = FetchResult("https://example.com/small", "HTTP 200\njust a tiny body");
        Assert.True(tiny["output"]!.ToString()!.Length <= 80);

        var discovery = ToDiscovery("", new List<Dictionary<string, object?>> { tiny });
        Assert.DoesNotContain("### WEB RESULTS", discovery);

        // A failed fetch (status error) is excluded even with a long message.
        var failed = new Dictionary<string, object?>
        {
            ["type"] = "_web_fetch",
            ["status"] = "error",
            ["url"] = "https://example.com/broken",
            ["output"] = "Exception: connection refused after retrying for a very long time indeed"
        };
        var discovery2 = ToDiscovery("", new List<Dictionary<string, object?>> { failed });
        Assert.DoesNotContain("### WEB RESULTS", discovery2);
    }

    // ── Scenario C: multiple web steps — sections accumulate under a SHARED total budget ─

    [Fact]
    public void MultipleSections_AccumulateWithinSharedTotalBudget()
    {
        var r1 = FetchResult("https://example.com/a", "HTTP 200\n" + new string('a', 15_000));
        var r2 = FetchResult("https://example.com/b", "HTTP 200\n" + new string('b', 15_000));
        var results = new List<Dictionary<string, object?>> { r1, r2 };

        var discovery = ToDiscovery("", results);
        // Both 15k sections fit under the 20k per-section cap and the shared 60k total —
        // both survive into the planner prompt.
        Assert.Contains("### WEB RESULTS [https://example.com/a] ###", discovery);
        Assert.Contains("### WEB RESULTS [https://example.com/b] ###", discovery);
        Assert.True(discovery.Length > 15_000 + 15_000, $"sections should accumulate: {discovery.Length}");
        var planner = ToPlanner(discovery);
        Assert.Contains("### WEB RESULTS [https://example.com/a] ###", planner);
        Assert.Contains("### WEB RESULTS [https://example.com/b] ###", planner);
        Assert.Contains(new string('b', 14_000), planner);
    }

    [Fact]
    public void ManySections_BoundedBySharedTotalBudget()
    {
        // Five 15k sections would be 75k+ without a total budget — the shared 60k cap must
        // bound the discovery context and drop the overflow sections explicitly.
        var results = new List<Dictionary<string, object?>>();
        for (var i = 0; i < 5; i++)
            results.Add(FetchResult($"https://example.com/s{i}", "HTTP 200\n" + new string((char)('a' + i), 15_000)));

        var discovery = ToDiscovery("", results);
        // 4 full 15k sections = 60k of content; the 5th must be omitted with a marker.
        Assert.Contains("### WEB RESULTS [https://example.com/s0] ###", discovery);
        Assert.Contains("### WEB RESULTS [https://example.com/s3] ###", discovery);
        Assert.DoesNotContain("### WEB RESULTS [https://example.com/s4] ###", discovery);
        Assert.Contains("web-results budget exhausted — 1 later section(s) omitted", discovery);
        Assert.True(discovery.Length <= 60_000 + 2_000, $"shared total budget violated: {discovery.Length}");
    }
}
