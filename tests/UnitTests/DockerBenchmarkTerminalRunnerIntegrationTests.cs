using System.Diagnostics;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Real-container checks are opt-in because CI runners may not provide Docker.
/// Run with WEAVER_RUN_DOCKER_TESTS=1 after pulling the pinned image.
/// </summary>
public sealed class DockerBenchmarkTerminalRunnerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "weaver-docker-terminal-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SuccessfulCommandCopiesWorkspaceChangesBack()
    {
        if (!DockerIntegrationEnabled()) return;
        Directory.CreateDirectory(_root);
        var result = await new DockerBenchmarkTerminalRunner().RunAsync(
            "printf created > /workspace/created.txt", _root, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("created", await File.ReadAllTextAsync(Path.Combine(_root, "created.txt")));
    }

    [Fact]
    public async Task NonZeroCommandDoesNotCopyPartialWorkspaceChanges()
    {
        if (!DockerIntegrationEnabled()) return;
        Directory.CreateDirectory(_root);
        var result = await new DockerBenchmarkTerminalRunner().RunAsync(
            "printf partial > /workspace/partial.txt; exit 7", _root, CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(_root, "partial.txt")));
    }

    [Fact]
    public async Task SymlinkCreatedInsideContainerIsRejectedDuringSynchronization()
    {
        if (!DockerIntegrationEnabled()) return;
        Directory.CreateDirectory(_root);
        var result = await new DockerBenchmarkTerminalRunner().RunAsync(
            "ln -s /etc/passwd /workspace/escape", _root, CancellationToken.None);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.Message.Contains("workspace limits", StringComparison.OrdinalIgnoreCase) ||
            result.Message.Contains("synchronization", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(_root, "escape")));
    }

    [Fact]
    public async Task ExcessiveOutputIsStoppedAndNotAllowedToRunToTimeout()
    {
        if (!DockerIntegrationEnabled()) return;
        Directory.CreateDirectory(_root);
        var result = await new DockerBenchmarkTerminalRunner().RunAsync(
            "python -c \"print('x' * 200000)\"", _root, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Contains("output limit", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExcessiveWorkspaceDirectoriesAreRejected()
    {
        if (!DockerIntegrationEnabled()) return;
        Directory.CreateDirectory(_root);
        var result = await new DockerBenchmarkTerminalRunner().RunAsync(
            "i=1; while [ $i -le 4200 ]; do mkdir /workspace/d$i; i=$((i+1)); done",
            _root, CancellationToken.None);

        Assert.True(result.TimedOut || result.ExitCode != 0);
        Assert.Contains("workspace", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationStopsTheContainerAndDoesNotCopyChanges()
    {
        if (!DockerIntegrationEnabled()) return;
        Directory.CreateDirectory(_root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DockerBenchmarkTerminalRunner().RunAsync(
                "sleep 60; printf late > /workspace/late.txt", _root, cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_root, "late.txt")));
        Assert.Empty(RunDocker("ps", "-aq", "--filter", "name=weaver-benchmark-agent-"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static bool DockerIntegrationEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("WEAVER_RUN_DOCKER_TESTS"), "1", StringComparison.Ordinal);

    private static string RunDocker(params string[] arguments)
    {
        var psi = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Docker could not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }
}
