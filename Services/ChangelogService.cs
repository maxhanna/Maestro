using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weaver.Services;

/// <summary>
/// Fetches changelog data directly from GitHub releases and formats it
/// for display. No local file caching — the UI always shows live data.
/// </summary>
public class ChangelogService
{
    private readonly IHttpClientFactory _clientFactory;
    private const string GitHubOwner = "maxhanna";
    private const string GitHubRepo = "Weaver";
    private DateTime _lastFetch = DateTime.MinValue;
    private static readonly object _lock = new();

    public ChangelogService(IHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    /// <summary>Last time the changelog was fetched from GitHub, or null if never.</summary>
    public DateTime? LastFetchTime
    {
        get { lock (_lock) { return _lastFetch == DateTime.MinValue ? null : _lastFetch; } }
    }

    /// <summary>Fetches all releases from GitHub and returns formatted changelog text.</summary>
    public async Task<string> FetchChangelogAsync()
    {
        var client = _clientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Weaver/1.0");
        client.Timeout = TimeSpan.FromSeconds(15);

        var releases = await FetchReleasesAsync(client);
        var content = FormatReleases(releases);

        lock (_lock) { _lastFetch = DateTime.UtcNow; }

        return content;
    }

    private async Task<List<GitHubRelease>> FetchReleasesAsync(HttpClient client)
    {
        var releases = new List<GitHubRelease>();
        var page = 1;

        while (page <= 5)
        {
            var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases?per_page=100&page={page}";
            var json = await client.GetStringAsync(url);
            var items = JsonSerializer.Deserialize<List<GitHubRelease>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items == null || items.Count == 0) break;
            releases.AddRange(items);
            if (items.Count < 100) break;
            page++;
        }

        return releases;
    }

    private static string FormatReleases(List<GitHubRelease> releases)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Weaver Changelog");
        sb.AppendLine($"# {releases.Count} release(s)");
        sb.AppendLine();

        for (var i = 0; i < releases.Count; i++)
        {
            var r = releases[i];
            var version = NormalizeVersion(r.TagName ?? "unknown");
            var date = r.PublishedAt?.ToString("MMM dd, yyyy") ?? "unknown";
            var body = (r.Body ?? "").Trim();

            sb.AppendLine($"# Release {version}");
            sb.AppendLine($"Released {date}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(body))
                FormatReleaseBody(sb, body);
            else
                sb.AppendLine("_(No release notes provided.)_");

            if (i < releases.Count - 1)
            {
                sb.AppendLine();
                sb.AppendLine(new string('─', 60));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string NormalizeVersion(string tag)
    {
        tag = tag.Trim();
        return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag : "v" + tag;
    }

    private static void FormatReleaseBody(System.Text.StringBuilder sb, string body)
    {
        var lines = body.Split('\n');
        var hasSections = lines.Any(l =>
            l.TrimStart().StartsWith("### ", StringComparison.OrdinalIgnoreCase) &&
            (l.Contains("Added", StringComparison.OrdinalIgnoreCase) ||
             l.Contains("Changed", StringComparison.OrdinalIgnoreCase) ||
             l.Contains("Fixed", StringComparison.OrdinalIgnoreCase) ||
             l.Contains("Removed", StringComparison.OrdinalIgnoreCase)));

        if (hasSections)
        {
            sb.AppendLine(body);
            return;
        }

        var added = new List<string>();
        var changed = new List<string>();
        var fixed_ = new List<string>();
        var other = new List<string>();

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim().TrimStart('-', '*', '•', '·');
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var lower = trimmed.ToLowerInvariant();

            if (lower.StartsWith("new ") || lower.StartsWith("added ") || lower.StartsWith("introducing ") ||
                lower.Contains("new feature") || lower.Contains("added support"))
                added.Add($"- {trimmed}");
            else if (lower.StartsWith("fixed ") || lower.StartsWith("bug fix") || lower.Contains("fix for") ||
                     lower.Contains("resolved ") || lower.Contains("patched "))
                fixed_.Add($"- {trimmed}");
            else if (lower.StartsWith("changed ") || lower.StartsWith("updated ") || lower.StartsWith("improved ") ||
                     lower.StartsWith("enhanced ") || lower.Contains("now ") || lower.Contains("refactor"))
                changed.Add($"- {trimmed}");
            else
                other.Add($"- {trimmed}");
        }

        if (added.Count > 0)
        {
            sb.AppendLine("### Added");
            sb.AppendLine();
            foreach (var l in added) sb.AppendLine(l);
            sb.AppendLine();
        }
        if (changed.Count > 0)
        {
            sb.AppendLine("### Changed");
            sb.AppendLine();
            foreach (var l in changed) sb.AppendLine(l);
            sb.AppendLine();
        }
        if (fixed_.Count > 0)
        {
            sb.AppendLine("### Fixed");
            sb.AppendLine();
            foreach (var l in fixed_) sb.AppendLine(l);
            sb.AppendLine();
        }
        if (other.Count > 0)
        {
            if (added.Count > 0 || changed.Count > 0 || fixed_.Count > 0)
            {
                sb.AppendLine("### Other");
                sb.AppendLine();
            }
            foreach (var l in other) sb.AppendLine(l);
        }
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("published_at")]
        public DateTime? PublishedAt { get; set; }
        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }
}
