using Weaver;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Corpus for multi-edit steps (<c>PlanStep.Edits</c> — small repetitive changes across
/// repeated column/section patterns). Runs the COMPLETE deterministic batch-apply path
/// the agent executes at AgentController ~4242-4295, mirrored exactly by
/// <c>FuzzHarness.RunBatchApplyMirror</c>: (1) overlap rejection — any pair of
/// trimmed-normalized oldStrings where one CONTAINS the other rejects the whole batch;
/// (2) sequential application via <c>TryReplaceSafe</c> with per-edit LineNumber,
/// threading the evolving content; (3) NO partial application — the first failing
/// sub-edit aborts the batch and the ORIGINAL content is returned untouched; (4) a
/// no-op batch (result == input) is reported as not replaced.
///
/// The claim locked here: every edit lands independently with no cross-edit
/// interference — the final content equals the pure sequential substitution, the
/// byte-delta is exactly the sum of the per-edit deltas, sibling sections (comments,
/// untouched blocks) stay byte-identical, and any bad/overlapping/no-op edit rejects
/// the ENTIRE batch rather than half-applying.
/// </summary>
public class BatchEditCorpusTests
{
    private static readonly string[] Extensions = { ".ts", ".js", ".cs", ".txt", ".css" };

    // ── Generators ───────────────────────────────────────────────────────────

    private static string Comment(int i, int k, string ext) => ext switch
    {
        ".css" => $"/* section-{i}-{k} */",
        ".txt" => $"# section-{i}-{k}",
        _      => $"// section-{i}-{k}",
    };

    private static string SectionBody(string ext, string token, int n) => ext switch
    {
        ".cs"  => $"    public void {token}()\n    {{\n        var tmp = {n};\n    }}",
        ".ts"  => $"  {token}(): void {{\n    this.count = {n};\n  }}",
        ".css" => $".{token} {{ margin: {n}px; color: #12ab34; }}",
        ".txt" => $"section {token}:\n  value = {n}",
        _      => $"  {token}() {{\n    const tmp = {n};\n  }}",
    };

    // ── Deterministic tests ──────────────────────────────────────────────────

    [Fact]
    public void BatchApply_MultipleEdits_AllLandIndependently()
    {
        var ext = ".js";
        var b0 = SectionBody(ext, "alpha", 1);
        var b1 = SectionBody(ext, "beta", 2);
        var b2 = SectionBody(ext, "gamma", 3);
        var content = $"// top\n\n{b0}\n\n{b1}\n\n{b2}\n\n// bottom";
        var n0 = SectionBody(ext, "alpha", 100);
        var n2 = SectionBody(ext, "gamma", 300);
        var edits = new List<EditPair>
        {
            new() { OldString = b0, NewString = n0, LineNumber = 3 },
            new() { OldString = b2, NewString = n2, LineNumber = 9 },
        };

        var (replaced, applied, error) = FuzzHarness.RunBatchApplyMirror(content, edits, "update the alpha and gamma sections");

        Assert.True(replaced, $"non-overlapping batch must apply: {error}");
        // Pure sequential substitution — no cross-edit interference.
        var expected = content.Replace(b0, n0).Replace(b2, n2);
        Assert.Equal(expected, applied);
        // Byte delta is exactly the sum of the per-edit deltas.
        Assert.Equal(content.Length + (n0.Length - b0.Length) + (n2.Length - b2.Length), applied.Length);
        // Every edit landed: old bodies gone, new bodies present once.
        Assert.Equal(0, CountOccurrences(applied, b0));
        Assert.Equal(0, CountOccurrences(applied, b2));
        Assert.Equal(1, CountOccurrences(applied, n0));
        Assert.Equal(1, CountOccurrences(applied, n2));
        // Untouched sibling section and the comments are byte-identical.
        Assert.Equal(1, CountOccurrences(applied, b1));
        Assert.Contains("// top", applied);
        Assert.Contains("// bottom", applied);
    }

    [Fact]
    public void BatchApply_OverlappingEdits_RejectsWholeBatchUntouched()
    {
        var ext = ".js";
        var b0 = SectionBody(ext, "alpha", 1);
        var inner = "    const tmp = 1;"; // substring of b0 (after trim: "const tmp = 1;" ⊂ b0)
        var content = $"// top\n\n{b0}";
        var edits = new List<EditPair>
        {
            new() { OldString = b0, NewString = SectionBody(ext, "alpha", 100) },
            new() { OldString = inner, NewString = "    this.count = 200;", LineNumber = 4 },
        };

        var (replaced, applied, error) = FuzzHarness.RunBatchApplyMirror(content, edits, "update the section");

        Assert.False(replaced, "overlapping batch must be rejected");
        Assert.Equal(content, applied); // byte-identical — nothing applied
        Assert.NotNull(error);
        Assert.Contains("overlap", error);
    }

    [Fact]
    public void BatchApply_OneFailedEdit_NoPartialApplication()
    {
        var ext = ".js";
        var b0 = SectionBody(ext, "alpha", 1);
        var b1 = SectionBody(ext, "beta", 2);
        var content = $"// top\n\n{b0}\n\n{b1}";
        var edits = new List<EditPair>
        {
            new() { OldString = b0, NewString = SectionBody(ext, "alpha", 100) },          // would succeed
            new() { OldString = "function nonexistent() {", NewString = "function x() {" }, // hallucinated
            new() { OldString = b1, NewString = SectionBody(ext, "beta", 200) },           // would succeed
        };

        var (replaced, applied, error) = FuzzHarness.RunBatchApplyMirror(content, edits, "update both sections");

        Assert.False(replaced, "a failing sub-edit must abort the whole batch");
        Assert.Equal(content, applied); // the first (valid) edit must NOT be half-applied
        Assert.NotNull(error);
        Assert.Contains("failed", error);
    }

    [Fact]
    public void BatchApply_NoOpEdits_NotReplaced()
    {
        var ext = ".js";
        var b0 = SectionBody(ext, "alpha", 1);
        var content = $"// top\n\n{b0}";
        var edits = new List<EditPair>
        {
            new() { OldString = b0, NewString = b0 }, // identical old == new
        };

        var (replaced, applied, error) = FuzzHarness.RunBatchApplyMirror(content, edits, "update the section");

        Assert.False(replaced, "a no-op batch is not a replacement");
        Assert.Equal(content, applied);
        Assert.NotNull(error);
    }

    [Fact]
    public void BatchApply_IdenticalOldStrings_RejectedAsOverlap()
    {
        // Two edits targeting the SAME section (identical oldString) must be rejected
        // by the overlap pass — the batch cannot disambiguate which occurrence to edit.
        var ext = ".js";
        var b0 = SectionBody(ext, "alpha", 1);
        var content = $"// top\n\n{b0}\n\n{b0}";
        var edits = new List<EditPair>
        {
            new() { OldString = b0, NewString = SectionBody(ext, "alpha", 100) },
            new() { OldString = b0, NewString = SectionBody(ext, "alpha", 200) },
        };

        var (replaced, applied, error) = FuzzHarness.RunBatchApplyMirror(content, edits, "update the duplicate sections");

        Assert.False(replaced, "identical oldStrings are an overlap and must be rejected");
        Assert.Equal(content, applied);
        Assert.NotNull(error);
        Assert.Contains("overlap", error);
    }

    [Fact]
    public void BatchApply_IdenticalOldStrings_DistinctLineNumbers_ApplyCleanly()
    {
        // Position-aware overlap (mirrors AgentController.ApplyEdit): identical anchors
        // are NOT an overlap when each edit carries its own LineNumber hint — the hint
        // disambiguates which occurrence to edit. This is exactly what deterministic
        // multi-match batches ("update all five RetryCount defaults") emit.
        var line = "const retryCount = 3;";
        var content = $"{line}\n{line}\n{line}\n";
        var edits = new List<EditPair>
        {
            new() { OldString = line, NewString = "const retryCount = 5;", LineNumber = 1 },
            new() { OldString = line, NewString = "const retryCount = 5;", LineNumber = 2 },
            new() { OldString = line, NewString = "const retryCount = 5;", LineNumber = 3 },
        };

        var (replaced, applied, error) = FuzzHarness.RunBatchApplyMirror(content, edits, "update all retryCount defaults to 5");

        Assert.True(replaced);
        Assert.Null(error);
        Assert.Equal("const retryCount = 5;\nconst retryCount = 5;\nconst retryCount = 5;\n", applied);
    }

    [Fact]
    public void BatchApply_IdenticalOldStrings_WrongLineNumber_FailsClosed()
    {
        // Two identical anchors whose line hints point at the SAME occurrence must be
        // rejected as overlap — the hints are ambiguous and the batch cannot prove each
        // edit targets a unique area.
        var line = "const retryCount = 3;";
        var content = $"{line}\n{line}\n";
        var edits = new List<EditPair>
        {
            new() { OldString = line, NewString = "const retryCount = 5;", LineNumber = 1 },
            new() { OldString = line, NewString = "const retryCount = 6;", LineNumber = 1 },
        };

        var (replaced, applied, error) = FuzzHarness.RunBatchApplyMirror(content, edits, "update the retryCount defaults");

        Assert.False(replaced);
        Assert.Equal(content, applied);
        Assert.NotNull(error);
        Assert.Contains("overlap", error);
    }

    // ── Fuzz corpus ──────────────────────────────────────────────────────────

    /// <summary>
    /// 30 seeded docs with repeated section patterns across .ts/.js/.cs/.txt/.css,
    /// cycling four variants: (0) distinct non-overlapping edits all land with the
    /// final content equal to the pure sequential substitution and every sibling
    /// byte-identical; (1) one hallucinated sub-edit → whole batch rejected, file
    /// byte-identical (no partial application); (2) one overlapping sub-edit → whole
    /// batch rejected; (3) no-op edits → not replaced. Non-vacuous guards prove every
    /// variant fired.
    /// </summary>
    [Fact]
    public void Fuzz_BatchEdit_Chain_LandsEveryEditWithoutInterference()
    {
        const int docCount = 30;
        const int seed = 424242;
        const int prime = 7919;
        var checkedCount = 0;
        var allLand = 0;
        var hallucinationRejects = 0;
        var overlapRejects = 0;
        var noopRejects = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(seed, i, prime);
            var ext = Extensions[i % Extensions.Length];
            var relPath = $"gen/BatchCorpus/batch_doc_{i:D2}{ext}";
            var variant = i % 4;

            // Build sections: each has a comment line + a unique-token body.
            var sectionCount = rng.Next(2, 5);
            var parts = new List<string>();
            var bodies = new List<string>();
            for (var k = 0; k < sectionCount; k++)
            {
                var body = SectionBody(ext, $"blk{i}{k}", rng.Next(10, 99));
                parts.Add(Comment(i, k, ext));
                parts.Add(body);
                bodies.Add(body);
            }
            var content = string.Join("\n\n", parts);

            var edits = new List<EditPair>();
            switch (variant)
            {
                case 0:
                    // Distinct non-overlapping edits on sections 0 and (2 if present).
                    var targets = new List<int> { 0, sectionCount > 2 ? 2 : 1 };
                    foreach (var t in targets.Distinct())
                    {
                        edits.Add(new EditPair
                        {
                            OldString = bodies[t],
                            NewString = SectionBody(ext, $"blk{i}{t}", rng.Next(100, 999)),
                            LineNumber = t * 2 + 2,
                        });
                    }
                    break;
                case 1:
                    // Valid edit, then a hallucinated one, then another valid edit.
                    edits.Add(new EditPair { OldString = bodies[0], NewString = SectionBody(ext, $"blk{i}{0}", 111), LineNumber = 2 });
                    edits.Add(new EditPair { OldString = $"function ghost_{i}() {{\n    const x = 1;\n  }}", NewString = "function ghost() {}", LineNumber = 99 });
                    edits.Add(new EditPair { OldString = bodies[1], NewString = SectionBody(ext, $"blk{i}{1}", 222), LineNumber = 4 });
                    break;
                case 2:
                    // A valid edit plus an overlapping one (substring of the first oldString).
                    edits.Add(new EditPair { OldString = bodies[0], NewString = SectionBody(ext, $"blk{i}{0}", 333), LineNumber = 2 });
                    var innerLine = bodies[0].Split('\n').First(l => l.Trim().Length > 0);
                    edits.Add(new EditPair { OldString = innerLine, NewString = "// touched", LineNumber = 3 });
                    break;
                default:
                    // No-op edits — old == new.
                    edits.Add(new EditPair { OldString = bodies[0], NewString = bodies[0], LineNumber = 2 });
                    edits.Add(new EditPair { OldString = bodies[1], NewString = bodies[1], LineNumber = 4 });
                    break;
            }

            var (replaced, applied, error) = FuzzHarness.RunBatchApplyMirror(content, edits, $"update sections in {relPath}");

            switch (variant)
            {
                case 0:
                    Assert.True(replaced, $"doc #{i} batch must apply: {error}");
                    // Final == pure sequential substitution.
                    var expected = content;
                    foreach (var e in edits) expected = expected.Replace(e.OldString, e.NewString);
                    Assert.Equal(expected, applied);
                    // Byte delta == sum of per-edit deltas (no cross-edit drift).
                    var delta = edits.Sum(e => e.NewString.Length - e.OldString.Length);
                    Assert.Equal(content.Length + delta, applied.Length);
                    // Every edit landed; every sibling comment and untouched body byte-identical.
                    foreach (var e in edits)
                    {
                        Assert.Equal(0, CountOccurrences(applied, e.OldString));
                        Assert.Equal(1, CountOccurrences(applied, e.NewString));
                    }
                    var editedTokens = edits.Select(e => e.OldString.Split('\n')[0].Trim()).ToHashSet();
                    for (var k = 0; k < sectionCount; k++)
                    {
                        Assert.Equal(1, CountOccurrences(applied, Comment(i, k, ext)));
                        if (!editedTokens.Contains(bodies[k].Split('\n')[0].Trim()))
                            Assert.Equal(1, CountOccurrences(applied, bodies[k]));
                    }
                    allLand++;
                    break;
                case 1:
                    Assert.False(replaced, $"doc #{i} hallucinated sub-edit must reject the batch: {error}");
                    Assert.Equal(content, applied); // NO partial application
                    Assert.NotNull(error);
                    Assert.Contains("failed", error); // rejected by sequential failure, not overlap
                    hallucinationRejects++;
                    break;
                case 2:
                    Assert.False(replaced, $"doc #{i} overlapping sub-edit must reject the batch: {error}");
                    Assert.Equal(content, applied); // untouched
                    Assert.NotNull(error);
                    Assert.Contains("overlap", error);
                    overlapRejects++;
                    break;
                default:
                    Assert.False(replaced, $"doc #{i} no-op batch must not replace: {error}");
                    Assert.Equal(content, applied);
                    Assert.NotNull(error);
                    Assert.Contains("no net change", error); // genuinely a no-op, not a sub-edit failure
                    noopRejects++;
                    break;
            }
            checkedCount++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedCount, docCount, "batch-edit corpus");
        FuzzHarness.AssertExercised(allLand, "no fuzz doc exercised the all-land path");
        FuzzHarness.AssertExercised(hallucinationRejects, "no fuzz doc exercised the hallucination-rejection path");
        FuzzHarness.AssertExercised(overlapRejects, "no fuzz doc exercised the overlap-rejection path");
        FuzzHarness.AssertExercised(noopRejects, "no fuzz doc exercised the no-op path");
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
}
