using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the guard INTERACTIONS the audit flagged as untested: each guard has unit
/// coverage alone, but no test exercised two guards on one prompt or the ORDER in
/// which their vetoes are checked. These came from the "Search the web … write the
/// data into a text file on my desktop" run class, where a web-needing OS task can
/// trip the fetch-in-command guard (web hint) AND the OS-task guard (Desktop target)
/// on the same plan.
/// </summary>
public class GuardInteractionTests
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
            /*projectRoot*/ ".", /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ false, /*lastStepCompletionNote*/ null, /*attachedFiles*/ null
        })!;
        var result = task.GetAwaiter().GetResult();
        return (result.valid, result.reason);
    }

    /// <summary>A prompt that hints at BOTH web need ("online") and the OS filesystem
    /// ("a file on my Desktop") — the guard-interaction shape from the failing runs.</summary>
    private const string DualHintPrompt =
        "Fetch the latest release version online and save the version number to a file on my Desktop.";

    // ── ORDER: the fetch-in-command veto wins for a URL-fetching _command step ──

    [Fact]
    public void FetchCommand_OnDualHintPrompt_RejectedWithFetchVetoNotOsVeto()
    {
        // A legit shell command that PULLS CONTENT from a URL. The OS-task guard does
        // not reject _command steps (they are the OS-task tool), so the ONLY veto that
        // can fire here is the fetch-in-command one — and it must fire, in preference
        // to any OS message.
        var (valid, reason) = Validate(
            new PlanStep { File = "_command", Change = "Invoke-RestMethod https://api.current.ai/releases | ConvertTo-Json | Set-Content out.json" },
            DualHintPrompt);
        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("Fetching web content is NOT a shell command", reason);
        Assert.DoesNotContain("Desktop", reason);
    }

    [Fact]
    public void RealCommand_OnDualHintPrompt_Allowed()
    {
        // A real terminal command (no URL fetch) on the same dual-hint prompt passes
        // BOTH guards: the fetch guard only matches URL-fetching, and the OS-task
        // guard deliberately permits _command (it is the OS-filesystem tool).
        var (valid, reason) = Validate(
            new PlanStep { File = "_command", Change = "Write-Output 'v2.1' | Set-Content release-version.txt" },
            DualHintPrompt);
        Assert.True(valid, reason);
    }

    // ── ORDER: the OS-path veto fires for OS-location create steps ─────────────

    [Fact]
    public void CreateFile_WithOsPath_OnDualHintPrompt_RejectedWithOsPathVeto()
    {
        // _create_file is repo-relative; a step whose change names an OS location
        // (here a Unix-style home path — the exact invention from the failing run)
        // must be rejected with the OS-path veto, teaching the real desktop path.
        var (valid, reason) = Validate(
            new PlanStep { File = "_create_file", Change = "/home/user/search_results.txt", NewString = "content" },
            DualHintPrompt);
        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains("writes RELATIVE TO THE PROJECT ROOT", reason);
        Assert.Contains("Desktop", reason);
    }

    // ── SCOPING: the fetch guard stays quiet where it must ────────────────────

    [Fact]
    public void BareCurlHealthCheck_OnRepoOnlyPrompt_Allowed()
    {
        // On a NON-web task a bare curl health check is a real terminal command —
        // the fetch-in-command guard is scoped to tasks that hint at needing web
        // data. This is the guard's precision boundary: it must not fire here.
        var (valid, reason) = Validate(
            new PlanStep { File = "_command", Change = "curl -s https://localhost:8080/health" },
            "Run the build and check the server responds");
        Assert.True(valid, reason);
    }

    [Fact]
    public void WebFetchStep_OnDualHintPrompt_NotRejectedByEitherGuard()
    {
        // The web tools themselves are never vetoed by the fetch-in-command or OS
        // guards — the correct tool for a web-needing OS task is _web_search/_web_fetch
        // followed by a _command write.
        var (valid, reason) = Validate(
            new PlanStep { File = "_web_fetch", Change = "https://bughosted.com/weaver/releases" },
            DualHintPrompt);
        Assert.True(valid, reason);
    }
}
