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
        // RepairBareClassSelectors passes line endings through unchanged, so normalize
        // to LF before asserting — otherwise the test breaks on Windows checkouts with
        // core.autocrlf=true, where the raw-string literal inherits CRLF from the source
        // file but the assertions below use \n.
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
}
