using System.Text;
using System.Text.RegularExpressions;
namespace Weaver.Services;
public static class HtmlDomEditor
{
    public static bool IsHtmlDomFile(string relPath)
    {
        var ext = Path.GetExtension(relPath)?.ToLowerInvariant();
        return ext is ".html" or ".htm" or ".cshtml" or ".razor";
    }
    public static (string? matchedBlock, int matchIndex, string? error) ResolveHtmlAnchor(
    string content, string targetName, string? stepChange = null, int centerLine = 0,
    bool expandToClosingTags = true, bool expandToLineStart = true)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, -1, "Empty content");
        if (string.IsNullOrWhiteSpace(targetName))
            return (null, -1, "Empty targetName");
        var matchInfo = IndexOfNormalized(content, targetName, stepChange, centerLine);
        if (matchInfo.index < 0)
            return (null, -1, "Target not found in file");
        var adjustedStart = matchInfo.index;
        if (expandToLineStart)
        {
            var lineStart = content.LastIndexOf('\n', matchInfo.index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            adjustedStart = lineStart;
        }
        var initialEndIndex = adjustedStart + (matchInfo.index - adjustedStart) + matchInfo.length;
        var finalEndIndex = initialEndIndex;
        if (expandToClosingTags)
        {
            var (expandedEndIndex, success) = ExpandToClosingTags(content, adjustedStart, initialEndIndex);
            if (success)
            {
                finalEndIndex = expandedEndIndex;
            }
        }
        var adjustedLength = finalEndIndex - adjustedStart;
        var matched = content.Substring(adjustedStart, adjustedLength);
        return (matched, adjustedStart, null);
    }
    private static (int endIndex, bool success) ExpandToClosingTags(string content, int startIndex, int initialEndIndex)
    {
        var stack = new Stack<string>();
        var voidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };
        int i = startIndex;
        while (i < initialEndIndex || stack.Count > 0)
        {
            if (i >= content.Length) return (initialEndIndex, false);
            int nextTagStart = content.IndexOf('<', i);
            if (nextTagStart < 0) return (initialEndIndex, false);
            if (nextTagStart + 1 < content.Length)
            {
                char nextChar = content[nextTagStart + 1];
                if (!char.IsLetter(nextChar) && nextChar != '/' && nextChar != '!')
                {
                    i = nextTagStart + 1;
                    continue;
                }
            }
            else
            {
                return (initialEndIndex, false);
            }
            if (content[nextTagStart + 1] == '/')
            {
                int closeEnd = content.IndexOf('>', nextTagStart);
                if (closeEnd < 0) return (initialEndIndex, false);
                var tagName = content.Substring(nextTagStart + 2, closeEnd - (nextTagStart + 2)).Trim();
                if (stack.Count > 0 && stack.Peek() == tagName)
                {
                    stack.Pop();
                }
                i = closeEnd + 1;
            }
            else if (nextTagStart + 3 < content.Length && content.Substring(nextTagStart, 4) == "<!--")
            {
                int commentEnd = content.IndexOf("-->", nextTagStart);
                if (commentEnd < 0) return (initialEndIndex, false);
                i = commentEnd + 3;
            }
            else
            {
                int openEnd = content.IndexOf('>', nextTagStart);
                if (openEnd < 0) return (initialEndIndex, false);
                var tagContent = content.Substring(nextTagStart + 1, openEnd - (nextTagStart + 1));
                var tagNameMatch = Regex.Match(tagContent, @"^([a-zA-Z0-9-]+)");
                if (!tagNameMatch.Success)
                {
                    i = openEnd + 1;
                    continue;
                }
                var tagName = tagNameMatch.Groups[1].Value;
                bool isSelfClosing = tagContent.EndsWith("/") || voidElements.Contains(tagName);
                if (!isSelfClosing)
                {
                    stack.Push(tagName);
                }
                i = openEnd + 1;
            }
        }
        return (i, true);
    } 
    public static string GetLineIndent(string content, int pos)
    {
        if (pos <= 0 || pos >= content.Length) return "";
        var lineStart = content.LastIndexOf('\n', pos);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var lineEnd = content.IndexOf('\n', pos);
        if (lineEnd < 0) lineEnd = content.Length;
        var line = content.Substring(lineStart, lineEnd - lineStart);
        var m = Regex.Match(line, @"^(\s*)");
        return m.Groups[1].Value;
    }
    public static string StripLeadingClosingDivs(string html, string? targetName = null)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;
        int targetLeading = 0;
        if (targetName != null)
        {
            var targetLines = targetName.Split('\n');
            foreach (var line in targetLines)
            {
                var trimmed = line.Trim();
                if (trimmed == "</div>" || string.IsNullOrWhiteSpace(trimmed))
                    targetLeading++;
                else
                    break;
            }
        }
        var lines = html.Split('\n').ToList();
        int toStrip = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == "</div>" || string.IsNullOrWhiteSpace(trimmed))
                toStrip++;
            else
                break;
        }
        int excess = toStrip - targetLeading;
        if (excess <= 0)
            return html;
        for (int i = 0; i < excess && lines.Count > 0; i++)
        {
            lines.RemoveAt(0);
        }
        return string.Join("\n", lines);
    }
    private static (int index, int length) IndexOfNormalized(
        string content, string targetName, string? stepChange, int centerLine)
    {
        var exactCandidates = FindAllExact(content, targetName);
        if (exactCandidates.Count > 0)
            return PickBestCandidate(content, exactCandidates, stepChange, centerLine);
        var tokens = Regex.Matches(targetName, @"\S+")
            .Select(m => Regex.Escape(m.Value))
            .ToList();
        if (tokens.Count > 0)
        {
            var pattern = string.Join(@"\s+", tokens);
            try
            {
                var matches = Regex.Matches(content, pattern, RegexOptions.IgnoreCase);
                if (matches.Count > 0)
                {
                    var candidates = matches.Select(m => (m.Index, m.Length)).ToList();
                    return PickBestCandidate(content, candidates, stepChange, centerLine);
                }
            }
            catch (RegexParseException)
            {
            }
        }
        var collapsedCandidates = FindAllCollapsed(content, targetName);
        if (collapsedCandidates.Count > 0)
            return PickBestCandidate(content, collapsedCandidates, stepChange, centerLine);
        // Last resort for HTML: attribute-aware fuzzy element matching. The LLM frequently
        // emits a targetName whose attribute VALUES drift from the file (e.g. a hallucinated
        // (closeClicked)=\"$event\" when the file has remove_me('RecipeComponent')). Match on
        // tag name + attribute KEYS (order-insensitive) with normalized value scoring instead
        // of rejecting the whole edit with "targetName block not found".
        var fuzzyCandidates = FindFuzzyElementCandidates(content, targetName);
        if (fuzzyCandidates.Count > 0)
            return PickBestCandidate(content, fuzzyCandidates, stepChange, centerLine);
        return (-1, 0);
    }
    private static List<(int index, int length)> FindAllExact(string content, string targetName)
    {
        var result = new List<(int, int)>();
        var pos = 0;
        while (true)
        {
            var idx = content.IndexOf(targetName, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            result.Add((idx, targetName.Length));
            pos = idx + Math.Max(1, targetName.Length);
        }
        return result;
    } 
    private static List<(int index, int length)> FindAllCollapsed(string content, string targetName)
    {
        var result = new List<(int, int)>();
        var collapsedContent = new StringBuilder(content.Length);
        var origIndices = new List<int>(content.Length);
        for (var i = 0; i < content.Length; i++)
        {
            if (char.IsWhiteSpace(content[i])) continue;
            collapsedContent.Append(content[i]);
            origIndices.Add(i);
        }
        var collapsedTarget = new StringBuilder(targetName.Length);
        for (var i = 0; i < targetName.Length; i++)
        {
            if (char.IsWhiteSpace(targetName[i])) continue;
            collapsedTarget.Append(targetName[i]);
        }
        if (collapsedTarget.Length == 0) return result;
        var contentStr = collapsedContent.ToString();
        var targetStr = collapsedTarget.ToString();
        var searchFrom = 0;
        while (true)
        {
            var idx = contentStr.IndexOf(targetStr, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            var endCollapsedIdx = idx + targetStr.Length - 1;
            if (endCollapsedIdx >= origIndices.Count) break;
            var startOrig = origIndices[idx];
            var endOrig = origIndices[endCollapsedIdx];
            result.Add((startOrig, endOrig - startOrig + 1));
            searchFrom = idx + Math.Max(1, targetStr.Length);
        }
        return result;
    }
    private static List<(int index, int length)> FindFuzzyElementCandidates(string content, string targetName)
    {
        var result = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(targetName))
            return result;
        var tagMatch = Regex.Match(targetName, @"<([a-zA-Z][\w-]*)\b");
        if (!tagMatch.Success)
            return result;
        var tag = tagMatch.Groups[1].Value;
        // Only parse the FIRST tag of the targetName (attributes of nested tags would pollute the key set).
        var targetTag = targetName;
        var gt = FindHtmlTagEnd(targetName, 1);
        if (gt >= 0)
            targetTag = targetName.Substring(0, gt + 1);
        var targetAttrs = ParseHtmlAttributes(targetTag);
        if (targetAttrs.Count == 0)
            return result;
        var tagRegex = new Regex($@"<{Regex.Escape(tag)}\b", RegexOptions.IgnoreCase);
        var scored = new List<(int index, int length, int score)>();
        foreach (Match m in tagRegex.Matches(content))
        {
            var elemStart = m.Index;
            var tagEnd = FindHtmlTagEnd(content, elemStart + m.Length);
            if (tagEnd < 0)
                continue;
            var openTag = content.Substring(elemStart, tagEnd - elemStart + 1);
            var candAttrs = ParseHtmlAttributes(openTag);
            // Every target attribute KEY must be present in the candidate (order-insensitive).
            if (targetAttrs.Any(ta => !candAttrs.Any(ca => string.Equals(ca.Key, ta.Key, StringComparison.OrdinalIgnoreCase))))
                continue;
            var score = targetAttrs.Count(ta =>
                candAttrs.Any(ca => string.Equals(ca.Key, ta.Key, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(ca.Value, ta.Value, StringComparison.OrdinalIgnoreCase)));
            var elemEnd = ExtendHtmlElementEnd(content, tagEnd, tag);
            scored.Add((elemStart, elemEnd - elemStart, score));
        }
        if (scored.Count == 0)
            return result;
        var best = scored.Max(s => s.score);
        // Require at least one attribute VALUE match — otherwise a hallucinated
        // targetName could silently select the wrong element that shares the keys.
        if (best < 1)
            return result;
        foreach (var (idx, len, s) in scored)
            if (s >= best)
                result.Add((idx, len));
        return result;
    }

    private static List<(string Key, string Value)> ParseHtmlAttributes(string tagText)
    {
        var result = new List<(string, string)>();
        var attrRegex = new Regex(@"([@#*]?\[[^\]""=]*\]|[@#*]?\([^\)""=]*\)|[@#*]?[\w:.-]+)\s*=\s*(""[^""]*""|'[^']*')", RegexOptions.IgnoreCase);
        foreach (Match m in attrRegex.Matches(tagText))
        {
            var normKey = Regex.Replace(m.Groups[1].Value, @"[\[\]()*#@]", "");
            var normVal = NormalizeHtmlValue(m.Groups[2].Value);
            result.Add((normKey, normVal));
        }
        return result;
    }

    private static string NormalizeHtmlValue(string value)
    {
        var s = (value ?? "").Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            s = s[1..^1];
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static int FindHtmlTagEnd(string content, int searchFrom)
    {
        var inQuote = '\0';
        for (var i = searchFrom; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuote != '\0')
            {
                if (c == '\\') { i++; continue; }
                if (c == inQuote) inQuote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') { inQuote = c; continue; }
            if (c == '>') return i;
        }
        return -1;
    }

    private static int ExtendHtmlElementEnd(string content, int openTagEnd, string tag)
    {
        // self-closing: <tag ... />
        var ci = openTagEnd - 1;
        while (ci >= 0 && char.IsWhiteSpace(content[ci])) ci--;
        if (ci >= 0 && content[ci] == '/')
            return openTagEnd + 1;
        var lineEnd = content.IndexOf('\n', openTagEnd);
        if (lineEnd < 0) lineEnd = content.Length;
        var closePattern = $@"</{Regex.Escape(tag)}\s*>";
        var slice = content.Substring(openTagEnd + 1, lineEnd - (openTagEnd + 1));
        var closeMatch = Regex.Match(slice, closePattern, RegexOptions.IgnoreCase);
        if (closeMatch.Success)
            return openTagEnd + 1 + closeMatch.Index + closeMatch.Length;
        return lineEnd;
    }

    private static (int index, int length) PickBestCandidate(
        string content, List<(int index, int length)> candidates, string? stepChange, int centerLine)
    {
        if (candidates.Count == 1) return candidates[0];
        var keywords = AgentUtilities.ExtractDisambiguationKeywords(stepChange);
        var hasKeywords = keywords.Count > 0;
        var hasLineHint = centerLine > 0;
        if (!hasKeywords && !hasLineHint)
            return candidates[^1];
        var best = candidates[^1];
        var bestScore = int.MinValue;
        foreach (var (index, length) in candidates)
        {
            var score = 0;
            if (hasKeywords)
            {
                var windowStart = Math.Max(0, index - 800);
                var windowLen = Math.Min(content.Length, index + length + 200) - windowStart;
                var window = content.Substring(windowStart, windowLen);
                score += keywords.Count(k => window.Contains(k, StringComparison.OrdinalIgnoreCase)) * 100;
            }
            if (hasLineHint)
            {
                var matchLine = content[..index].Count(c => c == '\n') + 1;
                var dist = Math.Abs(matchLine - centerLine);
                score -= dist;
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = (index, length);
            }
        }
        return best;
    }
}