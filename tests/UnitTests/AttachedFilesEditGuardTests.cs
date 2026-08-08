using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the ATTACHED-FILES EDIT GUARD in ValidateIncrementalStepAsync. Came out of the
/// "group benchmarks by benchmark name" run: with weaver.component.ts/.html attached, the
/// planner invented a brand-new benchmark_grouping_helper.ts at the repo ROOT instead of
/// adding a method to the component and updating its template. The guard rejects explicit
/// _create_file/_create_directory steps deterministically when the user attached files and
/// the task prompt does not ask to create a new artifact — unless the task genuinely asks
/// for creation ("create a helper file…"), which PromptSignalsFileCreation detects.
/// </summary>
public class AttachedFilesEditGuardTests
{
    private static readonly MethodInfo ValidateMethod = typeof(AgentController).GetMethod(
        "ValidateIncrementalStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly List<string> Attached =
        new() { "maxhanna.client/src/app/weaver/weaver.component.ts", "maxhanna.client/src/app/weaver/weaver.component.html" };

    /// <summary>Runs the REAL private validator on an uninitialized controller — the early
    /// guard paths are pure (static helpers only), so no DI/state is needed.</summary>
    private static (bool valid, string? reason) Validate(PlanStep step, string prompt, bool skipLlm = false)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var task = (Task<(bool valid, string? reason)>)ValidateMethod.Invoke(controller, new object?[]
        {
            step, prompt, /*discoveryContext*/ "", /*planSoFar*/ new List<PlanStep>(),
            /*projectRoot*/ ".", /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ skipLlm,
            /*lastStepCompletionNote*/ null, /*attachedFiles*/ Attached
        })!;
        var result = task.GetAwaiter().GetResult();
        return (result.valid, result.reason);
    }

    private static PlanStep CreateFile(string change) => new() { File = "_create_file", Change = change, NewString = "some content that is long enough" };

    // ── reject: edit-scoped task with attached files proposes a new file ─────

    [Fact]
    public void CreateFile_EditTaskWithAttachedFiles_IsRejected()
    {
        // The exact shape from the failing run: "group benchmarks" + a new helper file.
        var (valid, reason) = Validate(
            CreateFile("benchmark_grouping_helper.ts"),
            "In the benchmarks panel in weaver component, group benchmarks by benchmark name");
        Assert.False(valid);
        Assert.Contains("scoped to the attached file(s)", reason);
        Assert.Contains("weaver.component.ts", reason);
        Assert.Contains("EDIT", reason);
    }

    [Fact]
    public void CreateDirectory_EditTaskWithAttachedFiles_IsRejected()
    {
        var (valid, _) = Validate(
            new PlanStep { File = "_create_directory", Change = "benchmark_grouping", NewString = "" },
            "In the benchmarks panel in weaver component, group benchmarks by benchmark name");
        Assert.False(valid);
    }

    [Fact]
    public void CreateFile_EditTask_StillRejectedInRetryMode()
    {
        // Deterministic guards fire even when skipLlm (retry mode) is set — the model must
        // not bypass the guard by riding a rejection retry.
        var (valid, reason) = Validate(
            CreateFile("benchmark_grouping_helper.ts"),
            "In the benchmarks panel in weaver component, group benchmarks by benchmark name",
            skipLlm: true);
        Assert.False(valid);
    }

    [Fact]
    public void CreateFile_AddMethodPhrasing_IsRejectedNotAllowed()
    {
        // "add a method" is an edit to an existing file, not a file creation — the guard
        // must not read "add … method" as creation intent (method is not a creation noun).
        var (valid, _) = Validate(
            CreateFile("benchmark_grouping_helper.ts"),
            "Add a getGroupedBenchmarksByName method to the component");
        Assert.False(valid);
    }

    // ── allow: creation intent, no attached files, or edit steps ─────────────

    [Fact]
    public void CreateFile_WithExplicitCreationIntent_IsAllowed()
    {
        var (valid, reason) = Validate(
            CreateFile("benchmark_grouping_helper.ts"),
            "Create a helper file that groups benchmarks by name");
        Assert.True(valid, reason);
    }

    [Fact]
    public void CreateFile_WithNewFilePhrasing_IsAllowed()
    {
        var (valid, reason) = Validate(
            CreateFile("benchmark_grouping_helper.ts"),
            "Write a new spec file for the attached component");
        Assert.True(valid, reason);
    }

    [Fact]
    public void CreateFile_NoAttachedFiles_IsAllowed()
    {
        // Guard only applies when the user attached files to scope the task.
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var task = (Task<(bool valid, string? reason)>)ValidateMethod.Invoke(controller, new object?[]
        {
            CreateFile("benchmark_grouping_helper.ts"), "In the benchmarks panel in weaver component, group benchmarks by benchmark name",
            /*discoveryContext*/ "", /*planSoFar*/ new List<PlanStep>(), /*projectRoot*/ ".",
            /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ false, /*lastStepCompletionNote*/ null, /*attachedFiles*/ null
        })!;
        var (valid, _) = task.GetAwaiter().GetResult();
        Assert.True(valid);
    }

    [Fact]
    public void NormalEditStep_WithAttachedFiles_IsAllowed()
    {
        // The correct plan shape: an edit to the attached component, not a new file.
        // skipLlm:true keeps the deterministic guards under test (the LLM-coherence tail
        // needs a live endpoint, like the other guard test files).
        var (valid, reason) = Validate(
            new PlanStep
            {
                File = "maxhanna.client/src/app/weaver/weaver.component.ts",
                Change = "Add getGroupedBenchmarksByName method",
                OldString = "benchmarks: BenchmarkEntry[] = [];",
                NewString = "benchmarks: BenchmarkEntry[] = [];\n\ngetGroupedBenchmarksByName() { return this.benchmarks; }"
            },
            "In the benchmarks panel in weaver component, group benchmarks by benchmark name",
            skipLlm: true);
        Assert.True(valid, reason);
    }
}
