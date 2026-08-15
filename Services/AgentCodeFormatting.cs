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
public static class AgentCodeFormatting
{
    public static string AutoFixSqlWhitespace(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        var result = content;
        var changed = false;
        var stringRegex = new Regex(@"@?""(?:[^""\\]|\\.|"""")*""", RegexOptions.Singleline);
        var matches = stringRegex.Matches(result);
        foreach (Match m in matches)
        {
            var sqlStr = m.Value;
            if (!Regex.IsMatch(sqlStr, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE\s+TABLE|ALTER\s+TABLE)\b", RegexOptions.IgnoreCase))
                continue;
            var fixedSql = sqlStr;
            var keywordDigit = new Regex(@"\b(INTERVAL|MINUTE|HOUR|DAY|MONTH|YEAR|SECOND|MICROSECOND|WEEK|QUARTER|LIMIT|OFFSET|TOP|SELECT|DELETE|UPDATE|INSERT|FROM|WHERE|JOIN|AND|OR|NOT|IN|ON|AS|BY|ORDER|GROUP|HAVING|UNION|INTO|VALUES|SET|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH|ALL)(\d)", RegexOptions.IgnoreCase);
            fixedSql = keywordDigit.Replace(fixedSql, "$1 $2");
            var keywordStar = new Regex(@"\b(SELECT|DELETE|DISTINCT|ALL)\*", RegexOptions.IgnoreCase);
            fixedSql = keywordStar.Replace(fixedSql, "$1 *");
            var keywordParen = new Regex(@"\b(SELECT|FROM|WHERE|JOIN|INNER|LEFT|RIGHT|OUTER|AND|OR|NOT|IN|BETWEEN|LIKE|IS|ON|AS|BY|ORDER|GROUP|HAVING|LIMIT|OFFSET|UNION|INSERT|INTO|VALUES|UPDATE|SET|DELETE|CREATE|TABLE|ALTER|DROP|CASE|WHEN|THEN|ELSE|END|EXISTS|DISTINCT|WITH)\(", RegexOptions.IgnoreCase);
            fixedSql = keywordParen.Replace(fixedSql, "$1 (");
            if (fixedSql != sqlStr)
            {
                result = result.Replace(sqlStr, fixedSql);
                changed = true;
            }
        }
        return changed ? result : content;
    }

    internal static readonly Regex OperatorSpacingRegex = new(
        @"(===|!==|>=|<=|==|!=|&&|\|\||=>|=)\s*(\d)",
        RegexOptions.IgnoreCase);

    internal static readonly Regex LtGtDigitRegex = new(@"(<|>)\s*(\d)");

    internal static readonly Regex KeywordParenRegex = new(
        @"\b(if|for|while|switch|catch|typeof|instanceof)\s*\(");

    internal static readonly Regex ElseBraceRegex = new(@"\}\s*else\s*\{");

    internal static readonly Regex ElseIfParenRegex = new(@"else\s+if\s*\(");

    internal static readonly Regex ParenBraceRegex = new(@"\)\s*\{");

    internal static readonly Regex ReturnBraceRegex = new(@"\breturn\s*\{");

    internal static readonly Regex FatArrowBraceRegex = new(@"=>\s*\{");

    internal static readonly Regex CommaSpaceRegex = new(@",(\S)");

    public static string AutoFixOperatorSpacing(string code)
    {
        code = OperatorSpacingRegex.Replace(code, "$1 $2");
        code = LtGtDigitRegex.Replace(code, "$1 $2");
        code = KeywordParenRegex.Replace(code, m =>
        {
            var kw = m.Groups[1].Value;
            return kw + " (";
        });
        code = ElseBraceRegex.Replace(code, "} else {");
        code = ElseIfParenRegex.Replace(code, "else if (");
        code = ParenBraceRegex.Replace(code, ") {");
        code = ReturnBraceRegex.Replace(code, "return {");
        code = FatArrowBraceRegex.Replace(code, "=> {");
        code = CommaSpaceRegex.Replace(code, ", $1");
        return code;
    }

    public static string AutoFixPythonStatements(string content, string relPath)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        if (!Path.GetExtension(relPath).Equals(".py", StringComparison.OrdinalIgnoreCase)) return content;
        content = Regex.Replace(content, @"(?<!\\)([""'])([^\r\n]*?)(?<!\\)\1", m =>
        {
            var quote = m.Groups[1].Value;
            var strContent = m.Groups[2].Value;
            if (strContent.StartsWith(quote)) return m.Value;
            if (strContent.EndsWith(" ") || strContent.EndsWith("\t"))
            {
                strContent = strContent.TrimEnd(' ', '\t');
                return $"{quote}{strContent}{quote}";
            }
            return m.Value;
        });
        content = Regex.Replace(content, @"[ \t]+\r?\n", "\n");
        var pyKeywords = "print|return|if|for|while|def|class|import|from|with|try|except|finally|raise|yield|assert|del|global|nonlocal|pass|break|continue";
        content = Regex.Replace(content, $@"\)\s*({pyKeywords})\b", ")\n$1");
        content = Regex.Replace(content, $@";\s*({pyKeywords})\b", ";\n$1");
        content = Regex.Replace(content, $@"\]\s*({pyKeywords})\b", "]\n$1");
        content = Regex.Replace(content, $@"\}}\s*({pyKeywords})\b", "}\n$1");
        return content;
    }

    public static string AutoFixHtmlIndentation(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        var lines = content.Split('\n');
        var result = new List<string>();
        var depth = 0;
        var inCodeBlock = false;
        var codeBlockBaseIndent = -1;
        var jsBraceDepth = 0;
        var voidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };
        for (int i = 0; i < lines.Length; i++)
        {
            var originalLine = lines[i];
            var trimmed = originalLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                result.Add("");
                continue;
            }
            if (inCodeBlock)
            {
                if (trimmed.Contains("</script>", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("</style>", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("</pre>", StringComparison.OrdinalIgnoreCase))
                {
                    inCodeBlock = false;
                    codeBlockBaseIndent = -1;
                    jsBraceDepth = 0;
                    result.Add(new string(' ', (depth - 1) * 2) + trimmed);
                    depth = Math.Max(0, depth - 1);
                    continue;
                }
                if (codeBlockBaseIndent == -1)
                {
                    codeBlockBaseIndent = (depth + 1) * 2;
                }
                int currentJsIndent = codeBlockBaseIndent + (jsBraceDepth * 2);
                if (trimmed.StartsWith("}") || trimmed.StartsWith(")") || trimmed.StartsWith("]"))
                {
                    currentJsIndent = Math.Max(0, currentJsIndent - 2);
                }
                result.Add(new string(' ', currentJsIndent) + trimmed);
                int opens = trimmed.Count(c => c == '{');
                int closes = trimmed.Count(c => c == '}');
                jsBraceDepth = Math.Max(0, jsBraceDepth + opens - closes);
                continue;
            }
            var matches = Regex.Matches(trimmed, @"<(/?)([a-zA-Z0-9]+)[^>]*?(/?)>");
            int adjust = 0;
            bool startsWithClosing = trimmed.StartsWith("</");
            foreach (Match m in matches)
            {
                bool isClosing = m.Groups[1].Value == "/";
                string tag = m.Groups[2].Value.ToLower();
                bool isSelfClosing = m.Groups[3].Value == "/" || voidElements.Contains(tag);
                if (!isClosing && !isSelfClosing)
                {
                    adjust++;
                    if ((tag == "script" || tag == "style" || tag == "pre") &&
                        !trimmed.Contains($"</{tag}>", StringComparison.OrdinalIgnoreCase))
                    {
                        inCodeBlock = true;
                    }
                }
                else if (isClosing)
                {
                    adjust--;
                }
            }
            int currentDepth = depth;
            if (startsWithClosing) currentDepth = Math.Max(0, depth - 1);
            result.Add(new string(' ', currentDepth * 2) + trimmed);
            depth = Math.Max(0, depth + adjust);
        }
        return string.Join("\n", result);
    }

    /// <summary>
    /// Re-indents a Python code block so it can be inserted at a given anchor indentation.
    /// The LLM's newCode often mixes tabs and spaces (or drops indentation on some lines),
    /// which the generic min-indent realignment mishandles (tabs count as 1 char each) and
    /// which Python rejects outright (TabError). This normalizes every line's leading
    /// whitespace to the ANCHOR's own unit (tabs or spaces), measures indentation in that
    /// unit, and rebuilds the block with relative depth preserved: the first non-empty line
    /// lands exactly at <paramref name="anchorBaseIndent"/> and each following line keeps
    /// its depth RELATIVE to it (so a def at the anchor level gets its body indented by one
    /// unit, exactly as the anchor's own body is). Blank lines stay blank. The body text is
    /// preserved verbatim; only leading whitespace is rewritten.
    /// </summary>
    public static string ReindentPythonBlock(string newCode, string anchorBaseIndent, int tabWidth = 4)
    {
        if (string.IsNullOrWhiteSpace(newCode)) return newCode;
        if (tabWidth <= 0) tabWidth = 4;
        var lines = newCode.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var anchorTabs = anchorBaseIndent.Contains('\t');
        var normalized = new string[lines.Length];
        var indents = new int[lines.Length];
        int? baseIndent = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { normalized[i] = ""; indents[i] = 0; continue; }
            var ws = new StringBuilder();
            var pos = 0;
            while (pos < line.Length && (line[pos] == ' ' || line[pos] == '\t'))
            {
                if (line[pos] == '\t') ws.Append(' ', tabWidth); else ws.Append(' ');
                pos++;
            }
            indents[i] = ws.Length;
            normalized[i] = line.Substring(pos);
            if (baseIndent == null || indents[i] < baseIndent) baseIndent = indents[i];
        }
        if (baseIndent == null) baseIndent = 0;
        // The block's OWN indent unit (GCD of distinct positive indents): a body written with
        // 8 spaces under a flush-left def means ONE level (8-space file), not two — measuring
        // relative depth in this unit, not raw characters, keeps the block internally valid.
        var unit = tabWidth;
        var positiveIndents = indents.Where(i => i > 0).Distinct().OrderBy(i => i).ToList();
        if (positiveIndents.Count > 0)
        {
            var g = positiveIndents[0];
            foreach (var v in positiveIndents.Skip(1)) g = Gcd(g, v);
            if (g > 0) unit = g;
        }
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(normalized[i])) { sb.AppendLine(); continue; }
            var relative = Math.Max(0, indents[i] - baseIndent.Value);
            var relUnits = (int)Math.Round(relative / (double)unit);
            string indentStr;
            if (anchorTabs)
            {
                indentStr = anchorBaseIndent + new string('\t', relUnits);
            }
            else
            {
                indentStr = anchorBaseIndent + new string(' ', relUnits * unit);
            }
            sb.Append(indentStr).AppendLine(normalized[i]);
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { var t = a % b; a = b; b = t; }
        return Math.Max(1, a);
    }

    public static string ReindentToLevel(string code, string indent)
    {
        if (string.IsNullOrEmpty(code)) return code;
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
                lines[i] = indent + lines[i].TrimStart();
        }
        return string.Join("\n", lines);
    }

    internal static readonly HashSet<string> VoidHtmlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr"
    };

    public static string AutoIndentHtml(string html, string baseIndent)
    {
        const string IndentStep = "  ";
        var lines = html.Split('\n');
        var distinctDepths = lines
            .Where(l => l.Trim().Length > 0)
            .Select(l => GetLeadingWhitespace(l).Length)
            .Distinct().Count();
        if (distinctDepths > 1) { return html; }
        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;
            if (Regex.IsMatch(trimmed, @"^</[\w-]"))
            { depth = Math.Max(0, depth - 1); }
            lines[i] = baseIndent + new string(' ', depth * IndentStep.Length) + trimmed;
            var tagMatch = Regex.Match(trimmed, @"^<([\w-]+)[\s>]");
            if (tagMatch.Success)
            {
                var tag = tagMatch.Groups[1].Value;
                var isSelfClosing = trimmed.EndsWith("/>") || VoidHtmlElements.Contains(tag);
                var closedInline = trimmed.Contains($"</{tag}>");
                var isClosing = trimmed.StartsWith("</");
                var isComment = trimmed.StartsWith("<!--");
                if (!isSelfClosing && !closedInline && !isClosing && !isComment)
                { depth++; }
            }
        }
        return string.Join("\n", lines);
    }

    public static string AutoIndentFromFile(string replacement, string fileIndent, string[] fileLines, int start)
    {
        if (!replacement.Contains('{') && !replacement.Contains('}'))
        { return replacement; }
        var indentSize = InferIndentSize(fileLines, start);
        if (indentSize <= 0) return replacement;
        var lines = replacement.Split('\n');
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        var inTemplate = false;
        var inBlockComment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            var trimmed = lines[i].TrimStart();
            // Inside a template literal (e.g. Angular HTML templates, backtick
            // strings): the content is verbatim text — never re-indent it and
            // never count its braces as code. Only an UNESCAPED closing backtick
            // matters; code after it on the same line is still scanned.
            if (inTemplate)
            {
                var closeIdx = IndexOfUnescapedBacktick(trimmed);
                if (closeIdx >= 0)
                {
                    inTemplate = false;
                    ScanBraceDepth(trimmed[(closeIdx + 1)..],
                        ref depth, ref inSingle, ref inDouble, ref inTemplate, ref inBlockComment);
                }
                continue;
            }
            var lineDepth = depth;
            if (trimmed.StartsWith("}"))
                lineDepth = Math.Max(0, lineDepth - 1);
            var expectedIndent = fileIndent + new string(' ', lineDepth * indentSize);
            var lineIndent = GetLeadingWhitespace(lines[i]);
            if (lineIndent != expectedIndent)
                lines[i] = expectedIndent + trimmed;
            ScanBraceDepth(trimmed,
                ref depth, ref inSingle, ref inDouble, ref inTemplate, ref inBlockComment);
        }
        return string.Join("\n", lines);
    }
    /// <summary>
    /// Finds the first backtick not preceded by an odd run of backslashes (i.e.
    /// an unescaped closing backtick), or -1 if the template continues.
    /// </summary>

    internal static int IndexOfUnescapedBacktick(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '`') continue;
            var slashes = 0;
            for (var j = i - 1; j >= 0 && s[j] == '\\'; j--) slashes++;
            if (slashes % 2 == 0) return i;
        }
        return -1;
    }

    public static int InferIndentSize(string[] fileLines, int start)
    {
        var sampleStart = Math.Max(0, start - 5);
        var sampleEnd = Math.Min(fileLines.Length, start + 20);
        var deltas = new List<int>();
        for (var i = sampleStart + 1; i < sampleEnd; i++)
        {
            var prev = GetLeadingWhitespace(fileLines[i - 1]).Length;
            var curr = GetLeadingWhitespace(fileLines[i]).Length;
            var delta = Math.Abs(curr - prev);
            if (delta > 0 && delta <= 8)
                deltas.Add(delta);
        }
        if (deltas.Count == 0) return 2;
        var mode = deltas.GroupBy(d => d).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        return mode < 2 ? 2 : mode > 4 ? 4 : mode;
    }

    public static string AutoIndentFullFile(string fullContent, string[] originalLines)
    {
        var indentSize = InferIndentSize(originalLines, 0);
        if (indentSize <= 0) return fullContent;
        var lines = fullContent.Split('\n');
        var depth = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            var trimmed = lines[i].TrimStart();
            var lineDepth = depth;
            if (trimmed.StartsWith("}"))
                lineDepth = Math.Max(0, lineDepth - 1);
            var expectedIndent = new string(' ', lineDepth * indentSize);
            var lineIndent = GetLeadingWhitespace(lines[i]);
            if (lineIndent != expectedIndent)
                lines[i] = expectedIndent + trimmed;
            foreach (var c in trimmed)
            {
                if (c == '{') depth++;
                if (c == '}') depth = Math.Max(0, depth - 1);
            }
        }
        return string.Join("\n", lines);
    }

    public static string FindLastBalancedPrefix(string content)
    {
        var depth = 0;
        var lastBalanced = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '{') depth++;
            if (content[i] == '}') depth = Math.Max(0, depth - 1);
            if (depth == 0) lastBalanced = i + 1;
        }
        return content[..Math.Max(lastBalanced, content.Length / 2)];
    }

    public static bool IsFullFileTruncated(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var opens = content.Count(c => c == '{');
        var closes = content.Count(c => c == '}');
        return opens > closes;
    }
}
