using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Deterministic tests for <see cref="EditIntentClassifier.ClassifyAsync"/>.
/// The LLM itself is out of scope, but the JSON-extraction / fallback logic around it
/// is 100% deterministic and must never silently regress:
///   • valid kind strings map to the right EditIntentKind,
///   • unknown kinds, unparseable JSON, null raw output, and caller errors all fall
///     back to TargetedEdit instead of throwing.
/// </summary>
public class EditIntentClassifierTests
{
    private static Task<(string raw, string? error)> Stub(string raw, string? error = null) =>
        Task.FromResult((raw, error));

    [Fact]
    public async Task ClassifyAsync_ReplaceSymbol_MapsKindAndSymbol()
    {
        var intent = await EditIntentClassifier.ClassifyAsync(
            "rewrite the login method",
            "src/app/login.component.ts",
            (_, _, _) => Stub("""{"kind":"replace_symbol","symbol":"login","preferredKind":"method"}"""),
            default);

        Assert.Equal(EditIntentKind.ReplaceSymbol, intent.Kind);
        Assert.Equal("login", intent.Symbol);
        Assert.Equal("method", intent.PreferredKind);
    }

    [Fact]
    public async Task ClassifyAsync_InsertNearSymbol_MapsKind()
    {
        var intent = await EditIntentClassifier.ClassifyAsync(
            "add a new method",
            "src/app/login.component.ts",
            (_, _, _) => Stub("""{"kind":"insert_near_symbol","symbol":"ngOnInit"}"""),
            default);

        Assert.Equal(EditIntentKind.InsertNearSymbol, intent.Kind);
        Assert.Equal("ngOnInit", intent.Symbol);
    }

    [Fact]
    public async Task ClassifyAsync_AddProperty_MapsKind()
    {
        var intent = await EditIntentClassifier.ClassifyAsync(
            "add a property",
            "src/app/login.component.ts",
            (_, _, _) => Stub("""{"kind":"add_property","symbol":"IsOpen","preferredKind":"property"}"""),
            default);

        Assert.Equal(EditIntentKind.AddProperty, intent.Kind);
        Assert.Equal("IsOpen", intent.Symbol);
        Assert.Equal("property", intent.PreferredKind);
    }

    [Fact]
    public async Task ClassifyAsync_ValidJson_AbsentSymbol_YieldsNullSymbol()
    {
        // The JSON may legitimately omit the symbol key — must stay null, not throw.
        var intent = await EditIntentClassifier.ClassifyAsync(
            "add a new method",
            "src/app/login.component.ts",
            (_, _, _) => Stub("""{"kind":"insert_near_symbol"}"""),
            default);

        Assert.Equal(EditIntentKind.InsertNearSymbol, intent.Kind);
        Assert.Null(intent.Symbol);
    }

    [Fact]
    public async Task ClassifyAsync_ValidJson_NullSymbol_StaysNull()
    {
        var intent = await EditIntentClassifier.ClassifyAsync(
            "add a new method",
            "src/app/login.component.ts",
            (_, _, _) => Stub("""{"kind":"insert_near_symbol","symbol":null}"""),
            default);

        Assert.Equal(EditIntentKind.InsertNearSymbol, intent.Kind);
        Assert.Null(intent.Symbol);
    }

    [Fact]
    public async Task ClassifyAsync_TargetedEdit_ExplicitKind()
    {
        var intent = await EditIntentClassifier.ClassifyAsync(
            "tweak the wording",
            "src/app/login.component.ts",
            (_, _, _) => Stub("""{"kind":"targeted_edit"}"""),
            default);

        Assert.Equal(EditIntentKind.TargetedEdit, intent.Kind);
    }

    [Fact]
    public async Task ClassifyAsync_HTMLSwap_LlmSaysTargetedEdit_CorrectedToReplaceSymbol()
    {
        // The LLM may answer targeted_edit for "replace `b` with `group`" on an HTML file;
        // the DOM file type must deterministically correct it to ReplaceSymbol so the
        // resolver picks HtmlReplace (targeted single-line replace), not HtmlInsertBefore.
        var intent = await EditIntentClassifier.ClassifyAsync(
            "Replace `b` with `group` in the benchmark-item ngFor",
            "src/app/weaver/weaver.component.html",
            (_, _, _) => Stub("""{"kind":"targeted_edit","symbol":"b"}"""),
            default);

        Assert.Equal(EditIntentKind.ReplaceSymbol, intent.Kind);
        Assert.Equal("b", intent.Symbol);
    }

    [Fact]
    public async Task ClassifyAsync_CodeSwap_TargetedEditStaysTargetedEdit()
    {
        // On a code file the swap stays a tiny anchored edit — never a method rewrite.
        var intent = await EditIntentClassifier.ClassifyAsync(
            "Replace `b` with `group` in the benchmark-item ngFor",
            "src/app/foo/foo.component.ts",
            (_, _, _) => Stub("""{"kind":"targeted_edit","symbol":"b"}"""),
            default);

        Assert.Equal(EditIntentKind.TargetedEdit, intent.Kind);
    }

    [Fact]
    public async Task ClassifyAsync_HTMLNonSwap_TargetedEditStaysTargetedEdit()
    {
        // "tweak the wording" is not a swap — no correction fires on the HTML file.
        var intent = await EditIntentClassifier.ClassifyAsync(
            "tweak the wording of the empty state",
            "src/app/weaver/weaver.component.html",
            (_, _, _) => Stub("""{"kind":"targeted_edit"}"""),
            default);

        Assert.Equal(EditIntentKind.TargetedEdit, intent.Kind);
    }

    [Fact]
    public async Task ClassifyAsync_UnknownKind_FallsBackToTargetedEdit()
    {
        var intent = await EditIntentClassifier.ClassifyAsync(
            "do something",
            "src/app/login.component.ts",
            (_, _, _) => Stub("""{"kind":"banana","symbol":"x"}"""),
            default);

        Assert.Equal(EditIntentKind.TargetedEdit, intent.Kind);
    }

    [Fact]
    public async Task ClassifyAsync_InvalidJson_FallsBackToTargetedEdit()
    {
        var intent = await EditIntentClassifier.ClassifyAsync(
            "do something",
            "src/app/login.component.ts",
            (_, _, _) => Stub("this is not json {{{"),
            default);

        Assert.Equal(EditIntentKind.TargetedEdit, intent.Kind);
    }

    [Fact]
    public async Task ClassifyAsync_NullRaw_FallsBackToTargetedEdit()
    {
        var intent = await EditIntentClassifier.ClassifyAsync(
            "do something",
            "src/app/login.component.ts",
            (_, _, _) => Stub(""),
            default);

        Assert.Equal(EditIntentKind.TargetedEdit, intent.Kind);
    }

    [Fact]
    public async Task ClassifyAsync_CallerError_FallsBackToTargetedEdit()
    {
        var intent = await EditIntentClassifier.ClassifyAsync(
            "do something",
            "src/app/login.component.ts",
            (_, _, _) => Stub("""{"kind":"replace_symbol","symbol":"x"}""", error: "boom"),
            default);

        Assert.Equal(EditIntentKind.TargetedEdit, intent.Kind);
    }

    [Fact]
    public async Task ClassifyAsync_JsonWithSurroundingNoise_StillParses()
    {
        // The raw output often carries prose around the JSON object — ExtractFirstJsonObject
        // must pull the object out and the classifier must parse it.
        var noisy = """
            Here is my classification:

            {"kind": "replace_symbol", "symbol": "SaveAll", "preferredKind": "method"}

            I am confident in this.
            """;
        var intent = await EditIntentClassifier.ClassifyAsync(
            "rewrite save",
            "src/app/foo.component.ts",
            (_, _, _) => Stub(noisy),
            default);

        Assert.Equal(EditIntentKind.ReplaceSymbol, intent.Kind);
        Assert.Equal("SaveAll", intent.Symbol);
    }
}
