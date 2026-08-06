using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Regression tests for the between-steps whole-task verification in the
/// interleaved execution loop (AgentController).
///
/// BUG (fixed): when the per-step verifier returned keep + needsExtraStep=false,
/// the loop ran a between-steps AssessCompletion with a hardcoded 30s LLM
/// timeout. On slow local models the assessment timed out, and the timeout was
/// treated as "task NOT complete" — forcing the agent to plan a redundant
/// step 2 that re-did the same edit.
///
/// FIX: the assessment uses the configurable timeout + one retry, and when the
/// assessment LLM is STILL unavailable (timed out / unparseable / empty), the
/// loop declares the plan complete after a needsExtraStep=false edit instead of
/// planning a redundant follow-up step.
///
/// These tests drive the two extracted decision helpers (private static, via
/// reflection) and AssessCompletion's early-return paths that need no LLM.
/// </summary>
public class BetweenStepsVerificationTests
{
    private static Dictionary<string, object?> EditResult(
        string status, bool needsExtraStep, string path = "src/app/foo.ts", bool isEdit = true)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = isEdit ? "edit" : "create",
            ["status"] = status,
            ["path"] = path,
            ["needsExtraStep"] = needsExtraStep
        };
    }

    private static bool InvokeIsLastEditVerifiedComplete(List<object> results)
    {
        var method = typeof(AgentController).GetMethod(
            "IsLastEditVerifiedComplete", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsLastEditVerifiedComplete not found");
        var typed = results.OfType<Dictionary<string, object?>>().ToList();
        return (bool)(method.Invoke(null, new object?[] { typed }) ?? false);
    }

    private static (bool declare, string reason, bool failed) InvokeShouldDeclare(
        bool isComplete, string? assessReason)
    {
        var method = typeof(AgentController).GetMethod(
            "ShouldDeclarePlanCompleteAfterAssessment", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ShouldDeclarePlanCompleteAfterAssessment not found");
        var args = new object?[] { isComplete, assessReason, null, null };
        var result = (bool)method.Invoke(null, args)!;
        return (result, (string)args[2]!, (bool)args[3]!);
    }

    /// <summary>
    /// The gate: a needsExtraStep=false edit/create result must trigger
    /// between-steps verification.
    /// </summary>
    [Fact]
    public void IsLastEditVerifiedComplete_NeedsExtraStepFalseEdit_ReturnsTrue()
    {
        var results = new List<object> { EditResult("done", needsExtraStep: false) };
        Assert.True(InvokeIsLastEditVerifiedComplete(results));
    }

    /// <summary>
    /// The gate must NOT fire when the result carries no needsExtraStep flag
    /// (not verified), when the status is not done/modified/created, or when the
    /// flag is explicitly true (a follow-up step is genuinely needed).
    /// </summary>
    [Theory]
    [InlineData("done", true)]   // needsExtraStep=true → still needs step 2
    [InlineData("failed", false)] // failed edit → not a verified-complete gate
    [InlineData("skipped", false)]
    public void IsLastEditVerifiedComplete_NotVerified_ReturnsFalse(string status, bool needsExtraStep)
    {
        var results = new List<object> { EditResult(status, needsExtraStep) };
        Assert.False(InvokeIsLastEditVerifiedComplete(results));
    }

    [Fact]
    public void IsLastEditVerifiedComplete_MissingFlag_ReturnsFalse()
    {
        var results = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "edit", ["status"] = "done", ["path"] = "a.ts" }
        };
        Assert.False(InvokeIsLastEditVerifiedComplete(results));
    }

    /// <summary>
    /// The core regression: assessment LLM unavailable ("Assessment timed out")
    /// after a needsExtraStep=false edit → declare the plan complete, do NOT
    /// plan a redundant step 2.
    /// </summary>
    [Fact]
    public void AssessmentTimedOut_AfterNeedsExtraStepFalseEdit_DeclaresComplete()
    {
        var gate = InvokeIsLastEditVerifiedComplete(new List<object> { EditResult("done", false) });
        Assert.True(gate);

        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: false, assessReason: "Assessment timed out");
        Assert.True(declare);
        Assert.True(failed);
        Assert.Contains("stopping instead of planning a redundant step", reason);
    }

    /// <summary>Unparseable assessment JSON is treated the same as a timeout.</summary>
    [Fact]
    public void AssessmentUnparseable_AfterNeedsExtraStepFalseEdit_DeclaresComplete()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: false, assessReason: "Could not parse assessment");
        Assert.True(declare);
        Assert.True(failed);
        Assert.Contains("assessment LLM unavailable", reason);
    }

    /// <summary>Empty assessment response (LLM down) also declares complete.</summary>
    [Fact]
    public void AssessmentEmpty_AfterNeedsExtraStepFalseEdit_DeclaresComplete()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(isComplete: false, assessReason: null);
        Assert.True(declare);
        Assert.True(failed);
    }

    /// <summary>
    /// A REAL assessment verdict of "not complete" (LLM actually responded and
    /// found remaining work) must still keep planning — the fallback only covers
    /// assessment unavailability, never a genuine not-complete verdict.
    /// </summary>
    [Fact]
    public void AssessmentSaysNotComplete_KeepsPlanning()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: false, assessReason: "Step 2 still needs the button handler");
        Assert.False(declare);
        Assert.False(failed);
        Assert.Equal("Step 2 still needs the button handler", reason);
    }

    /// <summary>A healthy complete assessment declares complete with the LLM's reason.</summary>
    [Fact]
    public void AssessmentSaysComplete_DeclaresCompleteWithLlmReason()
    {
        var (declare, reason, failed) = InvokeShouldDeclare(
            isComplete: true, assessReason: "All requested changes applied");
        Assert.True(declare);
        Assert.False(failed);
        Assert.Equal("All requested changes applied", reason);
    }

    /// <summary>
    /// AssessCompletion's no-LLM early return: zero edit steps → command-only
    /// task is complete, no LLM call made.
    /// </summary>
    [Fact]
    public async Task AssessCompletion_NoEditSteps_CommandOnlyComplete_NoLlm()
    {
        var (isComplete, reason) = await InvokeAssessCompletion(
            new List<object>(), projectRoot: Path.GetTempPath());
        Assert.True(isComplete);
        Assert.Contains("No edit steps", reason);
    }

    /// <summary>
    /// AssessCompletion's no-LLM early return: a failed edit step → not complete,
    /// no LLM call made.
    /// </summary>
    [Fact]
    public async Task AssessCompletion_FailedEditStep_NotComplete_NoLlm()
    {
        var results = new List<object>
        {
            EditResult("done", false, "src/app/a.ts"),
            EditResult("failed", false, "src/app/b.ts")
        };
        var (isComplete, reason) = await InvokeAssessCompletion(results, projectRoot: Path.GetTempPath());
        Assert.False(isComplete);
        Assert.Contains("failed", reason);
    }

    private static async Task<(bool isComplete, string reason)> InvokeAssessCompletion(
        List<object> executedSteps, string projectRoot)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var method = typeof(AgentController).GetMethod(
            "AssessCompletion",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AssessCompletion not found");
        var task = (Task<(bool, string)>)method.Invoke(controller,
            new object?[]
            {
                "Make the button work", executedSteps, projectRoot,
                CancellationToken.None, new AgentPlan(), new List<string>(),
                /* atomicStepEstimate */ null
            })!;
        return await task;
    }
}
