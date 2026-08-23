using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for <c>CssSelectorRepair.RepairBareClassSelectors</c> — the deterministic repair for
/// the missing-dot CSS bug the LLM verifier can't catch: an edit adds a rule whose selector
/// names a class WITHOUT the '.' prefix (e.g. 'favoritesTable tbody tr td a {' when the file
/// defines '.favouritesTable'), so the rule silently never applies. The repair scans the
/// actual file content and rewrites bare, class-like selector tokens to '.&lt;defined
/// class&gt;' when the class exists in the same file (exact / case-insensitive / small
/// edit-distance match). Regression case from a real run: the favourites table selector.
/// </summary>
public class CssSelectorRepairTests
{
    [Fact]
    public void FavouritesTable_MissingDotAndMisspelling_RepairedToDefinedClass()
    {
        // The exact regression: the file defines .favouritesTable; the added rule uses
        // 'favoritesTable' (missing '.' AND US spelling) and 'favouritesTable' (missing '.').
        const string css = """
            .favouritesTable {
              border-collapse: collapse;
              width: 100%;
            }

            .titleCell > div:last-child {
              white-space: nowrap;
              overflow: hidden;
              text-overflow: ellipsis;
            }

            /* Apply consistent table cell truncation styling */
            favoritesTable tbody tr td .titleMain,
            favouritesTable tbody tr td a {
              white-space: nowrap;
              overflow: hidden;
              text-overflow: ellipsis;
            }
            """;

        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(".favouritesTable tbody tr td .titleMain,", repaired);
        Assert.Contains(".favouritesTable tbody tr td a {", repaired);
        Assert.DoesNotContain("favoritesTable tbody", repaired);
        Assert.DoesNotContain("\nfavouritesTable tbody", repaired);
    }

    [Fact]
    public void ExactMatchBareToken_Repaired()
    {
        const string css = """
            .foo {
              color: red;
            }

            foo span {
              display: block;
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        Assert.Single(warnings);
        Assert.Contains(".foo span {", repaired);
        Assert.DoesNotContain("\nfoo span", repaired);
    }

    [Fact]
    public void NoDefinedClass_BareTokenLeftUntouched()
    {
        const string css = "bogusTable tbody tr td a {\n  white-space: nowrap;\n}\n";
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        Assert.Equal(css, repaired);
        Assert.Empty(warnings);
    }

    [Fact]
    public void HtmlElementsAndPseudoClasses_NeverTreatedAsClasses()
    {
        const string css = """
            .list {
              display: flex;
            }

            div > p + a,
            .list li:hover,
            input[type="text"] {
              color: red;
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        Assert.Equal(css, repaired);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CommentMentionedClass_DoesNotDefineOrRepair()
    {
        // '.ghost' only appears in a comment — it must not define the class, and the bare
        // 'ghost' in a real selector must NOT be repaired (no defined class to match).
        const string css = """
            /* .ghost was removed last week */
            ghost div {
              display: none;
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        Assert.Equal(css, repaired);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CaseInsensitiveMatch_RepairedWithFilesSpelling()
    {
        const string css = """
            .TitleCell {
              width: 100%;
            }

            titlecell div {
              display: block;
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        Assert.Single(warnings);
        Assert.Contains(".TitleCell div {", repaired);
    }

    [Fact]
    public void FuzzyDistanceTooLarge_NoRepair()
    {
        const string css = """
            .container {
              width: 100%;
            }

            wrap {
              display: none;
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        Assert.Equal(css, repaired);
        Assert.Empty(warnings);
    }

    [Fact]
    public void AtRuleBlocks_NotTreatedAsClassSelectors()
    {
        const string css = """
            @media (max-width: 600px) {
              .foo {
                color: red;
              }
            }

            .foo {
              color: blue;
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        // The @media block itself is an at-rule (never repaired); the top-level .foo rule is
        // already correct. Nothing bare to fix.
        Assert.Equal(css, repaired);
        Assert.Empty(warnings);
    }

    [Fact]
    public void BareClassInsideMediaQuery_Repaired()
    {
        const string css = """
            .foo {
              color: blue;
            }

            @media (max-width: 600px) {
              foo span {
                display: block;
              }
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        Assert.Single(warnings);
        Assert.Contains(".foo span {", repaired);
        Assert.DoesNotContain("\n  foo span", repaired);
        // The @media prelude itself must be untouched.
        Assert.Contains("@media (max-width: 600px) {", repaired);
    }

    [Fact]
    public void ClassDefinedOnlyInsideMediaQuery_BareTokenRepaired_AndTopLevelUntouched()
    {
        // .titleMain is defined ONLY inside the media query; a bare 'titleMain' at top level
        // still resolves against it, and one inside the block is repaired too.
        const string css = """
            @media (max-width: 600px) {
              .titleMain {
                font-weight: bold;
              }

              titleMain a {
                color: red;
              }
            }

            titleMain {
              display: block;
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css.Replace("\r\n", "\n"));
        Assert.Equal(2, warnings.Count);
        Assert.Contains("\n  .titleMain a {", repaired);
        Assert.Contains("\n.titleMain {\n  display: block;", repaired);
        Assert.DoesNotContain("\n  titleMain a", repaired);
    }

    [Fact]
    public void SupportsNestedInsideMediaQuery_DeepestBareClassRepaired()
    {
        const string css = """
            .card {
              padding: 8px;
            }

            @media (min-width: 600px) {
              @supports (display: grid) {
                card grid {
                  display: grid;
                }
              }
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        Assert.Single(warnings);
        Assert.Contains(".card grid {", repaired);
        Assert.DoesNotContain("\n                card grid", repaired);
    }

    [Fact]
    public void KeyframesFrameSelectors_NeverTreatedAsClasses()
    {
        // Keyframe frame selectors (from / to / %) must not be mistaken for bare classes,
        // and a brace inside the keyframes body must not confuse the block scanner.
        const string css = """
            .spinner {
              animation: spin 1s infinite;
            }

            @keyframes spin {
              from {
                transform: rotate(0deg);
              }

              to {
                transform: rotate(360deg);
              }
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        Assert.Equal(css, repaired);
        Assert.Empty(warnings);
    }

    [Fact]
    public void MediaQueryWithStringAndComment_BracesInsideDoNotBreakScanner()
    {
        // A '}' inside a string or comment must not close the @media block early, and the
        // bare class after it is still found.
        const string css = """
            .icon {
              color: red;
            }

            @media (max-width: 600px) {
              /* content: "}" — closing brace in a comment */
              icon small {
                font-size: 10px;
              }
            }
            """;
        var (repaired, warnings) = CssSelectorRepair.RepairBareClassSelectors(css);
        Assert.Single(warnings);
        Assert.Contains(".icon small {", repaired);
        Assert.Contains("/* content: \"}\" — closing brace in a comment */", repaired);
    }

    [Fact]
    public void FindBareClassSelectorIssues_FavouritesRegression_ReturnsRepairSuggestions()
    {
        const string css = """
            .favouritesTable {
              width: 100%;
            }

            favoritesTable tbody tr td .titleMain,
            favouritesTable tbody tr td a {
              white-space: nowrap;
            }
            """;
        var issues = CssSelectorRepair.FindBareClassSelectorIssues("favourites.component.css", css);
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Contains("bare 'favoritesTable'") && i.Contains(".favouritesTable'"));
        Assert.Contains(issues, i => i.Contains("bare 'favouritesTable'") && i.Contains(".favouritesTable'"));
        Assert.All(issues, i => Assert.Contains("prefix with '.'", i));
    }

    [Fact]
    public void FindBareClassSelectorIssues_NoDefinedClass_NoIssue()
    {
        // 'bogusTable' has no matching class anywhere in the file — nothing to report.
        const string css = "bogusTable tbody tr td a {\n  white-space: nowrap;\n}\n";
        Assert.Empty(CssSelectorRepair.FindBareClassSelectorIssues("x.css", css));
    }

    [Fact]
    public void FindBareClassSelectorIssues_CleanCss_NoIssue()
    {
        const string css = """
            .foo {
              color: red;
            }

            .foo span,
            div > p + a {
              display: block;
            }
            """;
        Assert.Empty(CssSelectorRepair.FindBareClassSelectorIssues("x.css", css));
    }

    [Fact]
    public void FindBareClassSelectorIssues_PreEditSnapshot_OnlyNewTokensFlagged()
    {
        const string preEdit = """
            .foo {
              color: red;
            }

            foo span {
              display: block;
            }
            """;
        // The run ADDED a rule with a bare 'bar' (now that .bar is defined). The pre-existing
        // bare 'foo' must NOT be attributed to the run.
        const string css = """
            .foo {
              color: red;
            }

            .bar {
              color: blue;
            }

            foo span {
              display: block;
            }

            bar a {
              display: inline;
            }
            """;
        var issues = CssSelectorRepair.FindBareClassSelectorIssues("x.css", css, preEdit);
        var issue = Assert.Single(issues);
        Assert.Contains("bare 'bar'", issue);
        Assert.DoesNotContain("bare 'foo'", issue);
    }

    [Fact]
    public void FindBareClassSelectorIssues_AtRuleSelectorNeverFlagged_NestedRuleIs()
    {
        const string css = """
            .card {
              padding: 8px;
            }

            @media (max-width: 600px) {
              card grid {
                display: grid;
              }
            }
            """;
        var issues = CssSelectorRepair.FindBareClassSelectorIssues("x.css", css);
        var issue = Assert.Single(issues);
        Assert.Contains("bare 'card'", issue);
        Assert.DoesNotContain("@media", issue);
    }

    [Fact]
    public void FindBareClassSelectorIssues_RepeatedToken_DedupedToOneIssue()
    {
        const string css = """
            .icon {
              color: red;
            }

            icon small {
              font-size: 10px;
            }

            icon big {
              font-size: 20px;
            }
            """;
        var issues = CssSelectorRepair.FindBareClassSelectorIssues("x.css", css);
        Assert.Single(issues);
    }

    [Fact]
    public void CheckModifiedCss_ScansCssFiles_IgnoresOthers_AndUsesSnapshots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "css-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // bad.css: pre-existing bare token (snapshot covers it) + one introduced by the run.
            System.IO.File.WriteAllText(Path.Combine(dir, "bad.css"),
                ".foo { color: red; }\n\nfoo span { display: block; }\n\n.bar { color: blue; }\n\nbar a { display: inline; }\n");
            // clean.css: nothing bare.
            System.IO.File.WriteAllText(Path.Combine(dir, "clean.css"),
                ".ok { color: green; }\n\n.ok div { display: block; }\n");
            // ignored.txt: not CSS, must be skipped even with a bare-looking token.
            System.IO.File.WriteAllText(Path.Combine(dir, "notes.txt"), "foo bar baz");

            var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["bad.css"] = ".foo { color: red; }\n\nfoo span { display: block; }\n"
            };
            var issues = CssSelectorRepair.CheckModifiedCss(dir,
                new[] { "bad.css", "clean.css", "notes.txt" }, snapshots);

            // Only the NEW bare 'bar' in bad.css is flagged; pre-existing 'foo' is skipped;
            // clean.css and notes.txt contribute nothing.
            var issue = Assert.Single(issues);
            Assert.Contains("bad.css", issue);
            Assert.Contains("bare 'bar'", issue);
            Assert.DoesNotContain("bare 'foo'", issue);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void EmptyOrNull_NoOp()
    {
        var (empty, warnings) = CssSelectorRepair.RepairBareClassSelectors("");
        Assert.Equal("", empty);
        Assert.Empty(warnings);
        var (nullCss, nullWarnings) = CssSelectorRepair.RepairBareClassSelectors(null!);
        Assert.Null(nullCss);
        Assert.Empty(nullWarnings);
    }

    // ─── CheckUnwiredCssDefinitions: a class/variable defined by the run must be wired up ───

    /// <summary>Builds the pre-edit snapshot map with ONLY the given entries — the on-disk css
    /// holds the post-edit content (with the new definitions), the snapshot holds the pre-edit
    /// content (without them), exactly like the pipeline's CapturePreEditSnapshots.</summary>
    private static Dictionary<string, string> PreEdit(params (string rel, string content)[] entries)
    {
        var snap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, content) in entries) snap[rel] = content;
        return snap;
    }

    [Fact]
    public void UnwiredCss_ClassUsedInSiblingTemplate_NoIssue()
    {
        using var fx = new CssFixture("globe.component.css", "globe.component.html");
        fx.WriteCss(".flight-detail-panel { padding: 12px; }\n\n.flight-detail-body { max-height: 300px; }\n");
        fx.WriteTemplate("<div class=\"flight-detail-panel\">\n  <div class=\"flight-detail-body\"></div>\n</div>\n");
        var snap = PreEdit(("src/globe.component.css", ".flight-detail-panel { padding: 12px; }\n"));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/globe.component.css" }, snap);
        Assert.Empty(issues);
    }

    [Fact]
    public void UnwiredCss_NewClassNeverReferenced_Flagged()
    {
        using var fx = new CssFixture("globe.component.css", "globe.component.html");
        fx.WriteCss(".flight-detail-panel { padding: 12px; }\n\n.flight-detail-body { max-height: 300px; }\n");
        fx.WriteTemplate("<div class=\"flight-detail-panel\"></div>\n"); // .flight-detail-body never used
        var snap = PreEdit(("src/globe.component.css", ".flight-detail-panel { padding: 12px; }\n"));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/globe.component.css" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains(".flight-detail-body", issue);
        Assert.Contains("globe.component.html", issue);
        Assert.Contains("wire", issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnwiredCss_PreEditSnapshot_OnlyRunIntroducedClassFlagged()
    {
        using var fx = new CssFixture("panel.component.css", "panel.component.html");
        // .preExisting is already in the pre-edit snapshot and never referenced — NOT this run's
        // doing, so it must not be attributed to the run. Only .newClass (introduced by the run)
        // may be flagged.
        fx.WriteCss(".preExisting { color: red; }\n\n.newClass { color: blue; }\n");
        fx.WriteTemplate("<div></div>\n");
        var snap = PreEdit(("src/panel.component.css", ".preExisting { color: red; }\n"));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/panel.component.css" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains(".newClass", issue);
        Assert.DoesNotContain("preExisting", issue);
    }

    [Fact]
    public void UnwiredCss_CreatedCssFile_NoSnapshot_AllDefinitionsJudged()
    {
        using var fx = new CssFixture("banner.component.css", "banner.component.html");
        // No snapshot entry for the css ⇒ it was created by the run ⇒ every class is new.
        fx.WriteCss(".banner-hero { display: flex; }\n");
        fx.WriteTemplate("<div class=\"banner-hero\"></div>\n");
        Assert.Empty(CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/banner.component.css" }, null));

        // Without the template referencing the class, the same created file is flagged.
        fx.WriteTemplate("<div></div>\n");
        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/banner.component.css" }, null);
        var issue = Assert.Single(issues);
        Assert.Contains(".banner-hero", issue);
    }

    [Fact]
    public void UnwiredCss_CustomProperty_ConsumedInSameFile_NoIssue()
    {
        using var fx = new CssFixture("theme.component.css", "theme.component.html");
        fx.WriteCss(":root { --accent: #fff; }\n.card { color: var(--accent); }\n");
        fx.WriteTemplate("<div class=\"card\"></div>\n");
        var snap = PreEdit(("src/theme.component.css", ".card { color: red; }\n"));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/theme.component.css" }, snap);
        Assert.Empty(issues);
    }

    [Fact]
    public void UnwiredCss_CustomProperty_Unused_Flagged()
    {
        using var fx = new CssFixture("theme.component.css", "theme.component.html");
        fx.WriteCss(":root { --accent: #fff; }\n.card { color: red; }\n");
        fx.WriteTemplate("<div class=\"card\"></div>\n");
        var snap = PreEdit(("src/theme.component.css", ".card { color: red; }\n"));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/theme.component.css" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains("--accent", issue);
        Assert.Contains("var(--accent)", issue);
    }

    [Fact]
    public void UnwiredCss_ClassesInsideNotPseudo_NeverRequired()
    {
        using var fx = new CssFixture("list.component.css", "list.component.html");
        fx.WriteCss(".item:not(.is-hidden) { display: block; }\n");
        fx.WriteTemplate("<div class=\"item\"></div>\n");
        var snap = PreEdit(("src/list.component.css", ""));

        // .is-hidden is only inside :not(...) — the rule needs .item, not .is-hidden.
        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/list.component.css" }, snap);
        Assert.Empty(issues);
    }

    [Fact]
    public void UnwiredCss_PrefixSafe_CardDoesNotMatchCardBody()
    {
        using var fx = new CssFixture("card.component.css", "card.component.html");
        fx.WriteCss(".card { border: 1px solid; }\n");
        fx.WriteTemplate("<div class=\"card-body\"></div>\n"); // card-body is NOT .card
        var snap = PreEdit(("src/card.component.css", ""));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/card.component.css" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains(".card", issue);
    }

    [Fact]
    public void UnwiredCss_NoSiblingFiles_GlobalStylesheet_Skipped()
    {
        using var fx = new CssFixture("styles.css", null);
        fx.WriteCss(".global-helper { float: left; }\n"); // no connected template/component
        var snap = PreEdit(("src/styles.css", ""));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/styles.css" }, snap);
        Assert.Empty(issues);
    }

    [Fact]
    public void UnwiredCss_FallbackScan_UsesSameDirectoryFiles()
    {
        using var fx = new CssFixture("site.css", null);
        fx.WriteCss(".site-hero { padding: 8px; }\n");
        System.IO.File.WriteAllText(Path.Combine(fx.Root, "src", "index.html"),
            "<div class=\"site-hero\"></div>\n");
        var snap = PreEdit(("src/site.css", ""));

        // No name-matching sibling, but index.html lives beside site.css and uses the class.
        Assert.Empty(CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/site.css" }, snap));

        System.IO.File.WriteAllText(Path.Combine(fx.Root, "src", "index.html"), "<div></div>\n");
        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/site.css" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains(".site-hero", issue);
    }

    // ─── Dynamic class variables: a class applied via classList.add / querySelector ───────
    // A component may apply a class at RUNTIME through a variable instead of writing it
    // literally anywhere: el.classList.add(this.stateClass) or el.querySelector('.' + cls)
    // where the class name arrives as an @Input / computed value / composed string. The class
    // name may appear NOWHERE as a literal in the template OR the component source, so the
    // literal reference test alone would false-positive a genuinely wired class as unwired.
    // IsDynamicallyWired credits the wiring when the sibling .ts/.js applies a class through
    // a VARIABLE argument (literal arguments — a different class — never credit it).

    [Fact]
    public void UnwiredCss_ClassAppliedViaClassListAddVariable_NoIssue()
    {
        using var fx = new CssFixture("panel.component.css", "panel.component.html");
        fx.WriteCss(".flight-detail-body { max-height: 300px; }\n");
        fx.WriteTemplate("<div class=\"flight-detail-panel\"></div>\n");
        // The class name appears NOWHERE as a literal: stateClass is an @Input the caller
        // fills at runtime, and the component applies it via classList.add.
        System.IO.File.WriteAllText(Path.Combine(fx.Root, "src", "panel.component.ts"),
            "@Component({ selector: 'app-panel' })\n" +
            "export class PanelComponent {\n" +
            "  @Input() stateClass = '';\n" +
            "  ngAfterViewInit() { this.el.classList.add(this.stateClass); }\n" +
            "}\n");
        var snap = PreEdit(("src/panel.component.css", ""));

        Assert.Empty(CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/panel.component.css" }, snap));
    }

    [Fact]
    public void UnwiredCss_ClassAppliedViaQuerySelectorVariable_NoIssue()
    {
        using var fx = new CssFixture("globe.component.css", "globe.component.html");
        fx.WriteCss(".flight-detail-body { max-height: 300px; }\n");
        fx.WriteTemplate("<div id=\"flight\"></div>\n");
        // querySelector('.' + cls) — the class name arrives as a parameter, nowhere as a literal.
        System.IO.File.WriteAllText(Path.Combine(fx.Root, "src", "globe.component.ts"),
            "export class GlobeComponent {\n" +
            "  openFlight(cls: string) {\n" +
            "    const el = this.root.nativeElement.querySelector('.' + cls);\n" +
            "  }\n" +
            "}\n");
        var snap = PreEdit(("src/globe.component.css", ""));

        Assert.Empty(CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/globe.component.css" }, snap));
    }

    [Fact]
    public void UnwiredCss_ClassAppliedViaTemplateLiteralQuerySelector_NoIssue()
    {
        using var fx = new CssFixture("banner.component.css", "banner.component.html");
        fx.WriteCss(".banner-hero { display: flex; }\n");
        fx.WriteTemplate("<div id=\"banner\"></div>\n");
        // Template-literal form: querySelector(`.${hero}`) — braces inside the call window,
        // hero composed at runtime from component state (no literal anywhere).
        System.IO.File.WriteAllText(Path.Combine(fx.Root, "src", "banner.component.ts"),
            "export class BannerComponent {\n" +
            "  show() {\n" +
            "    const hero = this.state.heroClass;\n" +
            "    const el = this.ref.nativeElement.querySelector(`.${hero}`);\n" +
            "  }\n" +
            "}\n");
        var snap = PreEdit(("src/banner.component.css", ""));

        Assert.Empty(CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/banner.component.css" }, snap));
    }

    [Fact]
    public void UnwiredCss_DynamicClassVariable_AssignedButNeverApplied_StillFlagged()
    {
        // Negative control: the component has a class-shaped variable but never feeds it to a
        // DOM class-application call — the class is still unwired and must be flagged.
        using var fx = new CssFixture("list.component.css", "list.component.html");
        fx.WriteCss(".list-scroll { overflow: auto; }\n");
        fx.WriteTemplate("<div id=\"list\"></div>\n");
        System.IO.File.WriteAllText(Path.Combine(fx.Root, "src", "list.component.ts"),
            "export class ListComponent {\n" +
            "  @Input() cssName = '';\n" +   // would hold 'list-scroll' at runtime, never applied
            "}\n");
        var snap = PreEdit(("src/list.component.css", ""));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/list.component.css" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains(".list-scroll", issue);
    }

    [Fact]
    public void UnwiredCss_DynamicClassVariable_UsedForNonClassPurpose_StillFlagged()
    {
        // The variable is consumed, but NOT by a class-application call — e.g. it feeds a data
        // attribute instead. That is not wiring; the class stays unwired.
        using var fx = new CssFixture("card.component.css", "card.component.html");
        fx.WriteCss(".card-extra { margin: 4px; }\n");
        fx.WriteTemplate("<div id=\"card\"></div>\n");
        System.IO.File.WriteAllText(Path.Combine(fx.Root, "src", "card.component.ts"),
            "export class CardComponent {\n" +
            "  applyState() {\n" +
            "    const label = this.state.label;\n" +  // composed at runtime, no literal
            "    this.el.setAttribute('data-state', label);\n" +  // NOT a class-application call
            "  }\n" +
            "}\n");
        var snap = PreEdit(("src/card.component.css", ""));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/card.component.css" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains(".card-extra", issue);
    }

    [Fact]
    public void UnwiredCss_LiteralClassAddedForDifferentClass_NotAttributed()
    {
        // The component applies a DIFFERENT class as a literal argument — that must not wire
        // the CSS-defined class this run created (the literal-stripping guard).
        using var fx = new CssFixture("panel.component.css", "panel.component.html");
        fx.WriteCss(".flight-detail-body { max-height: 300px; }\n");
        fx.WriteTemplate("<div class=\"flight-detail-panel\"></div>\n");
        System.IO.File.WriteAllText(Path.Combine(fx.Root, "src", "panel.component.ts"),
            "export class PanelComponent {\n" +
            "  ngAfterViewInit() { this.el.classList.add('other-state'); }\n" +
            "}\n");
        var snap = PreEdit(("src/panel.component.css", ""));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/panel.component.css" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains(".flight-detail-body", issue);
    }

    // ─── SCSS / LESS: CheckUnwiredCssDefinitions scans .scss/.less files too, reusing the
    //     same connected-template wiring logic and selector parsing ──────────────────────────

    [Fact]
    public void UnwiredCss_ScssFile_UnreferencedClass_Flagged()
    {
        using var fx = new CssFixture("panel.component.scss", "panel.component.html");
        fx.WriteCss(".panel-shell { display: flex; }\n\n.panel-orphan { max-height: 300px; }\n");
        fx.WriteTemplate("<div class=\"panel-shell\"></div>\n"); // .panel-orphan never used
        var snap = PreEdit(("src/panel.component.scss", ".panel-shell { display: flex; }\n"));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/panel.component.scss" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains(".panel-orphan", issue);
        Assert.Contains("panel.component.scss", issue);
    }

    [Fact]
    public void UnwiredCss_ScssFile_ClassUsedInSiblingTemplate_NoIssue()
    {
        // The .component.scss sibling resolution finds panel.component.html exactly like .css.
        using var fx = new CssFixture("panel.component.scss", "panel.component.html");
        fx.WriteCss(".panel-shell { display: flex; }\n.panel-orphan { max-height: 300px; }\n");
        fx.WriteTemplate("<div class=\"panel-shell panel-orphan\"></div>\n");
        var snap = PreEdit(("src/panel.component.scss", ".panel-shell { display: flex; }\n"));

        Assert.Empty(CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/panel.component.scss" }, snap));
    }

    [Fact]
    public void UnwiredCss_LessFile_ClassUsedInSiblingComponent_NoIssue()
    {
        using var fx = new CssFixture("globe.component.less", "globe.component.html");
        fx.WriteCss(".flight-detail-body { max-height: 300px; }\n");
        fx.WriteTemplate("<div id=\"flight\"></div>\n");
        System.IO.File.WriteAllText(Path.Combine(fx.Root, "src", "globe.component.ts"),
            "export class GlobeComponent {\n" +
            "  get panelClass() { return 'flight-detail-body'; }\n" +
            "}\n");
        var snap = PreEdit(("src/globe.component.less", ""));

        Assert.Empty(CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/globe.component.less" }, snap));
    }

    [Fact]
    public void UnwiredCss_ScssNestedRule_UnreferencedNestedClass_Flagged()
    {
        // SCSS nesting: the parser descends into nested rules, so a class defined only inside
        // a nested block is still a definition and must be wired up.
        using var fx = new CssFixture("card.component.scss", "card.component.html");
        fx.WriteCss(".card {\n  .card-extra { margin: 4px; }\n}\n");
        fx.WriteTemplate("<div class=\"card\"></div>\n"); // .card-extra never used
        var snap = PreEdit(("src/card.component.scss", ""));

        var issues = CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/card.component.scss" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains(".card-extra", issue);
    }

    [Fact]
    public void UnwiredCss_ScssFile_PreexistingClassNotAttributed()
    {
        // The snapshot diff works for .scss too: a class that predates the run (present in the
        // pre-edit snapshot, unchanged) is not attributed, even if unreferenced.
        using var fx = new CssFixture("panel.component.scss", "panel.component.html");
        fx.WriteCss(".panel-old { color: red; }\n");
        fx.WriteTemplate("<div class=\"panel-shell\"></div>\n"); // .panel-old not referenced
        var snap = PreEdit(("src/panel.component.scss", ".panel-old { color: red; }\n"));

        Assert.Empty(CssSelectorRepair.CheckUnwiredCssDefinitions(fx.Root,
            new[] { "src/panel.component.scss" }, snap));
    }

    // ─── CheckOrphanedTemplateReferences: a class REMOVED by the run must be cleaned out of
    //     the connected template/component too (the mirror of the unwired check) ─────────────

    [Fact]
    public void OrphanedCss_RemovedClassStillReferencedInTemplate_Flagged()
    {
        using var fx = new CssFixture("globe.component.css", "globe.component.html");
        // The run removed the .flight-detail-body rule; the template still uses the class.
        fx.WriteCss(".flight-detail-panel { padding: 12px; }\n");
        fx.WriteTemplate("<div class=\"flight-detail-panel\">\n  <div class=\"flight-detail-body\"></div>\n</div>\n");
        var snap = PreEdit(("src/globe.component.css",
            ".flight-detail-panel { padding: 12px; }\n\n.flight-detail-body { max-height: 300px; }\n"));

        var issues = CssSelectorRepair.CheckOrphanedTemplateReferences(fx.Root,
            new[] { "src/globe.component.css" }, snap);
        var issue = Assert.Single(issues);
        Assert.Contains(".flight-detail-body", issue);
        Assert.Contains("globe.component.css", issue);
        Assert.Contains("globe.component.html", issue);
        Assert.Contains("orphaned", issue, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("removed", issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrphanedCss_TemplateReferenceCleanedToo_NoIssue()
    {
        using var fx = new CssFixture("globe.component.css", "globe.component.html");
        // Both the rule AND the template reference are gone — nothing dangles.
        fx.WriteCss(".flight-detail-panel { padding: 12px; }\n");
        fx.WriteTemplate("<div class=\"flight-detail-panel\"></div>\n");
        var snap = PreEdit(("src/globe.component.css",
            ".flight-detail-panel { padding: 12px; }\n\n.flight-detail-body { max-height: 300px; }\n"));

        Assert.Empty(CssSelectorRepair.CheckOrphanedTemplateReferences(fx.Root,
            new[] { "src/globe.component.css" }, snap));
    }

    [Fact]
    public void OrphanedCss_RemovalPredatesRun_NotAttributed()
    {
        // .gone was already absent from the PRE-EDIT snapshot — the run did not remove it, so
        // the leftover template reference is not this run's doing and must not be flagged.
        using var fx = new CssFixture("panel.component.css", "panel.component.html");
        fx.WriteCss(".remaining { color: red; }\n");
        fx.WriteTemplate("<div class=\"gone\"></div>\n");
        var snap = PreEdit(("src/panel.component.css", ".remaining { color: red; }\n")); // .gone already gone

        Assert.Empty(CssSelectorRepair.CheckOrphanedTemplateReferences(fx.Root,
            new[] { "src/panel.component.css" }, snap));
    }

    [Fact]
    public void OrphanedCss_ClassStillDefined_NotFlagged()
    {
        // The run removed only .card-extra; .card is still defined and referenced — no orphan.
        using var fx = new CssFixture("card.component.css", "card.component.html");
        fx.WriteCss(".card { border: 1px solid; }\n");
        fx.WriteTemplate("<div class=\"card\"></div>\n");
        var snap = PreEdit(("src/card.component.css",
            ".card { border: 1px solid; }\n\n.card-extra { margin: 4px; }\n"));

        Assert.Empty(CssSelectorRepair.CheckOrphanedTemplateReferences(fx.Root,
            new[] { "src/card.component.css" }, snap));
    }

    [Fact]
    public void OrphanedCss_PrefixSafe_CardBodyReferenceNotMatchedByCard()
    {
        // Removing '.card' while the template has class="card-body" — card-body is a DIFFERENT
        // class (whole-token match), so no orphan is created.
        using var fx = new CssFixture("card.component.css", "card.component.html");
        fx.WriteCss(".card-body { display: flex; }\n");
        fx.WriteTemplate("<div class=\"card-body\"></div>\n");
        var snap = PreEdit(("src/card.component.css",
            ".card { border: 1px solid; }\n\n.card-body { display: flex; }\n"));

        Assert.Empty(CssSelectorRepair.CheckOrphanedTemplateReferences(fx.Root,
            new[] { "src/card.component.css" }, snap));
    }

    [Fact]
    public void OrphanedCss_NoSnapshot_FileCreatedByRun_NothingRemoved()
    {
        using var fx = new CssFixture("banner.component.css", "banner.component.html");
        fx.WriteCss(".banner-hero { display: flex; }\n");
        fx.WriteTemplate("<div class=\"banner-hero\"></div>\n");

        Assert.Empty(CssSelectorRepair.CheckOrphanedTemplateReferences(fx.Root,
            new[] { "src/banner.component.css" }, null));
    }

    [Fact]
    public void OrphanedCss_StandaloneStylesheet_NoConnectedFiles_Skipped()
    {
        using var fx = new CssFixture("styles.css", null);
        fx.WriteCss(".global-helper { float: left; }\n");
        var snap = PreEdit(("src/styles.css",
            ".global-helper { float: left; }\n\n.removed-global { color: red; }\n"));

        // .removed-global is gone from the current file, but a global stylesheet has no
        // connected template/component to reference it — nothing to judge.
        Assert.Empty(CssSelectorRepair.CheckOrphanedTemplateReferences(fx.Root,
            new[] { "src/styles.css" }, snap));
    }

    /// <summary>Temp fixture: a css file (plus optional sibling template) under {root}/src.</summary>
    private sealed class CssFixture : IDisposable
    {
        public CssFixture(string cssFile, string? templateFile)
        {
            Root = Path.Combine(Path.GetTempPath(), "css-unwired-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            CssRel = "src/" + cssFile;
            TemplateRel = templateFile == null ? null : "src/" + templateFile;
        }

        public string Root { get; }
        public string CssRel { get; }
        public string? TemplateRel { get; }

        public void WriteCss(string content) =>
            System.IO.File.WriteAllText(Path.Combine(Root, CssRel.Replace('/', Path.DirectorySeparatorChar)), content);

        public void WriteTemplate(string content)
        {
            if (TemplateRel == null) throw new InvalidOperationException("no template in fixture");
            System.IO.File.WriteAllText(Path.Combine(Root, TemplateRel.Replace('/', Path.DirectorySeparatorChar)), content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
