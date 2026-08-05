using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public sealed class BenchmarkSandboxSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weaver-benchmark-sandbox-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PrepareAsync_RejectsRootOutsideServerOwnedSandbox()
    {
        var service = new BenchmarkService(Path.Combine(_root, "data"));
        var outside = Path.Combine(_root, "outside");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(1, outside));
    }

    [Fact]
    public async Task PrepareAsync_AllowsOnlyTheServerOwnedSandbox()
    {
        var service = new BenchmarkService(Path.Combine(_root, "data"));

        var prepared = await service.PrepareAsync(1, service.SandboxRoot);

        Assert.StartsWith(Path.Combine(service.SandboxRoot, ".runs"), prepared.RunRoot, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(prepared.RunRoot));
    }

    [Fact]
    public async Task PreparedRunCanBeResolvedByServerIssuedRunId()
    {
        var service = new BenchmarkService(Path.Combine(_root, "data"));
        var prepared = await service.PrepareAsync(1, service.SandboxRoot);

        Assert.Equal(prepared.RunRoot, service.ResolveBenchmarkRun(prepared.RunId));
    }

    [Fact]
    public void RunIdCannotEscapeTheServerOwnedRunsDirectory()
    {
        var service = new BenchmarkService(Path.Combine(_root, "data"));

        Assert.Throws<InvalidOperationException>(() => service.ResolveBenchmarkRun(".."));
        Assert.Throws<InvalidOperationException>(() => service.ResolveBenchmarkRun("../outside"));
    }

    [Fact]
    public async Task ResolveBenchmarkRun_RejectsForgedRunMarker()
    {
        var service = new BenchmarkService(Path.Combine(_root, "data"));
        var forged = Path.Combine(service.SandboxRoot, ".runs", "forged");
        Directory.CreateDirectory(forged);
        await File.WriteAllTextAsync(Path.Combine(forged, ".weaver-benchmark-run.json"), "{}");

        Assert.Throws<InvalidOperationException>(() => service.ResolveBenchmarkRun("forged"));
    }

    [Fact]
    public async Task EvaluateAsync_RejectsRootsOutsideAPreparedRun()
    {
        var service = new BenchmarkService(Path.Combine(_root, "data"));
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EvaluateAsync(1, outside, "test-model", 1));
    }

    [Fact]
    public async Task EvaluateAsync_RejectsTraversalToAPathOutsideSandbox()
    {
        var service = new BenchmarkService(Path.Combine(_root, "data"));
        var traversal = Path.Combine(service.SandboxRoot, ".runs", "..", "..", "outside");
        Directory.CreateDirectory(Path.GetFullPath(traversal));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EvaluateAsync(1, traversal, "test-model", 1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
