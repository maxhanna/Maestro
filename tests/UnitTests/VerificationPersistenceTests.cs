using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the final verification verdict persistence on the card: after a run, the reason it
/// was (or wasn't) verified complete is written to <c>card._verification</c> ({ complete,
/// reason, at }) so the user sees it on the card instead of only in the log. Coverage
/// locked in here (mirrors GroundTruthPersistenceTests):
///   • PublishVerificationAsync writes the verdict onto the card in boarddata
///   • An incomplete run with no reason gets the deterministic fallback reason
///   • A later PersistBoardDataPlanAsync rebuild must NOT wipe it (lives at card level)
///   • Unknown cardId is a silent no-op (no throw, no board write)
/// </summary>
public class VerificationPersistenceTests
{
    private static (AgentController controller, BoardDataService boardData, DatabaseService db) BuildHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-verif-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var db = new DatabaseService(Path.Combine(dir, "weaver.db"), dir, Path.Combine(dir, "weaverconfig.json"));
        var boardData = new BoardDataService(db, NullLogger<BoardDataService>.Instance);
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var field = typeof(AgentController).GetField("_boardData", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_boardData field not found");
        field.SetValue(controller, boardData);
        return (controller, boardData, db);
    }

    private static Task InvokePublishVerification(AgentController controller, string cardId, bool complete, string? reason)
    {
        var method = typeof(AgentController).GetMethod("PublishVerificationAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PublishVerificationAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, complete, reason, false, CancellationToken.None })!;
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
        var card = new Dictionary<string, object?>
        {
            ["id"] = cardId,
            ["text"] = "task",
            ["filePath"] = "C:/x"
        };
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

    [Fact]
    public async Task PublishVerification_WritesCompleteVerdictOntoCard()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "verif-card-1";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishVerification(controller, cardId, true, "Verified: the new class is wired into the template.");

        var raw = await boardData.LoadRawAsync();
        var prop = ReadCardProperty(raw!, cardId, "_verification");
        Assert.NotNull(prop);
        Assert.True(prop!.Value.GetProperty("complete").GetBoolean());
        Assert.Equal("Verified: the new class is wired into the template.", prop.Value.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(prop.Value.GetProperty("at").GetString()));
    }

    [Fact]
    public async Task PublishVerification_IncompleteRun_GetsFallbackReason()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "verif-card-2";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));

        await InvokePublishVerification(controller, cardId, false, null);

        var raw = await boardData.LoadRawAsync();
        var prop = ReadCardProperty(raw!, cardId, "_verification");
        Assert.NotNull(prop);
        Assert.False(prop!.Value.GetProperty("complete").GetBoolean());
        Assert.Contains("did not pass", prop.Value.GetProperty("reason").GetString()!);
    }

    [Fact]
    public async Task PublishVerification_SurvivesPlanRebuild()
    {
        var (controller, boardData, _) = BuildHarness();
        const string cardId = "verif-card-3";
        await boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        await InvokePublishVerification(controller, cardId, true, "Verified complete.");

        await InvokePersistPlan(controller, cardId, new List<PlanStep>
        {
            new() { File = "b.html", Change = "wire it up", Priority = 1 }
        });

        var raw = await boardData.LoadRawAsync();
        var prop = ReadCardProperty(raw!, cardId, "_verification");
        Assert.NotNull(prop);
        Assert.True(prop!.Value.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task PublishVerification_UnknownCard_IsSilentNoOp()
    {
        var (controller, boardData, _) = BuildHarness();
        await boardData.SaveRawAsync(BoardWithCard("known-card", "doing"));
        var before = await boardData.LoadRawAsync();

        await InvokePublishVerification(controller, "missing-card", true, "anything");

        // No throw, board untouched.
        Assert.Equal(before, await boardData.LoadRawAsync());
    }
}
