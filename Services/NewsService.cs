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
    private readonly DatabaseService? _db;

    /// <summary>SQLite key under which the summary cache is persisted (weaver_config table).
    /// Allows the cache to survive restarts, preserving the 32x warm-run speedup.</summary>
    private const string CacheDbKey = "news_summary_cache";
    private static readonly TimeSpan CachePersistDebounce = TimeSpan.FromSeconds(30);
    private long _lastPersistTicks;

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

    /// <summary>Max items for the single consolidated LLM call. Beyond this, attention
    /// quality degrades on smaller models and we fall back to per-item calls.</summary>
    private const int MaxItemsForSingleCall = 6;

    /// <summary>Max combined article text for the single call. ~16K chars ≈ 4K tokens,
    /// leaving room for the prompt and output in an 8K-context model.</summary>
    private const int MaxCombinedCharsForSingleCall = 16000;

    /// <summary>Max output tokens for the single call: ~200 per item summary + 300 for
    /// the overview, with headroom for formatting/markers.</summary>
    private const int SingleCallMaxTokens = 1500;

    /// <summary>TTL for cached per-item summaries and batch summaries. News articles
    /// rarely change post-publication; 2h balances freshness vs. reuse value.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(2);

    /// <summary>Number of leading body chars to hash for the content fingerprint. The
    /// lead paragraph is stable; page chrome/ads/footer vary between fetches, so
    /// hashing the full body would detect false changes. 200 chars is enough to
    /// distinguish genuinely different articles at the same URL (an updated article
    /// almost always changes the lead).</summary>
    private const int ContentHashChars = 200;

    /// <summary>Hosts whose article pages are JS-rendered (no server-side text) — skip the page fetch.</summary>
    private static readonly HashSet<string> JsRenderedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "news.google.com"
    };

    // ── Summary cache (in-memory, process-lifetime) ─────────────────────────
    //
    // NewsService is a singleton (Program.cs), so these dictionaries persist across
    // requests. Per-item summaries are query-agnostic (factual article summaries),
    // so a summary cached for "quantum" is reused for "AI". Batch summaries are
    // query-specific (they synthesize an overview for a particular query), so they
    // are keyed on (query, url-set). Filtered-out items are NOT cached — they have
    // no summary and must be re-evaluated on the next run.

    /// <summary>Per-item summary cache: URL → (summary, contentHash, cachedAt, model).</summary>
    private readonly ConcurrentDictionary<string, SummaryCacheEntry> _summaryCache = new();

    /// <summary>Batch summary cache: (query, url-fingerprint) → (summary, cachedAt).</summary>
    private readonly ConcurrentDictionary<string, BatchCacheEntry> _batchCache = new();

    /// <summary>One cached per-item summary with its content fingerprint and metadata.</summary>
    private sealed record SummaryCacheEntry(string Summary, string ContentHash, DateTime CachedAt, string Model);

    /// <summary>One cached batch summary for a (query, url-set) pair.</summary>
    private sealed record BatchCacheEntry(string Summary, DateTime CachedAt);

    // ── Per-run token accounting ─────────────────────────────────────────────
    //
    // Accumulated across all LLM calls within a single FetchNewsAsync run. Logged at
    // the end of the run so an operator can see how many tokens _news consumed without
    // digging through structured logs. Not persisted — it's per-run observability.

    /// <summary>Running total of prompt + completion tokens for the current run.
    /// Thread-safe because the fallback path runs per-item calls in parallel.</summary>
    private long _runPromptTokens;
    private long _runCompletionTokens;
    private int _runLlmCalls;

    private void ResetTokenCounters()
    {
        Interlocked.Exchange(ref _runPromptTokens, 0);
        Interlocked.Exchange(ref _runCompletionTokens, 0);
        Interlocked.Exchange(ref _runLlmCalls, 0);
    }

    private void RecordTokenUsage(string? respJson)
    {
        if (string.IsNullOrWhiteSpace(respJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(respJson);
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt64(out var pt))
                    Interlocked.Add(ref _runPromptTokens, pt);
                if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt64(out var ct))
                    Interlocked.Add(ref _runCompletionTokens, ct);
            }
        }
        catch { }
    }

    private void LogTokenUsage(string query)
    {
        var total = Interlocked.Read(ref _runPromptTokens) + Interlocked.Read(ref _runCompletionTokens);
        if (total > 0 || _runLlmCalls > 0)
        {
            _logger.LogInformation(
                "News \"{Query}\": {Calls} LLM calls, {Prompt} prompt + {Completion} completion = {Total} tokens",
                query, _runLlmCalls,
                Interlocked.Read(ref _runPromptTokens),
                Interlocked.Read(ref _runCompletionTokens),
                total);
        }
    }

    /// <summary>
    /// Shared LLM call wrapper: POST to /v1/chat/completions, record endpoint health,
    /// extract token usage, and return the raw response JSON for content extraction.
    /// Centralizes health tracking + token accounting so all three call sites
    /// (SummarizeAsync, SummarizeBatchAsync, TrySummarizeAllAsync) are observable.
    /// </summary>
    private async Task<string> CallLlmAsync(
        string model, string baseUrl, object req, CancellationToken ct, int timeoutMinutes = 3)
    {
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = TimeSpan.FromMinutes(timeoutMinutes);
        var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
        var resp = await client.PostAsync(baseUrl + "/v1/chat/completions", content, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        Interlocked.Increment(ref _runLlmCalls);
        // Record endpoint health so _news LLM failures are visible in the UI badge.
        var error = resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}";
        EndpointHealthService.RecordCall(baseUrl, respText, error);
        RecordTokenUsage(respText);
        return respText;
    }

    // Compiled regexes — avoids recompiling the same pattern on every call (StripHtml
    // runs per item, CleanExtractedText runs per fetched page).
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagStripRegex { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex { get; }

    /// <summary>Matches both marker formats the model might produce:
    /// Bracket:  [SUMMARY] or [0]
    /// Markdown: ### SUMMARY or ### Article 0
    /// Multiline + IgnoreCase so ^ matches at each line start and case varies.</summary>
    [GeneratedRegex(@"^(?:\[|#{1,4}\s*)(SUMMARY|ARTICLE\s*\d+|\d+)\]?\s*:?\s*",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex MarkerRegex { get; }

    public NewsService(IHttpClientFactory clientFactory, ConfigFileService configFile,
        ILogger<NewsService> logger, DatabaseService? db = null)
    {
        _clientFactory = clientFactory;
        _configFile = configFile;
        _logger = logger;
        _db = db;
        HydrateCacheFromDisk();
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
        ResetTokenCounters();

        // Parallel feed fetches — sources are independent, so all run concurrently.
        // Each writes to a ConcurrentDictionary (thread-safe — the tasks run on
        // different thread-pool threads and a regular Dictionary can corrupt under
        // concurrent writes, even with different keys).
        var sources = await GetSourcesAsync(ct);
        var sourceLists = new ConcurrentDictionary<string, List<NewsItem>>();
        var fetchTasks = sources.Select(s => FetchSourceAsync(s, q, ct)
            .ContinueWith(t => { if (t.Result.Count > 0) sourceLists[s.Label] = t.Result; }, ct));
        await Task.WhenAll(fetchTasks);

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

        // Phase 1: fetch article bodies in parallel (snippet-first). Bounded concurrency
        // because article-page HTTP fetches are independent but we don't want dozens of
        // simultaneous connections. No LLM calls here — bodies are just raw text for
        // the summarization phase.
        var semaphore = new SemaphoreSlim(MaxConcurrentItems, MaxConcurrentItems);
        var bodyTasks = interleaved.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var body = item.Snippet ?? "";
                if (body.Length < ThinSnippetThreshold && !IsJsRenderedUrl(item.Url))
                {
                    var fetched = await TryFetchArticleBodyAsync(item.Url, ct);
                    if (fetched.Length > body.Length) body = fetched;
                }
                return (item, body);
            }
            finally { semaphore.Release(); }
        });
        var bodies = (await Task.WhenAll(bodyTasks)).ToList();

        // Phase 2: summarize with caching. Three branches:
        //   (a) All items cached → check batch cache → 0 or 1 LLM call (batch only)
        //   (b) All items miss → single-call (if feasible) or fallback, then cache results
        //   (c) Partial hit → fallback for misses only, reuse cached hits, then cache misses
        var now = DateTime.UtcNow;
        var cacheKeys = bodies.Select(b => NormalizeUrl(b.item.Url)).ToList();
        var contentHashes = bodies.Select(b => HashBody(b.body)).ToList();

        // Check per-item cache.
        var cached = new string?[bodies.Count];
        var missIndices = new List<int>();
        for (var i = 0; i < bodies.Count; i++)
        {
            if (_summaryCache.TryGetValue(cacheKeys[i], out var entry)
                && entry.ContentHash == contentHashes[i]
                && entry.Model == model
                && (now - entry.CachedAt) < CacheTtl)
            {
                cached[i] = entry.Summary;
            }
            else
            {
                cached[i] = null;
                missIndices.Add(i);
            }
        }
        var allHit = missIndices.Count == 0;
        var allMiss = missIndices.Count == bodies.Count;

        string batchSummary;
        List<string> itemSummaries;

        if (allHit)
        {
            // (a) All per-item summaries are cached. Check batch cache.
            var batchKey = MakeBatchKey(q, cacheKeys);
            itemSummaries = cached.Select(s => s!).ToList();

            if (_batchCache.TryGetValue(batchKey, out var batchEntry)
                && (now - batchEntry.CachedAt) < CacheTtl)
            {
                batchSummary = batchEntry.Summary;
                _logger.LogDebug("News cache: all items + batch hit — zero LLM calls");
            }
            else
            {
                // Per-items cached, batch needs refresh — one batch call over cached summaries.
                var summariesWithItems = bodies.Select((b, i) => (b.item, cached[i]!)).ToList();
                batchSummary = await SummarizeBatchAsync(q, summariesWithItems, model, baseUrl, ct);
                _batchCache[batchKey] = new BatchCacheEntry(batchSummary, now);
                _logger.LogDebug("News cache: all items hit, batch refreshed — 1 LLM call");
            }
        }
        else if (allMiss)
        {
            // (b) Cold cache — try single-call (with relevance filtering) or fallback.
            var combinedChars = bodies.Sum(b => Math.Min((b.body ?? "").Length, MaxArticleCharsForSummary));

            if (interleaved.Count >= 2 && interleaved.Count <= MaxItemsForSingleCall
                && combinedChars <= MaxCombinedCharsForSingleCall)
            {
                var (ok, summary, summaries) = await TrySummarizeAllAsync(q, bodies, model, baseUrl, ct);
                if (ok)
                {
                    batchSummary = summary;
                    itemSummaries = summaries;
                    // Cache only non-empty summaries (filtered-out items have empty strings
                    // and must be re-evaluated on the next run).
                    for (var i = 0; i < bodies.Count; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(itemSummaries[i]))
                            _summaryCache[cacheKeys[i]] = new SummaryCacheEntry(itemSummaries[i], contentHashes[i], now, model);
                    }
                }
                else
                {
                    (batchSummary, itemSummaries) = await SummarizeAllFallbackAsync(q, bodies, model, baseUrl, ct);
                    for (var i = 0; i < bodies.Count; i++)
                        _summaryCache[cacheKeys[i]] = new SummaryCacheEntry(itemSummaries[i], contentHashes[i], now, model);
                }
            }
            else
            {
                (batchSummary, itemSummaries) = await SummarizeAllFallbackAsync(q, bodies, model, baseUrl, ct);
                for (var i = 0; i < bodies.Count; i++)
                    _summaryCache[cacheKeys[i]] = new SummaryCacheEntry(itemSummaries[i], contentHashes[i], now, model);
            }

            // Cache the batch summary.
            var batchKey = MakeBatchKey(q, cacheKeys);
            _batchCache[batchKey] = new BatchCacheEntry(batchSummary, now);
        }
        else
        {
            // (c) Partial cache hit — summarize only the miss items (per-item calls),
            // reuse cached summaries for hits, then one batch call over all items.
            var missBodies = missIndices.Select(i => bodies[i]).ToList();
            var missSemaphore = new SemaphoreSlim(MaxConcurrentItems, MaxConcurrentItems);
            var missTasks = missBodies.Select(async entry =>
            {
                await missSemaphore.WaitAsync(ct);
                try { return (entry.item, summary: await SummarizeAsync(entry.item.Title, entry.body, model, baseUrl, ct)); }
                finally { missSemaphore.Release(); }
            });
            var missResults = (await Task.WhenAll(missTasks)).ToList();

            // Store miss results in cache.
            for (var j = 0; j < missBodies.Count; j++)
            {
                var idx = missIndices[j];
                _summaryCache[cacheKeys[idx]] = new SummaryCacheEntry(missResults[j].summary, contentHashes[idx], now, model);
            }

            // Merge cached + fresh into a single ordered list.
            itemSummaries = new List<string>(bodies.Count);
            var mergedForBatch = new List<(NewsItem item, string summary)>(bodies.Count);
            var missIdx = 0;
            for (var i = 0; i < bodies.Count; i++)
            {
                if (cached[i] != null)
                {
                    itemSummaries.Add(cached[i]!);
                    mergedForBatch.Add((bodies[i].item, cached[i]!));
                }
                else
                {
                    itemSummaries.Add(missResults[missIdx].summary);
                    mergedForBatch.Add(missResults[missIdx]);
                    missIdx++;
                }
            }

            batchSummary = await SummarizeBatchAsync(q, mergedForBatch, model, baseUrl, ct);
            _batchCache[MakeBatchKey(q, cacheKeys)] = new BatchCacheEntry(batchSummary, now);
            _logger.LogDebug("News cache: {Hit} hits, {Miss} misses — {Miss}+1 LLM calls", bodies.Count - missIndices.Count, missIndices.Count, missIndices.Count);
        }

        // Assemble output. Fill any missing item summaries with snippet fallbacks
        // (truncation, not filtering — we no longer ask the model to omit items).
        // The `kept` filter drops only truly empty entries (no snippet either).
        var kept = new List<(NewsItem item, string summary)>();
        for (var i = 0; i < interleaved.Count; i++)
        {
            var summary = i < itemSummaries.Count ? itemSummaries[i] : "";
            if (string.IsNullOrWhiteSpace(summary))
            {
                // Fallback to the feed snippet so the item isn't lost from output.
                var body = i < bodies.Count ? bodies[i].body : "";
                summary = string.IsNullOrWhiteSpace(body) ? interleaved[i].Title : TruncateFallback(body);
            }
            if (!string.IsNullOrWhiteSpace(summary))
                kept.Add((interleaved[i], summary));
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Weaver web results");
        sb.AppendLine($"Task: {q}");
        sb.AppendLine($"Generated: {generatedAt}");
        sb.AppendLine();
        sb.AppendLine($"### WEB RESULTS [{q}] ###");
        sb.AppendLine("## Summary");
        sb.AppendLine(batchSummary);

        if (kept.Count > 0)
        {
            sb.AppendLine($"Source: {kept[0].item.Url}");
            sb.AppendLine();
            sb.AppendLine("## Results");
            foreach (var (item, summary) in kept)
            {
                var oneLiner = ExtractOneLiner(summary);
                sb.AppendLine($"  - {item.Title}: {oneLiner} ({item.Url})");
            }
        }

        LogTokenUsage(q);
        PersistCacheToDisk();
        return (sb.ToString(), null);
    }

    /// <summary>
    /// Extracts the first line of a summary for the bullet format. Falls back to
    /// the full (trimmed) summary when there's no newline. Safe on empty strings.
    /// </summary>
    internal static string ExtractOneLiner(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "";
        var nl = summary.IndexOf('\n');
        return nl >= 0 ? summary[..nl].Trim() : summary.Trim();
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

    /// <summary>Feed type for config-driven sources.</summary>
    private enum FeedType { Rss, Atom, JsonApi }

    /// <summary>One news source: URL, parser type, and label. Built-ins are
    /// hardcoded; custom sources come from config (newsFeedUrls).</summary>
    private sealed record NewsSource(string Url, FeedType Type, string Label, string QueryParam);

    /// <summary>The 4 built-in sources, always available unless newsReplaceBuiltinSources
    /// is true. HN and arXiv use query-aware APIs; VB and TC are fixed AI-category feeds.</summary>
    private static readonly NewsSource[] BuiltinSources = new[]
    {
        new NewsSource("https://venturebeat.com/category/ai/feed/", FeedType.Rss, "VentureBeat AI", ""),
        new NewsSource("https://techcrunch.com/category/artificial-intelligence/feed/", FeedType.Rss, "TechCrunch AI", ""),
        new NewsSource("https://hn.algolia.com/api/v1/search?query={q}&tags=story&hitsPerPage=10", FeedType.JsonApi, "Hacker News", "{q}"),
        new NewsSource("https://export.arxiv.org/api/query?search_query={q}&sortBy=submittedDate&sortOrder=descending&max_results=8", FeedType.Atom, "arXiv", "{q}"),
    };

    /// <summary>Builds the active source list: built-ins (unless replaced) + custom
    /// URLs from config. Custom URLs are auto-detected as RSS or Atom based on the
    /// response content (tried RSS first, then Atom). Returns the merged list.</summary>
    private async Task<List<NewsSource>> GetSourcesAsync(CancellationToken ct)
    {
        var sources = BuiltinSources.ToList();
        var cfg = await _configFile.LoadConfigAsync();
        if (cfg.newsReplaceBuiltinSources)
            sources.Clear();

        foreach (var url in cfg.newsFeedUrls ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            var u = url.Trim();
            // Derive a label from the host.
            string label;
            try { label = new Uri(u).Host; } catch { label = "Custom"; }
            // Default to RSS; the fetcher will try Atom if RSS parsing yields nothing.
            sources.Add(new NewsSource(u, FeedType.Rss, label, ""));
        }
        return sources;
    }

    /// <summary>
    /// Fetches items from a single source. Dispatches by FeedType: RSS → ParseRssItems,
    /// Atom → ParseAtomItems, JsonApi → HN-style JSON. Query-aware sources (HN, arXiv)
    /// substitute {q} in the URL with the escaped query. Custom RSS sources that yield
    /// zero items get a second try as Atom.
    /// </summary>
    private async Task<List<NewsItem>> FetchSourceAsync(NewsSource source, string query, CancellationToken ct)
    {
        var items = new List<NewsItem>();
        try
        {
            var url = source.QueryParam.Length > 0
                ? source.Url.Replace(source.QueryParam, Uri.EscapeDataString(query))
                : source.Url;
            // For arXiv, the {q} placeholder is "all:escapedQuery" or "cat:cs.AI".
            if (source.Label == "arXiv")
            {
                var term = string.IsNullOrWhiteSpace(query) ? "cat:cs.AI" : $"all:{Uri.EscapeDataString(query)}";
                url = source.Url.Replace(source.QueryParam, term);
            }
            var xml = await MakeFeedClient().GetStringAsync(url, ct);
            if (source.Type == FeedType.Rss)
            {
                ParseRssItems(xml, source.Label, items);
                // Custom RSS source that yielded nothing might actually be Atom.
                if (items.Count == 0 && !IsBuiltinSource(source))
                    ParseAtomItems(xml, source.Label, items);
            }
            else if (source.Type == FeedType.Atom)
            {
                ParseAtomItems(xml, source.Label, items);
            }
            else if (source.Type == FeedType.JsonApi)
            {
                ParseHnJson(xml, source.Label, items);
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "{Source} feed failed — skipping", source.Label); }
        return items;
    }

    private static bool IsBuiltinSource(NewsSource s)
        => BuiltinSources.Any(b => b.Url == s.Url);

    /// <summary>Parse HN-style Algolia JSON: hits[] with title/url/created_at_i/points.</summary>
    private void ParseHnJson(string json, string source, List<NewsItem> items)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return;
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
            items.Add(new NewsItem(title!, link!, pub, source,
                pts > 0 ? $"{source} story ({pts} points)." : $"{source} story."));
        }
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
        // Use Authority (not Host) so non-default ports are preserved — two different
        // services on the same host:port pair shouldn't collide.
        try
        {
            var u = new Uri(url);
            return u.Authority + u.AbsolutePath;
        }
        catch { return url.TrimEnd('/'); }
    }

    /// <summary>
    /// Computes a content fingerprint for an article body, used as the cache
    /// invalidation key. Hashes the first <see cref="ContentHashChars"/> chars
    /// (after whitespace normalization) so page chrome/footer changes don't
    /// produce false cache misses. Returns a base16 string.
    /// </summary>
    internal static string HashBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "empty";
        var slice = body.Length > ContentHashChars ? body[..ContentHashChars] : body;
        slice = WhitespaceRegex.Replace(slice, " ").Trim();
        return System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(slice)) is var b && b.Length > 0
            ? Convert.ToHexString(b)[..16]
            : "empty";
    }

    /// <summary>
    /// Builds the batch-cache key from the query and the sorted set of normalized
    /// URLs. The same articles + same query reuses the batch summary regardless of
    /// item order (URLs are sorted for determinism).
    /// </summary>
    internal static string MakeBatchKey(string query, IEnumerable<string> normalizedUrls)
    {
        var urls = string.Join("|", normalizedUrls.OrderBy(u => u, StringComparer.Ordinal));
        return $"{query}::{urls}";
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

    internal static string CleanExtractedText(string s)
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
            var respText = await CallLlmAsync(model, baseUrl, req, ct);
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
            var respText = await CallLlmAsync(model, baseUrl, req, ct);
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

    // ── Single consolidated call (N+1 → 1) ──────────────────────────────────

    /// <summary>
    /// Single consolidated LLM call: produces both the per-item summaries and the
    /// batch overview in one round-trip using marker-delimited output ([SUMMARY]
    /// and [N] markers). The model sees all articles at once, so the overview is
    /// more coherent than summarizing first-sentences separately.
    ///
    /// Returns (true, summary, itemSummaries) on success; (false, _, _) when the
    /// model didn't follow the marker format or the response was empty — the caller
    /// then falls back to the N+1 per-item path. Missing items within a partially-
    /// parseable response are filled with snippet fallbacks (no full retry).
    /// </summary>
    private async Task<(bool ok, string summary, List<string> itemSummaries)> TrySummarizeAllAsync(
        string query, List<(NewsItem item, string body)> bodies,
        string model, string baseUrl, CancellationToken ct)
    {
        var userContent = new StringBuilder();
        for (var i = 0; i < bodies.Count; i++)
        {
            var (item, body) = bodies[i];
            var bodyText = body ?? "";
            if (bodyText.StartsWith(item.Title, StringComparison.OrdinalIgnoreCase))
                bodyText = bodyText[item.Title.Length..].TrimStart('\n', ' ', '\r');
            var text = string.IsNullOrWhiteSpace(bodyText) ? item.Title : $"{item.Title}\n{bodyText}";
            if (text.Length > MaxArticleCharsForSummary)
                text = text[..MaxArticleCharsForSummary] + "…";
            userContent.Append($"[{i}]\n{text}\n\n");
        }

        if (userContent.Length < MinSummaryBodyLength)
            return (false, "", new List<string>());

            try
        {
            var systemPrompt = $"Summarize each article as ### Article 0 through ### Article {bodies.Count - 1} "
                              + "(≤150 words each, key facts, no opinion). "
                              + $"Then write ### SUMMARY: a ≤200-word overview focused on stories most relevant "
                              + $"to the query \"{query}\". Group related topics. Mention tangential stories briefly.";
            var req = new
            {
                model,
                stream = false,
                temperature = 0.2,
                max_tokens = SingleCallMaxTokens,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent.ToString() }
                }
            };
            var respText = await CallLlmAsync(model, baseUrl, req, ct, timeoutMinutes: 4);
            var responseText = ExtractContent(respText);

            if (string.IsNullOrWhiteSpace(responseText))
                return (false, "", new List<string>());

            var (summary, itemSummaries, markersFound) = ParseMarkerResponse(responseText, bodies.Count);

            _logger.LogDebug("Single-call response ({Len} chars): {Response}", responseText.Length, responseText);
            _logger.LogDebug("Parsed: hasSummary={HasSummary}, markers={Found}/{Expected}", summary != null, markersFound, bodies.Count);

            // Fallback only if the model didn't write a [SUMMARY] marker at all (format
            // failure). We do NOT fall back on few/zero item markers — with relevance
            // filtering, the model intentionally omits irrelevant articles, so fewer
            // items is expected behavior, not a parse failure. Zero items + a SUMMARY
            // means "no relevant results" — that's a valid answer, not a failure.
            if (summary == null)
                return (false, "", new List<string>());

            // Do NOT fill missing items with snippet fallbacks. With relevance
            // filtering, the model intentionally omits irrelevant articles — an
            // empty summary means "filtered out", not "parse failure". The output
            // assembly drops items with empty summaries. Filling them would
            // defeat the filtering feature by re-including every article.

            if (string.IsNullOrWhiteSpace(summary))
                return (false, "", new List<string>());

            return (true, summary.Trim(), itemSummaries);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Single-call summarization failed — falling back to per-item");
            return (false, "", new List<string>());
        }
    }

    /// <summary>
    /// Fallback: per-item summarization (N calls) + batch summary (1 call). This is
    /// the original N+1 approach, used when the single consolidated call isn't
    /// feasible (too many items, too much text, 1 item) or didn't produce parseable
    /// output. Kept intact as the safety net.
    /// </summary>
    private async Task<(string batchSummary, List<string> itemSummaries)> SummarizeAllFallbackAsync(
        string query, List<(NewsItem item, string body)> bodies,
        string model, string baseUrl, CancellationToken ct)
    {
        var semaphore = new SemaphoreSlim(MaxConcurrentItems, MaxConcurrentItems);
        var tasks = bodies.Select(async entry =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var summary = await SummarizeAsync(entry.item.Title, entry.body, model, baseUrl, ct);
                return (entry.item, summary);
            }
            finally { semaphore.Release(); }
        });
        var summariesWithItems = (await Task.WhenAll(tasks)).ToList();
        var itemSummaries = summariesWithItems.Select(s => s.summary).ToList();
        var batchSummary = await SummarizeBatchAsync(query, summariesWithItems, model, baseUrl, ct);
        return (batchSummary, itemSummaries);
    }

    /// <summary>
    /// Parses a marker-delimited LLM response into the overview and per-item
    /// summaries. Expected format:
    /// <code>
    /// [SUMMARY]
    /// overview text...
    /// [0]
    /// item 0 summary...
    /// [1]
    /// item 1 summary...
    /// </code>
    /// Returns (summary, itemSummaries, itemMarkersFound). summary is null when the
    /// [SUMMARY] marker is absent. itemSummaries is padded to expectedCount with
    /// empty strings for missing items. itemMarkersFound counts how many [N] markers
    /// matched a valid index — the caller uses this to decide whether to accept or
    /// fall back (fewer than half → fallback).
    /// </summary>
    internal static (string? summary, List<string> itemSummaries, int itemMarkersFound)
        ParseMarkerResponse(string response, int expectedCount)
    {
        var matches = MarkerRegex.Matches(response);
        if (matches.Count == 0)
            return (null, Enumerable.Repeat("", expectedCount).ToList(), 0);

        string? summary = null;
        var items = Enumerable.Repeat("", expectedCount).ToArray();
        var itemMarkersFound = 0;

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var label = m.Groups[1].Value.Trim();
            var start = m.Index + m.Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : response.Length;
            var text = response[start..end].Trim();

            if (label.StartsWith("SUMMARY", StringComparison.OrdinalIgnoreCase))
            {
                summary = text;
            }
            else
            {
                // Extract the numeric index from "Article 0" or "0".
                var numStr = label.StartsWith("ARTICLE", StringComparison.OrdinalIgnoreCase)
                    ? label[label.IndexOfAny("0123456789".ToCharArray())..]
                    : label;
                if (int.TryParse(numStr, out var idx) && idx >= 0 && idx < expectedCount)
                {
                    // Strip "**Summary:**" prefix that some models add before item text.
                    if (text.StartsWith("**", StringComparison.Ordinal))
                    {
                        var colon = text.IndexOf(':');
                        if (colon > 0 && colon < 25)
                            text = text[(colon + 1)..].TrimStart('*', ' ', '\n');
                    }
                    items[idx] = text;
                    itemMarkersFound++;
                }
            }
        }

        return (summary, items.ToList(), itemMarkersFound);
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

    internal static string ExtractContent(string respJson)
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

    internal static string TruncateFallback(string s)
        => s.Length > 400 ? s[..400] + "…" : s;

    // ── Cache persistence ────────────────────────────────────────────────────

    /// <summary>Loads the summary cache from SQLite on startup. Stale entries
    /// (older than CacheTtl) are dropped during hydration. Safe to call when
    /// _db is null (unit tests) — just skips persistence.</summary>
    private void HydrateCacheFromDisk()
    {
        if (_db == null) return;
        try
        {
            var json = _db.GetValue(CacheDbKey);
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            var now = DateTime.UtcNow;
            if (doc.RootElement.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in itemsEl.EnumerateArray())
                {
                    var key = el.TryGetProperty("k", out var k) ? k.GetString() : null;
                    var summary = el.TryGetProperty("s", out var s) ? s.GetString() : null;
                    var hash = el.TryGetProperty("h", out var h) ? h.GetString() : null;
                    var model = el.TryGetProperty("m", out var m) ? m.GetString() : null;
                    var cachedAtStr = el.TryGetProperty("t", out var t) ? t.GetString() : null;
                    if (key == null || summary == null || hash == null || model == null) continue;
                    if (!DateTime.TryParse(cachedAtStr, out var cachedAt)) continue;
                    if ((now - cachedAt) >= CacheTtl) continue;
                    _summaryCache[key] = new SummaryCacheEntry(summary, hash, cachedAt, model);
                }
            }
            if (doc.RootElement.TryGetProperty("batches", out var batchEl) && batchEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in batchEl.EnumerateArray())
                {
                    var key = el.TryGetProperty("k", out var k) ? k.GetString() : null;
                    var summary = el.TryGetProperty("s", out var s) ? s.GetString() : null;
                    var cachedAtStr = el.TryGetProperty("t", out var t) ? t.GetString() : null;
                    if (key == null || summary == null) continue;
                    if (!DateTime.TryParse(cachedAtStr, out var cachedAt)) continue;
                    if ((now - cachedAt) >= CacheTtl) continue;
                    _batchCache[key] = new BatchCacheEntry(summary, cachedAt);
                }
            }
            _logger.LogDebug("News cache hydrated: {Items} items, {Batches} batches from disk",
                _summaryCache.Count, _batchCache.Count);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to hydrate news cache from disk"); }
    }

    /// <summary>Persists the summary cache to SQLite, debounced to avoid writing on
    /// every cache miss. Only called when _db is non-null. Drops stale entries before
    /// serializing to keep the blob compact.</summary>
    private void PersistCacheToDisk()
    {
        if (_db == null) return;
        var now = DateTime.UtcNow;
        if (now.Ticks - Interlocked.Read(ref _lastPersistTicks) < CachePersistDebounce.Ticks)
            return;
        Interlocked.Exchange(ref _lastPersistTicks, now.Ticks);
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\"items\":[");
            var first = true;
            foreach (var kv in _summaryCache)
            {
                if ((now - kv.Value.CachedAt) >= CacheTtl) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append($"{{\"k\":{JsonSerializer.Serialize(kv.Key)},\"s\":{JsonSerializer.Serialize(kv.Value.Summary)},");
                sb.Append($"\"h\":{JsonSerializer.Serialize(kv.Value.ContentHash)},\"m\":{JsonSerializer.Serialize(kv.Value.Model)},");
                sb.Append($"\"t\":{JsonSerializer.Serialize(kv.Value.CachedAt.ToString("O"))}}}");
            }
            sb.Append("],\"batches\":[");
            first = true;
            foreach (var kv in _batchCache)
            {
                if ((now - kv.Value.CachedAt) >= CacheTtl) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append($"{{\"k\":{JsonSerializer.Serialize(kv.Key)},\"s\":{JsonSerializer.Serialize(kv.Value.Summary)},");
                sb.Append($"\"t\":{JsonSerializer.Serialize(kv.Value.CachedAt.ToString("O"))}}}");
            }
            sb.Append("]}");
            _db.SetValue(CacheDbKey, sb.ToString());
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to persist news cache to disk"); }
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private HttpClient MakeFeedClient()
    {
        // A fresh client (not the "llama" one) — these are public feed fetches,
        // independent of the LLM endpoint's long timeout and base URL.
        // 10s is enough for RSS/JSON feeds; arXiv occasionally hangs at 20s
        // (the previous default), which blocked the entire pipeline.
        var client = _clientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WeaverNews/1.0 (keyless RSS aggregator)");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        return client;
    }
}
