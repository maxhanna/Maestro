using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the '### OS-FILESYSTEM TASK — OUTSIDE THE REPOSITORY ###' directive in the
/// pre-plan deep-reasoning prompt (BuildPrePlanThinkingUserPrompt). The deep-reasoning
/// system prompt is framed entirely around repo edits (anchors, FORMAT C, imports), so
/// an OS-filesystem task ("create a text file on the desktop") makes the engine
/// improvise script/infrastructure plans — "create main.py, import requests, pathlib…" —
/// while the planner's actual next step is a plain _command write or a _web_fetch of a
/// URL from the results. The directive reframes the reasoning so the thinking shown in
/// the panel aligns with the step that actually gets planned. Gated on
/// IsExternalFilesystemTask (the same detection discovery uses); attached files mean
/// repo-edit intent and suppress the OS frame.
/// </summary>
public class PrePlanThinkingOsDirectiveTests
{
    private const string OsTask = "Fetch a recent AI news article and create a text file on the desktop.";
    private const string RepoTask = "Fix the login bug in the repo's auth service";

    private static string Build(string task, bool hasAttached = false, string webSections = "",
        List<PlanStep>? plan = null)
    {
        return AgentController.BuildPrePlanThinkingUserPrompt(
            task, "previous reasoning", plan ?? new List<PlanStep>(),
            "### read file.ts\n```\ncode\n```\n", hasAttached, webSections);
    }

    [Fact]
    public void OsTask_GetsOsFilesystemDirective()
    {
        var prompt = Build(OsTask);
        Assert.Contains("### OS-FILESYSTEM TASK — OUTSIDE THE REPOSITORY ###", prompt);
        Assert.Contains("Do NOT plan Python/JS/C#/PowerShell scripts", prompt);
        Assert.Contains("_web_fetch of a concrete URL from those results", prompt);
        Assert.Contains("never invent article URLs or data", prompt);
    }

    [Fact]
    public void OsTask_WithWebResultsInContext_GetsBothDirectives()
    {
        // The user's scheduled card: an OS task whose search already ran — the engine must
        // get BOTH the OS frame (no scripts) and the use-the-results directive.
        var plan = new List<PlanStep> { new() { File = "_web_search", Change = "AI research breakthroughs latest" } };
        var prompt = Build(OsTask, webSections: "### WEB RESULTS [AI research breakthroughs latest] ###\ncontent",
            plan: plan);
        Assert.Contains("### OS-FILESYSTEM TASK — OUTSIDE THE REPOSITORY ###", prompt);
        Assert.Contains("### EARLIER WEB SEARCH RESULTS (authoritative — do NOT re-search) ###", prompt);
    }

    [Fact]
    public void RepoTask_NoOsDirective()
    {
        var prompt = Build(RepoTask);
        Assert.DoesNotContain("### OS-FILESYSTEM TASK", prompt);
        Assert.DoesNotContain("Do NOT plan Python", prompt);
    }

    [Fact]
    public void OsTask_WithAttachedFiles_NoOsDirective()
    {
        // Attached files mean the user wants edits inside those files — repo-edit intent
        // wins over the OS frame.
        var prompt = Build(OsTask, hasAttached: true);
        Assert.DoesNotContain("### OS-FILESYSTEM TASK", prompt);
        Assert.Contains("### ATTACHED FILES (the ONLY files you may touch) ###", prompt);
    }
}
