using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the file-explorer ignore-list semantics: exact-segment matching (no fuzzy
/// partial-name hits like "bin" matching "binfoo"), the merge of config entries with
/// built-in defaults, and the '-' un-ignore convention that lets a user re-show a
/// default dir (e.g. a legit source "bin" folder).
/// </summary>
public class FileIgnoreListTests
{
    [Theory]
    [InlineData("src/bin", true)]
    [InlineData("client/node_modules/pkg", true)]
    [InlineData("src/binfoo", false)]
    [InlineData("foo.bin", false)]
    [InlineData("src/binfile.txt", false)]
    [InlineData("src/Debug/app.exe", true)]
    [InlineData("Weaver/bin/Debug/net10.0/Weaver.exe", true)]
    [InlineData("src/obj/x", true)]
    [InlineData("docs/.git/HEAD", true)]
    [InlineData("src/scripts/env.ts", false)]
    [InlineData("vendor/lib.js", false)] // vendor no longer a default
    [InlineData("packages/app/src/main.ts", false)] // packages no longer a default
    public void ContainsIgnoredSegment_MatchesExactSegments_Only(string relPath, bool expected)
    {
        var ignore = FileEditController.MergeIgnoreDirs(Array.Empty<string>());
        var method = typeof(FileEditController).GetMethod(
            "ContainsIgnoredSegment", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = (bool)method!.Invoke(null, new object?[] { relPath, ignore })!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ContainsIgnoredSegment_NullOrEmptySet_AlwaysVisible()
    {
        var method = typeof(FileEditController).GetMethod(
            "ContainsIgnoredSegment", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        Assert.False((bool)method!.Invoke(null, new object?[] { "node_modules/x", null })!);
        Assert.False((bool)method.Invoke(null, new object?[] { "node_modules/x", new HashSet<string>() })!);
    }

    [Fact]
    public void MergeIgnoreDirs_Defaults_IncludeBuildVcsDependencyDirs()
    {
        var set = FileEditController.MergeIgnoreDirs(Array.Empty<string>());
        foreach (var d in new[] { "node_modules", "bin", "obj", ".git", "dist", "build", "__pycache__", ".venv", "Debug" })
            Assert.Contains(d, set);
        // Aggressive names that can be real source are NOT hidden by default.
        Assert.DoesNotContain("vendor", set);
        Assert.DoesNotContain("packages", set);
        Assert.DoesNotContain("env", set);
    }

    [Fact]
    public void MergeIgnoreDirs_ConfigAdds_AndDashPrefixRemoves()
    {
        var set = FileEditController.MergeIgnoreDirs(new[] { "custom_dep", "-bin", "node_modules/.cache" });
        Assert.Contains("custom_dep", set);
        Assert.Contains(".cache", set);      // slash-separated entries split into segments
        Assert.DoesNotContain("bin", set);   // '-' un-hides a default
        Assert.Contains("obj", set);         // other defaults survive
    }

    [Fact]
    public void MergeIgnoreDirs_IsCaseInsensitive()
    {
        var set = FileEditController.MergeIgnoreDirs(new[] { "NODE_MODULES" });
        Assert.Contains("node_modules", set);
        set.Remove("bin");
        Assert.DoesNotContain("BIN", set);
    }
}
