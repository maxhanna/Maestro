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
public static class AgentProjectUtilities
{
    public static readonly HashSet<string> notTableWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "or", "not", "in", "on", "at", "to", "for", "of", "by",
        "as", "is", "it", "an", "be", "has", "have", "are", "was", "were",
        "from", "into", "with", "without", "using", "where", "when", "while",
        "then", "than", "this", "that", "these", "those", "each", "all",
        "both", "between", "after", "before", "above", "below", "under",
        "over", "through", "during", "until", "since", "within", "about",
        "join", "inner", "outer", "left", "right", "full", "cross", "natural",
        "order", "group", "having", "limit", "offset", "set", "values",
        "select", "insert", "update", "delete", "create", "alter", "drop",
        "true", "false", "null", "default", "unique", "index", "key",
        "primary", "foreign", "check", "cascade", "restrict", "action",
        "count", "sum", "avg", "min", "max", "distinct", "exists", "case",
        "when", "else", "then", "end", "cast", "convert", "coalesce",
        "nullif", "date", "time", "timestamp", "year", "month", "day",
        "hour", "minute", "second", "now", "utc_timestamp",
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint",
        "decimal", "numeric", "float", "double", "real", "bit", "boolean",
        "char", "varchar", "nvarchar", "text", "blob", "binary", "varbinary",
        "enum", "set", "json", "geometry", "point", "linestring", "polygon",
        "return", "returns", "declare", "begin", "end", "if", "else",
        "iterate", "leave", "loop", "repeat", "while", "signal", "resignal",
        "cursor", "handler", "continue", "exit", "undo", "condition",
        "open", "close", "fetch", "into", "call", "rename", "truncate",
        "start", "stop", "commit", "rollback", "savepoint", "release",
        "lock", "unlock", "grant", "revoke", "analyze", "optimize",
        "reorganize", "repair", "check", "checksum", "backup", "restore",
        "utf8", "utf8mb4", "ascii", "latin1", "unicode", "?",
        "auto_increment", "unsigned", "signed", "zerofill",
        "current_timestamp", "current_date", "current_time", "localtime",
        "localtimestamp"
    };

    public static HashSet<string> ExtractSqlTableNames(string source)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "the", "and", "or", "not", "in", "on", "at", "to", "for", "of", "by",
            "as", "is", "it", "from", "join", "inner", "outer", "left", "right",
            "where", "set", "values", "select", "insert", "update", "delete",
            "order", "group", "having", "limit", "offset", "true", "false", "null",
            "count", "sum", "avg", "min", "max", "distinct",
            "date", "time", "year", "month", "day", "hour", "minute", "second",
            "now", "between", "like", "exists", "case", "when", "then", "else", "end",
            "return", "returns", "declare", "begin", "if", "else",
            "start", "stop", "commit", "rollback", "savepoint",
            "int", "integer", "bigint", "smallint", "tinyint",
            "decimal", "numeric", "float", "double", "real",
            "char", "varchar", "text", "blob", "binary",
            "enum", "set", "json", "boolean", "bit",
            "default", "unique", "index", "key", "primary", "foreign",
            "cascade", "restrict", "action", "check",
            "auto_increment", "unsigned", "signed", "zerofill",
            "character", "collate", "charset", "engine", "row_format"
        };
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match sm in Regex.Matches(source,
            @"@?""(?:[^""\\]*(?:\\.[^""\\]*)*)""", RegexOptions.Singleline))
        {
            var val = sm.Value;
            if (!Regex.IsMatch(val, @"\b(SELECT|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase))
                continue;
            foreach (Match m in Regex.Matches(val,
                @"(?:FROM|JOIN|INTO|UPDATE|TABLE(?:\s+IF\s+NOT\s+EXISTS)?)\s+`?(\w+(?:\.\w+)?)`?",
                RegexOptions.IgnoreCase))
            {
                var tbl = m.Groups[1].Value;
                if (tbl.Contains('.')) tbl = tbl.Split('.')[^1];
                if (tbl.Length > 2 && !skip.Contains(tbl) && !char.IsDigit(tbl[0]))
                    tables.Add(tbl);
            }
        }
        return tables;
    }

    public static bool IsSpecialMarker(string? file) => file != null && (
        file.Equals("_git", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_rename", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_delete_file", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_show", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_display", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_ping", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_package_install", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_create_file", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_create_directory", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_sql_migration", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_command", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_browser_test", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_explore", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_discover", StringComparison.OrdinalIgnoreCase) ||
        file.Equals("_scraper", StringComparison.OrdinalIgnoreCase));

    public static bool IsPathUnderRoot(string fullPath, string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        fullPath = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return true;
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Path.IsPathRooted(path)) return false;
        var specialMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_git", "_ping", "_show", "_display", "_create_file", "_create_directory",
            "_package_install", "_command", "_web_search", "_web_fetch", "_explore", "_discover",
            "_rename", "_rename_file", "_move_file", "_delete_file", "_continue",
            "_scraper", "_browser_test"
        };
        return !specialMarkers.Contains(path);
    }

    public static bool LooksLikeShellCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var trimmed = command.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal)) return false;
        if (Regex.IsMatch(trimmed, @"^\s*(create|add|modify|update|edit|implement|explore|examine|inspect|review|understand|read)\s+(?:a|an|the|basic|template|file|component|method|structure)\b",
                RegexOptions.IgnoreCase))
            return false;

        var firstSegment = Regex.Split(trimmed, @"\s*(?:;|&&|\|\|)\s*")
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? "";
        if (firstSegment.Length == 0) return false;
        var firstToken = Regex.Match(firstSegment, @"^(?:&\s*)?(?:\.\\|\.\/)?[A-Za-z0-9_.:\\/\-]+").Value;
        if (string.IsNullOrWhiteSpace(firstToken)) return false;
        var exe = Path.GetFileName(firstToken).ToLowerInvariant();

        var knownCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cd", "dir", "ls", "pwd", "echo", "type", "copy", "xcopy", "robocopy",
            // Directory creation/removal — real terminal commands (the OS-task discovery
            // section and the docs teach mkdir/New-Item for folder creation; a planned
            // mkdir must pass the shell-command gate or it is rejected as "not an
            // executable shell command" after the web-prep exemption already let it through).
            "mkdir", "md", "rmdir", "rd",
            "dotnet", "npm", "npx", "pnpm", "yarn", "node", "python", "py",
            "git", "gh", "docker", "docker-compose", "kubectl", "ng", "vite",
            "tsc", "eslint", "prettier", "jest", "vitest", "playwright",
            "rg", "grep", "findstr", "curl", "wget", "powershell", "pwsh",
            "cmd", "msbuild", "nuget", "Invoke-RestMethod", "Invoke-WebRequest",
            "Get-ChildItem", "Get-Content", "Set-Location", "New-Item", "Remove-Item",
            "Move-Item", "Copy-Item", "Start-Process",
            // PowerShell file-write cmdlets — the exact commands the OS-filesystem discovery
            // section and the web-chain example TEACH for writing results to the desktop
            // ("… | Set-Content -Path \"<desktop-path>\file.txt\" -Encoding UTF8"). They
            // must pass the shell-command gate or a planned desktop write is rejected.
            "Set-Content", "Add-Content", "Out-File", "Export-Csv", "ConvertTo-Json",
            "Write-Output", "Test-Path", "Join-Path", "Split-Path", "Select-Object",
            "ForEach-Object", "Where-Object", "Measure-Object"
        };

        return knownCommands.Contains(exe) ||
               exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
               exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
               exe.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
               firstToken.StartsWith(".\\", StringComparison.Ordinal) ||
               firstToken.StartsWith("./", StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips a single matching pair of wrapping quotes/backticks (a model wrapping a whole
    /// command in "…", '…' or `…`), but NEVER an unmatched trailing quote — so a command that
    /// legitimately ends in a quoted path ("echo … &gt; \"/tmp/x/report.txt\"") keeps its closing
    /// quote. The old Trim('`', '"', '\'') ate the trailing quote of exactly such commands,
    /// leaving an unterminated quote that hung bash (the steered desktop-write CI failure).
    /// </summary>
    public static string UnwrapWrappingQuotes(string text)
    {
        var t = text.Trim();
        if (t.Length >= 2 &&
            ((t[0] == '"' && t[^1] == '"') ||
             (t[0] == '\'' && t[^1] == '\'') ||
             (t[0] == '`' && t[^1] == '`')))
        {
            return t[1..^1];
        }
        return t;
    }

    /// <summary>
    /// True when a _command step's text looks like it FETCHES CONTENT from an http(s)
    /// URL with a download tool (curl/wget/Invoke-RestMethod/Invoke-WebRequest,
    /// python urllib/requests, .NET WebClient/HttpClient, JS fetch()). This is the
    /// "api.current.ai" failure mode — the planner doing a web search by writing a
    /// command against an invented API instead of using _web_search/_web_fetch.
    /// Legitimate URL-using commands (git clone, npm install &lt;git url&gt;,
    /// dotnet add package --source, git fetch origin) never match: they lack a
    /// content-fetch tool word, and bare `git fetch` has no URL in the fetch call.
    /// </summary>
    public static bool LooksLikeContentFetchCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        return ContentFetchCommandRegex.IsMatch(command);
    }

    private static readonly Regex ContentFetchCommandRegex = new Regex(
        @"(?i)\b(curl|wget|curl\.exe|irm|iwr|invoke-restmethod|invoke-webrequest|requests\.(?:get|post)|urllib|httpclient|webclient|downloadstring|downloadfile)\b[^\r\n]{0,200}https?://"
        + @"|\bfetch\(\s*['""]?https?://",
        RegexOptions.Compiled);

    /// <summary>
    /// True when a _create_file payload is a scraper/fetch SCRIPT — application code that
    /// programs an HTTP fetch (requests.get/urllib/fetch()/Invoke-RestMethod/HttpClient/curl)
    /// against a URL AND writes the result somewhere. That is the "wrote a Python app to do
    /// the fetch" failure mode: on a web-needing task the fetch belongs to the _web_fetch step
    /// tool, not to a script the planner invents (the model reaches for requests + csv when it
    /// forgets _web_fetch exists). Requiring the URL AND a file-write keeps real client-service
    /// code (an HTTP client that returns data, no file output) out of the net — only standalone
    /// scrape-and-save scripts match.
    /// </summary>
    public static bool LooksLikeScraperScriptContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        if (!Regex.IsMatch(content, @"https?://", RegexOptions.IgnoreCase)) return false;
        if (!ScraperFetchWordRegex.IsMatch(content)) return false;
        return ScraperWriteRegex.IsMatch(content);
    }

    private static readonly Regex ScraperFetchWordRegex = new Regex(
        @"(?i)\b(requests\.(?:get|post|request)|urllib|urlopen|http\.client|httpclient|webclient|"
        + @"invoke-restmethod|invoke-webrequest|curl|wget|axios|fetch\s*\()",
        RegexOptions.Compiled);

    private static readonly Regex ScraperWriteRegex = new Regex(
        @"(?i)\b(open\s*\([^)]*['\""][wa][b+]?['\""]|\.[\w]*write\w*\s*\(|writealltext|"
        + @"set-content|out-file|to_csv|json\.dump|echo\s+[^\r\n]*>|>\s*['\""]?[\w./\\]+\.(?:csv|json|txt|md))",
        RegexOptions.Compiled);

    public static bool HasSuccessfulEdits(IEnumerable<object> steps) =>
        steps.OfType<Dictionary<string, object?>>().Any(s =>
            s.TryGetValue("type", out var t) &&
            (string.Equals(t?.ToString(), "edit", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t?.ToString(), "rename", StringComparison.OrdinalIgnoreCase)) &&
            s.TryGetValue("status", out var st) && st?.ToString() == "done");

    internal static readonly HashSet<string> _whitespaceSignificantExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyi", ".pyw",
        ".yaml", ".yml",
        ".coffee",
        ".haml", ".slim", ".pug", ".jade",
        ".fs", ".fsx", ".fsi",
        ".nim",
        ".sass",
        ".hs", ".lhs",
        ".elm",
        ".ml", ".mli",
    };

    internal static readonly HashSet<string> _endKeywordLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rb",
        ".lua",
        ".ex", ".exs",
        ".sh", ".bash", ".zsh", ".fish",
    };

    internal static string ResolveWorkspaceRoot(IConfiguration _config, IWebHostEnvironment _env)
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        return Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
    }

    public static string GetProjectRoot(string project, IConfiguration _config, IWebHostEnvironment _env)
    {
        var workspaceRoot = ResolveWorkspaceRoot(_config, _env);
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" :
            project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
    }

    public static async Task<(StringBuilder fileContents, string warn)> GetReplanFileContents(List<object> executedSteps, string projectRoot, List<string>? attachedFiles, CancellationToken ct)
    {
        var fileContents = new StringBuilder();
        var pathsToRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string warn = "";
        foreach (var step in executedSteps.OfType<Dictionary<string, object?>>())
        {
            var p = step.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p)) pathsToRead.Add(p.Replace('\\', '/'));
        }
        if (attachedFiles != null) { foreach (var f in attachedFiles) pathsToRead.Add(f.Replace('\\', '/')); }
        foreach (var relPath in pathsToRead)
        {
            if (string.IsNullOrWhiteSpace(relPath)) continue;
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(
                    Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                continue;
            }
            if (!System.IO.File.Exists(fullPath)) continue;
            if (!IsPathUnderRoot(fullPath, projectRoot)) continue;
            try
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                const int MaxCharsPerFile = 8000;
                if (content.Length > MaxCharsPerFile)
                    content = content[..MaxCharsPerFile]
                              + $"\n… (truncated — full file is {content.Length} chars)";
                fileContents.AppendLine($"### {relPath}");
                fileContents.AppendLine("```");
                fileContents.AppendLine(content);
                fileContents.AppendLine("```");
                fileContents.AppendLine();
            }
            catch (Exception ex)
            {
                warn = $"Replan: could not read {relPath} for context: {ex.Message}";
            }
        }
        return (fileContents, warn);
    }

    public static string GetBenchmarkSandboxPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var root = !string.IsNullOrEmpty(desktop) ? desktop : Path.Combine(home, "Desktop");
        if (!Directory.Exists(root))
            root = home;
        return Path.Combine(root, "benchmark_sandbox");
    }

    public static bool IsWhitespaceSignificant(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return _whitespaceSignificantExts.Contains(ext) || _endKeywordLanguages.Contains(ext);
    }

    public static bool IsAngularTemplate(string content, string? filePath = null)
    {
        // A known file path decides FIRST: only an actual Angular template file — a .html
        // file (navigation.component.html, the root index.html, etc.) — is a template. A .ts
        // component is NEVER one: its logic legitimately uses Math.min/parseInt/JSON.parse
        // and its template literals contain {{ }} interpolation markers, so the content
        // heuristic below must not fire on it (the live navigation.component.ts movie-count
        // run was blocked by exactly this false positive).
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var name = Path.GetFileName(filePath);
            return name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("component.html", StringComparison.OrdinalIgnoreCase);
        }
        // No path known — content heuristic (legacy fallback).
        if (string.IsNullOrWhiteSpace(content) || content.Length < 20)
            return false;
        return Regex.IsMatch(content, @"\*ng(If|For|Switch)") ||
               Regex.IsMatch(content, @"\(click\)|\(change\)|\(keydown\)|\(submit\)|\(focus\)|\(blur\)") ||
               (content.Contains("{{") && content.Contains("}}"));
    }

    public static List<string> FindExistingTestFiles(string projectRoot)
    {
        var patterns = new[] { "*Test*.cs", "*Tests.cs", "*.Specs.cs", "*.specs.cs" };
        var dirs = new[] { "test", "tests", "Test", "Tests" };
        var result = new List<string>();
        foreach (var p in patterns)
        {
            try
            {
                result.AddRange(Directory.EnumerateFiles(projectRoot, p, SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\")));
            }
            catch { }
        }
        foreach (var d in dirs)
        {
            var dp = Path.Combine(projectRoot, d);
            if (Directory.Exists(dp))
            {
                try { result.AddRange(Directory.EnumerateFiles(dp, "*.cs", SearchOption.AllDirectories)); }
                catch { }
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool FileContains(string filePath, params string[] keywords)
    {
        try
        {
            using var sr = new StreamReader(filePath, Encoding.UTF8);
            var header = sr.ReadToEnd();
            return keywords.Any(k => header.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public static string FindOrDetermineTestDir(string projectRoot, List<string> existingTestFiles)
    {
        if (existingTestFiles.Count > 0)
        {
            var dir = Path.GetDirectoryName(existingTestFiles[0]);
            if (dir != null) return dir;
        }
        return Path.Combine(projectRoot, "tests");
    }

    public static string GetTestFilePath(string projectRoot, string sourceFilePath, string testDir)
    {
        var fileName = Path.GetFileNameWithoutExtension(sourceFilePath);
        var ext = Path.GetExtension(sourceFilePath);
        return Path.Combine(testDir, $"{fileName}Tests{ext}");
    }

    public static async Task<string?> DetectTestFramework(string projectRoot, CancellationToken ct)
    {
        try
        {
            foreach (var csproj in Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.AllDirectories))
            {
                var content = await System.IO.File.ReadAllTextAsync(csproj, Encoding.UTF8, ct);
                if (content.Contains("xunit", StringComparison.OrdinalIgnoreCase)) return "xunit";
                if (content.Contains("nunit", StringComparison.OrdinalIgnoreCase)) return "nunit";
                if (content.Contains("MSTest", StringComparison.OrdinalIgnoreCase)) return "mstest";
            }
        }
        catch { }
        return null;
    }
}
