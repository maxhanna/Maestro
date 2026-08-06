using TreeSitter;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Deterministic tests for <see cref="AstCodeEditorService"/> — Tree-sitter based symbol
/// extraction and JS/TS syntax-error auto-repair. All input is in-memory strings, no
/// LLM, no filesystem. Guards the language map, query patterns, and the missing-token
/// inserter so FORMAT C/D resolution never silently regresses.
/// </summary>
public class AstCodeEditorServiceTests
{
    // ── IsSupportedExtension ─────────────────────────────────────────────────

    [Theory]
    [InlineData(".ts", true)]
    [InlineData(".js", true)]
    [InlineData(".tsx", true)]
    [InlineData(".cs", true)]
    [InlineData(".py", true)]
    [InlineData(".go", true)]
    [InlineData(".rs", true)]
    [InlineData(".java", true)]
    [InlineData(".css", true)]
    [InlineData(".xyz", false)]
    [InlineData("", false)]
    public void IsSupportedExtension_MatchesLanguageMap(string ext, bool expected)
    {
        Assert.Equal(expected, AstCodeEditorService.IsSupportedExtension(ext));
    }

    // ── FindFunctionSource — C# ──────────────────────────────────────────────

    [Fact]
    public void FindFunctionSource_CSharp_Method_ReturnsFullBlock()
    {
        var source = """
            using System;

            public class Foo
            {
                public void Bar()
                {
                    Console.WriteLine("hi");
                }
            }
            """;

        var (block, startLine, error) = AstCodeEditorService.FindFunctionSource(source, "Bar", ".cs");

        Assert.Null(error);
        Assert.NotNull(block);
        Assert.Contains("public void Bar", block);
        Assert.Contains("Console.WriteLine", block);
        Assert.True(startLine > 0);
    }

    [Fact]
    public void FindFunctionSource_CSharp_MissingSymbol_ReturnsError()
    {
        var source = "public class Foo { public void Bar() { } }";
        var (block, _, error) = AstCodeEditorService.FindFunctionSource(source, "Nope", ".cs");

        Assert.Null(block);
        Assert.NotNull(error);
        Assert.Contains("not found", error);
    }

    // ── FindFunctionSource — TypeScript ──────────────────────────────────────

    [Fact]
    public void FindFunctionSource_TypeScript_ClassMethod_ReturnsBlock()
    {
        var source = """
            export class Demo {
              run(): void {
                console.log("run");
              }
            }
            """;

        var (block, _, error) = AstCodeEditorService.FindFunctionSource(source, "run", ".ts");

        Assert.Null(error);
        Assert.NotNull(block);
        Assert.Contains("run(): void", block);
        Assert.Contains("console.log", block);
    }

    [Fact]
    public void FindFunctionSource_TypeScript_TopLevelFunction_ReturnsBlock()
    {
        var source = """
            export function helper(value: string): string {
              return value.trim();
            }
            """;

        var (block, _, error) = AstCodeEditorService.FindFunctionSource(source, "helper", ".ts");

        Assert.Null(error);
        Assert.NotNull(block);
        Assert.Contains("export function helper", block);
    }

    // ── FindFunctionSource — Python ──────────────────────────────────────────

    [Fact]
    public void FindFunctionSource_Python_Function_ReturnsBlock()
    {
        var source = """
            def greet(name):
                return f"hi {name}"
            """;

        var (block, startLine, error) = AstCodeEditorService.FindFunctionSource(source, "greet", ".py");

        Assert.Null(error);
        Assert.NotNull(block);
        Assert.Contains("def greet", block);
        Assert.True(startLine > 0);
    }

    // ── FindFunctionSource — Go ──────────────────────────────────────────────

    [Fact]
    public void FindFunctionSource_Go_Function_ReturnsBlock()
    {
        var source = """
            package main

            func Greet(name string) string {
                return "hi " + name
            }
            """;

        var (block, _, error) = AstCodeEditorService.FindFunctionSource(source, "Greet", ".go");

        Assert.Null(error);
        Assert.NotNull(block);
        Assert.Contains("func Greet", block);
        Assert.Contains("return \"hi \" + name", block);
    }

    // ── FindFunctionSource — Rust ────────────────────────────────────────────

    [Fact]
    public void FindFunctionSource_Rust_Function_ReturnsBlock()
    {
        var source = """
            fn add(a: i32, b: i32) -> i32 {
                a + b
            }
            """;

        var (block, _, error) = AstCodeEditorService.FindFunctionSource(source, "add", ".rs");

        Assert.Null(error);
        Assert.NotNull(block);
        Assert.Contains("fn add", block);
        Assert.Contains("a + b", block);
    }

    // ── Unsupported extension ────────────────────────────────────────────────

    [Fact]
    public void FindFunctionSource_UnsupportedExtension_ReturnsError()
    {
        var (block, _, error) = AstCodeEditorService.FindFunctionSource("x", "foo", ".xyz");

        Assert.Null(block);
        Assert.NotNull(error);
        Assert.Contains("Unsupported extension", error);
    }

    // ── FindAllFunctions ─────────────────────────────────────────────────────

    [Fact]
    public void FindAllFunctions_TypeScript_ReturnsEveryMethod()
    {
        var source = """
            class Demo {
              alpha() { return 1; }
              beta() { return 2; }
              gamma() { return 3; }
            }
            """;

        var funcs = AstCodeEditorService.FindAllFunctions(source, ".ts");

        Assert.Contains(funcs, f => f.name == "alpha");
        Assert.Contains(funcs, f => f.name == "beta");
        Assert.Contains(funcs, f => f.name == "gamma");
        Assert.All(funcs, f => Assert.True(f.startLine > 0));
        Assert.All(funcs, f => Assert.Contains(f.name, f.source));
    }

    [Fact]
    public void FindAllFunctions_CSharp_ReturnsMethodsAndProperties()
    {
        var source = """
            public class Foo
            {
                public int Id { get; set; }
                public void Run() { }
            }
            """;

        var funcs = AstCodeEditorService.FindAllFunctions(source, ".cs");

        Assert.Contains(funcs, f => f.name == "Id");
        Assert.Contains(funcs, f => f.name == "Run");
    }

    // ── AutoFixSyntaxErrors (JS/TS missing-token inserter) ───────────────────

    [Fact]
    public void AutoFixSyntaxErrors_ValidJs_ReturnsUnchanged()
    {
        var source = "const x = 1;\nconst y = [1, 2];\n";
        var result = AstCodeEditorService.AutoFixSyntaxErrors(source, ".js");

        Assert.Equal(source, result);
    }

    [Fact]
    public void AutoFixSyntaxErrors_MissingSemicolon_InsertsSemicolon()
    {
        // `for (let i = 0 i < 10; ...)` omits the `;` after the for-init clause —
        // tree-sitter flags it as a MISSING `;` token (probe confirmed: missing=[;@14]),
        // which the inserter repairs. (Most other recovery shapes become ERROR nodes,
        // which the service correctly leaves alone.)
        var source = "for (let i = 0 i < 10; i++) {}";
        var result = AstCodeEditorService.AutoFixSyntaxErrors(source, ".js");

        Assert.NotEqual(source, result);
        Assert.Contains(";", result);
    }

    [Fact]
    public void AutoFixSyntaxErrors_NonJsLanguage_ReturnsUnchanged()
    {
        var source = "public class Foo { public void Bar() { } }";
        var result = AstCodeEditorService.AutoFixSyntaxErrors(source, ".cs");

        Assert.Equal(source, result);
    }

    [Fact]
    public void AutoFixSyntaxErrors_EmptyContent_ReturnsUnchanged()
    {
        Assert.Equal("", AstCodeEditorService.AutoFixSyntaxErrors("", ".js"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FUZZ — AutoFixSyntaxErrors must never corrupt JS/TS
    // ═══════════════════════════════════════════════════════════════════════════
    // Two seeded random corpora. (1) DOZENS of valid JS/TS snippets — the fixer must
    // be a byte-identical no-op on each, and the generator itself is validated by a
    // direct Tree-sitter parse so a template bug fails loudly instead of silently.
    // (2) Deliberately broken snippets (missing commas, semicolons, braces, parens)
    // plus the known missing-`;` for-loop shape — the fixer must repair or leave
    // unchanged, and NEVER delete content (insertion-only contract). The RNG is
    // seeded, so the corpus is identical on every run and every machine.

    private static readonly string[] FuzzJsIds =
        { "data", "items", "value", "count", "total", "result", "index", "name", "score", "limit" };

    private static readonly string[] FuzzPascalIds =
        { "Data", "Item", "Config", "Result", "Point", "Shape", "Demo", "Counter", "Panel", "Wrapper" };

    private static readonly string[] FuzzJsStrings =
        { "\"hello\"", "\"world\"", "\"ok\"", "\"done\"", "\"pending\"" };

    // Base templates — valid in BOTH .js and .ts.
    private static readonly string[] FuzzJsTemplates =
    {
        "const __ID__ = __N__;",
        "const __ID__ = __N__", // ASI: valid JS without a trailing semicolon
        "let __ID__ = __N__;",
        "let __ID__ = __S__", // ASI: valid JS without a trailing semicolon
        "const __ID__ = [__N__, __N2__, __N__];",
        "const __ID__ = __S__;",
        "const __ID__ = { a: __N__, b: __S__ };",
        "function __ID__(__ID2__, __ID3__) { return __ID2__ + __ID3__; }",
        "function __ID__(__ID2__) { if (__ID2__ > __N__) { return __ID2__; } return __N__; }",
        "const __ID__ = (__ID2__) => __ID2__ * __N__;",
        "if (__ID__ > __N__) { console.log(__ID__); } else { console.log(__N__); }",
        "for (let __ID__ = 0; __ID__ < __N__; __ID__++) { total += __ID__; }",
        "while (__ID__ < __N__) { __ID__++; }",
        "const __ID__ = `hello ${name}`;",
        "const __ID__ = __S__ + __S__;",
        "class __IDC__ { constructor() { this.value = __N__; } get() { return this.value; } }",
        "const __ID__ = new Map();",
    };

    // TS-only templates — valid ONLY under the TypeScript grammar.
    private static readonly string[] FuzzTsTemplates =
    {
        "const __ID__: number = __N__;",
        "function __ID__(__ID2__: string): string { return __ID2__; }",
        "interface __IDC__ { width: number; height: number; }",
        "type __IDC__ = string | number;",
        "enum __IDC__ { Red, Green, Blue }",
        "class __IDC__ { private __ID__: string; constructor(__ID2__: string) { this.__ID__ = __ID2__; } }",
        "const __ID__: __IDC__[] = [__N__, __N2__];",
        "function __ID__<T>(value: T): T { return value; }",
        "const __ID__ = (__ID2__: number): number => __ID2__ * 2;",
        "let __ID__: string | null = null;",
    };

    private static string FillTemplate(string tpl, Random rng)
    {
        // Distinct identifiers: __ID__/__ID2__/__ID3__ must never collide, or a doc
        // could contain `function f(data, data)` — parse-legal in sloppy mode today
        // but fragile against future grammar strictness (duplicate params are an
        // early SyntaxError in strict mode).
        var ids = new string[3];
        var used = new HashSet<int>();
        for (var i = 0; i < 3; i++)
        {
            var idx = rng.Next(FuzzJsIds.Length);
            var guard = 0;
            while (used.Contains(idx) && used.Count < FuzzJsIds.Length && guard++ < 16)
                idx = rng.Next(FuzzJsIds.Length);
            used.Add(idx);
            ids[i] = FuzzJsIds[idx];
        }
        return tpl
            .Replace("__IDC__", FuzzPascalIds[rng.Next(FuzzPascalIds.Length)])
            .Replace("__ID3__", ids[2])
            .Replace("__ID2__", ids[1])
            .Replace("__ID__", ids[0])
            .Replace("__N2__", rng.Next(1, 100).ToString())
            .Replace("__N__", rng.Next(1, 100).ToString())
            .Replace("__S__", FuzzJsStrings[rng.Next(FuzzJsStrings.Length)]);
    }

    private static string GenerateValidSnippet(Random rng, string ext)
    {
        var pool = ext == ".ts"
            ? FuzzJsTemplates.Concat(FuzzTsTemplates).ToArray()
            : FuzzJsTemplates;
        var count = 2 + rng.Next(4); // 2–5 top-level statements
        var lines = new List<string>();
        for (var i = 0; i < count; i++)
            lines.Add(FillTemplate(pool[rng.Next(pool.Length)], rng));
        return string.Join("\n", lines);
    }

    /// <summary>JS/TS grammar name — single source of truth so a future .tsx corpus can't drift one of three copies.</summary>
    private static string JsLangName(string ext) => ext == ".ts" ? "TypeScript" : "JavaScript";

    /// <summary>Direct Tree-sitter parse check — the generator's docs must be genuinely valid.</summary>
    private static bool HasParseError(string content, string ext)
    {
        var langName = JsLangName(ext);
        try
        {
            using var language = new Language(langName);
            using var parser = new Parser(language);
            using var tree = parser.Parse(content);
            return tree == null || tree.RootNode.HasError;
        }
        catch
        {
            return true; // grammar unavailable → treat as failure so the corpus is auditable
        }
    }

    private static string DropFirst(string source, string ch)
    {
        var idx = source.IndexOf(ch, StringComparison.Ordinal);
        return idx < 0 ? source : source.Remove(idx, 1);
    }

    private static string GenerateBrokenSnippet(Random rng, string ext)
    {
        // 1-in-6 of .js docs: the known MISSING-`;` for-loop shape (guaranteed repair).
        // Only emit it for .js — the TypeScript grammar recovers the same input as an
        // ERROR node (unrepairable), so emitting it there would starve the repair assertion.
        if (ext == ".js" && rng.Next(6) == 0) return "for (let i = 0 i < 10; i++) {}";
        var valid = GenerateValidSnippet(rng, ext);
        return rng.Next(5) switch
        {
            0 => DropFirst(valid, ";"),
            1 => DropFirst(valid, ","),
            2 => DropFirst(valid, "}"),
            3 => DropFirst(valid, "{"),
            _ => DropFirst(valid, ")")
        };
    }

    [Fact]
    public void Fuzz_AutoFixSyntaxErrors_ValidJsTs_IsByteIdenticalNoOp()
    {
        const int docCount = 60;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(20261103, i, 7919);
            var ext = rng.Next(2) == 0 ? ".js" : ".ts";
            var doc = GenerateValidSnippet(rng, ext);

            // The corpus itself must be genuinely valid (generator guard — a template
            // bug fails here loudly instead of silently passing the no-op assertion).
            Assert.True(!HasParseError(doc, ext),
                $"fuzz doc #{i} ({ext}) is NOT code the parser accepts (or the grammar failed to load):\n{doc}");

            var result = AstCodeEditorService.AutoFixSyntaxErrors(doc, ext);
            FuzzHarness.AssertByteIdenticalNoOp(doc, result, $"AutoFixSyntaxErrors ({ext})", i);
        }
    }

    [Fact]
    public void Fuzz_AutoFixSyntaxErrors_BrokenSnippets_RepairOrLeave_NoCrash()
    {
        const int docCount = 60;
        var knownRepairChecked = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(4242, i, 104729);
            var ext = rng.Next(2) == 0 ? ".js" : ".ts";
            var broken = GenerateBrokenSnippet(rng, ext);

            // No-crash guarantee (the service has internal try/catch, but the corpus
            // must prove it across every mutation shape).
            string? result = null;
            var exception = Record.Exception(() =>
                result = AstCodeEditorService.AutoFixSyntaxErrors(broken, ext));
            Assert.Null(exception);

            // Either repaired or left unchanged — and repair is INSERTION-ONLY: the
            // fixer never deletes or rewrites content, so a repaired doc is strictly
            // longer than the broken input.
            Assert.True(result == broken || result!.Length > broken.Length,
                $"AutoFixSyntaxErrors altered broken {ext} doc #{i} illegally:\n{broken}\n--- result ---\n{result}");

            // The known missing-`;` for-loop shape MUST be repaired under the JavaScript
            // grammar (tree-sitter reports a MISSING `;` there — locked by the unit test
            // above). The TypeScript grammar recovers this same input as an ERROR node,
            // which the inserter correctly leaves alone (repair-or-leave contract).
            if (broken == "for (let i = 0 i < 10; i++) {}" && ext == ".js")
            {
                knownRepairChecked++;
                Assert.True(result != broken && result.Contains("0; i", StringComparison.Ordinal),
                    $"known missing-; shape not repaired in fuzz doc #{i} ({ext}):\n{result}");
            }
        }

        // The guaranteed-repair path must have actually fired (corpus degradation would
        // otherwise let this test pass having asserted nothing about real repairs).
        FuzzHarness.AssertExercised(knownRepairChecked,
            "the known missing-; repair path was never exercised by the corpus");
    }

    /// <summary>
    /// Counts ERROR + MISSING nodes in a direct Tree-sitter parse — the progress metric
    /// for the differential test. A MISSING node (e.g. an omitted `;`) is a recoverable
    /// syntax defect the inserter can fix; an ERROR node is unrecovered garbage. Returns
    /// -1 when the grammar is unavailable so a corpus failure is auditable, never silent.
    /// </summary>
    private static int CountErrorAndMissingNodes(string content, string ext)
    {
        var langName = JsLangName(ext);
        try
        {
            using var language = new Language(langName);
            using var parser = new Parser(language);
            using var tree = parser.Parse(content);
            if (tree == null) return -1;
            return CountErrorNodes(tree.RootNode);
        }
        catch
        {
            return -1; // grammar unavailable → treat as failure so the corpus is auditable
        }
    }

    private static int CountErrorNodes(Node node)
    {
        var count = (node.IsError || node.IsMissing) ? 1 : 0;
        if (node.Children == null) return count;
        foreach (var child in node.Children)
            count += CountErrorNodes(child);
        return count;
    }

    [Fact]
    public void Fuzz_AutoFixSyntaxErrors_BrokenSnippets_RepairsAreStrictProgress()
    {
        const int docCount = 60;
        var repairedChecked = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(90909, i, 65537);
            var ext = rng.Next(2) == 0 ? ".js" : ".ts";
            var broken = GenerateBrokenSnippet(rng, ext);

            // Input error count — the baseline the repair must beat.
            var beforeCount = CountErrorAndMissingNodes(broken, ext);
            Assert.True(beforeCount >= 0,
                $"fuzz doc #{i} ({ext}): baseline parse failed (grammar unavailable?):\n{broken}");

            var result = AstCodeEditorService.AutoFixSyntaxErrors(broken, ext);

            // Only repaired snippets make a progress claim. The service repairs iff the
            // input has errors (it short-circuits on a clean tree), so a change here
            // always means the input was defective.
            if (result == broken) continue;
            repairedChecked++;

            var afterCount = CountErrorAndMissingNodes(result!, ext);
            Assert.True(afterCount >= 0,
                $"fuzz doc #{i} ({ext}): re-parse of repaired output failed (grammar unavailable?):\n{result}");

            // THE differential claim: a repair must be GENUINE progress — strictly fewer
            // error/missing nodes than the input, never a lateral move that reshuffles
            // the same defect count into a different shape. NOTE: this is STRICTER than
            // the service's own contract (insertion-only, not monotonic recovery) — a
            // future grammar edge could in theory convert one MISSING into one ERROR
            // (equal total); if that ever trips legitimately, relax to "no more ERROR
            // nodes AND strictly fewer MISSING nodes" rather than the combined total.
            Assert.True(afterCount < beforeCount,
                $"fuzz doc #{i} ({ext}): repair was NOT strict progress — " +
                $"errors+missing before={beforeCount}, after={afterCount}.\n" +
                $"--- broken input ---\n{broken}\n--- repaired output ---\n{result}");
        }

        // The corpus must have actually exercised the repair path — otherwise this test
        // passes having asserted nothing (corpus degradation guard).
        FuzzHarness.AssertExercised(repairedChecked,
            "no broken snippet was repaired — differential progress claim never exercised");
    }
}
