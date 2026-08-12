using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// NewsService is the keyless RSS aggregator behind the agent's _news step. These
/// tests lock the pure parsing/dedup/interleave helpers (no network) so a feed-format
/// change or a dedup regression is caught without standing up a fake HTTP server. The
/// full FetchNewsAsync path (HTTP + LLM summarization) is exercised manually.
/// </summary>
public class NewsServiceTests
{
    // ── StripHtml ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("plain text", "plain text")]
    [InlineData("<p>hello <b>world</b></p>", "hello world")]
    [InlineData("a &amp; b &lt; c &gt; d &quot;e&quot; &#39;f&#39;", "a & b < c > d \"e\" 'f'")]
    public void StripHtml_RemovesTagsAndDecodesEntities(string? input, string expected)
    {
        Assert.Equal(expected, NewsService.StripHtml(input));
    }

    // ── NormalizeUrl (dedup key) ────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.com/article", "example.com/article")]
    [InlineData("https://example.com/article?utm_source=feed", "example.com/article")]
    [InlineData("https://example.com/article#section", "example.com/article")]
    [InlineData("https://example.com/article/", "example.com/article/")]
    [InlineData("https://example.com:443/article", "example.com/article")]
    [InlineData("not-a-url", "not-a-url")]
    public void NormalizeUrl_StripsQueryAndFragment(string url, string expected)
    {
        Assert.Equal(expected, NewsService.NormalizeUrl(url));
    }

    [Fact]
    public void NormalizeUrl_MakesTrackerVariantsCollide()
    {
        // The same article reached via different query trackers must dedupe to one key.
        var a = NewsService.NormalizeUrl("https://news.site.com/ai-breakthrough?utm_source=rss");
        var b = NewsService.NormalizeUrl("https://news.site.com/ai-breakthrough?ref=twitter");
        Assert.Equal(a, b);
    }

    // ── IsJsRenderedUrl ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://news.google.com/rss/articles/abc123", true)]
    [InlineData("https://venturebeat.com/2026/01/ai-story/", false)]
    [InlineData("https://techcrunch.com/2026/01/ai-story/", false)]
    [InlineData("https://hn.algolia.com/api/v1/search", false)]
    [InlineData("https://arxiv.org/abs/1706.03762", false)]
    public void IsJsRenderedUrl_DetectsGoogleNews(string url, bool expected)
    {
        Assert.Equal(expected, NewsService.IsJsRenderedUrl(url));
    }

    // ── Dedup-by-URL end-to-end (the rule 6c contract) ──────────────────────

    [Fact]
    public void Dedup_CollapsesSameUrlAcrossSources()
    {
        // Simulate what FetchNewsAsync does after collecting from multiple feeds:
        // GroupBy(NormalizeUrl) → First. Same URL from VentureBeat + HN must merge.
        var items = new List<NewsItem>
        {
            new("AI breakthrough", "https://news.site.com/x?utm_source=rss", DateTime.UtcNow, "VentureBeat AI", "snip"),
            new("AI breakthrough (dup)", "https://news.site.com/x?ref=hn", DateTime.UtcNow, "Hacker News", "snip2"),
            new("Other story", "https://other.com/y", DateTime.UtcNow, "arXiv", "snip3"),
        };
        var deduped = items
            .GroupBy(i => NewsService.NormalizeUrl(i.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        Assert.Equal(2, deduped.Count);
        Assert.Equal("AI breakthrough", deduped[0].Title);
    }

    // ── InterleaveBySource (round-robin source diversity) ──────────────────

    [Fact]
    public void InterleaveBySource_RoundRobinsAcrossSources()
    {
        // 3 sources, each with 2 items. Limit 4 → one from each source, then cycle
        // back to the alphabetically-first source. Groups are sorted alphabetically
        // (Hacker News < TechCrunch AI < VentureBeat AI) for deterministic output.
        var items = new List<NewsItem>
        {
            new("VB-1", "https://vb.com/1", DateTime.UtcNow.AddDays(-1), "VentureBeat AI", ""),
            new("VB-2", "https://vb.com/2", DateTime.UtcNow.AddDays(-2), "VentureBeat AI", ""),
            new("TC-1", "https://tc.com/1", DateTime.UtcNow.AddDays(-1), "TechCrunch AI", ""),
            new("TC-2", "https://tc.com/2", DateTime.UtcNow.AddDays(-2), "TechCrunch AI", ""),
            new("HN-1", "https://hn.com/1", DateTime.UtcNow.AddDays(-1), "Hacker News", ""),
            new("HN-2", "https://hn.com/2", DateTime.UtcNow.AddDays(-2), "Hacker News", ""),
        };
        var result = NewsService.InterleaveBySource(items, 4);
        Assert.Equal(4, result.Count);
        // Round 1: one from each source, alphabetically: HN, TC, VB.
        Assert.Equal("HN-1", result[0].Title);
        Assert.Equal("TC-1", result[1].Title);
        Assert.Equal("VB-1", result[2].Title);
        // Round 2: cycle back to alphabetically-first source (HN).
        Assert.Equal("HN-2", result[3].Title);
    }

    [Fact]
    public void InterleaveBySource_SkipsExhaustedSources()
    {
        // HN has 1 item, VB has 3. Limit 4 → 1 from HN, 3 from VB (HN exhausted after round 1).
        var items = new List<NewsItem>
        {
            new("VB-1", "https://vb.com/1", DateTime.UtcNow.AddDays(-1), "VentureBeat AI", ""),
            new("VB-2", "https://vb.com/2", DateTime.UtcNow.AddDays(-2), "VentureBeat AI", ""),
            new("VB-3", "https://vb.com/3", DateTime.UtcNow.AddDays(-3), "VentureBeat AI", ""),
            new("HN-1", "https://hn.com/1", DateTime.UtcNow, "Hacker News", ""),
        };
        var result = NewsService.InterleaveBySource(items, 4);
        Assert.Equal(4, result.Count);
        // One from HN (exhausted after round 1), three from VB (newest-first).
        var hn = result.Where(r => r.Source == "Hacker News").ToList();
        var vb = result.Where(r => r.Source == "VentureBeat AI").ToList();
        Assert.Single(hn);
        Assert.Equal(3, vb.Count);
        Assert.Equal("HN-1", hn[0].Title);
        // VB items must be in newest-first order.
        Assert.Equal("VB-1", vb[0].Title);
        Assert.Equal("VB-2", vb[1].Title);
        Assert.Equal("VB-3", vb[2].Title);
    }

    [Fact]
    public void InterleaveBySource_EmptyInput_ReturnsEmpty()
    {
        var result = NewsService.InterleaveBySource(new List<NewsItem>(), 5);
        Assert.Empty(result);
    }

    // ── RSS parsing contract ────────────────────────────────────────────────

    private const string SampleRss = """
        <?xml version="1.0"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title>AI News</title>
            <item>
              <title>Google unveils new model</title>
              <link>https://news.google.com/r/cls/abc123</link>
              <description>&lt;p&gt;A &lt;b&gt;new&lt;/b&gt; model launched today.&lt;/p&gt;</description>
              <pubDate>Mon, 10 Aug 2026 09:00:00 GMT</pubDate>
            </item>
            <item>
              <title>No-link item</title>
              <description>should be skipped</description>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void FetchNews_RssParsing_ExtractsItemsAndSkipsLinkless()
    {
        var method = typeof(NewsService).GetMethod("ParseRssItems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var items = new List<NewsItem>();
        method!.Invoke(null, new object[] { SampleRss, "Google News", items });

        var single = Assert.Single(items);
        Assert.Equal("Google unveils new model", single.Title);
        Assert.Equal("https://news.google.com/r/cls/abc123", single.Url);
        Assert.Equal("Google News", single.Source);
        Assert.Equal("A new model launched today.", single.Snippet);
        Assert.Equal(2026, single.PubDate.Year);
    }

    // ── RSS with content:encoded ─────────────────────────────────────────────

    private const string SampleRssWithContent = """
        <?xml version="1.0"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title>VentureBeat AI</title>
            <item>
              <title>AI startup raises $100M</title>
              <link>https://venturebeat.com/2026/08/ai-startup-raises-100m/</link>
              <description>Short excerpt.</description>
              <content:encoded><![CDATA[<p>The startup builds AI tools for enterprise. It raised $100M in Series B funding led by Sequoia.</p>]]></content:encoded>
              <pubDate>Tue, 11 Aug 2026 10:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void FetchNews_RssParsing_PrefersContentEncodedOverDescription()
    {
        var method = typeof(NewsService).GetMethod("ParseRssItems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var items = new List<NewsItem>();
        method!.Invoke(null, new object[] { SampleRssWithContent, "VentureBeat AI", items });

        var single = Assert.Single(items);
        Assert.Equal("AI startup raises $100M", single.Title);
        // content:encoded is longer than description, so it should be used.
        Assert.Contains("Series B funding", single.Snippet);
        Assert.DoesNotContain("Short excerpt", single.Snippet);
    }

    // ── Atom (arXiv) parsing contract ───────────────────────────────────────

    private const string SampleAtom = """
        <?xml version="1.0"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <title>Attention Is All You Need</title>
            <link href="https://arxiv.org/abs/1706.03762"/>
            <published>2017-06-12T00:00:00Z</published>
            <summary>We propose a new network architecture.</summary>
          </entry>
          <entry>
            <title>No-link entry</title>
            <published>2020-01-01T00:00:00Z</published>
          </entry>
        </feed>
        """;

    [Fact]
    public void FetchNews_AtomParsing_ExtractsLinkFromHref()
    {
        var method = typeof(NewsService).GetMethod("ParseAtomItems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var items = new List<NewsItem>();
        method!.Invoke(null, new object[] { SampleAtom, "arXiv", items });

        var single = Assert.Single(items);
        Assert.Equal("Attention Is All You Need", single.Title);
        Assert.Equal("https://arxiv.org/abs/1706.03762", single.Url);
        Assert.Equal("arXiv", single.Source);
        Assert.Contains("new network architecture", single.Snippet);
    }

    // ── ParseMarkerResponse (single-call consolidation parser) ─────────────

    [Fact]
    public void ParseMarkerResponse_ParsesSummaryAndItems()
    {
        var resp = """
            [SUMMARY]
            Overview of the latest AI news.

            [0]
            First article summary.

            [1]
            Second article summary.
            """;
        var (summary, items, found) = NewsService.ParseMarkerResponse(resp, 2);

        Assert.Equal("Overview of the latest AI news.", summary);
        Assert.Equal(2, items.Count);
        Assert.Equal("First article summary.", items[0]);
        Assert.Equal("Second article summary.", items[1]);
        Assert.Equal(2, found);
    }

    [Fact]
    public void ParseMarkerResponse_ParsesMarkdownHeadings()
    {
        var resp = """
            ### Article 0
            **Summary:** First article about AI.

            ### Article 1
            **Summary:** Second article about quantum computing.

            ### SUMMARY
            These stories highlight AI and quantum advances.
            """;
        var (summary, items, found) = NewsService.ParseMarkerResponse(resp, 2);

        Assert.Equal("These stories highlight AI and quantum advances.", summary);
        Assert.Equal(2, found);
        Assert.Equal("First article about AI.", items[0]);
        Assert.Equal("Second article about quantum computing.", items[1]);
    }

    [Fact]
    public void ParseMarkerResponse_MixedFormats_Parseable()
    {
        // Model mixes bracket and markdown — parser should handle both.
        var resp = """
            ### Article 0
            First article summary.

            [1]
            Second article summary.

            ### SUMMARY
            Overview text.
            """;
        var (summary, items, found) = NewsService.ParseMarkerResponse(resp, 2);

        Assert.Equal("Overview text.", summary);
        Assert.Equal(2, found);
        Assert.Equal("First article summary.", items[0]);
        Assert.Equal("Second article summary.", items[1]);
    }

    [Fact]
    public void ParseMarkerResponse_NoMarkers_ReturnsNullSummary()
    {
        var resp = "The model just wrote prose without any markers at all.";
        var (summary, items, found) = NewsService.ParseMarkerResponse(resp, 3);

        Assert.Null(summary);
        Assert.Equal(3, items.Count);
        Assert.All(items, s => Assert.Equal("", s));
        Assert.Equal(0, found);
    }

    [Fact]
    public void ParseMarkerResponse_MissingSummary_ReturnsNull()
    {
        var resp = """
            [0]
            First summary.

            [1]
            Second summary.
            """;
        var (summary, _, _) = NewsService.ParseMarkerResponse(resp, 2);
        Assert.Null(summary);
    }

    [Fact]
    public void ParseMarkerResponse_MissingItems_PaddedToExpectedCount()
    {
        var resp = """
            [SUMMARY]
            Overview.

            [0]
            Only this one.
            """;
        var (summary, items, found) = NewsService.ParseMarkerResponse(resp, 3);

        Assert.Equal("Overview.", summary);
        Assert.Equal(3, items.Count);
        Assert.Equal("Only this one.", items[0]);
        Assert.Equal("", items[1]);
        Assert.Equal("", items[2]);
        Assert.Equal(1, found);
    }

    [Fact]
    public void ParseMarkerResponse_ItemsOutOfOrder_MappedByIndex()
    {
        var resp = """
            [SUMMARY]
            Overview.

            [1]
            Second summary.

            [0]
            First summary.
            """;
        var (summary, items, found) = NewsService.ParseMarkerResponse(resp, 2);

        Assert.Equal("Overview.", summary);
        Assert.Equal("First summary.", items[0]);
        Assert.Equal("Second summary.", items[1]);
        Assert.Equal(2, found);
    }

    [Fact]
    public void ParseMarkerResponse_OutOfRangeIndex_Ignored()
    {
        var resp = """
            [SUMMARY]
            Overview.

            [0]
            First.

            [5]
            Out of range — should be ignored.

            [1]
            Second.
            """;
        var (summary, items, found) = NewsService.ParseMarkerResponse(resp, 2);

        Assert.Equal("Overview.", summary);
        Assert.Equal("First.", items[0]);
        Assert.Equal("Second.", items[1]);
        // [5] is out of range (expectedCount=2), so it doesn't count.
        Assert.Equal(2, found);
    }

    [Fact]
    public void ParseMarkerResponse_LessThanHalfStillAccepted()
    {
        // With relevance filtering, fewer items is expected behavior, not a parse
        // failure. The caller should accept this (not fall back) as long as SUMMARY
        // exists. 5 expected, only 2 found — the model filtered out 3 as irrelevant.
        var resp = """
            [SUMMARY]
            Overview of 2 relevant items.

            [0]
            First.

            [1]
            Second.
            """;
        var (summary, items, found) = NewsService.ParseMarkerResponse(resp, 5);
        Assert.NotNull(summary);
        Assert.Equal(2, found);
        // Missing items [2], [3], [4] are padded with empty strings.
        Assert.Equal("", items[2]);
    }

    [Fact]
    public void ParseMarkerResponse_AllItemsFiltered_AcceptedWithSummaryOnly()
    {
        // Model filtered ALL items as irrelevant — only wrote a SUMMARY saying
        // "no relevant results". This is valid relevance-filtering behavior, not a
        // format failure. The caller should accept it.
        var resp = """
            [SUMMARY]
            No relevant results were found for the query.

            [0]
            Skip.
            """;
        // Note: 0 item markers but SUMMARY exists.
        var respNoItems = """
            [SUMMARY]
            No relevant results were found for the query.
            """;
        var (summary, items, found) = NewsService.ParseMarkerResponse(respNoItems, 5);
        Assert.NotNull(summary);
        Assert.Equal(0, found);
        Assert.Equal(5, items.Count);
        Assert.All(items, s => Assert.Equal("", s));
    }

    [Fact]
    public void ParseMarkerResponse_EmptyResponse_ReturnsNull()
    {
        var (summary, items, found) = NewsService.ParseMarkerResponse("", 3);
        Assert.Null(summary);
        Assert.Equal(3, items.Count);
        Assert.Equal(0, found);
    }

    // ── HashBody (content fingerprint for cache invalidation) ──────────────

    [Fact]
    public void HashBody_SameContent_SameHash()
    {
        var a = NewsService.HashBody("Scientists discover new AI model for protein folding.");
        var b = NewsService.HashBody("Scientists discover new AI model for protein folding.");
        Assert.Equal(a, b);
    }

    [Fact]
    public void HashBody_DifferentContent_DifferentHash()
    {
        var a = NewsService.HashBody("Scientists discover new AI model for protein folding.");
        var b = NewsService.HashBody("Quantum computer breaks RSA encryption in record time.");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashBody_WhitespaceOnlyDifference_SameHash()
    {
        // The hash normalizes whitespace, so extra spaces/newlines don't cause cache misses.
        var a = NewsService.HashBody("Lead paragraph text here. More content follows.");
        var b = NewsService.HashBody("Lead  paragraph\ttext here.\n\nMore content follows.");
        Assert.Equal(a, b);
    }

    [Fact]
    public void HashBody_OnlyFirst200CharsMatter()
    {
        // Two bodies with the same first 200 chars but different tails produce the same hash.
        var head = new string('x', 200);
        var a = NewsService.HashBody(head + "tail A which is different");
        var b = NewsService.HashBody(head + "tail B which is different");
        Assert.Equal(a, b);
    }

    [Fact]
    public void HashBody_EmptyOrNull_ReturnsConstant()
    {
        Assert.Equal("empty", NewsService.HashBody(""));
        Assert.Equal("empty", NewsService.HashBody(null));
        Assert.Equal("empty", NewsService.HashBody("   "));
    }

    // ── MakeBatchKey (batch cache key) ──────────────────────────────────────

    [Fact]
    public void MakeBatchKey_SameUrlsDifferentOrder_SameKey()
    {
        var urls = new[] { "example.com/a", "example.com/b", "example.com/c" };
        var k1 = NewsService.MakeBatchKey("AI", urls);
        var k2 = NewsService.MakeBatchKey("AI", urls.Reverse());
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void MakeBatchKey_DifferentQuery_DifferentKey()
    {
        var urls = new[] { "example.com/a", "example.com/b" };
        var k1 = NewsService.MakeBatchKey("quantum", urls);
        var k2 = NewsService.MakeBatchKey("AI", urls);
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void MakeBatchKey_DifferentUrls_DifferentKey()
    {
        var k1 = NewsService.MakeBatchKey("AI", new[] { "example.com/a" });
        var k2 = NewsService.MakeBatchKey("AI", new[] { "example.com/b" });
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void MakeBatchKey_EmptyUrlList_ValidKey()
    {
        var key = NewsService.MakeBatchKey("AI", new List<string>());
        Assert.Contains("AI", key);
        Assert.EndsWith("::", key);
    }

    // ── ExtractOneLiner (output bullet formatting) ─────────────────────────

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Single line summary.", "Single line summary.")]
    [InlineData("First line.\nSecond line.", "First line.")]
    [InlineData("First line.\r\nSecond line.", "First line.")]
    public void ExtractOneLiner_ReturnsFirstLine(string? input, string expected)
    {
        Assert.Equal(expected, NewsService.ExtractOneLiner(input));
    }

    // ── ExtractContent (LLM JSON response parsing) ─────────────────────────

    [Fact]
    public void ExtractContent_ValidResponse_ReturnsContent()
    {
        var json = """{"choices":[{"message":{"content":"Summary text."}}]}""";
        Assert.Equal("Summary text.", NewsService.ExtractContent(json));
    }

    [Fact]
    public void ExtractContent_MissingChoices_ReturnsEmpty()
    {
        var json = """{"model":"qwen"}""";
        Assert.Equal("", NewsService.ExtractContent(json));
    }

    [Fact]
    public void ExtractContent_EmptyChoicesArray_ReturnsEmpty()
    {
        var json = """{"choices":[]}""";
        Assert.Equal("", NewsService.ExtractContent(json));
    }

    [Fact]
    public void ExtractContent_MissingMessage_ReturnsEmpty()
    {
        var json = """{"choices":[{"finish_reason":"stop"}]}""";
        Assert.Equal("", NewsService.ExtractContent(json));
    }

    [Fact]
    public void ExtractContent_MissingContent_ReturnsEmpty()
    {
        var json = """{"choices":[{"message":{"role":"assistant"}}]}""";
        Assert.Equal("", NewsService.ExtractContent(json));
    }

    [Fact]
    public void ExtractContent_MalformedJson_ReturnsEmpty()
    {
        Assert.Equal("", NewsService.ExtractContent("not json at all"));
        Assert.Equal("", NewsService.ExtractContent("{broken"));
    }

    [Fact]
    public void ExtractContent_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", NewsService.ExtractContent(""));
    }

    // ── TruncateFallback (degradation path) ─────────────────────────────────

    [Theory]
    [InlineData("", "")]
    [InlineData("short", "short")]
    public void TruncateFallback_ShortString_Unchanged(string input, string expected)
    {
        Assert.Equal(expected, NewsService.TruncateFallback(input));
    }

    [Fact]
    public void TruncateFallback_Exactly400Chars_Unchanged()
    {
        var s = new string('a', 400);
        Assert.Equal(s, NewsService.TruncateFallback(s));
    }

    [Fact]
    public void TruncateFallback_401Chars_TruncatedWithEllipsis()
    {
        var s = new string('a', 401);
        var result = NewsService.TruncateFallback(s);
        Assert.Equal(401, result.Length); // 400 chars + "…"
        Assert.EndsWith("…", result);
        Assert.Equal(400, result.Length - 1);
    }

    // ── CleanExtractedText (paragraph-aware truncation) ────────────────────

    [Fact]
    public void CleanExtractedText_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", NewsService.CleanExtractedText(""));
        Assert.Equal("", NewsService.CleanExtractedText("   "));
        Assert.Equal("", NewsService.CleanExtractedText(null!));
    }

    [Fact]
    public void CleanExtractedText_ShortText_ReturnedAsIs()
    {
        var text = "First paragraph.\nSecond paragraph.";
        var result = NewsService.CleanExtractedText(text);
        Assert.Equal("First paragraph.\nSecond paragraph.", result);
    }

    [Fact]
    public void CleanExtractedText_ExceedingCap_StopsAtLastCompleteLine()
    {
        // Build text where the first few lines fit but a later line would exceed the cap.
        var lineLen = 500;
        var line = new string('x', lineLen);
        // MaxArticleCharsForSummary is 3000. 5 lines × 500 = 2500 (fits), 6th would be 3001.
        var text = string.Join("\n", Enumerable.Repeat(line, 7));
        var result = NewsService.CleanExtractedText(text);
        // Should stop at 5 lines (2500 chars + 4 newlines = 2504), not 6 (would be 3005).
        Assert.True(result.Length <= 3000 + lineLen);
        Assert.Equal(5, result.Split('\n').Length);
    }

    [Fact]
    public void CleanExtractedText_SingleLineExceedingCap_HardTruncated()
    {
        // A single line longer than the cap should be hard-truncated with "…".
        var longLine = new string('y', 5000);
        var result = NewsService.CleanExtractedText(longLine);
        Assert.EndsWith("…", result);
        // 3000 chars + "…" = 3001.
        Assert.Equal(3001, result.Length);
    }

    [Fact]
    public void CleanExtractedText_CollapsesWhitespace()
    {
        var text = "  hello   world  \n\n  spaced   out  ";
        var result = NewsService.CleanExtractedText(text);
        Assert.Equal("hello world\nspaced out", result);
    }

    // ── InterleaveBySource edge cases ──────────────────────────────────────

    [Fact]
    public void InterleaveBySource_LimitZero_ReturnsEmpty()
    {
        var items = new List<NewsItem>
        {
            new("A", "https://a.com/1", DateTime.UtcNow, "Source A", ""),
        };
        var result = NewsService.InterleaveBySource(items, 0);
        Assert.Empty(result);
    }

    [Fact]
    public void InterleaveBySource_LimitExceedsTotal_ReturnsAll()
    {
        var items = new List<NewsItem>
        {
            new("A1", "https://a.com/1", DateTime.UtcNow, "Source A", ""),
            new("A2", "https://a.com/2", DateTime.UtcNow.AddDays(-1), "Source A", ""),
            new("B1", "https://b.com/1", DateTime.UtcNow, "Source B", ""),
        };
        var result = NewsService.InterleaveBySource(items, 10);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void InterleaveBySource_SingleSource_ReturnsDateDescending()
    {
        var items = new List<NewsItem>
        {
            new("Old", "https://x.com/1", DateTime.UtcNow.AddDays(-3), "Only Source", ""),
            new("New", "https://x.com/2", DateTime.UtcNow, "Only Source", ""),
            new("Mid", "https://x.com/3", DateTime.UtcNow.AddDays(-1), "Only Source", ""),
        };
        var result = NewsService.InterleaveBySource(items, 5);
        Assert.Equal(3, result.Count);
        Assert.Equal("New", result[0].Title);
        Assert.Equal("Mid", result[1].Title);
        Assert.Equal("Old", result[2].Title);
    }

    // ── ParseRssItems edge cases ───────────────────────────────────────────

    [Fact]
    public void ParseRssItems_InvalidPubDate_FallsBackToUtcNow()
    {
        var rss = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <title>Test</title>
                <item>
                  <title>Bad date item</title>
                  <link>https://example.com/bad-date</link>
                  <description>Snippet.</description>
                  <pubDate>Not a real date</pubDate>
                </item>
              </channel>
            </rss>
            """;
        var method = typeof(NewsService).GetMethod("ParseRssItems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var items = new List<NewsItem>();
        method!.Invoke(null, new object[] { rss, "Test", items });

        var single = Assert.Single(items);
        // Falls back to DateTime.UtcNow — should be within the last minute.
        Assert.True((DateTime.UtcNow - single.PubDate).TotalSeconds < 60);
    }

    [Fact]
    public void ParseRssItems_EmptyFeed_ReturnsNoItems()
    {
        var rss = """
            <?xml version="1.0"?>
            <rss version="2.0">
              <channel>
                <title>Empty Feed</title>
              </channel>
            </rss>
            """;
        var method = typeof(NewsService).GetMethod("ParseRssItems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var items = new List<NewsItem>();
        method!.Invoke(null, new object[] { rss, "Empty", items });
        Assert.Empty(items);
    }

    [Fact]
    public void ParseRssItems_MalformedXml_ReturnsNoItems()
    {
        var rss = "<?xml version=\"1.0\"?><rss><broken>";
        var method = typeof(NewsService).GetMethod("ParseRssItems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var items = new List<NewsItem>();
        method!.Invoke(null, new object[] { rss, "Broken", items });
        Assert.Empty(items);
    }

    // ── ParseAtomItems edge cases ───────────────────────────────────────────

    [Fact]
    public void ParseAtomItems_MultipleLinks_PicksFirst()
    {
        var atom = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <title>Paper with multiple links</title>
                <link href="https://arxiv.org/abs/1234.5678" rel="alternate"/>
                <link href="https://arxiv.org/pdf/1234.5678" rel="related"/>
                <published>2026-01-01T00:00:00Z</published>
                <summary>Abstract text.</summary>
              </entry>
            </feed>
            """;
        var method = typeof(NewsService).GetMethod("ParseAtomItems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var items = new List<NewsItem>();
        method!.Invoke(null, new object[] { atom, "arXiv", items });

        var single = Assert.Single(items);
        // First link element is the alternate (article page), not the PDF.
        Assert.Equal("https://arxiv.org/abs/1234.5678", single.Url);
    }

    // ── ParseMarkerResponse: summary after items ───────────────────────────

    [Fact]
    public void ParseMarkerResponse_SummaryAfterItems_ParsesCorrectly()
    {
        // Model puts items first, summary last — the parser should still work.
        var resp = """
            [0]
            First article summary.

            [1]
            Second article summary.

            [SUMMARY]
            Overview written last.
            """;
        var (summary, items, found) = NewsService.ParseMarkerResponse(resp, 2);

        Assert.Equal("Overview written last.", summary);
        Assert.Equal(2, found);
        Assert.Equal("First article summary.", items[0]);
        Assert.Equal("Second article summary.", items[1]);
    }

    // ── HashBody boundary ──────────────────────────────────────────────────

    [Fact]
    public void HashBody_Exactly200Chars_NotTruncated()
    {
        // Boundary: 200 chars is NOT truncated (> not >=), so the full 200 chars are hashed.
        // 200 'a's and 200 'a's + 'b' should produce the SAME hash (first 200 chars match).
        var exactly200 = new string('a', 200);
        var _201 = new string('a', 200) + "b";
        Assert.Equal(NewsService.HashBody(exactly200), NewsService.HashBody(_201));
    }

    [Fact]
    public void HashBody_199And200DifferentContent_DifferentHash()
    {
        var _199 = new string('a', 199);
        var _200 = new string('b', 200);
        Assert.NotEqual(NewsService.HashBody(_199), NewsService.HashBody(_200));
    }

    // ── NormalizeUrl edge cases ────────────────────────────────────────────

    [Fact]
    public void NormalizeUrl_WithPort_IncludesPortInHost()
    {
        var result = NewsService.NormalizeUrl("https://example.com:8080/path?query=1");
        Assert.Equal("example.com:8080/path", result);
    }
}
