using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Weaver.Services;

/// <summary>
/// One fetched news item, before summarization. Snippet is the feed-provided
/// description/content excerpt (may be empty for sources that only ship a link).
/// </summary>
public record NewsItem(string Title, string Url, DateTime PubDate, string Source, string Snippet);

/// <summary>
/// Keyless, API-free news fetcher for the agent's <c>_news</c> step. Aggregates
/// AI-relevant headlines from public syndication feeds (curated AI-focused RSS,
/// Hacker News Algolia API, arXiv API), dedupes by URL, summarizes each item with
/// the same local llama.cpp/Ollama endpoint the rest of Weaver uses, and returns a
/// markdown blob the agent writes to a file via <c>_create_file</c>.
///
/// Design rules (locked with the user):
///   - No API keys, ever. Only public RSS/Atom feeds and keyless JSON APIs.
///   - Snippet-first: use the feed's own description / content:encoded when it is
///     substantial (&gt;200 chars); only fetch the article HTML page when the snippet
///     is thin or missing, to keep publisher-HTML contact to a minimum.
///   - Never store full article bodies — only the model-generated summary. The
///     article page text is fetched transiently for summarization and discarded.
///   - Source feed down → skip silently (the rest still run).
///   - Zero results across all sources → return a markdown "no results" note so
///     the agent's <c>_create_file</c> step still produces a non-empty file.
///   - Dedupe by normalized URL across all sources.
///   - Source diversity: round-robin interleave across sources so a small limit
///     doesn't get dominated by a single feed.
///   - Skip page fetches for known JS-rendered hosts (e.g. news.google.com) —
///     those pages have no server-rendered article text.
/// </summary>
public partial class NewsService
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly ConfigFileService _configFile;
    private readonly ILogger<NewsService> _logger;

    /// <summary>Feed snippets shorter than this trigger a full-page fetch for a real summary.</summary>
    private const int ThinSnippetThreshold = 200;

    /// <summary>Below this the body is too thin to summarize — skip the LLM call and use a truncated snippet.</summary>
    private const int MinSummaryBodyLength = 80;

    /// <summary>Cap the article text sent to the LLM so a giant page can't blow the context window.
    /// 3000 chars (~750 tokens) is enough for a 150-word summary — news articles follow the
    /// inverted pyramid, so key facts are in the first 2-3 paragraphs. Truncation is
    /// paragraph-aware (caps at the last complete line, not mid-sentence).</summary>
    private const int MaxArticleCharsForSummary = 3000;

    /// <summary>Max concurrent per-item (fetch + summarize) tasks. Bounded so we don't
    /// overwhelm a single-slot local LLM endpoint with dozens of simultaneous requests
    /// while still parallelizing the HTTP article fetches.</summary>
    private const int MaxConcurrentItems = 4;

    /// <summary>Hosts whose article pages are JS-rendered (no server-side text) — skip the page fetch.</summary>
    private static readonly HashSet<string> JsRenderedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "news.google.com"
    };

    // Compiled regexes — avoids recompiling the same pattern on every call (StripHtml
    // runs per item, CleanExtractedText runs per fetched page).
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagStripRegex { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex { get; }

    public NewsService(IHttpClientFactory clientFactory, ConfigFileService configFile,
        ILogger<NewsService> logger)
    {
        _clientFactory = clientFactory;
        _configFile = configFile;
        _logger = logger;
    }

    /// <summary>
    /// Fetches and summarizes AI news for a query. Returns a markdown string and
    /// null on success, or an empty string + error when every source failed.
    /// </summary>
    public async Task<(string output, string? error)> FetchNewsAsync(
        string query, int limit = 8, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q)) q = "AI";

        // Capture the request timestamp once at the start — not after all summarization
        // completes (which could be 30+ seconds later with 8 items + batch summary).
        var generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // Resolve the LLM endpoint and model ONCE, not per-item. When llamaModel is
        // empty (common for fresh installs), ResolveModelAsync queries /v1/models —
        // doing that per item was 8+ redundant HTTP round-trips.
        var cfg = await _configFile.LoadConfigAsync();
        var baseUrl = (cfg.llamaUrl ?? "http://localhost:8080").TrimEnd('/');
        var model = await ResolveModelAsync(cfg.llamaModel, baseUrl, ct);

        // Parallel feed fetches — sources are independent, so all run concurrently.
        // Each writes to a ConcurrentDictionary (thread-safe — the tasks run on
        // different thread-pool threads and a regular Dictionary can corrupt under
        // concurrent writes, even with different keys).
        var sourceLists = new ConcurrentDictionary<string, List<NewsItem>>();
        await Task.WhenAll(
            FetchSourceAsync("VentureBeat AI", TryVentureBeatRssAsync(ct), sourceLists),
            FetchSourceAsync("TechCrunch AI", TryTechCrunchRssAsync(ct), sourceLists),
            FetchSourceAsync("Hacker News", TryHackerNewsAsync(q, ct), sourceLists),
            FetchSourceAsync("arXiv", TryArxivAsync(q, ct), sourceLists));

        // Merge all source lists, sorted deterministically (by source name, then date
        // descending) so cross-source dedup is deterministic — g.First() always picks
        // the alphabetically-first source's newest item for a given URL, not whichever
        // fetch happened to complete first.
        var allItems = sourceLists.Values
            .SelectMany(l => l)
            .OrderBy(i => i.Source, StringComparer.Ordinal)
            .ThenByDescending(i => i.PubDate)
            .ToList();
        var deduped = allItems
            .Where(i => !string.IsNullOrWhiteSpace(i.Url))
            .GroupBy(i => NormalizeUrl(i.Url), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Round-robin interleave across sources so a small limit doesn't get
        // dominated by a single feed (source diversity). Source groups are sorted
        // alphabetically for deterministic output order across runs.
        var interleaved = InterleaveBySource(deduped, limit);

        if (interleaved.Count == 0)
        {
            // Rule 6b: empty file with a note, not a hard error.
            return ($"# Weaver web results\nTask: {q}\nGenerated: {generatedAt}\n\nNo results found for \"{q}\".\n", null);
        }

        // Per-item: fetch article body (snippet-first) + summarize. Run with bounded
        // concurrency — the article-page HTTP fetches are independent and parallelize
        // well, but the LLM calls will queue at a single-slot endpoint regardless.
        var semaphore = new SemaphoreSlim(MaxConcurrentItems, MaxConcurrentItems);
        var summaryTasks = interleaved.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var body = item.Snippet ?? "";
                if (body.Length < ThinSnippetThreshold && !IsJsRenderedUrl(item.Url))
                {
                    var fetched = await TryFetchArticleBodyAsync(item.Url, ct);
                    if (fetched.Length > body.Length) body = fetched;
                }
                var summary = await SummarizeAsync(item.Title, body, model, baseUrl, ct);
                return (item, summary);
            }
            finally { semaphore.Release(); }
        });
        var summaries = (await Task.WhenAll(summaryTasks)).ToList();

        // Overall batch summary — one LLM call over all individual summaries.
        var batchSummary = await SummarizeBatchAsync(q, summaries, model, baseUrl, ct);

        // Assemble in the same schema as WebSearchAsync output (## Summary / ## Results)
        // so the agent's web-results pipeline (### WEB RESULTS [query] ###) treats _news
        // output identically to _web_search output.
        var sb = new StringBuilder();
        sb.AppendLine("# Weaver web results");
        sb.AppendLine($"Task: {q}");
        sb.AppendLine($"Generated: {generatedAt}");
        sb.AppendLine();
        sb.AppendLine($"### WEB RESULTS [{q}] ###");
        sb.AppendLine("## Summary");
        sb.AppendLine(batchSummary);
        // Primary source: the first item's URL (the most recent / top result).
        sb.AppendLine($"Source: {interleaved[0].Url}");
        sb.AppendLine();
        sb.AppendLine("## Results");
        foreach (var (item, summary) in summaries)
        {
            // Bullet format matches WebSearchAsync: "  - <text> (<url>)"
            // Include the one-line summary so the digest carries real content, not just links.
            var oneLiner = ExtractOneLiner(summary);
            sb.AppendLine($"  - {item.Title}: {oneLiner} ({item.Url})");
        }

        return (sb.ToString(), null);
    }

    /// <summary>
    /// Extracts the first line of a summary for the bullet format. Falls back to
    /// the full (trimmed) summary when there's no newline. Safe on empty strings.
    /// </summary>
    private static string ExtractOneLiner(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "";
        var nl = summary.IndexOf('\n');
        return nl >= 0 ? summary[..nl].Trim() : summary.Trim();
    }

    private static async Task FetchSourceAsync(
        string name, Task<List<NewsItem>> fetchTask,
        ConcurrentDictionary<string, List<NewsItem>> dest)
    {
        try { dest[name] = await fetchTask; }
        catch { dest[name] = new List<NewsItem>(); /* source down — skip silently */ }
    }

    /// <summary>
    /// Round-robin interleaves items across sources. Each source's items are first
    /// sorted by date descending, then we cycle through sources picking one item at
    /// a time until the limit is reached or all sources are exhausted. Source groups
    /// are sorted alphabetically for deterministic output order across runs.
    /// </summary>
    internal static List<NewsItem> InterleaveBySource(List<NewsItem> items, int limit)
    {
        var bySource = items
            .GroupBy(i => i.Source)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(i => i.PubDate).ToList())
            .Where(g => g.Count > 0)
            .ToList();
        if (bySource.Count == 0) return new List<NewsItem>();

        var result = new List<NewsItem>();
        var indices = new int[bySource.Count];
        var exhausted = 0;
        while (result.Count < limit && exhausted < bySource.Count)
        {
            for (var s = 0; s < bySource.Count && result.Count < limit; s++)
            {
                if (indices[s] >= bySource[s].Count) continue;
                result.Add(bySource[s][indices[s]]);
                indices[s]++;
                if (indices[s] >= bySource[s].Count) exhausted++;
            }
        }
        return result;
    }

    // ── Sources ──────────────────────────────────────────────────────────────

    /// <summary>
    /// VentureBeat AI RSS: provides full-article descriptions (~16K chars), making
    /// it the richest source — no page fetch is usually needed. Keyless, no auth.
    /// </summary>
    private async Task<List<NewsItem>> TryVentureBeatRssAsync(CancellationToken ct)
    {
        var items = new List<NewsItem>();
        try
        {
            var url = "https://venturebeat.com/category/ai/feed/";
            var xml = await MakeFeedClient().GetStringAsync(url, ct);
            ParseRssItems(xml, "VentureBeat AI", items);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "VentureBeat AI feed failed — skipping"); }
        return items;
    }

    /// <summary>
    /// TechCrunch AI RSS: provides short descriptions (~100 chars) but real
    /// article URLs that fetch well (non-JS-rendered pages). Keyless, no auth.
    /// </summary>
    private async Task<List<NewsItem>> TryTechCrunchRssAsync(CancellationToken ct)
    {
        var items = new List<NewsItem>();
        try
        {
            var url = "https://techcrunch.com/category/artificial-intelligence/feed/";
            var xml = await MakeFeedClient().GetStringAsync(url, ct);
            ParseRssItems(xml, "TechCrunch AI", items);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "TechCrunch AI feed failed — skipping"); }
        return items;
    }

    /// <summary>
    /// Hacker News via Algolia's keyless search API. JSON with hits[] carrying
    /// title/url/created_at_i. Falls back to the HN item URL when the article URL
    /// is missing (Show HN / Ask HN posts link to themselves). Filters to AI-relevant
    /// stories by appending the query term.
    /// </summary>
    private async Task<List<NewsItem>> TryHackerNewsAsync(string query, CancellationToken ct)
    {
        var items = new List<NewsItem>();
        try
        {
            var url = "https://hn.algolia.com/api/v1/search?query=" + Uri.EscapeDataString(query)
                      + "&tags=story&hitsPerPage=10";
            var json = await MakeFeedClient().GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return items;
            foreach (var hit in hits.EnumerateArray())
            {
                var title = hit.TryGetProperty("title", out var t) ? t.GetString() : null;
                var link = hit.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(link) && hit.TryGetProperty("objectID", out var oid))
                    link = "https://news.ycombinator.com/item?id=" + oid.GetString();
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;
                var pub = hit.TryGetProperty("created_at_i", out var c) && c.TryGetInt64(out var ts)
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : DateTime.UtcNow;
                var pts = hit.TryGetProperty("points", out var p) && p.TryGetInt32(out var pp) ? pp : 0;
                items.Add(new NewsItem(title!, link!, pub, "Hacker News",
                    pts > 0 ? $"Hacker News story ({pts} points)." : "Hacker News story."));
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Hacker News API failed — skipping"); }
        return items;
    }

    /// <summary>
    /// arXiv API: keyless Atom feed of recent AI research papers. The query is
    /// used as a search term; when empty it falls back to the cs.AI category.
    /// Snippets come from the paper abstract, which is usually substantial. Uses
    /// HTTPS — arXiv supports it and plain HTTP leaks the query to network observers.
    /// </summary>
    private async Task<List<NewsItem>> TryArxivAsync(string query, CancellationToken ct)
    {
        var items = new List<NewsItem>();
        try
        {
            var term = string.IsNullOrWhiteSpace(query) ? "cat:cs.AI" : $"all:{Uri.EscapeDataString(query)}";
            var url = "https://export.arxiv.org/api/query?search_query=" + term
                      + "&sortBy=submittedDate&sortOrder=descending&max_results=8";
            var xml = await MakeFeedClient().GetStringAsync(url, ct);
            ParseAtomItems(xml, "arXiv", items);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "arXiv API failed — skipping"); }
        return items;
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    private static void ParseRssItems(string xml, string source, List<NewsItem> items)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            // RSS 2.0: /rss/channel/item
            var entries = doc.Descendants().Where(e => e.Name.LocalName == "item");
            foreach (var e in entries)
            {
                var title = Text(e, "title");
                var link = Text(e, "link");
                // Prefer content:encoded (full article) when available; fall back to
                // description (excerpt). content:encoded is a common RSS extension that
                // carries the complete article body — VentureBeat uses it at ~16K chars.
                var content = Text(e, "encoded") ?? "";
                var desc = Text(e, "description") ?? "";
                var snippet = content.Length > desc.Length ? content : desc;
                var pubStr = Text(e, "pubDate");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;
                var pub = DateTime.TryParse(pubStr, out var d) ? d : DateTime.UtcNow;
                items.Add(new NewsItem(title!, link!, pub, source, StripHtml(snippet)));
            }
        }
        catch { /* malformed RSS XML — skip, source-level catch logs HTTP failures */ }
    }

    private static void ParseAtomItems(string xml, string source, List<NewsItem> items)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var entries = doc.Descendants().Where(e => e.Name.LocalName == "entry");
            foreach (var e in entries)
            {
                var title = Text(e, "title");
                // arXiv Atom: <link href="..."/> (alternate rel) carries the abs page URL.
                var link = e.Elements().FirstOrDefault(x => x.Name.LocalName == "link")?.Attribute("href")?.Value;
                var summary = Text(e, "summary");
                var pubStr = Text(e, "published");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;
                var pub = DateTime.TryParse(pubStr, out var d) ? d : DateTime.UtcNow;
                items.Add(new NewsItem(title!, link!, pub, source, StripHtml(summary)));
            }
        }
        catch { /* malformed feed — skip */ }
    }

    private static string? Text(XElement parent, string localName)
        => parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

    internal static string StripHtml(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // Strip tags FIRST, while entities are still escaped so decoded &lt;/&gt;
        // aren't mistaken for tag delimiters. (XDocument has already XML-decoded
        // &lt; into real < tags, which we strip here; any surviving &amp;/&quot;
        // are decoded next.)
        var stripped = TagStripRegex.Replace(s, " ");
        stripped = stripped.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                           .Replace("&quot;", "\"").Replace("&#39;", "'");
        // Collapse whitespace runs left by tag removal.
        return WhitespaceRegex.Replace(stripped, " ").Trim();
    }

    internal static string NormalizeUrl(string url)
    {
        // Strip query/fragment so the same article reached via different trackers dedupes.
        try
        {
            var u = new Uri(url);
            return u.Host + u.AbsolutePath;
        }
        catch { return url.TrimEnd('/'); }
    }

    /// <summary>
    /// Returns true for URLs whose article pages are JS-rendered (Angular/React
    /// apps with no server-side article text). Fetching these is a waste — the
    /// page body has no extractable article text.
    /// </summary>
    internal static bool IsJsRenderedUrl(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return JsRenderedHosts.Contains(host);
        }
        catch { return false; }
    }

    // ── Article extraction (snippet-first fallback) ──────────────────────────

    /// <summary>
    /// Fetches an article page and extracts main-body text. Uses AngleSharp to
    /// select &lt;article&gt;/&lt;main&gt; when available; falls back to a tag
    /// strip if AngleSharp is unavailable or the page has no semantic container.
    /// Returns empty on any failure — the caller falls back to the feed snippet.
    /// </summary>
    private async Task<string> TryFetchArticleBodyAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = MakeFeedClient();
            var resp = await client.GetAsync(url, ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "text/plain";
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return "";
            return ExtractArticleText(body);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Article page fetch failed for {Url}", url); return ""; }
    }

    private static string ExtractArticleText(string html)
    {
        try
        {
            // AngleSharp (brought in transitively by AngleSharp.Css) parses HTML.
            var parser = new AngleSharp.Html.Parser.HtmlParser();
            var doc = parser.ParseDocument(html);
            // Prefer semantic main-body containers; fall back to body.
            var node = doc.QuerySelector("article") ?? doc.QuerySelector("main")
                        ?? doc.QuerySelector("[role=main]") ?? doc.Body;
            if (node == null) return "";
            var text = node.TextContent ?? "";
            return CleanExtractedText(text);
        }
        catch
        {
            // Last resort: crude tag strip (same approach as WebFetchAsync).
            var stripped = TagStripRegex.Replace(html, " ");
            return CleanExtractedText(stripped);
        }
    }

    private static string CleanExtractedText(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // Collapse whitespace runs and trim each line, drop near-empty lines.
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => WhitespaceRegex.Replace(l, " ").Trim())
                     .Where(l => l.Length > 0)
                     .ToList();
        // Paragraph-aware cap: accumulate complete lines until we'd exceed the limit,
        // then stop — never cut mid-sentence. This gives the model clean, complete
        // sentences to summarize even with a smaller token budget.
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > MaxArticleCharsForSummary) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        var joined = sb.ToString();
        // If even the first line exceeds the cap (rare — a very long paragraph), hard-truncate it.
        if (joined.Length == 0 && lines.Count > 0)
            joined = lines[0].Length > MaxArticleCharsForSummary
                ? lines[0][..MaxArticleCharsForSummary] + "…"
                : lines[0];
        return joined;
    }

    // ── Summarization (local llama endpoint) ─────────────────────────────────

    /// <summary>
    /// Summarizes one article via the configured local llama.cpp/Ollama endpoint
    /// (OpenAI-style /v1/chat/completions). Inherits the user's llamaUrl/llamaModel
    /// so no new config or key is needed. Returns a ≤150-word summary; on any
    /// failure returns the (truncated) snippet verbatim rather than failing the
    /// whole news fetch. When the body is too thin to summarize, skips the LLM
    /// call entirely and returns a truncated snippet to avoid wasting tokens.
    /// The model and baseUrl are resolved once by the caller (FetchNewsAsync) and
    /// passed in so we don't re-query /v1/models per item.
    /// </summary>
    private async Task<string> SummarizeAsync(
        string title, string body, string model, string baseUrl, CancellationToken ct)
    {
        // Deduplicate: many feeds (especially content:encoded) include the title at the
        // start of the body. Sending it twice wastes ~10-20 input tokens per item.
        var bodyText = body ?? "";
        if (bodyText.StartsWith(title, StringComparison.OrdinalIgnoreCase))
            bodyText = bodyText[title.Length..].TrimStart('\n', ' ', '\r');
        var text = string.IsNullOrWhiteSpace(bodyText) ? title : $"{title}\n\n{bodyText}";

        // Content quality guard: if the combined text is too thin, skip the LLM
        // call and return a truncated snippet. This avoids wasting a model call on
        // content that will just produce "too thin to summarize" — the E2E test
        // showed thin sources hitting this path repeatedly.
        if (text.Length < MinSummaryBodyLength)
            return TruncateFallback(text);

        try
        {
            var client = _clientFactory.CreateClient("llama");
            client.Timeout = TimeSpan.FromMinutes(3);
            var req = new
            {
                model,
                stream = false,
                temperature = 0.2,
                max_tokens = 200,
                messages = new object[]
                {
                    new { role = "system", content = "Summarize this article in ≤150 words. Key facts only, no opinion." },
                    new { role = "user", content = text }
                }
            };
            var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(baseUrl + "/v1/chat/completions", content, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            var summary = ExtractContent(respText);
            return string.IsNullOrWhiteSpace(summary)
                ? TruncateFallback(text)
                : summary.Trim();
        }
        catch (Exception ex)
        {
            // LLM endpoint down → degrade to the raw snippet so the file isn't blank.
            _logger.LogWarning(ex, "LLM summarization failed for \"{Title}\" — degrading to snippet", title);
            return TruncateFallback(text);
        }
    }

    /// <summary>
    /// Generates an overall batch summary from the individual article summaries.
    /// One LLM call over all per-item summaries, producing a ≤200-word digest that
    /// goes in the output's <c>## Summary</c> section (matching the WebSearchAsync
    /// output schema). On any failure, degrades to a concatenated fallback.
    ///
    /// Token optimizations:
    ///   - Skipped entirely when there's only 1 item (the per-item summary is sufficient).
    ///   - Uses only the first sentence of each per-item summary as input — the full
    ///     summaries are already in the ## Results section, so the batch just needs
    ///     the thesis statement of each story to synthesize an overview.
    ///   - Short system prompt and reduced max_tokens.
    /// </summary>
    private async Task<string> SummarizeBatchAsync(
        string query, List<(NewsItem item, string summary)> summaries,
        string model, string baseUrl, CancellationToken ct)
    {
        if (summaries.Count == 0) return $"No AI news found for \"{query}\".";
        // When there's only 1 item, the batch summary would just rephrase the single
        // per-item summary — skip the LLM call and use it directly.
        if (summaries.Count == 1) return summaries[0].summary;

        // Use only the first sentence of each per-item summary — the full summaries
        // are already in the ## Results section, so the batch just needs the thesis
        // statement of each story to synthesize an overview. This cuts batch input
        // tokens by ~60%.
        var combined = string.Join("\n\n",
            summaries.Select(s => $"- {s.item.Title}: {ExtractOneLiner(s.summary)}"));
        if (combined.Length < MinSummaryBodyLength)
            return TruncateFallback(combined);

        try
        {
            var client = _clientFactory.CreateClient("llama");
            client.Timeout = TimeSpan.FromMinutes(3);
            var req = new
            {
                model,
                stream = false,
                temperature = 0.2,
                max_tokens = 300,
                messages = new object[]
                {
                    new { role = "system", content = "Write a ≤200-word overview of these AI news items. Group related stories, state key facts. Single paragraph." },
                    new { role = "user", content = combined }
                }
            };
            var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(baseUrl + "/v1/chat/completions", content, ct);
            var respText = await resp.Content.ReadAsStringAsync(ct);
            var overview = ExtractContent(respText);
            return string.IsNullOrWhiteSpace(overview)
                ? TruncateFallback(combined)
                : overview.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM batch summary failed — degrading to concatenated snippets");
            return TruncateFallback(combined);
        }
    }

    /// <summary>
    /// Resolves the model name to send to the LLM endpoint. Uses the configured
    /// llamaModel when set; otherwise queries /v1/models for the first loaded model
    /// so a fresh install with no model configured still works. Falls back to
    /// "medgemma:4b" (the documented default) only when the query itself fails.
    /// Called once per FetchNewsAsync run, not per item.
    /// </summary>
    private async Task<string> ResolveModelAsync(string? configuredModel, string baseUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configuredModel)) return configuredModel!;
        try
        {
            var client = MakeFeedClient();
            var resp = await client.GetAsync(baseUrl + "/v1/models", ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var firstId = data[0].TryGetProperty("id", out var id) ? id.GetString() : null;
                if (!string.IsNullOrWhiteSpace(firstId)) return firstId!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve LLM model from {BaseUrl}/v1/models — falling back to medgemma:4b", baseUrl);
        }
        return "medgemma:4b";
    }

    private static string ExtractContent(string respJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(respJson);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                if (choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                    return c.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    private static string TruncateFallback(string s)
        => s.Length > 400 ? s[..400] + "…" : s;

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private HttpClient MakeFeedClient()
    {
        // A fresh client (not the "llama" one) — these are public feed fetches,
        // independent of the LLM endpoint's long timeout and base URL.
        var client = _clientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WeaverNews/1.0 (keyless RSS aggregator)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        return client;
    }
}
