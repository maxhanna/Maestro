using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the WRONG-ROOT _command guard: a _command step that creates a folder/file with an
/// absolute path OUTSIDE the project root is rejected (the benchmark-folder confusion — the
/// model puts benchmark_test_N on the Desktop instead of in the sandbox), while legitimate
/// OS-filesystem writes (the prompt itself demands the output on Desktop/Downloads) are
/// allowed, and URL schemes must never be misread as drive/absolute paths.
/// </summary>
public class WrongRootGuardTests
{
    private static readonly MethodInfo ValidateMethod = typeof(AgentController).GetMethod(
        "ValidateIncrementalStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static (bool valid, string? reason) Validate(PlanStep step, string prompt)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var task = (Task<(bool valid, string? reason)>)ValidateMethod.Invoke(controller, new object?[]
        {
            step, prompt, /*discoveryContext*/ "", /*planSoFar*/ new List<PlanStep>(),
            /*projectRoot*/ ".", /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ false,
            /*lastStepCompletionNote*/ null, /*attachedFiles*/ null
        })!;
        var result = task.GetAwaiter().GetResult();
        return (result.valid, result.reason);
    }

    // ── Repo tasks: benchmark-folder creation on the Desktop stays REJECTED ───

    [Fact]
    public void MkdirBenchmarkFolderOnDesktop_RepoTask_Rejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (valid, reason) = Validate(
            new PlanStep { File = "_command", Change = "mkdir \"C:\\Users\\Saint\\Desktop\\benchmark_test_22\"" },
            "Create a folder called benchmark_test_22 at the project root");
        Assert.False(valid);
        Assert.Contains("OUTSIDE the project root", reason);
    }

    [Fact]
    public void NewItemBenchmarkFolderOnDesktop_RepoTask_Rejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (valid, reason) = Validate(
            new PlanStep { File = "_command", Change = "New-Item -ItemType Directory -Path \"C:\\Users\\Saint\\Desktop\\benchmark_test_22\" -Force" },
            "Create a folder called benchmark_test_22 at the project root");
        Assert.False(valid);
        Assert.Contains("OUTSIDE the project root", reason);
    }

    // ── OS-filesystem tasks: writing to the demanded Desktop path stays ALLOWED ─

    [Fact]
    public void WriteFileToDesktop_OsTask_Allowed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (valid, reason) = Validate(
            new PlanStep { File = "_command", Change = "Set-Content -Path \"C:\\Users\\Saint\\Desktop\\results.txt\" -Value \"v2.1\"" },
            "Search the web for an AI article and write the data into a text file on my desktop");
        Assert.True(valid, reason);
    }

    [Fact]
    public void NewItemDirectoryOnDesktop_OsTask_Allowed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (valid, reason) = Validate(
            new PlanStep { File = "_command", Change = "New-Item -ItemType Directory -Path \"C:\\Users\\Saint\\Desktop\\search_results\" -Force" },
            "Search the web for an AI article and save it to my desktop");
        Assert.True(valid, reason);
    }

    // ── URL commands are never hijacked by the wrong-root guard ───────────────

    [Fact]
    public void UrlFetchCommand_RepoTask_NotWrongRootVeto()
    {
        // https://… must not be read as a drive/absolute path — the fetch-in-command guard
        // owns this, so the reason must be the fetch veto, never "OUTSIDE the project root".
        var (valid, reason) = Validate(
            new PlanStep { File = "_command", Change = "Invoke-RestMethod https://api.current.ai/releases | ConvertTo-Json | Set-Content notes.json" },
            "Check the latest weaver release notes online and note the version in NOTES.md");
        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.DoesNotContain("OUTSIDE the project root", reason);
        Assert.Contains("Fetching web content", reason);
    }
}
