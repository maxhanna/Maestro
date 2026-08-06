using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Weaver;
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

    [Fact]
    public void ResolveHtmlAnchor_CrossTagHallucination_TagGateRejects()
    {
        // The target hallucinates <section> — a tag that does NOT exist in the file —
        // while keeping attribute KEYS AND VALUES byte-identical to a real <div> element.
        // FindFuzzyElementCandidates only ever scans candidates sharing the target's FIRST
        // tag (tagRegex = `<{tag}\b`), so any attribute-based gate alone would ACCEPT this
        // target; only the fuzzy tag gate can reject it. It must fail resolution instead
        // of silently matching the real <div> with identical attributes.
        // Same card shape as the fuzz generators — only the first tag is hallucinated.
        var realBlock = BuildCard("Header", "ghost", "removeItem(item)", "X");
        var html = realBlock + "\n";
        var crossTagTarget = Regex.Replace(realBlock, @"^<div", "<section");
        crossTagTarget = Regex.Replace(crossTagTarget, @"</div>$", "</section>");

        // Positive control: the real <div> element still resolves fine.
        var (bReal, _, eReal) = HtmlDomEditor.ResolveHtmlAnchor(html, "<div class=\"card\">");
        Assert.Null(eReal);
        Assert.Contains("Header", bReal);

        var (b, _, e) = HtmlDomEditor.ResolveHtmlAnchor(html, crossTagTarget);
        Assert.NotNull(e);
        Assert.Null(b);
    }

    [Fact]
    public void ResolveHtmlAnchor_PartialHallucination_PicksBestScoreWinner()
    {
        // Two elements share the class value. The target drifts ONE value: it matches the
        // first element on class + id (score 2) but the second on class only (score 1).
        // FindFuzzyElementCandidates keeps ONLY candidates at the global best score
        // (`s >= best`), so the lower-scoring decoy must be excluded — the winner is the
        // element with the MOST matching values, never merely any candidate.
        // Same button shape as the fuzz tier — reuses the shared generator so the
        // deterministic and fuzz tiers can never drift from each other.
        var html = BuildButton("primary", "saveForm()", "btn-w", "Save") + "\n" +
                   BuildButton("primary", "togglePanel()", "btn-s", "Toggle");
        var target = BuildButton("primary", "hallucinated()", "btn-w", "Save");

        var (b, _, e) = HtmlDomEditor.ResolveHtmlAnchor(html, target);

        Assert.Null(e);
        Assert.Contains("btn-w", b);
        Assert.DoesNotContain("btn-s", b);
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

    /// <summary>Tags never emitted by the card generators — used for cross-tag hallucination targets.</summary>
    private static readonly string[] FuzzHtmlAbsentTags =
        { "section", "article", "aside", "header", "footer", "main", "nav", "figure" };

    /// <summary>Single source of truth for the card template — every generator builds from here.</summary>
    private static string BuildCard(string heading, string cls, string handler, string label, string? extra = null)
    {
        return "<div class=\"card\">\n" +
               "  <h2>" + heading + "</h2>\n" +
               "  <button class=\"" + cls + "\"" + (extra ?? "") + " (click)=\"" + handler + "\">" + label + "</button>\n" +
               "</div>";
    }

    /// <summary>Prefix every line of a block with <paramref name="indent"/> — used to nest
    /// cards inside a container so the realign path sees a non-empty base indent.</summary>
    private static string IndentBlock(string block, string indent)
    {
        return string.Join("\n",
            block.Replace("\r\n", "\n").Split('\n').Select(l => indent + l));
    }

    /// <summary>A single-line button element with three attributes on its FIRST tag — the
    /// multi-attribute shape that makes the fuzzy score-based winner distinguishable
    /// (class + (click) + id values can match/drift independently).</summary>
    private static string BuildButton(string cls, string handler, string id, string label)
    {
        return "<button class=\"" + cls + "\" (click)=\"" + handler + "\" id=\"" + id + "\">" + label + "</button>";
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
            var rng = FuzzHarness.SeededRng(731, i, 104729);
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

    /// <summary>
    /// The middle tier between never-false-negative and never-picks-wrong-element: targets
    /// where SOME attribute values match and others drift. The fuzzy matcher must resolve
    /// (at least one value matches → score >= 1) AND pick the element with the MOST
    /// matching values — the global best-score winner — never merely any candidate. This
    /// locks FindFuzzyElementCandidates' `s >= best` filter: a decoy sharing the winner's
    /// class (score 1) must lose to the winner (score 2). A regression that admits all
    /// score>=1 candidates would break the assertion pair — `Contains(wId)` fails on any
    /// wrong pick (S or an extra element), so the corpus cannot silently pass.
    /// </summary>
    [Fact]
    public void Fuzz_ResolveHtmlAnchor_PartialHallucination_PicksBestScoreWinner()
    {
        const int docCount = 30;
        var bestScoreWins = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(88_101, i, 104729);

            // Winner W + decoy S share the class value; S deliberately follows W in the
            // doc so a broken matcher's last-candidate fallback can never land on W.
            var wCls = FuzzHtmlClasses[rng.Next(FuzzHtmlClasses.Length)];
            var wHandlerIdx = rng.Next(FuzzHtmlHandlers.Length);
            var wHandler = FuzzHtmlHandlers[wHandlerIdx];
            var wLabel = FuzzHtmlLabels[rng.Next(FuzzHtmlLabels.Length)];
            var wId = "btn-" + i + "-w";
            var w = BuildButton(wCls, wHandler, wId, wLabel);

            // S shares W's class but has a DIFFERENT handler + unique id → it can score
            // at most 1 against W's 2.
            var sHandler = FuzzHtmlHandlers[(wHandlerIdx + 1) % FuzzHtmlHandlers.Length];
            var sLabel = FuzzHtmlLabels[rng.Next(FuzzHtmlLabels.Length)];
            var sId = "btn-" + i + "-s";
            var s = BuildButton(wCls, sHandler, sId, sLabel);

            // Extra elements: each has a UNIQUE id, and no extra may share BOTH class and
            // handler with W (that would tie W's score of 2). The re-pick guarantees at
            // least one of the two differs, so extras score 1 at most.
            var extras = new List<string>();
            var extraCount = rng.Next(3);
            for (var j = 0; j < extraCount; j++)
            {
                var eCls = FuzzHtmlClasses[rng.Next(FuzzHtmlClasses.Length)];
                var eHandler = FuzzHtmlHandlers[rng.Next(FuzzHtmlHandlers.Length)];
                if (eCls == wCls && eHandler == wHandler)
                    eHandler = FuzzHtmlHandlers[(wHandlerIdx + 1) % FuzzHtmlHandlers.Length];
                extras.Add(BuildButton(eCls, eHandler, "btn-" + i + "-x" + j,
                    FuzzHtmlLabels[rng.Next(FuzzHtmlLabels.Length)]));
            }

            var blocks = new List<string> { w, s };
            blocks.AddRange(extras);
            var html = string.Join("\n", blocks) + "\n";

            // Drift values are guaranteed absent from the doc — a genuine partial
            // hallucination (a value collision would silently change the winner).
            var fakeHandler = "hallucinated_" + i + "()";
            var fakeId = "hallucinated-id-" + i;
            var fakeCls = "hallucinated-cls-" + i;
            Assert.DoesNotContain(fakeHandler, html);
            Assert.DoesNotContain(fakeId, html);
            Assert.DoesNotContain(fakeCls, html);

            // Positive control: the exact winner block resolves — the doc is valid and the
            // matcher works, so the fuzzy picks below are attributable to scoring, not to a
            // broken exact-match path.
            var (bReal, _, eReal) = HtmlDomEditor.ResolveHtmlAnchor(html, w);
            Assert.Null(eReal);
            Assert.Contains(wId, bReal);

            // VARIANT A — drift the (click) value: W scores class+id = 2, S scores class
            // only = 1. The 1-match decoy must NOT win.
            var targetA = Regex.Replace(w, @"\(click\)=\""[^\""]*\""", "(click)=\"" + fakeHandler + "\"");
            var (bA, _, eA) = HtmlDomEditor.ResolveHtmlAnchor(html, targetA);
            Assert.Null(eA);
            Assert.Contains(wId, bA);
            Assert.DoesNotContain(sId, bA);
            bestScoreWins++;

            // VARIANT B — drift the id: W scores class+handler = 2, S scores class only = 1.
            var targetB = Regex.Replace(w, @"id=\""[^\""]*\""", "id=\"" + fakeId + "\"");
            var (bB, _, eB) = HtmlDomEditor.ResolveHtmlAnchor(html, targetB);
            Assert.Null(eB);
            Assert.Contains(wId, bB);
            Assert.DoesNotContain(sId, bB);
            bestScoreWins++;

            // VARIANT C — drift the class: W scores handler+id = 2, S scores 0 (no
            // competition, but exercises the class-drift axis and the `best >= 1` gate).
            var targetC = Regex.Replace(w, @"class=\""[^\""]*\""", "class=\"" + fakeCls + "\"");
            var (bC, _, eC) = HtmlDomEditor.ResolveHtmlAnchor(html, targetC);
            Assert.Null(eC);
            Assert.Contains(wId, bC);
            bestScoreWins++;
        }

        // Hard guard against a vacuous pass: every partial-hallucination target must have
        // resolved to its best-score winner (3 variants × 30 docs).
        FuzzHarness.AssertAllDocsChecked(bestScoreWins, docCount * 3,
            "partial-hallucination corpus (best-score winners)");
    }

    [Fact]
    public void Fuzz_ResolveHtmlAnchor_HallucinatedValues_NeverPicksWrongElement()
    {
        const int docCount = 30;
        var rejectedCases = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(31_337, i, 104729);

            // Per-doc hallucination values — guaranteed disjoint from the real pools
            // (FuzzHtmlClasses / FuzzHtmlHandlers / FuzzHtmlLabels / FuzzHtmlExtraAttrs),
            // so no real element in the doc can ever carry them (a single value match
            // would push the fuzzy score to >= 1 and let a wrong element win).
            var fakeCls = "hallucinated-cls-" + i;
            var fakeHandler = "hallucinated_" + i + "()";
            var fakeLabel = "Fake" + i;

            var blocks = new List<string>();
            var count = 2 + rng.Next(4);
            for (var j = 0; j < count; j++) blocks.Add(FuzzCardHtml(rng));
            var html = string.Join("\n\n", blocks) + "\n";
            var real = blocks[rng.Next(blocks.Count)];

            // Positive control: the REAL element in the same doc must still resolve —
            // proving rejection below is specific to hallucinated input, not a broken matcher.
            var (bReal, _, eReal) = HtmlDomEditor.ResolveHtmlAnchor(html, real);
            Assert.Null(eReal);
            Assert.Contains("<h2>", bReal);

            // VARIANT 1 — same keys, ALL values differ. The target's FIRST tag is the div
            // (the fuzzy matcher scores only the first tag), so the div class is hallucinated
            // too: exact / normalized / collapsed all fail, the fuzzy path runs, and its
            // `best >= 1` gate must reject every candidate (no value can match).
            var allValuesHallucinated = Regex.Replace(
                Regex.Replace(
                    Regex.Replace(real,
                        @"class=\""[^\""]*\""", "class=\"" + fakeCls + "\""),
                    @"\(click\)=\""[^\""]*\""", "(click)=\"" + fakeHandler + "\""),
                @"<h2>(.*?)</h2>", "<h2>" + fakeLabel + "</h2>");

            // Self-verifying disjointness: the hallucinated target (and every fake value)
            // must be absent from the doc — a future pool collision fails here with a clear
            // message instead of silently flipping the fuzzy score to >= 1.
            Assert.False(html.Contains(allValuesHallucinated, StringComparison.Ordinal));
            Assert.DoesNotContain(fakeCls, html);
            Assert.DoesNotContain(fakeHandler, html);
            Assert.DoesNotContain(fakeLabel, html);

            var (b1, _, e1) = HtmlDomEditor.ResolveHtmlAnchor(html, allValuesHallucinated);
            Assert.NotNull(e1);
            Assert.Null(b1);
            rejectedCases++;

            // VARIANT 2 — keys aren't a superset match: the target carries an extra
            // attribute key (data-fake-N) no real element has. The fuzzy matcher's
            // key-superset gate rejects every candidate BEFORE any value comparison.
            var extraKeyTarget = real.Replace(
                "<div class=\"card\">", "<div class=\"card\" data-fake-" + i + "=\"1\">");

            Assert.False(html.Contains(extraKeyTarget, StringComparison.Ordinal));

            var (b2, _, e2) = HtmlDomEditor.ResolveHtmlAnchor(html, extraKeyTarget);
            Assert.NotNull(e2);
            Assert.Null(b2);
            rejectedCases++;

            // VARIANT 3 — cross-tag hallucination: the FIRST tag is replaced with a tag
            // that does not exist anywhere in the doc, while attribute KEYS AND VALUES stay
            // byte-identical to a real element. FindFuzzyElementCandidates only scans
            // candidates sharing the target's first tag (`<{tag}\b`), so the tag gate must
            // reject the target BEFORE any attribute comparison — perfect attributes
            // cannot rescue a wrong tag name.
            var hallTag = FuzzHtmlAbsentTags[rng.Next(FuzzHtmlAbsentTags.Length)];
            var crossTagTarget = Regex.Replace(real, @"^<div", "<" + hallTag);
            crossTagTarget = Regex.Replace(crossTagTarget, @"</div>$", "</" + hallTag + ">");

            // Self-verifying absence: the hallucinated tag must NOT exist in the doc — if a
            // future generator ever emits it, the tag gate would legitimately accept and
            // this assertion fails loudly instead of silently flipping the test's meaning.
            Assert.DoesNotContain("<" + hallTag, html);
            Assert.False(html.Contains(crossTagTarget, StringComparison.Ordinal));

            var (b3, _, e3) = HtmlDomEditor.ResolveHtmlAnchor(html, crossTagTarget);
            Assert.NotNull(e3);
            Assert.Null(b3);
            rejectedCases++;
        }

        // Hard guard against a vacuous pass: every hallucination must have been rejected
        // (and each doc's real element resolved), so the corpus provably exercised all
        // three fuzzy rejection gates (all-values-differ, key-superset, cross-tag).
        FuzzHarness.AssertAllDocsChecked(rejectedCases, docCount * 3, "hallucinated-values corpus (rejections)");
    }

    [Fact]
    public void Fuzz_ResolveHtmlAnchor_ArbitraryInput_NeverThrows()
    {
        const int docCount = 60;
        var traversedResolver = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(9191, i, 65537);
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
        FuzzHarness.AssertExercised(traversedResolver,
            "never-throws corpus never reached the resolver (all docs empty/whitespace)");
    }

    [Fact]
    public void Fuzz_ResolveHtmlAnchor_DuplicateAnchors_HintsPickClosest()
    {
        const int docCount = 30;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(555, i, 7919);

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

    // ═══════════════════════════════════════════════════════════════════════════
    //  FUZZ — FORMAT D full application path (resolve → classify → compose → apply → re-resolve)
    // ═══════════════════════════════════════════════════════════════════════════
    // Mirrors the agent's FORMAT D HTML branch (AgentController ~1317 / ~1954):
    //   already-done guard → ResolveHtmlAnchor → strategy composition
    //   (replace / insertAfter / insertBefore) → TryReplaceSafe.
    // Asserts the applied file is byte-identical to the pure substitution, the inserted
    // block is byte-present AND re-resolves as its own anchor, and re-applying the same
    // step is a deterministic no-op — across dozens of seeded fragments cycling all three
    // strategies. The RNG is seeded, so every corpus is identical on every run.

    /// <summary>
    /// Deterministic mirror of <c>AgentController.FormatSnippetAsync</c>'s re-indent core
    /// (baseIndent from the anchor's first real line, min-indent strip + prefix). The
    /// prettier pass is deliberately NOT spawned — unit tests never run formatter binaries.
    /// </summary>
    private static string FormatSnippetRealign(string oldSource, string newCode)
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
    /// Mirrors the agent's FORMAT D HTML application path: already-done guard first,
    /// then anchor resolution and the strategy-keyed composition. Returns the composed
    /// (oldString, newString) and whether the guard short-circuited.
    /// </summary>
    private static (string oldStr, string newStr, bool alreadyDone) ComposeFormatD(
        string html, string targetName, string newCode, string changeDesc, EditStrategy strategy)
    {
        // The agent strips leading </div> lines from newCode BEFORE the already-done check
        // and composition — mirror that so the helper stays byte-faithful for drifted newCode.
        newCode = HtmlDomEditor.StripLeadingClosingDivs(newCode, targetName);

        // Already-done guard — mirrors sourceText.Contains(newCodeStr, OrdinalIgnoreCase).
        if (html.Contains(newCode, StringComparison.OrdinalIgnoreCase))
            return ("", "", true);

        var (matchedBlock, _, htmlErr) = HtmlDomEditor.ResolveHtmlAnchor(html, targetName, changeDesc);
        if (matchedBlock == null) throw new Xunit.Sdk.XunitException($"anchor did not resolve: {htmlErr}");

        // Strategy-keyed composition — byte-for-byte the agent's three FORMAT D branches.
        var indented = FormatSnippetRealign(matchedBlock, newCode);
        var newStr = strategy switch
        {
            EditStrategy.HtmlReplace => indented,
            EditStrategy.HtmlInsertAfter => matchedBlock + "\n" + indented,
            _ => newCode + "\n" + matchedBlock // HtmlInsertBefore — agent uses raw newCodeStr
        };
        return (matchedBlock, newStr, false);
    }

    [Fact]
    public void Fuzz_FormatD_FullApplicationPath_RoundTripsAcrossSeededFragments()
    {
        const int docCount = 45;
        var strategyHits = new BranchHitCounter<EditStrategy>(
            new[] { EditStrategy.HtmlInsertBefore, EditStrategy.HtmlInsertAfter, EditStrategy.HtmlReplace },
            "FORMAT D fuzz");

        for (var i = 0; i < docCount; i++)
        {
            // Distinct seed from the AST broken-snippets corpus (also 4242/104729): the
            // harness contract is a unique (seed, prime) per corpus so doc streams never
            // collide across suites.
            var rng = FuzzHarness.SeededRng(4243, i, 104729);

            // Unique headings per doc → the full card block is an unambiguous anchor.
            var cardCount = 2 + rng.Next(4);
            var cards = new List<string>();
            for (var j = 0; j < cardCount; j++)
            {
                var heading = "Card" + i + "_" + j;
                var cls = FuzzHtmlClasses[rng.Next(FuzzHtmlClasses.Length)];
                var handler = FuzzHtmlHandlers[rng.Next(FuzzHtmlHandlers.Length)];
                var label = FuzzHtmlLabels[rng.Next(FuzzHtmlLabels.Length)];
                cards.Add(BuildCard(heading, cls, handler, label));
            }
            var html = string.Join("\n\n", cards) + "\n";
            var targetIdx = rng.Next(cards.Count);
            var targetName = cards[targetIdx];

            // Cycle the three FORMAT D strategies — the change description must classify to it.
            var mode = i % 3;
            var (changeDesc, expectedStrategy) = mode switch
            {
                0 => ("Insert a new section before the footer card", EditStrategy.HtmlInsertBefore),
                1 => ("Add a new section after the header card", EditStrategy.HtmlInsertAfter),
                _ => ("Update the footer card content", EditStrategy.HtmlReplace),
            };

            // 1. Strategy pick — the deterministic classifier must land on the expected one.
            var step = new PlanStep { File = "score.component.html", Change = changeDesc };
            var strategy = EditClassifier.Classify(step, fileExists: true, ".html");
            Assert.Equal(expectedStrategy, strategy);
            strategyHits.Hit(strategy);

            // 2–4. Compose + apply, mirroring the agent's FORMAT D branch.
            var newCode = BuildCard("Inserted" + i, "primary", "togglePanel()", "Run");
            var (oldStr, newStr, alreadyDone) = ComposeFormatD(html, targetName, newCode, changeDesc, strategy);
            Assert.False(alreadyDone, $"doc #{i}: new block unexpectedly already present");
            Assert.Equal(targetName, oldStr); // exact full-block anchor, no fuzzy drift

            var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(html, oldStr, newStr);
            Assert.True(replaced, $"doc #{i}: TryReplaceSafe failed: {matchError}");
            Assert.Equal(html.Replace(oldStr, newStr), applied); // byte-exact pure substitution

            // 5. The inserted block is byte-present AND re-resolves as its own anchor.
            Assert.Contains(newCode, applied);
            var (reResolved, _, reErr) = HtmlDomEditor.ResolveHtmlAnchor(applied, newCode);
            Assert.Null(reErr);
            Assert.Contains("Inserted" + i, reResolved);

            if (strategy == EditStrategy.HtmlReplace)
            {
                // Replacement consumed the old block — its unique heading is gone.
                Assert.DoesNotContain("Card" + i + "_" + targetIdx, applied);
            }
            else
            {
                // Insert modes keep the original anchor resolvable with its heading intact.
                var (anchorAgain, _, aErr) = HtmlDomEditor.ResolveHtmlAnchor(applied, targetName);
                Assert.Null(aErr);
                Assert.Contains("Card" + i + "_" + targetIdx, anchorAgain);
            }

            // 6. Round-trip: re-applying the identical step is a deterministic no-op
            //    (the already-done guard fires because newCode is now present verbatim).
            var (_, _, alreadyDone2) = ComposeFormatD(applied, targetName, newCode, changeDesc, strategy);
            Assert.True(alreadyDone2, $"doc #{i}: already-done guard failed on round-trip");
        }

        // Every strategy was exercised — the corpus can't silently skip a branch.
        strategyHits.AssertAllExercised(docCount, 3);
    }

    [Fact]
    public void Fuzz_FormatD_NestedAnchor_ReindentsInsertedBlockToBaseIndent()
    {
        const int docCount = 20;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(777, i, 65537);
            var heading = "Nested" + i;
            var targetName = BuildCard(heading, "ghost", "saveForm()", "Save");

            // Nest the anchor inside a 2-space-indented wrapper → baseIndent = "  ".
            var indentedAnchor = string.Join("\n", targetName.Split('\n').Select(l => "  " + l));
            var html = "<div class=\"wrap\">\n" + indentedAnchor + "\n</div>\n";

            var newCode = BuildCard("Deep" + i, "primary", "togglePanel()", "Run");
            var mode = i % 2;
            var changeDesc = mode == 0 ? "Add a section after the nested card" : "Update the nested card content";
            var expectedStrategy = mode == 0 ? EditStrategy.HtmlInsertAfter : EditStrategy.HtmlReplace;
            var step = new PlanStep { File = "score.component.html", Change = changeDesc };
            Assert.Equal(expectedStrategy, EditClassifier.Classify(step, fileExists: true, ".html"));

            var (oldStr, newStr, _) = ComposeFormatD(html, indentedAnchor, newCode, changeDesc, expectedStrategy);
            Assert.Equal(indentedAnchor, oldStr);

            var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(html, oldStr, newStr);
            Assert.True(replaced, $"doc #{i}: TryReplaceSafe failed: {matchError}");
            Assert.Equal(html.Replace(oldStr, newStr), applied); // byte-exact pure substitution

            // The inserted block is re-indented to the anchor's 2-space base indent.
            var expectedReindented = string.Join("\n", newCode.Split('\n').Select(l => "  " + l));
            Assert.Contains(expectedReindented, applied);

            // NOTE: no already-done round-trip assertion here, deliberately. After a
            // re-indented insert the file holds the RE-INDENTED block, not the raw newCode,
            // so the agent's raw-newCode guard does NOT fire on re-apply (it would re-insert
            // — the real agent behaves identically). The round-trip no-op is asserted only
            // in the top-level test where baseIndent is empty and indented == newCode.

            // ...and re-resolves as its own anchor, byte-present.
            var (reResolved, _, err) = HtmlDomEditor.ResolveHtmlAnchor(applied, expectedReindented);
            Assert.Null(err);
            Assert.Contains("Deep" + i, reResolved);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CORPUS FULL-CHAIN — HTML (classify → intent → decide → resolve → compose → apply)
    // ═══════════════════════════════════════════════════════════════════════════
    // The HTML analog of the CSS corpus full-chain in LlmCssCleanerPipelineTests: run
    // random valid HTML through the ENTIRE deterministic edit chain the agent executes
    // for a FORMAT D step — EditClassifier.Classify → ClassifyIntent →
    // EditStrategyResolver.Decide → HtmlDomEditor.ResolveHtmlAnchor → FORMAT D
    // composition → AgentEditHeuristics.TryReplaceSafe. HTML has NO post-edit cleaning
    // (LlmCssCleaner.Clean/FixCssStructure are gated on .css/.scss/.less, AgentController
    // ~4995), so the final file must equal the PURE SUBSTITUTION byte-for-byte — a
    // regression anywhere in the chain fails the build, not just a cleaner one.

    private static PlanStep HtmlStep(string file, string change) => new()
    {
        File = file,
        Change = change
    };

    /// <summary>
    /// Mirror the agent's complete deterministic edit chain for an HTML FORMAT D step.
    /// Change descriptions are crafted so Classify and Decide AGREE on the strategy
    /// (TargetedEdit→HtmlInsertBefore, InsertNearSymbol→HtmlInsertAfter, ReplaceSymbol→
    /// HtmlReplace). Returns the final content plus the independently-computed pure
    /// substitution so the caller can assert the headline guarantee.
    /// </summary>
    private static (EditStrategy strategy, EditPlanDecision decision, string finalContent, string expected) RunFullHtmlEditChain(
        string original, string targetName, string newCode, string changeDesc)
    {
        var step = HtmlStep("score.component.html", changeDesc);

        // 1–2. Classification — the strategy classifier AND the intent-driven resolver
        //      must land on the same HTML DOM strategy for the crafted change description.
        var strategy = EditClassifier.Classify(step, fileExists: true, ".html");
        var intent = EditClassifier.ClassifyIntent(step, ".html");
        var decision = EditStrategyResolver.Decide("score.component.html", true, original, changeDesc, intent);
        Assert.Equal(strategy, decision.Strategy);

        // 3. Resolve anchor + compose the FORMAT D payload (already-done guard, realign).
        var (oldStr, newStr, alreadyDone) = ComposeFormatD(original, targetName, newCode, changeDesc, strategy);
        Assert.False(alreadyDone, "new block unexpectedly already present");
        Assert.Equal(targetName, oldStr); // exact full-block anchor, no fuzzy drift

        // 4. Apply — the applier must produce the pure substitution (no fuzzy/dedupe drift).
        var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(original, oldStr, newStr);
        Assert.True(replaced, $"TryReplaceSafe failed on corpus doc: {matchError}");
        var expected = original.Replace(oldStr, newStr);
        Assert.Equal(expected, applied);

        // 5. Post steps — NONE for HTML: LlmCssCleaner.Clean/FixCssStructure are gated
        //    on .css/.scss/.less (AgentController ~4995). Assert the cleaner is a
        //    byte-identical no-op on the final HTML — this locks the
        //    "LlmCssCleaner-irrelevant post steps" claim itself, not just its absence
        //    (a future change that wrongly wired the cleaner into the HTML path would
        //    break these, even though the pure-substitution check above could not).
        Assert.Equal(applied, LlmCssCleaner.Clean(applied));
        Assert.Equal(applied, LlmCssCleaner.FixCssStructure(applied));
        return (strategy, decision, applied, expected);
    }

    /// <summary>
    /// Mirror the agent's complete deterministic DELETION chain for an HTML step.
    /// Change descriptions are crafted so Classify AND Decide agree on DeleteLines —
    /// HTML removals route to oldString → empty newString, the ONLY executable path
    /// (the FORMAT D compose rejects empty newCode at AgentController ~1961). The
    /// anchor is resolved via HtmlDomEditor, then removed with the pure deletion
    /// substitution.
    /// </summary>
    private static (EditStrategy strategy, EditPlanDecision decision, string finalContent, string expected) RunFullHtmlDeleteChain(
        string original, string targetName, string changeDesc)
    {
        var step = HtmlStep("score.component.html", changeDesc);

        // 1–2. Classification — all three stages must land on the deletion route
        //      (this is the reviewer-flagged HTML 'remove' gap, previously misrouted
        //      to HtmlInsertBefore by ClassifyHtml and by Decide's HTML branch).
        //      The specific strategy is pinned by the caller with doc context.
        var strategy = EditClassifier.Classify(step, fileExists: true, ".html");
        var intent = EditClassifier.ClassifyIntent(step, ".html");
        var decision = EditStrategyResolver.Decide("score.component.html", true, original, changeDesc, intent);
        Assert.Equal(EditIntentKind.DeleteContent, intent.Kind);
        Assert.Equal(strategy, decision.Strategy);

        // 3. Resolve the exact block via HtmlDomEditor — deletion never uses the
        //    FORMAT D compose (which rejects empty newCode); it removes the resolved
        //    block directly with an empty newString.
        var (matchedBlock, _, htmlErr) = HtmlDomEditor.ResolveHtmlAnchor(original, targetName, changeDesc);
        if (matchedBlock == null) throw new Xunit.Sdk.XunitException($"anchor did not resolve: {htmlErr}");
        Assert.Equal(targetName, matchedBlock); // exact full-block anchor, no fuzzy drift

        // 4. Apply the deletion — pure substitution with empty newString.
        var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(original, matchedBlock, "");
        Assert.True(replaced, $"TryReplaceSafe failed on deletion doc: {matchError}");
        var expected = original.Replace(matchedBlock, "");
        Assert.Equal(expected, applied);

        // 5. Post steps — NONE for HTML: LlmCssCleaner is a byte-identical no-op.
        Assert.Equal(applied, LlmCssCleaner.Clean(applied));
        Assert.Equal(applied, LlmCssCleaner.FixCssStructure(applied));
        return (strategy, decision, applied, expected);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DELETE CORPUS — duplicate-similar blocks through the HTML resolver
    // ═══════════════════════════════════════════════════════════════════════════
    // The HTML analog of DeleteCorpusTests (the DeleteLines anti-over-match corpus):
    // seeded docs where every card shares the same tag + class (<div class="card">),
    // with a byte-identical dup pair in most variants. The anchor is resolved through
    // the REAL HtmlDomEditor.ResolveHtmlAnchor (keyword / line / last-candidate /
    // short-anchor keyword-window disambiguation among duplicate anchors), then deleted
    // via the agent's exact call
    // (AgentController:4529 — TryReplaceSafe with step.LineNumber + step.Change, which
    // carry the duplicate disambiguation). Every success asserts the byte-length delta
    // equals exactly the target block's length, the <div class="card"> count drops by
    // exactly 1, and every sibling card survives byte-identical — never an over-match
    // that eats a same-tag/class sibling. The no-context variant must REFUSE and leave
    // the file byte-identical.

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
    /// The HTML deletion chain mirroring the agent's DOM delete path: Classify →
    /// ClassifyIntent → Decide must all route to DeleteLines/DeleteContent, the anchor
    /// resolves through the REAL HtmlDomEditor.ResolveHtmlAnchor (with the change's
    /// keyword disambiguation among duplicate anchors), then TryReplaceSafe is called
    /// with the step's line + change — exactly AgentController:4529 — so duplicate
    /// occurrences are disambiguated (or refused) by the same rules the agent runs.
    /// </summary>
    private static (bool replaced, string finalContent, string? error, string matchedBlock) RunHtmlDeleteChain(
        string html, string targetName, string changeDesc, int targetLine = 0)
    {
        var step = HtmlStep("score.component.html", changeDesc);
        Assert.Equal(EditStrategy.DeleteLines, EditClassifier.Classify(step, fileExists: true, ".html"));
        var intent = EditClassifier.ClassifyIntent(step, ".html");
        Assert.Equal(EditIntentKind.DeleteContent, intent.Kind);
        var decision = EditStrategyResolver.Decide("score.component.html", true, html, changeDesc, intent);
        Assert.Equal(EditStrategy.DeleteLines, decision.Strategy);

        var (matchedBlock, _, htmlErr) = HtmlDomEditor.ResolveHtmlAnchor(html, targetName, changeDesc);
        if (matchedBlock == null) throw new Xunit.Sdk.XunitException($"anchor did not resolve: {htmlErr}");
        var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(html, matchedBlock, "", targetLine, changeDesc);
        return (replaced, applied, matchError, matchedBlock);
    }

    /// <summary>
    /// One seeded corpus pass of the full HTML edit chain: generates <paramref name="docCount"/>
    /// random card documents (optionally nested inside a container so the realign path sees a
    /// non-empty base indent), runs the complete deterministic edit chain, and asserts the final
    /// file equals the pure substitution byte-for-byte. RNG consumption is IDENTICAL across all
    /// passes — only the base seed, heading prefix, and card indent differ — so doc streams stay
    /// deterministic and never collide between passes. Every doc cycles insertBefore → insertAfter
    /// → replace so the shared <paramref name="strategyHits"/> counter sees all three branches.
    /// </summary>
    private static void RunHtmlCorpusPass(
        int seedBase, string headingPrefix, string indent, int docCount,
        BranchHitCounter<EditStrategy> strategyHits)
    {
        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(seedBase, i, 104729);

            // Unique headings per doc → the full card block is an unambiguous anchor.
            var cardCount = 2 + rng.Next(4);
            var cards = new List<string>();
            for (var j = 0; j < cardCount; j++)
            {
                var heading = headingPrefix + i + "_" + j;
                var card = BuildCard(heading,
                    FuzzHtmlClasses[rng.Next(FuzzHtmlClasses.Length)],
                    FuzzHtmlHandlers[rng.Next(FuzzHtmlHandlers.Length)],
                    FuzzHtmlLabels[rng.Next(FuzzHtmlLabels.Length)]);
                cards.Add(indent.Length == 0 ? card : IndentBlock(card, indent));
            }
            var html = WrapCards(string.Join("\n\n", cards), indent);
            var targetIdx = rng.Next(cards.Count);
            var targetName = cards[targetIdx];

            // New block stays FLAT (unindented) exactly as the LLM emits it — the realign
            // step is what must give it the container's base indent when the anchor is nested.
            var newCode = BuildCard(headingPrefix + "Inserted" + i, "primary", "togglePanel()", "Run");

            // Change descriptions crafted so Classify AND Decide agree on the same branch:
            // TargetedEdit → HtmlInsertBefore, InsertNearSymbol → HtmlInsertAfter,
            // ReplaceSymbol → HtmlReplace.
            var mode = i % 3;
            var (changeDesc, expectedStrategy) = mode switch
            {
                0 => ("Add a small footer section", EditStrategy.HtmlInsertBefore),
                1 => ("Add a new API endpoint after the header card", EditStrategy.HtmlInsertAfter),
                _ => ("Update the entire method body", EditStrategy.HtmlReplace),
            };

            var (strategy, decision, finalContent, expected) =
                RunFullHtmlEditChain(html, targetName, newCode, changeDesc);
            strategyHits.Hit(strategy);

            // The strategy contract: Classify and Decide both picked the expected branch.
            Assert.Equal(expectedStrategy, strategy);
            Assert.Equal(expectedStrategy, decision.Strategy);

            // THE core guarantee: the final file equals the PURE SUBSTITUTION byte-for-byte
            // (no post-edit cleaning for HTML) — a regression anywhere in the chain fails here.
            var chainLabel = indent.Length == 0 ? "full" : headingPrefix.ToLowerInvariant();
            FuzzHarness.AssertByteIdenticalNoOp(expected, finalContent, $"{chainLabel} HTML chain ({strategy})", i, "final");

            if (indent.Length == 0)
            {
                // Flat pass: baseIndent is empty so realign is a byte no-op — the inserted
                // block is present exactly once in its raw form.
                Assert.Single(Regex.Matches(finalContent, Regex.Escape(newCode)));
            }
            else
            {
                // The realign path MUST have engaged inside the chain: baseIndent derived from
                // the nested anchor's first real line, so the flat newCode was re-indented
                // before insertion. For insert/replace the RAW flat newCode must NOT be present
                // — only its indented form (a test that only checks the helper would never
                // catch its absence).
                var indented = FormatSnippetRealign(targetName, newCode);
                Assert.NotEqual(newCode, indented); // non-empty base indent → realign actually fired
                Assert.StartsWith(indent + "<div class=\"card\">", indented);
                if (strategy == EditStrategy.HtmlInsertBefore)
                {
                    // Agent mirror: HtmlInsertBefore uses the RAW newCode (no realign) — locked here.
                    Assert.Single(Regex.Matches(finalContent, Regex.Escape(newCode)));
                }
                else
                {
                    Assert.Empty(Regex.Matches(finalContent, Regex.Escape(newCode)));
                    Assert.Single(Regex.Matches(finalContent, Regex.Escape(indented)));
                }
            }
        }
    }

    /// <summary>
    /// Wrap a joined card block in container(s) so cards sit at the given base indent:
    /// "" → no container (top-level cards), "  "/"\t" → one &lt;div class="panel"&gt;,
    /// "    " → panel at 0 plus a 2-space-indented section (double nesting). The container
    /// markup is derived from the indent so every corpus pass shares one path — the produced
    /// markup is byte-identical to the original hand-written wrappers.
    /// </summary>
    private static string WrapCards(string cardsJoined, string indent)
    {
        if (indent.Length == 0) return cardsJoined + "\n";

        // Each container adds two spaces of card indentation; a single tab counts as one level.
        var depth = indent == "\t" ? 1 : indent.Length / 2;
        var open = new StringBuilder();
        var close = new StringBuilder();
        for (var d = 0; d < depth; d++)
        {
            var pad = new string(' ', d * 2);
            open.Append(pad).Append("<div class=\"").Append(d == 0 ? "panel" : "section").Append("\">\n");
            close.Insert(0, "\n" + pad + "</div>");
        }
        return open.ToString() + cardsJoined + close.ToString() + "\n";
    }

    [Fact]
    public void Fuzz_CompleteHtmlEditPath_RandomHtml_EqualsPureSubstitution()
    {
        const int docCount = 30;
        var strategyHits = new BranchHitCounter<EditStrategy>(
            new[] { EditStrategy.HtmlInsertBefore, EditStrategy.HtmlInsertAfter, EditStrategy.HtmlReplace },
            "HTML corpus full-chain");

        // ── Flat pass: top-level cards, no container. The anchor carries no leading
        //    indentation, so realign is a byte no-op inside the chain — the inserted block
        //    must appear exactly once in its raw form and the pure substitution must hold.
        RunHtmlCorpusPass(60_607, "Card", "", docCount, strategyHits);

        // ── Nested variant: the same corpus with every card inside a 2-space-indented
        //    container. This proves the REALIGN path (FormatSnippetRealign with a non-empty
        //    baseIndent derived from the anchor's first real line) is exercised inside the
        //    full chain — not just the standalone helper — and that the pure substitution
        //    still holds byte-for-byte when the anchor carries leading indentation.
        RunHtmlCorpusPass(60_608, "Nested", "  ", docCount, strategyHits);

        // ── Tab variant: the same realign guarantee with a TAB-indented container
        //    (baseIndent = "\t"). Base indentation can be tabs just as well as spaces —
        //    FormatSnippetRealign derives it from the anchor's first real line via
        //    ^(\s*), so a tab-lead anchor must re-indent the flat newCode with tabs.
        //    This proves the pure substitution + realign-engaged checks hold for tabs,
        //    not just spaces, and that tab indentation can't confuse the anchor match.
        RunHtmlCorpusPass(60_611, "Tab", "\t", docCount, strategyHits);

        // ── Deep-nesting variant: cards wrapped in TWO containers (<div class="panel">
        //    at 0, <div class="section"> at 2-space) so every card sits at a 4-space
        //    base indent — the deepest anchor in the corpus. ResolveHtmlAnchor must
        //    still return the exact 4-space block (expandToLineStart/expandToClosingTags
        //    walk the card regardless of its leading indentation), and the realign path
        //    must derive baseIndent = "    " from the anchor's first real line.
        RunHtmlCorpusPass(60_612, "Deep", "    ", docCount, strategyHits);

        // ── Deletion pass: a removal change description must route through DeleteLines
        //    (oldString → empty newString — the ONLY executable HTML delete path, since
        //    the FORMAT D compose rejects empty newCode), resolve the anchor via
        //    HtmlDomEditor, and produce the PURE deletion substitution with the old
        //    block byte-absent. Every 3rd doc wraps the cards in a 2-space-indented
        //    container so indented-anchor removal is exercised too.
        const int deleteCount = 30;
        var deleteHits = new BranchHitCounter<EditStrategy>(
            new[] { EditStrategy.DeleteLines },
            "HTML corpus full-chain (deletion)");
        var removalVerbs = new[] { "Remove", "Delete", "Strip" };
        for (var i = 0; i < deleteCount; i++)
        {
            // Distinct base seed from the insert/replace passes (60_607/60_608/60_611/60_612).
            var rng = FuzzHarness.SeededRng(60_609, i, 104729);

            var cardCount = 2 + rng.Next(4);
            var cards = new List<string>();
            for (var j = 0; j < cardCount; j++)
            {
                var heading = "Del" + i + "_" + j;
                var card = BuildCard(heading,
                    FuzzHtmlClasses[rng.Next(FuzzHtmlClasses.Length)],
                    FuzzHtmlHandlers[rng.Next(FuzzHtmlHandlers.Length)],
                    FuzzHtmlLabels[rng.Next(FuzzHtmlLabels.Length)]);
                cards.Add(i % 3 == 0 ? IndentBlock(card, "  ") : card);
            }
            var html = i % 3 == 0
                ? "<div class=\"panel\">\n" + string.Join("\n\n", cards) + "\n</div>\n"
                : string.Join("\n\n", cards) + "\n";
            var targetIdx = rng.Next(cards.Count);
            var targetName = cards[targetIdx];

            // Removal phrasing — IsDeletion fires on every verb (Remove/Delete/Strip).
            var changeDesc = $"{removalVerbs[i % removalVerbs.Length]} the Del{i}_{targetIdx} card";

            var (strategy, decision, finalContent, expected) =
                RunFullHtmlDeleteChain(html, targetName, changeDesc);
            deleteHits.Hit(strategy);

            // The deletion route contract: Classify AND Decide agree on DeleteLines.
            Assert.Equal(EditStrategy.DeleteLines, strategy);
            Assert.Equal(EditStrategy.DeleteLines, decision.Strategy);

            // THE core guarantee: the final file equals the PURE DELETION SUBSTITUTION
            // (the block removed, nothing else touched)…
            FuzzHarness.AssertByteIdenticalNoOp(expected, finalContent,
                $"full HTML delete chain ({strategy})", i, "final");
            // …and the old block is byte-absent.
            Assert.DoesNotContain(targetName, finalContent, StringComparison.Ordinal);
            // Every sibling survives byte-identical.
            for (var k = 0; k < cards.Count; k++)
            {
                if (k == targetIdx) continue;
                Assert.Single(Regex.Matches(finalContent, Regex.Escape(cards[k])));
            }
        }

        // Every strategy was exercised across ALL passes — the corpus can't silently
        // skip a branch (4 passes × docCount docs each).
        strategyHits.AssertAllExercised(docCount * 4, 3);
        deleteHits.AssertAllExercised(deleteCount, 1);
    }

    /// <summary>
    /// 30 seeded docs where every card shares the same tag + class (&lt;div class="card"&gt;),
    /// cycling five variants: unique target (removed, siblings intact), byte-identical dup
    /// pair + keyword marker (marked occurrence removed, sibling survives), dup pair +
    /// target line (nearest occurrence removed, sibling survives), dup pair + no context
    /// (must REFUSE and leave the file byte-identical), and the SHORT ambiguous anchor
    /// &lt;div class="card"&gt; + a change keyword naming the target card's heading (the keyword
    /// window picks the intended card among ALL same-tag/class siblings — never the
    /// no-hint last-candidate fallback). The anchor is resolved through the
    /// REAL ResolveHtmlAnchor (duplicate-anchor disambiguation) and deleted via
    /// TryReplaceSafe with the step's line + change. Every success asserts the byte-length
    /// delta equals exactly the target block's length, the &lt;div class="card"&gt; count drops
    /// by exactly 1 (never a same-tag/class sibling eaten), and all siblings survive
    /// byte-identical.
    /// </summary>
    [Fact]
    public void Fuzz_DeleteHtml_DuplicateSimilarBlocks_RemovesOnlyExactElement()
    {
        const int docCount = 30;
        const int seed = 60_614;
        const int prime = 104729;
        var checkedCount = 0;
        var uniqueRemovals = 0;
        var keywordRemovals = 0;
        var lineRemovals = 0;
        var refusals = 0;
        var shortAnchorRemovals = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(seed, i, prime);
            var docIdx = 400 + i;
            var variant = i % 5;
            // Rotate per 5-doc cycle (not per index) so every variant samples ALL five
            // marker words across the corpus — RNG-independent, so variants 0-3 docs stay
            // byte-identical regardless of the chosen marker.
            var marker = FuzzHarness.DeleteMarkers[(i / 5) % FuzzHarness.DeleteMarkers.Length];

            // Shared 5-variant doc builder — byte-identical to the FORMAT D payload route's
            // corpus (FormatDPayloadCorpusTests) so the two routes stay in lockstep.
            var (html, parts, expectedTarget, targetName, targetLine, dup) =
                FuzzHarness.BuildDeleteCorpusDoc(rng, i, docIdx, variant, marker);
            var change = variant switch
            {
                // Variants 0/2/3 need no keyword (unique single match / line hint / refusal)
                // — their phrases are stopword-only so ExtractDisambiguationKeywords yields
                // nothing and the disambiguation is driven by the variant's path. Variants 1
                // and 4 carry the marker keyword — for 4 it names the target card's heading.
                0 => "remove the block",
                1 => $"remove the {marker} block",
                2 => "remove the block",
                3 => "remove the block",
                4 => $"remove the {marker} block",
                // Unreachable (variant = i % 5 ∈ [0,4]) — kept only for int exhaustiveness.
                _ => $"remove the {marker} block",
            };

            var (replaced, applied, error, matchedBlock) = RunHtmlDeleteChain(html, targetName, change, targetLine);

            // The anchor resolved to the intended block bytes (full card) in every variant:
            // variants 0-3 anchor on the full block verbatim, variant 4 anchors on the SHORT
            // <div class="card"> tag and expands to the intended card via its keyword window.
            if (variant == 4)
                Assert.Equal(expectedTarget, matchedBlock);
            else
                Assert.Equal(targetName, matchedBlock);

            // Anti-over-match invariants per variant:
            switch (variant)
            {
                case 0:
                    Assert.True(replaced, $"doc #{i} unique target must delete: {error}");
                    Assert.Equal(html.Length - targetName.Length, applied.Length);
                    Assert.DoesNotContain(targetName, applied, StringComparison.Ordinal);
                    // Every sibling card survives byte-identical.
                    foreach (var part in parts.Skip(1))
                        Assert.Equal(1, CountOccurrences(applied, part));
                    uniqueRemovals++;
                    break;
                case 1:
                    Assert.True(replaced, $"doc #{i} keyword target must delete: {error}");
                    Assert.Equal(html.Length - dup!.Length, applied.Length);
                    Assert.Contains($"<!-- {marker} -->", applied, StringComparison.Ordinal);
                    Assert.Equal(1, CountOccurrences(applied, dup)); // one dup survives
                    // The MARKED (first) duplicate was removed — the survivor is the second,
                    // shifted left by exactly dup.Length.
                    Assert.Equal(FuzzHarness.NthIndexOf(html, dup, 2) - dup.Length, applied.IndexOf(dup, StringComparison.Ordinal));
                    keywordRemovals++;
                    break;
                case 2:
                    Assert.True(replaced, $"doc #{i} line target must delete: {error}");
                    Assert.Equal(html.Length - dup!.Length, applied.Length);
                    Assert.Equal(1, CountOccurrences(applied, dup));
                    // The NEAREST (second) duplicate was removed — the survivor is the first,
                    // at its original position.
                    Assert.Equal(FuzzHarness.NthIndexOf(html, dup, 1), applied.IndexOf(dup, StringComparison.Ordinal));
                    lineRemovals++;
                    break;
                case 4:
                    Assert.True(replaced, $"doc #{i} short-anchor keyword target must delete: {error}");
                    Assert.Equal(html.Length - expectedTarget!.Length, applied.Length);
                    Assert.DoesNotContain(expectedTarget, applied, StringComparison.Ordinal);
                    // The keyword window picked the marker-heading card among ALL
                    // same-tag/class siblings — the block the short anchor expanded to IS it.
                    Assert.Contains(marker, matchedBlock, StringComparison.OrdinalIgnoreCase);
                    // The marker word appears NOWHERE outside the target card — the keyword
                    // was a genuine disambiguator, not a coincidental sibling substring.
                    Assert.DoesNotContain(marker, html.Replace(expectedTarget!, ""), StringComparison.OrdinalIgnoreCase);
                    // Non-vacuity: without the keyword the resolver's no-hint fallback picks
                    // candidates[^1] = the LAST sibling, NOT the target — proving the keyword
                    // window (not document order) selected the right card.
                    var (noHintBlock, _, noHintErr) = HtmlDomEditor.ResolveHtmlAnchor(html, "<div class=\"card\">");
                    Assert.Null(noHintErr);
                    Assert.NotEqual(expectedTarget, noHintBlock);
                    // Every sibling card survives byte-identical.
                    foreach (var part in parts.Skip(1).Where(p => p.Contains("<h2>")))
                        Assert.Equal(1, CountOccurrences(applied, part));
                    shortAnchorRemovals++;
                    break;
                case 3:
                    // No context at all → must refuse and leave the file byte-identical.
                    Assert.False(replaced, $"doc #{i} duplicate with no context must refuse");
                    Assert.Equal(html, applied);
                    Assert.NotNull(error);
                    Assert.Contains("times in file", error);
                    refusals++;
                    break;
            }

            // Every success case: exactly one card removed — never a sibling over-match.
            if (replaced)
                Assert.Equal(CountOccurrences(html, "<div class=\"card\">") - 1,
                             CountOccurrences(applied, "<div class=\"card\">"));
            checkedCount++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedCount, docCount, "HTML delete duplicate-similar corpus");
        FuzzHarness.AssertExercised(uniqueRemovals, "no doc exercised the unique-target HTML deletion path");
        FuzzHarness.AssertExercised(keywordRemovals, "no doc exercised the keyword-disambiguated HTML deletion path");
        FuzzHarness.AssertExercised(lineRemovals, "no doc exercised the target-line-disambiguated HTML deletion path");
        FuzzHarness.AssertExercised(refusals, "no doc exercised the HTML duplicate-refusal path");
        FuzzHarness.AssertExercised(shortAnchorRemovals, "no doc exercised the short-anchor keyword HTML deletion path");
    }
}
