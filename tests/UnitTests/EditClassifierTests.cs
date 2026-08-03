using Xunit;
using Weaver;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Deterministic tests for <see cref="EditClassifier"/> — the single place that maps
/// (step, file-exists, ext) → EditStrategy. These must never silently regress because
/// every downstream path (prompt builder, applier, escalation) switches on the result.
/// </summary>
public class EditClassifierTests
{
    private static PlanStep Step(string file, string change, string? symbol = null) => new()
    {
        File = file,
        Change = change,
        TargetSymbol = symbol
    };

    // ── CreateFile ───────────────────────────────────────────────────────────

    [Fact]
    public void Classify_MissingFile_ReturnsCreateFile_RegardlessOfExt()
    {
        foreach (var ext in new[] { ".ts", ".cs", ".html", ".css", ".js" })
        {
            var result = EditClassifier.Classify(Step("src/new/file" + ext, "Add a method"), fileExists: false, ext);
            Assert.Equal(EditStrategy.CreateFile, result);
        }
    }

    // ── HTML ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Replace the header with a new one", EditStrategy.HtmlReplace)]
    [InlineData("Update the footer text", EditStrategy.HtmlReplace)]
    [InlineData("Modify the button label", EditStrategy.HtmlReplace)]
    [InlineData("Change the title", EditStrategy.HtmlReplace)]
    [InlineData("Add a button after the header", EditStrategy.HtmlInsertAfter)]
    [InlineData("Insert a section below the nav", EditStrategy.HtmlInsertAfter)]
    [InlineData("Append a new row", EditStrategy.HtmlInsertAfter)]
    [InlineData("Add a button before the footer", EditStrategy.HtmlInsertBefore)]
    [InlineData("Insert a div above the list", EditStrategy.HtmlInsertBefore)]
    [InlineData("Add a new section", EditStrategy.HtmlInsertBefore)]
    public void Classify_HtmlChange_Subclassifies(string change, EditStrategy expected)
    {
        var result = EditClassifier.Classify(
            Step("wwwroot/kanban.html", change),
            fileExists: true, ext: ".html");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Classify_HtmlDeletion_ReturnsHtmlInsertBefore_NotDeleteLines()
    {
        // HTML is sub-classified before the deletion check, so "remove" lands on the
        // safe HTML default — the applier still handles the empty-newString deletion.
        var result = EditClassifier.Classify(
            Step("index.html", "Remove the split button from the card"),
            fileExists: true, ext: ".html");
        Assert.Equal(EditStrategy.HtmlInsertBefore, result);
    }

    // ── Deletion ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Remove the split button from the card actions")]
    [InlineData("Delete the unused function")]
    [InlineData("Strip the debug logging block")]
    [InlineData("Erase the stale comment")]
    public void Classify_DeletionChange_ReturnsDeleteLines(string change)
    {
        var result = EditClassifier.Classify(
            Step("src/app/foo.component.ts", change),
            fileExists: true, ext: ".ts");
        Assert.Equal(EditStrategy.DeleteLines, result);
    }

    [Fact]
    public void Classify_RemoveAndReplace_IsNotDeletion()
    {
        var result = EditClassifier.Classify(
            Step("src/app/foo.component.ts", "Remove the old button and add a new one"),
            fileExists: true, ext: ".ts");
        Assert.NotEqual(EditStrategy.DeleteLines, result);
    }

    // ── Property fill ────────────────────────────────────────────────────────

    [Fact]
    public void Classify_PropertyFill_OnCs_ReturnsFillClassBody()
    {
        var result = EditClassifier.Classify(
            Step("Controllers/FooController.cs", "Add a new property to the class", "IsOpen"),
            fileExists: true, ext: ".cs");
        Assert.Equal(EditStrategy.FillClassBody, result);
    }

    [Fact]
    public void Classify_PropertyFill_OnTs_FallsBackToAnchoredEdit()
    {
        // FORMAT C class-replace is blocked for .ts — property fill must be anchored.
        var result = EditClassifier.Classify(
            Step("src/app/foo/foo.component.ts", "Add isMenuPanelOpen property declaration", "isMenuPanelOpen"),
            fileExists: true, ext: ".ts");
        Assert.Equal(EditStrategy.AnchoredEdit, result);
    }

    // ── New method / endpoint (FORMAT C insert) ──────────────────────────────

    [Theory]
    [InlineData("Add a new method to the class", "getData")]
    [InlineData("Create an endpoint that returns the list", null)]
    [InlineData("Implement a new function named parseInput", "parseInput")]
    [InlineData("Add a PostRecipe method", "PostRecipe")]
    public void Classify_NewMethod_OnFormatCLanguage_ReturnsInsertMethod(string change, string? symbol)
    {
        var result = EditClassifier.Classify(
            Step("src/app/foo/foo.component.ts", change, symbol),
            fileExists: true, ext: ".ts");
        Assert.Equal(EditStrategy.InsertMethod, result);
    }

    [Fact]
    public void Classify_NewMethod_OnNonFormatCLanguage_ReturnsAnchoredEdit()
    {
        // .c/.h have supportsFormatC = false — new methods must be anchored text edits.
        var result = EditClassifier.Classify(
            Step("src/foo.c", "Add a new function to the file", "helper"),
            fileExists: true, ext: ".c");
        Assert.Equal(EditStrategy.AnchoredEdit, result);
    }

    [Fact]
    public void Classify_NewMethod_OnCs_ReturnsInsertMethod()
    {
        // C# is a FORMAT C language too — a new endpoint must resolve to InsertMethod.
        var result = EditClassifier.Classify(
            Step("Controllers/OrdersController.cs", "Add a GetOrders endpoint", "GetOrders"),
            fileExists: true, ext: ".cs");
        Assert.Equal(EditStrategy.InsertMethod, result);
    }

    [Theory]
    [InlineData("src/page.razor", "Add a section after the header", EditStrategy.HtmlInsertAfter)]
    [InlineData("src/page.cshtml", "Replace the title block", EditStrategy.HtmlReplace)]
    [InlineData("src/page.htm", "Add a footer", EditStrategy.HtmlInsertBefore)]
    public void Classify_HtmlChange_TemplateExtensions_Subclassifies(string file, string change, EditStrategy expected)
    {
        var result = EditClassifier.Classify(Step(file, change), fileExists: true, ext: ".html");
        Assert.Equal(expected, result);
    }

    // ── Full method rewrite (FORMAT C replace) ───────────────────────────────

    [Theory]
    [InlineData("Rewrite the loadData method body", "loadData")]
    [InlineData("Refactor the whole parse function", "parse")]
    [InlineData("Replace the entire save method", "save")]
    public void Classify_FullMethodRewrite_OnFormatCLanguage_ReturnsReplaceMethod(string change, string? symbol)
    {
        var result = EditClassifier.Classify(
            Step("src/app/foo/foo.component.ts", change, symbol),
            fileExists: true, ext: ".ts");
        Assert.Equal(EditStrategy.ReplaceMethod, result);
    }

    // ── Safe fallback ────────────────────────────────────────────────────────

    [Fact]
    public void Classify_GenericTweak_ReturnsAnchoredEdit()
    {
        var result = EditClassifier.Classify(
            Step("src/app/foo/foo.component.ts", "Fix the typo in the greeting message"),
            fileExists: true, ext: ".ts");
        Assert.Equal(EditStrategy.AnchoredEdit, result);
    }

    // ── ClassifyIntent ───────────────────────────────────────────────────────

    [Fact]
    public void ClassifyIntent_Deletion_ReturnsDeleteContent()
    {
        var intent = EditClassifier.ClassifyIntent(
            Step("src/app/foo.component.ts", "Remove the debug log line"), ".ts");
        Assert.Equal(EditIntentKind.DeleteContent, intent.Kind);
    }

    [Fact]
    public void ClassifyIntent_PropertyFill_ReturnsAddProperty()
    {
        var intent = EditClassifier.ClassifyIntent(
            Step("src/app/foo.component.ts", "Add a new property to the class", "IsOpen"), ".ts");
        Assert.Equal(EditIntentKind.AddProperty, intent.Kind);
        Assert.Equal("IsOpen", intent.Symbol);
    }

    [Fact]
    public void ClassifyIntent_NewMethod_ReturnsInsertNearSymbol()
    {
        var intent = EditClassifier.ClassifyIntent(
            Step("src/app/foo.component.ts", "Add a new method to the class", "getData"), ".ts");
        Assert.Equal(EditIntentKind.InsertNearSymbol, intent.Kind);
    }

    [Fact]
    public void ClassifyIntent_FullRewrite_ReturnsReplaceSymbol()
    {
        var intent = EditClassifier.ClassifyIntent(
            Step("src/app/foo.component.ts", "Rewrite the loadData method entirely", "loadData"), ".ts");
        Assert.Equal(EditIntentKind.ReplaceSymbol, intent.Kind);
    }

    [Fact]
    public void ClassifyIntent_Generic_ReturnsTargetedEdit()
    {
        var intent = EditClassifier.ClassifyIntent(
            Step("src/app/foo.component.ts", "Fix the padding on the sidebar"), ".ts");
        Assert.Equal(EditIntentKind.TargetedEdit, intent.Kind);
    }

    // ── Predicates directly ──────────────────────────────────────────────────

    [Theory]
    [InlineData("remove the button", true)]
    [InlineData("delete the function", true)]
    [InlineData("remove and replace the block", false)]
    [InlineData("add a new method", false)]
    [InlineData("update the text", false)]
    public void IsDeletion_ClassifiesPrefixVerbs(string change, bool expected)
    {
        Assert.Equal(expected, EditClassifier.IsDeletion(change.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("add a new method to the class", null, true)]
    [InlineData("create a GetOrders endpoint", null, true)]
    [InlineData("implement function called run", null, true)]
    [InlineData("update the existing login method", null, false)]
    [InlineData("fix the bug in save", "save", false)]
    public void IsNewMethodOrEndpoint_DetectsInsertions(string change, string? symbol, bool expected)
    {
        Assert.Equal(expected, EditClassifier.IsNewMethodOrEndpoint(change.ToLowerInvariant(), symbol));
    }

    [Theory]
    [InlineData("rewrite the loadData method", "loadData", true)]
    [InlineData("refactor the whole parse function", "parse", true)]
    [InlineData("fix the typo", "loadData", false)]
    [InlineData("update the button text", null, false)]
    public void IsFullMethodRewrite_DetectsBodyRewrites(string change, string? symbol, bool expected)
    {
        Assert.Equal(expected, EditClassifier.IsFullMethodRewrite(change.ToLowerInvariant(), symbol));
    }
}
