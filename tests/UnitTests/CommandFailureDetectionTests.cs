using Weaver.Controllers;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the _command hard-failure detector (AgentController.OutputShowsHardFailure). The
/// benchmark-22 failure: `node server.js` crashed with a SyntaxError but the step was
/// reported "done", so the planner never planned a recovery edit and deadlocked re-running
/// the same broken command. The detector marks a _command whose output shows a HARD failure
/// as status=error — while exempting benign success summaries ("Passed!", "Failed: 0",
/// "0 Error(s)") that contain the bare words 'failed'/'error' but mean the run PASSED.
/// </summary>
public class CommandFailureDetectionTests
{
    [Fact]
    public void NodeSyntaxErrorCrash_IsAFailure()
    {
        // The exact crash from the benchmark-22 log — node's stack-trace output.
        const string output =
            "C:\\...\\server.js:41\n" +
            "});\n" +
            "^\n" +
            "SyntaxError: Unexpected token '}'\n" +
            "    at wrapSafe (node:internal/modules/cjs/loader:1662:18)\n" +
            "    at Module._compile (node:internal/modules/cjs/loader:1704:20)";
        Assert.True(AgentController.OutputShowsHardFailure(output));
    }

    [Fact]
    public void CommandNotFound_IsAFailure()
    {
        // PowerShell "not recognized" (Windows) and bash "command not found" (Unix) — the
        // runtime-free deterministic failure the integration test uses.
        Assert.True(AgentController.OutputShowsHardFailure("notacommand12345xyz : The term 'notacommand12345xyz' is not recognized as the name of a cmdlet, function, script file, or operable program."));
        Assert.True(AgentController.OutputShowsHardFailure("bash: notacommand12345xyz: command not found"));
    }

    [Fact]
    public void XunitSuccessLine_IsNotAFailure()
    {
        const string output =
            "Test run for C:\\...\\Weaver.dll (.NETCoreApp,Version=v10.0)\n" +
            "A total of 1 test files matched the specified pattern.\n" +
            "Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 307 ms - Weaver.dll (net10.0)";
        Assert.False(AgentController.OutputShowsHardFailure(output));
    }

    [Fact]
    public void XunitFailureLine_IsAFailure()
    {
        // The FAILURE line starts "Failed!" and contains a NON-zero failed count — it must
        // NOT be exempted just because the word 'passed' appears ("Passed: 2026").
        const string output =
            "  Failed Weaver.UnitTests.SomeTest.Runs [3 ms]\n" +
            "Failed!  - Failed:     2, Passed:  2026, Skipped:     0, Total:  2028, Duration: 25 s - Weaver.dll (net10.0)";
        Assert.True(AgentController.OutputShowsHardFailure(output));
    }

    [Fact]
    public void DotnetBuildSuccess_IsNotAFailure()
    {
        Assert.False(AgentController.OutputShowsHardFailure("Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:00:02.01"));
    }

    [Fact]
    public void DotnetBuildCompileError_IsAFailure()
    {
        Assert.True(AgentController.OutputShowsHardFailure("Program.cs(12,9): error CS1002: ; expected"));
        Assert.True(AgentController.OutputShowsHardFailure("error CS0103: The name 'x' does not exist in the current context"));
    }

    [Fact]
    public void TestSummaryZeroFailed_IsNotAFailure()
    {
        Assert.False(AgentController.OutputShowsHardFailure("0 failed, 10 passed"));
        Assert.False(AgentController.OutputShowsHardFailure("Build succeeded"));
    }

    [Fact]
    public void EmptyOrNullOutput_IsNeverAFailure()
    {
        Assert.False(AgentController.OutputShowsHardFailure(null));
        Assert.False(AgentController.OutputShowsHardFailure(""));
        Assert.False(AgentController.OutputShowsHardFailure("   "));
    }

    [Fact]
    public void Excerpt_StripsPowerShellPromptLineBeforeTruncating()
    {
        // The PS terminal prefixes every command with a prompt/echo line. If it were kept,
        // the head-truncate would cut the diagnostic off before "Error: boom" (the
        // benchmark-22 node crash shape). The excerpt must drop that first line.
        const string output =
            "PS C:\\Users\\Saint\\AppData\\Local\\Temp\\proj> Set-Location -LiteralPath \"C:\\Users\\Saint\\AppData\\Local\\Temp\\proj\"; node benchmark_test_22/server.js\n" +
            "C:\\Users\\Saint\\AppData\\Local\\Temp\\proj\\benchmark_test_22\\server.js:1\n" +
            "throw new Error(\"boom\");\n" +
            "^\n" +
            "Error: boom\n";
        var excerpt = AgentController.ExtractCommandFailureExcerpt(output);
        Assert.DoesNotContain("PS ", excerpt, StringComparison.Ordinal);
        Assert.Contains("Error: boom", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Excerpt_NoPromptLine_PassesOutputThrough()
    {
        const string output = "server.js:1\nthrow new Error(\"boom\");\nError: boom\n";
        var excerpt = AgentController.ExtractCommandFailureExcerpt(output);
        Assert.Contains("Error: boom", excerpt, StringComparison.Ordinal);
    }
}
