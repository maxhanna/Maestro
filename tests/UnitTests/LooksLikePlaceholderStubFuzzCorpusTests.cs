using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Seeded fuzz corpus for <c>AgentEditHeuristics.LooksLikePlaceholderStub</c> — the
/// deterministic guard that rejects LLM-authored placeholder stubs in replacement code.
/// Every doc is a (code, preExisting) pair generated from the seeded RNG; the corpus
/// guards the detector's two halves with opposite strictness:
///   • PRECISION — real code must NEVER be flagged (zero false positives): carried-over
///     lines (empty ctors, real one-liners, even carried-over STUB SHAPES — an empty
///     helper, a NotImplemented throw, a TODO comment — which are pre-existing, not
///     LLM-authored), and real one-liner / multi-line method bodies.
///   • RECALL — genuine stubs must ALL be caught: empty methods, empty bare AND named
///     arrows, NotImplemented/NotSupported throws, single-line and multi-line
///     console.log-only bodies, and placeholder comments — even when a carried-over line
///     sits beside them and must not mask them.
/// Following the FuzzHarness discipline: a fixed (seed, prime) drives a per-doc RNG and a
/// deterministic variant rotation, and AssertAllDocsChecked / AssertExercised fail loudly
/// if a doc or a detector branch is silently skipped.
/// </summary>
public class LooksLikePlaceholderStubFuzzCorpusTests
{
    // Unique (seed, prime) for this corpus — no other corpus shares this doc stream.
    private const int Seed = 0x5B0B;
    private const int Prime = 131;
    private const int DocCount = 65;   // 13 variants × 5 docs
    private const int VariantCount = 13;

    private static readonly string[] MethodNames =
    {
        "getItems", "loadData", "fetchUser", "computeTotal", "renderList",
        "parsePayload", "openPanel", "refreshView", "submitForm", "validateInput"
    };

    private static readonly string[] FieldNames = { "items", "data", "result", "payload", "list", "config" };
    private static readonly string[] LogMessages = { "stub", "todo", "not-done", "placeholder", "wip" };

    [Fact]
    public void Corpus_PrecisionAndRecall_CarriedOverRealCodeAndGenuineStubs()
    {
        var branchHits = new BranchHitCounter<string>(
            new[] { "clean", "comment", "notimpl", "empty", "console1", "consoleBody" },
            "LooksLikePlaceholderStub corpus");
        var docsChecked = 0;
        var precisionCases = 0; // real / carried-over docs — must all be false
        var recallCases = 0;    // genuine stubs — must all be true

        for (var docIdx = 0; docIdx < DocCount; docIdx++)
        {
            var rng = FuzzHarness.SeededRng(Seed, docIdx, Prime);
            var variant = docIdx % VariantCount;
            var (code, preExisting, expected, branch) = BuildDoc(rng, docIdx, variant);

            var actual = AgentEditHeuristics.LooksLikePlaceholderStub(code, preExisting);
            branchHits.Hit(branch);
            if (expected)
            {
                recallCases++;
                Assert.True(actual,
                    $"doc #{docIdx} (variant {variant}) — genuine stub must be caught:\n{code}\n--- preExisting ---\n{preExisting}");
            }
            else
            {
                precisionCases++;
                Assert.False(actual,
                    $"doc #{docIdx} (variant {variant}) — real code must NOT be flagged:\n{code}\n--- preExisting ---\n{preExisting}");
            }
            docsChecked++;
        }

        FuzzHarness.AssertAllDocsChecked(docsChecked, DocCount, "LooksLikePlaceholderStub corpus");
        FuzzHarness.AssertExercised(precisionCases,
            "the corpus must exercise the real-code / carried-over precision buckets");
        FuzzHarness.AssertExercised(recallCases,
            "the corpus must exercise genuine stubs");
        foreach (var branch in new[] { "comment", "notimpl", "empty", "console1", "consoleBody", "clean" })
            FuzzHarness.AssertExercised(branchHits.Count(branch),
                $"detector branch '{branch}' must actually fire in the corpus");
    }

    [Fact]
    public void Corpus_IsSeedableDeterministic()
    {
        // Same (seed, docIdx, prime) must reproduce the same doc forever — a seed/prime typo
        // would silently weaken the corpus.
        for (var docIdx = 0; docIdx < VariantCount; docIdx++)
        {
            var a = BuildDoc(FuzzHarness.SeededRng(Seed, docIdx, Prime), docIdx, docIdx % VariantCount);
            var b = BuildDoc(FuzzHarness.SeededRng(Seed, docIdx, Prime), docIdx, docIdx % VariantCount);
            Assert.True(a.code == b.code && a.preExisting == b.preExisting && a.expected == b.expected,
                $"doc #{docIdx} is not seedable — rebuilt docs differ");
        }
    }

    /// <summary>One corpus doc: the replacement code, the oldString it replaces (carried-over
    /// context), the KNOWN verdict, and the detector branch it must exercise.</summary>
    private static (string code, string? preExisting, bool expected, string branch) BuildDoc(
        Random rng, int docIdx, int variant)
    {
        var name = MethodNames[rng.Next(MethodNames.Length)];
        var field = FieldNames[rng.Next(FieldNames.Length)];
        var log = LogMessages[rng.Next(LogMessages.Length)];
        const string ctor = "  constructor() { }";
        var emptyHelper = "  helper() { }";
        var notImpl = $"  {name}() {{ throw new NotImplementedException(); }}";

        return variant switch
        {
            // ── PRECISION: real code — must never fire ─────────────────────────────
            0 => ($"  {name}() {{ return this.{field}.slice(); }}", null, false, "clean"),
            1 => ($"{ctor}\n  {name}() {{ return this.{field}?.length ?? 0; }}", ctor, false, "clean"),
            2 => ($"{ctor}\n  {field} = [];", ctor + "\n  " + field + " = [];", false, "clean"),
            8 => ($"{emptyHelper}\n  {name}() {{ return this.{field}; }}", emptyHelper, false, "clean"),
            10 => ($"  {name}(): void {{\n    const total = this.{field}.length;\n    if (total > 0) {{ this.flag = true; }}\n    return;\n  }}", null, false, "clean"),
            // Carried-over STUB SHAPES are still pre-existing code — a NotImplemented throw or
            // a TODO comment carried unchanged must not doom the new real method beside it.
            11 => ($"{notImpl}\n  {name}Sync() {{ return this.{field}; }}", notImpl, false, "clean"),
            12 => ($"  // TODO: implement\n  {name}() {{ return this.{field}; }}", "  // TODO: implement", false, "clean"),
            // ── RECALL: genuine stubs — must all fire ──────────────────────────────
            3 => ($"{ctor}\n  {name}() {{ }}", ctor, true, "empty"),
            4 => (notImpl, null, true, "notimpl"),
            5 => ($"  {name}() {{ console.log('{log}'); }}", null, true, "console1"),
            6 => ($"  {name}() {{ // TODO: implement\n  }}", null, true, "comment"),
            7 => ($"  const {name} = () => {{ }};", null, true, "empty"),
            9 => ($"  {name}(): void {{\n    console.log('{log}');\n    console.error('{log}');\n  }}", null, true, "consoleBody"),
            _ => throw new InvalidOperationException("variant out of range")
        };
    }
}
