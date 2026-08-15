using System.Text.Json.Nodes;
using Weaver.Controllers;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the run-unique step-index remapping on the agent SSE stream. Every pipeline phase
/// used to restart its own 0-based step counter (discovery=0, plan=0, command=0, …), which
/// made the frontend guess (type + index + descriptor) to tell a step update apart from a
/// new step. The backend now draws every 'step' event's index from ONE monotonic per-run
/// counter (AgentController.RemapStepIndex), so the client dedupes on `index` alone.
/// </summary>
public class StepIndexRemapTests
{
    private static int IndexOf(JsonObject obj) => obj["index"]!.GetValue<int>();

    private static Dictionary<string, object?> Dict(params object?[] kv)
    {
        var d = new Dictionary<string, object?>();
        for (var i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]!] = kv[i + 1];
        return d;
    }

    [Fact]
    public void CollidingPerPhaseIndices_RemapToRunUniqueIndices()
    {
        var ctx = new AgentController.StepIndexContext();
        // Four steps whose per-phase indices collide (three different 0s).
        var list = AgentController.RemapStepIndex(Dict("type", "list", "index", 0, "description", "list root", "status", "done"), ctx);
        var cmdA = AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "command", "mkdir bench", "status", "done"), ctx);
        var cmdB = AgentController.RemapStepIndex(Dict("type", "command", "index", 1, "command", "node server.js", "status", "done"), ctx);
        var plan = AgentController.RemapStepIndex(Dict("type", "plan", "index", 0, "description", "write index.html", "status", "done"), ctx);

        Assert.Equal(0, IndexOf(list));
        Assert.Equal(1, IndexOf(cmdA));
        Assert.Equal(2, IndexOf(cmdB));
        Assert.Equal(3, IndexOf(plan));
    }

    [Fact]
    public void RunningThenDone_SameStep_ReusesSameIndex()
    {
        var ctx = new AgentController.StepIndexContext();
        var running = AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "description", "mkdir bench", "status", "running"), ctx);
        // The done event appends command/output but keeps type + index + description.
        var done = AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "description", "mkdir bench", "command", "mkdir bench", "status", "done", "output", "ok"), ctx);

        Assert.Equal(0, IndexOf(running));
        Assert.Equal(IndexOf(running), IndexOf(done));
    }

    [Fact]
    public void DonePayload_ReusesTheSameMappingAsLiveStream()
    {
        var ctx = new AgentController.StepIndexContext();
        // Live stream: list (phase 1) and command (phase 2), both with per-phase index 0.
        AgentController.RemapStepIndex(Dict("type", "list", "index", 0, "description", "list root", "status", "done"), ctx);
        var cmdLive = AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "command", "mkdir", "status", "done"), ctx);

        // The 'done' event carries the ORIGINAL (unmutated) per-phase dicts.
        var allSteps = new List<object>
        {
            Dict("type", "list", "index", 0, "description", "list root", "status", "done"),
            Dict("type", "command", "index", 0, "command", "mkdir", "status", "done")
        };
        var remapped = AgentController.RemapDoneSteps(allSteps, ctx);

        Assert.Equal(0, IndexOf((JsonObject)remapped[0]));
        Assert.Equal(IndexOf(cmdLive), IndexOf((JsonObject)remapped[1]));
    }

    [Fact]
    public void MissingIndex_AssignsAFreshUniqueIndex()
    {
        var ctx = new AgentController.StepIndexContext();
        var a = AgentController.RemapStepIndex(new Dictionary<string, object?> { ["type"] = "command", ["command"] = "a", ["status"] = "done" }, ctx);
        var b = AgentController.RemapStepIndex(new Dictionary<string, object?> { ["type"] = "command", ["command"] = "b", ["status"] = "done" }, ctx);

        Assert.Equal(0, IndexOf(a));
        Assert.Equal(1, IndexOf(b));
    }

    [Fact]
    public void ClosedReEmission_AfterDone_SameKey_ReusesIndex()
    {
        // Regression: the interleaved loop re-sends each just-completed step's result (now
        // tagged with its global planItemIndex) AFTER ExecutePlan already streamed the same
        // running→done pair. Without this, the second done got a FRESH index and the panel
        // showed every interleaved command twice (both with full output) — the commands were
        // executed once, reported twice.
        var ctx = new AgentController.StepIndexContext();
        AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "command", "mkdir bench", "status", "running"), ctx);
        var done = AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "command", "mkdir bench", "status", "done", "output", "ok"), ctx);
        var reSent = AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "command", "mkdir bench", "status", "done", "output", "ok", "planItemIndex", 3), ctx);

        Assert.Equal(0, IndexOf(done));
        Assert.Equal(IndexOf(done), IndexOf(reSent));
    }

    [Fact]
    public void ClosedReEmission_WithoutPrecedingRunning_AlsoReuses()
    {
        // Some steps (e.g. read) stream ONLY a done event, then the loop re-sends the same
        // dict with planItemIndex — the closed-after-closed rule must reuse there too.
        var ctx = new AgentController.StepIndexContext();
        var first = AgentController.RemapStepIndex(Dict("type", "read", "index", 0, "path", "Program.cs", "status", "done"), ctx);
        var reSent = AgentController.RemapStepIndex(Dict("type", "read", "index", 0, "path", "Program.cs", "status", "done", "planItemIndex", 1), ctx);

        Assert.Equal(IndexOf(first), IndexOf(reSent));
    }

    [Fact]
    public void ReRunSameCommand_LaterInRun_GetsAFreshIndex()
    {
        // A genuine re-run re-OPENS with a fresh running event after the first lifecycle
        // closed — that opening is the discriminator, so the re-run gets a NEW index and is
        // never merged into the first execution (which is what a re-run of the same command
        // must show as a separate entry).
        var ctx = new AgentController.StepIndexContext();
        AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "command", "npm test", "status", "running"), ctx);
        var first = AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "command", "npm test", "status", "done"), ctx);
        AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "command", "npm test", "status", "running"), ctx);
        var second = AgentController.RemapStepIndex(Dict("type", "command", "index", 0, "command", "npm test", "status", "done"), ctx);

        Assert.Equal(0, IndexOf(first));
        Assert.Equal(1, IndexOf(second));
    }
}
