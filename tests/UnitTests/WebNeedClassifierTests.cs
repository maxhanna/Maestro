using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// The ONE web-need classifier (Services/WebNeedClassifier.cs) — the unified source of truth
/// behind both the dump-task web-need gate (<c>TaskHintsWebNeed</c>) and the news-digest
/// routing (the single <see cref="WebNeedClassifier.IsNews"/> API). These lock the three-way verdict
/// (None/Web/News) and the news-wins-first ordering: a news-shaped prompt must classify as
/// News even when it also carries a generic hint that would otherwise say Web, so
/// classification and routing can never disagree.
/// </summary>
public class WebNeedClassifierTests
{
    // ── The three-way verdict ─────────────────────────────────────────────────────────────
    [Theory]
    // News: a strong news word, recency+topic+noun, or topic+noun+persist (the
    // invented-article class with no literal "news" word).
    [InlineData("Get a latest AI news article from the web and paste the article in a text file on the desktop", WebNeedClassifier.Kind.News)]
    [InlineData("What are today's top stories in AI?", WebNeedClassifier.Kind.News)]
    [InlineData("Search the web for an interesting and relevant AI article and write the data into a text file on my desktop.", WebNeedClassifier.Kind.News)]
    [InlineData("Find the local Montreal news and insert it into a text document on desktop", WebNeedClassifier.Kind.News)]
    // Web: generic live-data hints, but NOT news-shaped — the plain search/fetch path.
    [InlineData("Search the web for the current weather in London.", WebNeedClassifier.Kind.Web)]
    [InlineData("look up the latest API docs for the Stripe library", WebNeedClassifier.Kind.Web)]
    [InlineData("Fetch the current Bitcoin halving date from the internet", WebNeedClassifier.Kind.Web)]
    [InlineData("Check the latest weaver release version online and save the version to a file", WebNeedClassifier.Kind.Web)]
    [InlineData("search for the add function in the repo and explain it", WebNeedClassifier.Kind.Web)]
    [InlineData("Search for recent AI articles about machine learning advancements", WebNeedClassifier.Kind.Web)]
    // None: plain coding or repo-internal phrasing with no live-data hint.
    [InlineData("Refactor the login component and add tests", WebNeedClassifier.Kind.None)]
    [InlineData("Search the repo for the add function and explain it", WebNeedClassifier.Kind.None)]
    [InlineData("Add a property to the DTO", WebNeedClassifier.Kind.None)]
    public void Classify_ReturnsThreeWayVerdict(string prompt, WebNeedClassifier.Kind expected)
    {
        Assert.Equal(expected, WebNeedClassifier.Classify(prompt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_BlankNeverClassifies(string? prompt)
    {
        Assert.Equal(WebNeedClassifier.Kind.None, WebNeedClassifier.Classify(prompt));
    }

    // ── The news marker (migrated from NewsServiceTests): news-y phrasing routes to the
    // digest, generic web prompts stay on the plain search path — asserted straight through
    // WebNeedClassifier.IsNews now that the NewsService shim is gone. ──
    [Theory]
    [InlineData("Get a latest AI news article from the web and paste the article in a text file on the desktop", true)]
    [InlineData("Search the web for the latest AI news and add a summary line to NOTES.md.", true)]
    [InlineData("What are today's top stories in AI?", true)]
    [InlineData("Show me the latest AI headlines", true)]
    [InlineData("Breaking news in technology", true)]
    [InlineData("Show HN: top trending stories this week", true)]
    // The exact "invented article" failure class — topic + "article" + persist-to-file, with
    // NO recency word and NO literal "news", so only rule C catches it.
    [InlineData("Search the web for an interesting and relevant AI article and write the data into a text file on my desktop.", true)]
    [InlineData("Search the web for an AI article and write it to a text file on my desktop.", true)]
    // Every non-news web prompt already in the suite must stay on the plain search path.
    [InlineData("Search the web for the current weather in London.", false)]
    [InlineData("Search the web for the latest release notes of .NET 10.", false)]
    [InlineData("Check the latest weaver release version online and save the version to a file", false)]
    [InlineData("Fetch the URL https://example.com/pricing from the internet and summarize the Pro plan price.", false)]
    [InlineData("Search for recent AI articles about machine learning advancements", false)]
    [InlineData("Search for recent AI articles about machine learning advancements and summarize the top three", false)]
    [InlineData("Search the web for recent AI breakthroughs and verify each result is a real published paper", false)]
    [InlineData("Search the web for the latest release notes for weaver and add a summary line to NOTES.md.", false)]
    // Widened topic vocabulary: general-topic news must route to the digest, not plain search.
    [InlineData("Find the local Montreal news and insert it into a text document on desktop", true)]
    [InlineData("Get the latest food news and write it to a text file", true)]
    [InlineData("What are today's top stories in sports?", true)]
    [InlineData("Find the latest business headlines and save them to a document", true)]
    [InlineData("Show me today's sports updates", true)]
    // …but a topic alone is not news — no news word/noun, no recency, no persist intent.
    [InlineData("Find the best pizza place in Montreal", false)]
    public void IsNews_Marker(string prompt, bool expected)
    {
        Assert.Equal(expected, WebNeedClassifier.IsNews(prompt));
    }

    // ── News wins first: a generic hint alone must not mask a news verdict ────────────────
    [Theory]
    [InlineData("Search the web for the latest AI news and write it to a file")]
    [InlineData("Get the latest AI news from the internet and save it to my desktop")]
    [InlineData("Fetch a recent AI article from the internet and dump it to a file on the desktop")]
    public void Classify_NewsWinsOverGenericHints(string prompt)
    {
        // "search the web" / "latest" / "internet" / "fetch a" are all generic Web hints — if
        // the hint list were checked first this would come back Web. The news rules must win
        // so the digest router and the dump-task gate read the same News verdict.
        Assert.Equal(WebNeedClassifier.Kind.News, WebNeedClassifier.Classify(prompt));
    }

    // ── The convenience accessors agree with the verdict ──────────────────────────────────
    [Theory]
    [InlineData("Search the web for the latest AI news", true, true)]
    [InlineData("Search the web for the current weather in London", true, false)]
    [InlineData("Refactor the login component and add tests", false, false)]
    public void Accessors_AgreeWithClassify(string prompt, bool webNeed, bool news)
    {
        Assert.Equal(webNeed, WebNeedClassifier.IsWebNeed(prompt));
        Assert.Equal(news, WebNeedClassifier.IsNews(prompt));
    }

    // ── Property: classification is idempotent under case/whitespace folding ─────────────
    // A prompt's verdict must not depend on how it was typed — mixed case, extra spaces,
    // tabs or newlines all fold to the same result. Fuzzed deterministically (fixed seed) and
    // folded with an independent reference implementation, so the invariant is real (a prompt
    // whose folded form hits a multi-word hint like "up to date" must classify identically).
    [Fact]
    public void Classify_Idempotent_UnderCaseAndWhitespaceFolding()
    {
        var rng = new System.Random(20260814);
        var tokens = new[]
        {
            "news", "headline", "headlines", "breaking", "trending", "top stories", "front page",
            "latest", "today's", "todays", "fresh", "ai", "a.i.", "artificial intelligence",
            "machine learning", "tech", "business", "sports", "food", "local", "article",
            "stories", "updates", "events", "write", "save", "paste", "dump", "fetch",
            "file", "desktop", "document", "web", "search", "internet", "online", "current",
            "up to date", "live data", "api docs", "look up", "url", "refactor", "component",
            "login", "dto", "x", "123", "…", "the", "a"
        };
        var separators = new[] { " ", "  ", "\t", "\n", " \n ", " ", ".", ",", "-", "_", "'" };
        for (var i = 0; i < 2000; i++)
        {
            var sb = new System.Text.StringBuilder();
            var wordCount = rng.Next(0, 9);
            for (var w = 0; w < wordCount; w++)
            {
                sb.Append(tokens[rng.Next(tokens.Length)]);
                sb.Append(separators[rng.Next(separators.Length)]);
            }
            var s = sb.ToString();
            // Random casing / surrounding-whitespace mutations before folding.
            if (rng.Next(2) == 0) s = s.ToUpperInvariant();
            if (rng.Next(3) == 0) s = "  " + s + "\t";

            var folded = FoldCaseAndWhitespace(s);
            Assert.True(
                WebNeedClassifier.Classify(s) == WebNeedClassifier.Classify(folded),
                $"classification drifted under folding for fuzz#{i}: \"{s}\" → \"{folded}\"");
            Assert.True(
                WebNeedClassifier.IsWebNeed(s) == WebNeedClassifier.IsWebNeed(folded),
                $"IsWebNeed drifted under folding for fuzz#{i}: \"{s}\" → \"{folded}\"");
        }
    }

    private static string FoldCaseAndWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = true; continue; }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
            pendingSpace = false;
        }
        return sb.ToString();
    }

    // ── Property: IsNews ⇒ IsWebNeed, fuzzed over random prompt fragments ────────────────
    // News is a subset of web-need by construction (Classify returns News ≠ None), but a
    // future refactor could split the two back into independent detectors. Fuzzing random
    // fragment soup through BOTH accessors locks the invariant deterministically (fixed seed)
    // without depending on any specific prompt wording.
    [Fact]
    public void IsNews_Implies_IsWebNeed_AcrossFuzzedFragments()
    {
        var rng = new System.Random(20260813);
        var tokens = new[]
        {
            "news", "headline", "headlines", "breaking", "trending", "top stories", "front page",
            "latest", "today's", "todays", "fresh", "ai", "a.i.", "artificial intelligence",
            "machine learning", "tech", "technology", "business", "sports", "food", "local",
            "article", "articles", "stories", "updates", "events", "write", "save", "paste",
            "dump", "create", "fetch", "file", "desktop", "document", "notes.md", "web",
            "search", "internet", "online", "current", "url", "csv", "data", "refactor",
            "component", "login", "dto", "x", "123", "…", " "
        };
        var separators = new[] { " ", ".", ",", " the ", "-", "_", "\n", "'" };
        for (var i = 0; i < 2000; i++)
        {
            var sb = new System.Text.StringBuilder();
            var wordCount = rng.Next(0, 9);
            for (var w = 0; w < wordCount; w++)
            {
                sb.Append(tokens[rng.Next(tokens.Length)]);
                sb.Append(separators[rng.Next(separators.Length)]);
            }
            var s = sb.ToString();
            // Random casing / surrounding whitespace mutations.
            if (rng.Next(2) == 0) s = s.ToUpperInvariant();
            if (rng.Next(3) == 0) s = "  " + s + "  ";
            Assert.True(
                !WebNeedClassifier.IsNews(s) || WebNeedClassifier.IsWebNeed(s),
                $"IsNews=true but IsWebNeed=false for fuzz#{i}: \"{s}\"");
        }
    }
}
