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
public static class AgentTextUtilities
{
    public static string StripSpuriousBlankLines(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code;
        var lines = code.Split('\n');
        if (lines.Length < 6) return code;
        var codeCount = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        var blankCount = lines.Count(l => string.IsNullOrWhiteSpace(l));
        if (codeCount < 3 || blankCount < codeCount * 0.7) return code;
        var alternating = 0;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) &&
                string.IsNullOrWhiteSpace(lines[i + 1]))
                alternating++;
        }
        if (alternating < codeCount * 0.5) return code;
        var result = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                var hasPrev = result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]);
                var hasNext = i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]);
                if (hasPrev && hasNext)
                {
                    var prevTrimmed = result[^1].TrimEnd();
                    var prevIndent = result[^1].TakeWhile(c => c == ' ' || c == '\t').Count();
                    var nextIndent = lines[i + 1].TakeWhile(c => c == ' ' || c == '\t').Count();
                    if ((prevTrimmed.EndsWith(';') || prevTrimmed.EndsWith('}')) &&
                        Math.Abs(prevIndent - nextIndent) <= 1 &&
                        (i == 0 || i - 1 < 0 || string.IsNullOrWhiteSpace(lines[i - 1]) == false))
                    {
                        // Check if line before prev was also blank — if so, skip
                        if (result.Count > 1 && string.IsNullOrWhiteSpace(result[^2]))
                            continue;
                        result.Add(lines[i]);
                        continue;
                    }
                    continue; // Skip spurious blank
                }
            }
            result.Add(lines[i]);
        }
        return string.Join("\n", result);
    }

    public static string CleanVerbatimStringEscapes(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        var regex = new Regex(@"@""(?:""|[^""])*""", RegexOptions.Compiled);
        bool changed = false;
        var result = regex.Replace(content, match =>
        {
            var val = match.Value;
            var inside = val.Substring(2, val.Length - 3);
            bool hasEscapeSeq = inside.Contains(@"\r\n") || inside.Contains(@"\r") || inside.Contains(@"\n") || inside.Contains(@"\t");
            bool looksLikeSql = Regex.IsMatch(inside, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE\s+TABLE|ALTER\s+TABLE|DROP\s+TABLE|FROM|WHERE|JOIN|VALUES|SET)\b", RegexOptions.IgnoreCase);
            if (hasEscapeSeq && looksLikeSql)
            {
                changed = true;
                var fixedInside = inside
                    .Replace(@"\r\n", "\r\n")
                    .Replace(@"\r", "\r")
                    .Replace(@"\n", "\n")
                    .Replace(@"\t", "\t");
                return "@\"" + fixedInside + "\"";
            }
            return val;
        });
        return changed ? result : content;
    }
    // Keywords that can precede a `( ... ) { }` shape but are NOT method declarations.
    // Matching is whole-line anchored, so `new Foo() { }` never matches anyway.

    public static string PostEditCSharpFixup(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        content = CleanVerbatimStringEscapes(content);
        var flatPattern = new Regex(@"\.(SystemSpecs|System|HardwareInfo|Hardware|Specs|SystemInfo|MetaInfo|Details|DataInfo|BenchmarkInfo|BenchData)\??\.([A-Z]\w+)", RegexOptions.IgnoreCase);
        content = flatPattern.Replace(content, m => "." + m.Groups[2].Value);
        content = Regex.Replace(content, @"(\$""[^""]*)\{\{(\w+(?:\.\w+)+)\}\}([^""]*"")", "$1{$2}$3");
        content = Regex.Replace(content,
            @"decimal\.TryParse\s*\(\s*\w+\.Score\??(?:\.Replace\s*""[^""]*""(?:\s*,\s*""[^""]*"")?)?\s*,(\s*out\s+\w+(?:\.\w+)*\s*)\)",
            m =>
            {
                var outVar = m.Groups[1].Value.Trim();
                return $"decimal.TryParse(benchmark.Score, {outVar})";
            });
        content = Regex.Replace(content,
            @"(?<=[^ \t\r\n@])""\s*\r?\n[ \t]*;",
            @""";");
        return content;
    }

    public static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n");

    /// <summary>
    /// Builds a bounded view of a large file for the post-execution verifier. When the file
    /// fits within <paramref name="maxChars"/> the whole content is returned unchanged.
    /// When it doesn't, the view keeps a bounded head, a window around each located anchor
    /// (the newString of each applied edit — so the verifier ALWAYS sees the region this run
    /// changed, even in a 40k-char stylesheet), and a bounded tail, each with explicit
    /// truncation markers. Anchors are matched verbatim after line-ending normalization,
    /// with a fallback to the anchor's longest distinctive line so an edit that was later
    /// reformatted or merged is still located. Falls back to head+tail when no anchor can be
    /// located (e.g. a full-file rewrite superseded the snippet).
    /// </summary>
    public static string BuildVerifierFileView(string content, IReadOnlyList<string>? anchors, int maxChars = 12000)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
            return content;
        var normalized = NormalizeLineEndings(content);
        var windows = new List<(int start, int end)>();
        if (anchors != null)
        {
            foreach (var anchor in anchors)
            {
                if (string.IsNullOrWhiteSpace(anchor)) continue;
                var normAnchor = NormalizeLineEndings(anchor).Trim('\r', '\n');
                if (normAnchor.Length == 0) continue;
                var idx = normalized.IndexOf(normAnchor, StringComparison.Ordinal);
                if (idx < 0)
                {
                    // An edit that was later reformatted/merged may no longer match verbatim —
                    // retry with its longest distinctive line (selector/method signature lines
                    // survive reformatting).
                    var bestLine = normAnchor.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length >= 20 && !l.StartsWith("//") && !l.StartsWith("/*") && !l.StartsWith("*"))
                        .OrderByDescending(l => l.Length)
                        .FirstOrDefault();
                    if (bestLine != null)
                        idx = normalized.IndexOf(bestLine, StringComparison.Ordinal);
                }
                if (idx >= 0)
                {
                    var windowStart = Math.Max(0, idx - 400);
                    var windowEnd = Math.Min(normalized.Length, idx + normAnchor.Length + 400);
                    windows.Add((windowStart, windowEnd));
                }
            }
        }
        if (windows.Count > 1)
        {
            windows.Sort((a, b) => a.start.CompareTo(b.start));
            var merged = new List<(int start, int end)> { windows[0] };
            foreach (var w in windows.Skip(1))
            {
                var last = merged[^1];
                if (w.start <= last.end) merged[^1] = (last.start, Math.Max(last.end, w.end));
                else merged.Add(w);
            }
            windows = merged;
        }
        const int HeadBudget = 3000;
        const int TailBudget = 2000;
        var regionBudget = Math.Max(0, maxChars - HeadBudget - TailBudget);
        var sb = new StringBuilder();
        var printedTo = 0;
        // Head: up to the budget, but stop before the first edited region so it is never cut.
        var headEnd = Math.Min(HeadBudget, windows.Count > 0 ? windows[0].start : normalized.Length);
        headEnd = Math.Min(headEnd, normalized.Length);
        if (headEnd > printedTo)
        {
            sb.Append(normalized[..headEnd]);
            printedTo = headEnd;
            if (printedTo < normalized.Length)
                sb.Append("\n… [TRUNCATED — head of file shown; edited regions and tail follow]");
        }
        // Edited regions: the change(s) this run made, with ±400 chars of context.
        var regionUsed = 0;
        foreach (var w in windows)
        {
            var start = w.start;
            var end = w.end;
            if (start < printedTo) start = printedTo;
            if (start >= end) continue;
            var allowed = Math.Max(0, regionBudget - regionUsed);
            if (allowed <= 0) break;
            var take = Math.Min(end - start, allowed);
            if (start > printedTo)
                sb.Append("\n… [EDITED REGION — the change(s) this run made to this file] …\n");
            sb.Append(normalized[start..(start + take)]);
            printedTo = Math.Max(printedTo, start + take);
            regionUsed += take;
            if (take < end - start)
                sb.Append("\n… [region truncated]");
        }
        // Tail: the last chars of the file, so end-of-file edits are still visible.
        var tailStart = Math.Max(printedTo, normalized.Length - TailBudget);
        if (tailStart < normalized.Length)
        {
            if (tailStart > printedTo)
                sb.Append("\n… [TAIL — end of file] …\n");
            sb.Append(normalized[tailStart..]);
        }
        sb.Append($"\n… [TRUNCATED — file is {content.Length} chars, showing head + edited regions + tail capped at {maxChars} chars]");
        return sb.ToString();
    }

    public static string StripLineLeadingWhitespace(string s)
    {
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimStart();
        return string.Join("\n", lines);
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "\n[Preview ended; omitted remainder is not code.]";

    public static string NormalizeUiStatus(string? status) => status switch
    {
        "written" or "ok" or "created" or "modified" => "done",
        "running" => "running",
        "error" => "error",
        _ => status ?? "pending"
    };

    public static string StripClassWrapper(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code;
        var lines = code.Split('\n').ToList();
        while (lines.Count > 0)
        {
            var trimmed = lines[0].Trim();
            if (trimmed.Length == 0 ||
                Regex.IsMatch(trimmed, @"^(export\s+)?(default\s+)?(abstract\s+)?class\s+\w+"))
            {
                lines.RemoveAt(0);
            }
            else break;
        }
        while (lines.Count > 0)
        {
            var trimmed = lines[^1].Trim();
            if (trimmed == "}" || trimmed.Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            else break;
        }
        return string.Join("\n", lines);
    }

    public static string UnescapeString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        return s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
    }

    public static string? DetectExcessiveBlankLines(string newStr)
    {
        var repaired = CollapseExcessiveBlankLines(newStr);
        if (repaired == newStr) return null;
        var lines = newStr.Split('\n');
        var blankLines = lines.Where(l => string.IsNullOrWhiteSpace(l)).ToList();
        var codeLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return $"EXCESSIVE BLANK LINES — newString has a blank line between nearly every code line " +
               $"({blankLines.Count} blank lines for {codeLines.Count} code lines). " +
               "Remove the spurious blank lines.";
    }

    public static string CollapseExcessiveBlankLines(string newStr)
    {
        if (string.IsNullOrWhiteSpace(newStr)) return newStr;
        var lines = newStr.Split('\n');
        if (lines.Length < 6) return newStr;
        var codeLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (codeLines.Count < 3) return newStr;
        var alternating = 0;
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) &&
                string.IsNullOrWhiteSpace(lines[i + 1]))
                alternating++;
        }
        if (alternating < codeLines.Count * 0.6) return newStr;

        var result = new List<string>();
        var lastWasBlank = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var isBlank = string.IsNullOrWhiteSpace(lines[i]);
            if (isBlank && lastWasBlank) continue;
            if (isBlank)
            {
                lastWasBlank = true;
                result.Add(lines[i]);
            }
            else
            {
                lastWasBlank = false;
                result.Add(lines[i]);
            }
        }
        return string.Join("\n", result);
    }

    public static string GetLeadingWhitespace(string s)
    {
        var i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s[..i];
    }

    public static string FixAngularAttributeCasing(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        // Angular structural directives
        content = Regex.Replace(content, @"\*ngif\b", "*ngIf", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"\*ngfor\b", "*ngFor", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"\*ngswitch\b", "*ngSwitch", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"\*ngswitchcase\b", "*ngSwitchCase", RegexOptions.IgnoreCase);
        content = Regex.Replace(content, @"\*ngswitchdefault\b", "*ngSwitchDefault", RegexOptions.IgnoreCase);
        // Common Angular input bindings — restore camelCase
        var camelCaseAttrs = new[] {
        "ngClass", "ngStyle", "ngModel", "ngModelChange",
        "inputtedParentRef", "onlySearch", "hideStatus", "displaySocialResults",
        "urlSelectedEvent", "showTitle", "hasMenu", "showMenu", "hasClose", "showClose",
        "menuClicked", "closeClicked", "displayMiniTag", "pageSizeDropdown"
    };
        foreach (var attr in camelCaseAttrs)
        {
            var pattern = $@"(\[|\(\(|\(\[|\(|#){Regex.Escape(attr)}(\]|\)\)|\]|\))";
            content = Regex.Replace(content, pattern,
                m => m.Groups[1].Value + attr + m.Groups[2].Value,
                RegexOptions.IgnoreCase);
        }
        return content;
    }

    public static string StripFullFileFence(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = value.Replace("\r\n", "\n");
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline >= 0)
                cleaned = cleaned[(firstNewline + 1)..];
            else
                return string.Empty;
        }
        if (cleaned.EndsWith("```", StringComparison.Ordinal))
            cleaned = cleaned[..^3];
        return cleaned.TrimStart('\n').TrimEnd('\n');
    }

    public static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder();
        var inQuote = false;
        var quoteChar = '\0';
        var prevWasSpace = false;
        foreach (var c in s)
        {
            if ((c == '"' || c == '\'' || c == '`') && (sb.Length == 0 || sb[sb.Length - 1] != '\\'))
            {
                if (!inQuote) { inQuote = true; quoteChar = c; }
                else if (c == quoteChar) { inQuote = false; }
            }
            if (inQuote)
            {
                sb.Append(c);
                prevWasSpace = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!prevWasSpace && sb.Length > 0) { sb.Append(' '); prevWasSpace = true; }
            }
            else
            {
                sb.Append(c);
                prevWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    public static bool IsHtmlLikeContent(string content) =>
     content.Contains('<') && Regex.IsMatch(content, @"</?\w+[\s/>]");
}
