using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for the improvement-suggestions persist/read helpers used by the
/// POST api/agent/suggest-improvements endpoint. These helpers are the
/// deterministic half of the feature (the LLM half can't be unit-tested):
/// a generated suggestion set must round-trip onto the card in board data and
/// be read back without regenerating.
///
/// Coverage locked in here:
///   • Persist → Read returns the same suggestions (idempotent round-trip)
///   • A card that already has suggestions returns them WITHOUT re-running
///   • A card that legitimately got 0 suggestions (empty array) is still
///     treated as "done" — the reader returns the empty list, not null, so
///     the LLM is never re-invoked for it.
/// </summary>
public class ImprovementSuggestionsTests
{
    private static (AgentController controller, DatabaseService db, string dbPath) BuildHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-sugg-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "weaver.db");
        var db = new DatabaseService(dbPath, dir, Path.Combine(dir, "weaverconfig.json"));
        var boardData = new BoardDataService(db, NullLogger<BoardDataService>.Instance);
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var field = typeof(AgentController).GetField("_boardData", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_boardData field not found");
        field.SetValue(controller, boardData);
        return (controller, db, dbPath);
    }

    private static void SeedBoard(DatabaseService db, string cardId, string column)
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
            new Dictionary<string, object?> { ["id"] = cardId, ["text"] = "task", ["filePath"] = "C:/x" }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(board);
        db.SetBoardData(json);
    }

    private static async Task<List<object>?> InvokeRead(AgentController controller, string cardId)
    {
        var method = typeof(AgentController).GetMethod("ReadCardSuggestionsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ReadCardSuggestionsAsync not found");
        var task = (Task<List<object>?>)method.Invoke(controller, new object?[] { cardId })!;
        return await task;
    }

    private static Task InvokePersist(AgentController controller, string cardId, List<object> suggestions)
    {
        var method = typeof(AgentController).GetMethod("PersistCardSuggestionsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PersistCardSuggestionsAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, suggestions })!;
    }

    [Fact]
    public async Task Persist_Then_Read_RoundTrips_Suggestions()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            SeedBoard(db, "card-1", "done");
            var suggestions = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "abc12345",
                    ["description"] = "Add error handling",
                    ["files"] = new List<string> { "src/app/globe.ts" },
                    ["createdAt"] = "2026-08-04T00:00:00Z"
                },
                new Dictionary<string, object?>
                {
                    ["id"] = "def67890",
                    ["description"] = "Extract a service",
                    ["files"] = new List<string>(),
                    ["createdAt"] = "2026-08-04T00:00:00Z"
                }
            };

            await InvokePersist(controller, "card-1", suggestions);
            var read = await InvokeRead(controller, "card-1");

            Assert.NotNull(read);
            Assert.Equal(2, read!.Count);
            var first = read[0] as System.Text.Json.JsonElement? ?? default;
            Assert.Contains("Add error handling", read[0].ToString());
            Assert.Contains("Extract a service", read[1].ToString());
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    [Fact]
    public async Task Read_On_Empty_Array_Returns_Empty_Not_Null()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            SeedBoard(db, "card-2", "todo");
            // A card that legitimately earned 0 suggestions stores an empty array.
            await InvokePersist(controller, "card-2", new List<object>());
            var read = await InvokeRead(controller, "card-2");
            // Must NOT be null: null would make the endpoint re-run the LLM.
            Assert.NotNull(read);
            Assert.Empty(read!);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    [Fact]
    public async Task Read_Returns_Null_When_No_Suggestions_Persisted()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            SeedBoard(db, "card-3", "selfImproving");
            var read = await InvokeRead(controller, "card-3");
            Assert.Null(read);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }
}
