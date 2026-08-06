using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Pure lexical (BM25) scoring of project files against a task prompt. Extracted
/// from AgentController.Discovery.cs so the ranking logic is unit-testable without
/// reflection and reusable outside the controller.
/// </summary>
public static class Bm25Scorer
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".js", ".tsx", ".jsx", ".html", ".css", ".scss", ".less", ".vue", ".svelte",
        ".json", ".xml", ".yml", ".yaml", ".md", ".sql", ".py", ".java", ".go", ".rs", ".rb",
        ".php", ".c", ".h", ".cpp", ".hpp", ".fs", ".fsx", ".sh", ".bat", ".ps1", ".ini", ".cfg",
        ".env", ".gradle", ".properties", ".conf", ".toml", ".proto", ".graphql", ".prisma"
    };
    private static readonly HashSet<string> GeneratedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "composer.lock", "cargo.lock",
        "go.sum", "poetry.lock", "npm-shrinkwrap.json", "tsconfig.json", "tsconfig.tsbuildinfo",
        "angular.json", "project.json", "package.json", "global.json", "launch.json", "tasks.json"
    };
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "this", "that", "from", "into", "when", "what", "which",
        "where", "who", "how", "does", "do", "is", "are", "was", "were", "have", "has", "had",
        "not", "but", "can", "could", "would", "should", "will", "there", "their", "your", "its",
        "it's", "all", "any", "some", "each", "every", "both", "none", "one", "two", "if", "then",
        "else", "than", "too", "very", "just", "only", "also", "may", "might", "must", "about",
        "after", "before", "between", "during", "without", "within", "above", "below", "upon",
        "over", "under", "here", "there", "again", "once", "other", "more", "most", "such",
        "same", "still", "even", "though", "because", "since", "while", "until", "make", "made",
        "get", "got", "let", "put", "see", "show", "use", "used", "using", "need", "want", "like",
        "should", "please", "make", "sure", "ensure", "create", "add", "change", "fix", "bug"
    };

    /// <summary>Files scoring below this total don't get token attribution in the log —
    /// they collapse to a plain path so noisy prompts with many marginal matches stay readable.</summary>
    public const double AttributionMinScore = 2.0;

    /// <summary>
    /// Formats a file's BM25 entry for the log: "notepad.service.ts ← notepad(4.2), note(1.1)"
    /// when the file cleared the attribution threshold and carries token hits, otherwise the bare
    /// path (marginal matches and sibling files that were never lexically scored).
    /// </summary>
    public static string FormatHits(string file, double score, List<(string token, double contribution)>? tokenHits)
    {
        if (score < AttributionMinScore || tokenHits == null || tokenHits.Count == 0) return file;
        return $"{file} ← {string.Join(", ", tokenHits.Select(h => $"{h.token}({h.contribution:0.0})"))}";
    }

    /// <summary>Rank project files against the prompt using BM25 lexical scoring, returning the
    /// top 10 with per-token contribution attribution.</summary>
    public static List<(string file, double score, List<(string token, double contribution)> tokenHits)> ScoreProjectFiles(
        string prompt, string projectRoot, List<string> allFiles, CancellationToken ct)
    {
        var queryTokens = AgentDiscovery.ExtractMeaningfulKeywords(prompt.ToLowerInvariant())
            .Where(t => t.Length >= 2 && !StopWords.Contains(t))
            .ToList();
        if (queryTokens.Count == 0)
            queryTokens = Regex.Matches(prompt.ToLowerInvariant(), @"[a-z0-9_]{2,}")
                .Select(m => m.Value)
                .Where(t => !StopWords.Contains(t))
                .ToList();
        if (queryTokens.Count == 0) return new List<(string file, double score, List<(string token, double contribution)> tokenHits)>();
        var fileTokens = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        long totalTokens = 0;
        foreach (var rel in allFiles)
        {
            if (ct.IsCancellationRequested) return new List<(string file, double score, List<(string token, double contribution)> tokenHits)>();
            var ext = Path.GetExtension(rel);
            if (!TextExtensions.Contains(ext)) continue;
            if (rel.Contains(".min.", StringComparison.OrdinalIgnoreCase)) continue;
            var fileName = Path.GetFileName(rel);
            if (GeneratedNames.Contains(fileName)) continue;
            if (rel.Contains("/generated/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/migrations/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/wwwroot/lib/", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.GetFullPath(Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(full)) continue;
            string text;
            try
            {
                var fi = new FileInfo(full);
                if (fi.Length > 512 * 1024 || fi.Length == 0) continue;
                text = System.IO.File.ReadAllText(full);
            }
            catch { continue; }
            var toks = Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9_]{2,}")
                .Select(m => m.Value)
                .Where(t => !StopWords.Contains(t))
                .ToList();
            if (toks.Count < 20) continue;
            fileTokens[rel] = toks;
            totalTokens += toks.Count;
        }
        if (fileTokens.Count == 0) return new List<(string file, double score, List<(string token, double contribution)> tokenHits)>();
        var n = fileTokens.Count;
        var avgDl = (double)totalTokens / n;
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var toks in fileTokens.Values)
            foreach (var t in new HashSet<string>(toks, StringComparer.OrdinalIgnoreCase))
                df[t] = df.GetValueOrDefault(t) + 1;
        const double k1 = 1.5, b = 0.75;
        var idf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queryTokens)
            idf[q] = Math.Log(1 + (n - df.GetValueOrDefault(q) + 0.5) / (df.GetValueOrDefault(q) + 0.5));
        var scores = new List<(string file, double score, List<(string token, double contribution)> tokenHits)>();
        foreach (var kv in fileTokens)
        {
            if (ct.IsCancellationRequested) return new List<(string file, double score, List<(string token, double contribution)> tokenHits)>();
            var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in kv.Value) tf[t] = tf.GetValueOrDefault(t) + 1;
            var dl = kv.Value.Count;
            double s = 0;
            var hits = new List<(string token, double contribution)>();
            foreach (var q in queryTokens)
            {
                var f = tf.GetValueOrDefault(q);
                if (f == 0) continue;
                var contrib = idf[q] * (f * (k1 + 1)) / (f + k1 * (1 - b + b * dl / avgDl));
                s += contrib;
                hits.Add((q, contrib));
            }
            // Attribute filename/path bonuses to the matching query tokens so the
            // ranked list shows WHY a file scored highly (e.g. name contains 'notepad').
            var name = Path.GetFileNameWithoutExtension(kv.Key);
            foreach (var q in queryTokens)
            {
                double bonus = 0;
                if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) bonus += 3;
                if (kv.Key.Contains(q, StringComparison.OrdinalIgnoreCase)) bonus += 1;
                if (bonus <= 0) continue;
                s += bonus;
                var hitIdx = hits.FindIndex(h => h.token.Equals(q, StringComparison.OrdinalIgnoreCase));
                if (hitIdx >= 0) hits[hitIdx] = (q, hits[hitIdx].contribution + bonus);
                else hits.Add((q, bonus));
            }
            if (s > 0)
                scores.Add((kv.Key, s, hits.OrderByDescending(h => h.contribution).Take(5).ToList()));
        }
        return scores.OrderByDescending(x => x.score)
            .ThenBy(x => x.file.Length)
            .Take(10)
            .ToList();
    }
}
