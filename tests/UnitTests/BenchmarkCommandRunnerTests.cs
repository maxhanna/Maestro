using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public sealed class BenchmarkCommandRunnerTests
{
    [Fact]
    public void DefaultSandboxImageIsDigestPinned()
    {
        var runner = new DockerBenchmarkCommandRunner();

        Assert.Contains("@sha256:", runner.Image, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("python -m unittest discover")]
    [InlineData("python -m py_compile formatter.py")]
    [InlineData("python -m json.tool settings.json")]
    public void DockerRunner_AllowsOnlyKnownReadOnlyVerificationCommands(string command)
    {
        Assert.True(DockerBenchmarkCommandRunner.TryBuildPythonArguments(command, out var arguments));
        Assert.NotEmpty(arguments);
    }

    [Theory]
    [InlineData("python -c \"import os; os.remove('important')\"")]
    [InlineData("python -m json.tool settings.json & whoami")]
    [InlineData("cmd.exe /c whoami")]
    [InlineData("python -m json.tool ../outside.json")]
    public void DockerRunner_RejectsCommandsThatCouldEscapeTheVerificationPolicy(string command)
    {
        Assert.False(DockerBenchmarkCommandRunner.TryBuildPythonArguments(command, out _));
    }

    [Fact]
    public async Task DockerRunner_RejectsSymlinkedVerificationInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver-command-runner-tests", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(root);
        try
        {
            try { Directory.CreateSymbolicLink(source, target); }
            catch (UnauthorizedAccessException) { return; }
            catch (PlatformNotSupportedException) { return; }
            catch (IOException) { return; }

            var runner = new DockerBenchmarkCommandRunner(image: "weaver-test-image@sha256:0000000000000000000000000000000000000000000000000000000000000000");
            var result = await runner.RunAsync("python -m json.tool settings.json", source, 1, CancellationToken.None);

            Assert.Contains("unsafe filesystem link", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DockerRunner_FailsClosedWhenTheSandboxImageIsUnavailable()
    {
        var runner = new DockerBenchmarkCommandRunner(image: "weaver-test-image-that-does-not-exist@sha256:0000000000000000000000000000000000000000000000000000000000000000");

        var result = await runner.RunAsync("python -m json.tool settings.json", Path.GetTempPath(), 1, CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("sandbox", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
