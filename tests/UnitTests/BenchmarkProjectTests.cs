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

    // ── Regression comparison (acceptance-check deltas) ─────────────────────

    private static BenchmarkScore ScoreWithChecks(string id, double pct, double correctness, params (string name, bool passed)[] checks)
    {
        var s = new BenchmarkScore { Id = id, ScorePercent = pct, CorrectnessPercent = correctness };
        foreach (var (name, passed) in checks)
            s.Checks.Add(new BenchmarkCheckResult { Name = name, Passed = passed });
        return s;
    }

    [Fact]
    public void Compare_RealScores_CheckDeltasFlagRegressedAndFixed()
    {
        var baseline = ScoreWithChecks("base", 80, 80,
            ("A", true), ("B", false), ("C", true));
        var current = ScoreWithChecks("cur", 85, 90,
            ("A", false),  // regressed: passed before, fails now
            ("B", true),   // fixed: failed before, passes now
            ("C", true));  // stable

        var cmp = BenchmarkService.Compare(current, baseline);

        Assert.Equal("base", cmp.BaselineScoreId);
        Assert.Equal("cur", cmp.CurrentScoreId);
        Assert.Equal(5, cmp.ScoreDelta);        // 85 - 80
        Assert.Equal(10, cmp.CorrectnessDelta); // 90 - 80
        Assert.Equal(3, cmp.CheckDeltas.Count);
        Assert.Equal(1, cmp.RegressedChecks);   // A
        Assert.Equal(1, cmp.FixedChecks);      // B
        // The overall score ROSE but a check still regressed — HasRegression must catch it
        // (a recovered edit-success rate would otherwise mask the lost acceptance check).
        Assert.True(cmp.HasRegression);
        Assert.Contains(cmp.CheckDeltas, d => d.Name == "A" && d.Regressed);
        Assert.Contains(cmp.CheckDeltas, d => d.Name == "B" && d.Fixed);
    }

    [Fact]
    public void Compare_OldScoreHasNoChecks_DeltasAreNull_CheckDeltasEmpty()
    {
        // Scores saved before acceptance checks were wired into scoring carry no Checks and
        // 0 correctness — comparing one to a real run must NOT fabricate a correctness gain.
        var baseline = new BenchmarkScore { Id = "old", ScorePercent = 70, CorrectnessPercent = 0 };
        var current = ScoreWithChecks("new", 72, 95, ("A", true));

        var cmp = BenchmarkService.Compare(current, baseline);

        Assert.Equal(2, cmp.ScoreDelta);      // still computed from stored score numbers
        Assert.Null(cmp.CorrectnessDelta);     // no baseline checks → not comparable
        Assert.Null(cmp.EditSuccessDelta);
        Assert.Empty(cmp.CheckDeltas);
        Assert.Equal(0, cmp.RegressedChecks);
        Assert.False(cmp.HasRegression);       // score rose, no check regressions
    }
}
