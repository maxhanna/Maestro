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
        // The last real body line is the method's own close brace — still a wider,
        // context-carrying anchor than a lone '}' at class indent.
        Assert.Equal("  }\n}", edit!.OldStr);
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
}
