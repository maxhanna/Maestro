using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// The NewsService: fresh-news fetching (parallel RSS/API sources, URL+title dedup,
/// round-robin interleave, snippet/LLM summaries, markdown digest) plus the "news marker"
/// routing inside ExecuteWebPlanStep. The guard behind "a model asked for the latest AI
/// news and invented a headline with a fake example.com URL": with the marker, the search
/// step returns REAL dated items with REAL URLs the agent can fetch or paste; without it,
/// the search stays on the plain DuckDuckGo path.
/// </summary>
public class NewsServiceTests
{
    private const string NewsPrompt =
        "Get a latest AI news article from the web and paste the article in a text file on the desktop.";
    private const string PlainPrompt =
        "Search the web for recent AI breakthroughs and verify each result is a real published paper.";
    private const string Query = "latest AI news";

    // ── The routing marker: news-y phrasing routes to the digest, generic web prompts stay put ──
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
    public void LooksLikeNewsQuery_Marker(string prompt, bool expected)
    {
        Assert.Equal(expected, NewsService.LooksLikeNewsQuery(prompt));
    }

    // ── Digest pipeline: parallel sources → dedup → round-robin → snippets → markdown ──
    // VB: [V1, V2, V3] · TC: [T1 (SAME TITLE as V1 → deduped away), T2] · HN: [H1] ·
    // arXiv: [A1] · Lobsters: [L1] — the other four RSS feeds return empty.
    [Fact]
    public async Task FetchNewsAsync_ParsesDedupsInterleaves_AndAssemblesDigest()
    {
        var factory = new NewsScriptedClientFactory();
        var service = new NewsService(factory);

        var digest = await service.FetchNewsAsync("AI news", 8, CancellationToken.None);

        Assert.Contains("# AI News — ", digest);
        Assert.Contains("\"AI news\"", digest);
        Assert.Contains("7 item(s) from VentureBeat, TechCrunch, Hacker News, Lobsters, arXiv.", digest);
        // Every source parsed into the digest.
        Assert.Contains("AI Startup Raises $200M", digest);
        Assert.Contains("New AI Model Beats Benchmarks", digest);
        Assert.Contains("AI Chip Design News", digest);
        Assert.Contains("AI Robot Vacuum Review", digest);
        Assert.Contains("Show HN: Local AI News Reader", digest);
        Assert.Contains("An Efficient Transformer for Multi-Task Policy Learning", digest);
        Assert.Contains("Lobsters: AI News Thread", digest);
        // Dedup: T1 ("AI Startup Raises $200M") was the same story as V1 → collapsed, and the
        // kept copy is V1's (richer snippet, VentureBeat URL) — never the tracker's URL.
        Assert.Contains("https://vb.example.com/1", digest);
        Assert.DoesNotContain("https://tc.example.com/1", digest);
        // Interleave order is round-robin by feed: V1, T2, H1, L1, A1, V2, V3.
        AssertOrder(digest,
            "AI Startup Raises $200M", "AI Robot Vacuum Review", "Show HN: Local AI News Reader",
            "Lobsters: AI News Thread", "An Efficient Transformer for Multi-Task Policy Learning",
            "New AI Model Beats Benchmarks", "AI Chip Design News");
        // Long feed descriptions (>200 chars) become the summary — no LLM call needed.
        Assert.Contains("VentureBeat long-form description", digest);
        // The real article URLs landed (fetchable by a follow-up _web_fetch, not invented).
        Assert.Contains("https://arxiv.org/abs/2406.07539", digest);
        Assert.Contains("Feed: Hacker News", digest);
        Assert.Contains("Feed: arXiv", digest);
        Assert.Contains("Feed: Lobsters", digest);
    }

    [Fact]
    public async Task FetchNewsAsync_DeadFeed_DoesNotKillDigest()
    {
        var factory = new NewsScriptedClientFactory { FailTechCrunch = true };
        var service = new NewsService(factory);

        var digest = await service.FetchNewsAsync("AI news", 8, CancellationToken.None);

        // TechCrunch vanished; every other source still contributed.
        Assert.Contains("AI Startup Raises $200M", digest);
        Assert.Contains("6 item(s) from VentureBeat, Hacker News, Lobsters, arXiv.", digest);
        Assert.DoesNotContain("TechCrunch", digest);
    }

    [Fact]
    public async Task FetchNewsAsync_AllSourcesDead_ReturnsNoResultsDigest_NoThrow()
    {
        var factory = new NewsScriptedClientFactory { FailAll = true };
        var service = new NewsService(factory);

        var digest = await service.FetchNewsAsync("AI news", 8, CancellationToken.None);

        Assert.Contains("# AI News", digest);
        Assert.Contains("No fresh items could be fetched", digest);
    }

    [Fact]
    public async Task FetchNewsAsync_ThinSnippet_InjectedSummarizer_UsesLlmSummary()
    {
        var factory = new NewsScriptedClientFactory { ServeArticleHtml = true };
        var service = new NewsService(factory,
            (articleText, _) => Task.FromResult<string?>("INJECTED SUMMARY of [" + articleText[..20] + "]"));

        var digest = await service.FetchNewsAsync("AI news", 8, CancellationToken.None);

        // The thin snippet (< 200 chars) triggered the article fetch + summarizer path.
        Assert.Contains("INJECTED SUMMARY", digest);
        Assert.Contains("Thin Snippet Story", digest);
    }

    // ── Routing inside ExecuteWebPlanStep (the live _web_search step) ──
    [Fact]
    public async Task ExecuteWebPlanStep_NewsPrompt_ReturnsFreshDigest_NotInventedHeadline()
    {
        var factory = new NewsScriptedClientFactory();
        var controller = BuildRoutingController(factory, Path.GetTempPath());

        var wr = await RunWebSearchStep(controller, NewsPrompt, Query);

        Assert.Equal("done", wr.GetValueOrDefault("status")?.ToString());
        var outp = wr.GetValueOrDefault("output")?.ToString() ?? "";
        Assert.Contains("# AI News — ", outp);
        // A REAL title + REAL URL from the live feed — exactly what the pre-fix run invented.
        Assert.Contains("AI Startup Raises $200M", outp);
        Assert.Contains("https://vb.example.com/1", outp);
        Assert.DoesNotContain("example.com/fakearticle", outp);
    }

    [Fact]
    public async Task ExecuteWebPlanStep_ArticleWritePrompt_ReturnsFreshDigest_NotInventedHeadline()
    {
        // The exact run that produced the "invented headline + fake URL" failure: an AI
        // article must be written to a desktop file, with NO "news" word and NO recency
        // word — only the persist-intent rule C routes it to the digest.
        var factory = new NewsScriptedClientFactory();
        var controller = BuildRoutingController(factory, Path.GetTempPath());

        var wr = await RunWebSearchStep(controller,
            "Search the web for an interesting and relevant AI article and write the data into a text file on my desktop.",
            "AI research breakthroughs latest");

        Assert.Equal("done", wr.GetValueOrDefault("status")?.ToString());
        var outp = wr.GetValueOrDefault("output")?.ToString() ?? "";
        Assert.Contains("# AI News — ", outp);
        Assert.Contains("AI Startup Raises $200M", outp);
        Assert.DoesNotContain("example.com/fakearticle", outp);
    }

    [Fact]
    public async Task ExecuteWebPlanStep_NonNewsPrompt_StaysOnDuckDuckGo()
    {
        var factory = new NewsScriptedClientFactory();
        var controller = BuildRoutingController(factory, Path.GetTempPath());

        var wr = await RunWebSearchStep(controller, PlainPrompt, "AI research breakthroughs latest");

        var outp = wr.GetValueOrDefault("output")?.ToString() ?? "";
        Assert.Contains("## Results", outp); // DuckDuckGo format, untouched
        Assert.DoesNotContain("# AI News", outp);
    }

    private static void AssertOrder(string haystack, params string[] needles)
    {
        var prev = -1;
        foreach (var n in needles)
        {
            var idx = haystack.IndexOf(n, StringComparison.Ordinal);
            Assert.True(idx > prev, $"'{n}' should come after the previous needle (prev={prev}, idx={idx})");
            prev = idx;
        }
    }

    private static async Task<Dictionary<string, object?>> RunWebSearchStep(
        AgentController controller, string prompt, string query)
    {
        var method = typeof(AgentController).GetMethod("ExecuteWebPlanStep", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ExecuteWebPlanStep not found");
        var allResults = new List<object>();
        var task = (Task<(int stepIndex, string discoveryContext)>)method.Invoke(controller, new object?[]
        {
            "_web_search", query, prompt, Path.GetTempPath(), /*emitSse*/ false, CancellationToken.None,
            allResults, new List<PlanStep>(), /*itemIdx*/ 0, /*stepIndex*/ 0, /*discoveryContext*/ "", new StringBuilder()
        })!;
        await task;
        return (Dictionary<string, object?>)Assert.Single(allResults);
    }

    private static AgentController BuildRoutingController(IHttpClientFactory factory, string baseDir)
    {
        var db = new DatabaseService(
            Path.Combine(baseDir, "weaver_news_", "weaver.db"),
            Path.Combine(baseDir, "weaver_news_"),
            Path.Combine(baseDir, "weaver_news_", "weaverconfig.json"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Editor:DisableLLMRetries"] = "true" })
            .Build();
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        SetField(controller, "_clientFactory", factory);
        SetField(controller, "_config", config);
        SetField(controller, "_db", db);
        SetField(controller, "_configFile", new ConfigFileService(db));
        return controller;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {name} not found");
        field.SetValue(target, value);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // Scripted HTTP: each source serves a deterministic fixture; the four extra RSS
    // feeds serve empty feeds; article hosts serve HTML only in the summarizer test.
    // ═══════════════════════════════════════════════════════════════════════════════
    private sealed class NewsScriptedClientFactory : IHttpClientFactory
    {
        public bool FailTechCrunch;
        public bool FailAll;
        public bool ServeArticleHtml;
        public readonly List<string> Gets = new();

        public HttpClient CreateClient(string name) => new(new ScriptedHandler(this));
        public HttpClient CreateClient() => CreateClient("default");

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly NewsScriptedClientFactory _owner;
            public ScriptedHandler(NewsScriptedClientFactory owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                lock (_owner.Gets) _owner.Gets.Add(request.RequestUri?.ToString() ?? "");
                if (request.Method == HttpMethod.Post)
                    return Task.FromResult(Json(new { choices = new[] { new { message = new { content = "LLM SUMMARY" } } } }));
                if (_owner.FailAll)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                var host = request.RequestUri?.Host ?? "";
                if (host.Contains("duckduckgo"))
                    return Task.FromResult(Json(new
                    {
                        AbstractText = "Survey text", AbstractURL = "https://example.com/ai-overview", Answer = "",
                        RelatedTopics = new object[] { new { Text = "AlphaFold 3 predicts protein structures", FirstURL = "https://example.com/alphafold3" } }
                    }));
                if (host.Contains("hn.algolia"))
                    return Task.FromResult(Json(new
                    {
                        hits = new[]
                        {
                            new { title = "Show HN: Local AI News Reader", url = "https://example.com/hn-1",
                                  created_at = "2026-08-11T10:00:00Z", objectID = "1", story_text = "" }
                        }
                    }));
                if (host.Contains("lobste.rs"))
                    return Task.FromResult(Json(new[]
                    {
                        new { title = "Lobsters: AI News Thread", url = "https://lobste.rs/s/ai123",
                              created_at = "2026-08-11T10:00:00Z", description = "Discussion of the latest AI news." }
                    }));
                if (host.Contains("export.arxiv"))
                    return Task.FromResult(Text(ArxivAtom));
                if (host.Contains("techcrunch") && _owner.FailTechCrunch)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                if (host.Contains("venturebeat"))
                    return Task.FromResult(Text(_owner.ServeArticleHtml ? ThinSnippetRss : VentureBeatRss));
                if (host.Contains("techcrunch"))
                    return Task.FromResult(Text(TechCrunchRss));
                if (host.Contains("theverge") || host.Contains("arstechnica") ||
                    host.Contains("technologyreview") || host.Contains("wired"))
                    return Task.FromResult(Text(Rss()));
                if (host.Contains("example.com") && _owner.ServeArticleHtml)
                    return Task.FromResult(Text("<html><body><article>" + new string('x', 300) + "</article></body></html>"));
                return Task.FromResult(Json(new { }));
            }
        }

        private static HttpResponseMessage Json(object obj)
            => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Text(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

        private static string Rss(params (string title, string link, string desc)[] items)
            => "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel>" +
               string.Concat(items.Select(i =>
                   $"<item><title>{i.title}</title><link>{i.link}</link><description><![CDATA[{i.desc}]]></description></item>")) +
               "</channel></rss>";

        private static string VentureBeatRss => Rss(
            ("AI Startup Raises $200M", "https://vb.example.com/1",
                "VentureBeat long-form description " + new string('z', 300)),
            ("New AI Model Beats Benchmarks", "https://vb.example.com/2",
                "A model beats benchmarks " + new string('y', 250)),
            ("AI Chip Design News", "https://vb.example.com/3",
                "Chip design " + new string('x', 220)));

        private static string TechCrunchRss => Rss(
            ("AI Startup Raises $200M", "https://tc.example.com/1",
                "TechCrunch's shorter take on the same story"),
            ("AI Robot Vacuum Review", "https://tc.example.com/2",
                "Robot vacuum review " + new string('w', 260)));

        // The summarizer test's feed: a THIN snippet (< 200 chars) whose article URL lives on
        // example.com — so the item triggers the article-fetch + injected-summarizer path.
        private static string ThinSnippetRss => Rss(
            ("Thin Snippet Story", "https://example.com/thin-story", "A very short teaser."));

        private static string ArxivAtom =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\">" +
            "<entry><title>An Efficient Transformer for Multi-Task Policy Learning</title>" +
            "<id>https://arxiv.org/abs/2406.07539</id><published>2026-08-10T00:00:00Z</published>" +
            "<summary>Transformer architecture for multi-task robot policies.</summary></entry></feed>";
    }
}
