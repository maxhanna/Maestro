using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
