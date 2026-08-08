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
using static Weaver.Services.AgentJsonUtilities;    /// <summary>Part of the split of the former AgentUtilities monolith.</summary>
    public static class AgentDiscovery
    {
        /// <summary>
        /// Best-effort extraction of a concrete file name (with extension) from a step's change
        /// description, e.g. "Create index.html inside benchmark_test_7" → "index.html". Returns
        /// null when no plausible file name (extension that isn't purely numeric) appears. Used to
        /// redirect a step that targets an existing DIRECTORY (a replanner re-emitting "create
        /// directory X" as a normal edit step) into a named file inside it instead of attempting
        /// to write file content to the directory path (which throws UnauthorizedAccessException
        /// on Windows).
        /// </summary>
        public static string? TryExtractFileNameFromChange(string? changeDesc)
        {
            if (string.IsNullOrWhiteSpace(changeDesc)) return null;
            foreach (Match m in Regex.Matches(changeDesc, @"\b([A-Za-z0-9_-]+\.\w{1,12})\b"))
            {
                var tok = m.Groups[1].Value;
                var ext = Path.GetExtension(tok);
                if (string.IsNullOrWhiteSpace(ext)) continue;
                // Skip version-like tokens ("v1.2", "2.0") whose extension is purely numeric.
                if (ext.TrimStart('.').All(char.IsDigit)) continue;
                return tok;
            }
            return null;
        }

        /// <summary>
        /// When an edit step's resolved target turns out to be an existing DIRECTORY (a replanner
        /// re-emitted "create directory X" as a normal edit step), decide the effective target:
        /// if the change description names a concrete file, return that file path INSIDE the
        /// directory (the write is redirected there); otherwise return null, meaning the step's
        /// intent is already satisfied — the directory exists — and it should be marked done
        /// without touching disk.
        /// </summary>
        public static string? ResolveDirectoryTargetForStep(string dirRelPath, string? changeDesc)
        {
            if (string.IsNullOrWhiteSpace(dirRelPath)) return null;
            var namedFile = TryExtractFileNameFromChange(changeDesc);
            if (string.IsNullOrWhiteSpace(namedFile)) return null;
            return dirRelPath.TrimEnd('/') + "/" + namedFile;
        }

        public static string DistillExplorationContext(
        string explorationContext,
        string targetRelPath,
        string changeDesc,
        string? targetSymbol,
        int maxChars = 7_000)
    {
        if (string.IsNullOrWhiteSpace(explorationContext)) return "";
        var keywords = ExtractMeaningfulKeywords(changeDesc.ToLowerInvariant())
            .Where(k => k.Length >= 4)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(targetSymbol))
            keywords.Add(targetSymbol);
        var normalizedTarget = targetRelPath.Replace('\\', '/');
        var sections = Regex.Split(explorationContext.Trim(), @"(?=^### )", RegexOptions.Multiline);
        var result = new StringBuilder();
        foreach (var rawSection in sections)
        {
            if (string.IsNullOrWhiteSpace(rawSection)) continue;
            var firstLine = rawSection.Split('\n')[0];
            if (firstLine.Contains("TARGET FILE:", StringComparison.OrdinalIgnoreCase)) continue;
            var sectionPath = Regex.Match(firstLine, @"###\s+([^\s(]+)").Groups[1].Value
                .Replace('\\', '/').Trim();
            if (string.Equals(sectionPath, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                continue;
            var distilled = DistillFileSection(rawSection, keywords);
            if (string.IsNullOrWhiteSpace(distilled)) continue;
            var budget = maxChars - result.Length;
            if (budget < 100) { result.AppendLine("... [context budget exhausted]"); break; }
            if (distilled.Length > budget)
                distilled = distilled[..budget] + "\n    // ... [truncated]";
            result.AppendLine(distilled);
        }
        return result.ToString();
    }

    internal static string DistillFileSection(string section, HashSet<string> keywords, int maxCharsPerSection = 1_800)
    {
        var lines = section.Split('\n');
        var headerLines = new List<string>();
        var codeLines = new List<string>();
        var openingFence = "";
        var inFence = false;
        var pastFirstFence = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                if (!pastFirstFence)
                {
                    pastFirstFence = true;
                    inFence = true;
                    openingFence = line;
                }
                else if (inFence)
                {
                    inFence = false;
                    break;
                }
                continue;
            }
            if (inFence)
                codeLines.Add(line);
            else
                headerLines.Add(line);
        }
        if (codeLines.Count == 0)
            return string.Join("\n", headerLines);
        var included = new SortedSet<int>();
        for (var i = 0; i < Math.Min(20, codeLines.Count); i++)
            included.Add(i);
        for (var i = 20; i < codeLines.Count; i++)
        {
            if (keywords.Count == 0) break;
            if (keywords.Any(kw => codeLines[i].Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                for (var w = Math.Max(0, i - 3); w <= Math.Min(codeLines.Count - 1, i + 3); w++)
                    included.Add(w);
            }
            if (Regex.IsMatch(codeLines[i], @"^\s*((public|private|protected|static|async|export|function|get|set)\s+)*\w+\s*(<[^>]+>)?\s*\([^)]*\)\s*(:\s*[^{;]+)?\s*[{;]", RegexOptions.IgnoreCase))
            {
                for (var w = Math.Max(0, i - 1); w <= Math.Min(codeLines.Count - 1, i + 5); w++)
                    included.Add(w);
            }
        }
        var result = new List<string>(headerLines) { openingFence };
        var prevIdx = -2;
        foreach (var idx in included)
        {
            if (prevIdx >= 0 && idx > prevIdx + 1)
                result.Add("    // ...");
            result.Add(codeLines[idx]);
            prevIdx = idx;
        }
        if (prevIdx < codeLines.Count - 1)
            result.Add("    // ...");
        result.Add("```");
        var output = string.Join("\n", result);
        return output.Length > maxCharsPerSection
            ? output[..maxCharsPerSection] + "\n    // ... [truncated]"
            : output;
    }

    internal static HashSet<string> ExtractQuotedSnippets(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match m in Regex.Matches(text, @"<[^>]+>.*?</\w+>|`[^`]+`"))
        {
            var norm = Regex.Replace(m.Value.ToLowerInvariant(), @"\s+", " ").Trim();
            if (norm.Length >= 15) result.Add(norm);
        }
        return result;
    }

    public static string? ExtractFileSectionFromContext(string discoveryContext, string filePath)
    {
        if (string.IsNullOrWhiteSpace(discoveryContext) || string.IsNullOrWhiteSpace(filePath))
            return null;
        var normPath = filePath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normPath);
        var lines = discoveryContext.Split('\n');
        var startLine = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            // Focused reads render as "### read path (focused: ...; full file via _explore)" —
            // strip the suffix so section lookups still match the plain path.
            var trimmed = Regex.Replace(lines[i].TrimStart(),
                @"\s+\(focused:[^)]*\)\s*$", "", RegexOptions.IgnoreCase);
            if ((trimmed.StartsWith("### read ") || trimmed.StartsWith("### list ")) &&
                trimmed.EndsWith(normPath, StringComparison.OrdinalIgnoreCase))
            { startLine = i; break; }
            if (trimmed.StartsWith("### ") && trimmed.EndsWith(normPath, StringComparison.OrdinalIgnoreCase))
            { startLine = i; break; }
        }
        if (startLine < 0)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = Regex.Replace(lines[i].TrimStart(),
                    @"\s+\(focused:[^)]*\)\s*$", "", RegexOptions.IgnoreCase);
                if ((trimmed.StartsWith("### read ") || trimmed.StartsWith("### list ") || trimmed.StartsWith("### ")) &&
                    trimmed.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) &&
                    trimmed.IndexOfAny(new[] { ' ', '\t' }) > 3)
                { startLine = i; break; }
            }
        }
        if (startLine < 0) return null;
        var endLine = startLine + 1;
        for (var i = startLine + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("### ") && !trimmed.StartsWith("####"))
            { endLine = i; break; }
        }
        return string.Join("\n", lines.Skip(startLine).Take(endLine - startLine));
    }

    public static List<string> FindSimilarFiles(string missingPath, string projectRoot)
    {
        var name = Path.GetFileName(missingPath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = missingPath;
        var found = new List<string>();
        if (!Directory.Exists(projectRoot)) return found;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", "dist" };
        foreach (var file in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            if (skip.Any(s => rel.Contains("/" + s + "/", StringComparison.OrdinalIgnoreCase))) continue;
            if (rel.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase))
                found.Add(rel);
            if (found.Count >= 10) break;
        }
        return found;
    }

    /// <summary>
    /// Finds an existing file with the SAME NAME in the SAME directory as <paramref name="targetRelPath"/>
    /// (case-insensitive). Unlike a bare basename search, a same-named file in a DIFFERENT directory
    /// (e.g. benchmark_test_4/index.html when creating benchmark_test_7/index.html) is NOT a conflict
    /// and must never block creation. Returns the existing relative path or null.
    /// </summary>
    public static string? FindSameDirectoryFile(string targetRelPath, string projectRoot)
    {
        var norm = (targetRelPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        var dir = Path.GetDirectoryName(norm) ?? "";
        var name = Path.GetFileName(norm);
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var f in FindSimilarFiles(norm, projectRoot))
        {
            var fNorm = f.Replace('\\', '/');
            var fDir = Path.GetDirectoryName(fNorm) ?? "";
            if (string.Equals(fDir, dir, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fNorm).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }
        return null;
    }

    public static string? ExtractTargetPath(string changeDesc, string currentRelPath, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(changeDesc)) return null;
        var m = Regex.Match(changeDesc, @"(?:\s+to\s+|[ \t]*[→\u2192][ \t]*)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var after = changeDesc[(m.Index + m.Length)..].Trim().Trim(' ', '"', '\'');
        if (string.IsNullOrWhiteSpace(after)) return null;
        var dir = Path.GetDirectoryName(currentRelPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var target = after.Contains('/') || after.Contains('\\')
            ? after.Replace('\\', '/')
            : (string.IsNullOrEmpty(dir) ? after : dir.Replace('\\', '/') + "/" + after);
        return string.IsNullOrWhiteSpace(target) || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ? null : target;
    }

    public static string BuildDiscoveryTextFromSteps(List<object> steps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ONLY use paths that appear below. Do NOT invent paths.");
        sb.AppendLine();
        foreach (var item in steps)
        {
            if (item is not Dictionary<string, object?> r) continue;
            if (!r.TryGetValue("output", out var output) || output == null || string.IsNullOrEmpty(output.ToString())) continue;
            sb.AppendLine($"### {r.GetValueOrDefault("type")} {r.GetValueOrDefault("path") ?? r.GetValueOrDefault("description")}");
            sb.AppendLine($"```\n{output}\n```");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Words that never make a symbol even when joined with hyphens
    /// ("create-a-folder" is prose-with-hyphens, not a key file/method). Applies ONLY to
    /// kebab-case tokens — snake_case/camelCase/dotted tokens are code conventions and
    /// are always kept ("or_this" is a variable even though "or" + "this" are words).</summary>
    private static readonly HashSet<string> IdentifierStopwordParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","and","or","but","in","on","at","to","for","of","with","from",
        "into","onto","upon","this","that","it","its","my","your","our","their","his","her",
        "is","are","was","were","be","been","being","have","has","had","do","does","did",
        "will","would","should","could","may","might","shall","can","let","not","no","any","all",
        "create","creates","creating","created","add","adding","adds","added","make","making","makes","made",
        "fix","fixes","fixed","new","old","more","less","some","just","very","really",
        "set","get","put","use","using","used","show","hide","please","need","want",
        "try","look","see","then","when","where","how","why","what","which","who",
        "out","up","down","over","under","so","if","else",
        "folder","folders","file","files","directory","directories","desktop","project",
        "root","path","page","name","thing","things","element","button","panel","section"
    };

    /// <summary>
    /// Extracts identifier-shaped tokens from a task prompt — snake_case (or_this),
    /// kebab-case (things-like-this), camelCase/PascalCase (CreateDirectory) and dotted
    /// file names (web_searcher.py). Such tokens are usually the KEY file, method, or
    /// variable the task is about, so discovery searches the repo for them EXACTLY
    /// instead of relying on word-splitting BM25 (which shatters them into generic
    /// parts like "or" + "this" and matches nothing). Returns up to 12 deduped tokens.
    /// </summary>
    public static List<string> ExtractIdentifierTokens(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return new List<string>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string tok, bool dropIfProse = false)
        {
            if (tok.Length < 4 || tok.All(char.IsDigit)) return;
            // Prose filter applies only to hyphenated tokens — "create-a-folder" is
            // prose-with-hyphens, not a symbol. Underscore/case/dot tokens are code
            // conventions and are never dropped here ("or_this" stays).
            if (dropIfProse)
            {
                var parts = Regex.Split(tok, @"[_\-\.]").Where(p => p.Length > 0).ToList();
                if (parts.Count > 1 && parts.All(p => IdentifierStopwordParts.Contains(p))) return;
            }
            found.Add(tok);
        }
        // snake_case, incl. leading-underscore privates like _create_directory
        foreach (Match m in Regex.Matches(prompt, @"\b_?[a-z][a-z0-9]*(?:_[a-z0-9]+){1,}\b"))
            Add(m.Value);
        // kebab-case — also matches CSS class names (prose-with-hyphens is filtered out)
        foreach (Match m in Regex.Matches(prompt, @"\b[a-z][a-z0-9]*(?:-[a-z0-9]+){1,}\b"))
            Add(m.Value, dropIfProse: true);
        // camelCase / PascalCase with an internal case boundary
        foreach (Match m in Regex.Matches(prompt, @"\b[A-Za-z][A-Za-z0-9]*(?:[A-Z][a-z0-9]+){1,}\b"))
            Add(m.Value);
        // dotted file-ish tokens (web_searcher.py, index.html) — skip version-like
        // (v1.2.3, 8.0, 1.x: either the extension or the stem is purely numeric)
        foreach (Match m in Regex.Matches(prompt, @"\b[A-Za-z0-9_][A-Za-z0-9_.-]*\.[a-z0-9]{1,8}\b"))
        {
            var tok = m.Value;
            var ext = Path.GetExtension(tok);
            var stem = tok[..^ext.Length];
            if (!string.IsNullOrEmpty(ext) && ext.TrimStart('.').All(char.IsDigit)) continue;
            if (stem.All(char.IsDigit)) continue;
            Add(tok);
        }
        return found.OrderByDescending(t => t.Length).Take(12).ToList();
    }

    /// <summary>Files at or above this many characters get focused region extraction for
    /// identifier matches instead of a full read into the discovery context (a 50KB
    /// component shouldn't flood the prompt just because one symbol matched).</summary>
    public const int LargeFileFocusThresholdChars = 20_000;

    /// <summary>The focus threshold never drops below this, even under extreme context
    /// pressure — below it, even a hot run keeps files whole rather than over-slicing.</summary>
    public const int LargeFileFocusThresholdFloor = 4_000;

    private static readonly HashSet<string> BraceLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".c", ".h", ".cpp", ".hpp",
        ".java", ".go", ".rs", ".php", ".kt", ".kts", ".swift", ".scala", ".fs", ".fsx"
    };
    private static readonly HashSet<string> IndentLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyw", ".rb", ".sh", ".ps1", ".bat", ".cmd", ".yml", ".yaml"
    };

    /// <summary>
    /// When an identifier from the task prompt matches inside a LARGE file, extract just the
    /// enclosing method/class/block around each match instead of dumping the whole file into
    /// the discovery context — like GrepProjectForDefinitionAsync, but batched for all
    /// identifiers at once. Brace languages (.cs/.ts/.js/…) get brace-balanced method/class
    /// regions, indentation languages (.py/.sh/…) get their def/block scope, everything else
    /// (.html/.css/.json/…) gets a window around the match. Returns "" when nothing matched.
    /// </summary>
    public static string ExtractIdentifierRegions(
        string fileContent, List<string> identifiers, string? fileExt,
        int maxRegions = 3, int maxCharsPerRegion = 3500)
    {
        if (string.IsNullOrWhiteSpace(fileContent) || identifiers == null || identifiers.Count == 0)
            return "";
        var lines = fileContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var ext = (fileExt ?? "").ToLowerInvariant();
        var isBrace = BraceLanguages.Contains(ext);
        var isIndent = IndentLanguages.Contains(ext);
        var afterDepth = new int[lines.Length];
        if (isBrace)
        {
            var depth = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                depth += CountBraces(lines[i]);
                afterDepth[i] = depth;
            }
        }
        var regions = new List<(int start, int end, string id)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in identifiers)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            var matchLine = FindIdentifierLine(lines, id);
            if (matchLine < 0) continue;
            if (regions.Any(r => matchLine >= r.start && matchLine <= r.end)) continue;
            var (start, end) = isBrace ? ExpandBraceBlock(lines, matchLine, afterDepth)
                           : isIndent ? ExpandIndentBlock(lines, matchLine)
                           : (Math.Max(0, matchLine - 10), Math.Min(lines.Length - 1, matchLine + 24));
            regions.Add((start, end, id));
            if (regions.Count >= maxRegions) break;
        }
        if (regions.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var (start, end, id) in regions.OrderBy(r => r.start))
        {
            var text = string.Join("\n", lines.Skip(start).Take(end - start + 1));
            if (text.Length > maxCharsPerRegion)
                text = text[..maxCharsPerRegion] + "\n// ... (region truncated)";
            sb.AppendLine($"// ▼ '{id}' — lines {start + 1}–{end + 1}");
            sb.AppendLine(text);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

private static int CountBraces(string line)
    {
        // Skip quoted spans ("...", '...', `...`) so a brace inside a string or
        // template literal can't fake-close the region (e.g. C# string.Format("{0}")).
        var open = 0; var close = 0;
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != null)
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) quote = null;
                continue;
            }
            if (c is '"' or '\'' or '`') { quote = c; continue; }
            if (c == '{') open++;
            else if (c == '}') close++;
        }
        return open - close;
    }

    private static int FindIdentifierLine(string[] lines, string id)
    {
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(id, StringComparison.Ordinal)) return i;
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static (int start, int end) ExpandBraceBlock(string[] lines, int matchLine, int[] afterDepth)
    {
        // Start at the nearest '{' at-or-before the match line (the enclosing block's opener).
        var start = -1;
        for (var i = matchLine; i >= 0; i--)
            if (lines[i].Contains('{')) { start = i; break; }
        if (start < 0 || afterDepth[start] <= 0)
            return (Math.Max(0, matchLine - 10), Math.Min(lines.Length - 1, matchLine + 24));
        // Climb to the defining method/class: when the identifier sits inside a mere
        // if/loop/object-literal, prefer the enclosing method/class region the task
        // actually cares about, not the innermost block. Stops at the first block whose
        // opener is a declaration (or whose line above it is one).
        for (var climb = 0; climb < 6 && start > 0 && !IsBlockDefiningRegion(lines, start, afterDepth); climb++)
        {
            var targetDepth = afterDepth[start] - 1;
            var nextStart = -1;
            for (var i = start - 1; i >= 0; i--)
            {
                if (lines[i].Contains('{') && afterDepth[i] <= targetDepth) { nextStart = i; break; }
            }
            if (nextStart < 0) break;
            start = nextStart;
        }
        // Include the declaration line that owns the '{' ("public void Foo(...)" above "{").
        if (start > 0 && IsDeclarationLine(lines[start - 1])) start--;
        var outerDepth = start > 0 ? afterDepth[start - 1] : 0;
        var end = matchLine;
        for (var i = start + 1; i < lines.Length; i++)
        {
            end = i;
            if (afterDepth[i] <= outerDepth && lines[i].Contains('}')) break;
        }
        if (end - start > 160) end = Math.Min(lines.Length - 1, start + 160);
        return (start, end);
    }

    /// <summary>True when the block opened at <paramref name="braceLine"/> is a method/class/
    /// function definition (its opener line is a declaration, or the line above it is) —
    /// as opposed to a control-flow or object-literal block the region should climb past.</summary>
    private static bool IsBlockDefiningRegion(string[] lines, int braceLine, int[] afterDepth)
    {
        if (braceLine > 0 && IsDeclarationLine(lines[braceLine - 1])) return true;
        var t = lines[braceLine].Trim();
        if (Regex.IsMatch(t, @"^(if|for|while|foreach|switch|catch|using|lock|try|do|else|finally)\b", RegexOptions.IgnoreCase))
            return false;
        return Regex.IsMatch(t, @"\b(class|interface|struct|enum|record|function|void|namespace)\b", RegexOptions.IgnoreCase)
            || t.Contains("=>")
            || (t.EndsWith("{") && Regex.IsMatch(t, @"\)\s*\{\s*$"));
    }

    private static bool IsDeclarationLine(string line)
    {
        var t = line.Trim();
        if (string.IsNullOrWhiteSpace(t) || t.EndsWith("{") || t.EndsWith(";")) return false;
        if (t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith("*") || t.StartsWith("#")) return false;
        return Regex.IsMatch(t, @"^(public|private|protected|internal|static|readonly|async|function|class|interface|record|struct|enum|namespace|export|const|let|var)\b", RegexOptions.IgnoreCase)
            || t.EndsWith(":") || t.EndsWith("=>");
    }

    private static (int start, int end) ExpandIndentBlock(string[] lines, int matchLine)
    {
        var indent = IndentOf(lines[matchLine]);
        var start = matchLine;
        while (start > 0)
        {
            var prev = lines[start - 1];
            var prevTrimmed = prev.TrimStart();
            if (string.IsNullOrWhiteSpace(prevTrimmed)) break;
            var prevIndent = IndentOf(prev);
            if (prevIndent < indent && prevTrimmed.EndsWith(":")) { start--; break; }
            if (prevIndent < indent) break; // dedent without ':' — the match isn't inside a block
            start--;
        }
        var end = matchLine;
        while (end + 1 < lines.Length)
        {
            var next = lines[end + 1];
            if (string.IsNullOrWhiteSpace(next.Trim())) break; // blank line ends the block view
            if (IndentOf(next) < indent) break;
            end++;
        }
        if (end - start > 160) end = Math.Min(lines.Length - 1, start + 160);
        return (start, end);
    }

    private static int IndentOf(string line)
    {
        var n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
        return n;
    }

    public static List<string> ExtractMeaningfulKeywords(string lower)
    {
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","and","or","but","in","on","at","to","for","of","with","from",
            "into","onto","upon","after","before","about","above","below","between",
            "this","that","it","its","their","our","my","your","his","her","we","they","i",
            "is","are","was","were","be","been","being","have","has","had",
            "do","does","did","will","would","should","could","may","might","shall",
            "make","making","makes","made",
            "fix","fixing","fixes","fixed",
            "add","adding","adds","added",
            "change","changing","changes","changed",
            "update","updating","updates","updated",
            "edit","editing","edits","edited",
            "modify","modifying","modifies","modified",
            "create","creating","creates","created",
            "delete","deleting","deletes","deleted",
            "remove","removing","removes","removed",
            "set","get","put","use","using","used",
            "show","hide","display","handle",
            "more","less","some","any","all","no","not","also","very","just",
            "nice","nicely","good","better","best","new","old","right","left",
            "please","sure","now","then","when","where","how","why","what","which","who",
            "out","up","down","so","if","else","really","quite","bit","little","lot",
            "need","want","should","must","can","let","help","try","look","see"
        };
        return Regex.Matches(lower, @"\b[a-z]{3,}\b")
            .Select(m => m.Value)
            .Where(w => !stopwords.Contains(w))
            .Distinct()
            .Take(10)
            .ToList();
    }

    public static List<string> ApplyTaskTypeHeuristics(string prompt, List<string> allFiles)
    {
        var lower = prompt.ToLowerInvariant();
        var isStyleTask = Regex.IsMatch(lower, @"\b(style|css|color|theme|layout|spacing|font|design|ui|ux|look|appear|brand|visual|margin|padding|border|shadow|panel|card)\b");
        var isHtmlTask = Regex.IsMatch(lower, @"\b(html|template|page|view|markup|modal|popup|section|div)\b");
        var isJsTask = Regex.IsMatch(lower, @"\b(javascript|script|function|event|click|toggle|show|hide|angular|react|vue|component|state|behavior)\b");
        var isBackendTask = Regex.IsMatch(lower, @"\b(api|endpoint|controller|service|database|model|route|logic|backend|server|c#|csharp|dotnet)\b");
        var isConfigTask = Regex.IsMatch(lower, @"\b(config|setting|option|appsettings|environment|json)\b");
        var meaningfulKeywords = ExtractMeaningfulKeywords(lower);
        var scored = allFiles.Select(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var nameLow = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            var pathLow = f.ToLowerInvariant();
            var score = 0;
            if (isStyleTask)
            {
                if (ext is ".css" or ".scss" or ".sass" or ".less") score += 120;
                else if (ext is ".html" or ".htm") score += 60;
                else if (ext is ".js" or ".ts") score += 20;
            }
            if (isHtmlTask)
            {
                if (ext is ".html" or ".htm") score += 120;
                else if (ext is ".css" or ".scss") score += 50;
                else if (ext is ".js" or ".ts") score += 30;
            }
            if (isJsTask)
            {
                if (ext is ".js" or ".ts" or ".jsx" or ".tsx") score += 120;
                else if (ext is ".html" or ".htm") score += 40;
            }
            if (isBackendTask)
            {
                if (ext == ".cs") score += 120;
                else if (ext == ".json") score += 30;
            }
            if (isConfigTask)
            {
                if (ext is ".json" or ".yaml" or ".yml") score += 120;
            }
            foreach (var kw in meaningfulKeywords)
                if (nameLow.Contains(kw))
                    score += 50;
            if ((isStyleTask || isHtmlTask || isJsTask) && pathLow.StartsWith("wwwroot/"))
                score += 25;
            if (nameLow.Contains("agentcontroller")) score -= 200;
            if (nameLow == "filehints") score -= 200;
            if (pathLow.EndsWith(".min.js")) score -= 300;
            if (pathLow.EndsWith(".min.css")) score -= 300;
            if (ext is ".dll" or ".exe" or ".pdb" or ".nupkg" or ".lock" or ".sum")
                score -= 1000;
            return (file: f, score);
        })
        .Where(x => x.score > 0)
        .OrderByDescending(x => x.score)
        .Take(50)
        .Select(x => x.file)
        .ToList();
        if (scored.Count == 0)
        {
            scored = allFiles
                .Where(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return name is "index" or "app" or "main" or "program" or "startup"
                                or "styles" or "global" or "layout"
                        && ext is ".html" or ".js" or ".ts" or ".css" or ".cs";
                })
                .Take(10)
                .ToList();
        }
        return scored;
    }

    /// <summary>
    /// Large files (≥ LargeFileFocusThresholdChars) that an identifier from the task
    /// prompt matched INSIDE get a focused read — just the enclosing method/class/block
    /// around each match, with line numbers — so one stray symbol hit can't flood the
    /// prompt with an unrelated 50KB component. Small files and identifier-in-path
    /// matches stay whole. Shared by the bootstrap auto-read, the _discover tool, and
    /// the _explore pipeline.
    /// Returns the snippet to surface and the identifier list for the "(focused: ...)"
    /// header (null when the full read stays).
    /// </summary>
    public static (string snippet, string? focusIds) FocusLargeFileRead(
        string output, List<string> identifierTokens, string path, int? thresholdChars = null)
    {
        var threshold = thresholdChars ?? LargeFileFocusThresholdChars;
        if (output.Length >= threshold && identifierTokens.Count > 0)
        {
            var regionText = ExtractIdentifierRegions(output, identifierTokens, Path.GetExtension(path));
            if (!string.IsNullOrWhiteSpace(regionText))
            {
                var focusIds = string.Join(", ", identifierTokens
                    .Where(id => output.Contains(id, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4));
                // Defensive: a non-empty region with zero matched identifiers would render
                // a broken "(focused: ; ...)" header — never claim a focus without ids.
                if (focusIds.Length > 0) return (regionText, focusIds);
            }
        }
        return (output, null);
    }

    /// <summary>
    /// Effective focus threshold when the discovery context is running hot on chars.
    /// Pressure is the fraction of the auto-read context budget already consumed (0 = plenty
    /// of room, 1 = at the edge). High pressure shrinks the threshold so more large files
    /// contribute focused regions instead of being dropped whole. Pure — never mutates
    /// shared state, so concurrent agent runs can't interfere (a mutable static threshold
    /// would race across SSE streams). Tuning is applied in the bootstrap auto-read only:
    /// it is the one read phase with an aggregate budget that drops files, so a lower
    /// threshold elsewhere (_discover/_explore never drop files) would change nothing.
    /// </summary>
    public static int FocusThresholdForPressure(double pressure)
    {
        if (pressure <= 0) return LargeFileFocusThresholdChars;
        var scaled = (int)(LargeFileFocusThresholdChars * (1 - Math.Min(pressure, 1.0) * 0.8));
        return Math.Max(LargeFileFocusThresholdFloor, scaled);
    }

    /// <summary>
    /// Hot-context re-focus decision for the bootstrap auto-read: when a file's full read
    /// (or its already-focused region) won't fit the remaining aggregate budget, try
    /// shrinking the focus threshold so the file still contributes its key regions instead
    /// of being dropped. Returns true only when a fitting re-focus happened (the caller
    /// should then use the out values and continue the loop); false means drop/stop.
    /// Pure — safe across concurrent agent runs.
    /// </summary>
    public static bool TryRefocusHotFile(
        string output, List<string> identifierTokens, string path,
        bool alreadyFocused, int currentSnippetLength, long usedChars, long totalBudget,
        int effectiveThreshold,
        out string newSnippet, out string? newFocusIds, out int newThreshold)
    {
        newSnippet = "";
        newFocusIds = null;
        newThreshold = effectiveThreshold;
        if (alreadyFocused) return false; // already a region — can't shrink further
        var pressure = totalBudget > 0 ? (double)usedChars / totalBudget : 0;
        var candidate = FocusThresholdForPressure(pressure);
        if (output.Length < candidate || candidate > effectiveThreshold) return false;
        var refocus = FocusLargeFileRead(output, identifierTokens, path, candidate);
        if (refocus.focusIds == null || usedChars + refocus.snippet.Length > totalBudget) return false;
        newSnippet = refocus.snippet;
        newFocusIds = refocus.focusIds;
        newThreshold = candidate;
        return true;
    }

    /// <summary>
    /// Attaches focused-region metadata (focused/focusIds/focusedOutput) to completed
    /// "read" step results, using the same reader the context-building call sites use,
    /// so the SSE step events surface the region to the UI. Pure mutation over the
    /// results array — unit-testable without SSE/Response plumbing.
    /// </summary>
    public static void AttachFocusedRegions(
        Dictionary<string, object?>[] results,
        IReadOnlyList<AgentStep> steps,
        Func<string, List<string>, string, (string snippet, string? focusIds)>? focusReader,
        List<string>? focusTokens)
    {
        if (focusReader == null) return;
        var tokens = focusTokens ?? new List<string>();
        for (var i = 0; i < results.Length && i < steps.Count; i++)
        {
            if (!steps[i].Type.Equals("read", StringComparison.OrdinalIgnoreCase)) continue;
            var output = results[i].GetValueOrDefault("output")?.ToString();
            var path = results[i].GetValueOrDefault("path")?.ToString();
            if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(path)) continue;
            var (snippet, focusIds) = focusReader(output, tokens, path);
            if (focusIds != null)
            {
                results[i]["focused"] = true;
                results[i]["focusIds"] = focusIds;
                results[i]["focusedOutput"] = snippet;
            }
        }
    }

    public static string ExtractRelevantExcerpt(string fileContent, string changeDesc, string? planOldString, int fileBodyTruncation = 8000, string? fileExt = null)
    {
        const int RadiusLines = 60;
        var lines = fileContent.Split('\n');
        var ext = (fileExt ?? "").ToLowerInvariant();
        var structEnd = 0;
        var foundClassLine = -1;
        for (var i = 0; i < Math.Min(lines.Length, 100); i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                structEnd = i + 1;
                continue;
            }
            if (Regex.IsMatch(trimmed, @"^(using|import|namespace|package|from|export|#include|@|\[)", RegexOptions.IgnoreCase))
            {
                structEnd = i + 1;
                continue;
            }
            if (Regex.IsMatch(trimmed, @"\b(class|interface|struct|record|enum|function|void)\b", RegexOptions.IgnoreCase))
            {
                foundClassLine = i;
                structEnd = i + 1;
                if (i + 1 < lines.Length && lines[i + 1].Trim() == "{") structEnd = i + 2;
                break;
            }
            if (foundClassLine == -1 && i > 50) break;
        }
        if (foundClassLine >= 0) structEnd = Math.Max(structEnd, foundClassLine + 1);
        var targetStart = -1;
        var targetEnd = -1;
        if (!string.IsNullOrWhiteSpace(planOldString))
        {
            var anchor = planOldString.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length >= 8);
            if (anchor != null)
            {
                for (var i = structEnd; i < lines.Length; i++)
                {
                    if (!lines[i].Contains(anchor, StringComparison.OrdinalIgnoreCase)) continue;
                    targetStart = Math.Max(structEnd, i - 15);
                    targetEnd = Math.Min(lines.Length, i + planOldString.Split('\n').Length + RadiusLines);
                    break;
                }
            }
        }
        if (targetStart < 0)
        {
            var keywords = ExtractMeaningfulKeywords(changeDesc.ToLowerInvariant())
                .Where(kw => kw.Length >= 5)
                .OrderByDescending(kw => kw.Length)
                .ToList();
            var anchors = ExtractAnchorsByFileType(changeDesc, ext);
            if (keywords.Count > 0 || anchors.Count > 0)
            {
                var bestLine = -1;
                var bestScore = 0;
                for (var i = structEnd; i < lines.Length; i++)
                {
                    var lineLow = lines[i].ToLowerInvariant();
                    var score = 0;
                    // Structural anchors (file-type-specific) — highest priority
                    foreach (var (name, multiplier) in anchors)
                    {
                        if (lineLow.Contains(name, StringComparison.Ordinal))
                            score += name.Length * name.Length * multiplier;
                    }
                    // General keyword scoring
                    foreach (var kw in keywords)
                    {
                        if (!lineLow.Contains(kw, StringComparison.Ordinal)) continue;
                        score += kw.Length * kw.Length;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestLine = i;
                    }
                }
                if (bestLine >= 0)
                {
                    targetStart = Math.Max(structEnd, bestLine - 20);
                    targetEnd = Math.Min(lines.Length, bestLine + RadiusLines);
                }
            }
        }
        var header = string.Join('\n', lines.Take(structEnd));
        if (targetStart < 0)
        {
            var bodySkeleton = GetSkeletonForRange(lines, structEnd, lines.Length);
            var fullFile = string.Join('\n', lines);

            var result2 = new StringBuilder();
            result2.AppendLine(header);
            result2.AppendLine("// --- SKELETON (no excerpt found) ---");
            result2.AppendLine(bodySkeleton);
            result2.AppendLine("// --- FULL FILE (fallback) ---");
            result2.AppendLine(fullFile);

            return result2.ToString();
        }

        var preSkeleton = GetSkeletonForRange(lines, structEnd, targetStart);
        var excerpt = string.Join('\n', lines.Skip(targetStart).Take(targetEnd - targetStart));
        var postSkeleton = GetSkeletonForRange(lines, targetEnd, lines.Length);
        var result = new StringBuilder();
        result.AppendLine(header);

        if (!string.IsNullOrWhiteSpace(preSkeleton))
        {
            result.AppendLine("// --- PRE-SKELETON ---");
            result.AppendLine(preSkeleton);
        }

        result.AppendLine("// --- EXCERPT ---");
        result.AppendLine(excerpt);

        if (!string.IsNullOrWhiteSpace(postSkeleton))
        {
            result.AppendLine("// --- POST-SKELETON ---");
            result.AppendLine(postSkeleton);
        }

        result.AppendLine("// --- FULL FILE (always included) ---");
        result.AppendLine(string.Join('\n', lines));

        return result.ToString();
    }
    /// <summary>
    /// Extracts structural anchor names from a change description based on file type,
    /// paired with a bonus multiplier reflecting how precisely the anchor identifies a location.
    /// Higher multiplier = more specific = wins over generic keyword matches.
    ///
    /// .css/.scss   → class/id selector names (.toolBtn, #header)         multiplier 4
    /// .cs          → method/class names (PascalCase identifiers)          multiplier 4
    /// .ts/.tsx     → method/component names (camelCase/PascalCase)        multiplier 4
    /// .js/.jsx     → function names                                        multiplier 4
    /// .html/.cshtml→ element ids, Angular directives, component selectors multiplier 4
    /// .sql         → table/procedure/function names                        multiplier 4
    /// .json        → top-level key names                                   multiplier 3
    /// fallback     → any CamelCase/camelCase identifier ≥ 4 chars         multiplier 2
    /// </summary>

    internal static List<(string name, int multiplier)> ExtractAnchorsByFileType(string changeDesc, string ext)
    {
        var results = new List<(string name, int multiplier)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, int mult)
        {
            var low = name.ToLowerInvariant();
            if (low.Length >= 4 && seen.Add(low)) results.Add((low, mult));
        }
        switch (ext)
        {
            case ".css":
            case ".scss":
            case ".sass":
            case ".less":
                // Explicit .class / #id selectors in the description
                foreach (Match m in Regex.Matches(changeDesc, @"[.#]([A-Za-z][A-Za-z0-9_-]{2,})"))
                    Add(m.Groups[1].Value, 5);
                // Bare class-like names: camelCase or PascalCase or kebab-case words mentioned in description
                // e.g. "toolBtn", "tool-btn", "paintToolbar"
                foreach (Match m in Regex.Matches(changeDesc, @"\b([A-Za-z][A-Za-z0-9]*(?:[A-Z][a-z0-9]+)+|[a-z][a-z0-9]*(?:-[a-z0-9]+)+)\b"))
                    Add(m.Groups[1].Value, 4);
                // Also plain words that look like class names (≥6 chars, alphabetic)
                foreach (Match m in Regex.Matches(changeDesc, @"\b([A-Za-z]{6,})\b"))
                    Add(m.Groups[1].Value, 2);
                break;
            case ".cs":
                // PascalCase identifiers — method names, class names, property names
                foreach (Match m in Regex.Matches(changeDesc, @"\b([A-Z][a-z][A-Za-z0-9]{2,})\b"))
                    Add(m.Groups[1].Value, 4);
                // async Task / method signatures hint
                foreach (Match m in Regex.Matches(changeDesc, @"\b([A-Za-z][A-Za-z0-9]{3,})\s*\("))
                    Add(m.Groups[1].Value, 5);
                break;
            case ".ts":
            case ".tsx":
                // camelCase method names and PascalCase component names
                foreach (Match m in Regex.Matches(changeDesc, @"\b([a-z][a-zA-Z0-9]{3,}|[A-Z][a-zA-Z0-9]{3,})\b"))
                    Add(m.Groups[1].Value, 4);
                // Angular decorator hints (@Component selector, ngIf, etc.)
                foreach (Match m in Regex.Matches(changeDesc, @"\b(ng[A-Z][A-Za-z]+|@[A-Z][A-Za-z]+)\b"))
                    Add(m.Groups[1].Value.TrimStart('@'), 5);
                break;
            case ".js":
            case ".jsx":
                // function names — camelCase
                foreach (Match m in Regex.Matches(changeDesc, @"\b([a-z][a-zA-Z0-9]{3,})\s*\("))
                    Add(m.Groups[1].Value, 5);
                foreach (Match m in Regex.Matches(changeDesc, @"\b([a-z][a-zA-Z0-9]{3,})\b"))
                    Add(m.Groups[1].Value, 3);
                break;
            case ".html":
            case ".cshtml":
            case ".razor":
            case ".vue":
            case ".svelte":
                // HTML element id/class attributes, Angular *ngIf section names
                foreach (Match m in Regex.Matches(changeDesc, @"\bid=""([^""]+)""|\bclass=""([^""]+)"""))
                    Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, 5);
                // Angular structural directive values e.g. *ngIf="selectedTab === 'users'"
                foreach (Match m in Regex.Matches(changeDesc, @"'([A-Za-z][A-Za-z0-9_-]{2,})'|""([A-Za-z][A-Za-z0-9_-]{2,})"""))
                    Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, 5);
                // Component selectors and plain identifiers
                foreach (Match m in Regex.Matches(changeDesc, @"\b([A-Za-z][A-Za-z0-9]{3,})\b"))
                    Add(m.Groups[1].Value, 3);
                break;
            case ".sql":
                // Table/procedure/function names — usually UPPER_CASE or PascalCase in SQL
                foreach (Match m in Regex.Matches(changeDesc, @"\b([A-Za-z][A-Za-z0-9_]{3,})\b"))
                    Add(m.Groups[1].Value, 4);
                // SQL keywords as context anchors (SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER)
                foreach (Match m in Regex.Matches(changeDesc, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|TABLE|PROCEDURE|FUNCTION)\b", RegexOptions.IgnoreCase))
                    Add(m.Groups[1].Value, 3);
                break;
            case ".json":
            case ".jsonc":
                // Top-level key names in quotes
                foreach (Match m in Regex.Matches(changeDesc, @"""([A-Za-z][A-Za-z0-9_]{2,})"""))
                    Add(m.Groups[1].Value, 3);
                foreach (Match m in Regex.Matches(changeDesc, @"\b([A-Za-z][A-Za-z0-9_]{3,})\b"))
                    Add(m.Groups[1].Value, 2);
                break;
            default:
                // Fallback: any CamelCase or camelCase identifier ≥ 4 chars
                foreach (Match m in Regex.Matches(changeDesc, @"\b([A-Za-z][a-zA-Z0-9]{3,})\b"))
                    Add(m.Groups[1].Value, 2);
                break;
        }
        // De-noise: remove pure stopwords that slipped through
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "this","that","with","from","file","code","line","edit","step","make",
            "have","been","will","should","would","could","must","into","also",
            "them","they","then","when","what","your","their","there","increase",
            "decrease","change","update","modify","replace","remove","delete",
            "class","style","selector","element","function","method","property"
        };
        results.RemoveAll(r => stopwords.Contains(r.name));
        // Sort by multiplier desc, then length desc for tie-breaking
        results.Sort((a, b) => b.multiplier != a.multiplier
            ? b.multiplier.CompareTo(a.multiplier)
            : b.name.Length.CompareTo(a.name.Length));
        return results;
    }

    public static List<string> ExtractDisambiguationKeywords(string? changeDesc)
    {
        if (string.IsNullOrWhiteSpace(changeDesc)) return new List<string>();
        var stopWords = new HashSet<string> {
        "from", "remove", "delete", "update", "method", "function", "class",
        "property", "field", "variable", "code", "block", "line", "target",
        "change", "modify", "replace", "insert", "create", "implement",
        "ensure", "make", "file", "edit", "add", "element", "span", "div"
    };
        return Regex.Matches(changeDesc.ToLowerInvariant(), @"\b[a-z]{4,}\b")
            .Select(m => m.Value)
            .Where(w => !stopWords.Contains(w))
            .Distinct()
            .ToList();
    }
}
