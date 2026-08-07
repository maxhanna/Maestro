using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for <c>AgentController.FindImpliedCreateDirectory</c> — the helper that scopes a
/// pathless <c>_create_file</c> step (a bare filename extracted from its change description,
/// e.g. "Create README markdown document...") into the directory the run just created, instead
/// of silently dropping it at the project root. Invoked via reflection because the method is
/// private static, mirroring the pattern used by PostExecuteVerifyTests.
/// </summary>
public class CreateFilePathScopingTests : IDisposable
{
    private readonly string _root;
    private readonly List<object> _empty = new();

    public CreateFilePathScopingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "weaver-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string? InvokeFindImpliedCreateDirectory(AgentPlan? plan, int beforeIndex, List<object> allResults)
    {
        var method = typeof(AgentController).GetMethod(
            "FindImpliedCreateDirectory", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FindImpliedCreateDirectory not found");
        return (string?)method.Invoke(null, new object?[] { _root, plan, beforeIndex, allResults });
    }

    private void EnsureDir(string rel)
    {
        Directory.CreateDirectory(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static PlanStep CreateDir(string path) => new() { File = "_create_directory", Change = path };
    private static PlanStep CreateFile(string change) => new() { File = "_create_file", Change = change };

    private static List<object> CreateResult(string path) => new()
    {
        new Dictionary<string, object?>
        {
            ["type"] = "create",
            ["status"] = "done",
            ["path"] = path
        }
    };

    [Fact]
    public void SinglePrecedingCreateDirectoryStep_ReturnsIt()
    {
        EnsureDir("benchmark_test_6");
        // Batch path: the plan contains the _create_directory step at index 0 and the
        // pathless _create_file README step right after it.
        var plan = new AgentPlan
        {
            Plan = new List<PlanStep>
            {
                CreateDir("benchmark_test_6"),
                CreateFile("Create README markdown document with project overview and structure information")
            }
        };
        Assert.Equal("benchmark_test_6", InvokeFindImpliedCreateDirectory(plan, 1, _empty));
    }

    [Fact]
    public void NoPlanDirectory_ButExecutedDirectoryResult_ReturnsIt()
    {
        EnsureDir("benchmark_test_6");
        // Interleaved path: each step executes as its own single-step plan, so the plan scan
        // sees nothing — but allResults already contains the created-directory result.
        var plan = new AgentPlan
        {
            Plan = new List<PlanStep> { CreateFile("Create README markdown document with project overview and structure information") }
        };
        var results = CreateResult("benchmark_test_6");
        Assert.Equal("benchmark_test_6", InvokeFindImpliedCreateDirectory(plan, 0, results));
    }

    [Fact]
    public void ExecutedFileResults_DoNotCountAsDirectories()
    {
        EnsureDir("benchmark_test_6");
        // A created FILE result carries a sub-path — it must not be treated as a dir.
        var plan = new AgentPlan { Plan = new List<PlanStep> { CreateFile("x.md") } };
        var results = CreateResult("benchmark_test_6/readme.md");
        Assert.Null(InvokeFindImpliedCreateDirectory(plan, 0, results));
    }

    [Fact]
    public void ExtensionlessRootFile_IsNotMistakenForDirectory()
    {
        // A root-level LICENSE file produces the same result shape as a created directory
        // (type=create, no extension, no slash) — but it is NOT a directory on disk, so it
        // must never be picked as the implied directory.
        File.WriteAllText(Path.Combine(_root, "LICENSE"), "MIT");
        var plan = new AgentPlan { Plan = new List<PlanStep> { CreateFile("Create README markdown document") } };
        var results = CreateResult("LICENSE");
        Assert.Null(InvokeFindImpliedCreateDirectory(plan, 0, results));
    }

    [Fact]
    public void MultipleCreateDirectorySteps_PlanAmbiguous_FallsBackToExecutedDir()
    {
        EnsureDir("src");
        EnsureDir("tests");
        // Two directories in the plan is genuinely ambiguous — fall back to the most recently
        // created directory already recorded in the run.
        var plan = new AgentPlan
        {
            Plan = new List<PlanStep>
            {
                CreateDir("src"),
                CreateDir("tests"),
                CreateFile("Create README markdown document")
            }
        };
        var results = CreateResult("tests");
        Assert.Equal("tests", InvokeFindImpliedCreateDirectory(plan, 2, results));
    }

    [Fact]
    public void NoDirectoryContext_ReturnsNull_FileStaysAtRoot()
    {
        Assert.Null(InvokeFindImpliedCreateDirectory(
            new AgentPlan { Plan = new List<PlanStep> { CreateFile("x.md") } }, 0, _empty));
        Assert.Null(InvokeFindImpliedCreateDirectory(null, 0, _empty));
    }

    [Fact]
    public void CreateDirectoryStep_WithTrailingSlashAndQuotes_IsTrimmed()
    {
        EnsureDir("benchmark_test_6");
        var plan = new AgentPlan
        {
            Plan = new List<PlanStep>
            {
                CreateDir("'benchmark_test_6/'"),
                CreateFile("Create README markdown document")
            }
        };
        Assert.Equal("benchmark_test_6", InvokeFindImpliedCreateDirectory(plan, 1, _empty));
    }
}
