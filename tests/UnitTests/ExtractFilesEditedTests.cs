using System.Reflection;
using System.Text.Json;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for <c>AgentController.ExtractFilesEdited</c> — the server-side "files changed"
/// list sent in the agent finish event. Regression: a file edited across multiple steps
/// produced one entry per step, and the duplicate paths crashed the client's ng-repeat
/// ('track by f.path' → ngRepeat:dupes). The extractor must collapse duplicates by path,
/// keeping the LAST edit per file (its preview is the final state).
/// </summary>
public class ExtractFilesEditedTests
{
    private static List<object> InvokeExtractFilesEdited(List<object> steps)
    {
        var method = typeof(AgentController).GetMethod(
            "ExtractFilesEdited", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ExtractFilesEdited not found");
        return (List<object>)method.Invoke(null, new object[] { steps })!;
    }

    private static Dictionary<string, object?> Step(string type, string status, string path, string? preview = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["status"] = status,
            ["path"] = path,
            ["editAction"] = "modified",
            ["toPath"] = null,
            ["linesAdded"] = 1,
            ["linesRemoved"] = 1
        };
        if (preview != null) d["diffPreview"] = preview;
        return d;
    }

    private static (string path, string preview) ReadEntry(object entry)
    {
        var json = JsonSerializer.Serialize(entry);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (
            root.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
            root.TryGetProperty("preview", out var pr) ? pr.GetString() ?? "" : "");
    }

    [Fact]
    public void SameFileEditedTwice_CollapsesToOneEntry_LastEditWins()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "maxhanna.client/src/app/favourites/favourites.component.css", "preview-1"),
            Step("edit", "done", "maxhanna.client/src/app/favourites/favourites.component.css", "preview-2")
        };
        var result = InvokeExtractFilesEdited(steps);
        var entry = Assert.Single(result);
        var (path, preview) = ReadEntry(entry);
        Assert.Equal("maxhanna.client/src/app/favourites/favourites.component.css", path);
        Assert.Equal("preview-2", preview);
    }

    [Fact]
    public void DistinctPaths_AllKept_InOrder()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "a.css"),
            Step("edit", "done", "b.ts"),
            Step("rename", "done", "c.html", "renamed")
        };
        var result = InvokeExtractFilesEdited(steps);
        Assert.Equal(3, result.Count);
        Assert.Equal("a.css", ReadEntry(result[0]).path);
        Assert.Equal("b.ts", ReadEntry(result[1]).path);
        Assert.Equal("c.html", ReadEntry(result[2]).path);
    }

    [Fact]
    public void RenameAndEditOfSamePath_CollapsesToLast()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "x.ts", "edit-preview"),
            Step("rename", "done", "x.ts", "rename-preview")
        };
        var result = InvokeExtractFilesEdited(steps);
        var entry = Assert.Single(result);
        Assert.Equal("rename-preview", ReadEntry(entry).preview);
    }

    [Fact]
    public void NonDoneOrNonEditSteps_Excluded()
    {
        var steps = new List<object>
        {
            Step("edit", "error", "broken.css", "err"),
            Step("command", "done", "run.sh"),
            Step("edit", "done", "ok.ts", "ok-preview")
        };
        var result = InvokeExtractFilesEdited(steps);
        var entry = Assert.Single(result);
        Assert.Equal("ok.ts", ReadEntry(entry).path);
    }

    [Fact]
    public void PathComparison_IsCaseInsensitive_AndSlashNormalized()
    {
        var steps = new List<object>
        {
            Step("edit", "done", "maxhanna.client\\Src\\App\\X.ts", "upper"),
            Step("edit", "done", "maxhanna.client/src/app/x.ts", "lower")
        };
        var result = InvokeExtractFilesEdited(steps);
        var entry = Assert.Single(result);
        Assert.Equal("lower", ReadEntry(entry).preview);
    }

    [Fact]
    public void EmptySteps_ReturnsEmpty()
    {
        Assert.Empty(InvokeExtractFilesEdited(new List<object>()));
    }
}
