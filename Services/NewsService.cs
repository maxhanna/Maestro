using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Html.Parser;

namespace Weaver.Services;

/// <summary>
/// Fetches FRESH news from live feeds instead of whatever the model was pretrained on.
/// A model asked for "the latest AI news" invents a headline it memorized and slaps a
/// fake URL on it (example.com/...). This service gives the agent real, dated, deduped
/// items with REAL URLs and article URLs so a follow-up _web_fetch/_create_file step
/// writes actual gathered content.
///
/// Pipeline per query (all fetch steps run in PARALLEL, the summarize step sequentially):
///   1. FETCH — RSS feeds (VentureBeat, TechCrunch, The Verge, Ars Technica, MIT Tech
///      Review, Wired) + query API sources (Hacker News Algolia, Reddit, arXiv Atom),
///      each with a 20s timeout, each degrading to an empty list on failure so one dead
///      feed never kills the digest.
///   2. DEDUP — collapse by normalized URL (query/fragment stripped), then by normalized
///      title, keeping the item with the richer snippet — the same story covered by
///      several trackers becomes one item.
///   3. INTERLEAVE — round-robin across feeds (VB→TC→HN→arXiv→VB→…) so a small limit is
///      never dominated by the busiest feed.
///   4. SUMMARIZE — snippet-first (a feed description &gt; 200 chars is the summary); thin
///      snippets trigger an article fetch (AngleSharp <article>/<main> extraction, capped
///      at 6000 chars) summarized by the injected LLM delegate (≤150 words); bodies under
///      80 chars are treated as JS-rendered/thin and fall back to the snippet. No LLM, no
///      article fetch, when no summarizer is injected (tests / llama unreachable).
///   5. ASSEMBLE — a markdown digest ("# AI News — &lt;date&gt; — \"&lt;query&gt;\"" with
///      N item(s), title, Source URL, Published, Feed, summary) returned into the agent's
///      discovery context, so the next step (e.g. _create_file ai_news.md, or a _command
///      write) pastes REAL content.
///
/// X/Twitter, Facebook and Reddit are deliberately absent: all three require OAuth
/// credentials and Reddit additionally blocks keyless server-side requests (search.json
/// returns 403 to datacenter IPs). Lobsters, Hacker News and arXiv cover the
/// social/discussion surface without keys.
/// </summary>
public sealed class NewsService
{
    public const int DefaultLimit = 8;

    public sealed record NewsItem(string Title, string Url, string? Published, string Feed, string Snippet)
    {
        public string Summary { get; set; } = "";
    }

    private static readonly (string Feed, string Url)[] RssSources =
    {
        ("VentureBeat", "https://venturebeat.com/feed/"),
        ("TechCrunch", "https://techcrunch.com/feed/"),
        ("The Verge", "https://www.theverge.com/rss/index.xml"),
        ("Ars Technica", "https://feeds.arstechnica.com/arstechnica/technology-lab"),
        ("MIT Technology Review", "https://www.technologyreview.com/feed/"),
        ("Wired", "https://www.wired.com/feed/rss")
    };

    private enum ApiKind { HackerNews, Lobsters, Arxiv }

    // HN/arXiv interpolate the query server-side ({q}), so their items are pre-filtered by
    // the provider. Lobsters has NO public search API (its search endpoint 400s), so it
    // serves its newest-stories feed and gets the same client-side relevance filter the
    // RSS feeds use (see FetchApiAsync).
    private static readonly (string Feed, ApiKind Kind, string Url)[] ApiSources =
    {
        ("Hacker News", ApiKind.HackerNews, "https://hn.algolia.com/api/v1/search?query={q}&tags=story&hitsPerPage=15"),
        ("Lobsters", ApiKind.Lobsters, "https://lobste.rs/newest.json"),
        ("arXiv", ApiKind.Arxiv, "https://export.arxiv.org/api/query?search_query=all:{q}&sortBy=submittedDate&sortOrder=descending&max_results=10")
    };

    private static readonly string[] StopWords =
    {
        "the", "a", "an", "and", "or", "for", "to", "of", "on", "in", "with", "from",
        "at", "by", "is", "are", "was", "were", "latest", "recent", "today's", "todays",
        "top", "best", "get", "find", "search", "web", "about", "article", "articles"
    };

    private const int MaxSnippetChars = 1500;
    private const int MaxArticleChars = 6000;
    private const int MaxDigestChars = 25000;

    private readonly IHttpClientFactory _clientFactory;
    private readonly Func<string, CancellationToken, Task<string?>>? _summarize;
    private readonly TimeSpan _timeout;

    public NewsService(
        IHttpClientFactory clientFactory,
        Func<string, CancellationToken, Task<string?>>? summarize = null,
        TimeSpan? timeout = null)
    {
        _clientFactory = clientFactory;
        _summarize = summarize;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>
    /// The "news marker": true for news-y phrasing, false for generic web searches. Three rules:
    /// (A) a strong news word outright (news/headline/breaking/trending/top stories/front page);
    /// (B) a recency word (latest/today's/fresh) + a tech topic (ai/tech/startup/…) + a news
    /// noun (stories/articles/headlines/updates);
    /// (C) a tech topic + a news noun + a persist intent (write/save/paste/dump … to a
    /// file/desktop/document) — catches the exact failure class the digest was built for:
    /// "Search the web for an interesting and relevant AI article and write the data into a
    /// text file on my desktop" has an AI topic and "article" but NO recency word, so rules
    /// A/B both miss it and the agent ends up inventing a headline + fake URL. The topic + noun
    /// requirement is what keeps "release notes", "weather", "pricing" and bare research
    /// prompts ("recent AI breakthroughs … verify each result") on the plain search path.
    /// </summary>
    public static bool LooksLikeNewsQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = " " + text.ToLowerInvariant() + " ";
        if (Regex.IsMatch(t, @"\b(news|headlines?|breaking|trending|top stories|front[- ]page)\b")) return true;
        if (Regex.IsMatch(t, @"\b(latest|today's|todays|fresh)\b") &&
            Regex.IsMatch(t, @"\b(ai|a\.i\.|artificial intelligence|tech|technology|startups?|crypto|blockchain|security|software|gadgets?|science|research|papers?)\b") &&
            Regex.IsMatch(t, @"\b(stories?|articles?|headlines?|updates?)\b"))
            return true;
        if (Regex.IsMatch(t, @"\b(ai|a\.i\.|artificial intelligence|tech|technology|startups?|crypto|blockchain|security|software|gadgets?|science|research|papers?)\b") &&
            Regex.IsMatch(t, @"\b(stories?|articles?|headlines?|updates?)\b") &&
            Regex.IsMatch(t, @"\b(write|save|paste|dump|put|create|copy|fetch)\b.{0,60}\b(file|desktop|document|notes?\.md)\b"))
            return true;
        return false;
    }

    /// <summary>
    /// Fetches and assembles the fresh-news digest. Never throws: per-source failures yield
    /// empty lists, and only a catastrophic whole-fetch failure falls into the outer catch,
    /// which returns an error digest instead of crashing the web step.
    /// </summary>
    public async Task<string> FetchNewsAsync(string query, int limit = DefaultLimit, CancellationToken ct = default)
    {
        try
        {
            var tasks = new List<Task<List<NewsItem>>>();
            foreach (var (feed, url) in RssSources)
                tasks.Add(FetchRssAsync(feed, url, query, ct));
            foreach (var (feed, kind, url) in ApiSources)
                tasks.Add(FetchApiAsync(feed, kind, url.Replace("{q}", Uri.EscapeDataString(query)), query, ct));

            var perSource = await Task.WhenAll(tasks).WaitAsync(ct);
            var all = perSource.SelectMany(x => x).ToList();
            if (all.Count == 0)
                return $"# AI News — {DateTime.Now:yyyy-MM-dd} — \"{query}\"\n" +
                       $"No fresh items could be fetched from {RssSources.Length + ApiSources.Length} news source(s) for \"{query}\" — retry with a different query or check the feeds.\n";

            var picked = Interleave(Deduplicate(all), limit);
            foreach (var item in picked)
            {
                item.Summary = await SummarizeAsync(item, ct);
            }
            return BuildDigest(query, picked);
        }
        catch (Exception ex)
        {
            return $"# AI News — {DateTime.Now:yyyy-MM-dd} — \"{query}\"\nNews fetch failed: {ex.Message}\n";
        }
    }

    private async Task<List<NewsItem>> FetchRssAsync(string feed, string url, string query, CancellationToken ct)
    {
        try
        {
            var xml = await GetStringAsync(url, ct);
            var doc = XDocument.Parse(xml);
            var nsContent = XNamespace.Get("http://purl.org/rss/1.0/modules/content/");
            var items = new List<NewsItem>();
            foreach (var el in doc.Descendants("item").Take(20))
            {
                var title = el.Element("title")?.Value?.Trim() ?? "";
                var link = el.Element("link")?.Value?.Trim() ?? "";
                var pub = el.Element("pubDate")?.Value?.Trim();
                var desc = Regex.Replace(el.Element("description")?.Value ?? "", "<[^>]+>", " ").Trim();
                var encoded = el.Element(nsContent + "encoded")?.Value;
                var rawSnippet = encoded != null && encoded.Length > desc.Length ? encoded : desc;
                var snippet = Regex.Replace(rawSnippet, "<[^>]+>", " ").Trim();
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(link)) continue;
                items.Add(new NewsItem(title, link, pub, feed, Cap(snippet, MaxSnippetChars)));
            }
            return FilterRelevant(items, query);
        }
        catch
        {
            return new List<NewsItem>();
        }
    }

    private async Task<List<NewsItem>> FetchApiAsync(string feed, ApiKind kind, string url, string query, CancellationToken ct)
    {
        try
        {
            var body = await GetStringAsync(url, ct);
            var items = kind switch
            {
                ApiKind.HackerNews => ParseHackerNews(body, feed),
                ApiKind.Lobsters => ParseLobsters(body, feed),
                ApiKind.Arxiv => ParseArxiv(body, feed),
                _ => new List<NewsItem>()
            };
            // Lobsters' feed is static (no query search API) — apply the same client-side
            // relevance filter the RSS feeds use so the newest-stories list narrows to the
            // query instead of flooding the digest with unrelated items.
            return kind == ApiKind.Lobsters ? FilterRelevant(items, query) : items;
        }
        catch
        {
            return new List<NewsItem>();
        }
    }

    private static List<NewsItem> ParseHackerNews(string json, string feed)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return new();
        var list = new List<NewsItem>();
        foreach (var hit in hits.EnumerateArray().Take(15))
        {
            var title = hit.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var url = hit.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(url))
            {
                var id = hit.TryGetProperty("objectID", out var oid) && oid.ValueKind == JsonValueKind.String ? oid.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(id)) url = "https://news.ycombinator.com/item?id=" + id;
            }
            var created = hit.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetString() : null;
            var story = hit.TryGetProperty("story_text", out var st) && st.ValueKind == JsonValueKind.String
                ? Regex.Replace(Regex.Replace(st.GetString() ?? "", "<[^>]+>", " "), @"\s+", " ").Trim()
                : "";
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            list.Add(new NewsItem(title, url, created, feed, Cap(story, MaxSnippetChars)));
        }
        return list;
    }

    private static List<NewsItem> ParseLobsters(string json, string feed)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return new();
        var list = new List<NewsItem>();
        foreach (var item in root.EnumerateArray().Take(25))
        {
            var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var url = item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(url))
            {
                var sid = item.TryGetProperty("short_id_url", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
                url = sid;
            }
            var created = item.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetString() : null;
            var desc = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? Regex.Replace(Regex.Replace(d.GetString() ?? "", "<[^>]+>", " "), @"\s+", " ").Trim()
                : "";
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            list.Add(new NewsItem(title, url, created, feed, Cap(desc, MaxSnippetChars)));
        }
        return list;
    }

    /// <summary>Static feeds (RSS, Lobsters) — filter to items that mention a significant
    /// query token; a feed with zero matches still contributes its newest item so it isn't
    /// lost.</summary>
    private static List<NewsItem> FilterRelevant(List<NewsItem> items, string query)
    {
        var tokens = QueryTokens(query);
        var relevant = items
            .Where(i => tokens.Any(tok =>
                i.Title.Contains(tok, StringComparison.OrdinalIgnoreCase) ||
                i.Snippet.Contains(tok, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return relevant.Count > 0 ? relevant : items.Take(1).ToList();
    }

    private static List<NewsItem> ParseArxiv(string xml, string feed)
    {
        var doc = XDocument.Parse(xml);
        var ns = XNamespace.Get("http://www.w3.org/2005/Atom");
        var list = new List<NewsItem>();
        foreach (var entry in doc.Descendants(ns + "entry").Take(10))
        {
            var title = entry.Element(ns + "title")?.Value?.Trim() ?? "";
            var id = entry.Element(ns + "id")?.Value?.Trim() ?? "";
            var pub = entry.Element(ns + "published")?.Value?.Trim();
            var summary = Regex.Replace(entry.Element(ns + "summary")?.Value ?? "", @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id)) continue;
            list.Add(new NewsItem(title, id, pub, feed, Cap(summary, MaxSnippetChars)));
        }
        return list;
    }

    /// <summary>URL dedup first (query/fragment/www/trailing-slash stripped), then title
    /// dedup — the same story covered by several feeds becomes one item, keeping the copy
    /// with the richer snippet.</summary>
    private static List<NewsItem> Deduplicate(List<NewsItem> items)
    {
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        var seenTitles = new Dictionary<string, NewsItem>(StringComparer.Ordinal);
        var result = new List<NewsItem>();
        foreach (var item in items)
        {
            var urlKey = NormalizeUrl(item.Url);
            if (urlKey.Length > 0 && !seenUrls.Add(urlKey)) continue;
            var titleKey = NormalizeTitle(item.Title);
            if (titleKey.Length > 0 && seenTitles.TryGetValue(titleKey, out var existing))
            {
                if (item.Snippet.Length > existing.Snippet.Length)
                {
                    var idx = result.IndexOf(existing);
                    if (idx >= 0) result[idx] = item;
                    seenTitles[titleKey] = item;
                }
                continue;
            }
            if (titleKey.Length > 0) seenTitles[titleKey] = item;
            result.Add(item);
        }
        return result;
    }

    /// <summary>Round-robin across feed groups so a small limit cycles VB→TC→HN→arXiv→…</summary>
    private static List<NewsItem> Interleave(List<NewsItem> items, int limit)
    {
        var byFeed = items.GroupBy(i => i.Feed).Select(g => g.ToList()).ToList();
        var result = new List<NewsItem>();
        for (var idx = 0; result.Count < limit; idx++)
        {
            var added = false;
            foreach (var list in byFeed)
            {
                if (idx < list.Count)
                {
                    result.Add(list[idx]);
                    added = true;
                    if (result.Count >= limit) break;
                }
            }
            if (!added) break;
        }
        return result.Take(limit).ToList();
    }

    /// <summary>Snippet-first; thin snippets fetch the real article (plain-HTML pages only)
    /// and hand it to the injected summarizer (≤150-word summary). No summarizer injected
    /// → thin snippet returned as-is, no article fetch, no LLM call.</summary>
    private async Task<string> SummarizeAsync(NewsItem item, CancellationToken ct)
    {
        var snippet = item.Snippet;
        if (snippet.Length > 200) return Cap(snippet, MaxSnippetChars);
        if (_summarize != null &&
            Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            try
            {
                var html = await GetStringAsync(uri.ToString(), ct);
                var text = ExtractArticleText(html);
                if (text.Length >= 80)
                {
                    var summary = await _summarize(Cap(text, MaxArticleChars), ct);
                    if (!string.IsNullOrWhiteSpace(summary)) return Cap(summary.Trim(), MaxSnippetChars);
                }
            }
            catch { /* feed/HTML hiccup — fall back to the snippet */ }
        }
        return snippet.Length > 0 ? Cap(snippet, 600) : "(no summary available)";
    }

    private static string ExtractArticleText(string html)
    {
        try
        {
            var doc = new HtmlParser().ParseDocument(html);
            var node = doc.QuerySelector("article") ?? doc.QuerySelector("main") ?? doc.Body;
            return Regex.Replace(node?.TextContent ?? "", @"\s+", " ").Trim();
        }
        catch
        {
            return "";
        }
    }

    private string BuildDigest(string query, List<NewsItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# AI News — {DateTime.Now:yyyy-MM-dd} — \"{query}\"");
        var sources = items.Select(i => i.Feed).Distinct().ToList();
        sb.AppendLine($"{items.Count} item(s) from {string.Join(", ", sources)}.");
        foreach (var item in items)
        {
            sb.AppendLine();
            sb.AppendLine($"## {item.Title}");
            sb.AppendLine($"Source: {item.Url}");
            if (!string.IsNullOrWhiteSpace(item.Published)) sb.AppendLine($"Published: {item.Published}");
            sb.AppendLine($"Feed: {item.Feed}");
            sb.AppendLine(item.Summary.Length > 0 ? item.Summary : item.Snippet);
            if (sb.Length > MaxDigestChars)
            {
                sb.AppendLine("\n… [digest truncated]");
                break;
            }
        }
        return sb.ToString();
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        var client = _clientFactory.CreateClient();
        client.Timeout = _timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Weaver-News/1.0 (+https://github.com/weaver)");
        var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string[] QueryTokens(string query)
    {
        // Min length 2 so short-but-meaningful tokens like "ai" survive the stopword filter;
        // without them the RSS relevance filter would drop every AI story from a "AI news" query.
        return Regex.Split(query.ToLowerInvariant(), @"[^a-z0-9']+")
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .Distinct()
            .ToArray();
    }

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim().ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host + uri.AbsolutePath.TrimEnd('/');
    }

    private static string NormalizeTitle(string title)
        => Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static string Cap(string text, int max)
        => text.Length <= max ? text : text[..max].TrimEnd() + "…";
}
