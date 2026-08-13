using System.Text;
using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// The ONE web-need classifier. Two detectors previously drifted apart: the broad
/// "does this task need CURRENT EXTERNAL information?" gate (<c>TaskHintsWebNeed</c>) and
/// the news-digest router. A news-shaped prompt
/// ("fetch a recent AI news article …") passed the router but not the gate, so a dump task
/// missed the deterministic short-circuit and ran the full planning loop. Both now read the
/// same verdict from here, so classification and routing cannot disagree.
///
/// Three outcomes:
/// <list type="bullet">
/// <item><see cref="Kind.News"/> — news-y phrasing → the fresh-news RSS digest (real, dated,
/// deduped items with real URLs), fetched via a <c>_web_search</c> step.</item>
/// <item><see cref="Kind.Web"/> — any other live-data hint → plain <c>_web_search</c> /
/// <c>_web_fetch</c> (DuckDuckGo search or a direct URL).</item>
/// <item><see cref="Kind.None"/> — no external info needed; work from the repo.</item>
/// </list>
/// </summary>
public static class WebNeedClassifier
{
    public enum Kind { None, Web, News }

    // Trigger phrases that hint a task may want CURRENT EXTERNAL information.
    // Deliberately broad — "search for"/"look up" often mean searching the repo,
    // so a hit only opens the LLM verification gate, never rejects by itself.
    private static readonly string[] WebNeedHints =
    {
        "web search", "search the web", "web_search", "web fetch", "web_fetch",
        "internet", "online", "current", "up to date", "up-to-date", "latest",
        "live data", "today's", "todays", "fetch from", "fetch the", "google",
        "api docs", "search for", "look up", "find out",
        "news", "headline", "fetch a", "fetch an"
    };

    /// <summary>Classifies a prompt/query into None, Web or News. News wins first — a
    /// news-shaped prompt routes to the digest even when it also carries a generic hint.
    /// Classification is idempotent under case/whitespace folding: the text is lowercased
    /// and every run of whitespace collapsed to a single space first, so a prompt typed with
    /// extra spaces/newlines/tabs (or mixed case) classifies identically to its folded form.</summary>
    public static Kind Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Kind.None;
        var t = Normalize(text);
        if (IsNewsShaped(t)) return Kind.News;
        foreach (var hint in WebNeedHints)
            if (t.Contains(hint)) return Kind.Web;
        return Kind.None;
    }

    /// <summary>Lowercases and collapses every whitespace run to a single space (trimmed) so
    /// hint phrases with stray spaces/newlines still match — the basis of the folding invariant.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = true; continue; }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
            pendingSpace = false;
        }
        return sb.ToString();
    }

    /// <summary>True when the task needs CURRENT EXTERNAL information at all (News or Web).</summary>
    public static bool IsWebNeed(string? text) => Classify(text) != Kind.None;

    /// <summary>True for news-y phrasing (headline / breaking news / "latest AI …"), i.e. the
    /// prompt routes to the fresh-news digest instead of a plain search.</summary>
    public static bool IsNews(string? text) => Classify(text) == Kind.News;

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
    private static bool IsNewsShaped(string normalizedText)
    {
        var t = " " + normalizedText + " ";
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
}
