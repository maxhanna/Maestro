
namespace Weaver;

public class AgentRequest
{
    public string Prompt { get; set; } = "";
    public string Project { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public int? MaxIterations { get; set; }
    public int? MaxStepsPerBatch { get; set; }
    public string? SteeringContext { get; set; }
    public bool SelfImproving { get; set; }
    public bool IsDecomposing { get; set; }
    public string? CardId { get; set; }
    public bool CreateTests { get; set; }
    public string? BuildCommands { get; set; }

    /// <summary>True when this card is a benchmark "test card". When set, the
    /// orchestrator emits a TestRunResult ("test_result" SSE event) scoring how
    /// far through the card's steps the agent got before breaking. A card is a
    /// canned benchmark-ladder run (formerly the separate IsBenchmark flag) when
    /// <see cref="Benchmark"/>.PresetLevel is set instead of hand-authored.</summary>
    public bool IsTest { get; set; }
    /// <summary>Display name for the benchmark; falls back to the card id / prompt.</summary>
    public string? TestName { get; set; }
    /// <summary>Card-authored benchmark expectations (expected step count, allowed paths, formatting oracle, or a ladder PresetLevel). Null when the card doesn't opt in.</summary>
    public BenchmarkManifest? Benchmark { get; set; }
    /// <summary>Opt-in: upload this run's TestRunResult to the shared BugHosted leaderboard
    /// once scored. Local history is always saved regardless of this flag.</summary>
    public bool ShareToBugHosted { get; set; }

    /// <summary>Indices of plan steps already completed (0-based).</summary>
    public List<int>? CompletedStepIndices { get; set; }
}

public class ExistingPlanItem
{
    public int Index { get; set; }
    public string File { get; set; } = "";
    public string Change { get; set; } = "";
    public bool Done { get; set; }
    public string OldString { get; set; } = "";
    public string NewString { get; set; } = "";
}
