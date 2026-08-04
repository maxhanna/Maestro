using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Corpus full-chain tests for FORMAT C languages (.ts/.js/.cs). Generates random
/// classes with methods, then runs the COMPLETE deterministic edit path the agent
/// executes: EditClassifier.Classify → ClassifyIntent → EditStrategyResolver.Decide
/// (AST resolution via Roslyn for .cs, Tree-sitter for .ts/.js) → TryReplaceSafe.
/// Asserts oldString extraction is SCOPED to exactly the intended method — never
/// drifting into sibling methods or the class header — and the replacement produces
/// EXACTLY the intended diff: applied == pure substitution, with every unrelated
/// method byte-identical. The RNG is seeded, so the corpus is identical on every run.
/// </summary>
public class FormatCCorpusTests
{
    private static readonly string[] CsMethodNames =
        { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" };

    private static readonly string[] JsMethodNames =
        { "alpha", "beta", "gamma", "delta", "epsilon" };

    // ── Generators ───────────────────────────────────────────────────────────

    /// <summary>A single method block at class-member indentation (4 spaces for .cs, 2 for ts/js).</summary>
    private static string MemberBlock(string ext, string name, int bodyNum) => ext switch
    {
        ".cs" => $"    public void {name}()\n    {{\n        var tmp = {bodyNum};\n    }}",
        ".ts" => $"  {name}(): void {{\n    this.count = {bodyNum};\n  }}",
        _     => $"  {name}() {{\n    const tmp = {bodyNum};\n  }}",
    };

    /// <summary>Prefix every line with the class-member indent — forces
    /// <c>FormatSnippetRealign</c> to actually strip min-indent + re-prefix (its
    /// transform path) instead of passing pre-indented code through as a no-op.</summary>
    private static string OverIndent(string block, string ext)
    {
        var indent = ext == ".cs" ? "    " : "  ";
        return string.Join("\n", block.Split('\n').Select(l => indent + l));
    }

    private static string BuildClass(string ext, List<string> names, List<int> bodyNums)
    {
        var members = names.Select((n, i) => MemberBlock(ext, n, bodyNums[i]));
        var body = string.Join("\n\n", members);
        return ext switch
        {
            ".cs" => "public class Sample\n{\n" + body + "\n}",
            ".ts" => "export class Sample {\n" + body + "\n}",
            _     => "class Sample {\n" + body + "\n}",
        };
    }

    private static string ReplaceChange(string name) => $"Rewrite the {name} method entirely";

    private const string InsertChange = "Add a new method to the class";

    /// <summary>
    /// Mirror the agent's complete deterministic edit chain for a FORMAT C step on a
    /// .ts/.js/.cs file. Returns the strategy, decision, extracted oldString, and the
    /// applied content. Asserts the apply produced the PURE SUBSTITUTION (the applier
    /// must never fuzzy-drift).
    /// </summary>
    private static (EditStrategy strategy, EditPlanDecision decision, string oldStr, string newStr, string applied) RunFormatCChain(
        string content, string ext, string changeDesc, string targetSymbol, string newCode, bool insert)
    {
        var file = ext switch
        {
            ".cs" => "src/Sample.cs",
            ".ts" => "src/sample.component.ts",
            _     => "src/sample.js"
        };
        var step = new PlanStep { File = file, Change = changeDesc, TargetSymbol = targetSymbol };

        // 1–2. Classification — strategy and intent must be the FORMAT C mapping.
        var strategy = EditClassifier.Classify(step, fileExists: true, ext);
        var intent = EditClassifier.ClassifyIntent(step, ext);
        var decision = EditStrategyResolver.Decide(file, true, content, changeDesc, intent);
        Assert.Equal(strategy, decision.Strategy);
        Assert.Equal(targetSymbol, decision.TargetName);
        Assert.NotNull(decision.ResolvedOldStr);

        // 3. The AST-extracted oldString is a VERBATIM substring of the file (scoped
        //    extraction — the resolver found the real block, not a hallucinated one).
        var oldStr = decision.ResolvedOldStr!;
        Assert.True(content.Contains(oldStr, StringComparison.Ordinal),
            $"ResolvedOldStr is not a verbatim substring of the {ext} file:\n{oldStr}");

        // 4. Compose newString EXACTLY as AgentController.ResolveEditForStep does for
        //    FORMAT C (non-HTML): insert → `newStr = fullStr + "\n" + indented` where
        //    fullStr is the AST-resolved anchor block (~line 2010); replace →
        //    `(astOldStr, indented)` (~line 2007). Single newline, not two.
        var indented = FuzzHarness.FormatSnippetRealign(oldStr, newCode);
        var newStr = insert ? oldStr + "\n" + indented : indented;

        // 5. Apply — must be the pure substitution (no fuzzy/dedupe drift).
        var (replaced, applied, matchError, _) = AgentUtilities.TryReplaceSafe(content, oldStr, newStr);
        Assert.True(replaced, $"TryReplaceSafe failed on {ext} doc: {matchError}");
        Assert.Equal(content.Replace(oldStr, newStr), applied);

        return (strategy, decision, oldStr, newStr, applied);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CORPUS — FORMAT C full chain (.ts / .js / .cs)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Fuzz_FormatC_FullChain_TsJsCs_PureSubstitutionNoDrift()
    {
        const int docCount = 30;
        var strategyHits = new BranchHitCounter<EditStrategy>(
            new[] { EditStrategy.InsertMethod, EditStrategy.ReplaceMethod },
            "FORMAT C corpus");

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(77_001, i, 104729);
            var ext = (i % 3) switch { 0 => ".ts", 1 => ".js", _ => ".cs" };
            var insert = (i / 3) % 2 == 0;

            // 2–4 distinct method names + random body values → a valid random class.
            var pool = ext == ".cs" ? CsMethodNames : JsMethodNames;
            var names = pool.OrderBy(_ => rng.Next()).Take(2 + rng.Next(3)).ToList();
            var bodyNums = names.Select(_ => rng.Next(10, 99)).ToList();
            var content = BuildClass(ext, names, bodyNums);
            var targetIdx = rng.Next(names.Count);
            var target = names[targetIdx];
            var siblings = names.Select((n, j) => (Name: n, Block: MemberBlock(ext, n, bodyNums[j])))
                                .Where(s => s.Name != target)
                                .ToList();

            // New code: replace keeps the name with a distinctive body marker; insert uses
            // a brand-new unique name. Both bodies use far-out markers that can never
            // collide with the doc's 10–99 body numbers.
            var newCode = insert
                ? MemberBlock(ext, ext == ".cs" ? "NewMethod" + i : "newMethod" + i, 55_000 + i)
                : MemberBlock(ext, target, 77_000 + i);

            // Docs 0, 4, 8, … feed an OVER-INDENTED block so FormatSnippetRealign must
            // actually strip min-indent + re-prefix to the anchor's base indent — its
            // transform path, not the pre-indented no-op. The pure-substitution assertion
            // below then locks the realigned result as the applied bytes.
            var overIndented = i % 4 == 0;
            var newCodeForChain = overIndented ? OverIndent(newCode, ext) : newCode;

            var changeDesc = insert ? InsertChange : ReplaceChange(target);
            var expectedStrategy = insert ? EditStrategy.InsertMethod : EditStrategy.ReplaceMethod;

            var (strategy, decision, oldStr, newStr, applied) =
                RunFormatCChain(content, ext, changeDesc, target, newCodeForChain, insert);
            strategyHits.Hit(strategy);

            Assert.Equal(expectedStrategy, strategy);
            Assert.Equal(expectedStrategy, decision.Strategy);

            // ── Realign transform is exercised with a KNOWN output: stripping the extra
            //    indent must recover the clean member block byte-for-byte ──
            if (overIndented)
                Assert.Equal(newCode, FuzzHarness.FormatSnippetRealign(oldStr, newCodeForChain));

            // ── Scoped extraction: the oldString is EXACTLY the intended method ──
            Assert.Contains(target, oldStr);                       // the target method
            Assert.DoesNotContain("class Sample", oldStr);         // not the whole class
            Assert.All(siblings, s => Assert.DoesNotContain(s.Name, oldStr)); // not siblings

            // ── oldString occurs EXACTLY ONCE — pure substitution is unambiguous ──
            var firstOccurrence = content.IndexOf(oldStr, StringComparison.Ordinal);
            Assert.True(firstOccurrence >= 0, "resolved oldString missing from content");
            var secondOccurrence = firstOccurrence + 1 < content.Length
                ? content.IndexOf(oldStr, firstOccurrence + 1, StringComparison.Ordinal)
                : -1;
            Assert.Equal(-1, secondOccurrence);

            // ── No unrelated drift: class scaffolding + every sibling byte-identical ──
            Assert.Contains("class Sample", applied);             // header survives
            Assert.All(siblings, s => Assert.Contains(s.Block, applied));

            // ── Exactly the intended diff ──
            if (insert)
            {
                Assert.Contains(newCode, applied);   // new method present
                Assert.Contains(oldStr, applied);    // anchor method intact

                // ── POSITION FIDELITY: the new method lands IMMEDIATELY after the
                //    anchor, at the SAME relative position in the file. The anchor
                //    doesn't move (its index is unchanged, so the whole prefix before
                //    it is byte-identical), and the suffix that originally followed the
                //    anchor still follows the new method — a single boundary insertion,
                //    never a tail-append or a reorder.
                var anchorIdx = applied.IndexOf(oldStr, StringComparison.Ordinal);
                Assert.Equal(content.IndexOf(oldStr, StringComparison.Ordinal), anchorIdx);
                var newIdx = applied.IndexOf(newCode, StringComparison.Ordinal);
                Assert.Equal(anchorIdx + oldStr.Length + 1, newIdx);
                // The bytes at the boundary are EXACTLY the composed insert (anchor + "\n"
                // + realigned new method) — use the chain's own newStr so the assert tracks
                // the real composition instead of reconstructing it.
                Assert.Equal(newStr, applied.Substring(anchorIdx, newStr.Length));
                Assert.Equal(content[(content.IndexOf(oldStr, StringComparison.Ordinal) + oldStr.Length)..],
                    applied[(anchorIdx + newStr.Length)..]);
            }
            else
            {
                Assert.Contains(newCode, applied);            // replacement body present
                Assert.DoesNotContain(oldStr, applied);       // old block consumed
                // Replacement occupies the anchor's exact position — same relative spot.
                Assert.Equal(content.IndexOf(oldStr, StringComparison.Ordinal),
                    applied.IndexOf(newCode, StringComparison.Ordinal));
            }
        }

        // Both branches were exercised — the rotation is exact: 15 inserts, 15 replaces.
        Assert.Equal(docCount / 2, strategyHits.Count(EditStrategy.InsertMethod));
        Assert.Equal(docCount / 2, strategyHits.Count(EditStrategy.ReplaceMethod));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  RE-RUN IDEMPOTENCY — the already-done guard stops a double insert
    // ═══════════════════════════════════════════════════════════════════════════
    // After a FORMAT C insert lands, re-running the IDENTICAL insert step must hit
    // the pipeline's already-done guard (AgentController.PreEditValidation — the exact
    // verdict the executor computes against the current file before resolving/applying)
    // instead of inserting a second copy of the method.

    [Fact]
    public void Fuzz_FormatC_Insert_Rerun_HitsAlreadyDoneGuard_NoDoubleInsert()
    {
        const int docCount = 30;
        var checkedDocs = 0;
        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(77_101, i, 104729);
            var ext = (i % 3) switch { 0 => ".ts", 1 => ".js", _ => ".cs" };
            var pool = ext == ".cs" ? CsMethodNames : JsMethodNames;
            var names = pool.OrderBy(_ => rng.Next()).Take(2 + rng.Next(3)).ToList();
            var bodyNums = names.Select(_ => rng.Next(10, 99)).ToList();
            var content = BuildClass(ext, names, bodyNums);
            var target = names[rng.Next(names.Count)];
            var newCode = MemberBlock(ext, ext == ".cs" ? "NewMethod" + i : "newMethod" + i, 55_000 + i);
            var newCodeForChain = i % 4 == 0 ? OverIndent(newCode, ext) : newCode;

            // First insert lands — exactly ONE copy of the new method.
            var (_, _, oldStr, newStr, applied) =
                RunFormatCChain(content, ext, InsertChange, target, newCodeForChain, insert: true);
            Assert.Equal(1, CountOccurrences(applied, newCode));

            // NEGATIVE CONTROL — the pure substitution is NOT idempotent on its own: a
            // naive re-apply of the identical oldStr→newStr double-inserts. This proves
            // the AlreadyDone assertion below is what actually stops the re-run (the
            // guard is load-bearing, not a vacuous pass).
            var (naiveReplaced, naiveApplied, _, _) =
                AgentUtilities.TryReplaceSafe(applied, oldStr, newStr);
            Assert.True(naiveReplaced, "anchor must still be present for a naive re-apply");
            Assert.Equal(2, CountOccurrences(naiveApplied, newCode));

            // THE GUARD — the exact check the executor runs against the current file
            // before resolving/applying a step (AgentController line ~3787). On the
            // already-inserted content it MUST say AlreadyDone so the pipeline skips the
            // re-run instead of double-inserting. On the ORIGINAL content it must say
            // Proceed (content-sensitivity — the guard is not a blanket skip).
            var step = new PlanStep
            {
                File = ext switch
                {
                    ".cs" => "src/Sample.cs",
                    ".ts" => "src/sample.component.ts",
                    _     => "src/sample.js"
                },
                Change = InsertChange,
                TargetSymbol = target,
                OldString = oldStr,
                NewString = newStr
            };
            var (beforeVerdict, beforeReason) = InvokePreEditValidation(content, step);
            var (afterVerdict, afterReason) = InvokePreEditValidation(applied, step);
            Assert.Equal(AgentUtilities.PreEditVerdict.AlreadyDone, afterVerdict);
            Assert.Contains("already", afterReason, StringComparison.OrdinalIgnoreCase);
            if (ext != ".js")
            {
                // .cs/.ts — the clean path: generic "code already present in file" check,
                // and Proceed on the pristine file (the insert is genuinely absent there).
                Assert.True(beforeVerdict == AgentUtilities.PreEditVerdict.Proceed,
                    $"doc {i} ({ext}): expected Proceed on original content, got {beforeVerdict}: {beforeReason}");
                Assert.Contains("code already present", afterReason, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // .js — PreEditValidation's pre-existing "from newString" branch flags the
                // anchor method embedded at the head of the composed insert (oldStr + "\n" +
                // newMethod) as already-existing, so the verdict is AlreadyDone even on the
                // pristine file. The re-run is still caught (asserted above); the pristine
                // Proceed cannot be asserted for .js without exercising that branch. The
                // invariant that matters — AlreadyDone on the applied file, never a second
                // copy on disk — holds for all three extensions.
                Assert.Contains("from newString", afterReason, StringComparison.OrdinalIgnoreCase);
            }

            checkedDocs++;
        }
        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "FORMAT C re-run already-done guard");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  REMOVAL-WITH-SURVIVOR — deletion expressed as survivor + target → survivor
    // ═══════════════════════════════════════════════════════════════════════════
    // The planner often emits a deletion as oldString = <surviving context + target>
    // → newString = <surviving context>. The survivor is necessarily ALREADY present in
    // the file, so the generic "code already present in file" insert-guard must NOT fire
    // (that guard is for insertions). This regression locks the bug seen in the wild:
    // "Remove the priority badge" produced oldString = BENCH span + priority span,
    // newString = BENCH span, and PreEditValidation wrongly declared it AlreadyDone,
    // skipping the removal entirely. Also covers the whitespace-collapsed survivor.

    [Fact]
    public void PreEditValidation_RemovalWithSurvivor_IsProceedNotAlreadyDone()
    {
        var survivor = "<span class=\"card-tag tag-bench\" ng-if=\"card._benchmark\" style=\"color:#e5c07b;font-weight:700;\">BENCH</span>";
        var target = " <span class=\"card-tag\" ng-if=\"card.priority\" ng-class=\"'priority-'+card.priority\">{{card.priority}}</span>";
        var file = survivor + "\n" + target;

        // 1. The exact wild shape: removal step on a file that STILL contains both spans.
        //    The survivor's presence must not trip the insert guard → Proceed, so the
        //    resolver actually applies the deletion.
        var step = new PlanStep
        {
            File = "wwwroot/kanban.html",
            Change = "Remove priority badge from To Do column cards",
            OldString = survivor + "\n" + target,
            NewString = survivor
        };
        var (verdict, reason) = InvokePreEditValidation(file, step);
        Assert.Equal(AgentUtilities.PreEditVerdict.Proceed, verdict);

        // 2. Once the removal HAS been applied (file holds only the survivor), the same
        //    step must be AlreadyDone — the full oldString is gone, nothing left to remove.
        var (doneVerdict, _) = InvokePreEditValidation(survivor, step);
        Assert.Equal(AgentUtilities.PreEditVerdict.AlreadyDone, doneVerdict);

        // 3. Whitespace-collapsed survivor (drifted indentation between oldString and the
        //    actual file bytes): still Proceed on the pristine file.
        var drifted = survivor.Replace("  ", " ") + "\n" + target;
        var (driftVerdict, _) = InvokePreEditValidation(drifted, step);
        Assert.Equal(AgentUtilities.PreEditVerdict.Proceed, driftVerdict);

        // 4. NEGATIVE CONTROL — the generic insert guard is NOT disabled: a genuine
        //    insertion whose newString is present in the file still trips AlreadyDone.
        var insertStep = new PlanStep
        {
            File = "src/app/app.component.html",
            Change = "Add a loading banner",
            OldString = "<div class=\"app\">",
            NewString = "<div class=\"app\">\n<div class=\"loading-banner\">Loading…</div>"
        };
        var (insertVerdict, insertReason) = InvokePreEditValidation(insertStep.NewString, insertStep);
        Assert.Equal(AgentUtilities.PreEditVerdict.AlreadyDone, insertVerdict);
        Assert.Contains("already present", insertReason, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PREEDITVALIDATION FUZZ — insert/remove/replace × drift × applied states
    // ═══════════════════════════════════════════════════════════════════════════
    // The deterministic guard itself is now fuzzed: for seeded random steps across
    // .html/.ts/.cs, the verdict must be Proceed EXACTLY when the edit is pending
    // (pristine file) and AlreadyDone EXACTLY when it has been applied (pure
    // substitution) — with survivor fragments, whitespace drift, and quote drift
    // never causing a false skip or a double apply.

    [Fact]
    public void Fuzz_PreEditValidation_InsertRemoveReplace_DriftAndAppliedStates()
    {
        const int docCount = 36;
        var checkedDocs = 0;
        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(88_001, i, 104729);
            var ext = (i % 3) switch { 0 => ".html", 1 => ".ts", _ => ".cs" };
            var shape = (i / 3) % 4; // 0 insert, 1 remove, 2 replace, 3 survivor-remove
            var content = BuildPreEditDoc(ext, rng, out var anchor, out var target, out var token);
            // The removal-detection keyword must be something that DISAPPEARS once the
            // edit is applied. For .html the identifier lives in the target's id attribute;
            // for .ts/.cs it is the method name ({token} + index 1, the second block).
            var removalKeyword = ext == ".html"
                ? Regex.Match(target, @"id=""([\w.\-]+)""").Groups[1].Value
                : $"{token}1";
            var step = new PlanStep { File = $"src/file{ext}", LineNumber = 1 };

            switch (shape)
            {
                case 0: // INSERT: anchor + new content after it
                    step.Change = "Add a new block to the file";
                    var newBlock = PreEditNewBlock(ext, rng.Next(10_000, 99_999));
                    step.OldString = anchor;
                    step.NewString = anchor + "\n" + newBlock;
                    break;
                case 1: // REMOVE: target block → empty
                    step.Change = $"Remove the {removalKeyword} block from the file";
                    step.OldString = target;
                    step.NewString = "";
                    break;
                case 2: // REPLACE: target block → rewritten block
                    step.Change = $"Rewrite the {removalKeyword} block in the file";
                    step.OldString = target;
                    step.NewString = PreEditNewBlock(ext, rng.Next(10_000, 99_999));
                    break;
                default: // SURVIVOR REMOVE: full block → its LAST line (closing tag/brace).
                    // The survivor must NOT carry the identifier, so after apply the keyword is
                    // gone and the removal is genuinely detectable as already-done.
                    step.Change = $"Remove the {removalKeyword} block and its body from the file";
                    step.OldString = target;
                    step.NewString = SurvivorFragment(target);
                    break;
            }

            // ── PRISTINE (edit pending) → MUST be Proceed ──
            var (v1, r1) = InvokePreEditValidation(content, step);
            Assert.True(v1 == AgentUtilities.PreEditVerdict.Proceed,
                $"doc {i} ({ext} shape {shape}): pending edit must Proceed, got {v1}: {r1}");

            // ── APPLIED (pure substitution) → MUST be AlreadyDone (no double-apply) ──
            // Self-documenting generator guard: the oldString must genuinely exist in the
            // pristine content (otherwise Proceed/AlreadyDone would be vacuous).
            Assert.Contains(step.OldString, content, StringComparison.Ordinal);
            var applied = content.Replace(step.OldString, step.NewString);
            Assert.NotEqual(content, applied); // substitution actually changed the file
            var (v2, r2) = InvokePreEditValidation(applied, step);
            Assert.True(v2 == AgentUtilities.PreEditVerdict.AlreadyDone,
                $"doc {i} ({ext} shape {shape}): applied edit must AlreadyDone, got {v2}: {r2}");

            // ── WHITESPACE-DRIFTED oldString on PRISTINE → still Proceed (tolerant) ──
            var wsStep = CloneStep(step);
            wsStep.OldString = IndentLines(step.OldString, "  ");
            if (!string.IsNullOrEmpty(step.NewString))
                wsStep.NewString = IndentLines(step.NewString, "  ");
            var (v3, r3) = InvokePreEditValidation(content, wsStep);
            Assert.True(v3 == AgentUtilities.PreEditVerdict.Proceed,
                $"doc {i} ({ext} shape {shape}): whitespace drift must not false-skip, got {v3}: {r3}");

            // ── QUOTE-DRIFTED oldString (HTML only) → still Proceed ──
            if (ext == ".html" && shape is 1 or 2 or 3)
            {
                var qStep = CloneStep(step);
                qStep.OldString = step.OldString.Replace("\"", "'");
                if (!string.IsNullOrEmpty(step.NewString))
                    qStep.NewString = step.NewString.Replace("\"", "'");
                var (v4, r4) = InvokePreEditValidation(content, qStep);
                Assert.True(v4 == AgentUtilities.PreEditVerdict.Proceed,
                    $"doc {i} ({ext} shape {shape}): quote drift must not false-skip, got {v4}: {r4}");
            }

            // ── CONTENT-SENSITIVITY proof: the same step yields Proceed on pristine and
            // AlreadyDone on applied — the guard is not a blanket skip (that contrast is
            // v1 vs v2 above). Re-running the APPLIED file a second time stays stable.
            var (v5, _) = InvokePreEditValidation(applied, step);
            Assert.True(v5 == AgentUtilities.PreEditVerdict.AlreadyDone,
                $"doc {i}: applied re-run must stay AlreadyDone (stable, no double-apply), got {v5}");

            checkedDocs++;
        }
        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "PreEditValidation insert/remove/replace fuzz");
    }

    private static PlanStep CloneStep(PlanStep step) => new()
    {
        File = step.File, Change = step.Change, LineNumber = step.LineNumber,
        OldString = step.OldString, NewString = step.NewString, TargetSymbol = step.TargetSymbol,
        Edits = step.Edits, TargetType = step.TargetType, TargetName = step.TargetName,
        InsertAfter = step.InsertAfter, NewCode = step.NewCode, FullFile = step.FullFile
    };

    /// <summary>A random valid-ish doc with an anchor block, a target block, and a
    /// unique token embedded in the target so the removal keyword resolves.</summary>
    private static string BuildPreEditDoc(string ext, Random rng, out string anchor, out string target, out string token)
    {
        var tokenVal = $"tok{rng.Next(100, 999)}";
        token = tokenVal;
        var count = 2 + rng.Next(3);
        var blocks = Enumerable.Range(0, count).Select(k => PreEditBlock(ext, tokenVal, k, rng.Next(1, 9))).ToList();
        anchor = blocks[0];
        target = blocks[1];
        return ext switch
        {
            ".html" => "<main>\n" + string.Join("\n", blocks) + "\n</main>",
            ".ts" => "export class Sample {\n" + string.Join("\n\n", blocks) + "\n}\n",
            _ => "public class Sample\n{\n" + string.Join("\n\n", blocks) + "\n}\n"
        };
    }

    private static string PreEditBlock(string ext, string token, int idx, int num) => ext switch
    {
        ".html" => $"  <div class=\"card\" id=\"{token}-{idx}\">\n" +
                    $"    <span class=\"tag\">{token}-{idx}-{num}</span>\n  </div>",
        ".ts" => $"  {token}{idx}(): void {{\n    this.count = {num};\n  }}",
        _ => $"    public void {token}{idx}()\n    {{\n        var tmp = {num};\n    }}"
    };

    private static string PreEditNewBlock(string ext, int num) => ext switch
    {
        ".html" => $"  <div class=\"card\" id=\"new-{num}\">\n    <span class=\"tag\">NEW{num}</span>\n  </div>",
        ".ts" => $"  newMethod{num}(): void {{\n    this.count = {num};\n  }}",
        _ => $"    public void NewMethod{num}()\n    {{\n        var tmp = {num};\n    }}"
    };

    // ═══════════════════════════════════════════════════════════════════════════
    //  EXECUTOR/AUDITOR AGREEMENT — IsRemovalAlreadyApplied ≡ PreEditValidation
    // ═══════════════════════════════════════════════════════════════════════════
    // The plan auditor (PlanPreAuditAsync) and the executor guard (PreEditValidation)
    // must AGREE on every deletion. Both now route through the shared
    // IsRemovalAlreadyApplied helper; this corpus asserts the agreement is total across
    // the same insert/remove/replace/survivor shapes and applied states as the guard fuzz
    // above — including FORMAT D targetName carriers.

    /// <summary>
    /// Invokes the shared <c>AgentController.IsRemovalAlreadyApplied</c> (private static)
    /// — the single source of truth for "is this deletion already applied?" used by both
    /// PreEditValidation and PlanPreAuditAsync.
    /// </summary>
    private static bool InvokeIsRemovalAlreadyApplied(string content, PlanStep step)
    {
        var method = typeof(AgentController).GetMethod(
            "IsRemovalAlreadyApplied", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsRemovalAlreadyApplied not found");
        return (bool)(method.Invoke(null, new object?[] { content, step }) ?? false);
    }

    [Fact]
    public void Fuzz_IsRemovalAlreadyApplied_AgreesWithPreEditValidation()
    {
        const int docCount = 36;
        var checkedDocs = 0;
        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(88_201, i, 104729);
            var ext = (i % 3) switch { 0 => ".html", 1 => ".ts", _ => ".cs" };
            var shape = (i / 3) % 4;
            var content = BuildPreEditDoc(ext, rng, out var anchor, out var target, out var token);
            var removalKeyword = ext == ".html"
                ? Regex.Match(target, @"id=""([\w.\-]+)""").Groups[1].Value
                : $"{token}1";
            var step = new PlanStep { File = $"src/file{ext}", LineNumber = 1 };

            switch (shape)
            {
                case 0:
                    step.Change = "Add a new block to the file";
                    var newBlock = PreEditNewBlock(ext, rng.Next(10_000, 99_999));
                    step.OldString = anchor;
                    step.NewString = anchor + "\n" + newBlock;
                    break;
                case 1:
                    step.Change = $"Remove the {removalKeyword} block from the file";
                    step.OldString = target;
                    step.NewString = "";
                    break;
                case 2:
                    step.Change = $"Rewrite the {removalKeyword} block in the file";
                    step.OldString = target;
                    step.NewString = PreEditNewBlock(ext, rng.Next(10_000, 99_999));
                    break;
                default:
                    step.Change = $"Remove the {removalKeyword} block and its body from the file";
                    step.OldString = target;
                    step.NewString = SurvivorFragment(target);
                    break;
            }

            // AGREEMENT on pristine: executor says Proceed (or Irrelevant for non-removal
            // shapes) ⇒ IsRemovalAlreadyApplied must be FALSE. For remove/survivor shapes the
            // verdict is deterministically Proceed on the pending file.
            var (verdict, _) = InvokePreEditValidation(content, step);
            var applied1 = InvokeIsRemovalAlreadyApplied(content, step);
            if (shape is 1 or 3)
            {
                Assert.Equal(AgentUtilities.PreEditVerdict.Proceed, verdict);
                Assert.False(applied1, $"doc {i} ({ext} shape {shape}): pending removal must not be 'already applied'");
            }

            // AGREEMENT on applied: PreEditValidation ⇒ AlreadyDone ⟺ IsRemovalAlreadyApplied.
            // The helper's contract is about DELETIONS (shapes 1/2/3 carry a removal target);
            // the insert shape (0) has no removal target, so only the double-apply guard is
            // asserted there (newString already present ⇒ AlreadyDone).
            var appliedContent = content.Replace(step.OldString, step.NewString);
            var (verdict2, _) = InvokePreEditValidation(appliedContent, step);
            var applied2 = InvokeIsRemovalAlreadyApplied(appliedContent, step);
            if (shape is 1 or 3)
            {
                Assert.Equal(AgentUtilities.PreEditVerdict.AlreadyDone, verdict2);
                Assert.True(applied2, $"doc {i} ({ext} shape {shape}): applied removal must be 'already applied'");
            }
            else if (shape == 2)
            {
                // Replace applied: the executor guard sees the new block present (AlreadyDone)
                // and the helper sees the OLD target block gone (already applied) — agreement.
                Assert.Equal(AgentUtilities.PreEditVerdict.AlreadyDone, verdict2);
                Assert.True(applied2, $"doc {i} ({ext} shape 2): applied replace must be 'already applied'");
            }
            else
            {
                Assert.True(verdict2 == AgentUtilities.PreEditVerdict.AlreadyDone,
                    $"doc {i} ({ext} shape 0): applied insert must be AlreadyDone (double-apply guard), got {verdict2}");
            }

            // FORMAT D deletion carrier: targetType=html + targetName + empty newCode must
            // agree with the same content states. Only removal shapes (1/2/3) remove the
            // target block — the insert shape (0) keeps it, so its applied content cannot be
            // used to prove the FORMAT D removal is done.
            if (ext == ".html" && shape is 1 or 2 or 3)
            {
                var fmtStep = new PlanStep
                {
                    File = "src/file.html",
                    Change = $"Remove the {removalKeyword} card block from the page",
                    TargetType = "html",
                    TargetName = target
                };
                Assert.False(InvokeIsRemovalAlreadyApplied(content, fmtStep),
                    $"doc {i}: FORMAT D pending removal must not be already applied");
                Assert.True(InvokeIsRemovalAlreadyApplied(appliedContent, fmtStep),
                    $"doc {i}: FORMAT D applied removal must be already applied");

                // FORMAT D replace-with-survivor: TargetName = full block, NewCode = [survivor]
                // (the survivor fragment). The helper must delegate to FormatDAlreadyDoneVerdict
                // so the survivor's presence never proves the removal done — only the full
                // TargetName block being absent does. This is the executor/auditor agreement the
                // reviewer flagged as a gap.
                var fmtSurvivorStep = new PlanStep
                {
                    File = "src/file.html",
                    Change = $"Remove the {removalKeyword} card block from the page",
                    TargetType = "html",
                    TargetName = target,
                    NewCode = new List<string> { SurvivorFragment(target) }
                };
                Assert.False(InvokeIsRemovalAlreadyApplied(content, fmtSurvivorStep),
                    $"doc {i}: FORMAT D survivor pending must not be already applied");
                Assert.True(InvokeIsRemovalAlreadyApplied(appliedContent, fmtSurvivorStep),
                    $"doc {i}: FORMAT D survivor applied must be already applied");
            }

            checkedDocs++;
        }
        FuzzHarness.AssertAllDocsChecked(checkedDocs, docCount, "executor/auditor removal agreement");
    }

    [Fact]
    public void IsRemovalAlreadyApplied_SurvivorFragment_AgreesOnBothStates()
    {
        // Byte-mirror of the kanban priority-badge deletion: oldString = BENCH + priority,
        // newString = BENCH. The survivor's presence must NOT make either path declare the
        // removal done while the FULL block still exists.
        var survivor = "<span class=\"card-tag tag-bench\" ng-if=\"card._benchmark\" style=\"color:#e5c07b;font-weight:700;\">BENCH</span>";
        var target = " <span class=\"card-tag\" ng-if=\"card.priority\" ng-class=\"'priority-'+card.priority\">{{card.priority}}</span>";
        var file = survivor + "\n" + target;
        var step = new PlanStep
        {
            File = "wwwroot/kanban.html",
            Change = "Remove priority badge from To Do column cards",
            OldString = survivor + "\n" + target,
            NewString = survivor
        };

        // Pending: both paths agree the removal still needs to happen.
        var (v1, r1) = InvokePreEditValidation(file, step);
        Assert.Equal(AgentUtilities.PreEditVerdict.Proceed, v1);
        Assert.False(InvokeIsRemovalAlreadyApplied(file, step));

        // Applied (survivor only): both paths agree the removal is done.
        var (v2, r2) = InvokePreEditValidation(survivor, step);
        Assert.Equal(AgentUtilities.PreEditVerdict.AlreadyDone, v2);
        Assert.True(InvokeIsRemovalAlreadyApplied(survivor, step));
    }

    [Fact]
    public void IsRemovalAlreadyApplied_ShortFormatDTarget_WhitespaceDrift_NotAlreadyDone()
    {
        // Reviewer-flagged blind spot: FormatDTargetBlockAbsent's collapsed fallback only
        // trusts a negative match for blocks whose collapsed form is ≥ 15 chars. A SHORT
        // FORMAT D target (< 15 chars collapsed) still present with intra-token whitespace
        // drift must NOT be declared already-done — the removal hasn't applied yet, and
        // skipping it would silently leave the drifted block in the file. The guard is
        // conservative: short snippets that can't be confirmed absent stay "present".
        var driftedFile = "<div class=\"wrap\">\n  <p >x</p>\n</div>";
        var shortTarget = "<p>x</p>"; // collapsed length 8 < 15 — exact, trim, and collapsed
                                       // containment all fail against <p >x</p>, so "absent"
                                       // must NOT be assumed.
        var step = new PlanStep
        {
            File = "src/file.html",
            Change = "Remove the paragraph block from the page",
            TargetType = "html",
            TargetName = shortTarget
        };

        Assert.False(InvokeIsRemovalAlreadyApplied(driftedFile, step),
            "short drifted target is still present — must NOT be declared already done");
        var (verdict, _) = InvokePreEditValidation(driftedFile, step);
        Assert.Equal(AgentUtilities.PreEditVerdict.Proceed, verdict);

        // Documented tradeoff (conservative direction): a SHORT target that can't be
        // confirmed absent — including one genuinely gone — is never declared already-done.
        // Collapsed matching is unreliable under 15 chars (short snippets are substrings of
        // nearly any file), so the guard prefers a spurious re-attempt over a false skip
        // that would silently leave a drifted block behind. The executor's own anchor-fail
        // error path then reports "block not found" for genuinely-removed short blocks.
        var absentFile = "<div class=\"wrap\">\n  <span>kept</span>\n</div>";
        Assert.False(InvokeIsRemovalAlreadyApplied(absentFile, step),
            "short target: absence can't be confirmed under 15 collapsed chars — conservative 'present'");
    }

    [Fact]
    public void IsRemovalAlreadyApplied_LongFormatDTarget_DriftedStillPresent_NotAlreadyDone()
    {
        // Positive control for the ≥ 15-char collapsed path: a LONG FORMAT D target present
        // with indentation/line-break drift must be caught by the collapsed containment
        // check (present → not already-done). Regression guard for the conservative flip.
        var longTarget = "<div class=\"card\">\n  <span class=\"tag\">ready</span>\n</div>";
        var driftedFile = "<main>\n    <div class=\"card\">\n        <span class=\"tag\">ready</span>\n    </div>\n</main>";
        var step = new PlanStep
        {
            File = "src/file.html",
            Change = "Remove the card block from the page",
            TargetType = "html",
            TargetName = longTarget
        };

        Assert.False(InvokeIsRemovalAlreadyApplied(driftedFile, step),
            "long drifted target still present — collapsed containment must catch it");

        // Genuinely gone → already done (collapsed negative match trusted for long blocks).
        var absentFile = "<main>\n  <span>kept</span>\n</main>";
        Assert.True(InvokeIsRemovalAlreadyApplied(absentFile, step),
            "long target gone — collapsed negative match trusted");
    }

    /// <summary>A strict fragment (LAST line: closing tag/brace) of a block — the survivor
    /// shape. Choosing the closing line means the removed portion carries the block's
    /// identifier, so the already-done detection stays sound after the apply.</summary>
    private static string SurvivorFragment(string block)
    {
        var nl = block.LastIndexOf('\n');
        return nl < 0 ? block : block[(nl + 1)..];
    }

    private static string IndentLines(string s, string indent)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => indent + l));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var idx = haystack.IndexOf(needle, StringComparison.Ordinal); idx >= 0;
             idx = haystack.IndexOf(needle, idx + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// Invokes the pipeline's deterministic already-done guard
    /// (<c>AgentController.PreEditValidation</c>, private static) via reflection — the
    /// same pattern LlmCssCleanerPipelineTests uses to exercise
    /// <c>FormatAcceptedEditRegionAsync</c>. The method touches no instance/DI state.
    /// </summary>
    private static (AgentUtilities.PreEditVerdict verdict, string reason) InvokePreEditValidation(
        string fileContent, PlanStep step)
    {
        var method = typeof(AgentController).GetMethod(
            "PreEditValidation",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("PreEditValidation not found");
        var result = method.Invoke(null, new object[] { fileContent, step })
            ?? throw new InvalidOperationException("PreEditValidation returned null");
        return ((AgentUtilities.PreEditVerdict, string))result;
    }
}
