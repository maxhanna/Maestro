using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Weaver.Controllers;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the SSE frame shape the client parses (wwwroot/agent.js's EventSource / fetch
/// stream reader: `event: <name>` / `data: <json>` / blank line). The audit flagged that
/// no test asserts the full event frame the pipeline emits — a shape regression (like
/// the diff-system one) has no net. SendSse is private static and frame-pure, so this
/// drives it directly against a real DefaultHttpContext response stream.
/// </summary>
public class SseEventShapeTests
{
    private static readonly MethodInfo SendSseMethod = typeof(AgentController).GetMethod(
        "SendSse", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SendSse static method not found.");

    private static string Emit(string eventName, object data)
    {
        var ctx = new DefaultHttpContext();
        using var stream = new MemoryStream();
        ctx.Response.Body = stream;
        var task = (Task)SendSseMethod.Invoke(null, new object?[]
        {
            ctx.Response, eventName, data, CancellationToken.None
        })!;
        task.GetAwaiter().GetResult();
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Frame_HasEventLine_DataLine_AndBlankTerminator()
    {
        var frame = Emit("phase", new { message = "Planning…" });
        Assert.StartsWith("event: phase\n", frame);
        Assert.Contains("\ndata: ", frame);
        Assert.EndsWith("\n\n", frame);
    }

    [Fact]
    public void DataLine_IsValidJson_WithThePayload()
    {
        var frame = Emit("context", new { contextSize = 2048, contextBreakdown = new object[] { new { name = "skeleton", tokens = 10 } } });
        var dataLine = frame.Split('\n').First(l => l.StartsWith("data: "));
        var json = dataLine["data: ".Length..];
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2048, doc.RootElement.GetProperty("contextSize").GetInt32());
        Assert.Equal("skeleton", doc.RootElement.GetProperty("contextBreakdown")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void MultipleFrames_AppendWithoutCorruption()
    {
        var ctx = new DefaultHttpContext();
        using var stream = new MemoryStream();
        ctx.Response.Body = stream;
        var invoke = (Action<string, object>)((name, data) =>
        {
            var task = (Task)SendSseMethod.Invoke(null, new object?[] { ctx.Response, name, data, CancellationToken.None })!;
            task.GetAwaiter().GetResult();
        });
        invoke("step", new { index = 1, type = "plan" });
        invoke("thinking", new { text = "second frame" });
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var all = reader.ReadToEnd();
        Assert.Contains("event: step\n", all);
        Assert.Contains("event: thinking\n", all);
        // Each frame is self-contained: exactly two blank-line terminators for two frames.
        Assert.Equal(2, all.Split("\n\n", StringSplitOptions.None).Length - 1);
    }

    /// <summary>
    /// Locks the plan-marker contract: the transient activity marker ("Deep thinking for
    /// plan — Step N…", "Proposing step N…", "Applying edits — Step N — …") must travel in
    /// a separate `marker` field — never as a row inside `items` — so the UI renders it as
    /// a bottom-of-plan status line instead of a checkable plan step that gets marked done
    /// while the step is still being produced.
    /// </summary>
    [Fact]
    public async Task PlanEvent_MarkerIsSeparate_NotAplanItem()
    {
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var ctx = new DefaultHttpContext();
        using var stream = new MemoryStream();
        ctx.Response.Body = stream;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        var method = typeof(AgentController).GetMethod("SendPlanActivityEventAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SendPlanActivityEventAsync not found");
        var planSoFar = new List<PlanStep>
        {
            new() { File = "_command", Change = "mkdir \"benchmark_test_22\"" },
            new() { File = "benchmark_test_22/index.html", Change = "Create index.html" }
        };
        // runningIndex = planSoFar.Count - 1: the step currently being produced (idx 1)
        // must NOT be marked done — only the completed steps before it are.
        var task = (Task)method.Invoke(controller, new object?[]
        {
            new StringBuilder(), planSoFar, true, "_planning",
            "Deep thinking for plan — Step 3…", "Deep thinking for plan — Step 3…",
            1, CancellationToken.None
        })!;
        await task;

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var frame = reader.ReadToEnd();
        Assert.StartsWith("event: plan\n", frame);
        var dataLine = frame.Split('\n').First(l => l.StartsWith("data: "));
        using var doc = JsonDocument.Parse(dataLine["data: ".Length..]);

        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var markerFiles = new[] { "_planning", "_executing", "_verifying", "_exploring" };
        foreach (var item in items.EnumerateArray())
        {
            var file = item.GetProperty("File").GetString();
            Assert.DoesNotContain(file, markerFiles);
        }
        // Committed steps keep their done semantics: completed step done, in-flight step not.
        Assert.True(items[0].GetProperty("done").GetBoolean());
        Assert.False(items[1].GetProperty("done").GetBoolean(),
            "the step currently being produced must not be marked done");

        var marker = doc.RootElement.GetProperty("marker");
        Assert.Equal("_planning", marker.GetProperty("File").GetString());
        Assert.Equal("Deep thinking for plan — Step 3…", marker.GetProperty("Change").GetString());
    }

    [Fact]
    public void EventNameWithDashes_PreservedVerbatim()
    {
        // The client switches on exact case labels (e.g. 'groundTruth', 'stepVerified',
        // 'verifiedComplete') — the emitter must never lowercase or mangle the name.
        var frame = Emit("verifiedComplete", new { reason = "done" });
        Assert.StartsWith("event: verifiedComplete\n", frame);
        Assert.Contains("data: {", frame);
    }
}
