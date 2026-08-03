using System.Reflection;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks in the deterministic HeuristicComplexityScore bands so micro tasks
/// (auto-focus, typo, placeholder…) stay Trivial, large-signal tasks stay
/// Complex, and the length fallbacks never silently regress. The method is
/// private static (Controllers/AgentController.cs, ~line 12342), so it is
/// exercised through reflection — if the method is ever renamed, these tests
/// fail loudly instead of silently skipping.
/// </summary>
public class HeuristicComplexityScoreTests
{
    private static readonly MethodInfo HeuristicMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("HeuristicComplexityScore", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("HeuristicComplexityScore static method not found.");

    private static int Score(string? prompt)
        => (int)(HeuristicMethod.Invoke(null, new object?[] { prompt }) ?? -1);

    /// <summary>Pads a seed to an exact character count with 'x' (never a signal).</summary>
    private static string Pad(string seed, int length)
        => seed + new string('x', Math.Max(0, length - seed.Length));

    // ── Edge cases: empty / whitespace / null ────────────────────────────

    [Fact]
    public void EmptyPrompt_ReturnsBaseline20() => Assert.Equal(20, Score(""));

    [Fact]
    public void WhitespacePrompt_ReturnsBaseline20() => Assert.Equal(20, Score("   "));

    [Fact]
    public void NewlinesAndTabs_ReturnBaseline20() => Assert.Equal(20, Score("\n\t  \r\n"));

    [Fact]
    public void NullPrompt_ReturnsBaseline20() => Assert.Equal(20, Score(null));

    // ── Micro tasks (auto focus, typo, placeholder, cosmetic tweaks) ─────

    [Theory]
    [InlineData("auto focus the new card's input", 5)]
    [InlineData("auto focus the new card input field", 5)]
    [InlineData("fix the typo in the header", 5)]
    [InlineData("fix a typo in the login message", 5)]
    [InlineData("add a placeholder to the search box", 5)]
    [InlineData("add placeholder text to the email input", 5)]
    [InlineData("change the button text to Save", 5)]
    [InlineData("change the label to 'Created'", 5)]
    [InlineData("rename the variable foo to bar", 5)]
    [InlineData("add a comment explaining this section", 5)]
    [InlineData("change the color of the button to red", 5)]
    [InlineData("scroll into view after the card is added", 5)]
    public void MicroTask_ShortPrompt_StaysTrivial(string prompt, int expected)
        => Assert.Equal(expected, Score(prompt));

    [Fact]
    public void MicroTask_MidLength_UpTo120_Scores8()
    {
        // 41-120 chars, still a micro signal → 8
        var prompt = "please auto focus the new card's input field when the card is created so the user can start typing immediately";
        Assert.InRange(prompt.Length, 41, 120);
        Assert.Equal(8, Score(prompt));
    }

    [Fact]
    public void MicroTask_Longer_UpTo350_Scores10()
    {
        // 121-350 chars with a micro signal → 10 (still well below "Moderate")
        var prompt = "fix the typo in the welcome message and add a placeholder to the search input, and also change the button text " +
                     "so it reads 'Continue' instead of 'OK', then rename the submit handler so its name matches the convention used " +
                     "by the rest of the form components in this module.";
        Assert.InRange(prompt.Length, 121, 350);
        Assert.Equal(10, Score(prompt));
    }

    // Exact micro length-band boundaries (regression guard for the band edges)

    [Theory]
    [InlineData(40, 5)]    // ≤40 micro → 5
    [InlineData(41, 8)]    // 41-120 micro → 8
    [InlineData(120, 8)]   // boundary still ≤120 → 8
    [InlineData(121, 10)]  // 121-350 micro → 10
    [InlineData(350, 10)]  // boundary still ≤350 → 10
    [InlineData(351, 30)]  // past micro band → length fallback 30
    [InlineData(700, 30)]
    [InlineData(701, 38)]
    [InlineData(1500, 38)]
    [InlineData(1501, 45)]
    public void MicroTask_LengthBoundaries_DoNotRegress(int length, int expected)
        => Assert.Equal(expected, Score(Pad("auto focus", length)));

    // ── Non-micro prompts: pure length fallback bands ────────────────────

    [Theory]
    [InlineData(40, 10)]   // ≤40 no signal → 10
    [InlineData(41, 15)]   // 41-120 → 15
    [InlineData(120, 15)]
    [InlineData(121, 30)]  // 121-700 → 30
    [InlineData(350, 30)]
    [InlineData(700, 30)]
    [InlineData(701, 38)]  // 701-1500 → 38
    [InlineData(1500, 38)]
    [InlineData(1501, 45)] // >1500 → 45
    public void PlainPrompt_LengthBoundaries_DoNotRegress(int length, int expected)
        => Assert.Equal(expected, Score(Pad("hello world", length)));

    // ── Large-signal tasks: never downgraded below 55 ────────────────────

    [Theory]
    [InlineData("migrate the database to the new schema")]
    [InlineData("add a new endpoint for user profile management")]
    [InlineData("implement authentication for the login flow")]
    [InlineData("refactor the payment service into multiple files")]
    [InlineData("set up docker and a test suite for deployment")]
    [InlineData("create a background service that processes the queue")]
    [InlineData("new endpoint")]
    public void LargeSignalTask_ScoresComplex55(string prompt)
        => Assert.Equal(55, Score(prompt));

    [Fact]
    public void LargeSignal_OverridesMicroWording()
    {
        // Contains "typo" (micro) but "database"/"migration" win → 55, not 5.
        Assert.Equal(55, Score("fix the typo in the database migration script"));
    }

    [Fact]
    public void LargeSignal_OverridesLengthFallback()
    {
        // Long micro-sounding prompt is still anchored by the large signal.
        Assert.Equal(55, Score(Pad("migrate the database", 2000)));
    }

    // ── Long, genuinely complex prompts ──────────────────────────────────

    [Fact]
    public void LongComplexPrompt_Over1500_Scores45()
    {
        var prompt = new string('x', 1600); // no signals, long → 45
        Assert.Equal(45, Score(prompt));
    }

    [Fact]
    public void RealisticLongFeatureDescription_701To1500_Scores38()
    {
        // Deliberately avoids every large/micro signal word — must hit the pure
        // length fallback band 701-1500 → 38.
        var prompt = "Implement a full-featured reporting dashboard with drill-down charts, exportable CSV/PDF views, " +
                     "role-based visibility rules, scheduled email digests, and a configuration page, all wired to the " +
                     "existing data pipeline with proper error handling, loading states, empty states, keyboard navigation, " +
                     "accessibility labels, responsive layouts, unit tests for the chart data transformations, integration " +
                     "tests for the export endpoints, and documentation covering the rollout, rollback, and any environment " +
                     "variables the team needs to set in staging and production before this can ship, plus an operational " +
                     "runbook, a monitoring checklist covering latency, error rate and throughput dashboards, a support " +
                     "triage guide for the most common failure modes, and a handoff document describing the ownership " +
                     "model, the on-call rotation, and the escalation path so the next engineer can pick this up without " +
                     "having to reverse-engineer the whole system from scratch or guess at the expected behavior.";
        Assert.InRange(prompt.Length, 701, 1500);
        Assert.Equal(38, Score(prompt)); // 701-1500 fallback
    }

    [Fact]
    public void MicroSignal_InVeryLongPrompt_FallsBackToLength()
    {
        // A micro signal embedded in a very long prompt is no longer Trivial:
        // past 350 chars the length fallback governs (no large signal here).
        Assert.Equal(45, Score(Pad("fix the typo", 1600)));
    }

    // ── Case insensitivity ───────────────────────────────────────────────

    [Fact]
    public void Signals_AreCaseInsensitive()
    {
        Assert.Equal(5, Score("AUTO FOCUS the new card"));
        Assert.Equal(55, Score("MIGRATE THE DATABASE"));
    }
}
