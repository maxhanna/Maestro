using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Weaver.Services;

using static Weaver.Services.AgentTokenMetrics;
using static Weaver.Services.AgentEditHeuristics;
using static Weaver.Services.AgentPlanParsing;
using static Weaver.Services.AgentMethodInventory;
using static Weaver.Services.AgentProjectUtilities;
using static Weaver.Services.AgentDiscovery;
using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentCodeFormatting;
using static Weaver.Services.AgentSkeleton;
using static Weaver.Services.AgentDiffUtilities;
using static Weaver.Services.AgentJsonUtilities;

/// <summary>Part of the split of the former AgentUtilities monolith.</summary>
public static class AgentSkeleton
{
    // Shared per-project skeleton cache: both the agent's suggestion context and
    // the BugHosted heartbeat reuse the same entry, so a directory walk (and the
    // .gitignore parse) only happens once per TTL window instead of per call.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime at, SkeletonResult result)> Cache =
        new();

    /// <summary>
    /// Returns the project skeleton, serving from the shared cache when it's
    /// fresher than <paramref name="ttlMinutes"/> (default 10 minutes — a
    /// project layout changes rarely, so a full walk every call is wasted work).
    /// </summary>
    public static async Task<SkeletonResult> GetCachedSkeletonAsync(string projectRoot, int ttlMinutes = 10)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return new SkeletonResult();
        if (Cache.TryGetValue(projectRoot, out var c) && (DateTime.UtcNow - c.at).TotalMinutes < ttlMinutes)
            return c.result;
        var result = await GenerateSkeletonAsync(projectRoot);
        Cache[projectRoot] = (DateTime.UtcNow, result);
        return result;
    }

    /// <summary>Drops cached skeletons (optionally just one project).</summary>
    public static void ClearSkeletonCache(string? projectRoot = null)
    {
        if (projectRoot == null) Cache.Clear();
        else Cache.TryRemove(projectRoot, out _);
    }

    public static async Task<SkeletonResult> GenerateSkeletonAsync(string projectRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### PROJECT SKELETON (file/directory layout) ###");
        sb.AppendLine();
        var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "bin", "obj", "dist", ".git", ".vs", ".svn",
            "packages", "coverage", ".idea", ".vscode", "__pycache__",
            ".next", ".nuget"
        };
        var excludeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".pdb", ".so", ".dylib", ".zip", ".tar", ".gz",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg",
            ".woff", ".woff2", ".ttf", ".eot", ".mp3", ".mp4", ".wav",
            ".o", ".a", ".lib", ".nupkg"
        };
        var gitignorePath = System.IO.Path.Combine(projectRoot, ".gitignore");
        var gitignoreDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gitignoreExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (System.IO.File.Exists(gitignorePath))
        {
            var gitignoreContent = await System.IO.File.ReadAllTextAsync(gitignorePath);
            foreach (var line in gitignoreContent.Split('\n', '\r'))
            {
                var trimmed = line.Trim().Trim('/');
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith('!')) continue;
                if (trimmed.StartsWith('*') && trimmed.Length > 1)
                {
                    var ext = trimmed[1..];
                    if (ext.Contains('.'))
                    {
                        gitignoreExts.Add(ext.ToLowerInvariant());
                        excludeExtensions.Add(ext.ToLowerInvariant());
                    }
                }
                else if (!trimmed.Contains('/') && !trimmed.Contains('*'))
                {
                    gitignoreDirs.Add(trimmed);
                    excludeDirs.Add(trimmed);
                }
                else if (trimmed.Contains('/') && !trimmed.Contains('*'))
                {
                    var last = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
                    gitignoreDirs.Add(last);
                    excludeDirs.Add(last);
                }
            }
        }
        var paths = new List<string>();
        var omittedCount = new MutableInt();
        await BuildSkeletonTree(sb, paths, projectRoot, "", excludeDirs, excludeExtensions, gitignoreDirs, gitignoreExts, projectRoot, omittedCount);
        if (omittedCount.Value > 0)
            sb.AppendLine($"\n({omittedCount.Value} file(s)/dir(s) omitted by .gitignore)");
        return new SkeletonResult { Tree = sb.ToString(), Paths = paths };
    }

    internal class MutableInt { public int Value; }

    public class SkeletonResult
    {
        public string Tree { get; set; } = "";
        public List<string> Paths { get; set; } = new();
    }

    internal static async Task BuildSkeletonTree(StringBuilder sb, List<string> paths, string currentDir, string prefix,
        HashSet<string> excludeDirs, HashSet<string> excludeExtensions,
        HashSet<string> gitignoreDirs, HashSet<string> gitignoreExts,
        string projectRoot, MutableInt omittedCount)
    {
        var entries = new List<(bool isDir, string name, string fullPath)>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(currentDir))
            {
                var name = Path.GetFileName(entry);
                var isDir = Directory.Exists(entry);
                var ext = Path.GetExtension(name);
                if (isDir && excludeDirs.Contains(name))
                {
                    if (gitignoreDirs.Contains(name)) omittedCount.Value++;
                    continue;
                }
                if (!isDir && excludeExtensions.Contains(ext))
                {
                    if (gitignoreExts.Contains(ext)) omittedCount.Value++;
                    continue;
                }
                entries.Add((isDir, name, entry));
            }
        }
        catch { return; }
        entries.Sort((a, b) =>
        {
            if (a.isDir != b.isDir) return a.isDir ? -1 : 1;
            return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
        });
        for (var i = 0; i < entries.Count; i++)
        {
            var (isDir, name, fullPath) = entries[i];
            if (!isDir)
                paths.Add(Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/'));
            var isLast = i == entries.Count - 1;
            var connector = isLast ? "└── " : "├── ";
            sb.Append(prefix).Append(connector).AppendLine(name);
            if (isDir)
            {
                var childPrefix = prefix + (isLast ? "    " : "│   ");
                await BuildSkeletonTree(sb, paths, fullPath, childPrefix, excludeDirs, excludeExtensions, gitignoreDirs, gitignoreExts, projectRoot, omittedCount);
            }
        }
    }

    internal static bool TryNormalizeSkeletonSignature(string line, out string signature)
    {
        signature = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var l = line.Trim();
        if (l.StartsWith("["))
        {
            var am = Regex.Match(l, @"^\s*\[\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase);
            if (am.Success)
            {
                signature = am.Groups[1].Value + " { ... }";
                return true;
            }
            return false;
        }
        // Ignore single-line comments
        if (l.StartsWith("//") || l.StartsWith("/*")) return false;
        // C# class/struct/record/interface
        var csClass = Regex.Match(l, @"^\s*(public|internal|protected|private)?\s*(?:sealed|partial|static|abstract|unsafe|readonly)?\s*(class|struct|record|interface)\s+([A-Za-z_][\w<>]*)\s*(?:[:\{].*)?$", RegexOptions.IgnoreCase);
        if (csClass.Success)
        {
            var mods = csClass.Groups[1].Value;
            var kind = csClass.Groups[2].Value;
            var name = csClass.Groups[3].Value;
            signature = (mods + " " + kind + " " + name).Trim() + " { ... }";
            return true;
        }
        var csMethod = Regex.Match(l, @"^\s*(?!(?:func\b|pub\s+fn\b))(public|private|protected|internal)?\s*(?:static|async|virtual|override|extern|unsafe|sealed|partial)?\s*([\w<>,\s\[\]]+?)\s+([A-Za-z_][\w]*)\s*\([^\)]*\)\s*(?:\{|$)", RegexOptions.IgnoreCase);
        if (csMethod.Success)
        {
            var mods = csMethod.Groups[1].Value;
            var ret = csMethod.Groups[2].Value.Trim();
            var name = csMethod.Groups[3].Value;
            var hasAsync = Regex.IsMatch(l, @"\basync\b", RegexOptions.IgnoreCase);
            string head;
            if (!string.IsNullOrWhiteSpace(mods))
                head = mods + (hasAsync ? " async " : " ") + ret + " " + name;
            else
                head = (hasAsync ? "async " : "") + ret + " " + name;
            head = Regex.Replace(head, @"\t+|\s{2,}", " ").Trim();
            signature = head + "() { ... }";
            return true;
        }
        var tsDecl = Regex.Match(l, @"^\s*(export\s+)?(interface|class)\s+([A-Za-z_][\w]*)", RegexOptions.IgnoreCase);
        if (tsDecl.Success)
        {
            var expo = tsDecl.Groups[1].Value;
            var kind = tsDecl.Groups[2].Value;
            var name = tsDecl.Groups[3].Value;
            signature = (expo + kind + " " + name).Trim() + " { ... }";
            return true;
        }
        var tsMethod = Regex.Match(l, @"^\s*(async\s+)?([A-Za-z_][\w]*)\s*\([^\)]*\)\s*(:\s*[\w<>,\s\[\]]+)?\s*\{?", RegexOptions.IgnoreCase);
        if (tsMethod.Success)
        {
            var async = tsMethod.Groups[1].Value;
            var name = tsMethod.Groups[2].Value;
            signature = (async + name).Trim() + "() { ... }";
            return true;
        }
        var pyDef = Regex.Match(l, @"^\s*def\s+([A-Za-z_][\w]*)\s*\([^\)]*\)\s*:\s*$", RegexOptions.IgnoreCase);
        if (pyDef.Success)
        {
            signature = $"def {pyDef.Groups[1].Value}() {{ ... }}";
            return true;
        }
        var pyClass = Regex.Match(l, @"^\s*class\s+([A-Za-z_][\w]*)(?:\([^\)]*\))?\s*:\s*$", RegexOptions.IgnoreCase);
        if (pyClass.Success)
        {
            signature = $"class {pyClass.Groups[1].Value}() {{ ... }}";
            return true;
        }
        var goFunc = Regex.Match(l, @"^\s*func\s*(?:\(([^\)]*)\)\s*)?([A-Za-z_][\w]*)\s*\(", RegexOptions.IgnoreCase);
        if (goFunc.Success)
        {
            var recv = goFunc.Groups[1].Value?.Trim();
            var name = goFunc.Groups[2].Value;
            signature = (string.IsNullOrEmpty(recv) ? $"func {name}" : $"func ({recv}) {name}") + "() { ... }";
            return true;
        }
        var rustFn = Regex.Match(l, @"^\s*(pub\s+)?fn\s+([A-Za-z_][\w]*)\s*\(", RegexOptions.IgnoreCase);
        if (rustFn.Success)
        {
            var pub = rustFn.Groups[1].Value;
            var name = rustFn.Groups[2].Value;
            signature = (pub + "fn " + name).Trim() + "() { ... }";
            return true;
        }
        var funcLike = Regex.Match(l, @"^\s*([A-Za-z_][\w]*)\s*\([^\)]*\)\s*\{?\s*$");
        if (funcLike.Success)
        {
            var name = funcLike.Groups[1].Value;
            if (string.Equals(name, "func", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "pub", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "export", StringComparison.OrdinalIgnoreCase))
                return false;
            signature = name + "() { ... }";
            return true;
        }
        return false;
    }

    public static bool NormalizeSkeletonSignatureForTest(string line, out string signature) => TryNormalizeSkeletonSignature(line, out signature);

    public static string GetSkeletonForRange(string[] allLines, int start, int end)
    {
        if (start >= end) return "";
        var skeleton = new StringBuilder();
        int omittedCount = 0;
        for (int i = start; i < end; i++)
        {
            var line = allLines[i];
            if (TryNormalizeSkeletonSignature(line, out var normalized))
            {
                if (omittedCount > 0)
                {
                    skeleton.AppendLine($"... [{omittedCount} lines omitted]");
                    omittedCount = 0;
                }
                skeleton.AppendLine(normalized);
            }
            else
            {
                omittedCount++;
            }
        }
        if (omittedCount > 0) skeleton.AppendLine($"... [{omittedCount} lines omitted]");
        return skeleton.ToString();
    }
}
