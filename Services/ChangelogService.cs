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

        for (var i = 0; i < releases.Count; i++)
        {
            var r = releases[i];
            var version = NormalizeVersion(r.TagName ?? "unknown");
            var date = r.PublishedAt?.ToString("MMM dd, yyyy") ?? "unknown";
            var body = (r.Body ?? "").Trim();

            // Compact release header
            sb.AppendLine($"v{version.TrimStart('v')}  ({date})");

            if (!string.IsNullOrWhiteSpace(body))
                FormatReleaseBody(sb, body);
            else
                sb.AppendLine("  No release notes provided.");

            if (i < releases.Count - 1)
                sb.AppendLine();
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

        // Flatten: strip markdown, bullet markers, blank lines
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim().TrimStart('-', '*', '•', '·');
            // Skip blank lines and markdown headers/labels
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("### ", StringComparison.OrdinalIgnoreCase))
            {
                // Keep section labels but render them as compact labels
                var label = trimmed.Substring(4).Trim();
                sb.AppendLine($"  [{label}]");
                continue;
            }
            // Skip lines that are just section names with no content
            if (string.Equals(trimmed, "Added", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Changed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Fixed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "Removed", StringComparison.OrdinalIgnoreCase))
                continue;
            sb.AppendLine($"  \u2022 {trimmed}");
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
