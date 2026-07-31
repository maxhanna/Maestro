namespace Weaver;

/// <summary>
/// Optional benchmark configuration carried on a test card. Makes the perfect-pass
/// gates in <see cref="TestGateResults"/> decidable — without it, gates that need a
/// card-authored expectation (<see cref="TestGateResults.ExactStepCount"/>,
/// <see cref="TestGateResults.StructurePreserved"/>) report null (unmeasured).
/// </summary>
public class BenchmarkManifest
{
    /// <summary>How many plan steps this card expects. Null if the card doesn't pin it.</summary>
    public int? ExpectedSteps { get; set; }

    /// <summary>Project-root-relative globs (supporting `*` and `**`) the agent may create/modify.</summary>
    public List<string> AllowedPaths { get; set; } = new();

    public BenchmarkFormatting? Formatting { get; set; }

    /// <summary>Repeat count for determinism measurement (Phase 4a leaderboard aggregates a rate).</summary>
    public int Runs { get; set; } = 1;

    /// <summary>When set, this card is a canned benchmark-ladder run (see
    /// BenchmarkService.GetBenchmarkPlans()) rather than a hand-authored test card.
    /// The orchestrator resolves the prompt/sandbox from this level instead of the
    /// card's own text.</summary>
    public int? PresetLevel { get; set; }
}

/// <summary>
/// Formatting oracle configuration for the <c>formattingClean</c> gate.
///
/// Declares *what* to check, never *how*. The command per extension is machine-local
/// config (see CustomSystemInfo.FormatterCommands), because a card travels to other
/// machines via the BugHosted leaderboard and cannot carry one machine's tool paths.
/// Keeping resolution local also lets each machine pin its own formatter versions,
/// which is what makes a perfect-pass rate comparable across the leaderboard.
/// </summary>
public class BenchmarkFormatting
{
    /// <summary>"formatter" (run this machine's check command per extension), "golden" (diff against a fixture — not yet implemented), or "none".</summary>
    public string Mode { get; set; } = "none";

    /// <summary>
    /// Extensions (no dot, e.g. "py") this card requires to be checkable. If the running
    /// machine has no command for one of them the gate reports null — unmeasured — rather
    /// than passing on the strength of the extensions it could check.
    /// </summary>
    public List<string> Extensions { get; set; } = new();
}
