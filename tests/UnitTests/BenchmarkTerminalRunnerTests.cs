using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public class BenchmarkTerminalRunnerTests
{
    [Fact]
    public void SandboxArguments_IsolateTheWorkspaceAndNeverUseTheHostShell()
    {
        var args = DockerBenchmarkTerminalRunner.BuildSandboxArguments(
            "weaver-agent-test", "C:\\benchmark-run", "cat /etc/passwd; touch /workspace/created.txt", 30);

        Assert.Contains("--network", args);
        Assert.Contains("none", args);
        Assert.Contains("--read-only", args);
        Assert.Contains("--cap-drop", args);
        Assert.Contains("ALL", args);
        Assert.DoesNotContain("--privileged", args);
        Assert.Contains(args, argument => argument.Contains("target=/workspace") && argument.Contains("readonly=false"));
        Assert.Equal("sh", args[^3]);
        Assert.Equal("-lc", args[^2]);
        Assert.Equal("cat /etc/passwd; touch /workspace/created.txt", args[^1]);
    }

    [Fact]
    public void SandboxArguments_DoesNotReplaceACommandThatLooksLikeTheImage()
    {
        var image = DockerBenchmarkTerminalRunner.BuildSandboxArguments(
            "weaver-agent-test", "C:\\benchmark-run", "probe", 30)[^8];
        var args = DockerBenchmarkTerminalRunner.BuildSandboxArguments(
            "weaver-agent-test", "C:\\benchmark-run", image, 30);

        Assert.Equal(image, args[^8]);
        Assert.Equal(image, args[^1]);
    }

    [Fact]
    public void SandboxArguments_UsesBoundedTimeoutAndProcessLimits()
    {
        var args = DockerBenchmarkTerminalRunner.BuildSandboxArguments(
            "weaver-agent-test", "C:\\benchmark-run", "python script.py", 600);

        Assert.Contains("--pids-limit", args);
        Assert.Contains("--memory", args);
        Assert.Contains("--cpus", args);
        Assert.Contains("timeout", args);
        Assert.Contains("300", args);
    }
}
