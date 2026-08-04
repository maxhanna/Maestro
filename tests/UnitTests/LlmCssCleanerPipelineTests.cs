using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// End-to-end pipeline tests: a scratch CSS file in a temp project, a simulated agent
/// edit (oldString/newString substitution like the agent's edit-apply), then the EXACT
/// post-edit cleaning commands the agent runs for CSS files (AgentController ~4995):
/// LlmCssCleaner.Clean(newContent) followed by FixCssStructure(newContent).
/// Guarantees: unrelated selectors, hex colors, pseudo-selectors, media queries, and
/// keyframes names stay BYTE-IDENTICAL after an edit, while known LLM squishes
/// (all0.2s, width:40px, split hex, 6px14px, 0000) are still repaired.
/// </summary>
public class LlmCssCleanerPipelineTests : IDisposable
{
    private readonly string _tempRoot;

    public LlmCssCleanerPipelineTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "weaver-css-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    private string ScratchPath => Path.Combine(_tempRoot, "src", "app", "score", "score.component.css");

    private void WritePristine(string css)
    {
        var full = ScratchPath;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, css);
    }

    /// <summary>
    /// Mirrors the agent's CSS edit-apply + post-edit cleaning: substitute oldString with
    /// newString in the scratch file, then run LlmCssCleaner.Clean + FixCssStructure over
    /// the whole new content — the exact commands the pipeline runs after a CSS edit.
    /// </summary>
    private string ApplyAgentEditThenClean(string oldString, string newString)
    {
        var before = File.ReadAllText(ScratchPath);
        var idx = before.IndexOf(oldString, StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("oldString not found in scratch file");
        var newContent = before[..idx] + newString + before[(idx + oldString.Length)..];
        newContent = LlmCssCleaner.Clean(newContent);
        newContent = LlmCssCleaner.FixCssStructure(newContent);
        return newContent;
    }

    // Realistic component CSS that exercises every previously-broken shape:
    // h4 type selector, hex colors (zero-adjacent AND letter-adjacent), pseudo-class,
    // pseudo-element, multi-line selector list, media query, keyframes name, plain numbers.
    private const string Pristine =
        ".difficulty-group h4 {\n" +
        "    margin: 8px 0;\n" +
        "}\n" +
        "\n" +
        "a:hover {\n" +
        "    color: #ff0000;\n" +
        "}\n" +
        "\n" +
        "a::before {\n" +
        "    content: \"x\";\n" +
        "}\n" +
        "\n" +
        ".item2 {\n" +
        "    color: #000;\n" +
        "}\n" +
        "\n" +
        ".card,\n" +
        ".card--active,\n" +
        ".card:focus {\n" +
        "    border: 1px solid #f0f0f0;\n" +
        "}\n" +
        "\n" +
        "@media (max-width: 767px) {\n" +
        "    .guess-header {\n" +
        "        max-width: 10px;\n" +
        "    }\n" +
        "}\n" +
        "\n" +
        "@keyframes spin1s {\n" +
        "    from { transform: rotate(0deg); }\n" +
        "}\n" +
        "\n" +
        ".z-stack {\n" +
        "    z-index: 1000;\n" +
        "    transition: all 0.2s ease;\n" +
        "}";

    // ── Clean must be a no-op on pristine, valid CSS ─────────────────────────

    [Fact]
    public void Clean_OnPristineFile_IsByteIdentical()
    {
        Assert.Equal(Pristine, LlmCssCleaner.Clean(Pristine));
    }

    [Fact]
    public void FixCssStructure_OnPristineFile_IsByteIdentical()
    {
        Assert.Equal(Pristine, LlmCssCleaner.FixCssStructure(Pristine));
    }

    // ── The reported bug shape: add a class with an h4 selector nearby ───────

    [Fact]
    public void AgentEdit_AddingClassNearH4Selector_LeavesUnrelatedLinesByteIdentical()
    {
        WritePristine(Pristine);

        // The agent adds a class right next to the h4 selector (the real BugHosted
        // edit shape that corrupted h4 -> h 4).
        const string oldString = ".difficulty-group h4 {\n    margin: 8px 0;\n}";
        const string newString =
            ".difficulty-group h4 {\n" +
            "    margin: 8px 0;\n" +
            "}\n" +
            "\n" +
            ".score-summary h4 {\n" +
            "    font-size: 14px;\n" +
            "}";

        var cleaned = ApplyAgentEditThenClean(oldString, newString);

        // The new class landed and the h4 selector is intact (not h 4).
        Assert.Contains(".score-summary h4", cleaned);
        Assert.Contains(".difficulty-group h4", cleaned);
        Assert.DoesNotContain("h 4", cleaned);

        // Byte-identical guarantee: cleaning the edited file equals a pure
        // substitution — nothing outside the inserted block changed.
        var expected = Pristine.Replace(oldString, newString);
        Assert.Equal(expected, cleaned);
    }

    // ── Known LLM squishes still get fixed; everything else stays untouched ──

    [Fact]
    public void AgentEdit_WithKnownLlmSquishes_AreFixed_UnrelatedLinesStay()
    {
        WritePristine(Pristine);

        const string oldString = "a:hover {\n    color: #ff0000;\n}";
        const string newString =
            "a:hover {\n" +
            "    color: #ff0000;\n" +
            "}\n" +
            "\n" +
            ".new-squished {\n" +
            "    transition: all0.2s;\n" +
            "    width:40px;\n" +
            "    color:#ab cd ef;\n" +
            "    padding: 6px14px;\n" +
            "    margin: 0000;\n" +
            "}";

        var cleaned = ApplyAgentEditThenClean(oldString, newString);

        // Each known LLM squish is repaired by the cleaner. Note the split hex is
        // merged AND the missing space after the colon is added (both are repairs,
        // so the final form carries a space: `color: #abcdef;`).
        Assert.Contains("transition: all 0.2s;", cleaned);
        Assert.Contains("width: 40px;", cleaned);
        Assert.Contains("color: #abcdef;", cleaned);
        Assert.Contains("padding: 6px 14px;", cleaned);
        Assert.Contains("margin: 0 0;", cleaned);

        // The pre-existing pseudo-class line and its hex color are byte-identical.
        Assert.Contains("a:hover {\n    color: #ff0000;\n}", cleaned);

        // Pipeline output equals cleaning the substitution — nothing else changed.
        var expected = LlmCssCleaner.Clean(Pristine.Replace(oldString, newString));
        Assert.Equal(expected, cleaned);
    }

    // ── Every tricky shape survives an unrelated edit elsewhere in the file ──

    [Fact]
    public void AgentEdit_AppendingNewRuleAtEnd_LeavesAllTrickyShapesByteIdentical()
    {
        WritePristine(Pristine);

        // Anchor on the LAST rule so the edit happens far from the h4 selector,
        // the pseudo-selectors, the hex colors, and the keyframes name.
        const string oldString = ".z-stack {\n    z-index: 1000;\n    transition: all 0.2s ease;\n}";
        const string newString = oldString + "\n\n.footer h4 {\n    margin-top: 2rem;\n}";

        var cleaned = ApplyAgentEditThenClean(oldString, newString);

        Assert.Contains(".footer h4", cleaned);

        // Spot-check every previously-broken shape survived untouched.
        Assert.Contains(".difficulty-group h4 {\n    margin: 8px 0;\n}", cleaned);
        Assert.Contains("a:hover {\n    color: #ff0000;\n}", cleaned);
        Assert.Contains("a::before {\n    content: \"x\";\n}", cleaned);
        Assert.Contains("color: #000;", cleaned);
        Assert.Contains("border: 1px solid #f0f0f0;", cleaned);
        Assert.Contains("@media (max-width: 767px) {", cleaned);
        Assert.Contains("@keyframes spin1s {", cleaned);
        Assert.Contains("z-index: 1000;", cleaned);
        Assert.DoesNotContain("h 4", cleaned);
        Assert.DoesNotContain("#0 0", cleaned);

        // Whole-file byte-identical guarantee.
        var expected = Pristine.Replace(oldString, newString);
        Assert.Equal(expected, cleaned);
    }

    // ── FixCssStructure repairs genuinely broken braces ─────────────────────

    [Fact]
    public void FixCssStructure_RepairsMissingClosingBrace()
    {
        var unbalanced = ".broken {\n    color: red;";
        var fixedCss = LlmCssCleaner.FixCssStructure(unbalanced);
        Assert.Equal(fixedCss.Count(c => c == '{'), fixedCss.Count(c => c == '}'));
        Assert.Contains(".broken", fixedCss);
        Assert.Contains("color", fixedCss);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FUZZ — Clean + FixCssStructure must be byte-identical no-ops on valid CSS
    // ═══════════════════════════════════════════════════════════════════════════
    // The h4 -> "h 4" corruption taught us that a single hand-picked fixture is not
    // enough: an over-broad regex can sit dormant until a specific selector/value
    // combination walks through it. This generator throws DOZENS of random-but-valid
    // CSS documents (type selectors, classes, ids, attribute/child combinators,
    // pseudo-classes, pseudo-elements, hex colors of every length, media queries,
    // keyframes with duration-suffixed names, plain numbers, rgba, calc) at the exact
    // cleaning commands the agent runs — and requires ZERO drift. The RNG is seeded,
    // so the corpus is identical on every run and every machine.

    [Fact]
    public void Fuzz_RandomValidCss_CleanAndFixCssStructure_AreByteIdenticalNoOps()
    {
        const int docCount = 60;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(20260803, i, 7919);
            var css = GenerateValidCss(rng);

            var cleaned = LlmCssCleaner.Clean(css);
            FuzzHarness.AssertByteIdenticalNoOp(css, cleaned, "Clean()", i, "cleaned");

            var fixedStructure = LlmCssCleaner.FixCssStructure(css);
            FuzzHarness.AssertByteIdenticalNoOp(css, fixedStructure, "FixCssStructure()", i, "fixed");
        }
    }

    [Fact]
    public void Fuzz_RandomValidCss_FullPipeline_IsByteIdentical()
    {
        const int docCount = 60;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(4021, i, 104729);
            var css = GenerateValidCss(rng);

            var throughPipeline = LlmCssCleaner.FixCssStructure(LlmCssCleaner.Clean(css));
            FuzzHarness.AssertByteIdenticalNoOp(css, throughPipeline, "full pipeline", i, "pipeline");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CORPUS FULL-CHAIN — random CSS through the COMPLETE deterministic edit path
    // ═══════════════════════════════════════════════════════════════════════════
    // The fuzz tests above pin the cleaner alone. These go further: a random generated
    // CSS file is pushed through the ENTIRE chain the agent executes for a CSS edit —
    // EditClassifier.Classify → ClassifyIntent → EditStrategyResolver.Decide →
    // AgentUtilities.TryReplaceSafe (the oldString/newString applier) →
    // LlmCssCleaner.Clean → FixCssStructure — and the final file must equal the pure
    // substitution. A regression ANYWHERE in the chain (classifier quirk, resolver
    // misdecision, fuzzy apply, cleaner regex) fails the build — not just a cleaner one.

    private static PlanStep CssStep(string file, string change) => new()
    {
        File = file,
        Change = change
    };

    /// <summary>
    /// Mirror the agent's complete deterministic edit chain for a CSS oldString/newString
    /// step: classify → resolve → apply → clean → fix structure. Asserts the apply stage
    /// produced the pure substitution (the applier must never fuzzy-drift), then returns
    /// every stage so the caller can assert the final end-state.
    /// </summary>
    private static (EditStrategy strategy, EditPlanDecision decision, string finalContent) RunFullEditChain(
        string original, string oldString, string newString, string changeDesc)
    {
        var step = CssStep("score.component.css", changeDesc);

        // 1–2. Classification — the strategy/intent must be the deterministic CSS defaults.
        var strategy = EditClassifier.Classify(step, fileExists: true, ".css");
        var intent = EditClassifier.ClassifyIntent(step, ".css");
        var decision = EditStrategyResolver.Decide("score.component.css", true, original, changeDesc, intent);

        // 3. Apply — exactly the oldString/newString path. PickUniqueRuleAnchor guarantees
        //    a unique anchor, so this MUST succeed as a single-match replace and the applied
        //    content MUST equal the pure substitution (catches fuzzy/dedupe drift).
        var (replaced, applied, matchError, _) = AgentUtilities.TryReplaceSafe(original, oldString, newString);
        Assert.True(replaced, $"TryReplaceSafe failed on corpus doc: {matchError}");
        Assert.Equal(original.Replace(oldString, newString), applied);

        // 4–5. Post-edit cleaning — the exact commands the agent runs for CSS files.
        var finalContent = LlmCssCleaner.FixCssStructure(LlmCssCleaner.Clean(applied));

        return (strategy, decision, finalContent);
    }

    [Fact]
    public void Fuzz_CompleteAgentEditPath_RandomCss_EqualsPureSubstitution()
    {
        const int docCount = 30;
        var checkedDocs = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(90210, i, 7919);
            var css = GenerateValidCss(rng);

            // Pick a unique top-level rule block as the edit anchor.
            var anchor = PickUniqueRuleAnchor(css, startOffset: i);
            if (anchor == null) continue; // degenerate doc — generator rarely emits one
            checkedDocs++;

            // The agent adds a brand-new class right after the anchor rule (the real
            // h4-corruption shape) — clean newString, so the final file MUST equal the
            // pure substitution with zero drift anywhere in the chain.
            var newRule = GenerateRuleBlock(new Random(rng.Next()));
            var oldString = anchor;
            var newString = anchor + "\n\n" + newRule;

            var (strategy, decision, finalContent) = RunFullEditChain(css, oldString, newString,
                "Add a new class to the stylesheet");

            // The deterministic strategy contract for CSS edits: whitespace-significant
            // → anchored text edit, both at Classify and Decide level.
            Assert.Equal(EditStrategy.AnchoredEdit, strategy);
            Assert.Equal(EditStrategy.AnchoredEdit, decision.Strategy);

            // THE core guarantee: final file equals the pure substitution — no cleaner
            // drift, no apply fuzz, no resolver surprise anywhere in the chain.
            var expected = css.Replace(oldString, newString);
            FuzzHarness.AssertByteIdenticalNoOp(expected, finalContent, "full edit chain", i, "final");

            // Post-edit structural verify: braces balanced.
            Assert.Equal(finalContent.Count(c => c == '{'), finalContent.Count(c => c == '}'));
        }

        // Corpus degradation (a doc with no unique anchor) must be a hard failure,
        // not a silent pass that checked nothing.
        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "CSS corpus full-chain (pure substitution)");
    }

    [Fact]
    public void Fuzz_CompleteAgentEditPath_SquishInsideEdit_Repaired_OutsideByteIdentical()
    {
        const int docCount = 30;
        var checkedDocs = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(314159, i, 65537);
            var css = GenerateValidCss(rng);

            var anchor = PickUniqueRuleAnchor(css, startOffset: i + 1);
            if (anchor == null) continue;
            checkedDocs++;

            // The new class deliberately carries LLM squishes — the cleaner must repair
            // exactly those while every generated shape (h4, hex, pseudo, keyframes,
            // media) outside stays byte-identical.
            const string squishNewRule =
                ".fuzz-added {\n" +
                "    width:40px;\n" +
                "    transition: all0.2s;\n" +
                "    color:#ff0000;\n" +
                "}";
            var oldString = anchor;
            var newString = anchor + "\n\n" + squishNewRule;

            var (strategy, decision, finalContent) = RunFullEditChain(css, oldString, newString,
                "Add a new class to the stylesheet");

            Assert.Equal(EditStrategy.AnchoredEdit, strategy);
            Assert.Equal(EditStrategy.AnchoredEdit, decision.Strategy);

            // The squishes were repaired by the cleaning stage.
            Assert.Contains("width: 40px;", finalContent);
            Assert.Contains("transition: all 0.2s;", finalContent);
            Assert.DoesNotContain("width:40px", finalContent);
            Assert.DoesNotContain("all0.2s", finalContent);

            // Independent guarantee: EVERY pre-existing top-level block survives the
            // whole chain byte-identical — only the squish block's three lines changed.
            var preExistingBlocks = css.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var block in preExistingBlocks)
                Assert.True(finalContent.Contains(block, StringComparison.Ordinal),
                    $"corpus doc #{i} lost pre-existing block during squish-repair chain:\n{block}\n--- final ---\n{finalContent}");

            Assert.Equal(finalContent.Count(c => c == '{'), finalContent.Count(c => c == '}'));
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "CSS corpus full-chain (squish repair)");
    }

    /// <summary>
    /// Split a generated CSS doc into top-level blocks (separated by blank lines) and pick
    /// a plain rule block (not @media/@keyframes/comment) that occurs EXACTLY once, so
    /// TryReplaceSafe gets a unique anchor. Returns null when every candidate is duplicated.
    /// </summary>
    private static string? PickUniqueRuleAnchor(string css, int startOffset)
    {
        var blocks = css.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (blocks.Length < 2) return null;
        for (var i = 0; i < blocks.Length; i++)
        {
            var block = blocks[(startOffset + i) % blocks.Length];
            if (block.StartsWith("@") || block.StartsWith("/*")) continue;
            var occurrences = 0;
            var searchPos = 0;
            while ((searchPos = css.IndexOf(block, searchPos, StringComparison.Ordinal)) >= 0)
            {
                occurrences++;
                searchPos += block.Length;
            }
            if (occurrences == 1) return block;
        }
        return null;
    }

    private static int Pick(Random rng, params int[] values) => values[rng.Next(values.Length)];

    private static readonly string[] FuzzHexPool =
    {
        "#000", "#fff", "#f00", "#0f0", "#00f", "#ff0000", "#00ff00", "#0000ff",
        "#f0f0f0", "#ffffff", "#00000080", "#ff8800", "#123456", "#abcdef", "#a1b2c3"
    };

    private static readonly string[] FuzzSimpleSelectors =
    {
        "h1", "h2", "h3", "h4", "h5", "p", "span", "button", "input", "table", "li",
        ".difficulty-group", ".card", ".btn", ".header", ".footer", ".item", ".z-stack",
        "#app", "#main", "#root", "div.item2", "nav > ul", "main section", ".header .title",
        "article p", "input[type=text]", "a[href^=https]", "table tr td", "p + span",
        "h4 ~ ul", ".card > .body"
    };

    private static readonly string[] FuzzPseudoParts =
    {
        ":hover", ":focus", ":active", ":disabled", ":first-child", ":last-child",
        ":checked", "::before", "::after", "::placeholder", "::selection",
        ":nth-child(2n)", ":not(.hidden)"
    };

    private static string GenerateSelector(Random rng)
    {
        var s = FuzzSimpleSelectors[rng.Next(FuzzSimpleSelectors.Length)];
        if (rng.Next(4) == 0) s += FuzzPseudoParts[rng.Next(FuzzPseudoParts.Length)];
        return s;
    }

    private static string GenerateSelectorList(Random rng)
    {
        var count = 1 + rng.Next(3);
        var parts = new List<string>();
        for (var i = 0; i < count; i++) parts.Add(GenerateSelector(rng));
        return string.Join(",\n", parts);
    }

    private static string GenerateDeclaration(Random rng)
    {
        var hex = FuzzHexPool[rng.Next(FuzzHexPool.Length)];
        switch (rng.Next(17))
        {
            case 0:  return $"margin: {Pick(rng, 0, 2, 4, 8, 12, 16)}px {Pick(rng, 0, 4, 8, 16)}px;";
            case 1:  return $"color: {hex};";
            case 2:  return $"background-color: {hex};";
            case 3:  return $"border: 1px solid {hex};";
            case 4:  return $"padding: {Pick(rng, 0, 4, 6, 8, 12, 16)}px {Pick(rng, 8, 12, 14, 16, 20)}px;";
            case 5:  return "z-index: 1000;";
            case 6:  return "transition: all 0.2s ease;";
            case 7:  return "transition: opacity 0.3s ease-in-out;";
            case 8:  return $"font-size: {Pick(rng, 10, 12, 14, 16, 18)}px;";
            case 9:  return "line-height: 1.5;";
            case 10: return "opacity: 0.85;";
            case 11: return "margin: 0 auto;";
            case 12: return $"max-width: {Pick(rng, 480, 600, 767, 1024, 1280)}px;";
            case 13: return "box-shadow: 0 2px 4px rgba(0, 0, 0, 0.1);";
            case 14: return $"width: {Pick(rng, 25, 50, 75, 100)}%;";   // % unit — UnitRx's % alternative
            case 15: return "transition: transform 150ms linear;";     // ms unit
            case 16: return "width: calc(100% - 20px);";               // already-spaced calc — must stay untouched
            default: return "margin: 0;";
        }
    }

    private static string GenerateRuleBlock(Random rng)
    {
        var decls = new List<string>();
        var count = 1 + rng.Next(4);
        for (var i = 0; i < count; i++) decls.Add("    " + GenerateDeclaration(rng));
        return GenerateSelectorList(rng) + " {\n" + string.Join("\n", decls) + "\n}";
    }

    private static string GenerateMediaQuery(Random rng)
    {
        var width = Pick(rng, 480, 600, 767, 768, 1024, 1280);
        var minMax = rng.Next(2) == 0 ? "max" : "min";
        var lines = new List<string> { $"@media ({minMax}-width: {width}px) {{" };
        var ruleCount = 1 + rng.Next(3);
        for (var r = 0; r < ruleCount; r++)
        {
            lines.Add("    " + GenerateSelector(rng) + " {");
            lines.Add("        " + GenerateDeclaration(rng));
            lines.Add("    }");
        }
        lines.Add("}");
        return string.Join("\n", lines);
    }

    private static string GenerateKeyframes(Random rng)
    {
        var names = new[] { "spin1s", "fadeIn2s", "pulse", "slideDown", "bounce1s", "flip3d", "shake" };
        var lines = new List<string> { $"@keyframes {names[rng.Next(names.Length)]} {{" };
        lines.Add("    from { transform: rotate(0deg); }");
        if (rng.Next(2) == 0) lines.Add("    50% { opacity: 0.5; }");
        lines.Add("    to { transform: rotate(360deg); }");
        lines.Add("}");
        return string.Join("\n", lines);
    }

    /// <summary>Compose a valid CSS document from the random building blocks.</summary>
    private static string GenerateValidCss(Random rng)
    {
        var blocks = new List<string> { "/* fuzz-valid-css */" };
        var ruleCount = 4 + rng.Next(6);
        for (var r = 0; r < ruleCount; r++) blocks.Add(GenerateRuleBlock(rng));
        if (rng.Next(2) == 0) blocks.Add(GenerateMediaQuery(rng));
        if (rng.Next(2) == 0) blocks.Add(GenerateKeyframes(rng));
        return string.Join("\n\n", blocks) + "\n";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  REGION-WINDOW FORMATTER — AgentController.FormatAcceptedEditRegionAsync
    // ═══════════════════════════════════════════════════════════════════════════
    // The agent does NOT clean the whole file after a CSS oldString/newString edit —
    // it locates the applied region, widens it to a ±4-line window, formats ONLY that
    // window (prettier + LlmCssCleaner.Clean), and splices it back. That private method
    // is exercised here via reflection (GetUninitializedObject skips the DI ctor; the
    // method touches no instance state), so the window logic — not just whole-file
    // Clean — is covered. The squish-fix assertion is prettier-independent: even if the
    // external formatter were a no-op, Clean() inside the method still repairs
    // `width:40px`, so the window-replacement path always executes on this fixture.

    private static async Task<string> InvokeFormatAcceptedEditRegionAsync(
        string filePath, string content, string? oldString, string? newString)
    {
        // RuntimeHelpers.GetUninitializedObject skips the DI constructor (the method
        // touches no instance state); FormatterServices is obsolete, hence the modern API.
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var method = typeof(AgentController).GetMethod(
            "FormatAcceptedEditRegionAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("FormatAcceptedEditRegionAsync not found");
        var invokeResult = method.Invoke(controller,
            new object?[] { filePath, content, oldString, newString, CancellationToken.None });
        var task = (Task<string>)(invokeResult ?? throw new InvalidOperationException("Invoke returned null"));
        return await task;
    }

    [Fact]
    public async Task FormatAcceptedEditRegion_NonCssExtension_ReturnsContentUnchanged()
    {
        // The window formatter is CSS-only — a .ts path must pass through untouched.
        var content = ".card {\n    width:40px;\n}";
        var result = await InvokeFormatAcceptedEditRegionAsync(
            "style.ts", content, ".card {", ".card {\n    width:40px;\n}");

        Assert.Equal(content, result);
        Assert.Contains("width:40px", result); // not even cleaned — gate fires first
    }

    [Fact]
    public async Task FormatAcceptedEditRegion_NewStringNotFound_ReturnsContentUnchanged()
    {
        // The applied region must be found in the content; otherwise no window work.
        var content = ".card {\n    color: #f00;\n}";
        var result = await InvokeFormatAcceptedEditRegionAsync(
            "style.css", content, ".card {", ".ghost {\n    color: red;\n}");

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task FormatAcceptedEditRegion_EmptyNewString_ReturnsContentUnchanged()
    {
        var content = ".card {\n    color: #f00;\n}";
        var result = await InvokeFormatAcceptedEditRegionAsync(
            "style.css", content, ".card {", "");

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task FormatAcceptedEditRegion_FixesSquishInWindow_LeavesOutsideWindowByteIdentical()
    {
        // The region (.middle) sits mid-file with >4 lines of padding on both sides, so
        // the ±4-line window is strictly interior. The h4 selector and pseudo-selector
        // are OUTSIDE the window and must survive byte-identical; the width:40px squish
        // is INSIDE it and must be repaired.
        var content = string.Join("\n", new[]
        {
            ".top h4 {",
            "    color: #ff0000;",
            "}",
            "",
            "/* p1 */",
            "/* p2 */",
            "/* p3 */",
            "/* p4 */",
            "/* p5 */",
            "",
            ".middle {",
            "    width:40px;",
            "}",
            "",
            "/* q1 */",
            "/* q2 */",
            "/* q3 */",
            "/* q4 */",
            "/* q5 */",
            "/* q6 */",
            "/* q7 */",
            "/* q8 */",
            "",
            ".bottom a:hover {",
            "    color: #000;",
            "}"
        });

        var newString = ".middle {\n    width:40px;\n}";
        var result = await InvokeFormatAcceptedEditRegionAsync(
            "score.component.css", content, ".middle {", newString);

        // The squish inside the ±4-line window is repaired (by Clean if not prettier).
        Assert.Contains("width: 40px", result);
        Assert.DoesNotContain("width:40px", result);

        // Everything outside the window is byte-identical — the whole-file Clean
        // guarantee, but scoped to the region window.
        var expectedPrefix =
            ".top h4 {\n" +
            "    color: #ff0000;\n" +
            "}\n" +
            "\n" +
            "/* p1 */\n" +
            "/* p2 */\n" +
            "/* p3 */\n" +
            "/* p4 */\n" +
            "/* p5 */\n";
        var expectedSuffix =
            "/* q4 */\n" +
            "/* q5 */\n" +
            "/* q6 */\n" +
            "/* q7 */\n" +
            "/* q8 */\n" +
            "\n" +
            ".bottom a:hover {\n" +
            "    color: #000;\n" +
            "}";

        Assert.StartsWith(expectedPrefix, result);
        Assert.EndsWith(expectedSuffix, result);
        Assert.DoesNotContain("h 4", result);
        Assert.DoesNotContain("#0 0", result);
        Assert.DoesNotContain("a: hover", result);
    }
}
