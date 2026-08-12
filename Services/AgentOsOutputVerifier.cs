using System.Text;
using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Deterministic verification for tasks that demand a file be written to the OS
/// filesystem OUTSIDE the repository ("write the data into a text file on my
/// desktop"). This is the guard for the "confident wrong answer" class of
/// completion: a run that fetches web data and then declares
/// <c>planComplete=true</c> — with a reason like "Now I need only create the final
/// output file" — without ever writing the file. No LLM is consulted anywhere in
/// this class; everything is a pure function of the task text and the run's
/// executed results, so the findings are always CONFIRMED.
/// </summary>
/// <remarks>
/// <para>
/// Detection discipline mirrors <c>AgentController.IsExternalFilesystemTask</c>
/// (OS-location word + filesystem artifact + repo-context escape hatch): a demand
/// fires only when the task has a WRITE verb (write/save/dump/export/store/put/log),
/// a FILE artifact ("text file", " file", ".txt", "data", "results", …) AND an OS
/// location ("my desktop", "downloads folder", an absolute path, a ~ path). A repo
/// mention anywhere in the prompt escapes — "save to the project's desktop folder"
/// is repo work, never an OS write.
/// </para>
/// <para>
/// When a demand exists and the run finished without writing the file, the
/// harvested web results (the content the run gathered but never dumped) are
/// written straight to the resolved target path — a deterministic server-side
/// finalization that needs no LLM planning round. If there is nothing to dump,
/// <see cref="CheckOsOutputWritten"/> returns a CONFIRMED issue so the repair loop
/// steers the planner to write the file.
/// </para>
/// </remarks>
public static class AgentOsOutputVerifier
{
    /// <summary>The file name used when the task demands "a file on the desktop" without naming it.</summary>
    public const string DefaultDumpFileName = "ai_article_data.txt";

    private const int MaxSectionChars = 20000;
    private const int MaxTotalChars = 100000;

    public sealed record OsOutputDemand(string LocationKind, string DirectoryPath, string? FileNameHint);

    // Kept in sync with AgentController.IsExternalFilesystemTask — a demand must never
    // fire on a task that references the repo ("fix the bug in the desktop folder").
    private static readonly string[] RepoContexts =
    {
        "in the repo", "in the repository", "in the repo's", "of the repo", "the repo's",
        "in the project", "of the project", "the project's", "at the project root",
        "in the codebase", "in src", "under src", "src/"
    };

    // NOTE: never list bare "desktop"/"the desk" — "the desk" is a prefix of
    // "the desktop", so "fix the bug in the desktop folder" would falsely fire.
    private static readonly string[] OsLocations =
    {
        "on the desk", "my desk", "my desktop", "on the desktop",
        "to the desktop", "from the desktop", "downloads folder", "download folder",
        "my downloads", "to my downloads", "documents folder", "document folder",
        "my documents", "in documents", "to documents", "in downloads", "to downloads",
        "home directory", "home folder", "user folder", "temp folder", "tmp folder",
        "recycle bin", "program files", "appdata", "startup folder", "pictures folder",
        "music folder", "videos folder", "screenshots folder"
    };

    private static readonly string[] WriteVerbs =
        { "write", "save", "dump", "export", "store", "put", "log" };

    private static readonly string[] FileArtifacts =
    {
        "text file", "txt file", " file", ".txt", ".md", ".csv", ".json", ".xml", ".html",
        " data", " results", " summary", " output", " article", " content", " report"
    };

    /// <summary>
    /// Deterministically detects whether the task demands a file be written to the OS
    /// filesystem (desktop/downloads/documents/home or an absolute/~/path). Fires only
    /// when a write verb AND a file artifact AND an OS location all appear, and never
    /// when the task references the repo.
    /// </summary>
    public static bool TryGetOsFileOutputDemand(string? prompt, out OsOutputDemand demand)
    {
        demand = new OsOutputDemand("", "", null);
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var lower = prompt.ToLowerInvariant();

        foreach (var ctx in RepoContexts)
            if (lower.Contains(ctx)) return false;

        var hasWriteVerb = false;
        foreach (var v in WriteVerbs)
        {
            if (Regex.IsMatch(lower, $@"\b{v}\w*\b")) { hasWriteVerb = true; break; }
        }
        if (!hasWriteVerb) return false;

        // Absolute / ~ / home path wins over named locations — it pins the exact directory
        // (and usually the file name). A quoted path may contain spaces; an unquoted one may not.
        var absPath = ExtractAbsolutePath(prompt);
        if (absPath != null)
        {
            var full = ExpandTilde(absPath).TrimEnd('/', '\\');
            var isFile = LooksLikeFilePath(full);
            var dir = isFile ? (Path.GetDirectoryName(full) ?? Path.GetPathRoot(full) ?? "") : full;
            if (string.IsNullOrWhiteSpace(dir)) return false;
            demand = new OsOutputDemand("absolute", dir, isFile ? Path.GetFileName(full) : null);
            return true;
        }

        var hasArtifact = false;
        foreach (var a in FileArtifacts)
        {
            if (lower.Contains(a)) { hasArtifact = true; break; }
        }
        if (!hasArtifact) return false;

        string? locationKind = null;
        foreach (var loc in OsLocations)
        {
            if (lower.Contains(loc)) { locationKind = loc; break; }
        }
        if (locationKind == null) return false;

        var dir2 = ResolveOsLocation(locationKind);
        if (string.IsNullOrWhiteSpace(dir2)) return false;
        demand = new OsOutputDemand(locationKind, dir2, ExtractFileNameHint(prompt));
        return true;
    }

    /// <summary>
    /// True when the run plausibly wrote the demanded output: a successful _command step
    /// whose command text references the target directory, or the target file existing on
    /// disk (task-named file, else <see cref="DefaultDumpFileName"/>).
    /// </summary>
    public static bool IsOsOutputWritten(OsOutputDemand demand, IEnumerable<Dictionary<string, object?>> results)
    {
        if (string.IsNullOrWhiteSpace(demand.DirectoryPath)) return false;
        string dir;
        try { dir = Path.GetFullPath(demand.DirectoryPath); }
        catch { return false; }
        var normDir = dir.Replace('\\', '/');
        foreach (var r in results)
        {
            if (r.GetValueOrDefault("type")?.ToString() is not ("command" or "_command")) continue;
            if (r.GetValueOrDefault("status")?.ToString() != "done") continue;
            var cmd = r.GetValueOrDefault("command")?.ToString() ?? r.GetValueOrDefault("change")?.ToString() ?? "";
            if (cmd.Length > 0 && cmd.Replace('\\', '/').Contains(normDir, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var fileName = string.IsNullOrWhiteSpace(demand.FileNameHint) ? DefaultDumpFileName : demand.FileNameHint!;
        try { return System.IO.File.Exists(Path.Combine(dir, fileName)); }
        catch { return false; }
    }

    /// <summary>
    /// The deterministic CONFIRMED issue for a run that ended without writing a demanded
    /// OS output file — or null when there is no demand or the file was written. Used by
    /// PostExecuteVerify so a falsely-declared completion drives the repair loop instead
    /// of silently passing.
    /// </summary>
    public static string? CheckOsOutputWritten(string? prompt, IEnumerable<Dictionary<string, object?>> results)
    {
        if (!TryGetOsFileOutputDemand(prompt, out var demand)) return null;
        if (IsOsOutputWritten(demand, results)) return null;
        var where = string.IsNullOrWhiteSpace(demand.FileNameHint)
            ? demand.DirectoryPath
            : Path.Combine(demand.DirectoryPath, demand.FileNameHint!);
        return $"The task asked to write a file to \"{where}\" on the OS filesystem (outside the repo), " +
               "but the run ended without creating it — no _command step wrote there and the file does not exist. " +
               "The requested file was never created, so the task is not complete.";
    }

    /// <summary>
    /// Writes the run's harvested web results (search + fetch outputs, in order, capped)
    /// to the demanded target path. Returns the written path on success. The file gets a
    /// small header (task + timestamp) so the dump stands alone. Pure function of the
    /// results — no LLM, no planning round.
    /// </summary>
    public static (bool dumped, string? path, string? error) TryAutoDumpWebResults(
        string prompt, OsOutputDemand demand, IEnumerable<Dictionary<string, object?>> results)
    {
        if (string.IsNullOrWhiteSpace(demand.DirectoryPath))
            return (false, null, "no resolvable target directory");
        var sections = new StringBuilder();
        var total = 0;
        foreach (var r in results)
        {
            var type = r.GetValueOrDefault("type")?.ToString();
            if (type is not ("_web_search" or "_web_fetch")) continue;
            if (r.GetValueOrDefault("status")?.ToString() != "done") continue;
            var label = r.GetValueOrDefault("query")?.ToString() ?? r.GetValueOrDefault("url")?.ToString() ?? type;
            var output = r.GetValueOrDefault("output")?.ToString();
            if (string.IsNullOrWhiteSpace(output) || output.Length <= 80) continue;
            var capped = output.Length > MaxSectionChars
                ? output[..MaxSectionChars] + "\n… [section truncated]"
                : output;
            if (total + capped.Length > MaxTotalChars)
            {
                var remaining = Math.Max(0, MaxTotalChars - total);
                if (remaining <= 0) break;
                capped = capped[..remaining] + "\n… [dump truncated]";
            }
            sections.Append("\n### WEB RESULTS [").Append(label).Append("] ###\n").Append(capped).Append('\n');
            total += capped.Length;
            if (total >= MaxTotalChars) break;
        }
        if (total == 0) return (false, null, "no web results available to dump");
        var fileName = string.IsNullOrWhiteSpace(demand.FileNameHint) ? DefaultDumpFileName : demand.FileNameHint!;
        string target;
        try { target = Path.Combine(demand.DirectoryPath, fileName); }
        catch (Exception ex) { return (false, null, $"invalid target path: {ex.Message}"); }
        try
        {
            var header = $"# Weaver web results\nTask: {prompt}\nGenerated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
            System.IO.File.WriteAllText(target, header + sections, Encoding.UTF8);
            return (true, target, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"failed to write {target}: {ex.Message}");
        }
    }

    private static string? ExtractAbsolutePath(string prompt)
    {
        var quoted = Regex.Match(prompt,
            @"[""']([a-zA-Z]:[\\/][^""']+|~/[^""']+|/home/[^""']+|/Users/[^""']+)[""']");
        if (quoted.Success) return quoted.Groups[1].Value.Trim();
        var bare = Regex.Match(prompt,
            @"([a-zA-Z]:[\\/][^\s""';]+|~/[^\s""';]+|/home/[^\s""';]+|/Users/[^\s""';]+)");
        return bare.Success ? bare.Groups[1].Value.Trim() : null;
    }

    private static bool LooksLikeFilePath(string path)
    {
        return Path.HasExtension(path) &&
               Path.GetFileName(path).IndexOfAny(new[] { '*', '?', '<', '>', '|' }) < 0;
    }

    private static string ExpandTilde(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home)) return Path.Combine(home, path[2..]);
        }
        return path;
    }

    private static string ResolveOsLocation(string locationKind)
    {
        var lower = locationKind.ToLowerInvariant();
        if (lower.Contains("desk")) return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (lower.Contains("document")) return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (lower.Contains("download")) return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (lower.Contains("home") || lower.Contains("user folder"))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (lower.Contains("picture")) return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (lower.Contains("music")) return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (lower.Contains("video")) return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (lower.Contains("temp") || lower.Contains("tmp")) return Path.GetTempPath().TrimEnd('\\', '/');
        return "";
    }

    private static string? ExtractFileNameHint(string prompt)
    {
        var m = Regex.Match(prompt, @"[""']?([A-Za-z0-9][A-Za-z0-9 _.\-]{0,80}\.(txt|md|csv|json|html|xml|log))[""']?",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
