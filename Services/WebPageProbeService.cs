using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace Weaver.Services;

/// <summary>A deterministic read of one web page: title, headings, links, buttons,
/// inputs, and the visible text — everything a blind model needs to "see" a page
/// without a browser. Built from real DOM parsing, never from assumptions.</summary>
public sealed class PageSnapshot
{
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> Headings { get; init; } = new();
    public List<PageLink> Links { get; init; } = new();
    public List<string> Buttons { get; init; } = new();
    public List<string> Inputs { get; init; } = new();
    public string BodyText { get; init; } = "";
}

/// <summary>A navigation link with its visible text and target URL.</summary>
public sealed record PageLink(string Text, string? Href);

/// <summary>The section of a page that best matches what a prompt asked to test.</summary>
public sealed record SectionMatch(string Label, string? Url, double Score, string Kind);

/// <summary>One deterministic observation from a live test (info / pass / fail / warning).</summary>
public sealed record TestFinding(string Kind, string Message);

/// <summary>
/// Reads and navigates a live web server WITHOUT a browser: fetch the HTML over HTTP and
/// parse it with AngleSharp into a <see cref="PageSnapshot"/> (title, headings, links,
/// buttons, inputs, visible text). Section discovery ("find the section the prompt
/// mentions") is keyword ranking over the real DOM, and verification is deterministic
/// presence/absence checks — no LLM anywhere in the loop, so a very basic model gets the
/// same reliable results as a strong one.
/// </summary>
public static class WebPageProbeService
{
    private static readonly string[] StopWords =
    {
        "the", "a", "an", "and", "or", "but", "of", "to", "in", "on", "at", "for", "with",
        "from", "by", "is", "are", "was", "were", "be", "been", "it", "its", "this", "that",
        "these", "those", "test", "verify", "check", "make", "sure", "ensure", "page",
        "does", "can", "you", "me", "please", "should", "would", "have", "has", "had",
        "load", "loads", "works", "working", "render", "renders", "open", "opens", "show",
        "shows", "display", "displays", "there", "when", "how", "what", "which", "who",
        "if", "then", "else", "not", "no", "yes", "also", "into", "over", "under", "about",
        "than", "too", "very", "just", "only", "will", "shall", "can", "do", "did", "done"
    };

    /// <summary>Fetches a URL over HTTP and parses it into a snapshot. Throws on network
    /// failure so callers can report exactly what went wrong.</summary>
    public static async Task<PageSnapshot> FetchSnapshotAsync(string url, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Weaver-LiveTest/1.0");
        using var resp = await http.GetAsync(url, ct);
        var html = await resp.Content.ReadAsStringAsync(ct);
        return SnapshotFromHtml(html, resp.RequestMessage?.RequestUri?.ToString() ?? url);
    }

    /// <summary>Parses raw HTML into a snapshot. The workhorse — everything the probe
    /// "sees" comes from here, so tests can feed fixture HTML directly.</summary>
    public static PageSnapshot SnapshotFromHtml(string html, string url = "http://localhost/")
    {
        var parser = new HtmlParser();
        using var doc = parser.ParseDocument(html);
        var headings = new List<string>();
        foreach (var h in doc.QuerySelectorAll("h1, h2, h3, h4, h5, h6"))
        {
            var text = NormalizeText(h.TextContent);
            if (text.Length > 0) headings.Add(text);
        }
        var links = new List<PageLink>();
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var text = NormalizeText(a.TextContent);
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(href)) continue;
            if (string.IsNullOrWhiteSpace(text))
            {
                var aria = a.GetAttribute("aria-label");
                text = aria != null ? NormalizeText(aria) : "";
            }
            if (string.IsNullOrWhiteSpace(text) && href != null)
                text = href.Length > 60 ? href[..60] : href;
            links.Add(new PageLink(text, href));
        }
        var buttons = new List<string>();
        foreach (var b in doc.QuerySelectorAll("button, input[type='submit'], input[type='button'], [role='button']"))
        {
            var text = NormalizeText(b.TextContent);
            if (text.Length == 0)
            {
                var value = b.GetAttribute("value");
                var aria = b.GetAttribute("aria-label");
                text = value ?? aria ?? "";
                text = NormalizeText(text);
            }
            if (text.Length > 0) buttons.Add(text);
        }
        var inputs = new List<string>();
        foreach (var i in doc.QuerySelectorAll("input, textarea, select"))
        {
            var type = i.GetAttribute("type") ?? "text";
            var name = i.GetAttribute("name") ?? i.GetAttribute("id") ?? "";
            if (type == "hidden" || type == "password") continue;
            inputs.Add(string.IsNullOrWhiteSpace(name) ? type : $"{type} \"{name}\"");
        }
        // TextContent concatenates block texts without spaces ("404 Not FoundPage not
        // found") — walk the DOM instead, inserting a space after every text node and
        // skipping script/style, so the text reads like a browser's innerText.
        var bodyText = NormalizeText(CollectBodyText(doc.Body ?? doc.DocumentElement));
        if (bodyText.Length > 30000) bodyText = bodyText[..30000];
        return new PageSnapshot
        {
            Url = url,
            Title = NormalizeText(doc.Title),
            Headings = headings,
            Links = links,
            Buttons = buttons,
            Inputs = inputs,
            BodyText = bodyText
        };
    }

    /// <summary>
    /// Finds the section of a snapshot that best matches a target ("kanban board",
    /// "calendar page"). Ranks headings, links, and buttons by keyword overlap with the
    /// target and prompt words; returns null when nothing plausibly matches so callers
    /// can fall back to verifying the current page. Deterministic: same snapshot +
    /// same target → same section.
    /// </summary>
    public static SectionMatch? FindTargetSection(PageSnapshot snapshot, string target, string? prompt = null)
    {
        var keywords = ExtractKeywords(target, prompt);
        if (keywords.Count == 0) return null;

        SectionMatch? best = null;
        void Consider(string label, string? url, double score, string kind)
        {
            if (best == null || score > best.Score) best = new SectionMatch(label, url, score, kind);
        }

        foreach (var heading in snapshot.Headings)
            Consider(heading, null, ScoreText(heading, keywords, headingBonus: 3), "heading");
        foreach (var link in snapshot.Links)
            Consider(link.Text, link.Href, ScoreText(link.Text, keywords, headingBonus: 2), "link");
        foreach (var button in snapshot.Buttons)
            Consider(button, null, ScoreText(button, keywords, headingBonus: 1), "button");

        return best != null && best.Score >= 2.5 ? best : null;
    }

    /// <summary>True when the page snapshot shows any of the given signals (case-insensitive
    /// word-boundary substring) in its headings, links, buttons, or body text.</summary>
    public static bool PageMentions(PageSnapshot snapshot, params string[] signals)
    {
        foreach (var s in signals)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var needle = NormalizeText(s);
            if (snapshot.Headings.Any(h => h.Contains(needle, StringComparison.OrdinalIgnoreCase)) ||
                snapshot.Links.Any(l => l.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)) ||
                snapshot.Buttons.Any(b => b.Contains(needle, StringComparison.OrdinalIgnoreCase)) ||
                snapshot.BodyText.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Deterministic verification findings for a snapshot given the tested target.
    /// Returns a list of observations; a FAIL finding marks the test as not passed.
    /// </summary>
    public static List<TestFinding> Verify(PageSnapshot snapshot, string target)
    {
        var findings = new List<TestFinding>();
        if (string.IsNullOrWhiteSpace(snapshot.Title))
            findings.Add(new TestFinding("warning", "The page has no <title>."));
        else
            findings.Add(new TestFinding("pass", $"Page title: \"{snapshot.Title}\"."));

        if (snapshot.Headings.Count == 0)
            findings.Add(new TestFinding("warning", "The page has no headings (h1-h6) — content may be missing or JavaScript-rendered."));

        if (!string.IsNullOrWhiteSpace(target))
        {
            var keywords = ExtractKeywords(target, null);
            if (keywords.Count > 0 && PageMentions(snapshot, target) is false && !snapshot.BodyText.Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                var mention = PageMentions(snapshot, keywords.ToArray());
                if (mention)
                    findings.Add(new TestFinding("pass", $"Page content relates to \"{target}\" (keyword match)."));
                else
                    findings.Add(new TestFinding("fail", $"Could not find content matching \"{target}\" on the page."));
            }
            else if (PageMentions(snapshot, target))
            {
                findings.Add(new TestFinding("pass", $"Page content matching \"{target}\" is present."));
            }
        }
        else
        {
            findings.Add(new TestFinding("info", "No specific target was named — verifying the page loads and has content."));
        }

        if (string.IsNullOrWhiteSpace(snapshot.BodyText))
            findings.Add(new TestFinding("fail", "The page has no visible text — nothing to inspect."));
        else
            findings.Add(new TestFinding("pass", $"Page has {CountWords(snapshot.BodyText)} words of visible text."));

        // Error signals — a rendered app page should not show these.
        if (Regex.IsMatch(snapshot.BodyText, @"\b(404 not found|page not found|unhandled exception|internal server error|application error)\b", RegexOptions.IgnoreCase))
            findings.Add(new TestFinding("fail", "The page shows an error message (404/exception/application error)."));

        return findings;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Collects visible text like a browser's innerText: every text node's
    /// content followed by a space, script/style/template contents skipped.</summary>
    internal static string CollectBodyText(AngleSharp.Dom.INode root)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(AngleSharp.Dom.INode node)
        {
            if (node is AngleSharp.Dom.IElement el &&
                el.LocalName is "script" or "style" or "noscript" or "template")
                return;
            if (node.NodeType == AngleSharp.Dom.NodeType.Text)
            {
                sb.Append(node.TextContent).Append(' ');
                return;
            }
            foreach (var child in node.ChildNodes) Walk(child);
        }
        Walk(root);
        return sb.ToString();
    }

    /// <summary>Extracts search keywords from the target and prompt: word-boundary
    /// phrases from the target plus significant words, minus stopwords.</summary>
    internal static List<string> ExtractKeywords(string target, string? prompt)
    {
        var result = new List<string>();
        void AddRange(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var word in Regex.Split(text, @"[^\w\-\.]+")
                         .Where(w => w.Length >= 3)
                         .Where(w => !StopWords.Contains(w))
                         .Where(w => !long.TryParse(w, out _)))
                if (!result.Contains(word, StringComparer.OrdinalIgnoreCase))
                    result.Add(word);
        }
        // Whole normalized target first (highest-value phrase), then its words.
        var normalizedTarget = NormalizeText(target);
        if (normalizedTarget.Length >= 3 && !result.Contains(normalizedTarget, StringComparer.OrdinalIgnoreCase))
            result.Add(normalizedTarget);
        AddRange(target);
        if (!string.IsNullOrWhiteSpace(prompt)) AddRange(prompt);
        return result;
    }

    private static double ScoreText(string text, List<string> keywords, double headingBonus)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var normalized = NormalizeText(text);
        var score = 0.0;
        // Full-phrase match is the strongest signal.
        if (normalized.Contains(keywords[0], StringComparison.OrdinalIgnoreCase))
            score += headingBonus + 6;
        foreach (var kw in keywords)
        {
            if (normalized.Contains(kw, StringComparison.OrdinalIgnoreCase))
                score += 2.5;
        }
        // Word overlap ratio for long texts.
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            var hits = words.Count(w => keywords.Any(k => w.Length >= 3 && k.Length >= 3 &&
                (k.Contains(w, StringComparison.OrdinalIgnoreCase) || w.Contains(k, StringComparison.OrdinalIgnoreCase))));
            score += 4.0 * hits / words.Length;
        }
        return score;
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static int CountWords(string text) =>
        Regex.Matches(text, @"\S+").Count;
}