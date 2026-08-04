using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Seeded-fuzz corpus for <c>AgentUtilities.ReindentReplacementSnippet</c> — the
/// brace-depth re-indenter the oldString/newString apply path uses for .ts/.js edits.
///
/// BUG (fixed, locked here): the apply path used to choose the HTML tag-depth
/// indenter by CONTENT SNIFFING (<c>IsHtmlLikeContent</c>), and TypeScript generics
/// like <c>Promise&lt;void&gt;</c> / <c>Array&lt;string&gt;</c> contain
/// '&lt;void&gt;' / '&lt;string&gt;' which matched the HTML regex — so .ts edits got
/// routed through the tag-depth indenter and flattened to a single level. The fix
/// gates on the file EXTENSION (<c>isHtmlDomFile=false</c> for code) instead.
///
/// This corpus generates dozens of structurally nested TS/JS snippets FILLED with
/// generic signatures and HTML-looking tokens (also braces inside strings and
/// template literals, which must never shift depth), runs the helper, and asserts:
///   * every line lands at the exact brace-depth indentation of the generator's
///     independent structural spec — output nesting is brace-consistent and never
///     flattened to a single level;
///   * the already-correctly-indented render is a byte-identical no-op;
/// so no future content-sniffing or brace-scanner regression can silently corrupt
/// code-file edits again.
/// </summary>
public class ReindentSnippetFuzzCorpusTests
{
    private const int Seed = 60_733;
    private const int Prime = 104_759;
    private const int DocCount = 40;
    private const int IndentSize = 2;

    /// <summary>Anchor line the snippet replaces: the existing method at class-member
    /// level, so baseIndent is 2 spaces and brace-depth 0 == class member.</summary>
    private const int MatchIdx = 1;

    /// <summary>The surrounding file: a small 2-space-indented TS class.</summary>
    private static readonly string[] FileLines =
    {
        "export class Foo {",
        "  existing(): void {",
        "    this.bar();",
        "  }",
        "}",
    };

    /// <summary>The old block being replaced (the existing method).</summary>
    private static readonly List<string> OldBlock = new()
    {
        "  existing(): void {",
        "    this.bar();",
        "  }",
    };

    /// <summary>Signatures that are ALL HTML-sniff traps — each contains '&lt;' '&gt;'
    /// (the old IsHtmlLikeContent regex matched '&lt;\\w+[\\s/&gt;]'), so any return to
    /// content sniffing flattens these instantly. None contains a brace, so they never
    /// shift depth.</summary>
    private static readonly string[] GenericSignatures =
    {
        "async load(): Promise<void>",
        "getItems(): Array<string>",
        "fetchAll(): Promise<Item[]>",
        "mapById(): Map<string, number>",
        "resolve(): Promise<Promise<void>>",
        "lookup(key: string): Record<string, any>",
    };

    private static readonly string[] BlockOpeners =
    {
        "if (this.ready) {",
        "for (const item of this.items) {",
        "while (index > 0) {",
        "try {",
    };

    /// <summary>TS statements. Several carry braces inside strings or template
    /// literals ('}', '${...}', HTML-looking '&lt;div&gt;') that must NOT shift depth —
    /// the generator does not track them, so if the production scanner ever counts
    /// them, the per-line spec assert fails.</summary>
    private static string TsStatement(Random rng, int docIdx) => (rng.Next(6)) switch
    {
        0 => $"const result: Promise<number> = this.fetchData({docIdx});",
        1 => $"const list: Array<string> = items.map(i => ({{ id: i.id, name: i.name }}));",
        2 => "const s = \"}\";",
        3 => "const t = '{';",
        4 => $"const html = `<div class=\"card\">${{this.name}} {{</div>`;",
        _ => $"this.handle(item, {docIdx});",
    };

    /// <summary>JS flavor — same traps, looser typing style.</summary>
    private static string JsStatement(Random rng, int docIdx) => (rng.Next(6)) switch
    {
        0 => $"const result = await this.fetchData({docIdx});",
        1 => "const list = items.map(i => ({ id: i.id }));",
        2 => "const msg = \"}\";",
        3 => $"const label = `Value: ${{result}} {{`;",
        4 => $"const el = document.querySelector('<div class=\"x\">');",
        _ => "this.handle(item);",
    };

    /// <summary>
    /// Builds one structurally-valid nested TS/JS method snippet in FLAT form (every
    /// line at column 0, exactly as an LLM emits it) plus the tracked structural depth
    /// of each line. The generator does its own brace bookkeeping AS IT EMITS — it is
    /// the INDEPENDENT spec the production scanner's output must reproduce. Braces that
    /// live inside strings / template literals are emitted at the surrounding depth and
    /// must not move it. A blank line is occasionally inserted (rng.Next(4)==0) to
    /// stress blank-line preservation; its depth is recorded as -1 (no indent).
    /// </summary>
    private static (List<string> flat, List<int> depths) GenerateSnippet(Random rng, int docIdx, bool isJs)
    {
        var lines = new List<string>();
        var depths = new List<int>();

        var depth = 0;
        var sig = GenericSignatures[rng.Next(GenericSignatures.Length)];
        lines.Add(sig + " {");
        depths.Add(depth);
        depth++; // method body

        var blockCount = 1 + rng.Next(3); // 1-3 nested blocks
        for (var b = 0; b < blockCount; b++)
        {
            lines.Add(BlockOpeners[rng.Next(BlockOpeners.Length)]);
            depths.Add(depth);
            depth++;

            var stmtCount = 1 + rng.Next(3);
            for (var s = 0; s < stmtCount; s++)
            {
                if (rng.Next(4) == 0)
                {
                    lines.Add("");
                    depths.Add(-1);
                }
                lines.Add(isJs ? JsStatement(rng, docIdx) : TsStatement(rng, docIdx));
                depths.Add(depth);
            }

            depth--;
            lines.Add("}");
            depths.Add(depth);
        }

        depth--;
        lines.Add("}");
        depths.Add(depth);
        return (lines, depths);
    }

    /// <summary>Expected leading whitespace for a line at the given tracked depth:
    /// the anchor line's own indent (the class-member base) + depth * indent size.
    /// Blank lines carry none.</summary>
    private static readonly string BaseIndent =
        AgentUtilities.GetLeadingWhitespace(FileLines[MatchIdx]);

    private static string ExpectedIndent(int depth) =>
        depth < 0 ? "" : BaseIndent + new string(' ', depth * IndentSize);

    /// <summary>
    /// The extension-gate WIRING this corpus exists to protect: the apply path picks
    /// the HTML tag-depth indenter only when <c>HtmlDomEditor.IsHtmlDomFile(relPath)</c>
    /// is true. If a future change reverts to content sniffing (e.g. passing
    /// <c>isHtmlDomFile: IsHtmlLikeContent(rawNew)</c> — the original bug that flattened
    /// <c>Promise&lt;void&gt;</c> snippets), these guards fail. The corpus itself only
    /// proves the brace indenter behaves when the gate is already false; this test proves
    /// the gate STAYS false for every code extension this corpus generates for.
    /// </summary>
    [Fact]
    public void ExtensionGate_CodeExtensionsNeverRoutedToHtmlIndenter()
    {
        // .ts and .js are the corpus's code flavors — the gate must never treat them as
        // HTML regardless of the HTML-looking tokens (generics, '<div>' strings) inside.
        Assert.False(HtmlDomEditor.IsHtmlDomFile("src/app/foo.component.ts"),
            "extension gate misclassifies .ts as an HTML DOM file");
        Assert.False(HtmlDomEditor.IsHtmlDomFile("src/app/foo.js"),
            "extension gate misclassifies .js as an HTML DOM file");
        // Real HTML stays gated IN — the indenter must still apply there.
        Assert.True(HtmlDomEditor.IsHtmlDomFile("src/app/foo.component.html"),
            "extension gate stopped classifying .html as an HTML DOM file");
        Assert.True(HtmlDomEditor.IsHtmlDomFile("Pages/Index.cshtml"),
            "extension gate stopped classifying .cshtml as an HTML DOM file");
    }

    /// <summary>The production path under test: ReindentReplacementSnippet with the
    /// code-file gate (isHtmlDomFile:false) — exactly what the .ts/.js apply path calls.</summary>
    private static List<string> RunReindent(List<string> newLines) =>
        AgentUtilities.ReindentReplacementSnippet(
            newLines, OldBlock, FileLines.ToList(), MatchIdx, isHtmlDomFile: false);

    [Fact]
    public void Fuzz_ReindentSnippet_NestedBracesAndGenerics_NeverFlattened_AlwaysBraceConsistent()
    {
        var checkedDocs = 0;
        var tsDocs = 0;
        var jsDocs = 0;
        var deepDocs = 0; // docs whose spec reached depth >= 2 (nested block bodies)

        for (var docIdx = 0; docIdx < DocCount; docIdx++)
        {
            var rng = FuzzHarness.SeededRng(Seed, docIdx, Prime);
            var isJs = docIdx % 2 == 1;
            var (flat, depths) = GenerateSnippet(rng, docIdx, isJs);

            // ── Pass A: flat LLM output must be re-indented to the file's nesting. ──
            var reindented = RunReindent(flat);

            Assert.True(flat.Count == reindented.Count,
                $"Doc #{docIdx}: line count changed {flat.Count} -> {reindented.Count}");
            Assert.True(flat.Count > 2, $"Doc #{docIdx}: snippet too small to exercise the indenter");

            var maxDepth = depths.Max();
            if (maxDepth >= 2) deepDocs++;
            for (var i = 0; i < reindented.Count; i++)
            {
                if (depths[i] < 0)
                {
                    Assert.True(string.IsNullOrWhiteSpace(reindented[i]),
                        $"Doc #{docIdx} line {i}: blank spec line gained content: '{reindented[i]}'");
                    continue;
                }
                var expected = ExpectedIndent(depths[i]);
                var actual = AgentUtilities.GetLeadingWhitespace(reindented[i]);
                Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
                    $"Doc #{docIdx} line {i}: expected indent '{expected}' (depth {depths[i]}) but got '{actual}'.\n" +
                    $"generated: {flat[i]}\nreindented: {reindented[i]}");
            }

            // Never flattened: the nested bodies are strictly deeper than the method level.
            Assert.True(maxDepth >= 2,
                $"Doc #{docIdx}: corpus invariant broken — spec never nests deeper than level 1");
            var deepestLine = reindented[depths.IndexOf(maxDepth)];
            Assert.True(AgentUtilities.GetLeadingWhitespace(deepestLine).Length >
                        ExpectedIndent(1).Length,
                $"Doc #{docIdx}: nesting collapsed to a single level — deepest line '{deepestLine}'");

            // ── Pass B: already-correctly-indented render must be a byte-identical no-op. ──
            var indented = flat.Select((l, i) => depths[i] < 0
                ? l
                : ExpectedIndent(depths[i]) + l.Trim()).ToList();
            var roundTrip = RunReindent(indented);
            Assert.True(indented.SequenceEqual(roundTrip),
                $"Doc #{docIdx}: correctly-indented snippet was modified by the helper:\n" +
                $"in:\n{string.Join("\n", indented)}\n--- out ---\n{string.Join("\n", roundTrip)}");

            checkedDocs++;
            if (isJs) jsDocs++; else tsDocs++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, DocCount, nameof(ReindentSnippetFuzzCorpusTests));
        FuzzHarness.AssertExercised(tsDocs, "corpus never exercised a .ts-flavor doc");
        FuzzHarness.AssertExercised(jsDocs, "corpus never exercised a .js-flavor doc");
        FuzzHarness.AssertExercised(deepDocs, "corpus never exercised a snippet with depth >= 2 (never-flattened assert is vacuous)");
    }
}
