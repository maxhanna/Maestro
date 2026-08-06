using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public sealed class ConfigFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weaver-config-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConcurrentWritesRemainValidAndDoNotCollideOnTemporaryFile()
    {
        Directory.CreateDirectory(_root);
        var service = new ConfigFileService(new FakeEnvironment(_root));
        var writes = Enumerable.Range(0, 20).Select(i => service.WriteConfigAsync(new FrontendConfig
        {
            defaultProject = $"project-{i}",
            projects = [new ProjectDto { Name = $"Project {i}", Path = $"project-{i}" }]
        }));

        await Task.WhenAll(writes);

        var loaded = await service.LoadConfigAsync();
        Assert.NotNull(loaded);
        Assert.Single(loaded.projects);
        Assert.StartsWith("project-", loaded.defaultProject);
        Assert.False(File.Exists(Path.Combine(_root, "weaverconfig.json.tmp")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Weaver.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
