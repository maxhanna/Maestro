using System.Reflection;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the '### WEB RESULTS ARE IN CONTEXT ###' nudge in the incremental step user prompt:
/// after a _web_search/_web_fetch step executes and its output was harvested into the
/// discovery context, the next planner turn is told the results are already there — do NOT
/// re-search, do NOT write scraping code, and if more detail is needed pick a concrete URL
/// from the results for a _web_fetch step. The nudge must only fire when a '### WEB RESULTS'
/// section actually exists (a failed/empty search produces none, and pointing at a missing
/// section would make the model hunt for it).
/// </summary>
public class WebResultsNudgePromptTests
{
    private static readonly MethodInfo BuildUserPromptMethod = typeof(AgentController).GetMethod(
        "BuildIncrementalStepUserPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string Build(string discoveryContext, List<PlanStep> planSoFar)
        => (string)BuildUserPromptMethod.Invoke(null, new object?[]
        {
            "Search the web for an interesting AI article and write the data into a text file on my desktop.",
            discoveryContext,
            planSoFar,
            null,
            new List<string>(),
            null,  // extendedReasoning
            null,  // atomicStepEstimate
            null,  // requirementChecklist (threaded separately — never appended to the task)
            null   // projectRoot
        })!;

    private static List<PlanStep> WebPlanSoFar() => new()
    {
        new PlanStep { File = "_web_search", Change = "AI research breakthroughs latest" }
    };

    [Fact]
    public void Nudge_Appears_WhenResultsAreInContext()
    {
        var ctx = "### read file.ts\n```\ncode\n```\n\n### WEB RESULTS [AI research breakthroughs latest] ###\n" +
                  "## Summary\nRecent breakthroughs include…\n## Results\n  - Article (https://example.com/ai-article)\n";
        var prompt = Build(ctx, WebPlanSoFar());
        Assert.Contains("### WEB RESULTS ARE IN CONTEXT ###", prompt);
        Assert.Contains("Do NOT search the web again", prompt);
        Assert.Contains("do NOT write code (Python/JS/C#/Selenium) to fetch pages", prompt);
        // The concrete-URL path: step 2 may _web_fetch the exact URL from the results.
        Assert.Contains("_web_fetch step with THAT exact URL from the results", prompt);
        Assert.Contains("https://example.com/ai-article", prompt); // the results section is present
    }

    [Fact]
    public void Nudge_DoesNotAppear_WhenSearchProducedNoResults()
    {
        // A failed/empty search harvests nothing — no '### WEB RESULTS' section, so no nudge
        // (it would point the model at a section that does not exist).
        var ctx = "### read file.ts\n```\ncode\n```\n";
        var prompt = Build(ctx, WebPlanSoFar());
        Assert.DoesNotContain("### WEB RESULTS ARE IN CONTEXT ###", prompt);
        Assert.DoesNotContain("_web_fetch step with THAT exact URL", prompt);
    }

    [Fact]
    public void Nudge_DoesNotAppear_WhenNoWebStepYet()
    {
        // The plan has no web step (nothing ran) — the nudge must not pre-empt it.
        var ctx = "### WEB RESULTS [AI] ###\nsome stray section\n";
        var prompt = Build(ctx, new List<PlanStep>());
        Assert.DoesNotContain("### WEB RESULTS ARE IN CONTEXT ###", prompt);
    }
}
