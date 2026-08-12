using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the INVENTED-DIRECTORY guard in ValidateIncrementalStepAsync (the _create_file arm
/// of the invented-file guard): a _create_file step whose directory prefix does not exist
/// anywhere in the project is rejected with a steer to the CLOSEST real directory — the
/// deepest existing ancestor, or an existing folder with the same leaf name — instead of
/// silently materializing the invented path on disk (the execution path auto-creates missing
/// parent directories, so an unchecked step would fabricate the whole tree). An earlier
/// _create_directory step in the same plan legitimately makes the directory real (its
/// execution creates it), so those steps are exempt. Mirrors the disk-sandbox pattern of
/// CreateFileConflictGuardTests; validator-level tests invoke the real private validator
/// exactly like AttachedFilesEditGuardTests.
/// </summary>
public class CreateFileDirectoryGuardTests : IDisposable
{
    private readonly string _root;

    public CreateFileDirectoryGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "weaver-create-dir-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void WriteFile(string rel, string content = "x")
    {
        var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private static PlanStep CreateFileStep(string change) =>
        new() { File = "_create_file", Change = change, NewString = "export const A = 1;" };

    private (bool valid, string? reason) Validate(PlanStep step, params PlanStep[] planSoFar)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var method = typeof(AgentController).GetMethod(
            "ValidateIncrementalStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ValidateIncrementalStepAsync not found");
        var task = (Task<(bool valid, string? reason)>)method.Invoke(controller, new object?[]
        {
            step, /*originalPrompt*/ "Create a helper file", /*discoveryContext*/ "", planSoFar.ToList(),
            _root, /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ true,
            /*lastStepCompletionNote*/ null, /*attachedFiles*/ null
        })!;
        return task.GetAwaiter().GetResult();
    }

    // ── Validator-level: the guard ─────────────────────────────────────────────────────

    [Fact]
    public void CreateUnderRealDirectory_IsAllowed()
    {
        WriteFile("maxhanna.client/src/app/demo/demo.component.ts");
        var (valid, reason) = Validate(CreateFileStep("maxhanna.client/src/app/demo/helper.ts"));
        Assert.True(valid, $"a _create_file under an existing directory must pass — reason: {reason}");
    }

    [Fact]
    public void CreateUnderInventedSubdirectory_Rejected_SteersToClosestAncestor()
    {
        WriteFile("maxhanna.client/src/app/demo/demo.component.ts");
        // 'weaver' exists nowhere; the closest real directory is .../app/demo.
        var (valid, reason) = Validate(CreateFileStep("maxhanna.client/src/app/demo/weaver/helper.ts"));
        Assert.False(valid);
        Assert.Contains("does not exist anywhere in the project", reason);
        Assert.Contains("maxhanna.client/src/app/demo", reason);
    }

    [Fact]
    public void CreateUnderInventedDirectory_SameLeafElsewhere_SteersToRealLeafDirectory()
    {
        WriteFile("maxhanna.client/src/app/weaver/weaver.component.ts");
        WriteFile("maxhanna.client/src/app/demo/demo.component.ts");
        // The planner wrote .../demo/weaver (invented) but a REAL 'weaver' directory exists at
        // .../app/weaver — the steer must name that directory, not the bare ancestor .../app/demo.
        var (valid, reason) = Validate(CreateFileStep("maxhanna.client/src/app/demo/weaver/helper.ts"));
        Assert.False(valid);
        Assert.Contains("does not exist anywhere in the project", reason);
        Assert.Contains("maxhanna.client/src/app/weaver", reason);
    }

    [Fact]
    public void CreateUnderInventedDirectory_PrecededByCreateDirectoryStep_IsAllowed()
    {
        // A _create_directory step in the same plan makes the directory real (its execution
        // creates it), so the follow-up _create_file must not be rejected for the dir missing
        // on disk YET — that is the normal create-directory-then-file chain.
        var (valid, reason) = Validate(
            CreateFileStep("maxhanna.client/src/app/demo/weaver/helper.ts"),
            new PlanStep { File = "_create_directory", Change = "maxhanna.client/src/app/demo/weaver" });
        Assert.True(valid, $"a _create_file under a planned _create_directory must pass — reason: {reason}");
    }

    [Fact]
    public void CreateUnderFullyInventedPath_Rejected_RootLandingWarning()
    {
        WriteFile("maxhanna.client/src/app/demo/demo.component.ts");
        // 'src/' itself does not exist anywhere — nothing real to steer to; the guard must
        // warn that the file would silently land at the project root.
        var (valid, reason) = Validate(CreateFileStep("src/helpers/weaver/util.ts"));
        Assert.False(valid);
        Assert.Contains("project root", reason);
        Assert.Contains("existing directory", reason);
    }

    [Fact]
    public void RootLevelCreateFile_IsAllowed()
    {
        // No directory prefix at all — the existing pathless/root behavior stays untouched.
        var (valid, reason) = Validate(CreateFileStep("README.md"));
        Assert.True(valid, $"a root-level _create_file must pass — reason: {reason}");
    }

    // ── Helper-level: FindClosestRealDirectory ─────────────────────────────────────────

    [Fact]
    public void Closest_ParentExists_ReturnsProposedDirectory()
    {
        WriteFile("maxhanna.client/src/app/demo/demo.component.ts");
        Assert.Equal("maxhanna.client/src/app/demo",
            AgentDiscovery.FindClosestRealDirectory("maxhanna.client/src/app/demo/helper.ts", _root));
    }

    [Fact]
    public void Closest_DeepestExistingAncestor_WhenParentMissing()
    {
        WriteFile("maxhanna.client/src/app/demo/demo.component.ts");
        Assert.Equal("maxhanna.client/src/app/demo",
            AgentDiscovery.FindClosestRealDirectory("maxhanna.client/src/app/demo/weaver/helper.ts", _root));
    }

    [Fact]
    public void Closest_SameLeafElsewhere_WinsOverAncestor()
    {
        WriteFile("maxhanna.client/src/app/weaver/weaver.component.ts");
        WriteFile("maxhanna.client/src/app/demo/demo.component.ts");
        Assert.Equal("maxhanna.client/src/app/weaver",
            AgentDiscovery.FindClosestRealDirectory("maxhanna.client/src/app/demo/weaver/helper.ts", _root));
    }

    [Fact]
    public void Closest_LongestTrailingSuffix_WinsAmongLeafMatches()
    {
        WriteFile("maxhanna.client/src/app/weaver/weaver.component.ts");
        WriteFile("other/weaver/thing.txt");
        // Both 'maxhanna.client/src/app/weaver' and 'other/weaver' match the leaf; the one
        // sharing the longest trailing suffix with the proposal ('.../demo/weaver') wins.
        Assert.Equal("maxhanna.client/src/app/weaver",
            AgentDiscovery.FindClosestRealDirectory("maxhanna.client/src/app/demo/weaver/helper.ts", _root));
    }

    [Fact]
    public void Closest_NothingReal_ReturnsNull()
    {
        WriteFile("maxhanna.client/src/app/demo/demo.component.ts");
        Assert.Null(AgentDiscovery.FindClosestRealDirectory("src/helpers/weaver/util.ts", _root));
    }

    // ── Helper-level: IsPathAncestorOrEqual (segment-aware) ────────────────────────────

    [Theory]
    [InlineData("a/b", "a/b", true)]       // equal
    [InlineData("a/b", "a/b/c", true)]     // direct child
    [InlineData("a/b", "a/b/c/d.ts", true)] // deeper descendant
    [InlineData("a/b", "a/bc", false)]     // prefix but NOT a segment boundary
    [InlineData("a/b", "b/c", false)]      // unrelated
    [InlineData("", "a/b/c.ts", true)]     // root-level creation covers everything
    public void AncestorOrEqual_SegmentAware(string dir, string path, bool expected)
    {
        Assert.Equal(expected, AgentDiscovery.IsPathAncestorOrEqual(dir, path));
    }
}
