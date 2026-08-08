using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks in the identifier-aware discovery pass (Services/AgentDiscovery.cs
/// ExtractIdentifierTokens + Services/Bm25Scorer.cs identifier bonuses):
/// snake_case / kebab-case / camelCase / PascalCase / dotted file-name tokens in
/// the prompt are usually the KEY file, method or variable a task targets, so they
/// get an exact-match search against repo paths and content instead of being
/// shattered into generic words by word-splitting BM25.
/// </summary>
public class IdentifierDiscoveryTests : IDisposable
{
    private readonly string _tempRoot;

    public IdentifierDiscoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "weaver-ids-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_tempRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private List<(string file, double score, List<(string token, double contribution)> tokenHits)> Score(
        string prompt, string? identifiers, params string[] files)
    {
        var ids = identifiers == null
            ? new List<string>()
            : identifiers.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return Bm25Scorer.ScoreProjectFiles(prompt, _tempRoot, files.ToList(), CancellationToken.None, ids);
    }

    private static string RepeatTokens(string phrase, int repetitions)
        => string.Join(" ", Enumerable.Repeat(phrase, repetitions));

    // ── ExtractIdentifierTokens ─────────────────────────────────────────────

    [Fact]
    public void ExtractIdentifierTokens_ReturnsSnakeKebabCamelPascalAndDotted()
    {
        var tokens = AgentDiscovery.ExtractIdentifierTokens(
            "create benchmark_test_6 with a things-like-this class, call CreateDirectory() via or_this, and read web_searcher.py");

        Assert.Contains("benchmark_test_6", tokens);
        Assert.Contains("things-like-this", tokens);
        Assert.Contains("CreateDirectory", tokens);
        Assert.Contains("or_this", tokens);
        Assert.Contains("web_searcher.py", tokens);
    }

    [Fact]
    public void ExtractIdentifierTokens_KeepsLeadingUnderscorePrivates()
    {
        var tokens = AgentDiscovery.ExtractIdentifierTokens("the model should call _create_directory instead");

        Assert.Contains("_create_directory", tokens);
    }

    [Fact]
    public void ExtractIdentifierTokens_DropsStopwordOnlyHyphenatedProse()
    {
        // "create-a-folder" is prose-with-hyphens, not a code symbol.
        var tokens = AgentDiscovery.ExtractIdentifierTokens("please create-a-folder and add-a-file");

        Assert.DoesNotContain("create-a-folder", tokens);
        Assert.DoesNotContain("add-a-file", tokens);
    }

    [Fact]
    public void ExtractIdentifierTokens_DropsVersionLikeTokens()
    {
        var tokens = AgentDiscovery.ExtractIdentifierTokens("upgrade to v1.2.3, use dotnet-8.0 and fix 1.x");

        Assert.DoesNotContain("v1.2.3", tokens);
        Assert.DoesNotContain("8.0", tokens);
        Assert.DoesNotContain("1.x", tokens);
    }

    [Fact]
    public void ExtractIdentifierTokens_DropsSubLengthAndAllDigitTokens()
    {
        var tokens = AgentDiscovery.ExtractIdentifierTokens("use a-b and 123_456 now");

        Assert.DoesNotContain("a-b", tokens);   // 3 chars — too short to be a symbol
        Assert.DoesNotContain("123_456", tokens); // not digit-start snake anyway
    }

    [Fact]
    public void ExtractIdentifierTokens_PlainWordsAreNotIdentifiers()
    {
        // No separators, no internal case boundary → nothing identifier-shaped.
        var tokens = AgentDiscovery.ExtractIdentifierTokens("fix the bug in the notepad service");

        Assert.DoesNotContain("notepad", tokens);
        Assert.DoesNotContain("service", tokens);
    }

    [Fact]
    public void ExtractIdentifierTokens_EmptyPrompt_ReturnsEmpty()
    {
        Assert.Empty(AgentDiscovery.ExtractIdentifierTokens(""));
        Assert.Empty(AgentDiscovery.ExtractIdentifierTokens(null));
        Assert.Empty(AgentDiscovery.ExtractIdentifierTokens("   "));
    }

    // ── Scorer: identifier bonuses ──────────────────────────────────────────

    [Fact]
    public void ShortFile_WithExactIdentifierInContent_IsNotSkippedByTokenFloor()
    {
        // Only ~4 tokens — below the 20-token BM25 floor, but it defines or_this,
        // the exact symbol the task names. It must still be scored and rank.
        Write("helper.ts", "function or_this() { return 42; }");
        Write("other.ts", RepeatTokens("alpha beta gamma delta epsilon", 10));

        var result = Score("call or_this somewhere", "or_this", "helper.ts", "other.ts");

        var hit = result.Single(r => r.file == "helper.ts");
        Assert.Contains(hit.tokenHits, h => h.token == "or_this" && h.contribution >= Bm25Scorer.IdentifierExactContentBonus);
    }

    [Fact]
    public void IdentifierPathMatch_OutranksContentMatch_AndExactBeatsLoose()
    {
        Write("benchmark_test_6/readme.md", RepeatTokens("documentation overview", 12)); // path hit
        Write("defines.ts", "const benchmark_test_6 = 'x';" + RepeatTokens(" util data", 8)); // exact content hit
        Write("casing.ts", "const Benchmark_Test_6 = 'x';" + RepeatTokens(" util data", 8)); // loose (case-insensitive) hit
        Write("unrelated.ts", RepeatTokens("alpha beta gamma delta epsilon", 10)); // no hit

        var result = Score("work inside benchmark_test_6", "benchmark_test_6",
            "benchmark_test_6/readme.md", "defines.ts", "casing.ts", "unrelated.ts");

        Assert.Equal("benchmark_test_6/readme.md", result[0].file);
        var pathHit = result.Single(r => r.file == "benchmark_test_6/readme.md");
        Assert.Contains(pathHit.tokenHits, h => h.token == "benchmark_test_6"
            && h.contribution >= Bm25Scorer.IdentifierPathBonus);
        var exactHit = result.Single(r => r.file == "defines.ts");
        Assert.Contains(exactHit.tokenHits, h => h.token == "benchmark_test_6"
            && h.contribution >= Bm25Scorer.IdentifierExactContentBonus);
        var looseHit = result.Single(r => r.file == "casing.ts");
        Assert.Contains(looseHit.tokenHits, h => h.token == "benchmark_test_6"
            && h.contribution >= Bm25Scorer.IdentifierLooseContentBonus
            && h.contribution < Bm25Scorer.IdentifierExactContentBonus);
        Assert.DoesNotContain(result, r => r.file == "unrelated.ts");
    }

    [Fact]
    public void IdentifierTokens_AreAttributedIntoHits_AndSumToScore()
    {
        Write("service.ts", RepeatTokens("notepad user event", 10) + " or_this");

        var result = Score("use or_this in notepad", "or_this", "service.ts");

        var hit = Assert.Single(result);
        Assert.Equal(hit.score, hit.tokenHits.Sum(h => h.contribution), 6);
        Assert.Contains(hit.tokenHits, h => h.token == "or_this");
    }

    [Fact]
    public void NoIdentifiers_WithIdentifiersNull_BehavesLikePlainBm25()
    {
        Write("notepad.service.ts", RepeatTokens("notepad user", 15));

        var result = Score("in notepad, add a user event", null, "notepad.service.ts");

        var hit = Assert.Single(result);
        Assert.Contains(hit.tokenHits, h => h.token == "notepad");
    }

    [Fact]
    public void EmptyQueryTokens_WithIdentifiers_StillRanksByIdentifierHits()
    {
        // Prompt is pure stopwords → BM25 query tokens come up empty; the identifier
        // list is the only signal, and the guard must NOT bail early when ids exist.
        Write("helper.ts", "function or_this() { return 42; }");
        Write("other.ts", RepeatTokens("alpha beta gamma delta epsilon", 10));

        var result = Score("and or but the", "or_this", "helper.ts", "other.ts");

        var hit = Assert.Single(result);
        Assert.Equal("helper.ts", hit.file);
        Assert.Contains(hit.tokenHits, h => h.token == "or_this"
            && h.contribution >= Bm25Scorer.IdentifierExactContentBonus);
    }

    [Fact]
    public void IdentifierBonuses_AreCappedToThreePerFile()
    {
        // A file mentioning several prompt identifiers must not stack unbounded bonuses.
        Write("hub.ts", "or_this and_that maybe_so" + RepeatTokens(" util data", 8));
        Write("single.ts", "or_this" + RepeatTokens(" util data", 8));

        var ids = "or_this,and_that,maybe_so,something_else,another_one,last_one";
        var result = Score("use and or but the", ids, "hub.ts", "single.ts");

        var hub = result.Single(r => r.file == "hub.ts");
        var idContribution = hub.tokenHits
            .Where(h => h.token is "or_this" or "and_that" or "maybe_so" or "something_else" or "another_one" or "last_one")
            .Sum(h => h.contribution);
        // 3 capped hits × exact-content bonus = 36 max, never 6×12 = 72.
        Assert.True(idContribution <= 3 * Bm25Scorer.IdentifierExactContentBonus + 0.001,
            $"expected identifier bonuses capped at 3 hits, got {idContribution}");
    }
}
