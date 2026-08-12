using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the delivered-steer persistence on the card: a live steer that is actually
/// injected into the planner is appended to <c>card._steers</c> as a transcript —
/// message + the turn it became visible to — so a reload shows what was steered and
/// when, exactly like the ground-truth section. Coverage locked in here:
///   • PublishDeliveredSteerAsync writes the steer onto the card in boarddata with the turn
///   • Multiple delivered steers APPEND in order (a turn-2 steer then a turn-3 steer both
///     survive, newest last)
///   • A later PersistBoardDataPlanAsync rebuild (which only rebuilds _plan.items) must NOT
///     wipe _steers — it lives at card level, not inside the plan
///   • Empty message is a no-op; unknown cardId is a silent no-op
/// </summary>
public class SteerPersistenceTests
{
    private static (AgentController controller, BoardDataService boardData, DatabaseService db) BuildHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-steer-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var db = new DatabaseService(Path.Combine(dir, "weaver.db"), dir, Path.Combine(dir, "weaverconfig.json"));
        var boardData = new BoardDataService(db, NullLogger<BoardDataService>.Instance);
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var field = typeof(AgentController).GetField("_boardData", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_boardData field not found");
        field.SetValue(controller, boardData);
        return (controller, boardData, db);
    }

    private static Task InvokePublishSteer(AgentController controller, string cardId, string message, int turn)
    {
        var method = typeof(AgentController).GetMethod("PublishDeliveredSteerAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PublishDeliveredSteerAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, message, turn, false, CancellationToken.None })!;
    }

    private static Task InvokePersistPlan(AgentController controller, string cardId, List<PlanStep> steps)
    {
        var method = typeof(AgentController).GetMethod("PersistBoardDataPlanAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PersistBoardDataPlanAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, steps, false, CancellationToken.None, "test summary", 1, false })!;
    }

    private static string BoardWithCard(string cardId, string column)
    {
        var board = new Dictionary<string, object?>
        {
            ["todo"] = new List<object>(),
            ["doing"] = new List<object>(),
            ["done"] = new List<object>(),
            ["archived"] = new List<object>(),
            ["selfImproving"] = new List<object>()
        };
        board[column] = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["id"] = cardId,
                ["text"] = "task",
                ["filePath"] = "C:/x"
            }
        };
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

    private const string SteerMessage =
        "IMPORTANT USER UPDATE: I only asked for getItems(). Remove the getItemsCount() helper " +
        "you just proposed — it is unwanted scope.";

    [Fact]
    public async Task PublishSteer_WritesSteerWithTurnOntoCard()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "steer-card-1";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishSteer(controller, cardId, SteerMessage, turn: 3);

        var prop = ReadCardProperty((await boardData.LoadRawAsync())!, cardId, "_steers");
        Assert.NotNull(prop);
        var arr = prop!.Value.EnumerateArray().ToList();
        Assert.Single(arr);
        Assert.Equal(3, arr[0].GetProperty("turn").GetInt32());
        Assert.Equal(SteerMessage, arr[0].GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(arr[0].GetProperty("at").GetString()));
    }

    [Fact]
    public async Task PublishSteer_MultipleSteers_AppendInOrder()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "steer-card-2";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishSteer(controller, cardId, "turn two steer", turn: 2);
        await InvokePublishSteer(controller, cardId, SteerMessage, turn: 3);

        var prop = ReadCardProperty((await boardData.LoadRawAsync())!, cardId, "_steers");
        Assert.NotNull(prop);
        var arr = prop!.Value.EnumerateArray().ToList();
        Assert.Equal(2, arr.Count);
        Assert.Equal(2, arr[0].GetProperty("turn").GetInt32());
        Assert.Equal("turn two steer", arr[0].GetProperty("message").GetString());
        Assert.Equal(3, arr[1].GetProperty("turn").GetInt32());
        Assert.Equal(SteerMessage, arr[1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task PublishSteer_SurvivesPlanRebuild()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "steer-card-3";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishSteer(controller, cardId, SteerMessage, turn: 3);
        await InvokePersistPlan(controller, cardId, new List<PlanStep>
        {
            new() { File = "demo.ts", Change = "add getItems()", OldString = "ctor()", NewString = "ctor() { }" }
        });

        var raw = await boardData.LoadRawAsync();
        var steers = ReadCardProperty(raw!, cardId, "_steers");
        Assert.NotNull(steers);
        var arr = steers!.Value.EnumerateArray().ToList();
        Assert.Single(arr);
        Assert.Equal(SteerMessage, arr[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task PublishSteer_EmptyMessage_IsNoOp()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "steer-card-4";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishSteer(controller, cardId, "", turn: 2);

        Assert.Null(ReadCardProperty((await boardData.LoadRawAsync())!, cardId, "_steers"));
    }

    [Fact]
    public async Task PublishSteer_UnknownCard_IsSilentNoOp()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "steer-card-5";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishSteer(controller, "steer-card-unknown", SteerMessage, turn: 2);

        var raw = await boardData.LoadRawAsync();
        Assert.Null(ReadCardProperty(raw!, cardId, "_steers"));
        Assert.Null(ReadCardProperty(raw!, "steer-card-unknown", "_steers"));
    }
}
