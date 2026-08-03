using System.Reflection;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks in the BM25 auto-read ranking behavior (Controllers/AgentController.cs):
/// per-token attribution that sums exactly to the total score, filename/path bonus
/// attribution, top-5 hit trimming, stopword filtering, the empty-query fallback
/// (including the 2-char regex rescue), and the FormatBm25Hits log format that
/// sibling files rely on for plain-path rendering. Both methods are private
/// static, so they are exercised through reflection — if either is ever renamed,
/// these tests fail loudly instead of silently skipping.
/// </summary>
public class Bm25ScoringTests : IDisposable
{
    private static readonly MethodInfo ScoreMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("ScoreProjectFilesWithBm25", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("ScoreProjectFilesWithBm25 static method not found.");

    private static readonly MethodInfo FormatHitsMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("FormatBm25Hits", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("FormatBm25Hits static method not found.");

    private readonly string _tempRoot;

    public Bm25ScoringTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "weaver-bm25-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Runs the scorer against the named files (created via <see cref="Write"/>) in the temp root.</summary>
    private List<(string file, double score, List<(string token, double contribution)> tokenHits)> Score(string prompt, params string[] files)
        => (List<(string file, double score, List<(string token, double contribution)> tokenHits)>)ScoreMethod.Invoke(null,
            new object?[] { prompt, _tempRoot, files.ToList(), CancellationToken.None })!;

    private string FormatHits(string file, double score, List<(string token, double contribution)>? hits)
        => (string)FormatHitsMethod.Invoke(null, new object?[] { file, score, hits })!;

    /// <summary>Repeats a token phrase until it carries at least the min-token threshold (20).</summary>
    private static string RepeatTokens(string phrase, int repetitions)
        => string.Join(" ", Enumerable.Repeat(phrase, repetitions));

    // ── Filename bonus: attribution + exact score accounting ─────────────────

    [Fact]
    public void FilenameBonus_IsAttributedIntoTokenHits_AndSumsToTotalScore()
    {
        Write("notepad.service.ts", RepeatTokens("notepad user addnote userevent", 10));

        var result = Score("in notepad, when user does addNote, add a userEvent", "notepad.service.ts");

        var hit = Assert.Single(result);
        Assert.Equal("notepad.service.ts", hit.file);
        // 'notepad' appears in the filename (+3) and the path (+1) → its token hit
        // must carry that bonus instead of it vanishing into an opaque total.
        var notepadHit = hit.tokenHits.Single(h => h.token == "notepad");
        Assert.True(notepadHit.contribution >= 4.0,
            $"expected 'notepad' to carry the filename bonus, got {notepadHit.contribution}");
        // Attribution is the whole story: token contributions sum exactly to the score.
        Assert.Equal(hit.score, hit.tokenHits.Sum(h => h.contribution), 6);
        // Hits are presented strongest-first for the log line.
        Assert.True(hit.tokenHits.SequenceEqual(hit.tokenHits.OrderByDescending(h => h.contribution)));
    }

    [Fact]
    public void IdenticalContent_DifferentName_ScoreDiffersByExactFilenameBonus()
    {
        Write("notepad.service.ts", RepeatTokens("notepad user", 15));
        Write("other.service.ts", RepeatTokens("notepad user", 15));

        var result = Score("notepad user", "notepad.service.ts", "other.service.ts");

        var named = result.Single(r => r.file == "notepad.service.ts");
        var plain = result.Single(r => r.file == "other.service.ts");
        // Same content, same length, same df — the only difference is +3 (name) + 1 (path)
        // on the 'notepad' token of the named file.
        Assert.Equal(plain.score + 4.0, named.score, 6);
        Assert.Equal(4.0, named.tokenHits.Single(h => h.token == "notepad").contribution
                           - plain.tokenHits.Single(h => h.token == "notepad").contribution, 6);
    }

    // ── Top-5 hit trimming ───────────────────────────────────────────────────

    [Fact]
    public void MoreThanFiveMatchingTokens_TrimsTokenHitsToFive()
    {
        Write("broad.ts", RepeatTokens("alpha beta gamma delta epsilon zeta eta theta", 10));

        var result = Score("alpha beta gamma delta epsilon zeta eta theta", "broad.ts");

        var hit = Assert.Single(result);
        Assert.Equal(5, hit.tokenHits.Count);
        Assert.All(hit.tokenHits, h => Assert.True(h.contribution > 0));
        // The displayed top-5 hits are a subset of all 8 contributions, so their sum is
        // necessarily LESS than the total score — the top-5 are the strongest five.
        Assert.True(hit.tokenHits.Sum(h => h.contribution) < hit.score);
    }

    // ── Stopword filtering ───────────────────────────────────────────────────

    [Fact]
    public void StopwordOnlyPrompt_ReturnsNoFiles()
    {
        Write("anything.ts", RepeatTokens("the and for with this that", 20));

        var result = Score("the and for with this that", "anything.ts");

        Assert.Empty(result);
    }

    [Fact]
    public void StopwordTokensInFile_DoNotContributeToScore()
    {
        // noise.ts carries plenty of REAL (non-stopword) tokens plus stopwords, so it is
        // not skipped for falling below the 20-token floor — yet none of its tokens match
        // the query, so it must not appear in the result. real.ts is the sole match.
        Write("real.ts", RepeatTokens("notepad user", 15));
        Write("noise.ts", RepeatTokens("alpha beta gamma delta epsilon zeta eta theta the and for with this that", 3));

        var result = Score("notepad user", "real.ts", "noise.ts");

        Assert.Single(result);                 // noise.ts matches no query token
        Assert.Equal("real.ts", result[0].file);
    }

    // ── Empty-query fallback path ────────────────────────────────────────────

    [Fact]
    public void EmptyOrWhitespacePrompt_ReturnsNoFiles()
    {
        Write("anything.ts", RepeatTokens("something relevant", 20));

        Assert.Empty(Score("", "anything.ts"));
        Assert.Empty(Score("   ", "anything.ts"));
        Assert.Empty(Score("\n\t  \r\n", "anything.ts"));
    }

    [Fact]
    public void TwoCharTokens_FallbackRegexRescuesThem()
    {
        // ExtractMeaningfulKeywords needs 3+ letter words; "hi jo" is empty there,
        // so the [a-z0-9_]{2,} fallback is the only path that scores 'jo'/'hi'.
        Write("jo.ts", RepeatTokens("hi jo", 15));

        var result = Score("hi jo", "jo.ts");

        var hit = Assert.Single(result);
        Assert.Contains(hit.tokenHits, h => h.token == "hi");
        var jo = hit.tokenHits.Single(h => h.token == "jo");
        Assert.True(jo.contribution >= 4.0, $"expected 'jo' filename bonus, got {jo.contribution}");
        Assert.Equal(hit.score, hit.tokenHits.Sum(h => h.contribution), 6);
    }

    // ── File eligibility guards ──────────────────────────────────────────────

    [Fact]
    public void FileUnderTwentyTokens_IsSkipped()
    {
        Write("tiny.ts", "notepad user"); // only 2 tokens → below the 20-token floor

        var result = Score("notepad user", "tiny.ts");

        Assert.Empty(result);
    }

    [Fact]
    public void GeneratedFileName_IsSkipped()
    {
        Write("package.json", RepeatTokens("notepad user", 20)); // in _bm25GeneratedNames
        Write("real.ts", RepeatTokens("notepad user", 20));

        var result = Score("notepad user", "package.json", "real.ts");

        Assert.DoesNotContain(result, r => r.file == "package.json");
        Assert.Equal("real.ts", Assert.Single(result).file);
    }

    // ── FormatBm25Hits (log rendering, incl. sibling plain-path fallback) ────

    [Fact]
    public void FormatBm25Hits_NullOrEmptyTokenHits_RendersPlainPath()
    {
        // Sibling files (AddTemplateStyleSiblings) never went through BM25, so they
        // arrive with no token hits — they must render as their bare path even when
        // the score argument is high (it never applies without hits).
        Assert.Equal("notepad.component.html", FormatHits("notepad.component.html", 9.0, null));
        Assert.Equal("notepad.component.css", FormatHits("notepad.component.css", 9.0,
            new List<(string token, double contribution)>()));
    }

    [Fact]
    public void FormatBm25Hits_WithTokenHits_ShowsTokenAttribution()
    {
        var hits = new List<(string token, double contribution)>
        {
            ("notepad", 4.2),
            ("note", 1.1)
        };

        Assert.Equal("notepad.service.ts ← notepad(4.2), note(1.1)", FormatHits("notepad.service.ts", 7.3, hits));
    }

    [Fact]
    public void FormatBm25Hits_BelowThreshold_CollapsesToPlainPath()
    {
        // Marginal matches carry token hits but a low total score — the log must
        // show the bare path instead of a long token list, keeping noisy prompts readable.
        var hits = new List<(string token, double contribution)>
        {
            ("notepad", 1.1),
            ("note", 0.4)
        };

        Assert.Equal("marginal.ts", FormatHits("marginal.ts", 1.5, hits));
        Assert.Equal("marginal.ts", FormatHits("marginal.ts", 1.999, hits));
    }

    [Fact]
    public void FormatBm25Hits_AtOrAboveThreshold_ShowsAttribution()
    {
        var hits = new List<(string token, double contribution)>
        {
            ("notepad", 4.2)
        };

        // Boundary: exactly at the 2.0 threshold attribution still shows.
        Assert.Equal("notepad.ts ← notepad(4.2)", FormatHits("notepad.ts", 2.0, hits));
        Assert.Equal("notepad.ts ← notepad(4.2)", FormatHits("notepad.ts", 2.1, hits));
    }
}
