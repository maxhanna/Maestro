using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the marker-level guards added for OS-filesystem tasks. These came out of the
/// "Search the web for an interesting and relevant AI article…" run, which failed three ways:
/// 1) the model's correct _web_search step was rejected by the research-verb guard
///    ("'search' is not an actionable edit") even though the web-need gate had confirmed web is
///    required; 2) the model invented a Linux path (/home/user/search_results) because it was
///    never told the OS or desktop path; 3) that repo-relative _create_directory then silently
///    created the folder INSIDE the project root.
/// </summary>
public class OsMarkerGuardTests
{
    private static readonly MethodInfo ValidateMethod = typeof(AgentController).GetMethod(
        "ValidateIncrementalStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Runs the REAL private validator on an uninitialized controller — the early
    /// guard paths are pure (static helpers only), so no DI/state is needed.</summary>
    private static (bool valid, string? reason) Validate(PlanStep step, string prompt)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var task = (Task<(bool valid, string? reason)>)ValidateMethod.Invoke(controller, new object?[]
        {
            step, prompt, /*discoveryContext*/ "", /*planSoFar*/ new List<PlanStep>(),
            /*projectRoot*/ ".", /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ false, /*lastStepCompletionNote*/ null
        })!;
        var result = task.GetAwaiter().GetResult();
        return (result.valid, result.reason);
    }

    private static PlanStep WebSearch(string query) => new() { File = "_web_search", Change = query };

    // ── _web_search / _web_fetch are actionable markers ──────────────────────

    [Fact]
    public void WebSearchStep_WithSearchVerbChange_IsNotRejectedByResearchGuard()
    {
        // The exact shape from the failing run: change starts with "search" but the step is a
        // _web_search marker — the web-need gate governs it, not the research-verb guard.
        var (valid, reason) = Validate(WebSearch("Search for recent AI articles about machine learning advancements"), "Search the web for an interesting and relevant AI article and write the data into a text file on my desktop.");
        Assert.True(valid, reason);
    }

    [Fact]
    public void WebFetchStep_WithReadVerbChange_IsNotRejectedByResearchGuard()
    {
        var (valid, _) = Validate(new PlanStep { File = "_web_fetch", Change = "Read the page at https://example.com/article" },
            "Fetch a page from the web");
        Assert.True(valid);
    }

    [Fact]
    public void NormalEditStep_WithSearchVerbChange_StillRejected()
    {
        // The research guard stays intact for actual repo-edit steps.
        var (valid, reason) = Validate(new PlanStep { File = "src/Login.ts", Change = "Search for the login component" },
            "Fix the login component");
        Assert.False(valid);
        Assert.Contains("Research step rejected", reason);
    }

    // ── _create_directory / _create_file cannot reach the OS filesystem ─────

    [Fact]
    public void CreateDirectory_WithOsPath_RejectedForOsTask()
    {
        // The exact failure: repo-relative _create_directory with an invented absolute path.
        var (valid, reason) = Validate(new PlanStep { File = "_create_directory", Change = "/home/user/search_results" },
            "Search the web for an AI article and write it to a text file on my desktop.");
        Assert.False(valid);
        Assert.Contains("writes RELATIVE TO THE PROJECT ROOT", reason);
        Assert.Contains("_command", reason);
    }

    [Fact]
    public void CreateFile_WithWindowsPath_RejectedForOsTask()
    {
        // Content is provided (so the pre-existing content check passes) — the OS-path guard
        // must still reject it before the repo-relative path would land inside the project.
        var (valid, reason) = Validate(new PlanStep { File = "_create_file", Change = "C:\\Users\\me\\Desktop\\out.txt", NewString = "# AI Article\n\nSummary" },
            "Search the web for an AI article and write it to a text file on my desktop");
        Assert.False(valid);
        Assert.Contains("writes RELATIVE TO THE PROJECT ROOT", reason);
    }

    [Fact]
    public void CreateDirectory_WithTildeHomePath_RejectedForOsTask()
    {
        var (valid, reason) = Validate(new PlanStep { File = "_create_directory", Change = "~/Desktop/search_results" },
            "Search the web for an AI article and write it to a text file on my desktop");
        Assert.False(valid);
        Assert.Contains("writes RELATIVE TO THE PROJECT ROOT", reason);
    }

    [Fact]
    public void CreateDirectory_WithUncPath_RejectedForOsTask()
    {
        var (valid, reason) = Validate(new PlanStep { File = "_create_directory", Change = "\\\\server\\share\\out" },
            "Search the web for an AI article and write it to a text file on my desktop");
        Assert.False(valid);
        Assert.Contains("writes RELATIVE TO THE PROJECT ROOT", reason);
    }

    [Fact]
    public void CreateDirectory_RepoRelative_AllowedEvenForOsTask()
    {
        var (valid, reason) = Validate(new PlanStep { File = "_create_directory", Change = "benchmark_test_6" },
            "Create a folder called benchmark_test_6");
        Assert.True(valid, reason);
    }

    [Fact]
    public void CreateDirectory_WithOsPath_AllowedForRepoTask()
    {
        // Repo tasks keep full marker freedom — the guard only fires for OS tasks.
        var (valid, reason) = Validate(new PlanStep { File = "_create_directory", Change = "src/desktop/components" },
            "Create a desktop components folder in the repo");
        Assert.True(valid, reason);
    }

    // ── _command prose gets desktop-aware feedback on OS tasks ───────────────

    [Fact]
    public void CommandStep_ProseChange_OsFeedbackMentionsDesktopPath()
    {
        var (valid, reason) = Validate(new PlanStep { File = "_command", Change = "Create temporary script to fetch latest AI article data" },
            "Search the web for an AI article and write it to a text file on my desktop");
        Assert.False(valid);
        Assert.Contains("New-Item -ItemType Directory", reason);
    }

    [Fact]
    public void CommandStep_RealCommand_Allowed()
    {
        var (valid, reason) = Validate(new PlanStep { File = "_command", Change = "New-Item -ItemType Directory -Path \"C:\\Users\\me\\Desktop\\search_results\" -Force" },
            "Search the web for an AI article and save it to my desktop.");
        Assert.True(valid, reason);
    }
}
