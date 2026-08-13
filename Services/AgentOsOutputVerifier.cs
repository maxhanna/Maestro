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
/// (OS-location word + filesystem artifact + repo-context escape hatch): a demand    /// fires only when the task has a WRITE verb (write/save/dump/export/store/put/log/create/append/insert/add),
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
        { "write", "save", "dump", "export", "store", "put", "log", "create", "append", "insert", "add" };

    private static readonly string[] FileArtifacts =
    {
        "text file", "txt file", " file", ".txt", ".md", ".csv", ".json", ".xml", ".html",
        " data", " results", " summary", " output", " article", " content", " report"
    };

    /// <summary>
    /// Deterministically detects whether the task demands a file be written to the OS
    /// filesystem (desktop/downloads/documents/home or an absolute/~/path). Fires only
    /// when a write verb (write/save/dump/export/store/put/log/create/append/insert/add) AND a file artifact
    /// AND an OS location all appear, and never
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
            // Parse the parent directory and file name using whichever separator the
            // prompt actually used. Never Path.GetDirectoryName here: it is
            // platform-dependent — a Windows-style C:\ path in a prompt yields an
            // empty directory when running on Linux because \ is not a separator.
            string dir;
            string? fileName = null;
            if (isFile)
            {
                var sep = full.Contains('\\') ? '\\' : (full.Contains('/') ? '/' : Path.DirectorySeparatorChar);
                var idx = full.LastIndexOf(sep);
                if (idx > 0)
                {
                    dir = full[..idx];
                    fileName = full[(idx + 1)..];
                }
                else if (idx == 0)
                {
                    dir = "/";
                    fileName = full[1..];
                }
                else
                {
                    dir = full;
                }
            }
            else
            {
                dir = full;
            }
            if (string.IsNullOrWhiteSpace(dir)) return false;
            demand = new OsOutputDemand("absolute", dir, fileName);
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

        // Detection is platform-independent: write verb + file artifact + OS location is a
        // demand even when the special folder can't be resolved on this platform (headless
        // Linux runners have no Desktop/Documents folder — GetFolderPath returns "").
        // Callers treat an empty directory as unresolvable but still report/steer.
        var dir2 = ResolveOsLocation(locationKind);
        demand = new OsOutputDemand(locationKind, dir2, ExtractFileNameHint(prompt));
        return true;
    }

    /// <summary>
    /// True when the run plausibly wrote the demanded output: the demanded file exists on
    /// disk with meaningful content (task-named file, else <see cref="DefaultDumpFileName"/>).
    /// When the OS location cannot be resolved (headless runners have no Desktop/Documents
    /// folder), a successful _command step whose text references the target file is the only
    /// evidence available and counts as written.
    /// </summary>
    public static bool IsOsOutputWritten(OsOutputDemand demand, IEnumerable<Dictionary<string, object?>> results)
    {
        var fileName = string.IsNullOrWhiteSpace(demand.FileNameHint) ? DefaultDumpFileName : demand.FileNameHint!;
        var normDir = "";
        if (!string.IsNullOrWhiteSpace(demand.DirectoryPath))
        {
            try { normDir = Path.GetFullPath(demand.DirectoryPath).Replace('\\', '/'); }
            catch { normDir = ""; }
        }
        // PRIMARY: whenever the target directory resolves, the demanded file itself must
        // exist with meaningful content. A done _command that merely MENTIONS the directory
        // — or that wrote a differently-named file — is not proof of the write: a failed
        // fetch can save an empty PowerShell object rendering ("@{title=; summary=}") or the
        // agent can pick its own file name, and neither satisfies "write the data into a
        // text file on my desktop".
        if (normDir.Length > 0)
        {
            try
            {
                var targetPath = Path.Combine(demand.DirectoryPath, fileName);
                if (System.IO.File.Exists(targetPath))
                    return HasMeaningfulContent(targetPath);
            }
            catch { }
            return false;
        }
        // HEADLESS FALLBACK: on platforms where the OS folder cannot be resolved there is no
        // file to inspect, so a done command naming the target file is the only evidence.
        foreach (var r in results)
        {
            if (r.GetValueOrDefault("type")?.ToString() is not ("command" or "_command")) continue;
            if (r.GetValueOrDefault("status")?.ToString() != "done") continue;
            var cmd = r.GetValueOrDefault("command")?.ToString() ?? r.GetValueOrDefault("change")?.ToString() ?? "";
            if (cmd.Length == 0) continue;
            if (fileName.Length > 0 && cmd.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the file exists with real, non-trivial content. Rejects an empty file and
    /// the "hollow object rendering" a failed PowerShell fetch produces — Select-Object on an
    /// HTML response stringifies as "@{title=; summary=; publishedDate=}" (every property
    /// empty) and Set-Content happily saves that. Such a file is evidence of a failed fetch,
    /// not of the demanded data being written.
    /// </summary>
    private static bool HasMeaningfulContent(string path)
    {
        string content;
        try { content = System.IO.File.ReadAllText(path); }
        catch { return false; }
        if (string.IsNullOrWhiteSpace(content)) return false;
        return !IsHollowObjectRendering(content);
    }

    /// <summary>
    /// True when the entire content is an "@{key=; key=}" rendering in which every property
    /// value is empty — the signature of a failed fetch (Select-Object of an HTML response)
    /// rather than real data.
    /// </summary>
    private static bool IsHollowObjectRendering(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("@{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
            return false;
        var inner = trimmed[2..^1];
        if (string.IsNullOrWhiteSpace(inner)) return true;
        foreach (var part in inner.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var value = eq >= 0 ? part[(eq + 1)..] : part;
            if (!string.IsNullOrWhiteSpace(value)) return false;
        }
        return true;
    }

    /// <summary>
    /// Resolves where a web step's task wants its output file, for the eager dump's
    /// destination preparation. OS demands (absolute / ~ / named desktop-downloads paths)
    /// are resolved exactly as <see cref="TryGetOsFileOutputDemand"/> and win; otherwise a
    /// REPO-RELATIVE demand ("create a folder called benchmark_test_16 … a file called
    /// pokemon_data.csv at the project root") resolves under <paramref name="projectRoot"/>
    /// — the benchmark-task failure mode where the fetch must land in a folder that does
    /// not exist yet. Returns false when the task has no write-verb + file-artifact demand
    /// either way.
    /// </summary>
    public static bool TryGetFileOutputTarget(string prompt, string projectRoot, out OsOutputDemand demand)
    {
        demand = new OsOutputDemand("", "", null);
        if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(projectRoot))
            return false;
        if (TryGetOsFileOutputDemand(prompt, out var osDemand))
        {
            demand = osDemand;
            return true;
        }
        var lower = prompt.ToLowerInvariant();
        var hasWriteVerb = false;
        foreach (var v in WriteVerbs)
            if (Regex.IsMatch(lower, $@"\b{v}\w*\b")) { hasWriteVerb = true; break; }
        if (!hasWriteVerb) return false;
        var hasArtifact = false;
        foreach (var a in FileArtifacts)
            if (lower.Contains(a)) { hasArtifact = true; break; }
        if (!hasArtifact) return false;
        // Prefer an explicit relative path ("benchmark_test_16/pokemon_data.csv"); otherwise a
        // bare file name may be scoped into a folder the task names ("create a folder called
        // X … a file called Y.csv"). Falls back to the file at the project root.
        var rel = ExtractRelativePath(prompt);
        if (string.IsNullOrWhiteSpace(rel))
        {
            // A tight bare-filename token (no spaces — a real file name), NOT the OS hint
            // extractor: "create a file called pokemon_data.csv" must yield "pokemon_data.csv",
            // never the whole phrase.
            var bareFile = Regex.Match(prompt, @"\b([A-Za-z0-9_\-]+\.[A-Za-z0-9]{1,5})\b");
            if (!bareFile.Success) return false;
            var hint = bareFile.Groups[1].Value;
            var folder = Regex.Match(lower,
                @"(?:folder|directory)\s+(?:called\s+|named\s+)?['""']?([a-z0-9_\-]+)['""']?",
                RegexOptions.IgnoreCase);
            rel = folder.Success ? folder.Groups[1].Value + "/" + hint : hint;
        }
        if (!LooksLikeFilePath(rel)) return false;
        string full;
        try { full = Path.GetFullPath(Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return false; }
        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(dir)) return false;
        demand = new OsOutputDemand("repo", dir, Path.GetFileName(full));
        return true;
    }

    /// <summary>
    /// Creates the demanded dump destination's parent directory when it does not exist
    /// (idempotent) so a web step's eager dump — and any later write step — can never fail
    /// on "directory not found". Returns the directory, or null when the demand has no
    /// resolvable directory or creation fails.
    /// </summary>
    public static string? PrepareDumpDirectory(OsOutputDemand demand)
    {
        if (string.IsNullOrWhiteSpace(demand.DirectoryPath)) return null;
        try
        {
            System.IO.Directory.CreateDirectory(demand.DirectoryPath);
            return demand.DirectoryPath;
        }
        catch { return null; }
    }

    /// <summary>A relative path with at least one separator and a file extension
    /// ("benchmark_test_16/pokemon_data.csv", 'data/notes.md') — quoted or bare.</summary>
    private static string? ExtractRelativePath(string prompt)
    {
        var quoted = Regex.Match(prompt,
            @"[""']((?:[A-Za-z0-9_\-]+[\\/])+[A-Za-z0-9_\-]+\.[A-Za-z0-9]{1,5})[""']");
        if (quoted.Success) return quoted.Groups[1].Value.Trim();
        var bare = Regex.Match(prompt,
            @"\b([A-Za-z0-9_\-]+[\\/][A-Za-z0-9_\-]+\.[A-Za-z0-9]{1,5})\b");
        return bare.Success ? bare.Groups[1].Value.Trim() : null;
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
               "but the run did not deliver it — the demanded file was never created (it does not exist), " +
               "or it exists with empty/hollow content (e.g. a failed fetch stringified as '@{title=; summary=}'). " +
               "The task is not complete.";
    }

    /// <summary>
    /// Writes the run's harvested web results (search + fetch outputs, in order, capped)
    /// to the demanded target path. Returns the written path on success. Creates the
    /// parent directory when needed (the task may name an arbitrary path); when the
    /// target file already exists the fresh sections are APPENDED so existing content is
    /// never clobbered, otherwise a small header (task + timestamp) leads the file so the
    /// dump stands alone. Pure function of the results — no LLM, no planning round.
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
            // CREATE the parent directory when needed — the task may name an arbitrary
            // path ("~/scratch/pokemon_data.csv") whose folders do not exist yet, and
            // File.WriteAllText would fail without them.
            var parentDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parentDir))
                System.IO.Directory.CreateDirectory(parentDir);
            if (System.IO.File.Exists(target))
            {
                // INSERT rather than clobber: append the fresh web-results sections to
                // whatever is already there, delimited, so an existing file (a notes doc,
                // a CSV that already has its header row, an earlier dump) keeps its
                // content instead of being destroyed by the next fetch.
                System.IO.File.AppendAllText(target, "\n" + sections, Encoding.UTF8);
            }
            else
            {
                var header = $"# Weaver web results\nTask: {prompt}\nGenerated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
                System.IO.File.WriteAllText(target, header + sections, Encoding.UTF8);
            }
            return (true, target, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"failed to write {target}: {ex.Message}");
        }
    }

    private static string? ExtractAbsolutePath(string prompt)
    {
        // Windows drive paths (C:\...) and any Unix absolute path (/tmp/..., /home/...).
        var quoted = Regex.Match(prompt,
            @"[""']((?:[a-zA-Z]:[\\/]|~/|/)[^""']+)[""']");
        if (quoted.Success)
        {
            var p = quoted.Groups[1].Value.Trim();
            return LooksLikePathRoot(p) ? p : null;
        }
        var bare = Regex.Match(prompt,
            @"((?:[a-zA-Z]:[\\/]|~/|/)[^\s""';]+)");
        if (!bare.Success) return null;
        var b = bare.Groups[1].Value.Trim();
        return LooksLikePathRoot(b) ? b : null;
    }

    /// <summary>True when the candidate path has a directory separator beyond the root
    /// marker ("~/x", "/tmp/x", "C:\x\y"), so a lone "/word" phrase is not a path.</summary>
    private static bool LooksLikePathRoot(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal)) return true;
        var rest = path.StartsWith('/')
            ? path[1..]
            : (path.Length > 2 ? path[2..] : "");
        return rest.Contains('\\') || rest.Contains('/');
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
