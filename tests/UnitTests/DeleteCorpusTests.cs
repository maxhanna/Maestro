using Xunit;
using Weaver;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Corpus for the DeleteLines / removal strategy. Generates random files with
/// duplicate-similar blocks, then runs the COMPLETE deterministic chain the agent
/// executes for a deletion step: EditClassifier.Classify → ClassifyIntent →
/// EditStrategyResolver.Decide → AgentEditHeuristics.TryReplaceSafe (oldStr → empty newStr,
/// with step.LineNumber and step.Change — exactly the call at AgentController:4529).
///
/// The claim locked here is the anti-over-match guarantee: a deletion removes ONLY
/// the exact target lines — never a fuzzy match that eats a sibling block. In
/// particular TryReplaceSafe's duplicate-handling must be honored: with multiple
/// occurrences of oldStr it REFUSES (returns false, file untouched) unless a change
/// keyword or target line disambiguates, and even then it removes exactly one
/// occurrence of exactly the target block's bytes. Every success case asserts the
/// byte-length delta == oldStr.Length, the surviving duplicate is the UNTARGETED one,
/// and all sibling blocks remain byte-identical.
/// </summary>
public class DeleteCorpusTests
{
    private static readonly string[] Extensions = { ".ts", ".js", ".cs", ".txt", ".css" };
    private static readonly string[] MarkerWords = { "wisp", "quill", "sable", "vanta", "okapi" };

    // ── Generators ───────────────────────────────────────────────────────────

    private static string BlockBody(string ext, string token, int bodyNum) => ext switch
    {
        ".cs"  => $"    public void {token}()\n    {{\n        var tmp = {bodyNum};\n    }}",
        ".ts"  => $"  {token}(): void {{\n    this.count = {bodyNum};\n  }}",
        ".css" => $".{token} {{ margin: {bodyNum}px; color: #12ab34; }}",
        ".txt" => $"section {token}:\n  value = {bodyNum}",
        _      => $"  {token}() {{\n    const tmp = {bodyNum};\n  }}",
    };

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

    /// <summary>Index of the <paramref name="n"/>-th (1-based) occurrence of <paramref name="block"/>, or -1.</summary>
    private static int NthIndexOf(string content, string block, int n)
    {
        var pos = -1;
        for (var i = 0; i < n; i++)
        {
            pos = content.IndexOf(block, pos + 1, StringComparison.Ordinal);
            if (pos < 0) return -1;
        }
        return pos;
    }

    /// <summary>
    /// The complete deletion chain mirroring AgentController:4529 — Classify, intent,
    /// Decide, then TryReplaceSafe with empty newStr and the step's line/change used
    /// for duplicate disambiguation. The classifier/resolver verdicts are asserted
    /// inside so a routing regression fails loudly.
    /// </summary>
    private static (bool replaced, string applied, string? error) RunDeleteChain(
        string content, string oldStr, string change, int targetLine, string ext, string relPath)
    {
        var step = new PlanStep
        {
            File = relPath,
            Change = change,
            OldString = oldStr,
            NewString = "",
            LineNumber = targetLine,
        };
        Assert.Equal(EditStrategy.DeleteLines, EditClassifier.Classify(step, fileExists: true, ext));
        var intent = EditClassifier.ClassifyIntent(step, ext);
        Assert.Equal(EditIntentKind.DeleteContent, intent.Kind);
        var decision = EditStrategyResolver.Decide(relPath, true, content, change, intent);
        Assert.Equal(EditStrategy.DeleteLines, decision.Strategy);
        var (replaced, newContent, matchError, _) = AgentEditHeuristics.TryReplaceSafe(content, oldStr, "", targetLine, change);
        return (replaced, newContent, matchError);
    }

    // ── Deterministic tests ──────────────────────────────────────────────────

    [Fact]
    public void DeleteChain_SingleBlock_RemovesExactBlockOnly()
    {
        const string content =
            "function alpha() {\n  return 1;\n}\n\n" +
            "function beta() {\n  return 2;\n}\n\n" +
            "function gamma() {\n  return 3;\n}";
        const string oldStr = "function beta() {\n  return 2;\n}";
        var (replaced, applied, error) = RunDeleteChain(content, oldStr, "remove the beta function", 0, ".js", "gen/del.js");

        Assert.True(replaced, $"single-match deletion must succeed: {error}");
        Assert.Equal(content.Length - oldStr.Length, applied.Length);
        var idx = content.IndexOf(oldStr, StringComparison.Ordinal);
        Assert.Equal(content[..idx] + content[(idx + oldStr.Length)..], applied);
        // Siblings byte-identical.
        Assert.True(applied.Contains("function alpha() {", StringComparison.Ordinal));
        Assert.True(applied.Contains("function gamma() {", StringComparison.Ordinal));
        Assert.False(applied.Contains("beta", StringComparison.Ordinal));
    }

    [Fact]
    public void DeleteChain_DuplicateBlocks_NoContext_RefusesAndLeavesFileUntouched()
    {
        // Two byte-identical blocks, change carries only stopwords, no target line →
        // TryReplaceSafe must REFUSE rather than guess which one to delete.
        const string content =
            "function helper() {\n  return 1;\n}\n\n" +
            "function helper() {\n  return 1;\n}";
        const string oldStr = "function helper() {\n  return 1;\n}";
        var (replaced, applied, error) = RunDeleteChain(content, oldStr, "remove the block", 0, ".js", "gen/del.js");

        Assert.False(replaced, "duplicate oldString with no disambiguation must refuse");
        Assert.Equal(content, applied); // byte-identical — no sibling eaten
        Assert.NotNull(error);
        Assert.Contains("found 2 times", error);
    }

    [Fact]
    public void DeleteChain_DuplicateBlocks_KeywordContext_PicksMarkedTarget()
    {
        // A change keyword ("wisp") appears only in the lookback of the FIRST block
        // (via the // wisp marker) — the keyword-context scoring must pick that one,
        // leaving the sibling duplicate untouched.
        const string content =
            "// wisp\n" +
            "function helper() {\n  return 1;\n}\n\n" +
            "function helper() {\n  return 1;\n}";
        const string oldStr = "function helper() {\n  return 1;\n}";
        var (replaced, applied, error) = RunDeleteChain(content, oldStr, "remove the wisp block", 0, ".js", "gen/del.js");

        Assert.True(replaced, $"keyword-disambiguated deletion must succeed: {error}");
        Assert.Equal(content.Length - oldStr.Length, applied.Length);
        // The surviving duplicate is the SECOND (untargeted) one — it shifts left by
        // exactly oldStr.Length after the first is removed; the marker stays.
        Assert.Equal(NthIndexOf(content, oldStr, 2) - oldStr.Length, applied.IndexOf(oldStr, StringComparison.Ordinal));
        Assert.Contains("// wisp", applied);
        Assert.Equal(1, CountOccurrences(applied, oldStr));
    }

    [Fact]
    public void DeleteChain_DuplicateBlocks_TargetLine_PicksNearest()
    {
        const string content =
            "function helper() {\n  return 1;\n}\n\n" +
            "function helper() {\n  return 1;\n}";
        const string oldStr = "function helper() {\n  return 1;\n}";
        // Line number of the SECOND block's first line (1-based).
        var targetLine = content[..NthIndexOf(content, oldStr, 2)].Count(c => c == '\n') + 1;
        var (replaced, applied, error) = RunDeleteChain(content, oldStr, "remove the block", targetLine, ".js", "gen/del.js");

        Assert.True(replaced, $"line-disambiguated deletion must succeed: {error}");
        Assert.Equal(content.Length - oldStr.Length, applied.Length);
        // The surviving duplicate is the FIRST (untargeted) one.
        Assert.Equal(NthIndexOf(content, oldStr, 1), applied.IndexOf(oldStr, StringComparison.Ordinal));
        Assert.Equal(1, CountOccurrences(applied, oldStr));
    }

    [Fact]
    public void DeleteChain_FuzzyFallback_RemovesExactTargetBlockNotSibling()
    {
        // oldStr has a leading blank line, so the verbatim scan finds zero matches and
        // the line-based fuzzy fallback kicks in. It must remove EXACTLY the target
        // block's bytes from the line start — never touching the sibling block below.
        const string content =
            "function alpha() {\n  return 1;\n}\n\n" +
            "function alpha() {\n  return 2;\n}";
        const string oldStr = "\nfunction alpha() {\n  return 1;\n}";
        var (replaced, applied, error) = RunDeleteChain(content, oldStr, "remove the first block", 0, ".js", "gen/del.js");

        Assert.True(replaced, $"fuzzy fallback deletion must succeed: {error}");
        Assert.Equal(content.Length - oldStr.Length, applied.Length);
        // Sibling (second block, return 2) fully intact.
        Assert.Contains("function alpha() {\n  return 2;\n}", applied);
        Assert.DoesNotContain("return 1;", applied);
    }

    // ── Fuzz corpus ──────────────────────────────────────────────────────────

    /// <summary>
    /// 30 seeded docs across .ts/.js/.cs/.txt/.css with duplicate-similar blocks,
    /// cycling four variants: unique target (removed), duplicate + keyword marker
    /// (marked occurrence removed, sibling survives), duplicate + target line
    /// (nearest occurrence removed, sibling survives), and duplicate + no context
    /// (must REFUSE and leave the file byte-identical). Every success asserts the
    /// byte-length delta equals exactly the target block's length and every sibling
    /// block's occurrence count is unchanged — never a fuzzy over-match.
    /// </summary>
    [Fact]
    public void Fuzz_Delete_Chain_RemovesOnlyTargetLines()
    {
        const int docCount = 30;
        const int seed = 24601;
        const int prime = 13331;
        var checkedCount = 0;
        var uniqueRemovals = 0;
        var keywordRemovals = 0;
        var lineRemovals = 0;
        var refusals = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(seed, i, prime);
            var ext = Extensions[i % Extensions.Length];
            var relPath = $"gen/DeleteCorpus/delete_doc_{i:D2}{ext}";
            var variant = i % 4;
            var marker = MarkerWords[i % MarkerWords.Length];
            var dup = variant == 0 ? null : BlockBody(ext, $"blk{i}d", rng.Next(10, 99));

            // Build the doc as a list of parts (a marker line and/or block bodies)
            // separated by blank lines; track the target's start line for the line variant.
            var parts = new List<string>();
            if (variant == 1) parts.Add($"// {marker}");            // keyword marker before first dup
            var blockCount = rng.Next(2, 5);
            var placed = 0;
            for (var k = 0; k < blockCount; k++)
            {
                string body;
                if (variant != 0 && placed < 2)   // first two blocks are the byte-identical dup pair
                {
                    body = dup!;
                    placed++;
                }
                else
                {
                    body = BlockBody(ext, $"blk{i}{k}", rng.Next(10, 99));
                }
                parts.Add(body);
            }
            var content = string.Join("\n\n", parts);
            // Variant 0 has no marker prefix, so the unique target is the first block.
            var oldStr = variant == 0
                ? parts[0]
                : dup!;
            var targetLine = 0;
            if (variant == 2)
                // 1-based line of the SECOND dup's first line, counting every '\n' up to
                // its actual position in the joined content (separators included).
                targetLine = content[..NthIndexOf(content, oldStr, 2)].Count(c => c == '\n') + 1;
            var change = variant switch
            {
                0 => "remove the first block",
                1 => $"remove the {marker} block",
                2 => "remove the block",
                _ => "remove the block",
            };

            var (replaced, applied, error) = RunDeleteChain(content, oldStr, change, targetLine, ext, relPath);

            // Chain routing is asserted inside RunDeleteChain. Now the anti-over-match invariants:
            switch (variant)
            {
                case 0:
                    Assert.True(replaced, $"doc #{i} unique target must delete: {error}");
                    Assert.Equal(content.Length - oldStr.Length, applied.Length);
                    Assert.False(applied.Contains(oldStr, StringComparison.Ordinal));
                    // Every sibling block's occurrence count unchanged.
                    foreach (var part in parts)
                    {
                        if (part == oldStr) continue;
                        var before = CountOccurrences(content, part);
                        var after = CountOccurrences(applied, part);
                        Assert.True(before == after,
                            $"doc #{i} sibling block [{part.Split('\n')[0]}] count changed {before} → {after}");
                    }
                    uniqueRemovals++;
                    break;
                case 1:
                    Assert.True(replaced, $"doc #{i} keyword target must delete: {error}");
                    Assert.Equal(content.Length - oldStr.Length, applied.Length);
                    // Marked (first) duplicate removed; the sibling duplicate survives,
                    // shifted left by exactly oldStr.Length.
                    Assert.Equal(NthIndexOf(content, oldStr, 2) - oldStr.Length, applied.IndexOf(oldStr, StringComparison.Ordinal));
                    Assert.Equal(1, CountOccurrences(applied, oldStr));
                    Assert.True(applied.Contains($"// {marker}", StringComparison.Ordinal));
                    keywordRemovals++;
                    break;
                case 2:
                    Assert.True(replaced, $"doc #{i} line target must delete: {error}");
                    Assert.Equal(content.Length - oldStr.Length, applied.Length);
                    // Nearest (second) duplicate removed; the first sibling survives.
                    Assert.Equal(NthIndexOf(content, oldStr, 1), applied.IndexOf(oldStr, StringComparison.Ordinal));
                    Assert.Equal(1, CountOccurrences(applied, oldStr));
                    lineRemovals++;
                    break;
                default:
                    // No context at all → must refuse and leave the file byte-identical.
                    Assert.False(replaced, $"doc #{i} duplicate with no context must refuse");
                    Assert.Equal(content, applied);
                    Assert.NotNull(error);
                    Assert.Contains("times in file", error);
                    refusals++;
                    break;
            }
            checkedCount++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedCount, docCount, "DeleteLines corpus");
        FuzzHarness.AssertExercised(uniqueRemovals, "no fuzz doc exercised the unique-target deletion path");
        FuzzHarness.AssertExercised(keywordRemovals, "no fuzz doc exercised the keyword-disambiguated deletion path");
        FuzzHarness.AssertExercised(lineRemovals, "no fuzz doc exercised the target-line-disambiguated deletion path");
        FuzzHarness.AssertExercised(refusals, "no fuzz doc exercised the duplicate-refusal path");
    }
}
