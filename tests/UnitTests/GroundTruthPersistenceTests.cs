using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the computed-ground-truth persistence on the card: the post-execution
/// verification publishes its deterministic expectations (e.g. "the new CSS class must be
/// referenced in the template") to <c>card._groundTruth</c> so a human can see the
/// known-correct answer the run is being checked against — live via SSE and after a reload
/// via boarddata. Coverage locked in here:
///   • PublishGroundTruthAsync writes _groundTruth onto the card in boarddata
///   • A later PersistBoardDataPlanAsync rebuild (which only rebuilds _plan.items) must
///     NOT wipe the ground truth — it lives at card level, not inside the plan
///   • Empty item lists are a no-op (nothing computed → nothing to show)
///   • Unknown cardId is a silent no-op (no throw, no board write)
/// </summary>
public class GroundTruthPersistenceTests
{
    private static (AgentController controller, BoardDataService boardData, DatabaseService db) BuildHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-gt-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var db = new DatabaseService(Path.Combine(dir, "weaver.db"), dir, Path.Combine(dir, "weaverconfig.json"));
        var boardData = new BoardDataService(db, NullLogger<BoardDataService>.Instance);
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var field = typeof(AgentController).GetField("_boardData", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_boardData field not found");
        field.SetValue(controller, boardData);
        return (controller, boardData, db);
    }

    private static Task InvokePublishGroundTruth(AgentController controller, string cardId, List<string> items)
    {
        var method = typeof(AgentController).GetMethod("PublishGroundTruthAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PublishGroundTruthAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, items, false, CancellationToken.None })!;
    }

    private static Task InvokePersistPlan(AgentController controller, string cardId, List<PlanStep> steps)
    {
        var method = typeof(AgentController).GetMethod("PersistBoardDataPlanAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PersistBoardDataPlanAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, steps, false, CancellationToken.None, "test summary", 1, false })!;
    }

    private static string BoardWithCard(string cardId, string column, string? planItemsJson = null)
    {
        var board = new Dictionary<string, object?>
        {
            ["todo"] = new List<object>(),
            ["doing"] = new List<object>(),
            ["done"] = new List<object>(),
            ["archived"] = new List<object>(),
            ["selfImproving"] = new List<object>()
        };
        var card = new Dictionary<string, object?>
        {
            ["id"] = cardId,
            ["text"] = "task",
            ["filePath"] = "C:/x"
        };
        if (!string.IsNullOrEmpty(planItemsJson))
        {
            card["_plan"] = new Dictionary<string, object?>
            {
                ["items"] = JsonSerializer.Deserialize<JsonElement>(planItemsJson),
                ["summary"] = "",
                ["score"] = 0
            };
        }
        board[column] = new List<object> { card };
        return JsonSerializer.Serialize(board);
    }

    private static JsonElement? ReadCardProperty(string raw, string cardId, string property)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                if (card.TryGetProperty(property, out var prop)) return prop.Clone();
                return null;
            }
        }
        return null;
    }

    private static List<(string? text, bool? verified)>? ReadPlanItemGroundTruth(string? raw, string cardId, int index)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                if (!card.TryGetProperty("_plan", out var plan) ||
                    !plan.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("index", out var idx) || idx.GetInt32() != index) continue;
                    if (!item.TryGetProperty("groundTruth", out var gt) || gt.ValueKind != JsonValueKind.Array)
                        return new List<(string?, bool?)>();
                    return gt.EnumerateArray().Select(e =>
                    {
                        string? text = e.TryGetProperty("text", out var t) ? t.GetString() : null;
                        bool? verified = null;
                        if (e.TryGetProperty("verified", out var v) &&
                            v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            verified = v.GetBoolean();
                        return (text, verified);
                    }).ToList();
                }
            }
        }
        return null;
    }

    private static readonly List<string> SampleGroundTruth = new()
    {
        "Newly created CSS class '.flight-detail-body' in globe.component.css is never referenced in the connected template/component — the rule will never apply. Wire it up by adding the class to the markup.",
        "Template binding in globe.component.html references 'schedules' which is missing from the component class — add it as a property/method."
    };

    [Fact]
    public async Task PublishGroundTruth_WritesGroundTruthOntoCard()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "gt-card-1";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishGroundTruth(controller, cardId, SampleGroundTruth);

        var raw = await boardData.LoadRawAsync();
        var prop = ReadCardProperty(raw!, cardId, "_groundTruth");
        Assert.NotNull(prop);
        var items = prop!.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        Assert.Equal(SampleGroundTruth, items);
    }

    [Fact]
    public async Task GroundTruth_SurvivesPlanRebuild()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "gt-card-2";
        var planItems = "[{\"index\":0,\"file\":\"a.css\",\"change\":\"x\",\"done\":false}]";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing", planItems));
        await InvokePublishGroundTruth(controller, cardId, SampleGroundTruth);

        // A rebuild of _plan.items (the repair-loop persistence) must not touch the
        // card-level ground truth.
        await InvokePersistPlan(controller, cardId, new List<PlanStep>
        {
            new() { File = "b.html", Change = "wire it up", Priority = 1 }
        });

        var raw = await boardData.LoadRawAsync();
        var prop = ReadCardProperty(raw!, cardId, "_groundTruth");
        Assert.NotNull(prop);
        var items = prop!.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        Assert.Equal(SampleGroundTruth, items);
        // And the plan rebuild still happened.
        var plan = ReadCardProperty(raw!, cardId, "_plan");
        Assert.NotNull(plan);
    }

    [Fact]
    public async Task PersistPlan_Rebuild_PreservesVerifiedFlagsOnStepGroundTruth()
    {
        // Per-step ground truth verified flags are stamped at step completion. A plan rebuild
        // (repair-loop persistence) recomputes the expectation texts from the steps but must
        // keep the verified=true/false marks for matching (file|change) + anchor — otherwise
        // an already-completed step silently drops back to "unverified" on every repair pass.
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "gt-rebuild-1";
        const string planJson = """
        [
          {
            "index": 0, "file": "a/b/c.ts", "change": "Add the open() method", "done": true,
            "groundTruth": [
              { "text": "Expected: \"open() { }\" present in a/b/c.ts", "file": "a/b/c.ts", "anchor": "open() { }", "verified": true }
            ]
          }
        ]
        """;
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing", planJson));

        await InvokePersistPlan(controller, cardId, new List<PlanStep>
        {
            new() { File = "a/b/c.ts", Change = "Add the open() method", OldString = "close() { }", NewString = "open() { }" }
        });

        var raw = await boardData.LoadRawAsync();
        var gt = ReadPlanItemGroundTruth(raw!, cardId, index: 0);
        Assert.NotNull(gt);
        Assert.NotEmpty(gt);
        // The expectation text was recomputed from the step (same anchor) and the verified
        // mark from the previous item survived the rebuild.
        Assert.Contains("present in a/b/c.ts", string.Join("\n", gt!.Select(g => g.text)));
        Assert.All(gt, g => Assert.True(g.verified, "verified flag must survive the plan rebuild"));
    }

    [Fact]
    public async Task PublishGroundTruth_EmptyItems_IsNoOp()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "gt-card-3";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishGroundTruth(controller, cardId, new List<string>());

        var raw = await boardData.LoadRawAsync();
        Assert.Null(ReadCardProperty(raw!, cardId, "_groundTruth"));
    }

    [Fact]
    public async Task PublishGroundTruth_UnknownCard_IsSilentNoOp()
    {
        var (controller, boardData, _) = BuildHarness();
        await boardData.SaveRawAsync(BoardWithCard("known-card", "doing"));
        var before = await boardData.LoadRawAsync();

        await InvokePublishGroundTruth(controller, "missing-card", SampleGroundTruth);

        // No throw, board untouched.
        Assert.Equal(before, await boardData.LoadRawAsync());
    }
}
