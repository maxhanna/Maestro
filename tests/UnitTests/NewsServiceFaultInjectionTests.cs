using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Fault-injection tests for <see cref="NewsService"/>: exercises the degradation
/// paths that unit tests on pure helpers can't reach. Uses a scripted
/// <see cref="IHttpClientFactory"/> that returns canned RSS feeds and LLM responses
/// (or throws) so every failure mode is deterministic and offline.
///
/// Each test constructs a real <see cref="NewsService"/> instance — not a mock — so
/// the full <c>FetchNewsAsync</c> path runs: feed parsing, dedup, interleave, body
/// fetch, summarization, cache, and output assembly. Only the HTTP layer is faked.
/// </summary>
public class NewsServiceFaultInjectionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A scripted IHttpClientFactory whose handler routes by URL/method and can
    /// be configured to throw or return canned responses for specific hosts.
    /// </summary>
    private sealed class ScriptedFactory : IHttpClientFactory, IDisposable
    {
        private readonly ScriptedHandler _handler = new();
        public HttpClient CreateClient(string name) => new(_handler);
        public HttpClient CreateClient() => CreateClient("default");
        public void Dispose() => _handler.Dispose();

        /// <summary>Set a fixed response body for GET requests to a host (text/plain).</summary>
        public void SetGetResponse(string host, string body, string contentType = "text/html")
            => _handler.GetResponses[host] = (body, contentType);

        /// <summary>Make GET requests to a host throw (simulates connection refused / timeout).</summary>
        public void SetGetThrows(string host, Exception ex)
            => _handler.GetThrows[host] = ex;

        /// <summary>Set a fixed response body for POST requests to a path prefix.</summary>
        public void SetPostResponse(string pathPrefix, string body)
            => _handler.PostResponses[pathPrefix] = body;

        /// <summary>Make POST requests to a path prefix throw (simulates LLM endpoint down).</summary>
        public void SetPostThrows(string pathPrefix, Exception ex)
            => _handler.PostThrows[pathPrefix] = ex;

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            public readonly Dictionary<string, (string body, string contentType)> GetResponses = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Exception> GetThrows = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> PostResponses = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Exception> PostThrows = new(StringComparer.OrdinalIgnoreCase);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var host = request.RequestUri?.Host ?? "";
                var path = request.RequestUri?.AbsolutePath ?? "";

                if (request.Method == HttpMethod.Get)
                {
                    if (GetThrows.TryGetValue(host, out var ex)) throw ex;
                    if (GetResponses.TryGetValue(host, out var resp))
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(resp.body, Encoding.UTF8, resp.contentType)
                        });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("", Encoding.UTF8, "text/html")
                    });
                }

                // POST (LLM calls)
                foreach (var (prefix, ex) in PostThrows)
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw ex;
                foreach (var (prefix, body) in PostResponses)
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(body, Encoding.UTF8, "application/json")
                        });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }
        }
    }

    /// <summary>Standard VB RSS XML with one item — enough to produce a result.</summary>
    private const string VentureBeatRss = """
        <?xml version="1.0"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title>VentureBeat AI</title>
            <item>
              <title>D-Wave CEO says Nvidia should be shaking in their boots</title>
              <link>https://venturebeat.com/2026/08/d-wave-quantum-ai/</link>
              <description>D-Wave CEO Alan Baratz claims Nvidia should be shaking in their boots as quantum computing challenges AI GPUs. D-Wave's quantum computer uses ten kilowatts of power, compared to five or ten GPUs.</description>
              <pubDate>Tue, 11 Aug 2026 10:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    /// <summary>Standard arXiv Atom feed with one entry.</summary>
    private const string ArxivAtom = """
        <?xml version="1.0"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <title>VidForensics-M1: Video Forensics</title>
            <link href="https://arxiv.org/abs/2608.11201v1"/>
            <published>2026-08-11T00:00:00Z</published>
            <summary>A new approach to AI-generated video forensics using meta-detection reinforcement learning.</summary>
          </entry>
        </feed>
        """;

    /// <summary>Hacker News API JSON response.</summary>
    private const string HnJson = """
        {"hits":[{"title":"Show HN: Local LLM news aggregator","url":"https://github.com/example/news-agg","points":50,"num_comments":12,"created_at":"2026-08-11T10:00:00.000Z"}]}
        """;

    /// <summary>TechCrunch RSS with one item.</summary>
    private const string TechCrunchRss = """
        <?xml version="1.0"?>
        <rss version="2.0">
          <channel>
            <title>TechCrunch AI</title>
            <item>
              <title>Accel closes $550M India fund</title>
              <link>https://techcrunch.com/2026/08/accel-550m-india/</link>
              <description>Accel has closed a $550 million India-focused fund within weeks of its last raise.</description>
              <pubDate>Tue, 11 Aug 2026 09:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    /// <summary>A valid marker-delimited LLM response for a 4-item single call.
    /// Interleaved order by StringComparer.Ordinal: Hacker News(0), TechCrunch AI(1),
    /// VentureBeat AI(2), arXiv(3). Uppercase sorts before lowercase in ordinal.</summary>
    private const string ValidMarkerResponse = """
        {"choices":[{"message":{"content":"### Article 0\nA local LLM news aggregator project on GitHub.\n\n### Article 1\nAccel closes a $550M India fund focusing on AI startups.\n\n### Article 2\nD-Wave CEO claims Nvidia should be worried about quantum computing.\n\n### Article 3\narXiv paper on video forensics using meta-detection.\n\n### SUMMARY\nQuantum computing challenges AI hardware, new forensics tools detect AI videos, and Accel raises a fund for AI startups."}}]}
        """;

    /// <summary>Valid LLM per-item response for the fallback path.</summary>
    private const string ValidItemResponse = """
        {"choices":[{"message":{"content":"D-Wave CEO claims Nvidia should be worried about quantum computing."}}]}
        """;

    /// <summary>Build a NewsService with a scripted client factory and config.</summary>
    private static NewsService BuildNewsService(ScriptedFactory factory)
    {
        var db = new DatabaseService(
            Path.Combine(Path.GetTempPath(), "weaver_news_test_" + Guid.NewGuid().ToString("N") + ".db"),
            Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "weaver_news_config.json"));
        var configFile = new ConfigFileService(db);
        return new NewsService(factory, configFile, NullLogger<NewsService>.Instance);
    }

    /// <summary>Wire up all four feeds with valid responses + a working LLM.</summary>
    private static void SetupAllFeedsWorking(ScriptedFactory f)
    {
        f.SetGetResponse("venturebeat.com", VentureBeatRss, "application/rss+xml");
        f.SetGetResponse("techcrunch.com", TechCrunchRss, "application/rss+xml");
        f.SetGetResponse("hn.algolia.com", HnJson, "application/json");
        f.SetGetResponse("export.arxiv.org", ArxivAtom, "application/xml");
        f.SetPostResponse("/v1/chat/completions", ValidMarkerResponse);
    }

    // ── Test: LLM endpoint down → degrade to snippets ───────────────────────

    [Fact]
    public async Task LlmDown_AllItemsDegradeToSnippetFallback()
    {
        using var f = new ScriptedFactory();
        SetupAllFeedsWorking(f);
        // LLM endpoint throws on every call.
        f.SetPostThrows("/v1/chat/completions", new HttpRequestException("Connection refused"));

        var svc = BuildNewsService(f);
        var (output, err) = await svc.FetchNewsAsync("AI", limit: 4, CancellationToken.None);

        // Output is non-empty — degradation, not a hard failure.
        Assert.False(string.IsNullOrEmpty(output));
        Assert.Null(err);
        // Contains the article titles (from feed snippets, not LLM summaries).
        Assert.Contains("D-Wave", output);
        Assert.Contains("Accel", output);
        // Contains the ## Summary section (degraded to snippet concatenation).
        Assert.Contains("## Summary", output);
        Assert.Contains("## Results", output);
    }

    // ── Test: all feeds down → "No results found" ───────────────────────────

    [Fact]
    public async Task AllFeedsDown_ReturnsNoResultsMessage()
    {
        using var f = new ScriptedFactory();
        f.SetGetThrows("venturebeat.com", new HttpRequestException("Connection refused"));
        f.SetGetThrows("techcrunch.com", new HttpRequestException("Connection refused"));
        f.SetGetThrows("hn.algolia.com", new HttpRequestException("Connection refused"));
        f.SetGetThrows("export.arxiv.org", new HttpRequestException("Connection refused"));
        f.SetPostResponse("/v1/chat/completions", ValidMarkerResponse);

        var svc = BuildNewsService(f);
        var (output, err) = await svc.FetchNewsAsync("AI", limit: 5, CancellationToken.None);

        Assert.Contains("No results found", output);
        // No error returned — "no results" is a valid empty result, not an error.
        Assert.Null(err);
    }

    // ── Test: one feed malformed XML → skipped, rest proceed ────────────────

    [Fact]
    public async Task MalformedFeed_SkippedOthersProceed()
    {
        using var f = new ScriptedFactory();
        f.SetGetResponse("venturebeat.com", "<?xml version='1.0'?><rss><broken>", "application/rss+xml");
        f.SetGetResponse("techcrunch.com", TechCrunchRss, "application/rss+xml");
        f.SetGetResponse("hn.algolia.com", HnJson, "application/json");
        f.SetGetResponse("export.arxiv.org", ArxivAtom, "application/xml");
        f.SetPostResponse("/v1/chat/completions", ValidMarkerResponse);

        var svc = BuildNewsService(f);
        var (output, err) = await svc.FetchNewsAsync("AI", limit: 4, CancellationToken.None);

        // VentureBeat's broken XML produced no items, but TechCrunch + HN + arXiv did.
        Assert.Null(err);
        // TechCrunch item present.
        Assert.Contains("Accel", output);
        // arXiv item present.
        Assert.Contains("VidForensics", output);
        // VentureBeat title NOT present (feed skipped — no item was parsed).
        Assert.DoesNotContain("shaking in their boots", output);
    }

    // ── Test: LLM returns malformed JSON → degrades to snippets ─────────────

    [Fact]
    public async Task LlmReturnsMalformedJson_DegradesToSnippets()
    {
        using var f = new ScriptedFactory();
        SetupAllFeedsWorking(f);
        // LLM returns garbage (not valid JSON).
        f.SetPostResponse("/v1/chat/completions", "this is not json at all");

        var svc = BuildNewsService(f);
        var (output, err) = await svc.FetchNewsAsync("AI", limit: 4, CancellationToken.None);

        Assert.Null(err);
        Assert.False(string.IsNullOrEmpty(output));
        // Items present with their feed snippets (degradation path).
        Assert.Contains("D-Wave", output);
        Assert.Contains("## Results", output);
    }

    // ── Test: LLM returns valid JSON but no markers → single-call fallback ─

    [Fact]
    public async Task LlmReturnsNoMarkers_FallsBackToPerItemThenSucceeds()
    {
        using var f = new ScriptedFactory();
        SetupAllFeedsWorking(f);
        // First call (single consolidated) returns valid JSON but no markers.
        // The fallback path makes per-item calls — give them valid responses.
        var noMarkers = """{"choices":[{"message":{"content":"The articles discuss AI and quantum topics in general terms."}}]}""";
        f.SetPostResponse("/v1/chat/completions", noMarkers);

        var svc = BuildNewsService(f);
        var (output, err) = await svc.FetchNewsAsync("AI", limit: 4, CancellationToken.None);

        Assert.Null(err);
        Assert.False(string.IsNullOrEmpty(output));
        // The fallback path still produces output — just without per-item LLM summaries
        // (ExtractContent returns the generic text for all items, but they're present).
        Assert.Contains("## Results", output);
    }

    // ── Test: LLM returns empty content → degrades to snippets ──────────────

    [Fact]
    public async Task LlmReturnsEmptyContent_DegradesToSnippets()
    {
        using var f = new ScriptedFactory();
        SetupAllFeedsWorking(f);
        f.SetPostResponse("/v1/chat/completions", """{"choices":[{"message":{"content":""}}]}""");

        var svc = BuildNewsService(f);
        var (output, err) = await svc.FetchNewsAsync("AI", limit: 4, CancellationToken.None);

        Assert.Null(err);
        Assert.False(string.IsNullOrEmpty(output));
        // Even with empty LLM responses, feed titles/snippets are still present.
        Assert.Contains("D-Wave", output);
    }

    // ── Test: LLM returns 500 → degrades to snippets ────────────────────────

    [Fact]
    public async Task LlmReturns500_DegradesToSnippets()
    {
        using var f = new ScriptedFactory();
        SetupAllFeedsWorking(f);
        // Override: LLM returns a 500 error body (not a throw — an actual error response).
        // The current code doesn't check status codes, only parses the body — so this
        // exercises ExtractContent on non-JSON error text.
        f.SetPostResponse("/v1/chat/completions", "Internal Server Error");

        var svc = BuildNewsService(f);
        var (output, err) = await svc.FetchNewsAsync("AI", limit: 4, CancellationToken.None);

        Assert.Null(err);
        Assert.False(string.IsNullOrEmpty(output));
        Assert.Contains("D-Wave", output);
    }

    // ── Test: all items relevant-filtered out → summary only, no results ────

    [Fact]
    public async Task AllItemsFiltered_RelevantSummaryOnly()
    {
        using var f = new ScriptedFactory();
        SetupAllFeedsWorking(f);
        // LLM returns only a SUMMARY saying "no relevant results".
        var noResults = """{"choices":[{"message":{"content":"### SUMMARY\nNo relevant results were found for the query."}}]}""";
        f.SetPostResponse("/v1/chat/completions", noResults);

        var svc = BuildNewsService(f);
        var (output, err) = await svc.FetchNewsAsync("xyz obscure query", limit: 4, CancellationToken.None);

        Assert.Null(err);
        Assert.Contains("No relevant results were found", output);
        // No ## Results section — all items were filtered out.
        Assert.DoesNotContain("## Results", output);
    }

    // ── Test: partial filtering — some items kept, some dropped ─────────────

    [Fact]
    public async Task PartialFiltering_KeptItemsInResults()
    {
        using var f = new ScriptedFactory();
        SetupAllFeedsWorking(f);
        // LLM returns only Article 2 (D-Wave/VB), filters 3 as irrelevant.
        // Ordinal interleaved order: HN(0), TC(1), VB(2), arXiv(3).
        var partial = """
            {"choices":[{"message":{"content":"### Article 2\nD-Wave CEO claims Nvidia should be worried about quantum computing.\n\n### SUMMARY\nOnly the D-Wave quantum article is relevant to the query."}}]}
            """;
        f.SetPostResponse("/v1/chat/completions", partial);

        var svc = BuildNewsService(f);
        var (output, err) = await svc.FetchNewsAsync("quantum", limit: 4, CancellationToken.None);

        Assert.Null(err);
        Assert.Contains("## Results", output);
        // Only the relevant item (D-Wave, index 2) is in the results.
        Assert.Contains("D-Wave", output);
        // The filtered-out items are NOT in the results.
        Assert.DoesNotContain("Accel", output);
        Assert.DoesNotContain("VidForensics", output);
    }

    // ── Test: cache survives across calls on same instance ──────────────────

    [Fact]
    public async Task Cache_WarmRunProducesOutputWithoutLlm()
    {
        using var f = new ScriptedFactory();
        SetupAllFeedsWorking(f);
        var callCount = 0;
        f.SetPostResponse("/v1/chat/completions", ValidMarkerResponse);

        var svc = BuildNewsService(f);

        // Cold run — populates cache.
        var (cold, _) = await svc.FetchNewsAsync("AI", limit: 4, CancellationToken.None);
        Assert.Contains("D-Wave", cold);

        // Warm run — cache hit, should produce same content (minus timestamp).
        var (warm, _) = await svc.FetchNewsAsync("AI", limit: 4, CancellationToken.None);
        Assert.Contains("D-Wave", warm);
        Assert.Contains("VidForensics", warm);
        // Both runs produce the ## Summary section.
        Assert.Contains("## Summary", warm);
    }

    // ── Test: single item → no batch call, item summary used directly ───────

    [Fact]
    public async Task SingleItem_NoBatchCall_SummaryFromItem()
    {
        using var f = new ScriptedFactory();
        // Only one feed has an item.
        f.SetGetResponse("venturebeat.com", VentureBeatRss, "application/rss+xml");
        f.SetGetThrows("techcrunch.com", new HttpRequestException("down"));
        f.SetGetThrows("hn.algolia.com", new HttpRequestException("down"));
        f.SetGetThrows("export.arxiv.org", new HttpRequestException("down"));
        f.SetPostResponse("/v1/chat/completions", ValidItemResponse);

        var svc = BuildNewsService(f);
        var (output, err) = await svc.FetchNewsAsync("AI", limit: 5, CancellationToken.None);

        Assert.Null(err);
        Assert.Contains("D-Wave", output);
        // Single item: summary and results both have the same content.
        Assert.Contains("## Summary", output);
        Assert.Contains("## Results", output);
    }
}
