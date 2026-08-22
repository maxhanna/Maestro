using Xunit;
using Weaver;
using Weaver.Services;
using static Weaver.Services.AgentEditHeuristics;

namespace Weaver.UnitTests;

/// <summary>
/// Deterministic tests for <see cref="DeterministicEditGenerator"/> — the LLM-free
/// content synthesizer. Guards every generator: literal swaps (from→to and set-to
/// forms, quotes, units, multi-line), C# auto-properties, getter/setter pairs,
/// interfaces, TS/JS class members, the safe-decline paths, and the wiring through
/// <see cref="EditStrategyResolver.Decide"/> (ResolvedOldStr + ResolvedNewStr).
/// Where the apply pipeline is simulated, TryReplaceSafe performs the swap exactly
/// as the agent would.
/// </summary>
public class DeterministicEditGeneratorTests
{
    // ── Literal swap: "X from N to M" ────────────────────────────────────────

    [Fact]
    public void Swap_FromTo_Basic()
    {
        const string file = "const retryCount = 3;\nconst maxRetries = 10;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "change retryCount from 3 to 5");

        Assert.NotNull(edit);
        Assert.Equal(EditStrategy.AnchoredEdit, edit!.Strategy);
        Assert.Equal("const retryCount = 3;", edit.OldStr);
        Assert.Equal("const retryCount = 5;", edit.NewStr);
        Assert.Equal(1, edit.LineNumber);

        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("const retryCount = 5;", content);
        Assert.Contains("const maxRetries = 10;", content);
    }

    [Fact]
    public void Swap_FromTo_QuotedValue_PreservesQuotes()
    {
        const string file = "maxRetries: \"3\",\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "appsettings.json", true, file, "change maxRetries from 3 to 5");

        Assert.NotNull(edit);
        Assert.Equal("maxRetries: \"3\",", edit!.OldStr);
        Assert.Equal("maxRetries: \"5\",", edit.NewStr);
    }

    // ── Quoted JSON keys: '"maxRetries": 3' is a key:value pair, not string content ──

    [Fact]
    public void Swap_SetTo_QuotedJsonKey_UnquotedValue()
    {
        const string file =
            "{\n" +
            "  \"maxRetries\": 3,\n" +
            "  \"timeoutSec\": 30\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "appsettings.json", true, file, "update all maxRetries defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits);
        Assert.Equal("  \"maxRetries\": 3,", edit.Edits[0].OldString); // the full indented line
        Assert.Equal("  \"maxRetries\": 5,", edit.Edits[0].NewString);
        Assert.Contains("applied 1/1 occurrences", edit.Reason);
    }

    [Fact]
    public void Swap_SetTo_QuotedJsonKey_QuotedValue()
    {
        const string file = "\"maxRetries\": \"3\",\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "appsettings.json", true, file, "update all maxRetries defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits);
        Assert.Equal("\"maxRetries\": \"5\",", edit.Edits[0].NewString);
    }

    [Fact]
    public void Swap_FromTo_QuotedJsonKey()
    {
        // The MULTI from-to form ("all ... from 3 to 5") — the quoted-key form lives in
        // ContainsStandaloneName, which only the multi-swap path consults.
        const string file = "\"maxRetries\": 3,\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "appsettings.json", true, file, "update all maxRetries from 3 to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits);
        Assert.Equal("\"maxRetries\": 5,", edit.Edits[0].NewString);
    }

    [Fact]
    public void Swap_QuotedJsonKey_AllOccurrencesUpdated()
    {
        const string file =
            "\"maxRetries\": 3,\n" +
            "\"maxRetries\": 3\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "appsettings.json", true, file, "update all maxRetries defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("applied 2/2 occurrences", edit.Reason);
        Assert.Contains("(deterministic batch: 2 edits, applied 2/2 occurrences)", edit.NewStr);
    }

    [Fact]
    public void Swap_QuotedKey_NonJsonFile_Declines()
    {
        // The quoted-key form is gated to JSON-family files: the same text in a .ts file could
        // live inside a string/template literal ('"maxRetries": 3' as literal content), so it
        // must still decline — the LLM handles it instead of editing string content.
        const string file = "\"maxRetries\": 3,\n";
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all maxRetries defaults to 5"));
    }

    [Fact]
    public void Swap_QuotedName_NotAKeyPair_Declines()
    {
        // An array element (or any quoted identifier NOT followed by ':') stays string content.
        const string file = "[\"maxRetries\", \"other\"]\n";
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "appsettings.json", true, file, "update all maxRetries defaults to 5"));
    }

    [Fact]
    public void Swap_QuotedKey_ObjectValue_Declines()
    {
        // An object-valued key must NOT be swapped: its nested literals are not the key's value,
        // so the whole request declines (safe) rather than mis-editing 'count' inside the object.
        const string file = "\"retry\": { \"count\": 3 }\n";
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "appsettings.json", true, file, "update all retry defaults to 5"));
    }

    [Fact]
    public void Swap_FromTo_UnitSuffix_PreservesUnit()
    {
        const string file = "  font-size: 12px;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "style.css", true, file, "change the font size from 12 to 14");

        Assert.NotNull(edit);
        Assert.Equal("  font-size: 12px;", edit!.OldStr);
        Assert.Equal("  font-size: 14px;", edit.NewStr);
    }

    [Fact]
    public void Swap_FromTo_ValueOnNextLine_SpansBothLines()
    {
        const string file = "const timeout =\n  30;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "change timeout from 30 to 60");

        Assert.NotNull(edit);
        Assert.Equal("const timeout =\n  30;", edit!.OldStr);
        Assert.Equal("const timeout =\n  60;", edit.NewStr);
        Assert.Equal(1, edit.LineNumber);

        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("const timeout =\n  60;", content);
    }

    // ── Literal swap: "set X to M" (current value read from the file) ────────

    [Fact]
    public void Swap_SetTo_ReadsCurrentValue()
    {
        const string file = "const port = 3000;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "server.ts", true, file, "set port to 4000");

        Assert.NotNull(edit);
        Assert.Equal("const port = 3000;", edit!.OldStr);
        Assert.Equal("const port = 4000;", edit.NewStr);
        Assert.Contains("Literal swap", edit.Reason);
    }

    [Fact]
    public void Swap_SetTo_Bool()
    {
        const string file = "let debug = false;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "main.ts", true, file, "set debug to true");

        Assert.NotNull(edit);
        Assert.Equal("let debug = false;", edit!.OldStr);
        Assert.Equal("let debug = true;", edit.NewStr);
    }

    [Fact]
    public void Swap_SetTo_AlreadyTarget_Declines()
    {
        const string file = "const retryCount = 5;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "set retryCount to 5");
        Assert.Null(edit); // already the target — a no-op must not be generated
    }

    [Fact]
    public void Swap_SetTo_MultipleLiteralsOnLine_ReadsAfterName()
    {
        const string file = "const min = 1, max = 5;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "set max to 8");

        Assert.NotNull(edit);
        Assert.Equal("const min = 1, max = 5;", edit!.OldStr);
        Assert.Equal("const min = 1, max = 8;", edit.NewStr);
    }

    // ── Literal swap: safe declines ──────────────────────────────────────────

    [Fact]
    public void Swap_NameMissingInFile_Declines()
    {
        const string file = "const other = 3;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "change bogusName from 3 to 5");
        Assert.Null(edit);
    }

    [Fact]
    public void Swap_FromValueNotPresent_Declines()
    {
        const string file = "const retryCount = 9;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "change retryCount from 3 to 5");
        Assert.Null(edit); // from-value (3) doesn't match the file's actual value (9) — don't guess
    }

    [Fact]
    public void Swap_RemovalWording_Declines()
    {
        const string file = "const retryCount = 3;\n";
        // "delete/remove X from N to M" must route to DeleteLines, never a swap.
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "delete retryCount from 3 to 5"));
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "remove the timeout from 30 to 60"));
    }

    [Fact]
    public void Swap_NameInsideStringOrComment_Declines()
    {
        const string file =
            "const msg = 'retryCount = 3';\n" +
            "// retryCount = 3\n" +
            "const retryCount = 9;\n";
        // The name+value only appear inside a string and a comment — the generator
        // must not swap those; the real variable has a different value (9), so decline.
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "change retryCount from 3 to 5");
        Assert.Null(edit);
    }

    [Fact]
    public void Swap_IncreaseTo_Works()
    {
        const string file = "const timeout = 30;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "increase the timeout to 60");
        Assert.NotNull(edit);
        Assert.Equal("const timeout = 30;", edit!.OldStr);
        Assert.Equal("const timeout = 60;", edit.NewStr);
    }

    [Fact]
    public void Swap_HtmlFile_Declines()
    {
        const string file = "<div class=\"card\">3</div>\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "index.html", true, file, "change retryCount from 3 to 5");
        Assert.Null(edit);
    }

    [Fact]
    public void Swap_MissingFile_Declines()
    {
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", false, "", "change retryCount from 3 to 5");
        Assert.Null(edit);
    }

    // ── C# property addition ─────────────────────────────────────────────────

    private const string CsClass =
        "public class User\n" +
        "{\n" +
        "    public string Name { get; set; }\n" +
        "}\n";

    [Fact]
    public void Property_CSharp_AutoProperty_AnchorsAtClassBody()
    {
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/User.cs", true, CsClass, "add a string Email property");

        Assert.NotNull(edit);
        Assert.Equal(EditStrategy.FillClassBody, edit!.Strategy);
        Assert.Equal("    public string Name { get; set; }\n}", edit.OldStr);
        Assert.Contains("public string Email { get; set; }", edit.NewStr);
        Assert.Equal(4, edit.LineNumber);

        var (replaced, content, _, _) = TryReplaceSafe(CsClass, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Equal(
            "public class User\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "    public string Email { get; set; }\n" +
            "}\n", content);
    }

    [Fact]
    public void Property_CSharp_TypeInferredFromName()
    {
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/User.cs", true, CsClass, "add an IsActive property");
        Assert.NotNull(edit);
        Assert.Contains("public bool IsActive { get; set; }", edit!.NewStr);
    }

    [Fact]
    public void Property_CSharp_GetterSetterPair()
    {
        const string file =
            "public class Timer\n" +
            "{\n" +
            "    public int Interval { get; set; }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Services/Timer.cs", true, file, "add a getter and setter for Timeout");

        Assert.NotNull(edit);
        Assert.Contains("private int _timeout;", edit!.NewStr);
        Assert.Contains("public int Timeout", edit.NewStr);
        Assert.Contains("get { return _timeout; }", edit.NewStr);
        Assert.Contains("set { _timeout = value; }", edit.NewStr);

        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("private int _timeout;", content);
        Assert.Contains("public int Timeout", content);
    }

    [Fact]
    public void Property_CSharp_Interface_NoAccessModifier()
    {
        const string file =
            "public interface IThing\n" +
            "{\n" +
            "    int Count { get; set; }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Contracts/IThing.cs", true, file, "add a string Label property to the IThing interface");

        Assert.NotNull(edit);
        Assert.Contains("string Label { get; set; }", edit!.NewStr);
        Assert.DoesNotContain("public string Label", edit.NewStr);
    }

    [Fact]
    public void Property_CSharp_MultiClass_AnchorsNamedClass()
    {
        const string file =
            "public class User\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "}\n" +
            "\n" +
            "public class Profile\n" +
            "{\n" +
            "    public string Bio { get; set; }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/User.cs", true, file, "add a string Tagline property to the Profile class");

        Assert.NotNull(edit);
        Assert.Contains("public string Tagline { get; set; }", edit!.NewStr);
        Assert.Contains("public string Bio { get; set; }\n    public string Tagline { get; set; }", edit.NewStr);
        Assert.Equal(9, edit.LineNumber); // Profile's close brace
    }

    [Fact]
    public void Property_CSharp_SingleLineClass_Declines()
    {
        const string file = "public class User { public string Name { get; set; } }\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/User.cs", true, file, "add a string Email property");
        Assert.Null(edit); // one-line class body can't be anchored safely
    }

    // ── TS/JS member addition ────────────────────────────────────────────────

    [Fact]
    public void Property_Ts_ClassMember_WithDefault()
    {
        const string file = "export class User {\n  name = '';\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/user.ts", true, file, "add an Age property to the User class");

        Assert.NotNull(edit);
        Assert.Contains("public age: number = 0;", edit!.NewStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public age: number = 0;", content);
    }

    [Fact]
    public void Property_Ts_InterfaceMember()
    {
        const string file = "export interface User {\n  name: string;\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/user.ts", true, file, "add an email property to the User interface");

        Assert.NotNull(edit);
        Assert.Contains("email: string;", edit!.NewStr);
        Assert.DoesNotContain("public", edit.NewStr);
    }

    [Fact]
    public void Property_Js_ClassField()
    {
        const string file = "export class User {\n  constructor() {}\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/user.js", true, file, "add a name property");

        Assert.NotNull(edit);
        Assert.Contains("name = '';", edit!.NewStr);
    }

    [Fact]
    public void Property_Ts_TemplateLiteralInClass_DoesNotConfuseBraceMatching()
    {
        const string file = "export class Greeter {\n  greeting = `Hello ${world()}`;\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/greeter.ts", true, file, "add a string Prefix property");

        Assert.NotNull(edit);
        Assert.Contains("public prefix: string = '';", edit!.NewStr);
        // G2: the anchor is widened past a lone '}' to the preceding member line —
        // the template literal (and its ${...}) must not confuse either the brace
        // matching or the widened anchor.
        Assert.Equal("  greeting = `Hello ${world()}`;\n}", edit.OldStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public prefix: string = '';", content);
    }

    // ── G2: widened TS/JS anchors · G3: unnamed multi-type declines ───────────

    [Fact]
    public void Property_Ts_Anchor_WidenedToIncludeMemberLine()
    {
        const string file = "export class User {\n  name = '';\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/user.ts", true, file, "add an Age property to the User class");

        Assert.NotNull(edit);
        Assert.Equal("  name = '';\n}", edit!.OldStr);
        Assert.Equal("  name = '';\n  public age: number = 0;\n}", edit.NewStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public age: number = 0;", content);
    }

    [Fact]
    public void Property_Ts_EmptyClass_AnchorsOnClassOpenLine()
    {
        const string file = "export class Foo {\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/foo.ts", true, file, "add a string Label property to the Foo class");

        Assert.NotNull(edit);
        Assert.Equal("export class Foo {\n}", edit!.OldStr); // class-open line is the widest anchor an empty body can carry
        Assert.Contains("public label: string = '';", edit.NewStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public label: string = '';", content);
    }

    [Fact]
    public void Property_Ts_NestedBlockLastMember_AnchorCarriesMemberClose()
    {
        const string file =
            "export class Foo {\n" +
            "  method() {\n" +
            "    return 1;\n" +
            "  }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/foo.ts", true, file, "add a string Label property to the Foo class");

        Assert.NotNull(edit);
        // With brace-depth awareness, the scan skips lines inside the method body
        // (depth > 0) and anchors on the method declaration — a class-level line.
        Assert.Equal("  method() {\n    return 1;\n  }\n}", edit!.OldStr);
        Assert.Contains("public label: string = '';", edit.NewStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public label: string = '';", content);
    }

    [Fact]
    public void Property_Ts_TrailingBlankLineBeforeClose_AnchorStaysContiguous()
    {
        const string file = "export class Foo {\n  name = '';\n\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/foo.ts", true, file, "add a string Label property to the Foo class");

        Assert.NotNull(edit);
        // The blank line before '}' stays INSIDE the anchor — the oldStr must be a
        // contiguous slice of the file, or TryReplaceSafe can never match it.
        Assert.Equal("  name = '';\n\n}", edit!.OldStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public label: string = '';", content);
    }

    [Fact]
    public void Property_Ts_CommentOnlyBody_AnchorIncludesComment()
    {
        const string file = "export class Foo {\n  // placeholder\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/foo.ts", true, file, "add a string Label property to the Foo class");

        Assert.NotNull(edit);
        // No members — the anchor falls back to the class-open line and keeps the
        // comment line in the middle, still contiguous with the close brace.
        Assert.Equal("export class Foo {\n  // placeholder\n}", edit!.OldStr);
        Assert.Contains("public label: string = '';", edit.NewStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public label: string = '';", content);
    }

    [Fact]
    public void Property_Ts_DocCommentClassMention_DoesNotInflateDeclCount()
    {
        const string file =
            "// @class LegacyUser kept for the migration\n" +
            "export class User {\n  name = '';\n}\n";
        // The JSDoc '@class LegacyUser' mention must not count toward the G3 ambiguity
        // gate — only real declarations do — so the unnamed add still anchors User.
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/user.ts", true, file, "add an Age property");

        Assert.NotNull(edit);
        Assert.Contains("public age: number = 0;", edit!.NewStr);
        Assert.Contains("name = '';", edit.OldStr);
    }

    [Fact]
    public void Property_Ts_MultiClass_Unnamed_Declines()
    {
        const string file =
            "export class User {\n  name = '';\n}\n" +
            "export class Profile {\n  bio = '';\n}\n";
        // No class named in the description + two classes = ambiguous → decline.
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "app/users.ts", true, file, "add an Email property"));
    }

    [Fact]
    public void Property_Ts_MultiClass_Named_AnchorsNamedClass()
    {
        const string file =
            "export class User {\n  name = '';\n}\n" +
            "export class Profile {\n  bio = '';\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/users.ts", true, file, "add an Age property to the Profile class");

        Assert.NotNull(edit);
        Assert.Contains("public age: number = 0;", edit!.NewStr);
        Assert.Contains("bio = '';\n  public age: number = 0;", edit.NewStr);
        Assert.Equal(6, edit.LineNumber); // Profile's close brace
    }

    // ── Sibling-aware placement: a new member lands next to its name-similar peers ──

    [Fact]
    public void Property_Ts_SiblingAware_PlacesNextToSimilarMember()
    {
        // The exact failure from the movie-count task: without sibling awareness the
        // generator anchored after the LAST member (hexWithAlpha at the end of the class),
        // not next to musicTodoCount where the member belongs.
        const string file =
            "export class Navigation {\n" +
            "  musicTodoCount: number | null = null;\n" +
            "  arrayActivePlayers: number | null = null;\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/navigation.ts", true, file, "add a movieTodoCount property");

        Assert.NotNull(edit);
        Assert.Equal("  musicTodoCount: number | null = null;\n  arrayActivePlayers: number | null = null;", edit!.OldStr);
        Assert.Equal(
            "  musicTodoCount: number | null = null;\n" +
            "  public movieTodoCount: number = 0;\n" +
            "  arrayActivePlayers: number | null = null;", edit.NewStr);
        Assert.Equal(2, edit.LineNumber); // the sibling's line, not the class close brace
        Assert.Contains("next to 'musicTodoCount'", edit.Reason);

        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("musicTodoCount: number | null = null;\n  public movieTodoCount: number = 0;", content);
    }

    [Fact]
    public void Property_Ts_SiblingAware_LastMemberBeforeClose_AnchorsOnSiblingAndBrace()
    {
        const string file =
            "export class Navigation {\n" +
            "  musicTodoCount: number | null = null;\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/navigation.ts", true, file, "add a movieTodoCount property");

        Assert.NotNull(edit);
        Assert.Equal("  musicTodoCount: number | null = null;\n}", edit!.OldStr);
        Assert.Equal(
            "  musicTodoCount: number | null = null;\n" +
            "  public movieTodoCount: number = 0;\n}", edit.NewStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("musicTodoCount: number | null = null;\n  public movieTodoCount: number = 0;\n}", content);
    }

    [Fact]
    public void Property_Ts_SiblingAware_MethodBodyAssignment_NotTreatedAsSibling()
    {
        // An assignment `this.movieTodoCount = 0` inside a method body shares the new
        // member's name verbatim — the depth-aware scan must skip it and still land on
        // the class-level declaration, not the method body line.
        const string file =
            "export class Navigation {\n" +
            "  load() {\n" +
            "    this.movieTodoCount = 0;\n" +
            "  }\n" +
            "  musicTodoCount: number | null = null;\n" +
            "  arrayActivePlayers: number | null = null;\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/navigation.ts", true, file, "add a movieTodoCount property");

        Assert.NotNull(edit);
        Assert.Equal("  musicTodoCount: number | null = null;\n  arrayActivePlayers: number | null = null;", edit!.OldStr);
        Assert.DoesNotContain("this.movieTodoCount", edit.OldStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public movieTodoCount: number = 0;", content);
    }

    [Fact]
    public void Property_Ts_SiblingAware_NoSimilarMember_FallsBackToEndOfClass()
    {
        const string file = "export class User {\n  name = '';\n  bio = '';\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/user.ts", true, file, "add an Age property to the User class");

        Assert.NotNull(edit);
        // 'age' shares no word with 'name'/'bio' → the end-of-class anchor is preserved.
        Assert.Equal("  bio = '';\n}", edit!.OldStr);
        Assert.Contains("  bio = '';\n  public age: number = 0;\n}", edit.NewStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public age: number = 0;", content);
    }

    [Fact]
    public void Property_CSharp_SiblingAware_PlacesNextToSimilarMember()
    {
        const string file =
            "public class Navigation\n" +
            "{\n" +
            "    public int MusicTodoCount { get; set; }\n" +
            "    public int ArrayActivePlayers { get; set; }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Navigation.cs", true, file, "add an int MovieTodoCount property");

        Assert.NotNull(edit);
        Assert.Equal("    public int MusicTodoCount { get; set; }\n    public int ArrayActivePlayers { get; set; }", edit!.OldStr);
        Assert.Equal(
            "    public int MusicTodoCount { get; set; }\n" +
            "    public int MovieTodoCount { get; set; }\n" +
            "    public int ArrayActivePlayers { get; set; }", edit.NewStr);
        Assert.Equal(3, edit.LineNumber); // the sibling's line
        Assert.Contains("next to 'MusicTodoCount'", edit.Reason);

        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public int MusicTodoCount { get; set; }\n    public int MovieTodoCount { get; set; }", content);
    }

    [Fact]
    public void Property_CSharp_SiblingAware_MultiLineMethod_NotAnAnchor()
    {
        const string file =
            "public class Navigation\n" +
            "{\n" +
            "    public void Load()\n" +
            "    {\n" +
            "        MovieTodoCount = 0;\n" +
            "    }\n" +
            "    public int MusicTodoCount { get; set; }\n" +
            "    public int ArrayActivePlayers { get; set; }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Navigation.cs", true, file, "add an int MovieTodoCount property");

        Assert.NotNull(edit);
        // The multi-line Load() method can't anchor, and its body assignment must not
        // count — the single-line MusicTodoCount declaration is the sibling.
        Assert.Equal("    public int MusicTodoCount { get; set; }\n    public int ArrayActivePlayers { get; set; }", edit!.OldStr);
        var (replaced, content, _, _) = TryReplaceSafe(file, edit.OldStr!, edit.NewStr!, edit.LineNumber);
        Assert.True(replaced);
        Assert.Contains("public int MovieTodoCount { get; set; }", content);
    }

    [Fact]
    public void Property_CSharp_MultiClass_Unnamed_Declines()
    {
        const string file =
            "public class User\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "}\n" +
            "\n" +
            "public class Profile\n" +
            "{\n" +
            "    public string Bio { get; set; }\n" +
            "}\n";
        // No class named in the description + two classes = ambiguous → decline.
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "Models/User.cs", true, file, "add a string Email property"));
    }

    [Fact]
    public void Property_CSharp_ClassPlusInterface_Unnamed_Declines()
    {
        const string file =
            "public class User\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "}\n" +
            "\n" +
            "public interface IUser\n" +
            "{\n" +
            "    int Id { get; set; }\n" +
            "}\n";
        // A class AND an interface both present → unnamed add is ambiguous → decline.
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "Models/User.cs", true, file, "add a string Email property"));
    }

    [Fact]
    public void Property_CSharp_SingleInterface_Unnamed_StillAnchors()
    {
        const string file =
            "public interface IThing\n" +
            "{\n" +
            "    int Count { get; set; }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Contracts/IThing.cs", true, file, "add a string Label property");

        Assert.NotNull(edit);
        Assert.Contains("string Label { get; set; }", edit!.NewStr);
    }

    // ── Wiring through EditStrategyResolver.Decide ───────────────────────────

    [Fact]
    public void Decide_ChangeLiteral_ReturnsResolvedOldAndNew()
    {
        const string file = "const retryCount = 3;\n";
        var intent = new EditIntent(EditIntentKind.TargetedEdit, null, null);
        var decision = EditStrategyResolver.Decide(
            "config.ts", true, file, "change retryCount from 3 to 5", intent);

        Assert.Equal(EditStrategy.AnchoredEdit, decision.Strategy);
        Assert.NotNull(decision.ResolvedOldStr);
        Assert.NotNull(decision.ResolvedNewStr);
        Assert.Equal("const retryCount = 3;", decision.ResolvedOldStr);
        Assert.Equal("const retryCount = 5;", decision.ResolvedNewStr);
        Assert.Contains("Literal swap", decision.Reason);
    }

    [Fact]
    public void Decide_AddProperty_ReturnsFullyResolvedFillClassBody()
    {
        var intent = new EditIntent(EditIntentKind.AddProperty, null, null);
        var decision = EditStrategyResolver.Decide(
            "Models/User.cs", true, CsClass, "add a string Email property", intent);

        Assert.Equal(EditStrategy.FillClassBody, decision.Strategy);
        Assert.NotNull(decision.ResolvedOldStr);
        Assert.NotNull(decision.ResolvedNewStr);
        Assert.Contains("public string Email { get; set; }", decision.ResolvedNewStr);
    }

    [Fact]
    public void Decide_NonDeterministic_StillFallsBackToAnchoredEdit()
    {
        const string file = "export function add(a: number, b: number) {\n  return a + b;\n}\n";
        var intent = new EditIntent(EditIntentKind.ReplaceSymbol, "add", "method");
        var decision = EditStrategyResolver.Decide(
            "math.ts", true, file, "rewrite the add function to also log", intent);

        // Not a deterministic pattern → normal AST resolution path still works.
        Assert.NotEqual(EditStrategy.AnchoredEdit, decision.Strategy);
    }

    // ── Multi-class member add: "add an Email property to every DTO class" ────────

    private const string MultiDtoFile =
        "public class UserDto\n" +
        "{\n" +
        "    public string Name { get; set; }\n" +
        "}\n" +
        "public class OrderDto\n" +
        "{\n" +
        "    public decimal Total { get; set; }\n" +
        "}\n" +
        "public class User\n" +
        "{\n" +
        "    public int Id { get; set; }\n" +
        "}\n";

    private const string MultiTsFile =
        "export class UserDto {\n" +
        "  name = '';\n" +
        "}\n" +
        "export class OrderDto {\n" +
        "  total = 0;\n" +
        "}\n" +
        "export interface IMeta {\n" +
        "  created: Date;\n" +
        "}\n";

    [Fact]
    public void MultiMember_CSharp_OneEditPerMatchingDtoClass()
    {
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile, "add a string Email property to every DTO class");

        Assert.NotNull(edit);
        Assert.Equal(EditStrategy.FillClassBody, edit!.Strategy);
        Assert.NotNull(edit.Edits);
        // UserDto + OrderDto get the property; User (no "DTO") is filtered out.
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("deterministic batch: 2 edits", edit.NewStr);
        Assert.Contains("(deterministic batch: 2 edits, applied 2/2 classes)", edit.NewStr);
        Assert.Contains("applied 2/2", edit.Reason);
        Assert.Equal(4, edit.Edits[0].LineNumber); // UserDto close brace
        Assert.Equal(8, edit.Edits[1].LineNumber); // OrderDto close brace
        Assert.All(edit.Edits, e => Assert.Contains("public string Email { get; set; }", e.NewString));

        // Each class body anchor is distinct — applying both in order works end-to-end.
        var content = MultiDtoFile;
        foreach (var e in edit.Edits)
        {
            var (replaced, nc, _, _) = TryReplaceSafe(content, e.OldString, e.NewString, e.LineNumber);
            Assert.True(replaced);
            content = nc;
        }
        Assert.Contains("public class UserDto", content);
        Assert.Contains("public class OrderDto", content);
        Assert.Equal(2, content.Split("public string Email { get; set; }").Length - 1);
    }

    [Fact]
    public void MultiMember_CSharp_UnanchorableSingleLineClass_SkippedNotSilentlyEdited()
    {
        const string file =
            "public class OneDto { public int X { get; set; } }\n" +
            "public class TwoDto\n" +
            "{\n" +
            "    public int Y { get; set; }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, file, "add an Email property to every DTO class");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits); // only the anchorable class gets edited
        Assert.Equal(5, edit.Edits[0].LineNumber);
        Assert.Contains("applied 1/2 matching classes", edit.Reason);
        Assert.Contains("skipped 1 unanchorable", edit.Reason);
    }

    [Fact]
    public void MultiMember_CSharp_NoMatchingClass_Declines()
    {
        // No class name contains "DTO" → the whole multi request declines, never
        // degrading to a single-class edit.
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "Models/Entities.cs", true, MultiDtoFile, "add a string Email property to every Entity class"));
    }

    [Fact]
    public void MultiMember_CSharp_SingleMatchingClass_StillBatches()
    {
        const string file =
            "public class UserDto\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, file, "add a string Email property to every DTO class");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits);
        Assert.Contains("applied 1/1 matching class", edit.Reason);
    }

    [Fact]
    public void MultiMember_CSharp_PluralInFileForm_NormalizesKind()
    {
        // "the DTO classes in this file" — the PLURAL spec form (kind2 group) whose
        // de-pluralization must NOT mangle the kind ("class".TrimEnd('s') → "clas" bug).
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile, "add a string Email property to the DTO classes in this file");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count); // both DTO classes — kind normalized back to "class"
        Assert.Contains("matching classes", edit.Reason);
        Assert.Contains("deterministic batch: 2 edits", edit.NewStr);
    }

    [Fact]
    public void MultiMember_CSharp_IdenticalAnchors_DisambiguatedByLineNumber()
    {
        // Two structurally identical classes — same last member line + same close brace
        // produce IDENTICAL oldStrings. The batch path disambiguates via each edit's
        // LineNumber hint (position-aware TryReplaceSafe), never silently collapsing.
        const string file =
            "public class OneDto\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "}\n" +
            "public class TwoDto\n" +
            "{\n" +
            "    public string Name { get; set; }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, file, "add a string Email property to every DTO class");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Equal(edit.Edits[0].OldString, edit.Edits[1].OldString); // identical anchor text
        Assert.Equal(4, edit.Edits[0].LineNumber);
        Assert.Equal(8, edit.Edits[1].LineNumber);

        // Both apply cleanly with their line hints — each class gets the property.
        var content = file;
        foreach (var e in edit.Edits)
        {
            var (replaced, nc, _, _) = TryReplaceSafe(content, e.OldString, e.NewString, e.LineNumber);
            Assert.True(replaced);
            content = nc;
        }
        Assert.Equal(2, content.Split("public string Email { get; set; }").Length - 1);
    }

    [Fact]
    public void MultiMember_CSharp_PerClassOverride_FirstNameKey()
    {
        // "... but NameKey on the first one" — the FIRST matching class gets a differently
        // named member; every other class keeps the description's base name (Email).
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile,
            "add a string Email property to every DTO class, but NameKey on the first one");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("public string NameKey { get; set; }", edit.Edits[0].NewString); // UserDto — first
        Assert.Contains("public string Email { get; set; }", edit.Edits[1].NewString);  // OrderDto — base
        Assert.Contains("'string NameKey' on the first", edit.Reason);

        // Both apply cleanly; the override lands in the earlier class, base name in the later.
        var content = MultiDtoFile;
        foreach (var e in edit.Edits)
        {
            var (replaced, nc, _, _) = TryReplaceSafe(content, e.OldString, e.NewString, e.LineNumber);
            Assert.True(replaced);
            content = nc;
        }
        Assert.Contains("public string NameKey { get; set; }", content);
        Assert.Contains("public string Email { get; set; }", content);
        Assert.True(content.IndexOf("public string NameKey", StringComparison.Ordinal)
                    < content.IndexOf("public string Email", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiMember_CSharp_PerClassOverride_LastName()
    {
        // "... but NameKey on the last one" — the override applies to the LAST matching class.
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile,
            "add a string Email property to every DTO class, but NameKey on the last one");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("public string Email { get; set; }", edit.Edits[0].NewString);
        Assert.Contains("public string NameKey { get; set; }", edit.Edits[1].NewString);
        Assert.Contains("'string NameKey' on the last", edit.Reason);
    }

    [Fact]
    public void MultiMember_CSharp_PerClassOverride_OutOfRange_Declines()
    {
        // "the fourth one" can't be honored with only two matching classes — a multi request
        // must never silently drop the override, so the whole thing declines to the LLM.
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile,
            "add a string Email property to every DTO class, but NameKey on the fourth one"));
    }

    [Fact]
    public void MultiMember_CSharp_PerClassOverride_SecondClauseForm()
    {
        // Same clause, worded "but on the first one NameKey" — the second alternative form.
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile,
            "add a string Email property to every DTO class, but on the first one NameKey");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("public string NameKey { get; set; }", edit.Edits[0].NewString);
        Assert.Contains("public string Email { get; set; }", edit.Edits[1].NewString);
    }

    [Fact]
    public void MultiMember_CSharp_NonOverrideBut_Ignored()
    {
        // "but skip the OrderDto" is NOT an override clause — the "but" is a different sense.
        // No override may fire: both classes keep the base name, no "on the first" in Reason.
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile,
            "add a string Email property to every DTO class, but skip the OrderDto");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.All(edit.Edits, e => Assert.Contains("public string Email { get; set; }", e.NewString));
        Assert.DoesNotContain("on the first", edit.Reason);
        Assert.DoesNotContain("on the last", edit.Reason);
    }

    [Fact]
    public void MultiMember_CSharp_PerClassOverride_NameStartingWithOn_NotBlocked()
    {
        // A name that STARTS with an article-ish prefix ("OnTime") must still be honored —
        // the exclusion list only bans the bare words "on/the/one/a/an", not longer names.
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile,
            "add a string Email property to every DTO class, but OnTime on the first one");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("public string OnTime { get; set; }", edit.Edits[0].NewString);
        Assert.Contains("public string Email { get; set; }", edit.Edits[1].NewString);
    }

    [Fact]
    public void MultiMember_CSharp_PerClassOverride_UnanchorableOverride_Declines()
    {
        // The "first one" is a single-line class (unanchorable) — honoring the NameKey intent
        // would be impossible, so the whole multi request declines instead of silently dropping it.
        const string file =
            "public class OneDto { public int X { get; set; } }\n" +
            "public class TwoDto\n" +
            "{\n" +
            "    public int Y { get; set; }\n" +
            "}\n";
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, file,
            "add an Email property to every DTO class, but NameKey on the first one"));
    }

    // ── Class-set narrowing: suffix / prefix / exclusion filters ─────────────────

    private const string RepoFile =
        "public class UserRepository\n" +
        "{\n" +
        "    public User Find(int id) => new();\n" +
        "}\n" +
        "public class OrderRepository\n" +
        "{\n" +
        "    public Order Find(int id) => new();\n" +
        "}\n" +
        "public class UserService\n" +
        "{\n" +
        "    public void Run() { }\n" +
        "}\n";

    private const string ExclusionFile =
        "public class BaseDto\n" +
        "{\n" +
        "    public int Id { get; set; }\n" +
        "}\n" +
        "public class UserDto\n" +
        "{\n" +
        "    public string Name { get; set; }\n" +
        "}\n" +
        "public class OrderDto\n" +
        "{\n" +
        "    public decimal Total { get; set; }\n" +
        "}\n";

    [Fact]
    public void MultiMember_CSharp_SuffixFilter_EndingInRepository()
    {
        // "all classes ending in Repository" — only names carrying the suffix are edited;
        // UserService (no suffix) is left alone.
        var edit = DeterministicEditGenerator.TryGenerate(
            "Data/Repos.cs", true, RepoFile,
            "add a string Audit property to all classes ending in Repository");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.All(edit.Edits, e => Assert.Contains("public string Audit { get; set; }", e.NewString));
        Assert.Contains("ending in 'Repository'", edit.Reason);

        var content = RepoFile;
        foreach (var e in edit.Edits)
        {
            var (replaced, nc, _, _) = TryReplaceSafe(content, e.OldString, e.NewString, e.LineNumber);
            Assert.True(replaced);
            content = nc;
        }
        Assert.Equal(2, content.Split("public string Audit { get; set; }").Length - 1);
        Assert.DoesNotContain("public string Audit { get; set; }", content.Substring(content.IndexOf("public class UserService", StringComparison.Ordinal)));
    }

    [Fact]
    public void MultiMember_CSharp_PrefixFilter_StartingWith()
    {
        // "every class starting with User" — UserDto + User get the member; OrderDto doesn't.
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile,
            "add a string Email property to every class starting with User");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.All(edit.Edits, e => Assert.Contains("public string Email { get; set; }", e.NewString));
        Assert.Contains("starting with 'User'", edit.Reason);
    }

    [Fact]
    public void MultiMember_CSharp_Exclusion_ExceptBaseClass()
    {
        // "every DTO class except the base one" — BaseDto matches the DTO filter but is
        // dropped by the exclusion; UserDto + OrderDto get the member.
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, ExclusionFile,
            "add a string Email property to every DTO class except the base one");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("excluding 'base'", edit.Reason);
        Assert.Contains("applied 2/2", edit.Reason); // applied/total count the post-filter set

        var content = ExclusionFile;
        foreach (var e in edit.Edits)
        {
            var (replaced, nc, _, _) = TryReplaceSafe(content, e.OldString, e.NewString, e.LineNumber);
            Assert.True(replaced);
            content = nc;
        }
        Assert.Equal(2, content.Split("public string Email { get; set; }").Length - 1);
        // BaseDto's block is untouched — its Email count stays zero.
        var baseBlock = content.Substring(content.IndexOf("public class BaseDto", StringComparison.Ordinal),
            content.IndexOf("public class UserDto", StringComparison.Ordinal) - content.IndexOf("public class BaseDto", StringComparison.Ordinal));
        Assert.DoesNotContain("Email", baseBlock);
    }

    [Fact]
    public void MultiMember_CSharp_Exclusion_OneNamedForm()
    {
        // "except the one named BaseDto" — the explicit-name alternative form excludes
        // exactly the named class.
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, ExclusionFile,
            "add a string Email property to every DTO class, except the one named BaseDto");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("excluding 'BaseDto'", edit.Reason);
    }

    [Fact]
    public void MultiMember_CSharp_Exclusion_ArticleGuard_NoOverExclude()
    {
        // "except a base class" — the article "a" must NOT be captured as the excluded word
        // (Contains("a") would exclude nearly every class). The clause falls through to no
        // exclusion, so ALL DTO classes (incl. BaseDto) are edited.
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, ExclusionFile,
            "add a string Email property to every DTO class except a base class");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(3, edit.Edits.Count); // no exclusion fired — BaseDto included
        Assert.DoesNotContain("excluding", edit.Reason);
    }

    [Fact]
    public void MultiMember_CSharp_ExclusionAndOverride_Combine()
    {
        // Narrowing + per-class naming compose: the override resolves among the REMAINING
        // classes, so "first one" = first non-excluded DTO class (UserDto).
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, ExclusionFile,
            "add a string Email property to every DTO class except the base one, but NameKey on the first one");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("public string NameKey { get; set; }", edit.Edits[0].NewString); // UserDto
        Assert.Contains("public string Email { get; set; }", edit.Edits[1].NewString);  // OrderDto
    }

    [Fact]
    public void MultiMember_CSharp_SuffixFilter_NoMatch_Declines()
    {
        // No class ends in "Repository" in MultiDtoFile — the narrowed set is empty, so
        // the whole multi request declines instead of editing something unrelated.
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile,
            "add a string Email property to all classes ending in Repository"));
    }

    [Fact]
    public void MultiMember_TypeScript_SuffixFilter_EndingInDto()
    {
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/models.ts", true, MultiTsFile,
            "add a string updatedAt property to all classes ending in Dto");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count); // UserDto + OrderDto — the IMeta interface is kind-excluded
        Assert.All(edit.Edits, e => Assert.Contains("public updatedAt: string = '';", e.NewString));
    }

    [Fact]
    public void MultiMember_CSharp_AdaptiveClassName_PrefixesMember()
    {
        // "... named after the class" — the member name adapts to each class: UserDto → UserName,
        // OrderDto → OrderName (DTO-ish suffix stripped before the class name is prefixed).
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile,
            "add a string Name property to every DTO class, named after the class");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("public string UserName { get; set; }", edit.Edits[0].NewString);
        Assert.Contains("public string OrderName { get; set; }", edit.Edits[1].NewString);
        Assert.Contains("class-prefixed names", edit.Reason);

        var content = MultiDtoFile;
        foreach (var e in edit.Edits)
        {
            var (replaced, nc, _, _) = TryReplaceSafe(content, e.OldString, e.NewString, e.LineNumber);
            Assert.True(replaced);
            content = nc;
        }
        Assert.Equal(1, content.Split("public string UserName { get; set; }").Length - 1);
        Assert.Equal(1, content.Split("public string OrderName { get; set; }").Length - 1);
    }

    [Fact]
    public void MultiMember_TypeScript_AdaptiveClassName_CamelCased()
    {
        // TS members are camelCased — "UserName" becomes "userName", "OrderName" → "orderName".
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/models.ts", true, MultiTsFile,
            "add a string name property to every DTO class, named after the class");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("public userName: string = '';", edit.Edits[0].NewString);
        Assert.Contains("public orderName: string = '';", edit.Edits[1].NewString);
    }

    [Fact]
    public void MultiMember_CSharp_OverrideBeatsAdaptive_OnThatClass()
    {
        // Override + adaptive together: the override wins on its class, adaptive names the rest.
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, MultiDtoFile,
            "add a string Email property to every DTO class, named after the class, but NameKey on the first one");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("public string NameKey { get; set; }", edit.Edits[0].NewString);       // override wins
        Assert.Contains("public string OrderEmail { get; set; }", edit.Edits[1].NewString);   // adaptive elsewhere
    }

    [Fact]
    public void MultiMember_CSharp_ReindentedFile_MemberMirrorsExistingIndent()
    {
        // A formatter-reindented (2-space) file must get style-consistent members, not a
        // hardcoded 4-space default — this is what lets G1's re-synthesis blend in after drift.
        const string file =
            "public class UserDto\n" +
            "{\n" +
            "  public int Id { get; set; }\n" +
            "}\n" +
            "public class OrderDto\n" +
            "{\n" +
            "  public string Name { get; set; }\n" +
            "}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "Models/Dtos.cs", true, file, "add a string Email property to every DTO class");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.All(edit.Edits, e => Assert.Contains("  public string Email { get; set; }", e.NewString));
        Assert.All(edit.Edits, e => Assert.DoesNotContain("    public string Email", e.NewString));

        var content = file;
        foreach (var e in edit.Edits)
        {
            var (replaced, nc, _, _) = TryReplaceSafe(content, e.OldString, e.NewString, e.LineNumber);
            Assert.True(replaced);
            content = nc;
        }
        Assert.Equal(2, content.Split("  public string Email { get; set; }").Length - 1);
        Assert.Equal(0, content.Split("    public string Email").Length - 1);
    }

    [Fact]
    public void MultiMember_TypeScript_OneEditPerMatchingClass_InterfaceExcluded()
    {
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/models.ts", true, MultiTsFile, "add a string email property to every DTO class");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        // UserDto + OrderDto (camelCased member), but NOT the IMeta interface.
        Assert.Equal(2, edit.Edits.Count);
        Assert.Equal(3, edit.Edits[0].LineNumber);
        Assert.Equal(6, edit.Edits[1].LineNumber);
        Assert.All(edit.Edits, e => Assert.Contains("public email: string = '';", e.NewString));

        var content = MultiTsFile;
        foreach (var e in edit.Edits)
        {
            var (replaced, nc, _, _) = TryReplaceSafe(content, e.OldString, e.NewString, e.LineNumber);
            Assert.True(replaced);
            content = nc;
        }
        Assert.Equal(2, content.Split("public email: string = '';").Length - 1);
    }

    [Fact]
    public void MultiMember_TypeScript_EveryInterface_KindFilter()
    {
        const string file =
            "export class UserDto {\n  name = '';\n}\n" +
            "export interface IMeta {\n  created: Date;\n}\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "app/models.ts", true, file, "add a string updatedAt property to every interface");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits);
        Assert.Contains("updatedAt: string;", edit.Edits[0].NewString);
    }

    [Fact]
    public void MultiMember_NoMatchingClasses_Declines()
    {
        const string file = "export class User {\n  name = '';\n}\n";
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "app/user.ts", true, file, "add a string Email property to every DTO class"));
    }

    [Fact]
    public void Decide_MultiClassMemberAdd_ReturnsResolvedEdits()
    {
        var intent = new EditIntent(EditIntentKind.AddProperty, null, null);
        var decision = EditStrategyResolver.Decide(
            "Models/Dtos.cs", true, MultiDtoFile, "add a string Email property to every DTO class", intent);

        Assert.Equal(EditStrategy.FillClassBody, decision.Strategy);
        Assert.NotNull(decision.ResolvedEdits);
        Assert.Equal(2, decision.ResolvedEdits.Count);
        Assert.Contains("deterministic batch: 2 edits", decision.ResolvedNewStr);
        Assert.Contains("member edits", decision.Reason);
    }

    // ── Multi-match batch: "update all five X defaults" → one edit per occurrence ──

    private const string FiveDefaults =
        "const retryCount = 3;\n" +
        "const retryCount = 3;\n" +
        "const retryCount = 3;\n" +
        "const retryCount = 3;\n" +
        "const retryCount = 3;\n";

    [Fact]
    public void Multi_SetTo_AllFive_ProducesOneEditPerOccurrence()
    {
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, FiveDefaults, "update all five RetryCount defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(5, edit.Edits.Count);
        Assert.Contains("deterministic batch", edit.NewStr);
        Assert.Contains("applied 5/5 occurrences, skipped 0", edit.Reason); // full batch — no skips
        // The marker carries applied/total so the meeting ticker can render one compact line.
        Assert.Contains("(deterministic batch: 5 edits, applied 5/5 occurrences)", edit.NewStr);
        for (var i = 0; i < edit.Edits.Count; i++)
        {
            Assert.Equal("const retryCount = 3;", edit.Edits[i].OldString);
            Assert.Equal("const retryCount = 5;", edit.Edits[i].NewString);
            Assert.Equal(i + 1, edit.Edits[i].LineNumber); // one-based, in file order
        }
    }

    [Fact]
    public void Multi_SetTo_ReadsEachCurrentValue()
    {
        const string file =
            "timeout = 30\n" +
            "timeout = 45\n" +
            "timeout = 60\n" +
            "timeout = 30\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ini", true, file, "update the timeout values to 60");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(3, edit.Edits.Count); // the already-60 line is a no-op and is skipped
        Assert.All(edit.Edits, e => Assert.Equal("timeout = 60", e.NewString));
    }

    [Fact]
    public void Multi_FromTo_StripsMultiSignal_AndSkippsMismatchedValues()
    {
        const string file =
            "const retryCount = 3;\n" +
            "const retryCount = 3;\n" +
            "const retryCount = 9;\n" +
            "const retryCount = 5;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all five RetryCount from 3 to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count); // only the lines whose value is 3
        Assert.All(edit.Edits, e => Assert.Equal("const retryCount = 5;", e.NewString));
        Assert.Equal(new[] { 1, 2 }, edit.Edits.Select(e => e.LineNumber).ToArray());
    }

    [Fact]
    public void Multi_CommentOccurrences_Skipped()
    {
        const string file =
            "// retryCount = 3\n" +
            "const retryCount = 3;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all retryCount defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits); // the comment line is not a real occurrence
        Assert.Equal(2, edit.Edits[0].LineNumber);
    }

    [Fact]
    public void Multi_TrailingCommentMention_Skipped()
    {
        const string file =
            "const retryCount = 9; // retryCount = 3\n" +
            "const retryCount = 3;\n";
        // From-to form: the first literal AFTER the name is the real value (9), not the
        // "3" inside the trailing comment — so line 1 must be skipped, only line 2 edited.
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all retryCount from 3 to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits);
        Assert.Equal(2, edit.Edits[0].LineNumber);
    }

    [Fact]
    public void Multi_SetTo_TrailingComment_PreservesComment()
    {
        const string file =
            "const retryCount = 9; // was 3, now 9\n" +
            "const retryCount = 3;\n";
        // Set-to form reads each REAL value (9 and 3) and swaps it; the trailing comment
        // text must survive untouched.
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all retryCount defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Equal("const retryCount = 5; // was 3, now 9", edit.Edits[0].NewString);
        Assert.Equal("const retryCount = 5;", edit.Edits[1].NewString);
    }

    [Fact]
    public void Multi_UrlInsideString_NotTreatedAsComment()
    {
        const string file =
            "const url = 'http://x'?0:1;\n" +
            "const retryCount = 3;\n";
        // "http://" inside a string must not be misread as a comment start when deciding
        // whether a later occurrence is real code.
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all retryCount defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits);
        Assert.Equal(2, edit.Edits[0].LineNumber);
    }

    [Fact]
    public void Multi_SingleOccurrence_StillBatchOfOne()
    {
        const string file = "const retryCount = 3;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all retryCount defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits);
        Assert.Contains("deterministic batch", edit.NewStr);
    }

    [Fact]
    public void Multi_SetTo_OneAlreadyCorrect_ReportsSkipped1()
    {
        // The exact partial combination the batch integration test drives end-to-end:
        // 1 of 2 occurrences is already the target → applied 1/2, skipped 1: already-correct.
        const string file =
            "maxRetries=3\n" +
            "maxRetries=5\n"; // already the target — skipped
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ini", true, file, "update all maxRetries defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits);
        Assert.Contains("applied 1/2 occurrences", edit.Reason);
        Assert.Contains("skipped 1: 1 already-correct", edit.Reason);
        Assert.Contains("(deterministic batch: 1 edits, applied 1/2 occurrences)", edit.NewStr);
    }

    [Fact]
    public void Multi_SetTo_Partial_ReportsSkippedReasons()
    {
        const string file =
            "const retryCount = 5; // already the target\n" +
            "const retryCount =\n" +
            "  3;\n" +
            "const retryCount = 3;\n" +
            "const retryCount = 3;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all retryCount defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count); // the two '= 3' lines
        // G6: the partial batch reports exactly what was skipped and why.
        Assert.Contains("applied 2/4 occurrences", edit.Reason);
        Assert.Contains("skipped 2: 1 already-correct, 1 multi-line value", edit.Reason);
    }

    [Fact]
    public void Multi_FromTo_Partial_ReportsMismatchAndAlreadyCorrect()
    {
        const string file =
            "const retryCount = 3;\n" + // applied
            "const retryCount = 9;\n" + // a different value — left alone
            "const retryCount = 5;\n" + // already the target
            "const retryCount = 3;\n";  // applied
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all retryCount from 3 to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Contains("applied 2/4 occurrences", edit.Reason);
        Assert.Contains("skipped 2: 1 already-correct, 1 value mismatch", edit.Reason);
    }

    [Fact]
    public void Multi_NoOccurrences_Declines()
    {
        const string file = "const other = 3;\n";
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all five RetryCount defaults to 5"));
    }

    [Fact]
    public void Multi_AllAlreadyTarget_Declines()
    {
        const string file = "const retryCount = 5;\nconst retryCount = 5;\n";
        Assert.Null(DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all retryCount defaults to 5")); // no-ops must not be emitted
    }

    [Fact]
    public void Multi_ValueOnNextLine_SkippedInBatchMode()
    {
        const string file =
            "const retryCount =\n  3;\n" +
            "const retryCount = 3;\n";
        // Batch mode requires each occurrence's value on the same line — the multi-line
        // occurrence is skipped, the single-line one is still edited (at line 3).
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all retryCount defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Single(edit.Edits);
        Assert.Equal(3, edit.Edits[0].LineNumber);
    }

    [Fact]
    public void Multi_LineNumbers_AreOneBasedInFileOrder()
    {
        const string file =
            "// header\n" +
            "const retryCount = 3;\n" +
            "\n" +
            "const retryCount = 3;\n";
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "update all retryCount defaults to 5");

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Edits);
        Assert.Equal(2, edit.Edits.Count);
        Assert.Equal(2, edit.Edits[0].LineNumber);
        Assert.Equal(4, edit.Edits[1].LineNumber);
    }

    [Fact]
    public void Multi_SingularWording_StaysSingleSwap()
    {
        const string file = "const retryCount = 3;\n";
        // "set the retryCount to 5" has no plural noun and no all/every signal — it must
        // remain a single swap (no batch), so no ResolvedEdits are produced.
        var edit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, file, "set the retryCount to 5");

        Assert.NotNull(edit);
        Assert.Null(edit!.Edits);
        Assert.Equal("const retryCount = 5;", edit.NewStr);
    }

    [Fact]
    public void Decide_UpdateAllDefaults_ReturnsResolvedEdits()
    {
        var intent = new EditIntent(EditIntentKind.TargetedEdit, null, null);
        var decision = EditStrategyResolver.Decide(
            "config.ts", true, FiveDefaults, "update all five RetryCount defaults to 5", intent);

        Assert.NotNull(decision.ResolvedEdits);
        Assert.Equal(5, decision.ResolvedEdits.Count);
        Assert.Equal("const retryCount = 3;", decision.ResolvedEdits[0].OldString);
        Assert.Equal("const retryCount = 5;", decision.ResolvedEdits[0].NewString);
        Assert.Contains("Synthesized 5 anchored edits", decision.Reason);
        Assert.Contains("applied 5/5 occurrences, skipped 0", decision.Reason);
        // The first edit's old string fills ResolvedOldStr — it's what routes the step
        // through the plan-provided apply path; the batch marker lives in ResolvedNewStr.
        Assert.Equal("const retryCount = 3;", decision.ResolvedOldStr);
        Assert.Contains("deterministic batch: 5 edits", decision.ResolvedNewStr);
    }

    // ── G1: drift recovery — re-generate against the CURRENT file content ─────

    [Fact]
    public void G1_DriftRecovery_FreshGenerationReAnchorsAgainstCurrentContent()
    {
        // The deterministic edit was generated against the ORIGINAL content…
        var originalEdit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, "const retryCount = 3;\n", "change retryCount from 3 to 5");
        Assert.NotNull(originalEdit);
        Assert.Equal("const retryCount = 3;", originalEdit!.OldStr);

        // …but the file drifted (retyped) before the apply ran — the original anchor
        // no longer exists, so attempt 0 fails (this is the apply failure G1 catches).
        const string drifted = "let retryCount: number = 3;\n";
        var (replaced, _, _, _) = TryReplaceSafe(drifted, originalEdit.OldStr!, originalEdit.NewStr!, originalEdit.LineNumber);
        Assert.False(replaced);

        // G1: re-running the generator against the CURRENT content re-anchors the edit
        // (old anchor + fresh new string), so attempt 1 applies with zero LLM calls.
        var freshEdit = DeterministicEditGenerator.TryGenerate(
            "config.ts", true, drifted, "change retryCount from 3 to 5");
        Assert.NotNull(freshEdit);
        Assert.Equal("let retryCount: number = 3;", freshEdit!.OldStr);
        Assert.Equal("let retryCount: number = 5;", freshEdit.NewStr);
        var (replaced2, content2, _, _) = TryReplaceSafe(drifted, freshEdit.OldStr!, freshEdit.NewStr!, freshEdit.LineNumber);
        Assert.True(replaced2);
        Assert.Contains("let retryCount: number = 5;", content2);
    }

    [Fact]
    public void G1_DriftRecovery_BatchRegeneratesAllOccurrences()
    {
        // Original content the batch was generated against…
        const string original = "timeout = 30\ntimeout = 45\ntimeout = 60\n";
        var originalBatch = DeterministicEditGenerator.TryGenerate(
            "config.ini", true, original, "update the timeout values to 60");
        Assert.NotNull(originalBatch);
        Assert.Equal(2, originalBatch!.Edits!.Count); // the 30 and 45 lines

        // …drifts (a 45 line becomes 50, a new 30 line appears) — the old batch anchors
        // no longer line up exactly.
        const string drifted = "timeout = 30\ntimeout = 50\ntimeout = 60\ntimeout = 30\n";
        // Re-generation against current content yields the correct, fresh batch.
        var freshBatch = DeterministicEditGenerator.TryGenerate(
            "config.ini", true, drifted, "update the timeout values to 60");
        Assert.NotNull(freshBatch);
        Assert.NotNull(freshBatch!.Edits);
        Assert.Equal(3, freshBatch.Edits.Count); // lines 1, 2 (50) and 4 (30)
        Assert.Equal("timeout = 60", freshBatch.Edits[0].NewString);
        Assert.Equal("timeout = 60", freshBatch.Edits[1].NewString);
        Assert.Equal("timeout = 60", freshBatch.Edits[2].NewString);
        Assert.Contains("applied 3/4 occurrences", freshBatch.Reason); // 60 line already correct
    }
}
