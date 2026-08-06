using Xunit;
using Weaver;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// FORMAT C full-chain tests on .cs files WHOSE METHOD BODIES CONTAIN SQL — the agent's
/// exact .cs edit path: Classify → ClassifyIntent → EditStrategyResolver.Decide (Roslyn)
/// → TryReplaceSafe → THEN the .cs-only SQL whitespace pass (AgentController ~4874,
/// AgentCodeFormatting.AutoFixSqlWhitespace on newContent + newStr) plus the post-write
/// verbatim-escape cleanup (PostEditCSharpFixup ~5078). The point of these tests: when an
/// agent edit carries SQL inside a C# verbatim string, the SQL must be PROPERLY FORMATTED
/// too (SELECT* → SELECT *, LIMIT5 → LIMIT 5, FROM( → FROM (…) — not just the C# around it.
/// Asserts the C# edit is the pure substitution with zero sibling drift, and every SQL
/// literal ends up with normalized keyword spacing while clean SQL stays byte-identical.
/// </summary>
public class FormatCSqlEditTests
{
    private static readonly string[] CsMethodNames = { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" };

    /// <summary>Malformed SQL fragments the formatter must repair, with the exact fixed form.</summary>
    private static readonly (string Malformed, string Fixed)[] SqlFixes =
    {
        ("SELECT* FROM users", "SELECT * FROM users"),
        ("SELECT name FROM users LIMIT5", "SELECT name FROM users LIMIT 5"),
        ("SELECT COUNT(*) FROM(users)", "SELECT COUNT(*) FROM (users)"),
        ("INSERT INTO logs VALUES(1, 'x')", "INSERT INTO logs VALUES (1, 'x')"),
        ("SELECT * FROM users WHERE(id = 1)", "SELECT * FROM users WHERE (id = 1)"),
    };

    /// <summary>Already-formatted SQL that must survive AutoFixSqlWhitespace byte-identical.</summary>
    private static readonly string[] CleanSql =
    {
        "SELECT * FROM users WHERE id = 1",
        "SELECT COUNT(*) FROM scores WHERE score > 50",
        "UPDATE users SET name = 'x' WHERE id = 5",
    };

    private const string InsertChange = "Add a new method to the class";
    private static string ReplaceChange(string name) => $"Rewrite the {name} method entirely";

    /// <summary>A .cs method whose body is a single SQL assignment inside a verbatim string.</summary>
    private static string MemberBlockSql(string name, string sql) =>
        $"    public void {name}()\n    {{\n        cmd.CommandText = @\"{sql}\";\n    }}";

    private static string BuildClassWithSql(List<string> names, string sqlBody)
    {
        var members = names.Select(n => MemberBlockSql(n, sqlBody));
        return "public class Sample\n{\n" + string.Join("\n\n", members) + "\n}";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  AutoFixSqlWhitespace — the SQL half of a .cs edit
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("SELECT* FROM users", "SELECT * FROM users")]
    [InlineData("SELECT name FROM users LIMIT5", "SELECT name FROM users LIMIT 5")]
    [InlineData("SELECT COUNT(*) FROM(users)", "SELECT COUNT(*) FROM (users)")]
    [InlineData("INSERT INTO logs VALUES(1, 'x')", "INSERT INTO logs VALUES (1, 'x')")]
    [InlineData("SELECT * FROM users WHERE(id = 1)", "SELECT * FROM users WHERE (id = 1)")]
    public void AutoFixSqlWhitespace_FixesKeywordSpacingInsideSqlStrings(string malformed, string expected)
    {
        var code = $"cmd.CommandText = @\"{malformed}\";";
        var fixedCode = $"cmd.CommandText = @\"{expected}\";";

        Assert.Equal(fixedCode, AgentCodeFormatting.AutoFixSqlWhitespace(code));
        // Idempotent: a second pass must be a byte-identical no-op.
        Assert.Equal(fixedCode, AgentCodeFormatting.AutoFixSqlWhitespace(fixedCode));
    }

    [Theory]
    [InlineData("SELECT * FROM users WHERE id = 1")]
    [InlineData("SELECT COUNT(*) FROM scores WHERE score > 50")]
    [InlineData("UPDATE users SET name = 'x' WHERE id = 5")]
    public void AutoFixSqlWhitespace_CleanSql_ByteIdenticalNoOp(string sql)
    {
        var code = $"cmd.CommandText = @\"{sql}\";";

        Assert.Equal(code, AgentCodeFormatting.AutoFixSqlWhitespace(code));
    }

    [Fact]
    public void AutoFixSqlWhitespace_NonSqlStrings_ByteIdentical()
    {
        var code = "var greeting = \"hello world\";\nvar path = @\"C:\\temp\\file.txt\";";

        Assert.Equal(code, AgentCodeFormatting.AutoFixSqlWhitespace(code));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PostEditCSharpFixup — verbatim-escape cleanup on SQL-looking strings
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PostEditCSharpFixup_ConvertsEscapedNewlinesInSqlVerbatimStrings()
    {
        // A verbatim string holding SQL with literal \r\n escape text (the LLM's common
        // hallucination) must get real line breaks after the post-write fixup.
        var code = "    var sql = @\"SELECT * FROM users\\r\\nWHERE id = 1\";";
        var fixedContent = AgentTextUtilities.PostEditCSharpFixup(code);

        Assert.Contains("SELECT * FROM users\r\nWHERE id = 1", fixedContent);
    }

    [Fact]
    public void PostEditCSharpFixup_LeavesNonSqlVerbatimEscapesUntouched()
    {
        var code = "    var msg = @\"line1\\r\\nline2\";";

        Assert.Equal(code, AgentTextUtilities.PostEditCSharpFixup(code));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Full chain — FORMAT C on .cs with SQL bodies (deterministic docs)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mirror the agent's complete deterministic edit chain for a FORMAT C step on a .cs
    /// file whose method bodies contain SQL: classification → Roslyn resolution →
    /// TryReplaceSafe (pure substitution asserted) → the .cs-only SQL whitespace pass that
    /// re-fixes newContent AND newStr when anything changed (~line 4874). Returns the
    /// pre-pass content and the final post-pass content.
    /// </summary>
    private static (EditStrategy Strategy, EditPlanDecision Decision, string OldStr, string Applied, string Final) RunFormatCSqlChain(
        string content, string changeDesc, string targetSymbol, string newCode, bool insert)
    {
        const string file = "src/Sample.cs";
        var step = new PlanStep { File = file, Change = changeDesc, TargetSymbol = targetSymbol };

        // 1–2. Classification — strategy and intent must be the FORMAT C mapping.
        var strategy = EditClassifier.Classify(step, fileExists: true, ".cs");
        var intent = EditClassifier.ClassifyIntent(step, ".cs");
        var decision = EditStrategyResolver.Decide(file, true, content, changeDesc, intent);
        Assert.Equal(strategy, decision.Strategy);
        Assert.Equal(targetSymbol, decision.TargetName);
        Assert.NotNull(decision.ResolvedOldStr);

        // 3. The AST-extracted oldString is a VERBATIM substring of the file.
        var oldStr = decision.ResolvedOldStr!;
        Assert.True(content.Contains(oldStr, StringComparison.Ordinal),
            $"ResolvedOldStr is not a verbatim substring of the .cs file:\n{oldStr}");

        // 4. Compose newString EXACTLY as AgentController.ResolveEditForStep does for
        //    FORMAT C: insert → oldStr + "\n" + indented; replace → indented.
        var indented = FuzzHarness.FormatSnippetRealign(oldStr, newCode);
        var newStr = insert ? oldStr + "\n" + indented : indented;

        // 5. Apply — pure substitution (no fuzzy/dedupe drift).
        var (replaced, applied, matchError, _) = AgentEditHeuristics.TryReplaceSafe(content, oldStr, newStr);
        Assert.True(replaced, $"TryReplaceSafe failed on .cs doc: {matchError}");
        Assert.Equal(content.Replace(oldStr, newStr), applied);

        // 6. The .cs-only SQL pass: fix whitespace across the whole newContent; when it
        //    changed, the agent re-derives newStr from the fixed content too (~4874).
        //    `applied` stays the PRE-pass pure substitution (what TryReplaceSafe wrote);
        //    `final` is the POST-pass content the agent actually keeps.
        var final = AgentCodeFormatting.AutoFixSqlWhitespace(applied);
        if (final != applied)
        {
            var fixedNewStr = AgentCodeFormatting.AutoFixSqlWhitespace(newStr);
            Assert.NotEqual(newStr, fixedNewStr);
        }

        // Transitive lock: the post-pass file must be EXACTLY the SQL-normalized pure
        // substitution — no drift anywhere beyond the keyword-spacing fix itself.
        Assert.Equal(AgentCodeFormatting.AutoFixSqlWhitespace(content.Replace(oldStr, newStr)), final);

        return (strategy, decision, oldStr, applied, final);
    }

    [Fact]
    public void FormatC_Insert_CsMethodWithSql_SqlFormattedAndCSharpIntact()
    {
        var content = BuildClassWithSql(new List<string> { "Alpha", "Beta" }, CleanSql[0]);
        var newCode = MemberBlockSql("Gamma", "SELECT* FROM users LIMIT5");

        var (strategy, _, _, applied, final) =
            RunFormatCSqlChain(content, InsertChange, "Alpha", newCode, insert: true);

        Assert.Equal(EditStrategy.InsertMethod, strategy);

        // C# structure: class header + anchor + sibling survive byte-identical.
        Assert.Contains("class Sample", final);
        Assert.Contains(MemberBlockSql("Alpha", CleanSql[0]), final);
        Assert.Contains(MemberBlockSql("Beta", CleanSql[0]), final);

        // The SQL pass engaged — the new method's SQL is now properly formatted.
        Assert.NotEqual(applied, final);
        Assert.Contains(MemberBlockSql("Gamma", "SELECT * FROM users LIMIT 5"), final);
        Assert.DoesNotContain("SELECT* FROM users LIMIT5", final);

        // Fully normalized: another pass is a no-op.
        Assert.Equal(final, AgentCodeFormatting.AutoFixSqlWhitespace(final));
    }

    [Fact]
    public void FormatC_Replace_CsMethodWithSql_SqlFormattedAndCSharpIntact()
    {
        var content = BuildClassWithSql(new List<string> { "Alpha", "Beta" }, CleanSql[1]);
        var newCode = MemberBlockSql("Alpha", "INSERT INTO logs VALUES(1, 'x')");

        var (strategy, _, oldStr, applied, final) =
            RunFormatCSqlChain(content, ReplaceChange("Alpha"), "Alpha", newCode, insert: false);

        Assert.Equal(EditStrategy.ReplaceMethod, strategy);

        // Old block consumed; sibling byte-identical.
        Assert.DoesNotContain(oldStr, final);
        Assert.Contains(MemberBlockSql("Beta", CleanSql[1]), final);

        // The SQL pass engaged — the replacement body's SQL is properly formatted.
        Assert.NotEqual(applied, final);
        Assert.Contains(MemberBlockSql("Alpha", "INSERT INTO logs VALUES (1, 'x')"), final);
        Assert.DoesNotContain("VALUES(1, 'x')", final);

        Assert.Equal(final, AgentCodeFormatting.AutoFixSqlWhitespace(final));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CORPUS — .cs FORMAT C full chain with SQL bodies (seeded)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Fuzz_FormatC_CsSqlCorpus_NoCSharpDriftAndSqlNormalized()
    {
        const int docCount = 30;
        var strategyHits = new BranchHitCounter<EditStrategy>(
            new[] { EditStrategy.InsertMethod, EditStrategy.ReplaceMethod }, "C#-SQL corpus");

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(60_113, i, 104729);
            var insert = i % 2 == 0;       // exact 15/15 rotation across 30 docs
            var malformedDoc = (i / 2) % 2 == 0; // alternate malformed/clean sibling SQL

            var names = CsMethodNames.OrderBy(_ => rng.Next()).Take(2 + rng.Next(3)).ToList();
            var (raw, fixedSql) = SqlFixes[i % SqlFixes.Length];
            var docSql = malformedDoc ? raw : CleanSql[i % CleanSql.Length];
            var content = BuildClassWithSql(names, docSql);
            var targetIdx = rng.Next(names.Count);
            var target = names[targetIdx];

            // New code always carries a FIXABLE malformed fragment (rotated) so the SQL
            // pass must engage on every doc; replace keeps the target name, insert adds a
            // brand-new unique method.
            var newFix = SqlFixes[(i + 1) % SqlFixes.Length];
            var newName = insert ? "NewMethod" + i : target;
            var newCode = MemberBlockSql(newName, newFix.Malformed);

            var (strategy, _, _, applied, final) = RunFormatCSqlChain(
                content, insert ? InsertChange : ReplaceChange(target), target, newCode, insert);
            strategyHits.Hit(strategy);
            Assert.Equal(insert ? EditStrategy.InsertMethod : EditStrategy.ReplaceMethod, strategy);

            // ── SQL pass ALWAYS engaged (every doc's new code is malformed) ──
            Assert.NotEqual(applied, final);
            Assert.Equal(final, AgentCodeFormatting.AutoFixSqlWhitespace(final)); // idempotent

            // ── C# structure: class header + every method signature present ──
            Assert.Contains("class Sample", final);
            foreach (var n in names)
                Assert.Contains($"public void {n}()", final);

            // ── Sibling bodies: clean SQL stays, malformed SQL is fixed — byte-exact ──
            foreach (var n in names.Where(n => n != target))
            {
                var expectedSql = malformedDoc ? fixedSql : docSql;
                Assert.Contains(MemberBlockSql(n, expectedSql), final);
            }

            // ── The edited method: present with FIXED SQL; its raw fragment is gone ──
            Assert.Contains(MemberBlockSql(newName, newFix.Fixed), final);
            Assert.DoesNotContain(newFix.Malformed, final);

            // ── Replace consumed the old block; insert keeps the anchor intact ──
            if (!insert)
            {
                Assert.DoesNotContain(MemberBlockSql(target, docSql), final);
            }
            else
            {
                var anchorSql = malformedDoc ? fixedSql : docSql;
                Assert.Contains(MemberBlockSql(target, anchorSql), final);
            }
        }

        // Both branches exercised — the rotation is exact: 15 inserts, 15 replaces.
        Assert.Equal(docCount / 2, strategyHits.Count(EditStrategy.InsertMethod));
        Assert.Equal(docCount / 2, strategyHits.Count(EditStrategy.ReplaceMethod));
    }
}
