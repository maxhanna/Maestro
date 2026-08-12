using System.Net;
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
/// items with REAL URLs so a follow-up _web_fetch/_create_file step writes actual content.
///
/// TOPIC- and PLACE-AWARE: the prompt is turned into a news plan (query + topics + places +
/// region) by the injected LLM planner when wired, else deterministically. The plan picks
/// the feeds — query-search (Google News/Bing) always on for any locality/niche, topic-keyed
/// feeds per topic, region feeds per region, general feeds when no topic — and localizes the
/// Google News locale.
///
/// CONFIG-DRIVEN SOURCES: `newsFeedUrls` (custom RSS/Atom/JSON feed URLs) are appended to the
/// built-ins, or replace them entirely when `newsReplaceBuiltinSources` is true. Every source
/// is a NewsSource with a FeedType (Rss/Atom/JsonApi); FetchSourceAsync dispatches by type.
/// A custom source declared as RSS that yields 0 items is retried as Atom (auto-detection),
/// and XML that is really JSON falls through to the generic JsonApi parser.
///
/// QUERY-FOCUSED SUMMARIZATION (no relevance judgment): the old design asked the model to
/// summarize items one at a time, expecting it to omit irrelevant ones — small local models
/// don't do that meta-judgment reliably (they shorten instead of omitting). Instead ONE batch
/// call summarizes all items and writes a query-focused overview (`## Summary` section), a
/// task any model can do. Per-item markers (ITEM n:) are mapped back onto the digest items;
/// any item the model omitted (truncation) is filled from its feed snippet — no data lost.
/// If the LLM is unreachable the digest falls back to feed snippets, with real article text
/// fetched for thin ones (no LLM needed).
///
/// OBSERVABILITY: all LLM calls route through CallLlmAsync, which parses usage tokens from the
/// response, accumulates per-run totals (exposed as LastLlmStats), and records every attempt in
/// EndpointHealthService so _news failures surface on the endpoint health badge.
///
/// X/Twitter and Facebook are deliberately absent (OAuth-gated). Reddit blocks keyless server
/// requests (403), so Lobsters + Hacker News cover the social/discussion surface.
/// </summary>
public sealed class NewsService
{
    public const int DefaultLimit = 8;

    public sealed record NewsItem(string Title, string Url, string? Published, string Feed, string Snippet)
    {
        public string Summary { get; set; } = "";
    }

    /// <summary>The extracted news intent: what to search, which topics, where, and the
    /// region code (canada/usa/uk/france/...) used for region feeds + Google News locale.</summary>
    public sealed record NewsPlan(string SearchQuery, string[] Topics, string[] Places, string? Region);

    /// <summary>Per-run LLM spend accumulated by CallLlmAsync (reset at every FetchNewsAsync).</summary>
    public sealed record LlmCallStats(int Calls, int PromptTokens, int CompletionTokens)
    {
        public int TotalTokens => PromptTokens + CompletionTokens;
        public override string ToString()
            => $"{Calls} LLM call(s), {PromptTokens} prompt + {CompletionTokens} completion = {TotalTokens} tokens";
    }

    /// <summary>Per-run LLM spend from the last FetchNewsAsync; null when no LLM call happened.</summary>
    public LlmCallStats? LastLlmStats { get; private set; }

    internal enum FeedType { Rss, Atom, JsonApi }

    /// <summary>A news source descriptor: Rss/Atom XML or a JsonApi (hn/lobsters/arxiv shapes or
    /// a generic array of {title,url,description}). Topics empty = always on; "general" = the
    /// fallback set when no topic is detected. QuerySearch sources are provider-filtered by
    /// their {q} (Google News, Bing), so no client-side relevance filter runs on them.</summary>
    internal sealed record NewsSource(string Name, string UrlTemplate, FeedType Type, string[] Topics,
        bool QuerySearch = false, string JsonKind = "");

    // ── Feed registry (built-ins; all curl-verified live: 200 + real content) ──
    private static readonly NewsSource[] BuiltinSources =
    {
        new("Google News", "https://news.google.com/rss/search?q={q}&hl={hl}&gl={gl}&ceid={ceid}", FeedType.Rss, Array.Empty<string>(), QuerySearch: true),
        new("Bing News", "https://www.bing.com/news/search?q={q}&format=rss", FeedType.Rss, Array.Empty<string>(), QuerySearch: true),

        new("BBC", "https://feeds.bbci.co.uk/news/world/rss.xml", FeedType.Rss, new[] { "general", "world", "politics", "business" }),
        new("The Guardian", "https://www.theguardian.com/world/rss", FeedType.Rss, new[] { "general", "world", "politics", "climate", "environment", "culture" }),
        new("NPR", "https://feeds.npr.org/1001/rss.xml", FeedType.Rss, new[] { "general", "world", "politics", "health", "science", "business" }),

        new("VentureBeat", "https://venturebeat.com/feed/", FeedType.Rss, new[] { "tech", "ai", "startups" }),
        new("TechCrunch", "https://techcrunch.com/feed/", FeedType.Rss, new[] { "tech", "ai", "startups" }),
        new("The Verge", "https://www.theverge.com/rss/index.xml", FeedType.Rss, new[] { "tech", "ai", "gaming", "science" }),
        new("Ars Technica", "https://feeds.arstechnica.com/arstechnica/technology-lab", FeedType.Rss, new[] { "tech", "ai", "science" }),
        new("MIT Technology Review", "https://www.technologyreview.com/feed/", FeedType.Rss, new[] { "tech", "ai", "science" }),
        new("Wired", "https://www.wired.com/feed/rss", FeedType.Rss, new[] { "tech", "ai", "science", "culture" }),
        new("Hacker News", "https://hn.algolia.com/api/v1/search?query={q}&tags=story&hitsPerPage=15", FeedType.JsonApi, new[] { "tech", "ai", "startups", "business" }, JsonKind: "hn"),
        new("Lobsters", "https://lobste.rs/newest.json", FeedType.JsonApi, new[] { "tech", "ai", "startups" }, JsonKind: "lobsters"),

        new("CNBC", "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=100003114", FeedType.Rss, new[] { "business", "finance", "economy", "markets" }),
        new("MarketWatch", "https://feeds.content.dowjones.io/public/rss/mw_topstories", FeedType.Rss, new[] { "business", "finance", "economy", "markets" }),

        new("Nature", "https://www.nature.com/nature.rss", FeedType.Rss, new[] { "science", "research", "climate", "health" }),
        new("ScienceDaily", "https://www.sciencedaily.com/rss/all.xml", FeedType.Rss, new[] { "science", "research", "health" }),
        new("arXiv", "https://export.arxiv.org/api/query?search_query=all:{q}&sortBy=submittedDate&sortOrder=descending&max_results=10", FeedType.JsonApi, new[] { "science", "research", "tech", "ai" }, JsonKind: "arxiv"),

        new("ESPN", "https://www.espn.com/espn/rss/news", FeedType.Rss, new[] { "sports" }),
        new("BBC Sport", "https://feeds.bbci.co.uk/sport/rss.xml", FeedType.Rss, new[] { "sports" }),

        new("Eater", "https://www.eater.com/rss/index.xml", FeedType.Rss, new[] { "food" }),

        new("Variety", "https://variety.com/feed/", FeedType.Rss, new[] { "entertainment", "movies", "tv", "culture" }),
        new("Billboard", "https://www.billboard.com/feed/", FeedType.Rss, new[] { "entertainment", "music" }),

        new("Polygon", "https://www.polygon.com/feed/", FeedType.Rss, new[] { "gaming" })
    };

    // Region feeds selected when the plan detects the region. Verified live.
    private static readonly Dictionary<string, NewsSource[]> RegionSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["canada"] = new[]
        {
            new NewsSource("CBC", "https://www.cbc.ca/cmlink/rss-canada", FeedType.Rss, new[] { "canada" }),
            new NewsSource("Global News", "https://globalnews.ca/feed/", FeedType.Rss, new[] { "canada" })
        }
    };

    // Google News locale per region (hl/gl/ceid). Defaults to en-US.
    private static readonly Dictionary<string, (string Hl, string Gl, string Ceid)> RegionLocales = new(StringComparer.OrdinalIgnoreCase)
    {
        ["canada"] = ("en-CA", "CA", "CA:en"),
        ["usa"] = ("en-US", "US", "US:en"),
        ["uk"] = ("en-GB", "GB", "GB:en"),
        ["france"] = ("fr-FR", "FR", "FR:fr"),
        ["germany"] = ("de-DE", "DE", "DE:de"),
        ["australia"] = ("en-AU", "AU", "AU:en"),
        ["india"] = ("en-IN", "IN", "IN:en"),
        ["japan"] = ("ja-JP", "JP", "JP:ja"),
        ["brazil"] = ("pt-BR", "BR", "BR:pt")
    };

    // Deterministic place → region fallback (the LLM planner is primary).
    private static readonly Dictionary<string, string> PlaceRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["montreal"] = "canada", ["toronto"] = "canada", ["vancouver"] = "canada", ["ottawa"] = "canada",
        ["calgary"] = "canada", ["edmonton"] = "canada", ["quebec"] = "canada", ["canada"] = "canada", ["canadian"] = "canada",
        ["new york"] = "usa", ["los angeles"] = "usa", ["chicago"] = "usa", ["san francisco"] = "usa",
        ["seattle"] = "usa", ["boston"] = "usa", ["austin"] = "usa", ["miami"] = "usa", ["houston"] = "usa",
        ["denver"] = "usa", ["phoenix"] = "usa", ["philadelphia"] = "usa", ["atlanta"] = "usa",
        ["washington"] = "usa", ["usa"] = "usa", ["america"] = "usa", ["united states"] = "usa",
        ["london"] = "uk", ["manchester"] = "uk", ["liverpool"] = "uk", ["birmingham"] = "uk",
        ["edinburgh"] = "uk", ["glasgow"] = "uk", ["uk"] = "uk", ["britain"] = "uk", ["england"] = "uk",
        ["scotland"] = "uk", ["wales"] = "uk", ["united kingdom"] = "uk",
        ["paris"] = "france", ["lyon"] = "france", ["marseille"] = "france", ["france"] = "france",
        ["berlin"] = "germany", ["munich"] = "germany", ["hamburg"] = "germany", ["frankfurt"] = "germany", ["germany"] = "germany",
        ["sydney"] = "australia", ["melbourne"] = "australia", ["brisbane"] = "australia", ["perth"] = "australia", ["australia"] = "australia",
        ["mumbai"] = "india", ["delhi"] = "india", ["bangalore"] = "india", ["bengaluru"] = "india", ["chennai"] = "india", ["india"] = "india",
        ["tokyo"] = "japan", ["osaka"] = "japan", ["kyoto"] = "japan", ["japan"] = "japan",
        ["sao paulo"] = "brazil", ["rio de janeiro"] = "brazil", ["brazil"] = "brazil"
    };

    // Deterministic topic detection (the LLM planner is primary).
    private static readonly (string Topic, string[] Keywords)[] TopicKeywords =
    {
        ("ai", new[] { "ai", "a.i.", "artificial intelligence", "machine learning", "deep learning", "llm", "chatgpt", "neural" }),
        ("tech", new[] { "tech", "technology", "software", "startup", "startups", "crypto", "blockchain", "security", "gadget", "gadgets", "app", "apps", "computer", "programming", "coding", "developer", "saas" }),
        ("science", new[] { "science", "research", "physics", "space", "nasa", "biology", "chemistry", "quantum", "astronomy", "genetics" }),
        ("business", new[] { "business", "finance", "economy", "market", "markets", "stock", "stocks", "earnings", "banking", "real estate", "companies" }),
        ("sports", new[] { "sports", "sport", "football", "soccer", "hockey", "basketball", "baseball", "tennis", "nfl", "nba", "nhl", "mlb", "olympics", "formula 1", "f1" }),
        ("food", new[] { "food", "cooking", "recipe", "recipes", "restaurant", "restaurants", "cuisine", "wine", "chef", "dining", "bakery" }),
        ("entertainment", new[] { "entertainment", "movie", "movies", "film", "films", "tv", "television", "music", "celebrity", "hollywood", "streaming", "netflix" }),
        ("gaming", new[] { "gaming", "video games", "esports", "nintendo", "playstation", "xbox", "steam", "twitch" }),
        ("health", new[] { "health", "medical", "medicine", "fitness", "wellness", "disease", "covid", "vaccine", "hospital", "healthcare" }),
        ("climate", new[] { "climate", "environment", "renewable", "carbon", "pollution", "sustainability", "energy" }),
        ("politics", new[] { "politics", "election", "elections", "government", "policy", "senate", "congress", "parliament", "geopolitics" }),
        ("world", new[] { "world", "global", "international", "foreign", "war", "ukraine", "israel", "china", "russia" }),
        ("local", new[] { "local", "regional", "nearby", "hometown", "neighborhood", "community", "city" })
    };

    private static readonly string[] StopWords =
    {
        "the", "a", "an", "and", "or", "for", "to", "of", "on", "in", "with", "from",
        "at", "by", "is", "are", "was", "were", "latest", "recent", "today's", "todays",
        "top", "best", "get", "find", "search", "web", "about", "article", "articles",
        "write", "save", "paste", "dump", "put", "create", "copy", "fetch", "insert",
        "text", "file", "files", "document", "desktop", "notes", "data", "my", "your",
        "me", "please", "into", "onto", "out", "it", "them", "this", "that", "these",
        "those", "any", "some", "then", "than", "so", "if", "but", "also", "just",
        "very", "will", "would", "can", "could", "should", "want", "wants", "looking",
        "look", "give", "show", "tell", "summarize", "summary", "add", "read", "open"
    };

    private const int MaxSnippetChars = 1500;
    private const int MaxArticleChars = 6000;
    private const int MaxDigestChars = 25000;
    private const int BatchSnippetChars = 400;
    private const int BatchMaxPromptChars = 12000;
    private const int BatchMaxTokens = 900;
    private const int PerItemMaxTokens = 300;
    private static readonly TimeSpan LlmTimeout = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _clientFactory;
    private readonly string _llamaBaseUrl;
    private readonly string _llamaModel;
    private readonly Func<string, CancellationToken, Task<string?>>? _planLlm;
    private readonly TimeSpan _timeout;
    private readonly List<NewsSource> _allSources;

    private int _llmCalls;
    private int _llmPromptTokens;
    private int _llmCompletionTokens;

    public NewsService(
        IHttpClientFactory clientFactory,
        string llamaBaseUrl,
        string llamaModel,
        Func<string, CancellationToken, Task<string?>>? planExtractor = null,
        TimeSpan? timeout = null,
        IEnumerable<string>? customFeedUrls = null,
        bool replaceBuiltins = false)
    {
        _clientFactory = clientFactory;
        _llamaBaseUrl = llamaBaseUrl.TrimEnd('/');
        _llamaModel = llamaModel;
        _planLlm = planExtractor;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);

        var custom = (customFeedUrls ?? Enumerable.Empty<string>())
            .Select(FromCustomUrl)
            .Where(s => !string.IsNullOrWhiteSpace(s.UrlTemplate))
            .ToList();
        _allSources = (replaceBuiltins ? custom : BuiltinSources.Concat(custom)).ToList();
    }

    /// <summary>Builds a NewsSource descriptor from a raw custom feed URL — the name comes from
    /// the host, the type is Rss with Rss→Atom→JSON auto-detection at fetch time.</summary>
    internal static NewsSource FromCustomUrl(string url)
    {
        var name = Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
            : "Custom Feed";
        return new NewsSource(name, url.Trim(), FeedType.Rss, Array.Empty<string>());
    }

    /// <summary>
    /// The "news marker": true for news-y phrasing, false for generic web searches. Three rules:
    /// (A) a strong news word outright (news/headline/breaking/trending/top stories/front page);
    /// (B) a recency word (latest/today's/fresh) + a topic + a news noun (stories/articles/
    /// headlines/updates/events) — the topic list spans AI/tech, business, sports, food,
    /// entertainment, gaming, health, climate, politics, local, … so "latest food news" routes;
    /// (C) a topic + a news noun + a persist intent (write/save/paste/… to a file/desktop/
    /// document) — catches the exact failure class the digest was built for: "Search the web for
    /// an interesting and relevant AI article and write the data into a text file on my desktop"
    /// has a topic and "article" but NO recency word, so rules A/B both miss it. The topic + noun
    /// requirement is what keeps "release notes", "weather", "pricing" and bare research prompts
    /// ("recent AI breakthroughs … verify each result") on the plain search path.
    /// </summary>
    public static bool LooksLikeNewsQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = " " + text.ToLowerInvariant() + " ";
        if (Regex.IsMatch(t, @"\b(news|headlines?|breaking|trending|top stories|front[- ]page)\b")) return true;
        if (Regex.IsMatch(t, @"\b(latest|today's|todays|fresh)\b") &&
            Regex.IsMatch(t, @"\b(" + MarkerTopics + @")\b") &&
            Regex.IsMatch(t, @"\b(stories?|articles?|headlines?|updates?|events?)\b"))
            return true;
        if (Regex.IsMatch(t, @"\b(" + MarkerTopics + @")\b") &&
            Regex.IsMatch(t, @"\b(stories?|articles?|headlines?|updates?|events?)\b") &&
            Regex.IsMatch(t, @"\b(write|save|paste|dump|put|create|copy|fetch|insert)\b.{0,60}\b(file|desktop|document|notes?\.md)\b"))
            return true;
        return false;
    }

    private const string MarkerTopics =
        @"ai|a\.i\.|artificial intelligence|machine learning|tech|technology|startups?|crypto|blockchain|" +
        @"security|software|gadgets?|science|research|papers?|business|finance|economy|markets?|stocks?|" +
        @"sports?|football|soccer|hockey|nba|nfl|food|cooking|recipes?|restaurants?|entertainment|movies?|" +
        @"films?|tv|television|music|gaming|video games|health|medical|medicine|climate|environment|" +
        @"politics|elections?|government|local|city|travel|fashion|education|real estate";

    /// <summary>
    /// Fetches and assembles the fresh-news digest. The full PROMPT is turned into a news plan
    /// (query + topics + places + region) by the LLM planner when wired, else deterministically;
    /// the plan selects the sources. Items are deduped, interleaved, then summarized in ONE
    /// query-focused batch call (snippets backfill anything the model omits). Never throws:
    /// per-source failures yield empty lists; the outer catch returns an error digest.
    /// </summary>
    public async Task<string> FetchNewsAsync(string prompt, string? query = null, int limit = DefaultLimit, CancellationToken ct = default)
    {
        ResetLlmStats();
        try
        {
            var plan = await ExtractPlanAsync(prompt, query, ct);
            var q = plan.SearchQuery;
            var sources = SelectSources(plan, _allSources).ToList();
            var tasks = sources.Select(source => FetchSourceAsync(source, q, plan.Region, ct)).ToList();

            var perSource = await Task.WhenAll(tasks).WaitAsync(ct);
            var all = perSource.SelectMany(x => x).ToList();
            if (all.Count == 0)
                return $"# {HeaderFor(plan)} — {DateTime.Now:yyyy-MM-dd} — \"{q}\"\n" +
                       $"No fresh items could be fetched from {sources.Count} news source(s) for \"{q}\" — retry with a different query or check the feeds.\n";

            var picked = Interleave(Deduplicate(all), limit);
            string? overview = null;
            var batch = await TrySummarizeAllAsync(picked, q, ct);
            if (batch != null)
            {
                overview = batch.Overview;
                for (var i = 0; i < picked.Count; i++)
                {
                    var item = picked[i];
                    item.Summary = batch.ByIndex.TryGetValue(i, out var s) && !string.IsNullOrWhiteSpace(s)
                        ? Cap(s, MaxSnippetChars)
                        : item.Snippet;
                }
            }
            else
            {
                // LLM unreachable — thin snippets still get real article text (no LLM needed),
                // long snippets stay as-is: nothing is lost.
                foreach (var item in picked.Where(i => i.Snippet.Length <= 200))
                    item.Summary = await SummarizeAsync(item, ct);
            }
            SnapshotLlmStats();
            return BuildDigest(plan, q, picked, overview);
        }
        catch (Exception ex)
        {
            SnapshotLlmStats();
            return $"# News — {DateTime.Now:yyyy-MM-dd} — \"{query ?? prompt}\"\nNews fetch failed: {ex.Message}\n";
        }
    }

    private void ResetLlmStats() { _llmCalls = _llmPromptTokens = _llmCompletionTokens = 0; LastLlmStats = null; }
    private void SnapshotLlmStats()
        => LastLlmStats = _llmCalls > 0 ? new LlmCallStats(_llmCalls, _llmPromptTokens, _llmCompletionTokens) : null;

    // ── News plan: prompt → (query, topics, places, region) ───────────────────────────────

    private async Task<NewsPlan> ExtractPlanAsync(string prompt, string? query, CancellationToken ct)
    {
        if (_planLlm != null)
        {
            try
            {
                var raw = await _planLlm(prompt, ct);
                var plan = ParsePlanJson(raw);
                if (plan != null) return plan;
            }
            catch { /* LLM hiccup or unparseable plan — fall through to the deterministic extractor */ }
        }
        return ExtractPlanDeterministic(prompt, query);
    }

    /// <summary>Parses the LLM planner's JSON ("query", "topics", "places", "region"). Tolerates
    /// markdown fences and trailing prose. Returns null on any malformation so the caller falls
    /// back to deterministic extraction.</summary>
    internal static NewsPlan? ParsePlanJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        using var doc = JsonDocument.Parse(raw.Substring(start, end - start + 1));
        var root = doc.RootElement;
        var query = GetString(root, "query");
        if (string.IsNullOrWhiteSpace(query)) return null;
        var topics = GetStringArray(root, "topics");
        var places = GetStringArray(root, "places");
        var region = GetString(root, "region");
        return new NewsPlan(
            CapToken(query.Trim()),
            topics.Select(CapToken).Where(t => t.Length > 0).ToArray(),
            places.Select(CapToken).Where(t => t.Length > 0).ToArray(),
            string.IsNullOrWhiteSpace(region) ? null : region.Trim().ToLowerInvariant());
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string[] GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "")
            .ToArray();
    }

    private static string CapToken(string token)
    {
        var t = token?.Trim() ?? "";
        return t.Length > 120 ? t[..120] : t;
    }

    internal static NewsPlan ExtractPlanDeterministic(string prompt, string? query)
    {
        var lower = " " + prompt.ToLowerInvariant() + " ";
        var topics = DetectTopics(lower);
        if (topics.Count == 0 && !string.IsNullOrWhiteSpace(query))
            topics = DetectTopics(" " + query.ToLowerInvariant() + " ");

        var places = PlaceRegions.Keys
            .Where(p => lower.Contains(p, StringComparison.Ordinal))
            .GroupBy(p => PlaceRegions[p])
            .Select(g => g.OrderBy(p => p.Length).First())
            .ToList();
        var region = places.Select(p => PlaceRegions[p])
            .GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        var q = query ?? CleanNewsQuery(prompt);
        if (places.Count > 0 && !places.Any(p => q.Contains(p, StringComparison.OrdinalIgnoreCase)))
            q = string.Join(" ", places.Take(2)) + " " + q;
        if (string.IsNullOrWhiteSpace(q)) q = "news";
        return new NewsPlan(q, topics.ToArray(), places.ToArray(), region);
    }

    private static List<string> DetectTopics(string lower)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var (topic, keywords) in TopicKeywords)
        {
            if (seen.Add(topic) && keywords.Any(k => lower.Contains(k, StringComparison.Ordinal)))
                result.Add(topic);
        }
        return result;
    }

    private static string CleanNewsQuery(string text)
    {
        // Preserve the original casing (the digest quotes the query verbatim) but split and
        // filter stopwords case-insensitively, deduping case-insensitively too.
        var tokens = Regex.Split(text, @"[^a-zA-Z0-9']+")
            .Where(t => t.Length >= 2 && !StopWords.Contains(t.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        return string.Join(" ", tokens);
    }

    /// <summary>Sources for the plan: always-on query search, topic-keyed sources (general as
    /// the no-topic fallback), plus region sources for the detected region.</summary>
    internal static IEnumerable<NewsSource> SelectSources(NewsPlan plan, IEnumerable<NewsSource> pool)
    {
        var topics = new HashSet<string>(plan.Topics, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in pool)
        {
            var wanted = source.Topics.Length == 0
                || source.Topics.Any(topics.Contains)
                || (topics.Count == 0 && source.Topics.Contains("general"));
            if (wanted && seen.Add(source.Name)) yield return source;
        }
        if (plan.Region != null && RegionSources.TryGetValue(plan.Region, out var regionSources))
        {
            foreach (var source in regionSources)
                if (seen.Add(source.Name)) yield return source;
        }
    }

    private static string HeaderFor(NewsPlan plan)
        => plan.Topics.Any(t => t.Equals("ai", StringComparison.OrdinalIgnoreCase) ||
                                t.Equals("tech", StringComparison.OrdinalIgnoreCase))
            ? "AI News"
            : "News";

    // ── Fetch pipeline: unified per-source dispatch ──────────────────────────────────────

    private Task<List<NewsItem>> FetchSourceAsync(NewsSource source, string query, string? region, CancellationToken ct)
    {
        var url = ResolveSourceUrl(source, query, region);
        return source.Type switch
        {
            FeedType.Rss => FetchXmlAsync(source.Name, url, query, source.QuerySearch, ct, forceAtom: false),
            FeedType.Atom => FetchXmlAsync(source.Name, url, query, source.QuerySearch, ct, forceAtom: true),
            FeedType.JsonApi => FetchJsonApiAsync(source.Name, source.JsonKind, url, query, ct),
            _ => Task.FromResult(new List<NewsItem>())
        };
    }

    private static string ResolveSourceUrl(NewsSource source, string query, string? region)
    {
        var url = source.UrlTemplate.Replace("{q}", Uri.EscapeDataString(query));
        if (url.Contains("{hl}", StringComparison.Ordinal))
        {
            var (hl, gl, ceid) = RegionLocales.TryGetValue(region ?? "", out var loc) ? loc : ("en-US", "US", "US:en");
            url = url.Replace("{hl}", hl).Replace("{gl}", gl).Replace("{ceid}", ceid);
        }
        return url;
    }

    /// <summary>Fetches XML (RSS 2.0 or Atom) for a source. Auto-detection: an Rss-typed source
    /// that yields no RSS items is retried as Atom, and XML-shaped content that is really JSON
    /// falls through to the generic JsonApi parser — a custom feed declared as RSS never dies
    /// because of a format mismatch.</summary>
    private async Task<List<NewsItem>> FetchXmlAsync(string feed, string url, string query, bool preFiltered,
        CancellationToken ct, bool forceAtom)
    {
        try
        {
            var xml = await GetStringAsync(url, ct);
            var items = forceAtom ? ParseAtomItems(xml, feed) : ParseXmlItemsAuto(xml, feed);
            if (items.Count == 0 && !forceAtom)
            {
                var trimmed = xml.TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                    items = ParseGenericJsonApi(xml, feed);
                else
                    items = ParseAtomItems(xml, feed);
            }
            // Query-search sources (Google News, Bing) are already filtered by the provider —
            // the client-side filter would only discard their results.
            return preFiltered ? items : FilterRelevant(items, query);
        }
        catch
        {
            return new List<NewsItem>();
        }
    }

    private async Task<List<NewsItem>> FetchJsonApiAsync(string feed, string jsonKind, string url, string query, CancellationToken ct)
    {
        try
        {
            var body = await GetStringAsync(url, ct);
            var items = jsonKind switch
            {
                "hn" => ParseHackerNews(body, feed),
                "lobsters" => ParseLobsters(body, feed),
                "arxiv" => ParseArxiv(body, feed),
                _ => ParseGenericJsonApi(body, feed)
            };
            // Lobsters' feed is static (no query search API) — apply the client-side relevance
            // filter so the newest-stories list narrows to the query instead of flooding the digest.
            return jsonKind == "lobsters" ? FilterRelevant(items, query) : items;
        }
        catch
        {
            return new List<NewsItem>();
        }
    }

    private static List<NewsItem> ParseXmlItemsAuto(string xml, string feed)
    {
        var rss = ParseRssItems(xml, feed);
        return rss.Count > 0 ? rss : ParseAtomItems(xml, feed);
    }

    private static List<NewsItem> ParseRssItems(string xml, string feed)
    {
        var doc = XDocument.Parse(xml);
        var nsContent = XNamespace.Get("http://purl.org/rss/1.0/modules/content/");
        var items = new List<NewsItem>();
        foreach (var el in doc.Descendants("item").Take(20))
        {
            var title = WebUtility.HtmlDecode(el.Element("title")?.Value?.Trim() ?? "");
            var link = el.Element("link")?.Value?.Trim() ?? "";
            var pub = el.Element("pubDate")?.Value?.Trim();
            var desc = Regex.Replace(el.Element("description")?.Value ?? "", "<[^>]+>", " ").Trim();
            var encoded = el.Element(nsContent + "encoded")?.Value;
            var rawSnippet = encoded != null && encoded.Length > desc.Length ? encoded : desc;
            var snippet = WebUtility.HtmlDecode(Regex.Replace(rawSnippet, "<[^>]+>", " ").Trim());
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(link)) continue;
            items.Add(new NewsItem(title, link, pub, feed, Cap(snippet, MaxSnippetChars)));
        }
        return items;
    }

    private static List<NewsItem> ParseAtomItems(string xml, string feed)
    {
        var doc = XDocument.Parse(xml);
        var ns = XNamespace.Get("http://www.w3.org/2005/Atom");
        var items = new List<NewsItem>();
        foreach (var entry in doc.Descendants(ns + "entry").Take(20))
        {
            var title = WebUtility.HtmlDecode(entry.Element(ns + "title")?.Value?.Trim() ?? "");
            var link = entry.Element(ns + "link")?.Attribute("href")?.Value?.Trim() ?? "";
            var pub = entry.Element(ns + "published")?.Value?.Trim() ?? entry.Element(ns + "updated")?.Value?.Trim();
            var summary = WebUtility.HtmlDecode(Regex.Replace(
                entry.Element(ns + "summary")?.Value ?? entry.Element(ns + "content")?.Value ?? "", "<[^>]+>", " ")).Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;
            items.Add(new NewsItem(title, link, pub, feed, Cap(summary, MaxSnippetChars)));
        }
        return items;
    }

    /// <summary>Generic JSON API contract for custom sources: an array (or {"items": [...]}) of
    /// objects with title / url|link|href / published|pubDate|date|created_at / description|
    /// snippet|summary|content. Everything optional except title + url.</summary>
    internal static List<NewsItem> ParseGenericJsonApi(string json, string feed)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var arr = root.ValueKind == JsonValueKind.Array
            ? root
            : (root.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Array ? i : default);
        if (arr.ValueKind != JsonValueKind.Array) return new();
        var list = new List<NewsItem>();
        foreach (var el in arr.EnumerateArray().Take(25))
        {
            var title = GetProp(el, "title");
            var url = GetProp(el, "url") ?? GetProp(el, "link") ?? GetProp(el, "href");
            var published = GetProp(el, "published") ?? GetProp(el, "pubDate") ?? GetProp(el, "date") ?? GetProp(el, "created_at");
            var snippet = GetProp(el, "description") ?? GetProp(el, "snippet") ?? GetProp(el, "summary") ?? GetProp(el, "content");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            list.Add(new NewsItem(title, url, published, feed, Cap(WebUtility.HtmlDecode(snippet ?? "").Trim(), MaxSnippetChars)));
        }
        return list;
    }

    private static string? GetProp(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
                ? WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(st.GetString() ?? "", "<[^>]+>", " "), @"\s+", " ").Trim())
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
                ? WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(d.GetString() ?? "", "<[^>]+>", " "), @"\s+", " ").Trim())
                : "";
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            list.Add(new NewsItem(title, url, created, feed, Cap(desc, MaxSnippetChars)));
        }
        return list;
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

    // ── Summarization: one query-focused batch call, snippets backfill the rest ──────────

    private const string BatchSystemPrompt =
        "You are a news summarizer building a research digest. You will receive a numbered list of " +
        "news items for a search query. Write:\n" +
        "1. A '## Summary' section: 2-4 sentences of QUERY-FOCUSED overview of the news landscape " +
        "for this query, naming the key stories and themes.\n" +
        "2. One short summary line per item, in this exact format: 'ITEM <number>: <1-2 sentence summary>'.\n" +
        "Rules: summarize EVERY item — never omit one. If an item's snippet is empty write " +
        "'ITEM <n>: No summary available.'. Output ONLY the format above with no preamble.";

    private static string BuildBatchPrompt(List<NewsItem> items, string query)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"News items for query \"{query}\":");
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            sb.AppendLine($"ITEM {i}");
            sb.AppendLine($"Title: {it.Title}");
            sb.AppendLine($"Source: {it.Url}");
            if (!string.IsNullOrWhiteSpace(it.Published)) sb.AppendLine($"Published: {it.Published}");
            sb.AppendLine($"Feed: {it.Feed}");
            sb.AppendLine($"Snippet: {Cap(it.Snippet, BatchSnippetChars)}");
            sb.AppendLine();
        }
        return Cap(sb.ToString(), BatchMaxPromptChars);
    }

    private sealed record BatchSummary(string? Overview, Dictionary<int, string> ByIndex);

    /// <summary>The batch summarizer — the single LLM call that replaces per-item relevance
    /// judgment. Returns null when the LLM is unreachable (caller falls back to snippets /
    /// article-text enrichment).</summary>
    private async Task<BatchSummary?> TrySummarizeAllAsync(List<NewsItem> items, string query, CancellationToken ct)
    {
        if (items.Count == 0) return null;
        return await SummarizeBatchAsync(items, query, ct);
    }

    private async Task<BatchSummary?> SummarizeBatchAsync(List<NewsItem> items, string query, CancellationToken ct)
    {
        var raw = await CallLlmAsync(BatchSystemPrompt, BuildBatchPrompt(items, query), ct, BatchMaxTokens);
        if (raw == null) return null;
        return ParseBatchSummary(raw, items.Count);
    }

    /// <summary>Parses the batch response: a '## Summary' overview plus 'ITEM n:' markers mapped
    /// back onto the item list. Missing markers are simply absent — the caller backfills from the
    /// feed snippet, so a truncated model response never loses an item.</summary>
    static BatchSummary? ParseBatchSummary(string raw, int itemCount)
    {
        var byIndex = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(raw,
            @"ITEM\s+(\d+)\s*:\s*(.*?)(?=ITEM\s+\d+\s*:|\z)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (int.TryParse(m.Groups[1].Value, out var idx) && idx >= 0 && idx < itemCount)
            {
                var text = Regex.Replace(m.Groups[2].Value, @"\s+", " ").Trim();
                if (text.Length > 0 && !text.Equals("no summary available", StringComparison.OrdinalIgnoreCase))
                    byIndex[idx] = text;
            }
        }
        var overview = ExtractOverview(raw);
        if (overview == null && byIndex.Count == 0) return null;
        return new BatchSummary(overview, byIndex);
    }

    private static string? ExtractOverview(string raw)
    {
        var m = Regex.Match(raw,
            @"(?is)^\s*(?:#{1,3}\s*|\*\*|__)?summary\s*(?::|\*\*|__)?\s*\n?([\s\S]*?)(?=ITEM\s+\d+\s*:|\z)");
        if (!m.Success) return null;
        var text = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>Per-item fallback used only when the batch call is unavailable: thin snippets
    /// fetch the real article (AngleSharp, ≤6000 chars) and are summarized; if the LLM is down
    /// the extracted article text itself is used, so the digest is enriched without any LLM.</summary>
    private async Task<string> SummarizeAsync(NewsItem item, CancellationToken ct)
    {
        var snippet = item.Snippet;
        if (snippet.Length > 200) return snippet; // already rich
        if (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            try
            {
                var html = await GetStringAsync(uri.ToString(), ct);
                var text = ExtractArticleText(html);
                if (text.Length >= 80)
                {
                    var summary = await CallLlmAsync(
                        "You are a news summarizer. Summarize the article below in at most 150 words, covering the key facts, numbers and names. Output ONLY the summary text.",
                        Cap(text, MaxArticleChars), ct, PerItemMaxTokens);
                    if (!string.IsNullOrWhiteSpace(summary)) return Cap(summary.Trim(), MaxSnippetChars);
                    return Cap(text, MaxSnippetChars); // no LLM → real article text beats the thin snippet
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

    // ── LLM call: single shared wrapper with token accounting + endpoint health ──────────

    /// <summary>
    /// The ONE LLM entry point for news summarization (shared by the batch and per-item paths).
    /// Posts a non-streaming chat completion, parses usage.prompt_tokens + usage.completion_tokens,
    /// accumulates per-run totals (exposed as LastLlmStats), and records every attempt in
    /// EndpointHealthService so news LLM failures surface on the endpoint health badge.
    /// Returns the content text or null on any failure — callers degrade gracefully.
    /// </summary>
    private async Task<string?> CallLlmAsync(string systemPrompt, string userMessage, CancellationToken ct, int maxTokens)
    {
        string? content = null;
        string? error = null;
        try
        {
            var client = _clientFactory.CreateClient("llama");
            client.Timeout = LlmTimeout;
            var req = new
            {
                model = _llamaModel,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage }
                },
                temperature = 0.0,
                max_tokens = maxTokens,
                stream = false
            };
            var httpContent = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(_llamaBaseUrl + "/v1/chat/completions", httpContent, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                error = $"HTTP {(int)resp.StatusCode}";
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                content = c.GetString();
            }
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number) _llmPromptTokens += p.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var comp) && comp.ValueKind == JsonValueKind.Number) _llmCompletionTokens += comp.GetInt32();
            }
            if (string.IsNullOrWhiteSpace(content)) return null;
            _llmCalls++;
            return content;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            EndpointHealthService.RecordCall(_llamaBaseUrl, content, error);
        }
    }

    private string BuildDigest(NewsPlan plan, string query, List<NewsItem> items, string? overview)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {HeaderFor(plan)} — {DateTime.Now:yyyy-MM-dd} — \"{query}\"");
        var sources = items.Select(i => i.Feed).Distinct().ToList();
        sb.AppendLine($"{items.Count} item(s) from {string.Join(", ", sources)}.");
        if (!string.IsNullOrWhiteSpace(overview))
        {
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine(overview);
        }
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
