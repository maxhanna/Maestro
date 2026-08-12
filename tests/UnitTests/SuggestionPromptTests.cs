using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the suggestion-generation prompt (AgentController.BuildSuggestionPrompt) — the
/// Freebuff-style guidance that makes suggestions advanced and connected instead of
/// superficial: the ranked value ladder (fix real problems → open new directions → harden
/// → connect), the spread rule, specificity rules, anti-patterns, and the RUN OUTCOME
/// block fed from the card's verification verdict + problem signals. Came out of the
/// request to stop generating "basic stupid stuff" that isn't connected to the run.
/// </summary>
public class SuggestionPromptTests
{
    private static string Build(
        string cardText = "card task",
        string thinking = "",
        List<string>? planLog = null,
        List<string>? stepLog = null,
        string summary = "",
        List<string>? filesEdited = null,
        string projectContext = "### PROJECT CONTEXT ###\nskeleton",
        int slots = 3,
        int maxSuggestions = 3,
        bool topup = false,
        List<string>? existingDescs = null,
        string contextDepth = "full",
        bool hasVerification = false,
        bool verificationComplete = false,
        string verificationReason = "",
        List<string>? runSignals = null)
        => AgentController.BuildSuggestionPrompt(
            cardText, thinking, planLog ?? new List<string>(), stepLog ?? new List<string>(),
            summary, filesEdited ?? new List<string>(), projectContext,
            slots, maxSuggestions, topup, existingDescs ?? new List<string>(), contextDepth,
            hasVerification, verificationComplete, verificationReason, runSignals ?? new List<string>());

    [Fact]
    public void Guidance_ContainsValueLadder_Spread_Specificity_AndAntiPatterns()
    {
        var p = Build();
        Assert.Contains("WHAT MAKES A GREAT SUGGESTION", p);
        Assert.Contains("FIX THE RUN'S ACTUAL PROBLEMS", p);
        Assert.Contains("OPEN A NEW DIRECTION", p);
        Assert.Contains("HARDEN OR TEST WHAT WAS JUST BUILT", p);
        Assert.Contains("CONNECT TO THE REST OF THE APP", p);
        Assert.Contains("SPREAD: aim for variety", p);
        Assert.Contains("mix one tightly-scoped next step with at least one bolder, more expansive", p);
        Assert.Contains("SPECIFICITY:", p);
        Assert.Contains("state the concrete end state in ONE", p);
        Assert.Contains("NEVER suggest:", p);
        Assert.Contains("Vague polish", p);
        Assert.Contains("Generic advice ('add tests', 'handle errors')", p);
    }

    [Fact]
    public void RunOutcome_VerificationIncomplete_AndSignals_AreRenderedVerbatim()
    {
        var p = Build(
            hasVerification: true, verificationComplete: false,
            verificationReason: "The demanded desktop file was never written.",
            runSignals: new List<string> { "repair pass 1/3 did not land", "rejected step: invented path" });
        Assert.Contains("### RUN OUTCOME", p);
        Assert.Contains("VERIFICATION: INCOMPLETE — The demanded desktop file was never written.", p);
        Assert.Contains("PROBLEM SIGNALS", p);
        Assert.Contains("repair pass 1/3 did not land", p);
        Assert.Contains("rejected step: invented path", p);
    }

    [Fact]
    public void RunOutcome_VerificationComplete_RendersComplete()
    {
        var p = Build(hasVerification: true, verificationComplete: true,
            verificationReason: "Template binding wired; CSS class present.", runSignals: new List<string>());
        Assert.Contains("VERIFICATION: COMPLETE — Template binding wired; CSS class present.", p);
        Assert.Contains("PROBLEM SIGNALS: none", p);
    }

    [Fact]
    public void RunOutcome_NoVerificationAndNoSignals_OmitsBlockEntirely()
    {
        var p = Build();
        // The guidance MENTIONS the RUN OUTCOME block, so assert on the actual section
        // header and verdict lines, not the phrase itself.
        Assert.DoesNotContain("### RUN OUTCOME", p);
        Assert.DoesNotContain("- VERIFICATION:", p);
        Assert.DoesNotContain("PROBLEM SIGNALS", p);
    }

    [Fact]
    public void RunOutcome_OnlySignals_RendersSignalsWithoutVerificationLine()
    {
        var p = Build(runSignals: new List<string> { "⛔ Repair circuit breaker tripped" });
        Assert.Contains("### RUN OUTCOME", p);
        Assert.Contains("VERIFICATION: not reported", p);
        Assert.Contains("⛔ Repair circuit breaker tripped", p);
    }

    [Fact]
    public void CardData_AndJsonShape_ArePresent()
    {
        var p = Build(cardText: "Add max height to schedules",
            thinking: "the popup was overflowing",
            planLog: new List<string> { "✓ add max-height rule" },
            stepLog: new List<string> { "globe.component.css — add .schedule max-height" },
            summary: "Schedules now scroll.",
            filesEdited: new List<string> { "src/app/globe/globe.component.css" });
        Assert.Contains("CARD TASK:\nAdd max height to schedules", p);
        Assert.Contains("AGENT THINKING", p);
        Assert.Contains("PLAN ITEMS", p);
        Assert.Contains("STEPS EXECUTED", p);
        Assert.Contains("COMPLETION SUMMARY", p);
        Assert.Contains("FILES CHANGED", p);
        Assert.Contains("src/app/globe/globe.component.css", p);
        Assert.Contains("Reply ONLY with a JSON array of 0-3 objects", p);
        Assert.Contains("never invent work", p);
    }

    [Fact]
    public void Topup_KeepsGuidance_AndListsExistingSuggestions()
    {
        var p = Build(topup: true, slots: 1,
            existingDescs: new List<string> { "Add error handling", "Extract a service" });
        Assert.Contains("More like this", p);
        Assert.Contains("EXISTING SUGGESTIONS (do not repeat or paraphrase these):", p);
        Assert.Contains("Add error handling", p);
        Assert.Contains("Extract a service", p);
        // The value ladder still applies to top-ups — new suggestions must be just as good.
        Assert.Contains("WHAT MAKES A GREAT SUGGESTION", p);
        Assert.Contains("Reply ONLY with a JSON array of 0-1 objects", p);
    }

    [Fact]
    public void DepthPointer_MatchesRequestedDepth()
    {
        var skeleton = Build(contextDepth: "skeleton");
        Assert.Contains("file/directory skeleton", skeleton);
        Assert.DoesNotContain("recent git history", skeleton);

        var board = Build(contextDepth: "board");
        Assert.Contains("every other kanban card on", board);
        Assert.DoesNotContain("recent git history", board);

        var full = Build(contextDepth: "full");
        Assert.Contains("recent git history", full);
    }

    [Fact]
    public void ProjectContext_IsAppendedAfterRunOutcome()
    {
        var p = Build(projectContext: "### PROJECT CONTEXT ###\nsrc/app", runSignals: new List<string> { "signal" });
        var outcomeIdx = p.IndexOf("RUN OUTCOME", StringComparison.Ordinal);
        var ctxIdx = p.IndexOf("### PROJECT CONTEXT ###", StringComparison.Ordinal);
        Assert.True(outcomeIdx >= 0 && ctxIdx > outcomeIdx, "PROJECT CONTEXT must come after RUN OUTCOME");
    }
}
