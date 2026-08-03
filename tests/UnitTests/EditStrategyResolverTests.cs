using Xunit;
using Weaver;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Deterministic tests for <see cref="EditStrategyResolver.Decide"/> — the state machine
/// that maps (path, exists, content, intent) → EditPlanDecision. Guards every branch:
/// create-file, HTML subclassification, deletion, whitespace-significant languages,
/// property-fill, AST symbol resolution (Roslyn + Tree-sitter), and the safe fallback.
/// </summary>
public class EditStrategyResolverTests
{
    // ── File does not exist ──────────────────────────────────────────────────

    [Fact]
    public void Decide_MissingFile_ReturnsCreateFile()
    {
        var intent = new EditIntent(EditIntentKind.TargetedEdit, null, null);
        var decision = EditStrategyResolver.Decide(
            "src/new/component.ts", fileExists: false, fileContent: "",
            "Create the component", intent);

        Assert.Equal(EditStrategy.CreateFile, decision.Strategy);
        Assert.Contains("does not exist", decision.Reason);
    }

    // ── HTML / template family ───────────────────────────────────────────────

    [Theory]
    [InlineData(EditIntentKind.InsertNearSymbol, EditStrategy.HtmlInsertAfter)]
    [InlineData(EditIntentKind.ReplaceSymbol, EditStrategy.HtmlReplace)]
    [InlineData(EditIntentKind.TargetedEdit, EditStrategy.HtmlInsertBefore)]
    [InlineData(EditIntentKind.AddProperty, EditStrategy.HtmlInsertBefore)]
    public void Decide_HtmlFile_MapsIntentToDomStrategy(EditIntentKind kind, EditStrategy expected)
    {
        var intent = new EditIntent(kind, "card", null);
        var decision = EditStrategyResolver.Decide(
            "wwwroot/index.html", fileExists: true, fileContent: "<div class='card'>x</div>",
            "update the card", intent);

        Assert.Equal(expected, decision.Strategy);
        Assert.Equal("card", decision.TargetName);
    }

    [Theory]
    [InlineData(".cshtml")]
    [InlineData(".razor")]
    [InlineData(".htm")]
    public void Decide_TemplateExtensions_UseDomStrategies(string ext)
    {
        var intent = new EditIntent(EditIntentKind.ReplaceSymbol, "header", null);
        var decision = EditStrategyResolver.Decide(
            $"src/page{ext}", fileExists: true, fileContent: "<header>x</header>",
            "replace header", intent);
        Assert.Equal(EditStrategy.HtmlReplace, decision.Strategy);
    }

    // ── Deletion ─────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_DeleteIntent_ReturnsDeleteLines()
    {
        var intent = new EditIntent(EditIntentKind.DeleteContent, null, null);
        var decision = EditStrategyResolver.Decide(
            "src/Foo.cs", fileExists: true, fileContent: "class Foo {}",
            "remove the debug log", intent);
        Assert.Equal(EditStrategy.DeleteLines, decision.Strategy);
    }

    // ── Whitespace-significant / non-AST languages ───────────────────────────

    [Theory]
    [InlineData(".css")]
    [InlineData(".scss")]
    [InlineData(".json")]
    [InlineData(".yaml")]
    [InlineData(".xml")]
    [InlineData(".md")]
    [InlineData(".txt")]
    public void Decide_WhitespaceSignificant_AlwaysAnchored(string ext)
    {
        var intent = new EditIntent(EditIntentKind.ReplaceSymbol, "card", "method");
        var decision = EditStrategyResolver.Decide(
            $"src/style{ext}", fileExists: true, fileContent: ".card {}",
            "change the color", intent);
        Assert.Equal(EditStrategy.AnchoredEdit, decision.Strategy);
    }

    // ── Property/field addition ──────────────────────────────────────────────

    [Fact]
    public void Decide_AddProperty_ReturnsFillClassBody_NeverClassReplace()
    {
        var intent = new EditIntent(EditIntentKind.AddProperty, "IsOpen", "property");
        var decision = EditStrategyResolver.Decide(
            "src/Foo.cs", fileExists: true, fileContent: "public class Foo { }",
            "add an IsOpen property", intent);

        Assert.Equal(EditStrategy.FillClassBody, decision.Strategy);
        Assert.Equal("class", decision.TargetType);
        Assert.Contains("not full-class replace", decision.Reason);
    }

    // ── AST symbol resolution — C# via Roslyn ────────────────────────────────

    [Fact]
    public void Decide_CSharp_ReplaceSymbol_ResolvesMethodViaRoslyn()
    {
        var source = """
            public class Foo
            {
                public void Bar()
                {
                    Console.WriteLine("hi");
                }
            }
            """;
        var intent = new EditIntent(EditIntentKind.ReplaceSymbol, "Bar", "method");
        var decision = EditStrategyResolver.Decide(
            "src/Foo.cs", fileExists: true, fileContent: source,
            "rewrite the Bar method", intent);

        Assert.Equal(EditStrategy.ReplaceMethod, decision.Strategy);
        Assert.Equal("method", decision.TargetType);
        Assert.NotNull(decision.ResolvedOldStr);
        Assert.Contains("public void Bar", decision.ResolvedOldStr);
        Assert.Contains("method", decision.Reason);
    }

    [Fact]
    public void Decide_CSharp_ReplaceSymbol_PreferredClass_ResolvesClass()
    {
        var source = """
            public class Foo
            {
                public int Value { get; set; }
            }
            """;
        var intent = new EditIntent(EditIntentKind.ReplaceSymbol, "Foo", "class");
        var decision = EditStrategyResolver.Decide(
            "src/Foo.cs", fileExists: true, fileContent: source,
            "rewrite the Foo class", intent);

        Assert.Equal(EditStrategy.ReplaceMethod, decision.Strategy);
        Assert.Equal("class", decision.TargetType);
        Assert.Contains("public class Foo", decision.ResolvedOldStr);
    }

    [Fact]
    public void Decide_CSharp_UnresolvableSymbol_FallsBackToAnchored()
    {
        var intent = new EditIntent(EditIntentKind.ReplaceSymbol, "DoesNotExist", "method");
        var decision = EditStrategyResolver.Decide(
            "src/Foo.cs", fileExists: true, fileContent: "public class Foo { }",
            "rewrite DoesNotExist", intent);

        Assert.Equal(EditStrategy.AnchoredEdit, decision.Strategy);
        Assert.Contains("anchored", decision.Reason);
    }

    // ── AST symbol resolution — TS via Tree-sitter ───────────────────────────

    [Fact]
    public void Decide_TypeScript_InsertNearSymbol_ResolvesViaTreeSitter()
    {
        var source = """
            export class RecipeComponent {
              ngOnInit(): void {
                this.loadRecipes();
              }
            }
            """;
        var intent = new EditIntent(EditIntentKind.InsertNearSymbol, "ngOnInit", "method");
        var decision = EditStrategyResolver.Decide(
            "src/app/recipe.component.ts", fileExists: true, fileContent: source,
            "add a new method after ngOnInit", intent);

        Assert.Equal(EditStrategy.InsertMethod, decision.Strategy);
        Assert.Equal("ngOnInit", decision.TargetName);
        Assert.NotNull(decision.ResolvedOldStr);
        Assert.Contains("ngOnInit", decision.ResolvedOldStr);
    }

    // ── Safe fallback ────────────────────────────────────────────────────────

    [Fact]
    public void Decide_NoSymbolHint_ReturnsAnchoredEdit()
    {
        var intent = new EditIntent(EditIntentKind.TargetedEdit, null, null);
        var decision = EditStrategyResolver.Decide(
            "src/Foo.cs", fileExists: true, fileContent: "public class Foo { }",
            "tweak the wording", intent);

        Assert.Equal(EditStrategy.AnchoredEdit, decision.Strategy);
    }

    [Fact]
    public void Decide_FormatCLanguage_InsertIntent_NoSymbol_FallsBackToAnchored()
    {
        // supportsFormatC is true, but a null symbol can never be AST-resolved.
        var intent = new EditIntent(EditIntentKind.InsertNearSymbol, null, "method");
        var decision = EditStrategyResolver.Decide(
            "src/Foo.cs", fileExists: true, fileContent: "public class Foo { }",
            "add a new method", intent);

        Assert.Equal(EditStrategy.AnchoredEdit, decision.Strategy);
    }
}
