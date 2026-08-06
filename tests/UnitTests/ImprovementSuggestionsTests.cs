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

    private static Task InvokePersistContext(AgentController controller, string cardId, object context)
    {
        var method = typeof(AgentController).GetMethod("PersistCardSuggestionsContextAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PersistCardSuggestionsContextAsync not found");
        return (Task)method.Invoke(controller, new object?[] { cardId, context })!;
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

    // ── "More like this" top-up dedupe ────────────────────────────────────
    // The top-up endpoint feeds the existing suggestions back to the LLM and
    // then filters the NEW ones through IsDuplicateSuggestion so the set is
    // extended rather than repeated. These helpers are pure/static, so they're
    // unit-tested directly (the endpoint's LLM half can't be).

    private static string InvokeNormalize(string s)
    {
        var method = typeof(AgentController).GetMethod("NormalizeSuggestionText",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("NormalizeSuggestionText not found");
        return (string)method.Invoke(null, new object?[] { s })!;
    }

    private static bool InvokeIsDuplicate(string desc, IEnumerable<string> known)
    {
        var method = typeof(AgentController).GetMethod("IsDuplicateSuggestion",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsDuplicateSuggestion not found");
        return (bool)method.Invoke(null, new object?[] { desc, known })!;
    }

    [Fact]
    public async Task Persist_Context_RoundTrips_Alongside_Suggestions()
    {
        var (controller, db, dbPath) = BuildHarness();
        try
        {
            SeedBoard(db, "card-ctx", "done");
            var suggestions = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "abc12345",
                    ["description"] = "Add error handling",
                    ["files"] = new List<string> { "src/app/globe.ts" },
                    ["createdAt"] = "2026-08-06T00:00:00Z"
                }
            };
            await InvokePersist(controller, "card-ctx", suggestions);
            await InvokePersistContext(controller, "card-ctx", new Dictionary<string, object?>
            {
                ["summary"] = "Refactored the upload flow",
                ["thinking"] = "The upload flow retried too aggressively, so I capped retries at 3.",
                // NOTE: keep these ASCII — System.Text.Json escapes non-ASCII
                // (e.g. "✓") as \uXXXX in board data, so a literal assert on the
                // raw string would fail even though the data round-trips fine.
                ["steps"] = new List<string> { "src/upload.ts - cap retries" },
                ["planItems"] = new List<string> { "Refactor upload retries" },
                ["filesEdited"] = new List<string> { "src/upload.ts" },
                ["generatedAt"] = "2026-08-06T00:00:00Z"
            });

            var raw = db.GetBoardData();
            Assert.NotNull(raw);
            var boardJson = raw!;
            Assert.Contains("_suggestions", boardJson);
            Assert.Contains("_suggestionsContext", boardJson);
            Assert.Contains("Refactored the upload flow", boardJson);
            Assert.Contains("capped retries at 3", boardJson);
            Assert.Contains("Refactor upload retries", boardJson);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    [Fact]
    public void Normalize_Suggestion_Text_Is_Case_And_Punctuation_Insensitive()
    {
        Assert.Equal("add error handling", InvokeNormalize("Add  error handling!"));
        Assert.Equal("extract a service", InvokeNormalize("Extract a service."));
    }

    [Fact]
    public void TopUp_Dedupe_Drops_Exact_And_Contained_Repeats()
    {
        // Exact normalized match → duplicate.
        Assert.True(InvokeIsDuplicate("Add error handling.", new[] { "add error handling" }));
        // A more specific suggestion that embeds the existing phrase → duplicate.
        Assert.True(InvokeIsDuplicate("Add error handling for the upload flow", new[] { "add error handling" }));
    }

    [Fact]
    public void TopUp_Dedupe_Allows_Genuinely_Distinct_Suggestions()
    {
        // Different topic entirely → not a duplicate.
        Assert.False(InvokeIsDuplicate("Add retry logic to the upload service", new[] { "add error handling" }));
        // Same general area but a distinct ask → not a duplicate.
        Assert.False(InvokeIsDuplicate("Write tests for the upload flow", new[] { "add error handling" }));
    }

    [Fact]
    public void TopUp_Dedupe_Drops_Repeats_Within_The_Same_Batch()
    {
        var known = new[] { "add error handling" };
        // Simulate the parse loop: the first new suggestion is distinct from the
        // known set and gets kept; the second is a containment rephrase of the
        // first and must be dropped.
        var batch = new List<string>();
        var first = "Add retry logic to the upload service";
        if (!InvokeIsDuplicate(first, known.Concat(batch))) batch.Add(first);
        var second = "Add retry logic to the upload service with exponential backoff";
        if (!InvokeIsDuplicate(second, known.Concat(batch))) batch.Add(second);
        Assert.Single(batch);
        Assert.Equal("Add retry logic to the upload service", batch[0]);
    }
}
