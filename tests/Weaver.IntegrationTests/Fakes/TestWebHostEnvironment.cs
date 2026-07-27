using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Weaver.IntegrationTests.Fakes;

/// <summary>Minimal IWebHostEnvironment stub — only ContentRootPath is actually read
/// by the services under test (ConfigFileService, FileHintsManager).</summary>
public sealed class TestWebHostEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "IntegrationTests";
    public string ApplicationName { get; set; } = "Weaver.IntegrationTests";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NoopFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NoopFileProvider();
}

sealed class NoopFileProvider : IFileProvider
{
    public IFileInfo GetFileInfo(string subpath) => new NoopFileInfo(subpath);
    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
}

sealed class NoopFileInfo : IFileInfo
{
    public NoopFileInfo(string name) => Name = name;
    public bool Exists => false;
    public long Length => -1;
    public string? PhysicalPath => null;
    public string Name { get; }
    public DateTimeOffset LastModified => DateTimeOffset.MinValue;
    public bool IsDirectory => false;
    public Stream CreateReadStream() => throw new NotSupportedException("Not used by the code paths under test.");
}
