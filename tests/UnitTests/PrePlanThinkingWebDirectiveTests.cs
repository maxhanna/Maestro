using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the '### EARLIER WEB SEARCH RESULTS (authoritative — do NOT re-search) ###'
/// directive in the pre-plan deep-reasoning prompt (BuildPrePlanThinkingUserPrompt):
/// when an earlier _web_search/_web_fetch step already ran and its results were
/// harvested into the discovery context, the reasoning engine is told those results
/// are authoritative FACTS to use — never to re-derive the research need (the
/// "loop of searching": every step's thinking starts over from scratch and re-proposes
/// the same _web_search on the next planner turn). The directive must only appear when
/// web sections are actually present — a non-web run must not be told about results
/// that do not exist.
/// </summary>
public class PrePlanThinkingWebDirectiveTests
{
    private const string Task = "Search the web for an interesting AI article and write the data into a text file on my desktop.";

    private static readonly List<PlanStep> WebPlanSoFar = new()
    {
        new PlanStep { File = "_web_search", Change = "AI research breakthroughs latest" }
    };

    private const string RelatedFiles =
        "### read file.ts\n```\ncode\n```\n\n### WEB RESULTS [AI research breakthroughs latest] ###\n" +
        "## Summary\nRecent breakthroughs include…\n## Results\n  - Article (https://example.com/ai-article)\n";

    private const string WebSections =
        "### WEB RESULTS [AI research breakthroughs latest] ###\n" +
        "## Summary\nRecent breakthroughs include…\n## Results\n  - Article (https://example.com/ai-article)\n";

    [Fact]
    public void Directive_Appears_WhenWebResultsAreInContext()
    {
        var prompt = AgentController.BuildPrePlanThinkingUserPrompt(
            Task, "previous reasoning", WebPlanSoFar, RelatedFiles, hasAttached: false, WebSections);
        Assert.Contains("### EARLIER WEB SEARCH RESULTS (authoritative — do NOT re-search) ###", prompt);
        Assert.Contains("Do NOT propose another _web_search with the same or similar query", prompt);
        Assert.Contains("_web_fetch a CONCRETE URL listed in those results", prompt);
        Assert.Contains("USE-THE-RESULTS task", prompt);
        // The actual web sections ride along under RELEVANT PROJECT FILES.
        Assert.Contains("https://example.com/ai-article", prompt);
        // The existing prompt structure is preserved.
        Assert.Contains("### PLAN SO FAR (committed steps — do NOT redo these) ###", prompt);
        Assert.Contains("Step 1: [_web_search] AI research breakthroughs latest", prompt);
    }

    [Fact]
    public void Directive_DoesNotAppear_WithoutWebResults()
    {
        // A failed/empty search harvests nothing — no web sections, so no directive
        // (it would make the reasoning engine hunt for results that do not exist).
        var prompt = AgentController.BuildPrePlanThinkingUserPrompt(
            Task, "previous reasoning", WebPlanSoFar, "### read file.ts\n```\ncode\n```\n",
            hasAttached: false, webSections: "");
        Assert.DoesNotContain("### EARLIER WEB SEARCH RESULTS", prompt);
        Assert.DoesNotContain("Do NOT propose another _web_search", prompt);
    }

    [Fact]
    public void Directive_DoesNotAppear_ForNonWebPlan()
    {
        // No web step ran yet — the directive must not pre-empt a genuine first search.
        var prompt = AgentController.BuildPrePlanThinkingUserPrompt(
            Task, "", new List<PlanStep>(), RelatedFiles, hasAttached: false, WebSections);
        Assert.DoesNotContain("### EARLIER WEB SEARCH RESULTS", prompt);
    }

    [Fact]
    public void Directive_Appears_WithAttachedFilesToo()
    {
        // The web-results directive must fire in BOTH branches (attached files and
        // discovery-context files) — the reasoning engine needs the directive exactly
        // when the web data would otherwise be silently buried under attached content.
        // The real caller appends the extracted web sections into `related` before
        // building the prompt, so the URL rides in under the ATTACHED FILES header.
        var relatedWithWeb = "### attached.ts\n```\ncode\n```\n\n" + WebSections;
        var prompt = AgentController.BuildPrePlanThinkingUserPrompt(
            Task, "previous reasoning", WebPlanSoFar, relatedWithWeb, hasAttached: true, WebSections);
        Assert.Contains("### EARLIER WEB SEARCH RESULTS (authoritative — do NOT re-search) ###", prompt);
        Assert.Contains("### ATTACHED FILES (the ONLY files you may touch) ###", prompt);
        Assert.Contains("https://example.com/ai-article", prompt);
    }
}
