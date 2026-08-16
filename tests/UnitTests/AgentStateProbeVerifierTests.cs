using Weaver;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks AgentStateProbeVerifier — the deterministic loop that turns a live browser read of a
/// benchmark's canvas/animation state global (<c>window.legCount = 4 (live canvas/animation state)</c>)
/// into (a) a CONFIRMED verifier issue when the task requires a different value and (b) a
/// fully-resolved oldString/newString edit that lands the fix without an LLM round-trip.
/// </summary>
public class AgentStateProbeVerifierTests
{
    private const string Benchmark23Prompt =
        "Start by drawing EXACTLY 4 legs. The page MUST expose window.legCount. " +
        "STEP 3 — edit the animation to add 2 more legs — window.legCount must equal 6.";

    // ── CheckLiveStateMismatch ───────────────────────────────────────────────

    [Fact]
    public void CheckLiveStateMismatch_NoExpectation_ReturnsNull()
    {
        Assert.Null(AgentStateProbeVerifier.CheckLiveStateMismatch(
            "fix the button", "[info] window.legCount = 4 (live canvas/animation state)"));
        Assert.Null(AgentStateProbeVerifier.CheckLiveStateMismatch(null, "anything"));
        Assert.Null(AgentStateProbeVerifier.CheckLiveStateMismatch(Benchmark23Prompt, null));
        Assert.Null(AgentStateProbeVerifier.CheckLiveStateMismatch(Benchmark23Prompt, ""));
    }

    [Fact]
    public void CheckLiveStateMismatch_LiveMatchesRequired_ReturnsNull()
    {
        Assert.Null(AgentStateProbeVerifier.CheckLiveStateMismatch(
            Benchmark23Prompt, "[info] window.legCount = 6 (live canvas/animation state)"));
    }

    [Fact]
    public void CheckLiveStateMismatch_LiveDiffers_ReturnsIssue()
    {
        var issue = AgentStateProbeVerifier.CheckLiveStateMismatch(
            Benchmark23Prompt, "[info] window.legCount = 4 (live canvas/animation state)");
        Assert.NotNull(issue);
        Assert.Contains("window.legCount is 4", issue);
        Assert.Contains("requires 6", issue);
    }

    [Fact]
    public void CheckLiveStateMismatch_LastProbeWins()
    {
        // A re-run appends a fresh probe; the final read is the one that counts.
        var output =
            "[info] window.legCount = 4 (live canvas/animation state)\n" +
            "[info] window.legCount = 6 (live canvas/animation state)";
        Assert.Null(AgentStateProbeVerifier.CheckLiveStateMismatch(Benchmark23Prompt, output));
    }

    // ── Repair-step synthesis ─────────────────────────────────────────────────

    [Fact]
    public void TryParseMismatch_RoundTrips()
    {
        var issue = AgentStateProbeVerifier.CheckLiveStateMismatch(
            Benchmark23Prompt, "[info] window.legCount = 4 (live canvas/animation state)");
        var parsed = AgentStateProbeVerifier.TryParseMismatch(issue);
        Assert.NotNull(parsed);
        Assert.Equal(("legCount", "4", "6"), parsed!.Value);
        Assert.Null(AgentStateProbeVerifier.TryParseMismatch("unrelated verifier issue"));
        Assert.Null(AgentStateProbeVerifier.TryParseMismatch(null));
    }

    [Fact]
    public void BuildStateRepairEdits_AssignmentAndLoop()
    {
        const string file =
            "<script>\n" +
            " // Initialize leg count\n" +
            " window.legCount =4;\n" +
            " if(window.legCount >=4) {\n" +
            "  for(let i=0;i<4;++i){\n" +
            "   drawLeg(i);\n" +
            "  }\n" +
            " }\n" +
            "</script>\n";

        var edits = AgentStateProbeVerifier.BuildStateRepairEdits(file, "legCount", "4", "6");
        Assert.NotNull(edits);
        Assert.Equal(2, edits!.Count);

        // Assignment edit preserves the "=4" spacing → "=6".
        Assert.Contains(edits, e => e.OldString == "window.legCount =4" && e.NewString == "window.legCount =6");
        // Loop edit rewrites the hardcoded bound to follow the global.
        Assert.Contains(edits, e => e.OldString == "i<4" && e.NewString == "i<window.legCount");
    }

    [Fact]
    public void BuildStateRepairEdits_OnlyAssignmentWhenLoopAlreadyFollowsGlobal()
    {
        const string file = "window.legCount = 4;\nfor(let i=0;i<window.legCount;i++) drawLeg(i);\n";
        var edits = AgentStateProbeVerifier.BuildStateRepairEdits(file, "legCount", "4", "6");
        Assert.NotNull(edits);
        Assert.Single(edits!);
        Assert.Equal("window.legCount = 4", edits![0].OldString);
        Assert.Equal("window.legCount = 6", edits[0].NewString);
    }

    [Fact]
    public void BuildStateRepairEdits_NoMatchingAssignment_ReturnsNull()
    {
        Assert.Null(AgentStateProbeVerifier.BuildStateRepairEdits(
            "const x = 1;", "legCount", "4", "6"));
        Assert.Null(AgentStateProbeVerifier.BuildStateRepairEdits("", "legCount", "4", "6"));
    }

    [Fact]
    public void TryBuildStateRepairStep_LandsTheEdit()
    {
        var root = Path.Combine(Path.GetTempPath(), "stateprobe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rel = Path.Combine("benchmark_test_23", "index.html");
            var full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full,
                "<script>\n window.legCount =4;\n for(let i=0;i<4;++i){ drawLeg(i); }\n</script>\n");

            var issue = AgentStateProbeVerifier.CheckLiveStateMismatch(
                Benchmark23Prompt, "[info] window.legCount = 4 (live canvas/animation state)");
            // The run's edited path is the candidate (even without a filesystem scan).
            var results = new List<object>
            {
                new Dictionary<string, object?> { ["type"] = "edit", ["path"] = rel }
            };

            var step = AgentStateProbeVerifier.TryBuildStateRepairStep(root, issue, results);
            Assert.NotNull(step);
            Assert.NotNull(step!.Edits);
            Assert.True(step.Edits!.Count >= 1);

            // Apply the batch the same way the deterministic-batch path does, and confirm the file changed.
            var content = File.ReadAllText(full);
            foreach (var e in step.Edits)
                content = content.Replace(e.OldString, e.NewString);
            Assert.Contains("window.legCount =6", content);
            Assert.Contains("i<window.legCount", content);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryBuildStateRepairStep_NonMismatchIssue_ReturnsNull()
    {
        Assert.Null(AgentStateProbeVerifier.TryBuildStateRepairStep(
            Path.GetTempPath(), "some unrelated issue", new List<object>()));
        Assert.Null(AgentStateProbeVerifier.TryBuildStateRepairStep(
            Path.GetTempPath(), null, new List<object>()));
    }
}
