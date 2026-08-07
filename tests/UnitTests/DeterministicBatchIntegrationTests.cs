using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// End-to-end integration coverage for a DETERMINISTIC multi-match batch step: the
/// real <c>ResolveAndApplyEdit</c> (private controller method, invoked via reflection
/// exactly like <c>BetweenStepsVerificationTests</c>) running a "update all X defaults
/// to N" change against a real <c>.ini</c> config file on disk. The step must:
///
/// 1. resolve through <c>PrepareEditContextAsync</c>'s deterministic hook
///    (<c>DeterministicEditGenerator.TryGenerate</c> → multi-swap batch) with ZERO LLM
///    calls — proven by a client factory that THROWS on <c>CreateClient</c>; every LLM
///    path in the controller routes through <c>_clientFactory</c>, so any accidental
///    LLM attempt fails the test loudly;
/// 2. apply the whole batch through the real batch-apply path (overlap rejection →
///    sequential <c>TryReplaceSafe</c> with per-edit LineNumber hints → marker-preserving
///    verify bypass → undo save → success), changing EVERY occurrence;
/// 3. leave the enriched "(deterministic batch: N edits, applied N/M occurrences)"
///    marker in the step result so the meeting ticker's compact line renders.
///
/// The change description is written for the deterministic grammar's multi-set form
/// ("update ... defaults to 5"); the file uses UNQUOTED <c>.ini</c> keys because the
/// generator deliberately excludes identifiers preceded by a quote — JSON keys like
/// <c>"maxRetries"</c> are treated as string content and correctly decline.
/// </summary>
public class DeterministicBatchIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly DatabaseService _db;
    private readonly ThrowingClientFactory _clientFactory = new();

    public DeterministicBatchIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "weaver_detbatch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = new DatabaseService(
            Path.Combine(_root, "weaver.db"),
            _root,
            Path.Combine(_root, "weaverconfig.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        _clientFactory.Dispose();
    }

    // ── The end-to-end multi-match batch ────────────────────────────────────

    /// <summary>
    /// A real "update all maxRetries defaults to 5" step against a real .ini file:
    /// ResolveAndApplyEdit resolves it deterministically (no LLM — the client factory
    /// throws on any CreateClient), applies the batch to BOTH occurrences, preserves the
    /// applied-count marker, and reports a done step. Sibling lines stay untouched.
    /// </summary>
    [Fact]
    public async Task DeterministicMultiMatch_EndToEndThroughResolveAndApplyEdit_EveryOccurrenceChanged_NoLlm()
    {
        const string relPath = "config.ini";
        var fullPath = Path.Combine(_root, relPath);
        var ini =
            "[retry]\n" +
            "maxRetries=3\n" +
            "timeoutSec=30\n" +
            "\n" +
            "[connection]\n" +
            "maxRetries=3\n" +
            "timeoutSec=60\n";
        await File.WriteAllTextAsync(fullPath, ini);

        var controller = BuildController();
        var step = new PlanStep
        {
            File = relPath,
            Change = "update all maxRetries defaults to 5",
            LineNumber = 0,
            OldString = null,
            NewString = null,
        };
        var allResults = new List<object>();

        var method = typeof(AgentController).GetMethod(
            "ResolveAndApplyEdit", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ResolveAndApplyEdit not found");
        var task = (Task<int>)method.Invoke(controller, new object?[]
        {
            step, _root, /*emitSse*/ false, CancellationToken.None, allResults,
            /*stepIndex*/ 0, /*prompt*/ null, /*plan*/ null, /*planItemIndex*/ -1,
            /*cardId*/ null, /*attachedFiles*/ null, /*replanDepth*/ 0,
            /*onActivity*/ null, /*skipLlmPreResolution*/ false
        })!;

        var nextIndex = await task;

        // 1. The step completed (stepIndex 0 → next is 1) with zero LLM calls.
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        // 2. Exactly one done result carrying the deterministic-batch marker.
        var result = Assert.Single(allResults);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("done", dict["status"]);
        Assert.Equal(relPath, dict["path"]);
        var preview = Assert.IsType<string>(dict["newStringPreview"]);
        Assert.Contains("(deterministic batch: 2 edits, applied 2/2 occurrences)", preview);

        // 2.5. The board step card data: applied/total counts + per-edit lines.
        Assert.Equal(2, Assert.IsType<int>(dict["batchApplied"]));
        Assert.Equal(2, Assert.IsType<int>(dict["batchTotal"]));
        Assert.Equal("occurrences", Assert.IsType<string>(dict["batchUnit"]));
        var batchEdits = Assert.IsType<List<object>>(dict["batchEdits"]);
        Assert.Equal(2, batchEdits.Count);
        var firstEdit = Assert.IsType<Dictionary<string, object?>>(batchEdits[0]);
        Assert.Equal(2, Assert.IsType<int>(firstEdit["line"]));
        Assert.Equal("maxRetries=3", Assert.IsType<string>(firstEdit["old"]));
        Assert.Equal("maxRetries=5", Assert.IsType<string>(firstEdit["to"]));
        var secondEdit = Assert.IsType<Dictionary<string, object?>>(batchEdits[1]);
        Assert.Equal(6, Assert.IsType<int>(secondEdit["line"]));
        Assert.Equal("maxRetries=3", Assert.IsType<string>(secondEdit["old"]));
        Assert.Equal("maxRetries=5", Assert.IsType<string>(secondEdit["to"]));

        // 3. Every occurrence changed; nothing else did.
        var final = await File.ReadAllTextAsync(fullPath);
        Assert.Equal(0, CountOccurrences(final, "maxRetries=3"));
        Assert.Equal(2, CountOccurrences(final, "maxRetries=5"));
        Assert.Contains("timeoutSec=30", final);
        Assert.Contains("timeoutSec=60", final);
        Assert.Contains("[retry]", final);
        Assert.Contains("[connection]", final);
    }

    /// <summary>
    /// Regression: when a batch does NOT fully apply (one sub-edit's anchor drifted — the exact
    /// scenario G1's comment anticipates: "parallel agent threads, an external save, a formatter"),
    /// the batch marker must NEVER be written into the file as if it were code. Before the
    /// marker-as-code guard, the failed batch fell through to the single-edit apply with
    /// oldStr=edits[0].OldString / newStr=the MARKER — TryReplaceSafe would write the marker
    /// verbatim over the first anchor, and the marker prefix would then PASS the verify bypass,
    /// completing the step "successfully" with the marker embedded in the file.
    /// </summary>
    [Fact]
    public async Task DeterministicBatch_DriftedAnchor_NeverWritesMarkerIntoFile()
    {
        const string relPath = "config.ini";
        var fullPath = Path.Combine(_root, relPath);
        var ini =
            "[retry]\n" +
            "maxRetries=3\n" +
            "timeoutSec=30\n" +
            "\n" +
            "[connection]\n" +
            "maxRetries=3\n" +
            "timeoutSec=60\n";
        await File.WriteAllTextAsync(fullPath, ini);

        var controller = BuildController();
        // Hand-supplied batch with a STALE second anchor: the file drifted since generation,
        // so sub-edit #2 can never match. skipLlmPreResolution=true keeps PrepareEditContextAsync's
        // deterministic hook from re-running (it would regenerate a fresh, valid batch).
        var step = new PlanStep
        {
            File = relPath,
            Change = "update all maxRetries defaults to 5",
            LineNumber = 0,
            OldString = "maxRetries=3",
            NewString = "(deterministic batch: 2 edits, applied 2/2 occurrences)",
            Edits = new List<EditPair>
            {
                new() { OldString = "maxRetries=3", NewString = "maxRetries=5", LineNumber = 2 },
                new() { OldString = "maxRetries=999", NewString = "maxRetries=5", LineNumber = 6 } // drifted
            }
        };
        var allResults = new List<object>();

        var method = typeof(AgentController).GetMethod(
            "ResolveAndApplyEdit", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ResolveAndApplyEdit not found");
        var task = (Task<int>)method.Invoke(controller, new object?[]
        {
            step, _root, /*emitSse*/ false, CancellationToken.None, allResults,
            /*stepIndex*/ 0, /*prompt*/ null, /*plan*/ null, /*planItemIndex*/ -1,
            /*cardId*/ null, /*attachedFiles*/ null, /*replanDepth*/ 0,
            /*onActivity*/ null, /*skipLlmPreResolution*/ true
        })!;

        try
        {
            await task; // LLM escalation paths throw by design — fine, we assert file integrity below.
        }
        catch { /* expected: the failed batch escalates toward the LLM, which the ThrowingClientFactory blocks */ }

        // The marker was NEVER applied as code: the file is byte-identical to the original.
        var final = await File.ReadAllTextAsync(fullPath);
        Assert.Equal(ini, final);
        Assert.DoesNotContain("(deterministic batch:", final);
        Assert.DoesNotContain("(batch:", final);
        Assert.Equal(2, CountOccurrences(final, "maxRetries=3")); // both unchanged — no partial single-edit either
    }

    /// <summary>
    /// The ScanMissingTypes safety net must still fire for a FillClassBody multi-class batch:
    /// a batch adding a member whose type is NOT declared in the file (e.g. <c>List&lt;EmailSettings&gt;</c>)
    /// should get a <c>public class EmailSettings {{ }} </c> stub appended, exactly like a
    /// single-edit add. Before the fix, ScanMissingTypes received the "(deterministic batch:"
    /// MARKER as its newCode and silently no-op'd (all-lowercase words), so the batch path lost
    /// missing-type stub generation. This drives the REAL generator path (the deterministic
    /// hook in PrepareEditContextAsync → multi-class FillClassBody batch → batch apply →
    /// ScanMissingTypes on the actual member snippets) and asserts the stub lands in the file.
    /// </summary>
    [Fact]
    public async Task DeterministicBatch_CsMemberAdd_MissingTypeStubStillAppended()
    {
        const string relPath = "Dtos.cs";
        var fullPath = Path.Combine(_root, relPath);
        var cs =
            "public class UserDto\n" +
            "{\n" +
            "    public int Id { get; set; }\n" +
            "}\n" +
            "public class OrderDto\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "}\n";
        await File.WriteAllTextAsync(fullPath, cs);

        var controller = BuildController();
        var step = new PlanStep
        {
            File = relPath,
            Change = "add a List<EmailSettings> Orders property to every DTO class",
            LineNumber = 0,
            OldString = null,
            NewString = null,
        };
        var allResults = new List<object>();

        var method = typeof(AgentController).GetMethod(
            "ResolveAndApplyEdit", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ResolveAndApplyEdit not found");
        var task = (Task<int>)method.Invoke(controller, new object?[]
        {
            step, _root, /*emitSse*/ false, CancellationToken.None, allResults,
            /*stepIndex*/ 0, /*prompt*/ null, /*plan*/ null, /*planItemIndex*/ -1,
            /*cardId*/ null, /*attachedFiles*/ null, /*replanDepth*/ 0,
            /*onActivity*/ null, /*skipLlmPreResolution*/ false
        })!;

        var nextIndex = await task;
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        var final = await File.ReadAllTextAsync(fullPath);
        var member = "public List<EmailSettings> Orders { get; set; }";
        Assert.Equal(2, CountOccurrences(final, member));                 // both DTO classes got the member
        Assert.Contains("public class EmailSettings", final);            // missing-type stub appended
        Assert.DoesNotContain("(deterministic batch:", final);           // marker never written as code

        var result = Assert.Single(allResults, r => r is Dictionary<string, object?> d &&
            d.TryGetValue("status", out var st) && "done".Equals(st));
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("done", dict["status"]);
        Assert.Equal(2, Assert.IsType<int>(dict["batchApplied"]));
        Assert.Equal(2, Assert.IsType<int>(dict["batchTotal"]));
        Assert.Equal("classes", Assert.IsType<string>(dict["batchUnit"]));
    }

    /// <summary>
    /// Per-class override clause end-to-end: "... but NameKey on the first one" drives the REAL
    /// generator path (deterministic hook → multi-class FillClassBody batch) and must leave the
    /// FIRST DTO class with NameKey and the second with Email — both applied, marker never in file,
    /// zero LLM calls.
    /// </summary>
    [Fact]
    public async Task DeterministicBatch_CsMemberAdd_PerClassOverrideAppliesEndToEnd()
    {
        const string relPath = "Dtos.cs";
        var fullPath = Path.Combine(_root, relPath);
        var cs =
            "public class UserDto\n" +
            "{\n" +
            "    public int Id { get; set; }\n" +
            "}\n" +
            "public class OrderDto\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "}\n";
        await File.WriteAllTextAsync(fullPath, cs);

        var controller = BuildController();
        var step = new PlanStep
        {
            File = relPath,
            Change = "add a string Email property to every DTO class, but NameKey on the first one",
            LineNumber = 0,
            OldString = null,
            NewString = null,
        };
        var allResults = new List<object>();

        var method = typeof(AgentController).GetMethod(
            "ResolveAndApplyEdit", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ResolveAndApplyEdit not found");
        var task = (Task<int>)method.Invoke(controller, new object?[]
        {
            step, _root, /*emitSse*/ false, CancellationToken.None, allResults,
            /*stepIndex*/ 0, /*prompt*/ null, /*plan*/ null, /*planItemIndex*/ -1,
            /*cardId*/ null, /*attachedFiles*/ null, /*replanDepth*/ 0,
            /*onActivity*/ null, /*skipLlmPreResolution*/ false
        })!;

        var nextIndex = await task;
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        var final = await File.ReadAllTextAsync(fullPath);
        Assert.Contains("public string NameKey { get; set; }", final); // first DTO class (UserDto)
        Assert.Contains("public string Email { get; set; }", final);    // second DTO class (OrderDto)
        Assert.True(final.IndexOf("public string NameKey", StringComparison.Ordinal)
                    < final.IndexOf("public string Email", StringComparison.Ordinal));
        Assert.DoesNotContain("(deterministic batch:", final);
        Assert.DoesNotContain("(batch:", final);

        var result = Assert.Single(allResults, r => r is Dictionary<string, object?> d &&
            d.TryGetValue("status", out var st) && "done".Equals(st));
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(2, Assert.IsType<int>(dict["batchApplied"]));
        Assert.Equal(2, Assert.IsType<int>(dict["batchTotal"]));
        Assert.Equal("classes", Assert.IsType<string>(dict["batchUnit"]));
    }

    /// <summary>
    /// Exclusion filter end-to-end: "every DTO class except the base one" drives the REAL
    /// generator path and must leave BaseDto untouched while UserDto + OrderDto get the
    /// member — marker never in file, zero LLM calls.
    /// </summary>
    [Fact]
    public async Task DeterministicBatch_CsMemberAdd_ExclusionFilterAppliesEndToEnd()
    {
        const string relPath = "Dtos.cs";
        var fullPath = Path.Combine(_root, relPath);
        var cs =
            "public class BaseDto\n" +
            "{\n" +
            "    public int Id { get; set; }\n" +
            "}\n" +
            "public class UserDto\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "}\n" +
            "public class OrderDto\n" +
            "{\n" +
            "    public decimal Total { get; set; }\n" +
            "}\n";
        await File.WriteAllTextAsync(fullPath, cs);

        var controller = BuildController();
        var step = new PlanStep
        {
            File = relPath,
            Change = "add a string Email property to every DTO class except the base one",
            LineNumber = 0,
            OldString = null,
            NewString = null,
        };
        var allResults = new List<object>();

        var method = typeof(AgentController).GetMethod(
            "ResolveAndApplyEdit", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ResolveAndApplyEdit not found");
        var task = (Task<int>)method.Invoke(controller, new object?[]
        {
            step, _root, /*emitSse*/ false, CancellationToken.None, allResults,
            /*stepIndex*/ 0, /*prompt*/ null, /*plan*/ null, /*planItemIndex*/ -1,
            /*cardId*/ null, /*attachedFiles*/ null, /*replanDepth*/ 0,
            /*onActivity*/ null, /*skipLlmPreResolution*/ false
        })!;

        var nextIndex = await task;
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        var final = await File.ReadAllTextAsync(fullPath);
        var member = "public string Email { get; set; }";
        Assert.Equal(2, CountOccurrences(final, member)); // UserDto + OrderDto
        // BaseDto's block is untouched.
        var baseBlock = final.Substring(final.IndexOf("public class BaseDto", StringComparison.Ordinal),
            final.IndexOf("public class UserDto", StringComparison.Ordinal) - final.IndexOf("public class BaseDto", StringComparison.Ordinal));
        Assert.DoesNotContain("Email", baseBlock);
        Assert.DoesNotContain("(deterministic batch:", final);
        Assert.DoesNotContain("(batch:", final);

        var result = Assert.Single(allResults, r => r is Dictionary<string, object?> d &&
            d.TryGetValue("status", out var st) && "done".Equals(st));
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(2, Assert.IsType<int>(dict["batchApplied"]));
        Assert.Equal(2, Assert.IsType<int>(dict["batchTotal"]));
        Assert.Equal("classes", Assert.IsType<string>(dict["batchUnit"]));
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private AgentController BuildController()
    {
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        SetField(controller, "_clientFactory", _clientFactory);
        SetField(controller, "_configFile", new ConfigFileService(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        return controller;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {name} not found");
        field.SetValue(target, value);
    }

    private static int CountOccurrences(string content, string block)
    {
        var count = 0;
        var pos = 0;
        while ((pos = content.IndexOf(block, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += block.Length;
        }
        return count;
    }

    /// <summary>
    /// An <see cref="IHttpClientFactory"/> that records and THROWS on every
    /// <c>CreateClient</c> call — the controller's only LLM paths (CallLlmRaw,
    /// CallLlmRawStreaming, CallLlmRawText) all go through this factory, so a nonzero
    /// call count means the deterministic step secretly touched the LLM.
    /// </summary>
    private sealed class ThrowingClientFactory : IHttpClientFactory, IDisposable
    {
        public int CreateClientCalls;

        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref CreateClientCalls);
            throw new InvalidOperationException(
                $"LLM client requested ('{name}') during a deterministic edit — the step must not call the LLM");
        }

        public HttpClient CreateClient() => CreateClient("default");

        public void Dispose() { }
    }
}
