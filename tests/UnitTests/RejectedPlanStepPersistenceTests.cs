using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the rejected-step persistence on the card's plan: web-gate vetoes and the
/// deterministic step guards (OS-task, fetch-command, duplicate) call
/// PersistRejectedPlanStepAsync to append {status:"rejected", error} records, and
/// PersistBoardDataPlanAsync must PRESERVE those records (with re-anchored indexes)
/// whenever it rebuilds _plan.items from the committed plan — otherwise the next
/// persist would silently wipe the reason before a reload.
///
/// Coverage locked in here:
///   • A first rejection on a card with no plan creates the container and lands
///   • Rebuild preserves rejected records, re-anchoring their indexes to the tail
///   • Non-rejected existing items are NOT preserved across a rebuild (only the
///     committed plan + rejected records survive)
///   • append=true does not duplicate rejected records
/// </summary>
public class RejectedPlanStepPersistenceTests
{
    private static (AgentController controller, DatabaseService db, string dir) BuildHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-rejplan-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "weaver.db");
        var db = new DatabaseService(dbPath, dir, Path.Combine(dir, "weaverconfig.json"));
        var boardData = new BoardDataService(db, NullLogger<BoardDataService>.Instance);
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var field = typeof(AgentController).GetField("_boardData", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_boardData field not found");
        field.SetValue(controller, boardData);
        return (controller, db, dir);
    }

    private static Task InvokePersistRejected(AgentController controller, string cardId, PlanStep step, string error)
    {
        var method = typeof(AgentController).GetMethod("PersistRejectedPlanStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PersistRejectedPlanStepAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, step, error, false, CancellationToken.None })!;
    }

    private static Task InvokePersistPlan(AgentController controller, string cardId, List<PlanStep> steps, bool append = false)
    {
        var method = typeof(AgentController).GetMethod("PersistBoardDataPlanAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PersistBoardDataPlanAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, steps, false, CancellationToken.None, "test summary", 1, append })!;
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

    private static JsonElement? ReadPlanItems(DatabaseService db, string cardId)
    {
        var raw = db.GetBoardData();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                if (card.TryGetProperty("_plan", out var plan) && plan.ValueKind == JsonValueKind.Object &&
                    plan.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    return items.Clone();
                return null; // card found but no plan items
            }
        }
        return null;
    }

    [Fact]
    public async Task PersistRejectedPlanStepAsync_CreatesPlanContainerAndAppendsRecord()
    {
        var (controller, db, dir) = BuildHarness();
        try
        {
            // The card has NO _plan yet — a rejection on the very first proposal must
            // still land on the card (the helper creates the container).
            db.SetBoardData(BoardWithCard("card-1", "doing"));
            var step = new PlanStep { File = "_web_search", Change = "AI research articles published in last month" };
            const string error = "Task does not need current external info. Classifier: no fresh info needed";
            await InvokePersistRejected(controller, "card-1", step, error);

            var items = ReadPlanItems(db, "card-1");
            Assert.NotNull(items);
            Assert.Equal(1, items!.Value.GetArrayLength());
            var item = items.Value[0];
            Assert.Equal(0, item.GetProperty("index").GetInt32());
            Assert.Equal("_web_search", item.GetProperty("file").GetString());
            Assert.Equal("AI research articles published in last month", item.GetProperty("change").GetString());
            Assert.False(item.GetProperty("done").GetBoolean());
            Assert.Equal("rejected", item.GetProperty("status").GetString());
            Assert.Equal(error, item.GetProperty("error").GetString());
        }
        finally { try { Directory.Delete(dir, true); } catch { /* db file stays locked — best effort */ } }
    }

    [Fact]
    public async Task PersistBoardDataPlanAsync_RebuildPreservesRejectedRecordWithReanchoredIndex()
    {
        var (controller, db, dir) = BuildHarness();
        try
        {
            // Pre-existing plan: one done normal item (must NOT survive the rebuild)
            // and one rejected record (must survive, with its index re-anchored).
            db.SetBoardData(BoardWithCard("card-2", "doing",
                "[{\"index\":0,\"file\":\"src/old.cs\",\"change\":\"old normal edit\",\"line\":5,\"done\":true}," +
                 "{\"index\":1,\"file\":\"_web_search\",\"change\":\"old search\",\"line\":0,\"done\":false,\"status\":\"rejected\",\"error\":\"Classifier: stale\"}]"));

            var planSteps = new List<PlanStep>
            {
                new() { File = "src/a.cs", Change = "new edit A", LineNumber = 3 },
                new() { File = "src/b.cs", Change = "new edit B", LineNumber = 7 }
            };
            await InvokePersistPlan(controller, "card-2", planSteps);

            var items = ReadPlanItems(db, "card-2");
            Assert.NotNull(items);
            Assert.Equal(3, items!.Value.GetArrayLength());

            // The two committed steps replaced the plan wholesale (the old done item is gone).
            var stepA = items.Value[0];
            Assert.Equal(0, stepA.GetProperty("index").GetInt32());
            Assert.Equal("src/a.cs", stepA.GetProperty("file").GetString());
            Assert.Equal("new edit A", stepA.GetProperty("change").GetString());
            Assert.False(stepA.GetProperty("done").GetBoolean());
            var stepB = items.Value[1];
            Assert.Equal(1, stepB.GetProperty("index").GetInt32());
            Assert.Equal("src/b.cs", stepB.GetProperty("file").GetString());
            Assert.Equal("new edit B", stepB.GetProperty("change").GetString());

            // The rejected record survived at the tail with a re-anchored index + reason intact.
            var rejected = items.Value[2];
            Assert.Equal(2, rejected.GetProperty("index").GetInt32());
            Assert.Equal("_web_search", rejected.GetProperty("file").GetString());
            Assert.Equal("old search", rejected.GetProperty("change").GetString());
            Assert.False(rejected.GetProperty("done").GetBoolean());
            Assert.Equal("rejected", rejected.GetProperty("status").GetString());
            Assert.Equal("Classifier: stale", rejected.GetProperty("error").GetString());
        }
        finally { try { Directory.Delete(dir, true); } catch { /* db file stays locked — best effort */ } }
    }

    [Fact]
    public async Task PersistBoardDataPlanAsync_AppendDoesNotDuplicateRejectedRecords()
    {
        var (controller, db, dir) = BuildHarness();
        try
        {
            db.SetBoardData(BoardWithCard("card-3", "doing",
                "[{\"index\":0,\"file\":\"src/existing.cs\",\"change\":\"existing edit\",\"line\":1,\"done\":true}," +
                 "{\"index\":1,\"file\":\"_web_fetch\",\"change\":\"old fetch\",\"line\":0,\"done\":false,\"status\":\"rejected\",\"error\":\"Classifier: stale\"}]"));

            var planSteps = new List<PlanStep> { new() { File = "src/new.cs", Change = "new edit", LineNumber = 2 } };
            await InvokePersistPlan(controller, "card-3", planSteps, append: true);

            var items = ReadPlanItems(db, "card-3");
            Assert.NotNull(items);
            Assert.Equal(3, items!.Value.GetArrayLength());

            // append keeps the pre-existing done item, adds the new committed step...
            Assert.Equal("src/existing.cs", items.Value[0].GetProperty("file").GetString());
            Assert.True(items.Value[0].GetProperty("done").GetBoolean());
            Assert.Equal("src/new.cs", items.Value[1].GetProperty("file").GetString());
            Assert.False(items.Value[1].GetProperty("done").GetBoolean());

            // ...and the rejected record appears EXACTLY once (skipped by the append
            // branch, re-appended once by the preserve loop) with its index re-anchored.
            var rejectedCount = 0;
            JsonElement? rejected = null;
            foreach (var it in items.Value.EnumerateArray())
            {
                if (it.TryGetProperty("status", out var st) && st.GetString() == "rejected")
                {
                    rejectedCount++;
                    rejected = it.Clone();
                }
            }
            Assert.Equal(1, rejectedCount);
            Assert.NotNull(rejected);
            Assert.Equal(2, rejected!.Value.GetProperty("index").GetInt32());
            Assert.Equal("_web_fetch", rejected.Value.GetProperty("file").GetString());
            Assert.Equal("Classifier: stale", rejected.Value.GetProperty("error").GetString());
        }
        finally { try { Directory.Delete(dir, true); } catch { /* db file stays locked — best effort */ } }
    }

    [Fact]
    public void LoadPlanFromBoardDataAsync_PreservesRejectedStatusForReplaySkip()
    {
        // The benchmark-run shape: the card's persisted plan contains a step the interleaved
        // validator REJECTED (the `mkdir` _command, status=rejected, done=false). When the
        // run restarts, the loaded PlanStep must carry Status="rejected" so ExecutePlan skips
        // it instead of executing a step the run already refused (the observed restart re-ran
        // the rejected mkdir on the Desktop).
        var (controller, db, dir) = BuildHarness();
        try
        {
            db.SetBoardData(BoardWithCard("card-replay", "doing",
                "[{\"index\":0,\"file\":\"_web_search\",\"change\":\"q\",\"line\":0,\"done\":true}," +
                 "{\"index\":1,\"file\":\"benchmark_test_16\",\"change\":\"benchmark_test_16\",\"line\":0,\"done\":true}," +
                 "{\"index\":2,\"file\":\"_command\",\"change\":\"mkdir C:\\\\Users\\\\Saint\\\\Desktop\\\\benchmark_test_16\",\"line\":0,\"done\":false,\"status\":\"rejected\",\"error\":\"web-gate veto\"}]"));

            var method = typeof(AgentController).GetMethod("LoadPlanFromBoardDataAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("LoadPlanFromBoardDataAsync not found");
            var result = ((Task<(AgentPlan? plan, HashSet<int>? completed, bool isBenchmark, List<Dictionary<string, object?>>? webResults)>)method
                .Invoke(controller, new object?[] { "card-replay" })!).GetAwaiter().GetResult();
            Assert.Null(result.webResults); // no web steps in this fixture — the 4th slot stays null

            Assert.NotNull(result.plan);
            Assert.Equal(3, result.plan!.Plan.Count);
            var rejected = result.plan.Plan[2];
            Assert.Equal("_command", rejected.File);
            Assert.Equal("mkdir C:\\Users\\Saint\\Desktop\\benchmark_test_16", rejected.Change);
            Assert.Equal("rejected", rejected.Status, ignoreCase: true);
            // The rejected step was never done, so it is NOT in the completed set — the
            // replay skip must come from the Status marker, not from completedIndices.
            Assert.NotNull(result.completed);
            Assert.False(result.completed!.Contains(2));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* db file stays locked — best effort */ } }
    }
}
