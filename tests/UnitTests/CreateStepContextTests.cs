using System.Reflection;
using System.Text;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the fix behind \"the create_directory result isn't going into long term context\":
/// (1) AppendCreatedPathsToDiscoveryContext appends '### CREATED {path} (… exists on disk,
/// current state) ###' sections after a successful _create_directory/_create_file step, so
/// the planner/thinking/verification turns that follow actually see the filesystem changed —
/// previously the create result vanished (web results and edits get appended, but a bare
/// create is neither a read nor an edit, and the FILES-IN inventory skips empty directories),
/// which made the reasoning engine conclude \"no confirmation this step actually completed
/// successfully — only its intent is recorded\" and made the planner re-propose mkdir.
/// (2) AppendCreateStepsSection surfaces the same on-disk reality in the between-steps
/// completion assessment, so the assessor stops claiming a created folder was \"never
/// physically created\".
/// </summary>
public class CreateStepContextTests
{
    private static readonly MethodInfo AppendCreatedMethod = typeof(AgentController).GetMethod(
        "AppendCreatedPathsToDiscoveryContext", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AppendCreatedPathsToDiscoveryContext not found");

    private static string AppendCreated(string ctx, List<Dictionary<string, object?>> results, string root)
    {
        return (string)AppendCreatedMethod.Invoke(null, new object?[] { ctx, results, root })!;
    }

    private static Dictionary<string, object?> CreateResult(string path, string status = "done")
        => new() { ["type"] = "create", ["status"] = status, ["path"] = path };

    private static string NewRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_create_ctx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, name));
        return root;
    }

    // ── AppendCreatedPathsToDiscoveryContext ────────────────────────────────

    [Fact]
    public void CreatedDirectory_AppendsOnDiskSection()
    {
        var root = NewRoot("benchmark_test_16");
        try
        {
            var ctx = AppendCreated("", new List<Dictionary<string, object?>> { CreateResult("benchmark_test_16") }, root);
            Assert.Contains("### CREATED benchmark_test_16 (directory — exists on disk, current state) ###", ctx);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void CreatedFile_AppendsFileSection()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_create_ctx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "benchmark_test_16"));
        File.WriteAllText(Path.Combine(root, "benchmark_test_16", "pokemon_data.csv"), "id,name\n1,bulbasaur\n");
        try
        {
            var ctx = AppendCreated("", new List<Dictionary<string, object?>> { CreateResult("benchmark_test_16/pokemon_data.csv") }, root);
            Assert.Contains("### CREATED benchmark_test_16/pokemon_data.csv (file — exists on disk, current state) ###", ctx);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AbsoluteOsPathCreate_SurfacesAsExistingFile()
    {
        // The eager OS-dump (ExecuteWebPlanStep) records the demanded OS file as a
        // type="create"/status="created" result carrying an absolute path. That result
        // must land in long-term context like any other created path — an absolute
        // path is rooted, so Path.Combine(projectRoot, path) still resolves to it.
        var root = NewRoot("x");
        var osFile = Path.Combine(Path.GetTempPath(), "weaver_create_ctx_" + Guid.NewGuid().ToString("N"), "nested", "ai_article_data.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(osFile)!);
        File.WriteAllText(osFile, "### WEB RESULTS\nreal data\n");
        try
        {
            var ctx = AppendCreated("", new List<Dictionary<string, object?>> { CreateResult(osFile, "created") }, root);
            Assert.Contains("### CREATED " + osFile + " (file — exists on disk, current state) ###", ctx);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(osFile)!, true); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void FailedCreate_NotAppended()
    {
        var root = NewRoot("benchmark_test_16");
        try
        {
            var ctx = AppendCreated("", new List<Dictionary<string, object?>> { CreateResult("benchmark_test_16", "error") }, root);
            Assert.DoesNotContain("### CREATED", ctx);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void DoneCreate_PathMissingOnDisk_ReportedAsNotFound()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_create_ctx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ctx = AppendCreated("", new List<Dictionary<string, object?>> { CreateResult("ghost_folder") }, root);
            Assert.Contains("### CREATED ghost_folder (path (not found on disk) — exists on disk, current state) ###", ctx);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void NonCreateResults_LeaveContextUnchanged()
    {
        var root = NewRoot("x");
        try
        {
            var results = new List<Dictionary<string, object?>>
            {
                new() { ["type"] = "edit", ["status"] = "done", ["path"] = "a.ts" },
                new() { ["type"] = "_web_search", ["status"] = "done", ["query"] = "q", ["output"] = new string('x', 200) }
            };
            var ctx = AppendCreated("### seed ###", results, root);
            Assert.Equal("### seed ###", ctx);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ── AppendCreateStepsSection (the between-steps assessment prompt) ──────

    [Fact]
    public void AssessorSection_ListsCreatedDirectoryAsExisting()
    {
        var root = NewRoot("benchmark_test_16");
        try
        {
            var sb = new StringBuilder();
            AgentController.AppendCreateStepsSection(sb,
                new List<Dictionary<string, object?>> { CreateResult("benchmark_test_16") }, root);
            var text = sb.ToString();
            Assert.Contains("## Filesystem step results (created on disk)", text);
            Assert.Contains("benchmark_test_16: done — EXISTS on disk", text);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void AssessorSection_MissingPath_ReportsNotExisting()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_create_ctx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sb = new StringBuilder();
            AgentController.AppendCreateStepsSection(sb,
                new List<Dictionary<string, object?>> { CreateResult("benchmark_test_16") }, root);
            Assert.Contains("benchmark_test_16: done — does not exist on disk", sb.ToString());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
