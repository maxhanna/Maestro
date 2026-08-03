using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Deterministic tests for <see cref="HtmlDomEditor"/> — FORMAT D anchor resolution.
/// Guards the exact → whitespace-normalized → collapsed → fuzzy fallback chain,
/// closing-tag expansion, keyword/line disambiguation between duplicate anchors,
/// and the leading-</div> strip that repairs LLM anchor drift.
/// </summary>
public class HtmlDomEditorTests
{
    // ── IsHtmlDomFile ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("wwwroot/index.html", true)]
    [InlineData("wwwroot/page.htm", true)]
    [InlineData("src/page.cshtml", true)]
    [InlineData("src/page.razor", true)]
    [InlineData("src/app/foo.component.ts", false)]
    [InlineData("src/style.css", false)]
    [InlineData("src/app.js", false)]
    public void IsHtmlDomFile_ByExtension(string path, bool expected)
    {
        Assert.Equal(expected, HtmlDomEditor.IsHtmlDomFile(path));
    }

    // ── Exact match ──────────────────────────────────────────────────────────

    [Fact]
    public void ResolveHtmlAnchor_ExactMatch_ReturnsBlock()
    {
        var html = "<div class=\"card\">\n  <button>Split</button>\n</div>";

        var (block, index, error) = HtmlDomEditor.ResolveHtmlAnchor(html, "<div class=\"card\">");

        Assert.Null(error);
        Assert.Contains("<button>Split</button>", block);
        Assert.True(index >= 0);
    }

    // ── Closing-tag expansion ────────────────────────────────────────────────

    [Fact]
    public void ResolveHtmlAnchor_ExpandsToClosingTags_ByDefault()
    {
        var html = "<div class=\"card\">\n  <span>x</span>\n</div>";

        var (block, _, error) = HtmlDomEditor.ResolveHtmlAnchor(html, "<div class=\"card\">");

        Assert.Null(error);
        Assert.Contains("</div>", block);
        Assert.Contains("<span>x</span>", block);
    }

    [Fact]
    public void ResolveHtmlAnchor_ExpansionDisabled_StopsAtOpenTag()
    {
        var html = "<div class=\"card\">\n  <span>x</span>\n</div>";

        var (block, _, error) = HtmlDomEditor.ResolveHtmlAnchor(
            html, "<div class=\"card\">", expandToClosingTags: false);

        Assert.Null(error);
        Assert.Contains("<div class=\"card\">", block);
        Assert.DoesNotContain("</div>", block);
    }

    // ── Whitespace-insensitive (normalized) match ────────────────────────────

    [Fact]
    public void ResolveHtmlAnchor_ExtraWhitespaceInTarget_StillMatches()
    {
        var html = "<div  class=\"card\">\n  <span>x</span>\n</div>";
        // Target has single spaces where the file has double — must still resolve.
        var (block, _, error) = HtmlDomEditor.ResolveHtmlAnchor(html, "<div class=\"card\">");

        Assert.Null(error);
        Assert.Contains("<span>x</span>", block);
    }

    // ── Collapsed (no-whitespace) match ──────────────────────────────────────

    [Fact]
    public void ResolveHtmlAnchor_NewlineBetweenTags_CollapsedMatch()
    {
        var html = "<div\nclass=\"card\">\n  <span>x</span>\n</div>";
        var (block, _, error) = HtmlDomEditor.ResolveHtmlAnchor(html, "<div class=\"card\">");

        Assert.Null(error);
        Assert.Contains("card", block);
        Assert.Contains("<span>x</span>", block);
    }

    // ── Fuzzy attribute match (LLM value drift) ──────────────────────────────

    [Fact]
    public void ResolveHtmlAnchor_FuzzyAttributes_MatchDespiteHallucinatedValues()
    {
        var html = "<button class=\"ghost\" (click)=\"removeItem(item)\">X</button>";
        // The LLM hallucinated a different (click) value — attribute-KEY matching must save it.
        var (block, _, error) = HtmlDomEditor.ResolveHtmlAnchor(
            html, "<button class=\"ghost\" (click)=\"remove_me('RecipeComponent')\">X</button>");

        Assert.Null(error);
        Assert.Contains("removeItem(item)", block);
    }

    [Fact]
    public void ResolveHtmlAnchor_FuzzyAllValuesDiffer_RejectsToAvoidWrongElement()
    {
        // No attribute VALUE matches at all → must not silently pick the wrong element.
        var html = "<button class=\"ghost\" (click)=\"removeItem(item)\">X</button>";
        var (_, _, error) = HtmlDomEditor.ResolveHtmlAnchor(
            html, "<button class=\"solid\" (click)=\"totallyDifferent()\">X</button>");

        Assert.NotNull(error);
    }

    // ── Not found ────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveHtmlAnchor_NotFound_ReturnsError()
    {
        var html = "<div class=\"card\">x</div>";
        var (_, _, error) = HtmlDomEditor.ResolveHtmlAnchor(html, "<div class=\"wizard\">");

        Assert.NotNull(error);
    }

    [Fact]
    public void ResolveHtmlAnchor_EmptyTarget_ReturnsError()
    {
        var (_, _, error) = HtmlDomEditor.ResolveHtmlAnchor("<div>x</div>", "");

        Assert.NotNull(error);
    }

    // ── Duplicate-anchor disambiguation ──────────────────────────────────────

    [Fact]
    public void ResolveHtmlAnchor_DuplicateAnchors_KeywordHintPicksRightOne()
    {
        // The two anchors must be >800 chars apart so each candidate's keyword
        // window (±800 before / +200 after) does not overlap the other block —
        // otherwise both candidates see both keywords and the tie is ambiguous.
        var filler = new string(' ', 1600);
        var html =
            "<div class=\"card\">\n  <h2>Header</h2>\n</div>\n" +
            filler +
            "\n<div class=\"card\">\n  <h2>Footer</h2>\n</div>";

        var (block, _, error) = HtmlDomEditor.ResolveHtmlAnchor(
            html, "<div class=\"card\">", stepChange: "Update the footer card text");

        Assert.Null(error);
        Assert.Contains("Footer", block);
        Assert.DoesNotContain("Header", block);
    }

    [Fact]
    public void ResolveHtmlAnchor_DuplicateAnchors_LineHintPicksRightOne()
    {
        var html = """
            <div class="card">
              <h2>Header</h2>
            </div>
            <div class="card">
              <h2>Footer</h2>
            </div>
            """;

        // centerLine 4 = the second card's opening tag line.
        var (block, _, error) = HtmlDomEditor.ResolveHtmlAnchor(
            html, "<div class=\"card\">", centerLine: 4);

        Assert.Null(error);
        Assert.Contains("Footer", block);
    }

    // ── GetLineIndent ────────────────────────────────────────────────────────

    [Fact]
    public void GetLineIndent_ReturnsLeadingWhitespaceOfLine()
    {
        var content = "  <div>\n    <span>x</span>\n  </div>";
        var pos = content.IndexOf("<span", StringComparison.Ordinal);

        var indent = HtmlDomEditor.GetLineIndent(content, pos);

        Assert.Equal("    ", indent);
    }

    [Fact]
    public void GetLineIndent_FirstLine_ReturnsEmpty()
    {
        Assert.Equal("", HtmlDomEditor.GetLineIndent("<div>x</div>", 2));
    }

    // ── StripLeadingClosingDivs ──────────────────────────────────────────────

    [Fact]
    public void StripLeadingClosingDivs_StripsExcessClosingDivs()
    {
        var html = "</div>\n</div>\n  <div>real</div>";
        var target = "  <div>real</div>";

        var result = HtmlDomEditor.StripLeadingClosingDivs(html, target);

        Assert.Equal("  <div>real</div>", result);
    }

    [Fact]
    public void StripLeadingClosingDivs_NoExcess_ReturnsUnchanged()
    {
        var html = "</div>\n<div>a</div>";
        var target = "</div>\n<div>a</div>";

        var result = HtmlDomEditor.StripLeadingClosingDivs(html, target);

        Assert.Equal(html, result);
    }

    [Fact]
    public void StripLeadingClosingDivs_EmptyHtml_ReturnsUnchanged()
    {
        Assert.Equal("", HtmlDomEditor.StripLeadingClosingDivs("", null));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FUZZ — ResolveHtmlAnchor must never regress
    // ═══════════════════════════════════════════════════════════════════════════
    // Three seeded random corpora. (1) A target that genuinely EXISTS in the file must
    // NEVER be a false negative — across every link of the exact → whitespace-normalized
    // → collapsed → fuzzy fallback chain. (2) Arbitrary garbage input must NEVER throw.
    // (3) With duplicate anchors, the no-hint last-candidate fallback, the line-hint
    // distance pick, and the keyword-hint ±800-char window pick must all be
    // deterministic. The RNG is seeded, so every corpus is identical on every run.

    private static readonly string[] FuzzHtmlHeadings =
        { "AlphaPanel", "BetaPanel", "GammaPanel", "DeltaPanel" };

    private static readonly string[] FuzzHtmlClasses =
        { "ghost", "primary", "small", "large", "active", "disabled" };

    private static readonly string[] FuzzHtmlHandlers =
        { "removeItem(item)", "saveForm()", "togglePanel()", "onClick($event)" };

    private static readonly string[] FuzzHtmlExtraAttrs =
        { "id=\"main\"", "role=\"button\"", "aria-label=\"x\"", "data-id=\"7\"" };

    private static readonly string[] FuzzHtmlLabels =
        { "Save", "Delete", "Edit", "Open", "Close", "Run" };

    /// <summary>Single source of truth for the card template — every generator builds from here.</summary>
    private static string BuildCard(string heading, string cls, string handler, string label, string? extra = null)
    {
        return "<div class=\"card\">\n" +
               "  <h2>" + heading + "</h2>\n" +
               "  <button class=\"" + cls + "\"" + (extra ?? "") + " (click)=\"" + handler + "\">" + label + "</button>\n" +
               "</div>";
    }

    private static string FuzzCardHtml(Random rng)
    {
        var heading = FuzzHtmlHeadings[rng.Next(FuzzHtmlHeadings.Length)];
        var cls = FuzzHtmlClasses[rng.Next(FuzzHtmlClasses.Length)];
        var handler = FuzzHtmlHandlers[rng.Next(FuzzHtmlHandlers.Length)];
        var label = FuzzHtmlLabels[rng.Next(FuzzHtmlLabels.Length)];
        var extra = rng.Next(3) == 0 ? " " + FuzzHtmlExtraAttrs[rng.Next(FuzzHtmlExtraAttrs.Length)] : "";
        return BuildCard(heading, cls, handler, label, extra);
    }

    private const string FuzzGarbageCharset =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789<>\\/\"'\n\t [](){}!@#$%^&*+-=;:,.?";

    private static string FuzzRandomString(Random rng, int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(FuzzGarbageCharset[rng.Next(FuzzGarbageCharset.Length)]);
        return sb.ToString();
    }

    [Fact]
    public void Fuzz_ResolveHtmlAnchor_ExistingTarget_NeverFalseNegative()
    {
        const int docCount = 30;

        for (var i = 0; i < docCount; i++)
        {
            var rng = new Random(731 + i * 104729);
            var blocks = new List<string>();
            var count = 2 + rng.Next(4);
            for (var j = 0; j < count; j++) blocks.Add(FuzzCardHtml(rng));
            var html = string.Join("\n\n", blocks) + "\n";
            var block = blocks[rng.Next(blocks.Count)];

            // Heading of the chosen block — the resolved region must actually be THAT
            // card (strengthens the check from "no error" to "matched the right region").
            var heading = Regex.Match(block, @"<h2>(.*?)</h2>").Groups[1].Value;

            // EXACT link: the verbatim block is a substring of the file.
            var (b1, _, e1) = HtmlDomEditor.ResolveHtmlAnchor(html, block);
            Assert.Null(e1);
            Assert.Contains(heading, b1);

            // NORMALIZED/COLLAPSED link: whitespace injected into the opening tag
            // (exact + normalized both fail — the collapsed form must still match).
            var drifted = block.Replace("<div class=\"card\">", "<div\nclass=\"card\" >");
            var (b2, _, e2) = HtmlDomEditor.ResolveHtmlAnchor(html, drifted);
            Assert.Null(e2);
            Assert.Contains(heading, b2);

            // FUZZY link: hallucinated attribute VALUE (same keys, class value kept) —
            // tag + attribute-key + one-value matching must save it. The fuzzy match
            // resolves to the button ELEMENT, so assert it landed on a real button.
            var hallucinated = Regex.Replace(block, @"\(click\)=\""[^\""]*\""", "(click)=\"remove_me('hallucinated')\"");
            var (b3, _, e3) = HtmlDomEditor.ResolveHtmlAnchor(html, hallucinated);
            Assert.Null(e3);
            Assert.Contains("<button", b3);
        }
    }

    [Fact]
    public void Fuzz_ResolveHtmlAnchor_ArbitraryInput_NeverThrows()
    {
        const int docCount = 60;
        var traversedResolver = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = new Random(9191 + i * 65537);
            var content = FuzzRandomString(rng, rng.Next(0, 400));
            var target = FuzzRandomString(rng, rng.Next(0, 150));
            var stepChange = FuzzRandomString(rng, rng.Next(0, 60));

            // Docs with both content and target non-empty actually reach the resolver
            // logic (vs. the Empty-content / Empty-target early-return guards).
            if (!string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(target))
                traversedResolver++;

            var ex = Record.Exception(() => HtmlDomEditor.ResolveHtmlAnchor(
                content, target, stepChange,
                centerLine: rng.Next(0, 40),
                expandToClosingTags: rng.Next(2) == 0,
                expandToLineStart: rng.Next(2) == 0));

            Assert.Null(ex);
        }

        // Hard guard against a vacuous pass: the corpus must have actually exercised
        // the full scan path, not only the early-return guards.
        Assert.True(traversedResolver > 0,
            "never-throws corpus never reached the resolver (all docs empty/whitespace)");
    }

    [Fact]
    public void Fuzz_ResolveHtmlAnchor_DuplicateAnchors_HintsPickClosest()
    {
        const int docCount = 30;

        for (var i = 0; i < docCount; i++)
        {
            var rng = new Random(555 + i * 7919);

            // Random leading cards — every one carries a duplicate `<div class="card">` anchor.
            var leadCount = 1 + rng.Next(3);
            var headings = FuzzHtmlHeadings.OrderBy(_ => rng.Next()).ToList();
            var leads = new List<string>();
            var firstHeading = headings[0];
            for (var j = 0; j < leadCount; j++)
            {
                var cls = FuzzHtmlClasses[rng.Next(FuzzHtmlClasses.Length)];
                var handler = FuzzHtmlHandlers[rng.Next(FuzzHtmlHandlers.Length)];
                var label = FuzzHtmlLabels[rng.Next(FuzzHtmlLabels.Length)];
                leads.Add(BuildCard(headings[j], cls, handler, label));
            }
            var filler = new string(' ', 1600 + rng.Next(800));
            var targetBlock = BuildCard("FooterBeta", "ghost", "saveForm()", "Save");
            var html = string.Join("\n\n", leads) + "\n\n" + filler + "\n\n" + targetBlock + "\n";
            const string anchor = "<div class=\"card\">";

            var firstIdx = html.IndexOf(anchor, StringComparison.Ordinal);
            var lastIdx = html.LastIndexOf(anchor, StringComparison.Ordinal);
            var lineOfFirst = html[..firstIdx].Count(c => c == '\n') + 1;
            var lineOfLast = html[..lastIdx].Count(c => c == '\n') + 1;

            // No hints → candidates[^1] fallback = the LAST anchor (the target block).
            var (bNone, _, errNone) = HtmlDomEditor.ResolveHtmlAnchor(html, anchor);
            Assert.Null(errNone);
            Assert.Contains("FooterBeta", bNone);

            // Line hint at the FIRST anchor → distance overrides the last-fallback.
            var (bFirst, _, errFirst) = HtmlDomEditor.ResolveHtmlAnchor(html, anchor, centerLine: lineOfFirst);
            Assert.Null(errFirst);
            Assert.Contains(firstHeading, bFirst);
            Assert.DoesNotContain("FooterBeta", bFirst);

            // Line hint at the LAST anchor → picks the target block.
            var (bLast, _, errLast) = HtmlDomEditor.ResolveHtmlAnchor(html, anchor, centerLine: lineOfLast);
            Assert.Null(errLast);
            Assert.Contains("FooterBeta", bLast);

            // Keyword hint naming the target heading → only its ±800-char window contains
            // 'footerbeta', so the keyword pick must land on it.
            var (bKw, _, errKw) = HtmlDomEditor.ResolveHtmlAnchor(html, anchor, stepChange: "update the footerbeta card");
            Assert.Null(errKw);
            Assert.Contains("FooterBeta", bKw);
        }
    }
}
