using System.Text.RegularExpressions;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Shared discipline for the seeded-random corpus tests (the fuzz / corpus full-chain
/// suites in LlmCssCleanerPipelineTests, AstCodeEditorServiceTests, HtmlDomEditorTests,
/// and FormatCCorpusTests). Every corpus derives its per-doc RNG from a fixed
/// (seed, prime) pair so the docs are byte-identical across runs and machines, and every
/// corpus carries the same guard discipline: doc counters that fail loudly on degradation,
/// byte-identical no-op asserts with self-diagnosing messages, and branch-hit tallies that
/// prove no strategy branch was silently skipped. Keeping all of it in one place means a
/// new corpus gets the same rigor for free — and a rule change applies everywhere at once
/// instead of drifting per-file.
/// </summary>
internal static class FuzzHarness
{
    /// <summary>
    /// The standard seeded per-doc RNG: <c>new Random(seed + docIdx * prime)</c>. Pick a
    /// unique (seed, prime) per corpus so no two corpora share a doc stream, but each
    /// corpus is deterministic forever.
    /// </summary>
    public static Random SeededRng(int seed, int docIdx, int prime) => new(seed + docIdx * prime);

    /// <summary>
    /// Guard against a silently-degraded corpus: exactly <paramref name="expectedCount"/>
    /// docs must have been processed. A corpus that skips docs (e.g. no usable anchor) must
    /// fail loudly, not pass having checked nothing.
    /// </summary>
    public static void AssertAllDocsChecked(int checkedCount, int expectedCount, string corpusName)
    {
        Assert.True(checkedCount == expectedCount,
            $"Only {checkedCount}/{expectedCount} corpus docs were processed — {corpusName}");
    }

    /// <summary>
    /// Guard against a vacuous pass: <paramref name="count"/> must be &gt; 0 — the corpus
    /// must have actually exercised the path whose claim is given. Pass the claim as a
    /// complete sentence; it is surfaced verbatim in the failure message.
    /// </summary>
    public static void AssertExercised(int count, string claim)
    {
        Assert.True(count > 0, claim);
    }

    /// <summary>
    /// Assert a deterministic transform left the doc byte-identical, with the classic
    /// self-diagnosing fuzz message (input and output side by side). <paramref name="subject"/>
    /// names the transform (e.g. "Clean()"), <paramref name="label"/> names the output column.
    /// </summary>
    public static void AssertByteIdenticalNoOp(
        string original, string result, string subject, int docIdx, string label = "result")
    {
        Assert.True(string.Equals(original, result, StringComparison.Ordinal),
            $"{subject} corrupted fuzz doc #{docIdx}:\n{original}\n--- {label} ---\n{result}");
    }

    /// <summary>
    /// Deterministic mirror of <c>AgentController.FormatSnippetAsync</c>'s base-indent
    /// realign (min-indent strip + baseIndent prefix), used by the FORMAT C corpora to
    /// compose newString exactly as the agent does for insert (anchor + "\n" + indented)
    /// and replace (indented). The prettier pass is deliberately NOT spawned — unit tests
    /// never run formatter binaries.
    /// </summary>
    public static string FormatSnippetRealign(string oldSource, string newCode)
    {
        var oldLines = oldSource.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var firstRealLine = oldLines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (firstRealLine == null) return newCode;
        var baseIndent = Regex.Match(firstRealLine, @"^(\s*)").Value;
        if (string.IsNullOrEmpty(baseIndent)) return newCode;

        var lines = newCode.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var minIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
                minIndent = Math.Min(minIndent, line.TakeWhile(char.IsWhiteSpace).Count());
        }
        if (minIndent == int.MaxValue) minIndent = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
                lines[i] = baseIndent + (minIndent < lines[i].Length ? lines[i].Substring(minIndent) : "");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Deterministic mirror of the batch-apply path for multi-edit steps
    /// (<c>AgentController</c> ~4242-4295). Replicates exactly: (1) the overlap-rejection
    /// pass — any pair of trimmed-normalized oldStrings where one CONTAINS the other
    /// rejects the whole batch (each batch edit must target a unique, non-overlapping
    /// area); (2) sequential application via <c>TryReplaceSafe</c> with the per-edit
    /// LineNumber (falling back to 0), threading the evolving content through; (3) NO
    /// partial application — the first failing sub-edit aborts the whole batch and the
    /// original content is returned untouched; (4) a batch whose result equals the input
    /// (all no-op edits) is reported as NOT replaced. This is the deterministic core of
    /// the batch path — deliberately not mirrored: the SSE logging and the surrounding
    /// single-edit fallback orchestration.
    /// </summary>
    public static (bool replaced, string newContent, string? error) RunBatchApplyMirror(
        string content, List<EditPair> edits, string change)
    {
        // 1. Overlap rejection — every edit must target a unique, non-overlapping area.
        for (var oi = 0; oi < edits.Count; oi++)
        {
            var normO = AgentTextUtilities.NormalizeLineEndings(edits[oi].OldString ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normO)) continue;
            for (var oj = oi + 1; oj < edits.Count; oj++)
            {
                var normJ = AgentTextUtilities.NormalizeLineEndings(edits[oj].OldString ?? "").Trim();
                if (string.IsNullOrWhiteSpace(normJ)) continue;
                if (normO.Contains(normJ) || normJ.Contains(normO))
                {
                    return (false, content,
                        $"Batch sub-edit overlap: edit {oi + 1} and edit {oj + 1} target overlapping oldString sections");
                }
            }
        }

        // 2. Sequential apply — each edit lands on the evolving content independently.
        var batchContent = content;
        foreach (var edit in edits)
        {
            if (string.IsNullOrWhiteSpace(edit.OldString)) continue;
            var normOld = AgentTextUtilities.NormalizeLineEndings(edit.OldString);
            var normNew = AgentTextUtilities.NormalizeLineEndings(edit.NewString);
            var (hasReplaced, nc, err, _) = AgentEditHeuristics.TryReplaceSafe(
                batchContent, normOld, normNew,
                edit.LineNumber > 0 ? edit.LineNumber : 0, change);
            if (!hasReplaced)
                return (false, content, $"Batch sub-edit failed: {err}");
            batchContent = nc;
        }

        // 3. No-op batch guard — result identical to input is NOT a replacement.
        if (batchContent == content)
            return (false, content, "batch produced no net change");
        return (true, batchContent, null);
    }

    /// <summary>
    /// Deterministic mirror of the CreateFile apply path
    /// (<c>AgentController.ApplyFullFile</c> for a file that does NOT exist yet).
    /// Replicates the exact byte transforms the pipeline applies to fullFile content:
    /// <c>StripFullFileFence</c> (markdown-fence strip + CRLF→LF + edge-newline trim), then
    /// the parse-time no-ops <c>AutoFixPythonStatements</c> (non-.py) and
    /// <c>CleanVerbatimStringEscapes</c> (no verbatim SQL strings), then the CSS-only
    /// <c>LlmCssCleaner.Clean</c> pass. Deliberately NOT mirrored (unit tests never spawn
    /// them): <c>CodeFormatterService</c> (external prettier/Roslyn), the
    /// <c>EnsureCompleteFullFile</c> LLM continuation (callers must assert
    /// <c>!IsFullFileTruncated</c>), <c>AutoIndentFullFile</c> (only for files that already
    /// exist), and <c>MergeDuplicateCssRules</c> (no-op on the corpus's unique-selector CSS).
    /// On clean corpus content this mirror must return the input byte-identical.
    /// </summary>
    public static string ApplyCreateFileMirror(string fullFile, string relPath)
    {
        var body = AgentTextUtilities.StripFullFileFence(fullFile);
        body = AgentCodeFormatting.AutoFixPythonStatements(body, relPath);
        body = AgentTextUtilities.CleanVerbatimStringEscapes(body);
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is ".css" or ".scss" or ".less")
            body = LlmCssCleaner.Clean(body);
        return body;
    }

    // ── Shared delete-corpus machinery (direct HTML chain ↔ FORMAT D payload route) ──
    // The DELETE corpus is exercised through TWO routes that MUST stay byte-identical:
    // the direct HtmlDomEditor chain (HtmlDomEditorTests) and the FORMAT D PAYLOAD route
    // (FormatDPayloadCorpusTests, which composes its PlanStep via the real
    // ParseStepFromJson with targetType=html + targetName + empty newCode). Both corpora
    // build their docs through BuildDeleteCorpusDoc below so a doc-shape change updates
    // both at once instead of silently diverging the two routes. RNG consumption is FIXED:
    // cardCount = 2 + rng.Next(3), then per random card exactly 3 draws (classes/
    // handlers/labels), plus one filler draw for variant 4 — byte-identical in both callers.

    /// <summary>The same-tag/class card shape the delete corpora anchor on — every card is
    /// <c>&lt;div class="card"&gt;</c>, so the &lt;div class="card"&gt; count is a precise
    /// anti-over-match invariant and "duplicate-similar" holds by construction.</summary>
    public static string BuildDeleteCard(string heading, string cls, string handler, string label)
    {
        return "<div class=\"card\">\n" +
               "  <h2>" + heading + "</h2>\n" +
               "  <button class=\"" + cls + "\" (click)=\"" + handler + "\">" + label + "</button>\n" +
               "</div>";
    }

    private static readonly string[] DeleteCardClasses =
        { "ghost", "primary", "small", "large", "active", "disabled" };

    private static readonly string[] DeleteCardHandlers =
        { "removeItem(item)", "saveForm()", "togglePanel()", "onClick($event)" };

    private static readonly string[] DeleteCardLabels =
        { "Save", "Delete", "Edit", "Open", "Close", "Run" };

    /// <summary>Marker words for keyword-disambiguated deletion (≥4 lowercase letters so
    /// ExtractDisambiguationKeywords keeps them; stopwords/digits would be dropped).</summary>
    public static readonly string[] DeleteMarkers =
        { "wisp", "quill", "sable", "vanta", "okapi" };

    /// <summary>Index of the <paramref name="n"/>-th (1-based) occurrence of <paramref name="block"/>, or -1.</summary>
    public static int NthIndexOf(string content, string block, int n)
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
    /// Shared 5-variant duplicate-card doc builder for the delete corpora. Variants: 0
    /// unique target (removed, siblings intact), 1 byte-identical dup pair + keyword
    /// marker comment (marked occurrence removed, sibling survives), 2 dup pair + target
    /// line (nearest occurrence removed, sibling survives), 3 dup pair + no context (must
    /// refuse), 4 SHORT anchor &lt;div class="card"&gt; + marker-named heading target
    /// isolated behind a 1600+ char filler (the keyword window picks the intended card
    /// among ALL same-tag/class siblings — never the no-hint last-candidate fallback).
    /// Returns the joined html, the parts, the expected full-card target (variant 4), the
    /// targetName (full block or short anchor), the 1-based targetLine (variant 2), and
    /// the dup block (variants 1-3). RNG consumption is byte-identical across both
    /// callers (see the section comment above).
    /// </summary>
    public static (string html, List<string> parts, string? expectedTarget, string targetName, int targetLine, string? dup) BuildDeleteCorpusDoc(
        Random rng, int i, int docIdx, int variant, string marker)
    {
        var cardCount = 2 + rng.Next(3); // 2-4 cards
        var dup = variant is 0 or 4 ? null : BuildDeleteCard($"dup{i}", "ghost", "removeItem(item)", "Delete");

        // Every card shares the same tag + class — "duplicate-similar" by construction.
        var parts = new List<string>();
        string? expectedTarget = null;
        if (variant == 4)
        {
            // Fifth variant — the SHORT ambiguous anchor <div class="card"> matches EVERY
            // card. The target card (FIRST, marker-named heading) is separated from the
            // siblings by a 1600+ char filler so each candidate's keyword window (±800
            // before / +200 after, PickBestCandidate) contains the marker word ONLY for
            // the target — the keyword pick is deterministic. Without the keyword the
            // no-hint fallback returns candidates[^1] (a SIBLING).
            expectedTarget = BuildDeleteCard(
                $"{char.ToUpperInvariant(marker[0])}{marker[1..]}{docIdx}", "ghost", "removeItem(item)", "Delete");
            parts.Add(expectedTarget);
            parts.Add(new string(' ', 1600 + rng.Next(800))); // isolates keyword windows
            for (var k = 0; k < cardCount; k++)
                parts.Add(BuildDeleteCard($"blk{docIdx}_{k}",
                    DeleteCardClasses[rng.Next(DeleteCardClasses.Length)],
                    DeleteCardHandlers[rng.Next(DeleteCardHandlers.Length)],
                    DeleteCardLabels[rng.Next(DeleteCardLabels.Length)]));
        }
        else
        {
            if (variant == 1) parts.Add($"<!-- {marker} -->"); // keyword marker before the first dup
            var placed = 0;
            for (var k = 0; k < cardCount; k++)
            {
                string card;
                if (dup != null && placed < 2)
                {
                    card = dup; // byte-identical dup pair as the first two cards
                    placed++;
                }
                else
                {
                    card = BuildDeleteCard($"blk{docIdx}_{k}",
                        DeleteCardClasses[rng.Next(DeleteCardClasses.Length)],
                        DeleteCardHandlers[rng.Next(DeleteCardHandlers.Length)],
                        DeleteCardLabels[rng.Next(DeleteCardLabels.Length)]);
                }
                parts.Add(card);
            }
        }
        var html = "<main>\n" + string.Join("\n\n", parts) + "\n</main>\n";

        var targetName = variant == 4 ? "<div class=\"card\">" : (variant == 0 ? parts[0] : dup!);
        var targetLine = 0;
        if (variant == 2)
            // 1-based line of the SECOND dup's first line, counting every '\n' up to
            // its actual position in the joined content (separators included).
            targetLine = html[..NthIndexOf(html, dup!, 2)].Count(c => c == '\n') + 1;
        return (html, parts, expectedTarget, targetName, targetLine, dup);
    }
}

/// <summary>
/// Tallies how often each branch fired across a corpus and guards that no branch was
/// silently skipped. Constructed with the FULL branch set so a misspelled branch key
/// fails loudly at construction time (KeyNotFound on Hit), not silently as a never-read
/// zero.
/// </summary>
internal sealed class BranchHitCounter<T> where T : notnull
{
    private readonly Dictionary<T, int> _hits;
    private readonly string _corpusName;

    public BranchHitCounter(IEnumerable<T> branches, string corpusName)
    {
        _hits = branches.ToDictionary(b => b, _ => 0);
        _corpusName = corpusName;
    }

    /// <summary>Record one firing of a branch.</summary>
    public void Hit(T branch) => _hits[branch]++;

    /// <summary>Exact hit count for a branch — for corpora whose rotation is exact (e.g. docCount / 2).</summary>
    public int Count(T branch) => _hits[branch];

    /// <summary>
    /// Guard: every branch must have fired at least <c>docCount / branchCount - 1</c> times
    /// (the standard tolerance for a cycled corpus), so no branch can ever be skipped. The
    /// failure message prints the actual per-branch counts.
    /// </summary>
    public void AssertAllExercised(int docCount, int branchCount)
    {
        Assert.True(_hits.Values.All(h => h >= docCount / branchCount - 1),
            $"{_corpusName} under-exercised a branch: {string.Join(", ", _hits.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }
}
