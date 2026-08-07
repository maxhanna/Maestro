using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for the same-directory "already exists" scoping (AgentDiscovery.FindSameDirectoryFile)
/// and the fresh verify-time structure listing (AgentController.BuildCurrentStructureListing) —
/// the fixes that stop same-named files in OTHER benchmark folders from blocking file creation,
/// and stop the verifier from judging a run against a stale pre-run listing.
/// </summary>
public class CreateFileConflictGuardTests : IDisposable
{
    private readonly string _root;

    public CreateFileConflictGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "weaver-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteFile(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ── FindSameDirectoryFile ──────────────────────────────────────────────

    [Fact]
    public void SameNameInOtherDirectory_DoesNotBlockCreation()
    {
        // benchmark_test_4/index.html exists; creating benchmark_test_7/index.html is fine —
        // the over-broad basename guard wrongly rejected this in the benchmark_test_7 run.
        WriteFile("benchmark_test_4/index.html", "<html></html>");
        Assert.Null(AgentDiscovery.FindSameDirectoryFile("benchmark_test_7/index.html", _root));
    }

    [Fact]
    public void SameNameInSameDirectory_IsDetected()
    {
        WriteFile("benchmark_test_7/index.html", "<html></html>");
        Assert.Equal("benchmark_test_7/index.html",
            AgentDiscovery.FindSameDirectoryFile("benchmark_test_7/index.html", _root));
    }

    [Fact]
    public void RootLevelSameName_IsDetected()
    {
        WriteFile("index.html", "<html></html>");
        Assert.Equal("index.html", AgentDiscovery.FindSameDirectoryFile("index.html", _root));
    }

    // ── BuildCurrentStructureListing ───────────────────────────────────────

    private static string InvokeBuildCurrentStructureListing(string root, List<object> allResults)
    {
        var method = typeof(AgentController).GetMethod(
            "BuildCurrentStructureListing", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildCurrentStructureListing not found");
        return (string)method.Invoke(null, new object?[] { root, allResults })!;
    }

    [Fact]
    public void Listing_ShowsTopLevelDirs_AndCreatedDirectoryContents()
    {
        WriteFile("benchmark_test_4/index.html", "<html></html>");
        WriteFile("benchmark_test_7/index.html", "<html><h1>Benchmark7</h1></html>");
        var results = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "create", ["status"] = "done", ["path"] = "benchmark_test_7" },
            new Dictionary<string, object?> { ["type"] = "create", ["status"] = "done", ["path"] = "benchmark_test_7/index.html" }
        };
        var listing = InvokeBuildCurrentStructureListing(_root, results);
        Assert.Contains("benchmark_test_7/", listing);
        Assert.Contains("[contents of benchmark_test_7/]", listing);
        Assert.Contains("index.html", listing);
    }

    [Fact]
    public void Listing_CreatedDirMissingOnDisk_IsSkipped()
    {
        // The directory result references a folder that was never actually created — the
        // listing must not fabricate its contents.
        var results = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "create", ["status"] = "done", ["path"] = "never_created" }
        };
        var listing = InvokeBuildCurrentStructureListing(_root, results);
        Assert.DoesNotContain("[contents of never_created/]", listing);
    }

    // ── Directory-target guard (ResolveDirectoryTargetForStep) ──────────────

    [Fact]
    public void DirectoryTarget_NoFileNameInChange_ReturnsNull_MeaningAlreadyDone()
    {
        // "Create directory 'benchmark_test_7' at project root level" names no file → the
        // step's intent is satisfied (the directory exists) → null → skipped, no disk write.
        var resolved = AgentDiscovery.ResolveDirectoryTargetForStep(
            "benchmark_test_7", "Create directory 'benchmark_test_7' at project root level");
        Assert.Null(resolved);
    }

    [Fact]
    public void DirectoryTarget_ChangeNamesFile_RedirectsInsideDirectory()
    {
        // The replanner re-emitted a step targeting the directory but the change names the
        // file that belongs inside it → the write must be redirected to dir/file.
        var resolved = AgentDiscovery.ResolveDirectoryTargetForStep(
            "benchmark_test_7", "Create index.html inside benchmark_test_7 with the hero section");
        Assert.Equal("benchmark_test_7/index.html", resolved);
    }

    [Fact]
    public void DirectoryTarget_VersionLikeTokens_AreIgnored()
    {
        // "v1.2" / "2.0" look like file names but are version tokens → no redirect.
        Assert.Null(AgentDiscovery.ResolveDirectoryTargetForStep("app", "Upgrade to v1.2 and create the folder"));
        Assert.Null(AgentDiscovery.ResolveDirectoryTargetForStep("app", "Requires node 18.0 runtime"));
    }

    [Fact]
    public void DirectoryTarget_EmptyOrNullChange_ReturnsNull()
    {
        Assert.Null(AgentDiscovery.ResolveDirectoryTargetForStep("benchmark_test_7", null));
        Assert.Null(AgentDiscovery.ResolveDirectoryTargetForStep("benchmark_test_7", ""));
        Assert.Null(AgentDiscovery.ResolveDirectoryTargetForStep("", "anything here"));
    }
}
