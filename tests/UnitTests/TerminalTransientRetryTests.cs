using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the terminal/build transient-failure detector (Services/TransientFailureDetector.cs)
/// that gates the one-shot retry for command steps and build checks. File-lock blips (a stale
/// build daemon holding obj/bin) and transient network/feed errors (NuGet restore, git fetch,
/// npm) must be retried; genuine failures (compile errors, test failures, permission denials
/// that are permanent) must NOT be — re-running those would waste a build cycle.
/// </summary>
public class TerminalTransientRetryTests
{
    private static bool IsTransientCommandFailure(string? output)
        => TransientFailureDetector.IsTransientCommandFailure(output);

    // ── File-lock blips → retry ────────────────────────────────────────────
    [Fact]
    public void FileLock_BeingUsedByAnotherProcess_IsTransient()
    {
        Assert.True(IsTransientCommandFailure(
            "MSB3021: Could not copy file \"bin/Debug/net10.0/app.dll\" to \"obj/app.dll\" because " +
            "it is being used by another process."));
    }

    [Fact]
    public void FileLock_ProcessCannotAccessFile_IsTransient()
    {
        Assert.True(IsTransientCommandFailure(
            "The process cannot access the file 'C:\\proj\\obj\\app.dll' because it is being used by another process."));
    }

    [Fact]
    public void FileLock_SharingViolation_IsTransient()
    {
        Assert.True(IsTransientCommandFailure("Unhandled exception. IOException: The process cannot access the file because it is being used by another process."));
    }

    [Fact]
    public void FileLock_AccessToPath_IsTransient()
    {
        Assert.True(IsTransientCommandFailure("Access to the path 'C:\\proj\\obj\\x.dll' is denied."));
    }

    // ── Transient network / feed blips → retry ─────────────────────────────
    [Fact]
    public void Nuget_RestoreConnectionFailure_IsTransient()
    {
        Assert.True(IsTransientCommandFailure(
            "NU1101: Unable to find package X. ... Retrying 'FindPackagesByIdAsync' for source " +
            "'https://api.nuget.org/v3-flatcontainer/'."));
    }

    [Fact]
    public void Network_UnableToConnect_IsTransient()
    {
        Assert.True(IsTransientCommandFailure("fatal: unable to connect to github.com: Connection refused"));
    }

    [Fact]
    public void Network_RemoteNameNotResolved_IsTransient()
    {
        Assert.True(IsTransientCommandFailure("The remote name could not be resolved: 'registry.npmjs.org'"));
    }

    [Fact]
    public void Network_TimedOut_IsTransient()
    {
        Assert.True(IsTransientCommandFailure("npm error! network request to https://registry.npmjs.org timed out"));
    }

    [Fact]
    public void Network_EConnReset_IsTransient()
    {
        Assert.True(IsTransientCommandFailure("fatal: early EOF; ECONNRESET during fetch"));
    }

    [Fact]
    public void Restore_FailedToRestore_IsTransient()
    {
        Assert.True(IsTransientCommandFailure("Failed to restore NuGet packages. The remote server returned an error."));
    }

    // ── Genuine failures → do NOT retry ────────────────────────────────────
    [Fact]
    public void Retrying_WithSuccessfulBuildSummary_IsNotTransient()
    {
        // NuGet prints "Retrying 'FindPackagesByIdAsync'…" when the feed blips and then
        // recovers internally — a SUCCESSFUL build can contain both that line and "0 Error(s)".
        // The bare word 'error' (as in "0 Error(s)") must NOT trigger a spurious re-run.
        Assert.False(IsTransientCommandFailure(
            "Restored C:\\proj\\app.csproj (in 2s). Retrying 'FindPackagesByIdAsync' for source " +
            "'https://api.nuget.org/v3-flatcontainer/'.\nBuild succeeded.\n    0 Error(s)\n    0 Warning(s)"));
    }

    [Fact]
    public void Retrying_WithGenuineErrorCode_IsTransient()
    {
        // Feed gave up after its own retries: retrying sits next to a real error code.
        Assert.True(IsTransientCommandFailure(
            "Retrying 'FindPackagesByIdAsync' for source 'https://api.nuget.org/v3-flatcontainer/'.\n" +
            "error NU1101: Unable to find package X."));
    }

    [Fact]
    public void CompileError_IsNotTransient()
    {
        Assert.False(IsTransientCommandFailure(
            "error CS0103: The name 'foo' does not exist in the current context"));
    }

    [Fact]
    public void TestFailure_IsNotTransient()
    {
        Assert.False(IsTransientCommandFailure(
            "Failed!  - Failed:     2, Passed:   548, Total:   550"));
    }

    [Fact]
    public void PermanentPermissionDenied_IsNotTransient()
    {
        Assert.False(IsTransientCommandFailure("bash: ./script.sh: Permission denied"));
    }

    [Fact]
    public void EmptyOrNull_IsNotTransient()
    {
        Assert.False(IsTransientCommandFailure(null));
        Assert.False(IsTransientCommandFailure(""));
    }

    [Fact]
    public void CleanBuildOutput_IsNotTransient()
    {
        Assert.False(IsTransientCommandFailure(
            "Build succeeded.\n    0 Error(s)\n    0 Warning(s)"));
    }

    // ── LooksLikeCommandFailure: judges whether a retry actually recovered ──
    private static bool LooksLikeCommandFailure(string? output)
        => TransientFailureDetector.LooksLikeCommandFailure(output);

    [Fact]
    public void RetrySurfacingGenuineCompileError_IsAFailure()
    {
        // The file lock cleared but now there's a real compile error — a retry that lands
        // here did NOT recover the command, so the metric must say "still failed", not
        // "recovered ✓".
        Assert.True(LooksLikeCommandFailure(
            "error CS0103: The name 'foo' does not exist in the current context"));
    }

    [Fact]
    public void SuccessLineWithZeroErrors_IsNotAFailure()
    {
        // "0 Error(s)" contains the bare word 'error' but is a success line.
        Assert.False(LooksLikeCommandFailure(
            "Build succeeded.\n    0 Error(s)\n    0 Warning(s)"));
    }
}
