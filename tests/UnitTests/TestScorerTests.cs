using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

public class TestScorerTests
{
    static Dictionary<string, object?> Step(string type, string status, string? path = null, string? extraKey = null, string? extraVal = null)
    {
        var d = new Dictionary<string, object?> { ["type"] = type, ["status"] = status };
        if (path != null) d["path"] = path;
        if (extraKey != null) d[extraKey] = extraVal;
        return d;
    }

    static Dictionary<string, object?> StepWithOrigin(string type, string status, string origin, string? path = null)
    {
        var d = Step(type, status, path);
        d["origin"] = origin;
        return d;
    }

    static AgentPlan PlanOf(int stepCount)
    {
        var p = new AgentPlan();
        for (var i = 0; i < stepCount; i++)
            p.Plan.Add(new PlanStep { File = $"file{i}.cs", Change = "do thing" });
        return p;
    }

    [Fact]
    public void Score_MixedEditOutcomes_ComputesEditPointsPercentAndStatus()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.cs"),
            Step("create", "created", "b.cs"),
            Step("edit", "error", "c.cs"),
            Step("command", "done"), // not an edit-type step — excluded from edit counting
        };

        var r = TestScorer.Score("mixed", "card1", steps, PlanOf(4), complete: true,
            filesEdited: new[] { "a.cs", "b.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal(2, r.SuccessfulEdits);
        Assert.Equal(1, r.FailedEdits);
        // successful + (successful again, only if zero failures) — one failure here, so no bonus.
        Assert.Equal(2, r.Points);
        Assert.Equal(66.7, r.EditScorePercent, precision: 1);
        Assert.Equal("partial", r.Status);
    }

    [Fact]
    public void Score_AllEditsCleanNoFailures_DoublesPointsAndMarksCompleted()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.cs"),
            Step("rename", "applied", "b.cs"),
        };

        var r = TestScorer.Score("clean", "card1", steps, PlanOf(2), complete: true,
            filesEdited: new[] { "a.cs", "b.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal(2, r.SuccessfulEdits);
        Assert.Equal(0, r.FailedEdits);
        Assert.Equal(4, r.Points); // 2 successful, doubled since nothing failed
        Assert.Equal(100, r.EditScorePercent);
        Assert.Equal("completed", r.Status);
    }

    static TestRunResult ScoreWithAllowedPaths(string[] allowedPaths, params string[] filesEdited) =>
        TestScorer.Score("struct", "card1",
            new List<object> { Step("edit", "done") }, PlanOf(1), complete: true,
            filesEdited: filesEdited,
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { AllowedPaths = allowedPaths.ToList() });

    [Fact]
    public void StructurePreserved_EveryFileInsideAllowedPaths_IsTrue()
    {
        var r = ScoreWithAllowedPaths(
            new[] { "benchmark_0/**" },
            "benchmark_0/notes.txt", "benchmark_0/nested/deep/file.cs");

        Assert.True(r.Gates.StructurePreserved);
    }

    [Fact]
    public void StructurePreserved_FileOutsideAllowedPaths_IsFalse()
    {
        // The point of the gate: the agent wrote somewhere its card never sanctioned.
        var r = ScoreWithAllowedPaths(
            new[] { "benchmark_0/**" },
            "benchmark_0/ok.txt", "src/Program.cs");

        Assert.False(r.Gates.StructurePreserved);
    }

    [Fact]
    public void StructurePreserved_AbsolutePathsInsideProjectRoot_AreMatchedAgainstRelativeGlobs()
    {
        // Step paths reach the scorer absolute as often as relative (FormattingGate tests
        // Path.IsPathRooted for the same reason). allowedPaths are authored relative, so
        // without normalisation these legitimate files would fail the gate.
        var root = Path.Combine(Path.GetTempPath(), "weaver-sandbox");
        var r = TestScorer.Score("struct", "card1",
            new List<object> { Step("edit", "done") }, PlanOf(1), complete: true,
            filesEdited: new[]
            {
                Path.Combine(root, "benchmark_0", "notes.txt"),
                Path.Combine(root, "benchmark_0", "nested", "deep.cs"),
            },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { AllowedPaths = new List<string> { "benchmark_0/**" } },
            projectRoot: root);

        Assert.True(r.Gates.StructurePreserved);
    }

    [Fact]
    public void StructurePreserved_AbsolutePathEscapingProjectRoot_IsFalse()
    {
        // The escape this gate exists to catch. Relativising yields a "..\" prefix, which
        // matches no card-authored glob.
        var root = Path.Combine(Path.GetTempPath(), "weaver-sandbox");
        var r = TestScorer.Score("struct", "card1",
            new List<object> { Step("edit", "done") }, PlanOf(1), complete: true,
            filesEdited: new[] { Path.Combine(Path.GetTempPath(), "elsewhere", "stolen.txt") },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { AllowedPaths = new List<string> { "benchmark_0/**" } },
            projectRoot: root);

        Assert.False(r.Gates.StructurePreserved);
    }

    [Fact]
    public void StructurePreserved_SingleStarDoesNotCrossDirectoryBoundary()
    {
        // "*" must stay within one segment, otherwise an allowedPaths of "bm/*" would
        // silently sanction the entire subtree and the gate would wave through exactly
        // the escapes it exists to catch.
        var r = ScoreWithAllowedPaths(new[] { "bm/*" }, "bm/nested/file.txt");

        Assert.False(r.Gates.StructurePreserved);
    }

    // ── Red step 2 (seed manifest story): the actual manifests each ladder level will
    // ship, exercised the way TestScorer really sees them — via BenchmarkManifest, not a
    // hand-rolled allowedPaths array. Expected to fail until BenchmarkService.
    // GetBenchmarkPlans() attaches real manifests (Red step 1).

    static TestGateResults ScoreLadderRun(int level, params string[] editedPaths)
    {
        var plan = BenchmarkService.GetBenchmarkPlans().Single(p => p.Level == level);
        var r = TestScorer.Score("ladder", "card1",
            new List<object> { Step("edit", "done") }, PlanOf(1), complete: true,
            filesEdited: editedPaths,
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: plan.Benchmark);
        return r.Gates;
    }

    [Fact]
    public void LadderManifest_FileInsideOwnFolder_StructurePreservedTrue()
    {
        var gates = ScoreLadderRun(1, "benchmark_test_1/test.md");
        Assert.True(gates.StructurePreserved);
    }

    [Fact]
    public void LadderManifest_FileInSiblingBenchmarkFolder_StructurePreservedFalse()
    {
        // The escape that most plausibly happens in practice: the agent writes into a
        // neighbouring level's folder rather than truly outside the sandbox. A glob
        // authored too loosely (e.g. "benchmark_test_*/**") would wave this through.
        var gates = ScoreLadderRun(1, "benchmark_test_2/leaked.md");
        Assert.False(gates.StructurePreserved);
    }

    [Fact]
    public void LadderManifest_FileAtProjectRoot_StructurePreservedFalse()
    {
        var gates = ScoreLadderRun(1, "leaked_at_root.md");
        Assert.False(gates.StructurePreserved);
    }

    [Fact]
    public void Score_NoEditStepsAtAll_ReportsZeroPointsNotDivideByZero()
    {
        // A command-only card (e.g. "Benchmark 0" just creates a folder) attempts no
        // edits. The percentage has no denominator here, so it must report 0 rather
        // than NaN — NaN would serialise into the local store and the leaderboard.
        var steps = new List<object>
        {
            Step("command", "done"),
            Step("done_signal", "done"),
        };

        var r = TestScorer.Score("cmd-only", "card1", steps, PlanOf(2), complete: true,
            filesEdited: Array.Empty<string>(),
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal(0, r.SuccessfulEdits);
        Assert.Equal(0, r.FailedEdits);
        Assert.Equal(0, r.Points);
        Assert.Equal(0, r.EditScorePercent);
        Assert.Equal("failed", r.Status);
    }

    [Fact]
    public void Score_EveryEditFailed_ReportsFailedStatusAndNoPoints()
    {
        var steps = new List<object>
        {
            Step("edit", "error", "a.cs"),
            Step("create", "rejected", "b.cs"),
        };

        var r = TestScorer.Score("all-bad", "card1", steps, PlanOf(2), complete: false,
            filesEdited: Array.Empty<string>(),
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal(0, r.SuccessfulEdits);
        Assert.Equal(2, r.FailedEdits);
        Assert.Equal(0, r.Points);
        Assert.Equal(0, r.EditScorePercent);
        Assert.Equal("failed", r.Status);
        Assert.False(r.Passed);
    }

    [Fact]
    public void Score_AllStepsDone_IsPerfectAndPassing()
    {
        var steps = new List<object>
        {
            Step("create_file", "done", "tests.md"),
            Step("edit", "done", "tests.md"),
            Step("edit", "done", "src/a.cs"),
        };

        var r = TestScorer.Score("starter", "card1", steps, PlanOf(3), complete: true,
            filesEdited: new[] { "tests.md", "src/a.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal(3, r.TotalSteps);
        Assert.Equal(3, r.StepsPassed);
        Assert.Equal(100, r.Score);
        Assert.True(r.Passed);
        Assert.Null(r.FailedStep);
    }

    [Fact]
    public void Score_HaltedMidway_ScoresPartialProgressAndFails()
    {
        var steps = new List<object>
        {
            Step("create_file", "done", "tests.md"),
            Step("edit", "done", "tests.md"),
            new Dictionary<string, object?>
            {
                ["type"] = "plan_halted",
                ["status"] = "error",
                ["reason"] = "Fatal step failure: could not apply edit",
                ["failedFile"] = "src/hard.cs",
                ["remainingSteps"] = 2,
            },
        };

        var r = TestScorer.Score("starter", "card1", steps, PlanOf(4), complete: false,
            filesEdited: new[] { "tests.md" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal(4, r.TotalSteps);
        Assert.Equal(2, r.StepsPassed);
        Assert.Equal(50, r.Score);
        Assert.False(r.Passed);
        Assert.Equal("src/hard.cs", r.FailedStep);
        Assert.Contains("could not apply edit", r.FailureReason);
    }

    [Fact]
    public void Score_ErroredStepWithoutHaltMarker_StillCountsAsFailure()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.cs"),
            Step("edit", "error", "b.cs", extraKey: "error", extraVal: "boom"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: false,
            filesEdited: new[] { "a.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.False(r.Passed);
        Assert.Equal(1, r.StepsPassed);
        Assert.Equal(50, r.Score);
        Assert.Equal("b.cs", r.FailedStep);
        Assert.Equal("boom", r.FailureReason);
    }

    [Fact]
    public void Score_NoPlan_FallsBackToObservedStepCount()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.cs"),
            Step("edit", "done", "b.cs"),
        };

        var r = TestScorer.Score("t", null, steps, plan: null, complete: true,
            filesEdited: new[] { "a.cs", "b.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal(2, r.TotalSteps);
        Assert.Equal(100, r.Score);
        Assert.True(r.Passed);
    }

    [Fact]
    public void Score_IdentifiesWrittenTestsAndCodeFile()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "src/calc.cs"),
            Step("create_file", "done", "tests/CalcTests.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "src/calc.cs", "tests/CalcTests.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Equal("src/calc.cs", r.CodeFile);
        Assert.Contains("tests/CalcTests.cs", r.WrittenTests);
        Assert.DoesNotContain("src/calc.cs", r.WrittenTests);
    }

    [Fact]
    public void Score_NoBenchmarkManifest_GatesThatNeedItAreUnmeasuredAndNotPerfect()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.cs"),
            Step("edit", "done", "b.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "a.cs", "b.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6");

        Assert.Null(r.Gates.ExactStepCount);
        Assert.Null(r.Gates.StructurePreserved);
        Assert.True(r.Gates.PermissionsRespected);
        Assert.True(r.Gates.NoReplan);
        // Unmeasured gates count as not-perfect even though the run itself passed.
        Assert.True(r.Passed);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_OriginalPlanOnly_MatchingManifest_IsPerfectPass()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "src/a.cs"),
            Step("edit", "done", "src/b.cs"),
        };

        var benchmark = new BenchmarkManifest
        {
            ExpectedSteps = 2,
            AllowedPaths = new List<string> { "src/**" }
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "src/a.cs", "src/b.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6", benchmark: benchmark);

        Assert.True(r.Gates.ExactStepCount);
        Assert.True(r.Gates.StructurePreserved);
        Assert.True(r.Gates.NoReplan);
        Assert.Equal(2, r.PlannedSteps);
        Assert.Equal(2, r.ExpectedSteps);
        // FormattingClean is untouched by the sync Score() overload — still null (unmeasured).
        Assert.Null(r.Gates.FormattingClean);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_ReplanOriginStep_FailsNoReplanGateEvenIfComplete()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "src/a.cs"),
            StepWithOrigin("edit", "done", "replan", "src/b.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "src/a.cs", "src/b.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { ExpectedSteps = 2, AllowedPaths = new List<string> { "src/**" } });

        Assert.False(r.Gates.NoReplan);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_RepairOriginStep_FailsNoReplanGate()
    {
        var steps = new List<object>
        {
            StepWithOrigin("edit", "done", "repair", "src/a.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(1), complete: true,
            filesEdited: new[] { "src/a.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { ExpectedSteps = 1, AllowedPaths = new List<string> { "src/**" } });

        Assert.False(r.Gates.NoReplan);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_PlanExceedsExpectedSteps_FailsExactStepCountGate()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.cs"),
            Step("edit", "done", "b.cs"),
            Step("edit", "done", "c.cs"),
            Step("edit", "done", "d.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(4), complete: true,
            filesEdited: new[] { "a.cs", "b.cs", "c.cs", "d.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { ExpectedSteps = 3 });

        Assert.False(r.Gates.ExactStepCount);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_FileOutsideAllowedPaths_FailsStructurePreservedGate()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "src/a.cs"),
            Step("create_file", "done", "../outside/escape.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "src/a.cs", "../outside/escape.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { AllowedPaths = new List<string> { "src/**" } });

        Assert.False(r.Gates.StructurePreserved);
        Assert.False(r.PerfectPass);
    }

    [Fact]
    public void Score_MidPatternDoubleStarGlob_MatchesNestedPathAndRejectsSibling()
    {
        // Regression test for the allowedPaths glob matcher: a mid-pattern "**" (not
        // just a trailing one) must match arbitrarily-nested paths under it and must
        // NOT degrade into matching everything.
        var steps = new List<object>
        {
            Step("edit", "done", "src/deep/nested/dir/tests/CalcTests.cs"),
            Step("create_file", "done", "src/other/not-allowed.cs"),
        };

        var r = TestScorer.Score("t", null, steps, PlanOf(2), complete: true,
            filesEdited: new[] { "src/deep/nested/dir/tests/CalcTests.cs", "src/other/not-allowed.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            benchmark: new BenchmarkManifest { AllowedPaths = new List<string> { "src/**/tests/*.cs" } });

        // One matched, one didn't -> overall gate is false, proving the matcher
        // discriminates rather than matching (or rejecting) everything uniformly.
        Assert.False(r.Gates.StructurePreserved);
    }

    [Fact]
    public async Task ScoreAsync_FormattingModeNone_LeavesFormattingGateUnmeasured()
    {
        var steps = new List<object> { Step("edit", "done", "a.cs") };
        var benchmark = new BenchmarkManifest
        {
            ExpectedSteps = 1,
            AllowedPaths = new List<string> { "*.cs" },
            Formatting = new BenchmarkFormatting { Mode = "none" }
        };

        var r = await TestScorer.ScoreAsync("t", null, steps, PlanOf(1), complete: true,
            filesEdited: new[] { "a.cs" },
            machine: new EnvironmentMetadata(), weaverVersion: "6",
            projectRoot: Path.GetTempPath(), benchmark: benchmark);

        Assert.Null(r.Gates.FormattingClean);
        Assert.False(r.PerfectPass);
    }
}

public class AgentControllerTagStepOriginTests
{
    static Dictionary<string, object?> Step(string origin) =>
        new() { ["type"] = "edit", ["status"] = "done", ["origin"] = origin };

    static Dictionary<string, object?> UntaggedStep() =>
        new() { ["type"] = "edit", ["status"] = "done" };

    [Fact]
    public void TagStepOrigin_ChainedPipelineStage_TagsPreviouslyUntaggedSteps()
    {
        // Mirrors the CommandExecution -> UnifiedPipeline chaining branch in
        // AgentController: chainResult.steps are unplanned (the second stage only
        // runs because the first stage's commands produced files), so every step
        // that came back untagged must be marked "replan".
        var chainSteps = new List<object> { UntaggedStep(), UntaggedStep() };

        AgentController.TagStepOrigin(chainSteps, 0, "replan");

        Assert.All(chainSteps, s => Assert.Equal("replan", ((Dictionary<string, object?>)s)["origin"]));
    }

    [Fact]
    public void TagStepOrigin_ChainedPipelineStage_PreservesInternalRepairTag()
    {
        // If UnifiedPipeline already had to repair a step internally before
        // returning, that more specific "repair" tag must survive the outer
        // "replan" stamp applied at the chaining call site.
        var chainSteps = new List<object> { UntaggedStep(), Step("repair") };

        AgentController.TagStepOrigin(chainSteps, 0, "replan");

        var tags = chainSteps.Select(s => (string?)((Dictionary<string, object?>)s)["origin"]).ToList();
        Assert.Equal(new[] { "replan", "repair" }, tags);
    }
}

public class WeaverVersionTests
{
    [Fact]
    public void Read_PrefersProvidedDirOverFallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-ver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".weaver-version.txt"), "42\n");
            Assert.Equal("42", WeaverVersion.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Read_FindsDotWeaverVersionWithoutTxtExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-ver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".weaver-version"), "7");
            Assert.Equal("7", WeaverVersion.Read(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Read_EmptyProvidedDir_FallsThroughToNonEmptyValue()
    {
        // An empty provided dir must fall through to a fallback (base dir / LocalAppData
        // self-update copy / "0") rather than throwing or returning empty.
        var dir = Path.Combine(Path.GetTempPath(), "weaver-ver-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.False(string.IsNullOrWhiteSpace(WeaverVersion.Read(dir))); }
        finally { Directory.Delete(dir, true); }
    }
}

public class EnvironmentMetadataTests
{
    [Fact]
    public void Collect_PopulatesCoreFields()
    {
        var m = EnvironmentMetadata.Collect();
        Assert.False(string.IsNullOrWhiteSpace(m.Os));
        Assert.True(m.CpuCores > 0);
        Assert.False(string.IsNullOrWhiteSpace(m.Runtime));
    }
}
