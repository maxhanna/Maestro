using Xunit;
using Weaver;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for the benchmark project helpers — <see cref="BenchmarkService.ResolveBenchmarkRoot"/>
/// and <see cref="BenchmarkService.ResolveBenchmarkProjectEntry"/> — the logic that backs the
/// "Weaver Benchmarks" kanban project: created on first benchmark run (or when missing),
/// rooted at the benchmark project root, and reused/adopted on later runs.
/// </summary>
public class BenchmarkProjectTests
{
    // ── ResolveBenchmarkRoot ─────────────────────────────────────────────────

    [Fact]
    public void Root_CustomSet_ReturnsFullPathOfCustomRoot()
    {
        var custom = Path.GetFullPath(Path.Combine("benchmark_test_roots", "custom_sandbox"));
        var resolved = BenchmarkService.ResolveBenchmarkRoot(custom);
        Assert.Equal(BenchmarkService.NormalizeProjectPath(custom), resolved);
    }

    [Fact]
    public void Root_BlankOrNull_FallsBackToSandbox()
    {
        var fallback = BenchmarkService.ResolveBenchmarkRoot(null);
        Assert.False(string.IsNullOrWhiteSpace(fallback));
        Assert.EndsWith("benchmark_sandbox", fallback, StringComparison.OrdinalIgnoreCase);

        var fallback2 = BenchmarkService.ResolveBenchmarkRoot("   ");
        Assert.Equal(fallback, fallback2);
    }

    [Fact]
    public void Root_CustomRootWithTrailingSeparator_IsNormalized()
    {
        var basePath = Path.Combine("benchmark_test_roots", "custom_sandbox");
        var withTrailing = Path.GetFullPath(basePath) + Path.DirectorySeparatorChar;
        var resolved = BenchmarkService.ResolveBenchmarkRoot(withTrailing);
        Assert.Equal(BenchmarkService.NormalizeProjectPath(basePath), resolved);
        Assert.False(resolved.EndsWith(Path.DirectorySeparatorChar.ToString()));
    }

    // ── ResolveBenchmarkProjectEntry ─────────────────────────────────────────

    [Fact]
    public void Create_WhenMissing_AddsEntryRootedAtBenchmarkRoot()
    {
        var projects = new List<ProjectDto>();
        var root = BenchmarkService.NormalizeProjectPath(Path.Combine("benchmark_test_roots", "sandbox"));

        var (proj, created, updated) = BenchmarkService.ResolveBenchmarkProjectEntry(projects, root);

        Assert.True(created);
        Assert.False(updated);
        Assert.Single(projects);
        Assert.Equal("Weaver Benchmarks", proj.Name);
        Assert.Equal(root, proj.Path);
    }

    [Fact]
    public void Reuse_WhenPathAlreadyMatches_DoesNotDuplicate()
    {
        var root = BenchmarkService.NormalizeProjectPath(Path.Combine("benchmark_test_roots", "sandbox"));
        var projects = new List<ProjectDto>
        {
            new() { Name = "Weaver Benchmarks", Path = root }
        };

        var (proj, created, updated) = BenchmarkService.ResolveBenchmarkProjectEntry(projects, root);

        Assert.False(created);
        Assert.False(updated);
        Assert.Single(projects);
        Assert.Equal("Weaver Benchmarks", proj.Name);
    }

    [Fact]
    public void Reuse_WhenPathMatches_CaseAndTrailingSeparatorInsensitive()
    {
        var baseRoot = Path.Combine("benchmark_test_roots", "sandbox");
        var stored = BenchmarkService.NormalizeProjectPath(baseRoot);
        var projects = new List<ProjectDto>
        {
            new() { Name = "Other Name", Path = stored }
        };

        // Query with a different case and a trailing separator — must still match.
        var query = stored + Path.DirectorySeparatorChar;
        var upper = query.ToUpperInvariant();
        var (proj, created, updated) = BenchmarkService.ResolveBenchmarkProjectEntry(projects, upper);

        Assert.False(created);
        Assert.False(updated);
        Assert.Equal("Other Name", proj.Name); // matched by path, name untouched
        Assert.Single(projects);
    }

    [Fact]
    public void Adopt_WhenNameMatchesButPathStale_RepointsPath()
    {
        var staleRoot = BenchmarkService.NormalizeProjectPath(Path.Combine("benchmark_test_roots", "old"));
        var newRoot = BenchmarkService.NormalizeProjectPath(Path.Combine("benchmark_test_roots", "sandbox"));
        var projects = new List<ProjectDto>
        {
            new() { Name = "Weaver Benchmarks", Path = staleRoot }
        };

        var (proj, created, updated) = BenchmarkService.ResolveBenchmarkProjectEntry(projects, newRoot);

        Assert.False(created);
        Assert.True(updated);
        Assert.Equal(newRoot, proj.Path); // re-pointed at the actual benchmark root
        Assert.Single(projects);          // no duplicate entry
    }

    [Fact]
    public void Create_RepeatedCalls_AreIdempotent()
    {
        var projects = new List<ProjectDto>();
        var root = BenchmarkService.NormalizeProjectPath(Path.Combine("benchmark_test_roots", "sandbox"));

        var (_, firstCreated, _) = BenchmarkService.ResolveBenchmarkProjectEntry(projects, root);
        var (_, secondCreated, _) = BenchmarkService.ResolveBenchmarkProjectEntry(projects, root);
        var (proj, thirdCreated, _) = BenchmarkService.ResolveBenchmarkProjectEntry(projects, root);

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.False(thirdCreated);
        Assert.Single(projects);
        Assert.Equal("Weaver Benchmarks", proj.Name);
    }
}
