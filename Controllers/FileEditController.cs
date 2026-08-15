using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Diagnostics;
namespace Weaver.Controllers;

[ApiController]
[Route("api/editor")]
public class FileEditController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    public FileEditController(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }
    public class EditRequest
    {
        public string Project { get; set; } = "";
        public string Path { get; set; } = "";
        public string Content { get; set; } = "";
        public bool Apply { get; set; } = true;
        public bool CreateIfMissing { get; set; } = true;
    }
    [HttpPost("write")]
    public async Task<IActionResult> Write([FromBody] EditRequest req)
    {
        if (req == null) return BadRequest("Missing request");
        // Containment is validated against the resolved PROJECT root (which may be an
        // absolute path outside the workspace root, e.g. a benchmark sandbox) so writes
        // stay consistent with reads/renames/deletes on such projects.
        var projectRoot = ResolveProjectRoot(req.Project ?? "");
        var relativePath = req.Path?.Trim() ?? "";
        var targetFull = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        if (!IsPathWithinWorkspace(targetFull, projectRoot))
        {
            return BadRequest("Path outside project root is not allowed.");
        }
        if (!req.Apply)
        {
            return Ok(new { path = targetFull, exists = System.IO.File.Exists(targetFull) });
        }
        var dir = Path.GetDirectoryName(targetFull);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (!System.IO.File.Exists(targetFull) && !req.CreateIfMissing)
        {
            return NotFound("File does not exist.");
        }
        try
        {
            await System.IO.File.WriteAllTextAsync(targetFull, req.Content ?? string.Empty, Encoding.UTF8);
            return Ok(new { path = targetFull, written = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
    [HttpGet("projects")]
    public IActionResult Projects()
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? (Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot)))
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        try
        {
            var dirs = Directory.GetDirectories(workspaceRoot, "*", SearchOption.TopDirectoryOnly)
                        .Select(d => new { name = Path.GetFileName(d), path = Path.GetRelativePath(workspaceRoot, d) });
            return Ok(dirs);
        }
        catch (Exception ex) { return StatusCode(500, ex.Message); }
    }
    [HttpGet("list")]
    public IActionResult List([FromQuery] string project = "", [FromQuery] string path = "", [FromQuery] string search = "", [FromQuery] bool recursive = false, [FromQuery] bool showHidden = false)
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? (Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot)))
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" : project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectRoot = Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
        var projectRootPrefix = projectRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? projectRoot : projectRoot + Path.DirectorySeparatorChar;
        // Ignored dirs (node_modules, bin, obj, .git, ...) are hidden from the explorer
        // unless the client asks to reveal them. Configurable via Editor:IgnoreDirs.
        var ignoreDirs = showHidden ? null : GetIgnoreDirs();
        try
        {
            // Recursive search when a search term is provided
            if (!string.IsNullOrWhiteSpace(search))
            {
                var searchTerm = search.Trim();
                // Determine the search root based on whether a path is specified
                var searchRoot = string.IsNullOrWhiteSpace(path) ? projectRoot : Path.GetFullPath(Path.Combine(projectRoot, path.Trim()));
                // Validate that the search root is within the project root
                if (!string.Equals(searchRoot, projectRoot, StringComparison.OrdinalIgnoreCase) &&
                    !searchRoot.StartsWith(projectRootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Path outside project root is not allowed.");
                }
                var matchingDirs = Directory.EnumerateDirectories(searchRoot, "*", SearchOption.AllDirectories)
                    .Where(d => FileNameMatches(Path.GetFileName(d), searchTerm)
                        && !ContainsIgnoredSegment(Path.GetRelativePath(projectRoot, d).Replace("\\", "/"), ignoreDirs))
                    .Select(d => new
                    {
                        name = Path.GetFileName(d),
                        path = Path.GetRelativePath(projectRoot, d).Replace("\\", "/"),
                        isDirectory = true
                    });
                var matchingFiles = Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
                    .Where(f => FileNameMatches(Path.GetFileName(f), searchTerm)
                        && !ContainsIgnoredSegment(Path.GetRelativePath(projectRoot, f).Replace("\\", "/"), ignoreDirs))
                    .Select(f => new
                    {
                        name = Path.GetFileName(f),
                        path = Path.GetRelativePath(projectRoot, f).Replace("\\", "/"),
                        isDirectory = false
                    });
                var searchEntries = matchingDirs.Concat(matchingFiles).OrderByDescending(x => x.isDirectory).ThenBy(x => x.name);
                return Ok(new { path = "", entries = searchEntries, search = searchTerm });
            }
            // Recursive listing — return all files and dirs under the path
            if (recursive)
            {
                var recRoot = string.IsNullOrWhiteSpace(path) ? projectRoot : Path.GetFullPath(Path.Combine(projectRoot, path.Trim()));
                if (!string.Equals(recRoot, projectRoot, StringComparison.OrdinalIgnoreCase) &&
                    !recRoot.StartsWith(projectRootPrefix, StringComparison.OrdinalIgnoreCase))
                    return BadRequest("Path outside project root is not allowed.");
                if (!Directory.Exists(recRoot))
                    return NotFound("Path not found.");
                var recDirs = Directory.EnumerateDirectories(recRoot, "*", SearchOption.AllDirectories)
                    .Where(d => !ContainsIgnoredSegment(Path.GetRelativePath(projectRoot, d).Replace("\\", "/"), ignoreDirs))
                    .Select(d => new
                    {
                        name = Path.GetFileName(d),
                        path = Path.GetRelativePath(projectRoot, d).Replace("\\", "/"),
                        isDirectory = true
                    });
                var recFiles = Directory.EnumerateFiles(recRoot, "*", SearchOption.AllDirectories)
                    .Where(f => !ContainsIgnoredSegment(Path.GetRelativePath(projectRoot, f).Replace("\\", "/"), ignoreDirs))
                    .Select(f => new
                    {
                        name = Path.GetFileName(f),
                        path = Path.GetRelativePath(projectRoot, f).Replace("\\", "/"),
                        isDirectory = false
                    });
                var recEntries = recDirs.Concat(recFiles).OrderBy(e => e.path).ToList();
                return Ok(new { path = "", entries = recEntries, recursive = true });
            }

            // Normal directory listing when no search term
            var relativePath = (path ?? "").Trim().TrimStart('/', '\\');
            var targetFull = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
            // Ensure the target path is within the project root
            if (!string.Equals(targetFull, projectRoot, StringComparison.OrdinalIgnoreCase) &&
                !targetFull.StartsWith(projectRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Path outside project root is not allowed.");
            }
            // If a specific file is requested, return its info
            if (System.IO.File.Exists(targetFull))
            {
                return Ok(new
                {
                    path = Path.GetRelativePath(projectRoot, targetFull).Replace("\\", "/"),
                    name = Path.GetFileName(targetFull),
                    isDirectory = false
                });
            }
            // If the path doesn't exist as a file or directory, return not found
            if (!Directory.Exists(targetFull))
            {
                return NotFound("Path not found.");
            }
            var dirs = Directory.GetDirectories(targetFull)
                .Where(d => !ContainsIgnoredSegment(Path.GetRelativePath(projectRoot, d).Replace("\\", "/"), ignoreDirs))
                .Select(d => new
                {
                    name = Path.GetFileName(d),
                    path = Path.GetRelativePath(projectRoot, d).Replace("\\", "/"),
                    isDirectory = true
                });
            var files = Directory.GetFiles(targetFull)
                .Where(f => !ContainsIgnoredSegment(Path.GetRelativePath(projectRoot, f).Replace("\\", "/"), ignoreDirs))
                .Select(f => new
                {
                    name = Path.GetFileName(f),
                    path = Path.GetRelativePath(projectRoot, f).Replace("\\", "/"),
                    isDirectory = false
                });
            var entries = dirs.Concat(files).OrderByDescending(x => x.isDirectory).ThenBy(x => x.name);
            return Ok(new { path = Path.GetRelativePath(projectRoot, targetFull).Replace("\\", "/"), entries });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
    /// <summary>
    /// True when <paramref name="name"/> matches <paramref name="term"/> under the file
    /// picker's search rules: a plain case-insensitive substring, OR a FUZZY substring
    /// that ignores filename separators ('.', '-', '_', spaces). So searching
    /// "movieservice" matches "movie.service.js" and "movie-service" — users rarely
    /// type the exact separators a file actually uses. The fuzzy pass is ADDITIVE
    /// (strict still wins when the term itself contains separators, e.g. "service.js").
    /// </summary>
    internal static bool FileNameMatches(string name, string term)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (string.IsNullOrEmpty(term)) return true;
        if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        var nameNorm = NormalizeForFuzzyMatch(name);
        var termNorm = NormalizeForFuzzyMatch(term);
        // A separator-only term ("-", ".") normalizes to empty and would match EVERY
        // file — that's not fuzzy, it's a blank search. Fall back to the strict result.
        if (termNorm.Length == 0) return false;
        return nameNorm.IndexOf(termNorm, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Keeps letters/digits only, so "movie.service", "movie-service",
    /// "movie_service" and "movie service" all collapse to "movieservice".</summary>
    private static string NormalizeForFuzzyMatch(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// Default ignore-list for build/vcs/dependency folders. Users can extend it via
    /// the Editor:IgnoreDirs config key (array or comma-separated). Entries prefixed
    /// with '-' remove a default (e.g. "-bin" re-shows a legit bin source dir).
    /// </summary>
    private static readonly string[] DefaultIgnoreDirs =
    {
        "node_modules", "bin", "obj", ".git", "dist", "build", "out", ".vs", ".idea",
        ".vscode", ".angular", ".next", ".nuxt", "coverage", ".cache", ".parcel-cache",
        ".pytest_cache", "__pycache__", ".venv", "venv", ".gradle", ".mypy_cache",
        ".tox", ".ruff_cache", ".turbo", "target", "Debug", "Release"
    };

    /// <summary>
    /// Builds the effective ignore set: config entries are parsed first (each may be a
    /// single segment or a slash-separated path like "node_modules/.cache"), then the
    /// defaults are added. A '-' prefix on any config entry removes it from the final
    /// set, so a user can un-hide a default like "bin" when it's real source.
    /// </summary>
    private HashSet<string> GetIgnoreDirs()
    {
        var raw = new List<string>();
        // Support both an array config and a comma-separated string.
        var section = _config.GetSection("Editor:IgnoreDirs");
        if (section.GetChildren().Any())
        {
            foreach (var child in section.GetChildren())
                if (!string.IsNullOrWhiteSpace(child.Value))
                    raw.Add(child.Value!);
        }
        var csv = _config.GetValue<string>("Editor:IgnoreDirs");
        if (!string.IsNullOrWhiteSpace(csv)) raw.Add(csv);
        return MergeIgnoreDirs(raw);
    }

    /// <summary>
    /// Pure merge of raw config entries with the built-in defaults. Kept separate from
    /// GetIgnoreDirs so the exact-match semantics are unit-testable without an
    /// IConfiguration.
    /// </summary>
    public static HashSet<string> MergeIgnoreDirs(IEnumerable<string> configEntries)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRaw(string raw)
        {
            foreach (var seg in raw.Split(new[] { ',', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seg.StartsWith('-'))
                {
                    var un = seg[1..].Trim();
                    if (un.Length > 0) removals.Add(un);
                }
                else if (seg.Length > 0)
                {
                    set.Add(seg);
                }
            }
        }

        foreach (var entry in configEntries)
            if (!string.IsNullOrWhiteSpace(entry))
                AddRaw(entry);

        foreach (var d in DefaultIgnoreDirs) set.Add(d);
        foreach (var r in removals) set.Remove(r);
        return set;
    }

    /// <summary>
    /// True when any path segment of the project-relative path matches an ignored dir,
    /// which hides both the dir itself and everything nested inside it (e.g. a file
    /// under node_modules).
    /// </summary>
    private static bool ContainsIgnoredSegment(string relPath, HashSet<string>? ignoreDirs)
    {
        if (ignoreDirs == null || ignoreDirs.Count == 0) return false;
        foreach (var seg in relPath.Split('/'))
            if (ignoreDirs.Contains(seg)) return true;
        return false;
    }

    [HttpGet("content")]
    public IActionResult GetContent([FromQuery] string project = "", [FromQuery] string path = "")
    {
        if (string.IsNullOrEmpty(path))
        {
            return BadRequest("Path is required");
        }
        // The project may be an absolute path OUTSIDE the configured workspace root
        // (e.g. a benchmark sandbox folder). Resolve the project root exactly like the
        // agent endpoints (GetProjectRoot) do — Path.Combine with a rooted second
        // segment yields that segment — and validate containment against THAT root,
        // not the workspace root. Otherwise opening undo diffs (data/undo/*.diff) for
        // such projects fails with a spurious 400 "Path outside workspace root".
        var projectRoot = ResolveProjectRoot(project);
        var relativePath = path.Trim();
        var targetFull = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        if (!IsPathWithinWorkspace(targetFull, projectRoot))
        {
            return BadRequest("Path outside project root is not allowed.");
        }
        if (!System.IO.File.Exists(targetFull))
        {
            return NotFound("File not found.");
        }
        try
        {
            var content = System.IO.File.ReadAllText(targetFull);
            var lastModified = System.IO.File.GetLastWriteTimeUtc(targetFull).ToString("O");
            return Ok(new { content, lastModified });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
    [HttpGet("snippet")]
    public IActionResult GetSnippet([FromQuery] string file = "", [FromQuery] int line = 0, [FromQuery] int context = 3)
    {
        if (string.IsNullOrWhiteSpace(file) || line <= 0)
            return BadRequest(new { error = "file and line are required" });
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            workspaceRoot = Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        }
        else
        {
            workspaceRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        }
        try
        {
            var resolved = ResolveSnippetFile(workspaceRoot, file);
            if (resolved == null || !System.IO.File.Exists(resolved))
                return NotFound(new { error = "File not found in workspace." });
            var rel = Path.GetRelativePath(workspaceRoot, resolved).Replace('\\', '/');
            var allLines = System.IO.File.ReadAllLines(resolved);
            if (allLines.Length == 0)
                return Ok(new { path = rel, file = Path.GetFileName(resolved), line = 0, lines = new List<object>() });
            context = Math.Clamp(context, 0, 20);
            var target = Math.Clamp(line, 1, allLines.Length);
            var start = Math.Max(0, target - 1 - context);
            var end = Math.Min(allLines.Length, target + context);
            var snippet = new List<object>();
            for (var i = start; i < end; i++)
            {
                snippet.Add(new { number = i + 1, text = allLines[i], isTarget = (i + 1) == target });
            }
            return Ok(new { path = rel, file = Path.GetFileName(resolved), line = target, lines = snippet });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private string? ResolveSnippetFile(string workspaceRoot, string file)
    {
        var norm = file.Trim();
        // Strip scheme://host prefixes (file://, http://host/...)
        norm = System.Text.RegularExpressions.Regex.Replace(norm, @"^[a-z]+://[^/]+/", "/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        norm = norm.Replace("file:///", "/").Replace("file://", "/");
        norm = norm.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(norm)) return null;

        var candidates = new List<string>();
        if (Path.IsPathRooted(norm))
        {
            var abs = Path.GetFullPath(norm);
            if (IsPathWithinWorkspace(abs, workspaceRoot))
                candidates.Add(abs);
        }
        else
        {
            // Direct candidates: under the workspace root, under the app's own
            // content root (Weaver's wwwroot), and under each project folder.
            candidates.Add(Path.GetFullPath(Path.Combine(workspaceRoot, norm)));
            candidates.Add(Path.GetFullPath(Path.Combine(_env.ContentRootPath, norm)));
            if (Directory.Exists(workspaceRoot))
            {
                foreach (var dir in Directory.GetDirectories(workspaceRoot))
                    candidates.Add(Path.GetFullPath(Path.Combine(dir, norm)));
            }
        }
        foreach (var c in candidates)
        {
            if (IsPathWithinWorkspace(c, workspaceRoot) && System.IO.File.Exists(c))
                return c;
        }

        // Basename fallback: bounded recursive search, skipping noise dirs.
        // workspaceRoot covers its top-level project folders, and the app's
        // content root covers Weaver's own wwwroot; only add the content root
        // separately when it lives outside the workspace (custom WorkspaceRoot).
        var basename = Path.GetFileName(norm);
        if (string.IsNullOrWhiteSpace(basename)) return null;
        var roots = new List<string> { workspaceRoot };
        if (!IsPathWithinWorkspace(_env.ContentRootPath, workspaceRoot))
            roots.Add(_env.ContentRootPath);
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "bin", "obj", "dist", "build", ".vs", ".idea", ".vscode", ".playwright-mcp", ".freebuff", ".venv", "venv" };
        foreach (var root in roots)
        {
            try
            {
                foreach (var f in EnumerateFilesBounded(root, basename, skipDirs, 0))
                {
                    var full = Path.GetFullPath(f);
                    if (IsPathWithinWorkspace(full, workspaceRoot))
                        return full;
                }
            }
            catch { }
        }
        return null;
    }

    private static bool IsPathWithinWorkspace(string path, string workspaceRoot)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var rootPrefix = workspaceRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? workspaceRoot : workspaceRoot + Path.DirectorySeparatorChar;
        return path.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateFilesBounded(string root, string fileName, HashSet<string> skipDirs, int depth)
    {
        if (depth > 10) yield break;
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.EnumerateFiles(root))
        {
            if (Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                yield return f;
        }
        foreach (var d in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(d);
            if (skipDirs.Contains(name)) continue;
            foreach (var f in EnumerateFilesBounded(d, fileName, skipDirs, depth + 1))
                yield return f;
        }
    }

    [HttpGet("check-modified")]
    public IActionResult CheckModified([FromQuery] string project = "", [FromQuery] string path = "", [FromQuery] string? since = null)
    {
        if (string.IsNullOrEmpty(path))
            return BadRequest("Path is required");
        // Containment is validated against the resolved PROJECT root so file-change
        // polling also works for absolute-path projects outside the workspace root.
        var projectRoot = ResolveProjectRoot(project);
        var relativePath = path.Trim();
        var targetFull = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        if (!IsPathWithinWorkspace(targetFull, projectRoot))
            return BadRequest("Path outside project root is not allowed.");
        if (!System.IO.File.Exists(targetFull))
            return Ok(new { exists = false, modified = false, lastModified = (string?)null });
        var lastModified = System.IO.File.GetLastWriteTimeUtc(targetFull);
        var modified = true;
        if (!string.IsNullOrWhiteSpace(since) && DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var sinceDt))
        {
            modified = lastModified > sinceDt;
        }
        return Ok(new { exists = true, modified, lastModified = lastModified.ToString("O") });
    }
    [HttpPost("save")]
    public async Task<IActionResult> Save([FromBody] EditRequest req)
    {
        if (req == null) return BadRequest("Missing request");
        // Containment is validated against the resolved PROJECT root (which may be an
        // absolute path outside the workspace root, e.g. a benchmark sandbox) so saves
        // stay consistent with reads/renames/deletes on such projects.
        var projectRoot = ResolveProjectRoot(req.Project ?? "");
        var relativePath = req.Path?.Trim() ?? "";
        var targetFull = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        if (!IsPathWithinWorkspace(targetFull, projectRoot))
        {
            return BadRequest("Path outside project root is not allowed.");
        }
        if (!req.Apply)
        {
            return Ok(new { path = targetFull, exists = System.IO.File.Exists(targetFull) });
        }
        var dir = Path.GetDirectoryName(targetFull);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (!System.IO.File.Exists(targetFull) && !req.CreateIfMissing)
        {
            return NotFound("File does not exist.");
        }
        try
        {
            await System.IO.File.WriteAllTextAsync(targetFull, req.Content ?? string.Empty, Encoding.UTF8);
            return Ok(new { path = targetFull, written = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
    [HttpGet("git-diff")]
    public async Task<IActionResult> GitDiff([FromQuery] string project = "")
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            workspaceRoot = Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        }
        else
        {
            workspaceRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        }
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" : project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectRoot = Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
        if (!Directory.Exists(projectRoot))
        {
            return NotFound("Project directory not found.");
        }
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff --no-color",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            // Staged diff
            using var stagedProc = new Process();
            stagedProc.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff --staged --no-color",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            stagedProc.Start();
            var stagedOutput = await stagedProc.StandardOutput.ReadToEndAsync();
            await stagedProc.WaitForExitAsync();
            // Untracked files
            using var untracked = new Process();
            untracked.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "ls-files --others --exclude-standard",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            untracked.Start();
            var untrackedOutput = await untracked.StandardOutput.ReadToEndAsync();
            await untracked.WaitForExitAsync();
            // Current branch name
            using var branchProc = new Process();
            branchProc.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --abbrev-ref HEAD",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            branchProc.Start();
            var branchOutput = await branchProc.StandardOutput.ReadToEndAsync();
            await branchProc.WaitForExitAsync();
            // Parse working-dir diff into structured file-level entries
            var files = new List<object>();
            var currentFile = "";
            var currentLines = new List<string>();
            void FlushFile(List<object> target)
            {
                if (!string.IsNullOrWhiteSpace(currentFile) && currentLines.Count > 0)
                {
                    target.Add(new
                    {
                        path = currentFile,
                        body = string.Join("\n", currentLines)
                    });
                }
            }
            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("diff --git "))
                {
                    FlushFile(files);
                    var parts = line.Split(' ');
                    currentFile = parts.Length >= 4 && parts[3].Length > 2 ? parts[3][2..] : "";
                }
                currentLines.Add(line);
            }
            FlushFile(files);
            // Parse staged diff
            var stagedFiles = new List<object>();
            currentFile = "";
            currentLines.Clear();
            foreach (var line in stagedOutput.Split('\n'))
            {
                if (line.StartsWith("diff --git "))
                {
                    FlushFile(stagedFiles);
                    var parts = line.Split(' ');
                    currentFile = parts.Length >= 4 && parts[3].Length > 2 ? parts[3][2..] : "";
                }
                currentLines.Add(line);
            }
            FlushFile(stagedFiles);
            var untrackedFiles = untrackedOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();
            var branchName = branchOutput?.Trim() ?? "unknown";
            var hasUnpushed = false;
            try
            {
                using var unpushedProc = new Process();
                unpushedProc.StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-list --count @{u}..HEAD",
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                unpushedProc.Start();
                var unpushedOut = (await unpushedProc.StandardOutput.ReadToEndAsync()).Trim();
                await unpushedProc.WaitForExitAsync();
                if (int.TryParse(unpushedOut, out var count))
                    hasUnpushed = count > 0;
            }
            catch { }
            return Ok(new
            {
                diff = output,
                files,
                staged = stagedFiles,
                untracked = untrackedFiles,
                branch = branchName,
                hasChanges = !string.IsNullOrWhiteSpace(output) || stagedFiles.Count > 0 || untrackedFiles.Count > 0,
                hasStaged = stagedFiles.Count > 0,
                hasUnpushed
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
    [HttpGet("git-diff-file")]
    public async Task<IActionResult> GitDiffFile([FromQuery] string project = "", [FromQuery] string path = "")
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Path is required" });
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            workspaceRoot = Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        }
        else
        {
            workspaceRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        }
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" : project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectRoot = Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
        if (!Directory.Exists(projectRoot))
            return NotFound(new { error = "Project directory not found." });
        // Read new content from disk
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, path.Trim().Replace('/', Path.DirectorySeparatorChar)));
        string newContent = "";
        if (System.IO.File.Exists(fullPath))
            newContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        // Read old content from git HEAD
        string oldContent = "";
        bool isGitRepo = false;
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"show HEAD:\"{path.Trim().Replace('\\', '/')}\"",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process.Start();
            var gitOutput = await process.StandardOutput.ReadToEndAsync();
            var gitError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
                oldContent = gitOutput;
        }
        catch { }
        // Distinguish "new/untracked file" from "not a git repo at all" so the
        // minimap doesn't paint every line green for non-git projects.
        try
        {
            using var checkProc = new Process();
            checkProc.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --is-inside-work-tree",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            checkProc.Start();
            var checkOut = (await checkProc.StandardOutput.ReadToEndAsync()).Trim();
            await checkProc.WaitForExitAsync();
            isGitRepo = checkProc.ExitCode == 0 && string.Equals(checkOut, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return Ok(new
        {
            path,
            oldContent,
            newContent,
            isNewFile = string.IsNullOrWhiteSpace(oldContent),
            isGitRepo
        });
    }
    public class GitCommitRequest
    {
        public string Project { get; set; } = "";
        public string Message { get; set; } = "";
    }
    public class GitPrRequest
    {
        public string Project { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Summary { get; set; }
    }
    private async Task<string> RunGitAsync(string args, string workingDir)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        proc.Start();
        var output = await proc.StandardOutput.ReadToEndAsync();
        var error = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0 ? output : error;
    }
    [HttpPost("git-commit")]
    public async Task<IActionResult> GitCommit([FromBody] GitCommitRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Project))
            return BadRequest(new { error = "Project required" });
        var projectRoot = ResolveProjectRoot(req.Project);
        if (!Directory.Exists(projectRoot))
            return NotFound(new { error = "Project directory not found." });
        try
        {
            var addOut = await RunGitAsync("add -A", projectRoot);
            var escaped = (req.Message ?? "commit").Replace("\"", "\\\"");
            var commitOut = await RunGitAsync($"commit -m \"{escaped}\"", projectRoot);
            var isNoOp = commitOut.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase);
            return Ok(new { success = !isNoOp, addOutput = addOut, commitOutput = commitOut, nothingToCommit = isNoOp });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }
    [HttpPost("git-push")]
    public async Task<IActionResult> GitPush([FromBody] GitCommitRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Project))
            return BadRequest(new { error = "Project required" });
        var projectRoot = ResolveProjectRoot(req.Project);
        if (!Directory.Exists(projectRoot))
            return NotFound(new { error = "Project directory not found." });
        try
        {
            var branch = (await RunGitAsync("rev-parse --abbrev-ref HEAD", projectRoot)).Trim();
            var pushOut = await RunGitAsync($"push origin \"{branch}\"", projectRoot);
            return Ok(new { success = true, branch, output = pushOut });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }
    [HttpPost("git-pr")]
    public async Task<IActionResult> GitPr([FromBody] GitPrRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Project))
            return BadRequest(new { error = "Project required" });
        var projectRoot = ResolveProjectRoot(req.Project);
        if (!Directory.Exists(projectRoot))
            return NotFound(new { error = "Project directory not found." });
        try
        {
            // Stage + commit
            await RunGitAsync("add -A", projectRoot);
            var escapedMsg = (req.Message ?? "commit").Replace("\"", "\\\"");
            await RunGitAsync($"commit -m \"{escapedMsg}\"", projectRoot);
            // Push to current branch
            var branch = (await RunGitAsync("rev-parse --abbrev-ref HEAD", projectRoot)).Trim();
            var pushOut = await RunGitAsync($"push -u origin \"{branch}\"", projectRoot);
            // Create PR via gh
            var prBody = req.Summary ?? req.Message ?? "";
            var escapedBody = prBody.Replace("\"", "\\\"").Replace("\n", "\\n");
            var escapedTitle = (req.Message ?? "Weaver changes").Replace("\"", "\\\"");
            using var ghProc = new Process();
            ghProc.StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = $"pr create --title \"{escapedTitle}\" --body \"{escapedBody}\" --head \"{branch}\"",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            ghProc.Start();
            var ghOut = await ghProc.StandardOutput.ReadToEndAsync();
            var ghErr = await ghProc.StandardError.ReadToEndAsync();
            await ghProc.WaitForExitAsync();
            return Ok(new
            {
                success = ghProc.ExitCode == 0,
                branch,
                pushOutput = pushOut,
                prUrl = ghProc.ExitCode == 0 ? ghOut?.Trim() : ghErr
            });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }
    public class FileOpRequest
    {
        public string Project { get; set; } = "";
        public string Path { get; set; } = "";
        public string NewName { get; set; } = "";
        public string TargetPath { get; set; } = "";
    }
    [HttpPost("rename")]
    public IActionResult Rename([FromBody] FileOpRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "Path required" });
        var projectRoot = ResolveProjectRoot(req.Project ?? "");
        var srcFull = Path.GetFullPath(Path.Combine(projectRoot, req.Path.Trim().TrimStart('/', '\\')));
        if (!IsPathWithinWorkspace(srcFull, projectRoot))
            return BadRequest(new { error = "Path outside project root is not allowed." });
        if (!System.IO.File.Exists(srcFull) && !Directory.Exists(srcFull))
            return NotFound(new { error = "File or folder not found." });
        var newName = (req.NewName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return BadRequest(new { error = "Invalid name." });
        var parent = Path.GetDirectoryName(srcFull)!;
        var dstFull = Path.Combine(parent, newName);
        if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
            return Ok(new { success = true, path = req.Path });
        if (System.IO.File.Exists(dstFull) || Directory.Exists(dstFull))
            return Conflict(new { error = $"'{newName}' already exists." });
        try
        {
            if (System.IO.File.Exists(srcFull))
                System.IO.File.Move(srcFull, dstFull);
            else
                Directory.Move(srcFull, dstFull);
            return Ok(new { success = true, path = Path.GetRelativePath(projectRoot, dstFull).Replace('\\', '/') });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
    [HttpPost("delete")]
    public IActionResult Delete([FromBody] FileOpRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "Path required" });
        var projectRoot = ResolveProjectRoot(req.Project ?? "");
        var srcFull = Path.GetFullPath(Path.Combine(projectRoot, req.Path.Trim().TrimStart('/', '\\')));
        if (!IsPathWithinWorkspace(srcFull, projectRoot))
            return BadRequest(new { error = "Path outside project root is not allowed." });
        if (!System.IO.File.Exists(srcFull) && !Directory.Exists(srcFull))
            return NotFound(new { error = "File or folder not found." });
        try
        {
            if (System.IO.File.Exists(srcFull))
                System.IO.File.Delete(srcFull);
            else
                Directory.Delete(srcFull, true);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
    [HttpPost("mkdir")]
    public IActionResult Mkdir([FromBody] FileOpRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "Path required" });
        var projectRoot = ResolveProjectRoot(req.Project ?? "");
        var dirFull = Path.GetFullPath(Path.Combine(projectRoot, req.Path.Trim().TrimStart('/', '\\')));
        if (!IsPathWithinWorkspace(dirFull, projectRoot))
            return BadRequest(new { error = "Path outside project root is not allowed." });
        if (Directory.Exists(dirFull))
            return Conflict(new { error = "Folder already exists." });
        try
        {
            Directory.CreateDirectory(dirFull);
            return Ok(new { success = true, path = Path.GetRelativePath(projectRoot, dirFull).Replace('\\', '/') });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
    [HttpPost("move")]
    public IActionResult Move([FromBody] FileOpRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "Path required" });
        var projectRoot = ResolveProjectRoot(req.Project ?? "");
        var srcFull = Path.GetFullPath(Path.Combine(projectRoot, req.Path.Trim().TrimStart('/', '\\')));
        var dstDirFull = Path.GetFullPath(Path.Combine(projectRoot, (req.TargetPath ?? "").Trim().TrimStart('/', '\\')));
        if (!IsPathWithinWorkspace(srcFull, projectRoot) || !IsPathWithinWorkspace(dstDirFull, projectRoot))
            return BadRequest(new { error = "Path outside project root is not allowed." });
        if (!System.IO.File.Exists(srcFull) && !Directory.Exists(srcFull))
            return NotFound(new { error = "File or folder not found." });
        if (!Directory.Exists(dstDirFull))
            return NotFound(new { error = "Target folder not found." });
        // Prevent moving a folder into itself or one of its own descendants.
        if (Directory.Exists(srcFull))
        {
            var srcPrefix = srcFull.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? srcFull : srcFull + Path.DirectorySeparatorChar;
            if (dstDirFull.Equals(srcFull, StringComparison.OrdinalIgnoreCase) ||
                dstDirFull.StartsWith(srcPrefix, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Cannot move a folder into itself." });
        }
        var name = Path.GetFileName(srcFull);
        var dstFull = Path.Combine(dstDirFull, name);
        if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
            return Ok(new { success = true, path = req.Path });
        if (System.IO.File.Exists(dstFull) || Directory.Exists(dstFull))
            return Conflict(new { error = $"'{name}' already exists in the destination." });
        try
        {
            if (System.IO.File.Exists(srcFull))
                System.IO.File.Move(srcFull, dstFull);
            else
                Directory.Move(srcFull, dstFull);
            return Ok(new { success = true, path = Path.GetRelativePath(projectRoot, dstFull).Replace('\\', '/') });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
    private string ResolveProjectRoot(string project)
    {
        var configuredRoot = _config.GetValue<string>("Editor:WorkspaceRoot");
        string workspaceRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            workspaceRoot = Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configuredRoot));
        }
        else
        {
            workspaceRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, ".."));
        }
        var projectSegment = string.IsNullOrWhiteSpace(project) ? "" : project.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(workspaceRoot, projectSegment));
    }
}
