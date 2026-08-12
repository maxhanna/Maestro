using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the SIBLING-STEERING extension of the invented-file guard's anchored-EDIT arm
/// (ValidateIncrementalStepAsync): an edit step targeting a file that does not exist is
/// rejected — and instead of a bare rejection the feedback now proposes the closest REAL
/// sibling path, so the next planner turn re-grounds in an existing file instead of
/// retrying the same invented path. Two steering families: (1) the invented leaf name
/// exists verbatim in a DIFFERENT real directory (retarget there), and (2) the invented
/// leaf is a typo/plural/case variant (Levenshtein ≤ 2) of a real file in the closest
/// real directory (retarget to the near-miss sibling). With no plausible sibling, the
/// generic invented-file rejection still fires (never a bare pass). Mirrors the disk
/// sandbox + reflection pattern of CreateFileDirectoryGuardTests / AttachedFilesEditGuardTests.
/// </summary>
public class InventedEditFileSiblingTests : IDisposable
{
    private readonly string _root;

    public InventedEditFileSiblingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "weaver-invented-sibling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void WriteFile(string rel, string content = "export const A = 1;")
    {
        var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    /// <summary>An anchored edit step (oldString + newString — NOT create-eligible).</summary>
    private static PlanStep AnchoredEditStep(string file, string change) => new()
    {
        File = file,
        Change = change,
        OldString = "const a = 1;",
        NewString = "const a = 2;"
    };

    private (bool valid, string? reason) Validate(PlanStep step, params PlanStep[] planSoFar)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var method = typeof(AgentController).GetMethod(
            "ValidateIncrementalStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ValidateIncrementalStepAsync not found");
        var task = (Task<(bool valid, string? reason)>)method.Invoke(controller, new object?[]
        {
            step, /*originalPrompt*/ "Fix the card service", /*discoveryContext*/ "", planSoFar.ToList(),
            _root, /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ true,
            /*lastStepCompletionNote*/ null, /*attachedFiles*/ null
        })!;
        return task.GetAwaiter().GetResult();
    }

    // ── Steering family 1: same leaf name in a DIFFERENT real directory ────────────────

    [Fact]
    public void InventedPath_SameLeafElsewhere_Rejected_ProposesRealFile()
    {
        WriteFile("src/shared/card.service.ts");

        var (valid, reason) = Validate(AnchoredEditStep("src/app/services/card.service.ts", "Fix the card service"));

        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("does not exist", reason);
        Assert.Contains("The closest real sibling is 'src/shared/card.service.ts'", reason);
        Assert.Contains("retarget this step to that existing file", reason);
    }

    // ── Steering family 2: near-miss leaf (typo/plural) in the closest real directory ──

    [Fact]
    public void InventedPath_TypoLeafInClosestRealDir_Rejected_ProposesNearMissSibling()
    {
        WriteFile("src/app/services/cards.service.ts");   // real: plural variant
        WriteFile("src/app/services/other.service.ts");

        var (valid, reason) = Validate(AnchoredEditStep("src/app/services/card.service.ts", "Fix the card service"));

        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("The closest real sibling is 'src/app/services/cards.service.ts'", reason);
    }

    [Fact]
    public void InventedPath_InventedDir_ClosestRealDirHoldsNearMiss_ProposesIt()
    {
        WriteFile("src/app/util.ts");   // real: same leaf, one typo off, in closest real dir
        WriteFile("src/app/demo.ts");

        // The directory "src/app/helpers" does not exist; closest real dir is "src/app".
        var (valid, reason) = Validate(AnchoredEditStep("src/app/helpers/utill.ts", "Fix the helper"));

        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("The closest real sibling is 'src/app/util.ts'", reason);
    }

    // ── No plausible sibling: generic rejection, never a pass ──────────────────────────

    [Fact]
    public void InventedPath_NoSiblingAnywhere_Rejected_GenericGuidance()
    {
        WriteFile("src/app/services/card.service.ts");

        var (valid, reason) = Validate(AnchoredEditStep("src/ghost/phantom.ts", "Fix the phantom"));

        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("Cannot edit 'src/ghost/phantom.ts'", reason);
        Assert.DoesNotContain("closest real sibling", reason);
        Assert.Contains("NEVER invent file paths", reason);
    }

    // ── Guard boundary: legit steps still pass / take their normal route ───────────────

    [Fact]
    public void EditToRealFile_IsAllowed()
    {
        WriteFile("src/app/card.service.ts", "export const a = 1;");

        var (valid, reason) = Validate(AnchoredEditStep("src/app/card.service.ts", "Fix the card service"));

        Assert.True(valid);
        Assert.Null(reason);
    }

    [Fact]
    public void NewStringOnlyStep_InventedPath_RedirectsToCreateFile_NotRejected()
    {
        // No oldString → create-eligible: the invented path is a legitimate NEW file,
        // converted to a _create_file step (never vetoed by the invented-file guard).
        var step = new PlanStep
        {
            File = "src/app/brand-new.ts",
            Change = "Create brand-new helper",
            OldString = "",
            NewString = "export const B = 2;"
        };

        var (valid, reason) = Validate(step);

        Assert.True(valid);
        Assert.Null(reason);
        Assert.Equal("_create_file", step.File);
        Assert.Equal("src/app/brand-new.ts", step.Change);
    }

    [Fact]
    public void EditToFileCreatedByEarlierStep_IsExempt()
    {
        var createStep = new PlanStep { File = "_create_file", Change = "src/app/card.service.ts", NewString = "export const a = 1;" };

        var (valid, reason) = Validate(AnchoredEditStep("src/app/card.service.ts", "Fix the card service"), createStep);

        Assert.True(valid);
        Assert.Null(reason);
    }
}
