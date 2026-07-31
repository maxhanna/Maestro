using Xunit;
using Weaver;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Covers the local TestRunResult store that backs "local history is always saved,
/// BugHosted upload is opt-in". Every assertion here is about data the user would
/// silently lose (or silently corrupt) if persistence regressed — the store is the
/// only durable record of a benchmark run.
/// </summary>
public class BenchmarkServiceTests : IDisposable
{
    readonly string _dataDir;
    readonly BenchmarkService _svc;

    public BenchmarkServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "weaver-bench-" + Guid.NewGuid().ToString("N"));
        _svc = new BenchmarkService(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    static TestRunResult Result(string testName = "Benchmark 1", string? id = null)
    {
        var r = new TestRunResult
        {
            TestName = testName,
            CardId = "card-abc",
            StepsPassed = 3,
            TotalSteps = 4,
            Score = 75,
            Passed = false,
            FailedStep = "src/a.cs",
            FailureReason = "boom",
            CodeFile = "src/a.cs",
            WrittenTests = new List<string> { "tests/a.spec.ts" },
            SuccessfulEdits = 2,
            FailedEdits = 1,
            Points = 2,
            EditScorePercent = 66.7,
            Status = "partial",
            WeaverVersion = "12",
            ExpectedSteps = 4,
            PlannedSteps = 4,
            Machine = new EnvironmentMetadata { Os = "TestOS", CpuCores = 8, RamGb = 32 },
            Model = new ModelInfo { Name = "test-model" }
        };
        if (id != null) r.Id = id;
        return r;
    }

    [Fact]
    public void SaveThenLoad_RoundTripsScoringFields()
    {
        _svc.SaveTestResult(Result());

        var loaded = Assert.Single(_svc.LoadTestResults());
        Assert.Equal("Benchmark 1", loaded.TestName);
        Assert.Equal(75, loaded.Score);
        Assert.Equal(3, loaded.StepsPassed);
        Assert.Equal(4, loaded.TotalSteps);
        Assert.Equal(2, loaded.SuccessfulEdits);
        Assert.Equal(1, loaded.FailedEdits);
        Assert.Equal(2, loaded.Points);
        Assert.Equal(66.7, loaded.EditScorePercent, precision: 1);
        Assert.Equal("partial", loaded.Status);
        Assert.False(loaded.Passed);
        Assert.Equal("12", loaded.WeaverVersion);
        Assert.Equal("test-model", loaded.Model?.Name);
        Assert.Equal("TestOS", loaded.Machine.Os);
        Assert.Equal("tests/a.spec.ts", Assert.Single(loaded.WrittenTests));
    }

    [Fact]
    public void SaveThenLoad_KeepsUnmeasuredGatesNullRatherThanFalse()
    {
        // The null/false distinction is the whole contract of a gate: null means
        // "could not be evaluated" and false means "evaluated and failed". If JSON
        // round-tripping collapsed null to false, a run would look measured-and-failed
        // instead of unmeasured, and PerfectPass would be computed off a lie.
        var r = Result();
        r.Gates = new TestGateResults
        {
            PermissionsRespected = true,
            NoReplan = false,
            FormattingClean = null,
            StructurePreserved = null,
            ExactStepCount = null
        };
        _svc.SaveTestResult(r);

        var loaded = Assert.Single(_svc.LoadTestResults());
        Assert.True(loaded.Gates.PermissionsRespected);
        Assert.False(loaded.Gates.NoReplan);
        Assert.Null(loaded.Gates.FormattingClean);
        Assert.Null(loaded.Gates.StructurePreserved);
        Assert.Null(loaded.Gates.ExactStepCount);
        Assert.False(loaded.Gates.AllTrue);
    }

    [Fact]
    public void Save_AppendsRatherThanOverwritingPreviousRuns()
    {
        _svc.SaveTestResult(Result("Benchmark 0"));
        _svc.SaveTestResult(Result("Benchmark 1"));
        _svc.SaveTestResult(Result("Benchmark 2"));

        var names = _svc.LoadTestResults().Select(r => r.TestName).ToList();
        Assert.Equal(new[] { "Benchmark 0", "Benchmark 1", "Benchmark 2" }, names);
    }

    [Fact]
    public void DeleteTestResult_RemovesOnlyTheMatchingRow()
    {
        _svc.SaveTestResult(Result("keep-me", id: "aaaa1111"));
        _svc.SaveTestResult(Result("delete-me", id: "bbbb2222"));

        Assert.True(_svc.DeleteTestResult("bbbb2222"));

        var remaining = Assert.Single(_svc.LoadTestResults());
        Assert.Equal("keep-me", remaining.TestName);
    }

    [Fact]
    public void DeleteTestResult_UnknownId_ReturnsFalseAndLeavesDataIntact()
    {
        _svc.SaveTestResult(Result(id: "aaaa1111"));

        Assert.False(_svc.DeleteTestResult("does-not-exist"));
        Assert.Single(_svc.LoadTestResults());
    }

    [Fact]
    public void LoadTestResults_NoFileYet_ReturnsEmptyInsteadOfThrowing()
    {
        // First ever run on a fresh machine: the Benchmarks panel must open, not 500.
        Assert.Empty(_svc.LoadTestResults());
    }

    [Fact]
    public void LoadTestResults_CorruptFile_ReturnsEmptyInsteadOfThrowing()
    {
        // A half-written file (killed mid-save) must not permanently break the panel.
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, "test_results.json"), "{ not valid json");

        Assert.Empty(_svc.LoadTestResults());
    }

    [Fact]
    public void SaveTestResult_CreatesDataDirectoryWhenMissing()
    {
        Assert.False(Directory.Exists(_dataDir));

        _svc.SaveTestResult(Result());

        Assert.True(File.Exists(Path.Combine(_dataDir, "test_results.json")));
    }

    [Fact]
    public void TestResults_AreStoredSeparatelyFromLegacyBenchmarkScores()
    {
        // Phase-4b decision: the pre-existing benchmark_scores.json stays as read-only
        // history and is never migrated or rewritten by the unified store.
        var legacyPath = Path.Combine(_dataDir, "benchmark_scores.json");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(legacyPath, "[{\"id\":\"legacy1\",\"level\":3}]");

        _svc.SaveTestResult(Result());
        _svc.DeleteTestResult(Result().Id);

        Assert.Equal("[{\"id\":\"legacy1\",\"level\":3}]", File.ReadAllText(legacyPath));
    }

    [Fact]
    public void DefaultFormatterCommands_UseAbsoluteToolPaths()
    {
        // FormattingGate runs with WorkingDirectory set to the benchmark sandbox, not the
        // Weaver install, so a repo-relative tool path would resolve against the wrong
        // directory and fail every file. Commands must therefore be self-locating.
        var commands = BenchmarkService.DefaultFormatterCommands(@"C:\weaver-install");

        Assert.NotEmpty(commands);
        foreach (var (ext, command) in commands)
        {
            Assert.Contains("{file}", command);
            // The tool is either resolved from PATH (python/dotnet) or given absolutely;
            // what must never appear is a path relative to the Weaver install.
            Assert.DoesNotContain("./.formatter", command);
            Assert.DoesNotContain(".formatter/node_modules", command.Replace('\\', '/').Replace("C:/weaver-install/", ""));
        }
    }

    [Fact]
    public void ResolveFormatterCommands_MachineOverrideWinsPerExtension()
    {
        var overrides = new CustomSystemInfo
        {
            FormatterCommands = new Dictionary<string, string> { ["py"] = "my-formatter {file}" }
        };

        var resolved = BenchmarkService.ResolveFormatterCommands(overrides, @"C:\weaver-install");

        Assert.Equal("my-formatter {file}", resolved["py"]);
        // Unrelated defaults survive rather than being replaced wholesale.
        Assert.True(resolved.ContainsKey("cs"));
    }

    [Fact]
    public void ResolveFormatterCommands_NoOverrides_FallsBackToDefaults()
    {
        var resolved = BenchmarkService.ResolveFormatterCommands(null, @"C:\weaver-install");

        Assert.Equal(BenchmarkService.DefaultFormatterCommands(@"C:\weaver-install").Count, resolved.Count);
    }

    [Fact]
    public void TestRunResult_GeneratesDistinctIds()
    {
        // Ids are the delete key — a collision would delete someone else's run.
        var ids = Enumerable.Range(0, 200).Select(_ => new TestRunResult().Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}

/// <summary>
/// Red step 1 (seed manifest story): every ladder plan must declare enough of a
/// BenchmarkManifest for structurePreserved and formattingClean to be measurable.
/// Written before BenchmarkPlanDefinition carries a manifest at all, so every test here
/// is expected to fail to compile / fail at runtime until that lands.
/// </summary>
public class BenchmarkPlanManifestTests
{
    [Fact]
    public void EveryBenchmarkPlan_CarriesAManifest()
    {
        foreach (var plan in BenchmarkService.GetBenchmarkPlans())
            Assert.NotNull(plan.Benchmark);
    }

    [Fact]
    public void EveryBenchmarkPlan_AllowedPathsMatchesItsOwnFolderName()
    {
        // The folder name embedded in each plan's Description is the ground truth the
        // agent will actually create. allowedPaths must match that literal name, or
        // structurePreserved fails every run regardless of what the agent does. This is
        // also what catches Level 5's malformed description (missing closing quote before
        // "Create 'datastructures.py'") — if the folder name can't be extracted cleanly,
        // the allowedPaths glob and the real folder will not agree.
        foreach (var plan in BenchmarkService.GetBenchmarkPlans())
        {
            var folderMatch = System.Text.RegularExpressions.Regex.Match(
                plan.Description, @"folder called '([^']+)'");
            Assert.True(folderMatch.Success,
                $"Level {plan.Level}: could not extract a single-quoted folder name from the description — " +
                "the description is malformed (see Level 5's original unbalanced quote).");

            var folder = folderMatch.Groups[1].Value;
            Assert.NotNull(plan.Benchmark);
            Assert.Contains(plan.Benchmark!.AllowedPaths, p => p == $"{folder}/**");
        }
    }

    [Fact]
    public void EveryBenchmarkPlan_DeclaresFormattingModeFormatter()
    {
        foreach (var plan in BenchmarkService.GetBenchmarkPlans())
        {
            Assert.NotNull(plan.Benchmark?.Formatting);
            Assert.Equal("formatter", plan.Benchmark!.Formatting!.Mode);
        }
    }

    [Fact]
    public void EveryBenchmarkPlan_DeclaredExtensionsAreResolvableByDefaultCommands()
    {
        // A manifest that requires an extension no default command covers would make
        // formattingClean permanently null on a fresh install — shipping that would be
        // indistinguishable from not having fixed the gate at all.
        //
        // Deliberately uses the real repo root, not a synthetic path: prettier-covered
        // extensions (js/html/css/md) are only advertised when .formatter/node_modules
        // actually exists at the given root (see DefaultFormatterCommands), so asserting
        // against a fake path would be a false red for every non-Python level regardless
        // of whether the real manifests are correct. This test therefore also requires
        // .formatter/node_modules to be installed — the same prerequisite `dotnet build`
        // already requires for the gate to be exercisable at all.
        var defaults = BenchmarkService.DefaultFormatterCommands(FindRepoRoot());
        foreach (var plan in BenchmarkService.GetBenchmarkPlans())
        {
            Assert.NotNull(plan.Benchmark?.Formatting);
            Assert.NotEmpty(plan.Benchmark!.Formatting!.Extensions);
            foreach (var ext in plan.Benchmark.Formatting.Extensions)
                Assert.True(defaults.ContainsKey(ext),
                    $"Level {plan.Level} requires '.{ext}' but DefaultFormatterCommands has no entry for it " +
                    "(is .formatter/node_modules installed? run `npm install` in .formatter/).");
        }
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Weaver.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Weaver.sln) from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Level0_EditsAtLeastOneFileSoFormattingIsMeasurable()
    {
        // Level 0 originally only created a directory. filesEdited would be empty, so
        // FormattingGate.CheckAsync's editedPaths.Count==0 guard returns null — the gate
        // is structurally unmeasurable no matter what formatter config exists. The fix is
        // to give it a file to write, same shape as every other level.
        var level0 = BenchmarkService.GetBenchmarkPlans().Single(p => p.Level == 0);
        Assert.Matches(@"[Cc]reate.*(a |the )?file", level0.Description);
    }
}
