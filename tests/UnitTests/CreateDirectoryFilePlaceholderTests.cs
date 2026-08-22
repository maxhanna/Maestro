using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the mirror of the dummy-file-for-folder guard: when a weak planner cannot create a
/// directory directly, it sometimes plans a <c>_create_directory</c> step whose PATH is a
/// FILE placeholder (e.g. <c>benchmark_test_23/placeholder.txt</c>) instead of the folder.
/// <c>AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder</c> rewrites such a step to
/// the clean parent directory it implies (here <c>benchmark_test_23</c>) so the junk file
/// path never becomes a directory on disk. Genuine directory paths (no parent segment, or a
/// plain extensionless name like <c>keep</c>/<c>temp</c>) must never be rewritten.
/// </summary>
public class CreateDirectoryFilePlaceholderTests
{
    // ── The regression: a file placeholder path planned as a directory ──────

    [Fact]
    public void PlaceholderTxt_PlannedAsDirectory_CleansToParentFolder()
    {
        Assert.Equal("benchmark_test_23",
            AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder("benchmark_test_23/placeholder.txt"));
    }

    [Fact]
    public void PlaceholderTxt_WithBackslashSeparators_CleansToParentFolder()
    {
        Assert.Equal("benchmark_test_23",
            AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder("benchmark_test_23\\placeholder.txt"));
    }

    [Fact]
    public void DummyFile_PlannedAsDirectory_CleansToParentFolder()
    {
        Assert.Equal("benchmark_test_23",
            AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder("benchmark_test_23/dummy.txt"));
    }

    [Fact]
    public void KeepFile_PlannedAsDirectory_CleansToParentFolder()
    {
        Assert.Equal("assets",
            AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder("assets/keep.md"));
    }

    [Fact]
    public void Gitkeep_PlannedAsDirectory_CleansToParentFolder()
    {
        Assert.Equal("benchmark_test_23",
            AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder("benchmark_test_23/.gitkeep"));
    }

    [Fact]
    public void DotKeep_PlannedAsDirectory_CleansToParentFolder()
    {
        Assert.Equal("src",
            AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder("src/.keep"));
    }

    [Fact]
    public void DeepNestedPlaceholder_CleansToImmediateParent()
    {
        Assert.Equal("a/b",
            AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder("a/b/placeholder.tmp"));
    }

    [Fact]
    public void TrailingSlashPlaceholder_CleansToParent()
    {
        Assert.Equal("benchmark_test_23",
            AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder("benchmark_test_23/placeholder.txt/"));
    }

    // ── Genuine directory paths must never be rewritten ────────────────────

    [Theory]
    [InlineData("benchmark_test_23")]                 // the real thing — plain folder name
    [InlineData("benchmark_test_23/")]                // trailing slash only
    [InlineData("src/components")]                    // nested real folder
    [InlineData("benchmark_test_23/index.html")]      // a real file path, not a placeholder
    [InlineData("benchmark_test_23/server.js")]       // a real file path, not a placeholder
    [InlineData("assets/spider.png")]                 // real asset, not a placeholder
    [InlineData("keep")]                              // plain word directory — leave alone
    [InlineData("temp")]                              // plain word directory — leave alone
    [InlineData("dummy")]                             // plain word directory — leave alone
    [InlineData("placeholder")]                       // plain word directory — leave alone
    [InlineData("README")]                            // extensionless real file at root
    [InlineData("LICENSE")]                           // extensionless real file at root
    public void GenuineDirectoryOrRealFilePaths_AreLeftAlone(string path)
    {
        Assert.Null(AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData(null)]
    public void EmptyOrRootPath_IsLeftAlone(string? path)
    {
        Assert.Null(AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder(path!));
    }

    // ── The shared stem check stays consistent with the _create_file guard ──

    [Fact]
    public void StemCheck_IsSharedWithCreateFileGuard()
    {
        // The same placeholder name that makes a _create_file a dummy scaffold must also
        // make a _create_directory with a file path convertible.
        Assert.True(AgentTextUtilities.IsDirectoryScaffoldPlaceholder("placeholder.txt", null));
        Assert.Equal("benchmark_test_23",
            AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder("benchmark_test_23/placeholder.txt"));
    }

    [Fact]
    public void RealFilePath_IsNotScaffold_And_NotConvertible()
    {
        Assert.False(AgentTextUtilities.IsDirectoryScaffoldPlaceholder("index.html", null));
        Assert.Null(AgentTextUtilities.CleanDirectoryPathFromFilePlaceholder("benchmark_test_23/index.html"));
    }
}
