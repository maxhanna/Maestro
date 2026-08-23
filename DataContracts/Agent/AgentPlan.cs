using System.Text.Json.Serialization;
using Weaver.Services;

namespace Weaver;

/// <summary>
/// One item in the structured plan the LLM produces during Phase 2.
/// </summary>
public class PlanItem
{
    public string File { get; set; } = "";
    public string Change { get; set; } = "";
    public int Priority { get; set; } = 1;
}
public class MetaPlanSubPlan
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string ContextNote { get; set; } = "";
    public List<string> Files { get; set; } = new();
}

public class MetaPlanResult
{
    public string MetaThinking { get; set; } = "";
    public string MetaSummary { get; set; } = "";
    public int Complexity { get; set; }
    public List<MetaPlanSubPlan> SubPlans { get; set; } = new();
}
public class PlanItemDeserialized
{
    public string file { get; set; } = "";
    public string change { get; set; } = "";
    public int priority { get; set; } = 1;
}

public class AgentPlanDeserialized
{
    public string thinking { get; set; } = "";
    public string summary { get; set; } = "";
    public List<PlanItemDeserialized> plan { get; set; } = new();
}

/// <summary>
/// The full plan envelope returned by the Phase-2 LLM call.
/// </summary> 
public class AgentPlan
{
    public string Thinking { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public int Score { get; set; } = 100;
    public List<PlanStep> Plan { get; set; } = new();
}

public class EditPair
{
    public string OldString { get; set; } = "";
    public string NewString { get; set; } = "";
    public int LineNumber { get; set; }
}

public class PlanStep
{
    public string File { get; set; } = string.Empty;
    public string Change { get; set; } = string.Empty;

    /// <summary>Transient planning state; never persisted to board data or sent to the client.</summary>
    [JsonIgnore]
    public bool SkipDuplicateCheck { get; set; }

    /// <summary>
    /// Frontend-side plan-item state persisted on the card (e.g. "rejected" for a step the
    /// interleaved validator vetoed). Round-trips through boarddata so a restarted run can
    /// skip rejected steps instead of executing them.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    public int Priority { get; set; }
    public string? OldString { get; set; } = string.Empty;
    public string? NewString { get; set; } = string.Empty;
    public List<string>? ReferenceFiles { get; set; }
    /// <summary>Line number in the file where this edit targets (1-based).</summary>
    [JsonPropertyName("line")]
    public int LineNumber { get; set; }

    /// <summary>Meta-plan group label (sub-plan title) this step belongs to, if any.</summary>
    [JsonPropertyName("metaGroup")]
    public string? MetaGroup { get; set; }

    /// <summary>Explicit target symbol (function/method/class/selector) to edit, set by the planner LLM.</summary>
    [JsonPropertyName("targetSymbol")]
    public string? TargetSymbol { get; set; }

    /// <summary>Multiple edits in one step — for small repetitive changes across columns/sections.</summary>
    [JsonPropertyName("edits")]
    public List<EditPair>? Edits { get; set; }

    /// <summary>FORMAT C/D: the target symbol type (method/function/class/interface/property/html).</summary>
    [JsonPropertyName("targetType")]
    public string? TargetType { get; set; }

    /// <summary>FORMAT C/D: the name of the symbol (or HTML code block) this edit targets.</summary>
    [JsonPropertyName("targetName")]
    public string? TargetName { get; set; }

    /// <summary>FORMAT C: insert the new code after the target symbol instead of replacing it.</summary>
    [JsonPropertyName("insertAfter")]
    public bool? InsertAfter { get; set; }

    /// <summary>FORMAT C/D: the replacement code, one array element per line.</summary>
    [JsonPropertyName("newCode")]
    public List<string>? NewCode { get; set; }

    /// <summary>fullFile format: the complete file content to write (used when the file does not exist).</summary>
    [JsonPropertyName("fullFile")]
    public string? FullFile { get; set; }

    /// <summary>
    /// Deterministic expected outcome for THIS step, computed from the step's own content —
    /// the known-correct answer the step is checked against (a literal swap's new text must
    /// be present; a 'did you mean' typo fix's corrected token must be what's on disk).
    /// Pure function of the step — no LLM, no disk. Attached to the plan item so each step
    /// shows its own expected outcome, and verified against disk when the step completes
    /// (see PersistBoardDataPlanStepAsync). Getter-only: never parsed from LLM responses.
    /// </summary>
    [JsonPropertyName("groundTruth")]
    public List<StepGroundTruth>? GroundTruth
    {
        get
        {
            var items = AgentTextUtilities.ComputeStepGroundTruth(File, OldString, NewString, FullFile, NewCode, Edits);
            return items.Count == 0 ? null : items;
        }
    }
}
