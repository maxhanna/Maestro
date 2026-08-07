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
///    marker in the step result so the meeting ticker's compact line renders;
/// 4. recover from drift between generation and apply: a formatter/parallel edit that breaks
///    the anchors trips the marker-as-code guard, then G1 re-synthesizes against the CURRENT
///    file content and re-applies the batch — still zero LLM calls (or, when the change is no
///    longer describable, escalates to the LLM with the marker never written as code).
///
/// The change description is written for the deterministic grammar's multi-set form
/// ("update ... defaults to 5"). Unquoted <c>.ini</c> keys are the base case; QUOTED JSON
/// keys (<c>"maxRetries": 3</c>) are recognized via the quoted-key form of
/// <c>ContainsStandaloneName</c>, gated to JSON-family files (.json/.jsonc/.json5), so the
/// same batch machinery drives appsettings.json — as long as the line is a genuine key:value
/// pair with a scalar value (an array element or object-valued key is still treated as string
/// content and declines).
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
    /// Partial-batch transparency end-to-end: one occurrence is ALREADY the target value
    /// (maxRetries=5 sits among maxRetries=3 lines), so the multi-swap generator emits a
    /// single edit for the stale line and the batch applies 1/2. The done result must report
    /// "applied 1/2 occurrences" (marker + batchApplied/batchTotal/batchUnit), the per-edit
    /// list must contain ONLY the stale line, and the already-correct occurrence plus all
    /// siblings must stay byte-identical — zero LLM calls throughout.
    /// </summary>
    [Fact]
    public async Task DeterministicMultiMatch_OneOccurrenceAlreadyCorrect_PartialBatchApplied_NoLlm()
    {
        const string relPath = "config.ini";
        var fullPath = Path.Combine(_root, relPath);
        var ini =
            "[retry]\n" +
            "maxRetries=3\n" +
            "timeoutSec=30\n" +
            "\n" +
            "[connection]\n" +
            "maxRetries=5\n" + // already the target — skipped as already-correct
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
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        // The result reports the PARTIAL batch: applied 1/2, not 2/2.
        var result = Assert.Single(allResults);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("done", dict["status"]);
        var preview = Assert.IsType<string>(dict["newStringPreview"]);
        Assert.Contains("(deterministic batch: 1 edits, applied 1/2 occurrences)", preview);
        Assert.Equal(1, Assert.IsType<int>(dict["batchApplied"]));
        Assert.Equal(2, Assert.IsType<int>(dict["batchTotal"]));
        Assert.Equal("occurrences", Assert.IsType<string>(dict["batchUnit"]));

        // Per-edit list contains ONLY the stale line — the already-correct one never shipped.
        var batchEdits = Assert.IsType<List<object>>(dict["batchEdits"]);
        var singleEdit = Assert.IsType<Dictionary<string, object?>>(Assert.Single(batchEdits));
        Assert.Equal(2, Assert.IsType<int>(singleEdit["line"])); // the maxRetries=3 line
        Assert.Equal("maxRetries=3", Assert.IsType<string>(singleEdit["old"]));
        Assert.Equal("maxRetries=5", Assert.IsType<string>(singleEdit["to"]));

        // Only the stale line changed: both lines now read 5, siblings and headers untouched.
        var final = await File.ReadAllTextAsync(fullPath);
        Assert.Equal(2, CountOccurrences(final, "maxRetries=5"));
        Assert.Equal(0, CountOccurrences(final, "maxRetries=3"));
        Assert.Contains("timeoutSec=30", final);
        Assert.Contains("timeoutSec=60", final);
        Assert.Contains("[retry]", final);
        // The already-correct [connection] block is byte-identical — only line 2 was touched.
        Assert.Contains("[connection]\nmaxRetries=5\ntimeoutSec=60", final);
    }

    /// <summary>
    /// The quoted-key form end-to-end: "update all maxRetries defaults to 5" on an
    /// appsettings.json whose keys are QUOTED ("maxRetries": 3). Previously quoted identifiers
    /// were treated as string content and the multi-swap declined; now the key:value pair is
    /// recognized, the batch applies with the quotes and structure preserved, and the result
    /// reports applied 1/1 — zero LLM calls.
    /// </summary>
    [Fact]
    public async Task DeterministicMultiMatch_QuotedJsonKey_AppliesThroughResolveAndApplyEdit_NoLlm()
    {
        const string relPath = "appsettings.json";
        var fullPath = Path.Combine(_root, relPath);
        var json =
            "{\n" +
            "  \"maxRetries\": 3,\n" +
            "  \"timeoutSec\": 30\n" +
            "}\n";
        await File.WriteAllTextAsync(fullPath, json);

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
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        var result = Assert.Single(allResults);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("done", dict["status"]);
        var preview = Assert.IsType<string>(dict["newStringPreview"]);
        Assert.Contains("(deterministic batch: 1 edits, applied 1/1 occurrences)", preview);
        Assert.Equal(1, Assert.IsType<int>(dict["batchApplied"]));
        Assert.Equal(1, Assert.IsType<int>(dict["batchTotal"]));
        Assert.Equal("occurrences", Assert.IsType<string>(dict["batchUnit"]));

        // Quotes and structure preserved; only the value changed.
        var final = await File.ReadAllTextAsync(fullPath);
        Assert.Contains("\"maxRetries\": 5,", final);
        Assert.DoesNotContain("\"maxRetries\": 3", final);
        Assert.Contains("\"timeoutSec\": 30", final);
        Assert.Contains("{", final);
        Assert.Contains("}", final);
    }

    /// <summary>
    /// Drifted-batch regression, in two acts: (1) when a batch does NOT fully apply (one
    /// sub-edit's anchor drifted — the exact scenario G1's comment anticipates: "parallel
    /// agent threads, an external save, a formatter"), the batch marker must NEVER be written
    /// into the file as if it were code — the marker-as-code guard fails fast instead.
    /// (2) Because the step carries the deterministic-batch marker, attempt 1's G1
    /// re-synthesis re-runs the generator against the CURRENT file content, re-anchors both
    /// occurrences, and applies them — zero LLM calls. Before the isDeterministicEdit gate
    /// recognized marker-carrying steps, this scenario escalated to the LLM resolver instead
    /// of recovering for free.
    /// </summary>
    [Fact]
    public async Task DeterministicBatch_DriftedAnchor_G1ReanchorsAndApplies_MarkerNeverWritten()
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

        // G1 re-anchored the stale batch against the current file and applied it — no LLM,
        // no exception, step completed. The stale hand-supplied batch (whose second edit
        // targets maxRetries=999) never matched, so success here is PROOF of the G1 path.
        var nextIndex = await task;
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        // The marker was NEVER applied as code — G1 produced a fresh, real batch instead.
        var final = await File.ReadAllTextAsync(fullPath);
        Assert.Equal(2, CountOccurrences(final, "maxRetries=5"));
        Assert.Equal(0, CountOccurrences(final, "maxRetries=3"));
        Assert.DoesNotContain("(deterministic batch:", final);
        Assert.DoesNotContain("(batch:", final);

        // The done result still carries the fresh marker + batch counts for the ticker/board.
        var result = Assert.Single(allResults, r => r is Dictionary<string, object?> d &&
            d.TryGetValue("status", out var st) && "done".Equals(st));
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(2, Assert.IsType<int>(dict["batchApplied"]));
        Assert.Equal(2, Assert.IsType<int>(dict["batchTotal"]));
        Assert.Equal("occurrences", Assert.IsType<string>(dict["batchUnit"]));
    }

    /// <summary>
    /// The full G1 drift story, using the user's example: the batch is generated against the
    /// original file, then an EXTERNAL FORMATTER REINDENTS the file (4-space → 2-space) between
    /// generation and apply. The stale batch's anchors no longer match on attempt 0 (marker
    /// guard fires, marker never written as code); attempt 1's G1 re-synthesis re-runs the REAL
    /// generator against the drifted content, re-anchors every class body, and applies the whole
    /// batch — still zero LLM calls, exactly like an unharmed run.
    /// </summary>
    [Fact]
    public async Task DeterministicBatch_ExternalFormatterReindent_G1ReanchorsAndApplies_NoLlm()
    {
        const string relPath = "Dtos.cs";
        var fullPath = Path.Combine(_root, relPath);
        var original =
            "public class UserDto\n" +
            "{\n" +
            "    public int Id { get; set; }\n" +
            "}\n" +
            "public class OrderDto\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "}\n";
        // The external formatter reindents to 2 spaces — every anchor the generator built
        // against `original` (4-space member line + close brace) is now stale.
        var drifted = original.Replace("    public", "  public", StringComparison.Ordinal);
        Assert.NotEqual(original, drifted);

        // Generation happens FIRST (against the original content) — the stale batch is
        // hand-supplied exactly as PrepareEditContextAsync would have produced it.
        var generated = DeterministicEditGenerator.TryGenerate(
            relPath, true, original, "add a string Email property to every DTO class");
        Assert.NotNull(generated);
        Assert.NotNull(generated!.Edits);
        Assert.Equal(2, generated.Edits.Count);

        await File.WriteAllTextAsync(fullPath, drifted);

        var controller = BuildController();
        var step = new PlanStep
        {
            File = relPath,
            Change = "add a string Email property to every DTO class",
            LineNumber = generated.LineNumber,
            OldString = generated.OldStr,
            NewString = generated.NewStr, // the deterministic-batch marker
            Edits = generated.Edits,
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

        // The stale 4-space batch cannot match the 2-space file on attempt 0 — success below
        // is only possible through G1's re-anchoring, and the ThrowingClientFactory proves it
        // happened with zero LLM calls.
        var nextIndex = await task;
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        var final = await File.ReadAllTextAsync(fullPath);
        // Both DTO classes got the member, re-anchored with the DRIFTED (2-space) indentation.
        Assert.Equal(2, CountOccurrences(final, "  public string Email { get; set; }"));
        Assert.Equal(0, CountOccurrences(final, "    public string Email")); // no 4-space leak
        Assert.Contains("public class UserDto", final);
        Assert.Contains("public class OrderDto", final);
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
    /// The marker guard's decline path: when G1's re-synthesis finds the change no longer
    /// deterministically describable against the current content (the retry section was
    /// removed by an external edit), the stale batch is nulled and the step escalates to the
    /// LLM resolver — which the ThrowingClientFactory blocks. The marker must NEVER have been
    /// written into the file, even on the escalation path.
    /// </summary>
    [Fact]
    public async Task DeterministicBatch_DriftedAnchor_G1Declines_MarkerNeverWritten()
    {
        const string relPath = "config.ini";
        var fullPath = Path.Combine(_root, relPath);
        var drifted =
            "[connection]\n" +
            "timeoutSec=60\n"; // the retry section is gone — "maxRetries" no longer exists
        await File.WriteAllTextAsync(fullPath, drifted);

        var controller = BuildController();
        var step = new PlanStep
        {
            File = relPath,
            Change = "update all maxRetries defaults to 5",
            LineNumber = 0,
            OldString = "maxRetries=3",
            NewString = "(deterministic batch: 1 edits, applied 1/1 occurrences)",
            Edits = new List<EditPair>
            {
                new() { OldString = "maxRetries=3", NewString = "maxRetries=5", LineNumber = 2 }
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

        // G1 declines → LLM escalation → the ThrowingClientFactory faults the task by design.
        await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)task);

        // The marker was NEVER applied as code: the file is byte-identical to the drifted input.
        var final = await File.ReadAllTextAsync(fullPath);
        Assert.Equal(drifted, final);
        Assert.DoesNotContain("(deterministic batch:", final);
        Assert.DoesNotContain("(batch:", final);
        // It truly escalated to the LLM (G1 declined) rather than recovering silently — the
        // factory throws on its FIRST CreateClient, so exactly one call proves the attempt.
        Assert.Equal(1, _clientFactory.CreateClientCalls);
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
