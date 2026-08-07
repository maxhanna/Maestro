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
public static class AgentEditHeuristics
{
    public static readonly string[] UnsafeEditMarkers =
    {
        "…(truncated)", "â€¦(truncated)", "...(truncated)"
    };

    public enum PreEditVerdict { Proceed, AlreadyDone, Irrelevant }

    internal static readonly HashSet<string> ControlFlowKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "while", "switch", "catch", "using", "lock", "foreach"
    };

    internal static readonly Regex[] PlaceholderPatterns = new[]
    {
        new Regex(@"\bMyMethod\b", RegexOptions.Compiled),
        new Regex(@"\bmyNewMethod\b", RegexOptions.Compiled),
        new Regex(@"\bMyNewMethod\b", RegexOptions.Compiled),
        new Regex(@"\bSomeMethod\b", RegexOptions.Compiled),
        new Regex(@"\bDoSomething\b", RegexOptions.Compiled),
        new Regex(@"\bNewMethod\b", RegexOptions.Compiled),
        new Regex(@"\bPlaceholderMethod\b", RegexOptions.Compiled),
        new Regex(@"\bTestMethod\b", RegexOptions.Compiled),
        new Regex(@"\bMyProperty\b", RegexOptions.Compiled),
        new Regex(@"\bSomeProperty\b", RegexOptions.Compiled),
    };

    public static string? DetectHallucinatedProperties(string oldStr, string newStr, string fileContent, string relPath)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is not (".ts" or ".tsx" or ".js" or ".jsx" or ".cs" or ".vb")) return null;
        var newProps = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(newStr, @"\.([A-Za-z_]\w*)", RegexOptions.Compiled))
        {
            var name = m.Groups[1].Value;
            if (!IsBuiltinIdentifier(name)) newProps.Add(name);
        }
        var oldProps = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(oldStr, @"\.([A-Za-z_]\w*)", RegexOptions.Compiled))
        {
            oldProps.Add(m.Groups[1].Value);
        }
        var introducedProps = newProps.Except(oldProps).ToList();
        var trulyInvented = new List<string>();
        var fileWords = new HashSet<string>(fileContent.Split(new[] { ' ', '\n', '\r', '\t', '.', ';', ',', '(', ')', '[', ']', '{', '}', '<', '>', '=', '!', '?', '|', '&', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries));
        foreach (var prop in introducedProps)
        {
            if (Regex.IsMatch(newStr, $@"\b{Regex.Escape(prop)}\s*[:=]")) { continue; }
            if (fileWords.Contains(prop)) { continue; }
            var existingSimilar = fileWords.FirstOrDefault(w =>
                (w.Length > 3) &&
                ((w + "s" == prop) || (w + "es" == prop) ||
                 (prop + "s" == w) || (prop + "es" == w) ||
                 (w + "Array" == prop) || (w + "List" == prop) ||
                 (prop + "Array" == w) || (prop + "List" == w)));
            if (existingSimilar != null)
            {
                trulyInvented.Add($"{prop} (did you mean '{existingSimilar}'?)");
            }
        }
        if (trulyInvented.Count > 0)
        {
            var preview = string.Join(", ", trulyInvented.Take(5));
            return $"HALLUCINATED PROPERTY — newString references [{preview}] which do NOT appear anywhere in {relPath}. " +
                   "The LLM invented properties by modifying the name of existing properties (e.g., pluralizing). " +
                   "Use ONLY properties that already appear in the file. If you need a collection, check if the existing singular property can be used, or explicitly declare the new property in the same edit.";
        }
        return null;
    }

    public static string? ExtractMostUniqueLine(string oldStr, string fileContent)
    {
        var normFile = NormalizeLineEndings(fileContent);
        var oldLines = oldStr.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (oldLines.Count <= 1) return null;
        string? bestLine = null;
        int bestCount = int.MaxValue;
        foreach (var line in oldLines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 15) continue;
            var count = normFile.Split(new[] { trimmed }, StringSplitOptions.None).Length - 1;
            if (count < bestCount)
            {
                bestCount = count;
                bestLine = line;
            }
        }
        return bestLine;
    }

    public static string? ExtractFullHtmlBlock(string fileContent, string oldStr)
    {
        var firstLine = oldStr.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.TrimStart();
        if (string.IsNullOrEmpty(firstLine) || !firstLine.StartsWith("<")) return null;
        var tagMatch = Regex.Match(firstLine, @"^<([a-zA-Z0-9]+)");
        if (!tagMatch.Success) return null;
        var tagName = tagMatch.Groups[1].Value;
        var normFile = NormalizeLineEndings(fileContent);
        var startIdx = normFile.IndexOf(firstLine, StringComparison.Ordinal);
        if (startIdx < 0) return null;
        var depth = 0;
        var pos = startIdx;
        while (pos < normFile.Length)
        {
            var nextOpen = normFile.IndexOf($"<{tagName}", pos, StringComparison.OrdinalIgnoreCase);
            var nextClose = normFile.IndexOf($"</{tagName}>", pos, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0) return null;
            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                var charAfter = nextOpen + tagName.Length + 1 < normFile.Length
                    ? normFile[nextOpen + tagName.Length + 1]
                    : '\0';
                if (charAfter == ' ' || charAfter == '>' || charAfter == '\t' || charAfter == '\n' || charAfter == '\r')
                {
                    depth++;
                }
                pos = nextOpen + tagName.Length + 1;
            }
            else
            {
                if (depth <= 0)
                {
                    var endIdx = nextClose + tagName.Length + 3;
                    return normFile.Substring(startIdx, endIdx - startIdx);
                }
                depth--;
                pos = nextClose + tagName.Length + 3;
            }
        }
        return null;
    }

    public static string? DetectDuplicatePropertyAddition(string oldStr, string newStr)
    {
        string StripStrings(string s)
        {
            s = Regex.Replace(s, @"`[^`]*`", "``", RegexOptions.Singleline);
            s = Regex.Replace(s, @"""[^""]*""", "\"\"", RegexOptions.Singleline);
            s = Regex.Replace(s, @"'[^']*'", "''", RegexOptions.Singleline);
            return s;
        }
        var cleanOld = StripStrings(oldStr);
        var cleanNew = StripStrings(newStr);
        var keyRegex = new Regex(@"^\s*(?:'([^']+)'|""([^""]+)""|(\w+))\s*:", RegexOptions.Multiline);
        var oldCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in keyRegex.Matches(cleanOld))
        {
            var key = (m.Groups[1].Value ?? m.Groups[2].Value ?? m.Groups[3].Value).Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (!oldCounts.ContainsKey(key)) oldCounts[key] = 0;
            oldCounts[key]++;
        }
        var newCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in keyRegex.Matches(cleanNew))
        {
            var key = (m.Groups[1].Value ?? m.Groups[2].Value ?? m.Groups[3].Value).Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (!newCounts.ContainsKey(key)) newCounts[key] = 0;
            newCounts[key]++;
        }
        foreach (var kvp in newCounts)
        {
            oldCounts.TryGetValue(kvp.Key, out var oldVal);
            if (kvp.Value > oldVal && kvp.Value > 1)
            {
                return $"DUPLICATE PROPERTY ADDITION — newString contains {kvp.Value} occurrences of property '{kvp.Key}' " +
                       $"but oldString only had {oldVal}. You added a duplicate property instead of modifying the existing one. " +
                       "MODIFY the existing property value instead of adding a new one with the same name. Include the ENTIRE existing backtick string in oldString.";
            }
        }
        return null;
    }

    /// <summary>
    /// Counts code braces in a line while skipping braces inside single/double
    /// quoted strings, template literals, line comments, and block comments
    /// (including block comments that span multiple lines) — so `const s = "}";`,
    /// `${x} {`, or a `{` on a comment continuation line never corrupts nesting.
    /// </summary>
    internal static void ScanBraceDepth(string s,
        ref int depth, ref bool inSingle, ref bool inDouble, ref bool inTemplate,
        ref bool inBlockComment)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (inSingle)
            {
                if (c == '\\') i++;
                else if (c == '\'') inSingle = false;
                continue;
            }
            if (inDouble)
            {
                if (c == '\\') i++;
                else if (c == '"') inDouble = false;
                continue;
            }
            if (inTemplate)
            {
                if (c == '\\') i++;
                else if (c == '`') inTemplate = false;
                continue;
            }
            if (c == '/' && next == '/') return; // line comment: ignore rest
            if (c == '/' && next == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end >= 0) { i = end + 1; continue; }
                inBlockComment = true; // comment spans into following lines
                i = s.Length;
                continue;
            }
            if (c == '\'') { inSingle = true; continue; }
            if (c == '"') { inDouble = true; continue; }
            if (c == '`') { inTemplate = true; continue; }
            if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
        }
    }

    public static (bool replaced, string newContent, string? matchError, string? snippet) TryReplaceSafe(
     string fileContent, string oldStr, string newStr, int targetLine = 0, string? changeDesc = null)
    {
        if (string.IsNullOrEmpty(oldStr) && string.IsNullOrEmpty(fileContent) && !string.IsNullOrEmpty(newStr))
            return (true, newStr, null, null);
        if (string.IsNullOrEmpty(oldStr) && !string.IsNullOrEmpty(fileContent))
        {
            return (false, fileContent,
                "oldString is empty but the file is non-empty — refusing to perform an unbounded replacement. " +
                "Provide a non-empty, specific anchor.", null);
        }
        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr).TrimEnd('\r');
        var matches = new List<int>();
        var searchPos = 0;
        var maxIterations = normFile.Length + 2;
        var iterations = 0;
        while (iterations++ < maxIterations)
        {
            var idx = normFile.IndexOf(normOld, searchPos, StringComparison.Ordinal);
            if (idx < 0) break;
            matches.Add(idx);
            searchPos = idx + Math.Max(1, normOld.Length);
        }
        if (matches.Count == 1)
        {
            var normNew = NormalizeLineEndings(newStr);
            return (true, normFile[..matches[0]] + normNew + normFile[(matches[0] + normOld.Length)..], null, null);
        }
        if (matches.Count > 1)
        {
            int chosenIdx = -1;
            var keywords = ExtractDisambiguationKeywords(changeDesc);
            if (keywords.Count > 0)
            {
                int bestContextScore = -1;
                for (int i = 0; i < matches.Count; i++)
                {
                    var lookbackStart = Math.Max(0, matches[i] - 2000);
                    var context = normFile.Substring(lookbackStart, matches[i] - lookbackStart).ToLowerInvariant();
                    var score = keywords.Count(k => context.Contains(k));
                    if (score > bestContextScore)
                    {
                        bestContextScore = score;
                        chosenIdx = i;
                    }
                }
            }
            if (chosenIdx == -1 && targetLine > 0)
            {
                var bestDist = int.MaxValue;
                for (int i = 0; i < matches.Count; i++)
                {
                    var matchLine = normFile[..matches[i]].Count(c => c == '\n') + 1;
                    var dist = Math.Abs(matchLine - targetLine);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        chosenIdx = i;
                    }
                }
                if (bestDist > 50) chosenIdx = -1;
            }
            if (chosenIdx >= 0)
            {
                var normNew = NormalizeLineEndings(newStr);
                return (true, normFile[..matches[chosenIdx]] + normNew + normFile[(matches[chosenIdx] + normOld.Length)..], null, null);
            }
            var firstLine = normOld.Split('\n')[0].Trim();
            var uniqueLine = ExtractMostUniqueLine(normOld, normFile);
            var err = $"oldString found {matches.Count} times in file — include more surrounding lines as anchor context.";
            if (uniqueLine != null)
                err += $" OR use ONLY this unique line as your entire oldString: `{uniqueLine.Trim()}`";
            return (false, fileContent, err, firstLine);
        }
        var firstRealLine = normOld.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (firstRealLine != null)
        {
            var fuzzyIdx = normFile.IndexOf(firstRealLine, StringComparison.Ordinal);
            if (fuzzyIdx >= 0)
            {
                var lineStart = normFile.LastIndexOf('\n', fuzzyIdx) + 1;
                var fileSegment = normFile[lineStart..];
                if (fileSegment.StartsWith(normOld.TrimStart()))
                {
                    var normNew = NormalizeLineEndings(newStr);
                    return (true, normFile[..lineStart] + normNew + normFile[(lineStart + normOld.Length)..], null, null);
                }
            }
        }
        return (false, fileContent, "oldString not found verbatim in file", null);
    }

    public static string? BuildExactMatchBlock(string fileContent, string oldStr, int targetLine = 0, string? changeDesc = null)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return null;
        var normFile = NormalizeLineEndings(fileContent);
        var changeLower = (changeDesc ?? "").ToLowerInvariant();
        bool isRemoval = changeLower.Contains("remove") ||
            (changeLower.Contains("delete") && !Regex.IsMatch(changeLower, @"\b(add|create|insert|implement)\b"));
        if (!isRemoval)
        {
            var htmlBlock = ExtractFullHtmlBlock(normFile, oldStr);
            if (htmlBlock != null) return htmlBlock;
        }
        var normOld = NormalizeLineEndings(oldStr);
        var oldLines = normOld.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (oldLines.Count == 0) return null;
        var fileLines = normFile.Split('\n');
        var candidates = new List<(int startIdx, int score)>();
        for (var i = 0; i < fileLines.Length; i++)
        {
            var score = 0;
            var fIdx = i;
            var oIdx = 0;
            while (fIdx < fileLines.Length && oIdx < oldLines.Count)
            {
                var fileTrim = fileLines[fIdx].Trim();
                var oldTrim = oldLines[oIdx].Trim();
                if (fileTrim == oldTrim || fileTrim.StartsWith(oldTrim) || oldTrim.StartsWith(fileTrim))
                {
                    score++;
                    fIdx++;
                    oIdx++;
                }
                else if (string.IsNullOrEmpty(fileTrim) || string.IsNullOrEmpty(oldTrim))
                {
                    if (string.IsNullOrEmpty(fileTrim)) fIdx++;
                    if (string.IsNullOrEmpty(oldTrim)) oIdx++;
                }
                else
                {
                    break;
                }
            }
            if (score >= Math.Max(1, oldLines.Count / 2))
            {
                candidates.Add((i, score));
            }
        }
        if (candidates.Count == 0) return null;
        int chosenCandidate = -1;
        if (candidates.Count == 1)
        {
            chosenCandidate = 0;
        }
        else
        {
            var keywords = ExtractDisambiguationKeywords(changeDesc);
            if (keywords.Count > 0)
            {
                int bestContextScore = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var startIdx = candidates[i].startIdx;
                    var lookbackStart = Math.Max(0, startIdx - 50);
                    var context = string.Join("\n", fileLines.Skip(lookbackStart).Take(startIdx - lookbackStart)).ToLowerInvariant();
                    var score = keywords.Count(k => context.Contains(k));
                    if (score > bestContextScore)
                    {
                        bestContextScore = score;
                        chosenCandidate = i;
                    }
                }
            }
            if (chosenCandidate == -1 && targetLine > 0)
            {
                int bestDist = int.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var dist = Math.Abs(candidates[i].startIdx + 1 - targetLine);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        chosenCandidate = i;
                    }
                }
                if (bestDist > 50) chosenCandidate = -1;
            }
        }
        if (chosenCandidate >= 0)
        {
            var bestStart = candidates[chosenCandidate].startIdx;
            var endIdx = Math.Min(fileLines.Length, bestStart + oldLines.Count);
            var joined = string.Join("\n", fileLines.Skip(bestStart).Take(endIdx - bestStart));
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }
        return null;
    }

    public static string? GetUnsafeEditPayloadReason(string oldString, string newString)
    {
        foreach (var marker in UnsafeEditMarkers)
            if (oldString.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                newString.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return $"Edit contains placeholder marker '{marker}'.";
        return null;
    }

    /// <summary>
    /// Detects placeholder/stub implementations that do NOT implement real logic —
    /// e.g. a method body that is only a console.log call, contains a
    /// '// Placeholder implementation' or '// TODO: implement' comment, is an empty
    /// body, or throws NotImplementedException. Used to reject such edits
    /// deterministically instead of letting the LLM verifier score them as acceptable.
    /// </summary>

    public static bool LooksLikePlaceholderStub(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        // Explicit placeholder comments
        if (Regex.IsMatch(code,
            @"//\s*(placeholder\s*(implementation|stub)|TODO\s*:?\s*(implement|add|fill\s*in)|stub\s+implementation|will\s+be\s+wired\s+up|not\s+implemented\s+yet|dummy\s+implementation|temporary\s+implementation)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled))
            return true;

        // NotImplementedException / NotSupportedException stubs
        if (Regex.IsMatch(code, @"throw\s+new\s+(NotImplementedException|NotSupportedException)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled))
            return true;

        // Empty method body: `name(...): type { }` or arrow `(...) => { }`.
        // Whole-line anchored and dominance-guarded so legitimate code like
        // `if (x) { }`, `for (;;) { }`, `while (x) { }`, `new Foo() { }` or an object
        // literal containing one empty helper is NOT flagged — only a stub that is
        // essentially an empty declaration gets rejected.
        var noComments = Regex.Replace(code, @"//[^\n]*", " ");
        var meaningfulLines = noComments.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != "{" && l != "}" && l != "{ }")
            .ToList();
        if (meaningfulLines.Count <= 3)
        {
            var arrowEmpty = meaningfulLines.Any(l =>
                Regex.IsMatch(l, @"^\([^)]*\)\s*=>\s*\{\s*\}\s*,?\s*$", RegexOptions.Compiled));
            var methodEmpty = meaningfulLines.Any(l =>
            {
                var m = Regex.Match(l,
                    @"^(?:(?:public|private|protected|internal|readonly|static|async|export|default|function|def|const|let|var)\s+)*(?<name>\w+)\s*\([^)]*\)\s*(:\s*[^{}\r\n]{0,80})?\s*\{\s*\}\s*,?\s*$",
                    RegexOptions.Compiled);
                return m.Success && !ControlFlowKeywords.Contains(m.Groups["name"].Value);
            });
            if (arrowEmpty || methodEmpty)
                return true;
        }

        // Single-line console.log stub: `name(...): void { console.log('x'); }`
        if (Regex.IsMatch(noComments.Trim(),
            @"^\w+\s*\([^)]*\)\s*(:\s*[^{\r\n]{0,60})?\s*\{\s*console\.(log|error|warn|info)\([^;]*\);?\s*\}\s*;?$",
            RegexOptions.Compiled))
            return true;

        // Console.log-only body: a block whose only meaningful statements are console.* calls.
        // Signature lines like `showMenuPanel(): void {` are stripped (they end with `{`),
        // so a body that only logs still gets caught.
        var strippedComments = Regex.Replace(code, @"//[^\n]*", " ");
        var meaningful = strippedComments.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != "{" && l != "}" && l != "{ }")
            .Where(l => !(l.Contains('(') && l.EndsWith("{")) && !l.EndsWith("=>") && !l.EndsWith("=> {"))
            .ToList();
        if (meaningful.Count > 0 && meaningful.All(l =>
            Regex.IsMatch(l, @"^console\.(log|error|warn|info)\([^;]*\);?$", RegexOptions.Compiled)))
            return true;

        return false;
    }

    public static string IndentReplacement(string[] fileLines, int start, string replacement, bool isHtmlDomFile = false)
    {
        if (string.IsNullOrEmpty(replacement) || start >= fileLines.Length)
            return replacement;
        var fileIndent = GetLeadingWhitespace(fileLines[start]);
        if (fileIndent.Length == 0)
            return replacement;
        var replLines = replacement.Split('\n');
        var replBaseIndent = replLines.Where(l => l.Length > 0)
                                      .Select(GetLeadingWhitespace)
                                      .FirstOrDefault();
        if (replBaseIndent != null && replBaseIndent != fileIndent)
        {
            for (var i = 0; i < replLines.Length; i++)
            {
                if (replLines[i].Length == 0) continue;
                var lineIndent = GetLeadingWhitespace(replLines[i]);
                if (lineIndent.StartsWith(replBaseIndent, StringComparison.Ordinal))
                {
                    var excess = lineIndent[replBaseIndent.Length..];
                    replLines[i] = fileIndent + excess + replLines[i][lineIndent.Length..];
                }
                else
                {
                    replLines[i] = fileIndent + replLines[i];
                }
            }
        }
        if (isHtmlDomFile && IsHtmlLikeContent(replacement) && replLines.Length > 5)
        {
            return AutoIndentHtml(string.Join("\n", replLines), fileIndent);
        }
        var joined = string.Join("\n", replLines);
        var distinctIndentDepths = replLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => GetLeadingWhitespace(l).Length)
            .Distinct()
            .Count();
        return distinctIndentDepths <= 1 && replLines.Length > 2
            ? AutoIndentFromFile(joined, fileIndent, fileLines, start)
            : joined;
    }
    /// <summary>
    /// Re-indents an oldString/newString replacement snippet so it sits at the
    /// matched block's base indentation, preserving relative nesting. HTML DOM
    /// files (by EXTENSION, never content sniffing) get tag-depth re-indentation;
    /// code files get brace-depth re-indentation. Content sniffing must not be
    /// used here: TS/JS generics like `Promise&lt;void&gt;` contain '&lt;void&gt;' and
    /// would be misdetected as HTML, flattening the entire snippet to base indent.
    /// </summary>

    public static List<string> ReindentReplacementSnippet(
        List<string> newLinesArr, List<string> oldLinesArr,
        List<string> fileLinesArr, int matchIdx, bool isHtmlDomFile)
    {
        var finalNewLines = new List<string>();
        if (newLinesArr.Count == 0) return finalNewLines;
        var baseIndent = Regex.Match(fileLinesArr[matchIdx], @"^(\s*)").Value;
        var oldBaseIndent = Regex.Match(oldLinesArr[0], @"^(\s*)").Value;
        foreach (var nl in newLinesArr)
        {
            if (string.IsNullOrWhiteSpace(nl))
            {
                finalNewLines.Add(nl);
                continue;
            }
            var currentOldIndent = Regex.Match(nl, @"^(\s*)").Value;
            string relativeIndent = currentOldIndent.Length >= oldBaseIndent.Length
                ? currentOldIndent.Substring(oldBaseIndent.Length)
                : "";
            finalNewLines.Add(baseIndent + relativeIndent + nl.TrimStart());
        }
        var rawNew = string.Join("\n", finalNewLines);
        if (isHtmlDomFile && IsHtmlLikeContent(rawNew) && finalNewLines.Count > 5)
        {
            var stripped = finalNewLines
                .Select(l => string.IsNullOrWhiteSpace(l) ? "" : l.TrimStart())
                .ToList();
            var fixedHtml = AutoIndentHtml(string.Join("\n", stripped), baseIndent);
            finalNewLines = fixedHtml.Split('\n').ToList();
        }
        var rawAfter = string.Join("\n", finalNewLines);
        if (!isHtmlDomFile && (rawAfter.Contains('{') || rawAfter.Contains('}')) &&
            finalNewLines.Count > 2)
        {
            var fixedBraces = AutoIndentFromFile(
                string.Join("\n", finalNewLines), baseIndent,
                fileLinesArr.ToArray(), matchIdx);
            finalNewLines = fixedBraces.Split('\n').ToList();
        }
        return finalNewLines;
    }

    public static string? BuildExactMatchHint(string content, string oldString)
    {
        var fileLines = content.Split('\n');
        var oldLines = oldString.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 8)
            .ToList();
        if (oldLines.Count == 0 || fileLines.Length == 0) return null;
        bool IsTrivialLine(string line)
        {
            var t = line.Trim();
            if (t.Length < 12) return true;
            var meaningful = new string(t.Where(char.IsLetterOrDigit).ToArray());
            if (meaningful.Length < 12) return true;
            if (Regex.IsMatch(t, @"^\s*[\w-]+\s*:\s*[\w\d#.()-]+\s*;?\s*$"))
            {
                return true;
            }
            return false;
        }
        var results = new List<(int fileIdx, double score, string line)>();
        for (var fi = 0; fi < fileLines.Length; fi++)
        {
            var fLine = fileLines[fi];
            if (IsTrivialLine(fLine)) continue;
            var bestSim = oldLines.Max(o => ComputeLineSimilarity(fLine, o));
            if (bestSim >= 0.50)
                results.Add((fi, bestSim, fLine));
        }
        var best = results
            .OrderByDescending(r => r.score)
            .ThenByDescending(r => r.line.Trim().Length)
            .Take(3)
            .ToList();
        if (best.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var b in best)
        {
            sb.AppendLine($"  ({(b.score * 100):F0}% match) line {b.fileIdx + 1}: {b.line}");
            var llmLine = oldLines
                .OrderByDescending(o => ComputeLineSimilarity(b.line, o))
                .FirstOrDefault();
            if (llmLine != null && llmLine != b.line.Trim())
            {
                var fileTrimmed = b.line.Trim();
                var diff = DescribeLineDiff(llmLine, fileTrimmed);
                if (diff != null)
                    sb.AppendLine($"    └ DIFF: {diff}");
            }
        }
        return sb.ToString();
    }

    public static string? DetectWrongSectionEdit(
        string oldStr, string fileContent, string stepChange, string relPath)
    {
        if (string.IsNullOrWhiteSpace(oldStr) || string.IsNullOrWhiteSpace(stepChange))
        { return null; }
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (ext is not (".html" or ".htm" or ".cshtml" or ".razor" or ".vue" or ".svelte"))
        { return null; }
        var sectionRegex = new Regex(
            @"\*ngIf\s*=\s*""(\w+)\s*={2,3}\s*'([^']+)'""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var sections = new List<(string name, int divStart, int divEnd)>();
        foreach (Match m in sectionRegex.Matches(fileContent))
        {
            var name = m.Groups[2].Value;
            var divStart = fileContent.LastIndexOf("<div", m.Index, StringComparison.Ordinal);
            if (divStart < 0) continue;
            var divEnd = FindMatchingCloseDiv(fileContent, divStart);
            if (divEnd < 0) continue;
            sections.Add((name, divStart, divEnd));
        }
        if (sections.Count < 2) return null;
        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr);
        var oldStrIdx = normFile.IndexOf(normOld, StringComparison.Ordinal);
        if (oldStrIdx < 0) return null;
        string? actualSection = null;
        foreach (var (name, divStart, divEnd) in sections)
        {
            if (oldStrIdx >= divStart && oldStrIdx <= divEnd)
            {
                actualSection = name;
                break;
            }
        }
        if (actualSection == null) { return null; }
        var stepLower = stepChange.ToLowerInvariant();
        string? targetSection = null;
        foreach (var (name, _, _) in sections)
        {
            if (Regex.IsMatch(stepLower, $@"\b{Regex.Escape(name.ToLowerInvariant())}\b"))
            {
                targetSection = name;
                break;
            }
        }
        if (targetSection == null) return null;
        if (string.Equals(actualSection, targetSection, StringComparison.OrdinalIgnoreCase))
        { return null; }
        var targetSectionEntry = sections.FirstOrDefault(s =>
            string.Equals(s.name, targetSection, StringComparison.OrdinalIgnoreCase));
        var error = new StringBuilder();
        error.AppendLine($"WRONG SECTION — the step description references the '{targetSection}' section, " +
                         $"but your oldString was found in the '{actualSection}' section.");
        error.AppendLine();
        error.AppendLine($"You MUST find the section marked with *ngIf=\"... === '{targetSection}'\" " +
                         $"and use lines from THAT section as your oldString.");
        error.AppendLine($"Do NOT edit the '{actualSection}' section.");
        if (targetSectionEntry.divEnd > targetSectionEntry.divStart)
        {
            var sectionContent = normFile.Substring(
                targetSectionEntry.divStart,
                Math.Min(targetSectionEntry.divEnd - targetSectionEntry.divStart + 6, 3000));
            var sectionLines = sectionContent.Split('\n');
            if (sectionLines.Length > 45)
            {
                sectionContent = string.Join('\n', sectionLines.Take(40)) +
                                 "\n... (section continues)";
            }
            error.AppendLine();
            error.AppendLine($"═══ CORRECT SECTION CONTENT (*ngIf=\"... === '{targetSection}'\") ═══");
            error.AppendLine("```html");
            error.AppendLine(sectionContent);
            error.AppendLine("```");
            error.AppendLine();
            error.AppendLine($"Pick a unique line from the CORRECT section above as your oldString.");
        }
        return error.ToString();
    }

    internal static int FindMatchingCloseDiv(string content, int openDivIdx)
    {
        if (openDivIdx < 0 || openDivIdx >= content.Length) return -1;
        var depth = 0;
        var pos = openDivIdx;
        while (pos < content.Length)
        {
            var nextOpen = content.IndexOf("<div", pos, StringComparison.OrdinalIgnoreCase);
            var nextClose = content.IndexOf("</div>", pos, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0) return -1;
            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                var charAfter = nextOpen + 4 < content.Length
                    ? content[nextOpen + 4]
                    : '\0';
                if (charAfter == ' ' || charAfter == '>' || charAfter == '\t' ||
                    charAfter == '\n' || charAfter == '\r')
                {
                    depth++;
                }
                pos = nextOpen + 4;
            }
            else
            {
                if (depth <= 0) return nextClose;
                depth--;
                pos = nextClose + 6;
            }
        }
        return -1;
    }

    public static int ResolveTargetLineNumber(
        string fileContent,
        string changeDesc,
        string? targetSymbol = null,
        string? estimatedLineRange = null,
        int plannerLineNumber = 0)
    {
        if (string.IsNullOrWhiteSpace(fileContent) || string.IsNullOrWhiteSpace(changeDesc))
            return plannerLineNumber > 0 ? plannerLineNumber : 0;
        var lines = fileContent.Split('\n');
        var candidates = new List<(int line, int score)>();
        var changeLower = changeDesc.ToLowerInvariant();
        var isInsertion = Regex.IsMatch(changeDesc,
            @"\b(add|insert|append|expand|include|new)\b", RegexOptions.IgnoreCase);
        if (!string.IsNullOrWhiteSpace(estimatedLineRange))
        {
            var rangeMatch = Regex.Match(estimatedLineRange, @"(\d+)\s*[-–~]\s*(\d+)");
            if (rangeMatch.Success &&
                int.TryParse(rangeMatch.Groups[1].Value, out var rangeStart) &&
                int.TryParse(rangeMatch.Groups[2].Value, out var rangeEnd))
            {
                var mid = (rangeStart + rangeEnd) / 2;
                if (mid >= 1 && mid <= lines.Length)
                    candidates.Add((mid, 100));
            }
            else
            {
                var singleMatch = Regex.Match(estimatedLineRange, @"(\d+)");
                if (singleMatch.Success &&
                    int.TryParse(singleMatch.Groups[1].Value, out var singleLine) &&
                    singleLine >= 1 && singleLine <= lines.Length)
                    candidates.Add((singleLine, 95));
            }
        }
        if (!string.IsNullOrWhiteSpace(targetSymbol))
        {
            var isCallTarget = Regex.IsMatch(changeDesc, @"\bcall\b", RegexOptions.IgnoreCase);
            var declPattern = $@"\b(class|record|struct)\s+{Regex.Escape(targetSymbol)}\b";
            // Method declaration: symbol(params) { with opening brace on same line
            var methodDeclPattern = $@"\b(?:async\s+|private\s+|public\s+|protected\s+)?{Regex.Escape(targetSymbol)}\s*\([^)]*\)\s*\{{";
            var symPattern = $@"\b{Regex.Escape(targetSymbol)}\s*[\(<{{]";
            var foundDecl = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], declPattern, RegexOptions.IgnoreCase))
                {
                    candidates.Add((i + 1, 200));
                    foundDecl = true;
                    break;
                }
            }
            if (!foundDecl && !isCallTarget)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!Regex.IsMatch(lines[i], methodDeclPattern, RegexOptions.IgnoreCase)) continue;
                    candidates.Add((i + 1, 190));
                    foundDecl = true;
                    break;
                }
            }
            if (!foundDecl)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!Regex.IsMatch(lines[i], symPattern, RegexOptions.IgnoreCase)) continue;
                    // When change targets a "call", skip matches where symbol starts the line (declaration)
                    if (isCallTarget && Regex.IsMatch(lines[i], $@"^\s*(?:async\s+|private\s+|public\s+|protected\s+)?{Regex.Escape(targetSymbol)}\s*\(", RegexOptions.IgnoreCase))
                        continue;
                    var score = 180;
                    // Prefer call sites with complex arguments (template literals or very long lines) --
                    // these are likely the actual target of a replacement, not trivial validation calls
                    if (lines[i].Contains("${")) score += 10;
                    else if (lines[i].Length > 100) score += 5;
                    candidates.Add((i + 1, score));
                }
            }
        }
        if (isInsertion)
        {
            AddInsertionLineCandidates(lines, changeLower, candidates);
        }
        else
        {
            foreach (Match qm in Regex.Matches(changeDesc, @"['""]([^'""]{4,})['""]"))
            {
                var quoted = qm.Groups[1].Value;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(quoted, StringComparison.OrdinalIgnoreCase))
                        candidates.Add((i + 1, 160));
                }
            }
        }
        var keywords = ExtractMeaningfulKeywords(changeLower).Where(w => w.Length >= 4).ToList();
        if (keywords.Count > 0)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var lineLower = lines[i].ToLowerInvariant();
                var hitCount = keywords.Count(w => lineLower.Contains(w));
                if (hitCount >= 2)
                    candidates.Add((i + 1, 20 + hitCount * 5));
            }
        }
        if (changeDesc.TrimStart().StartsWith("<"))
        {
            var textFragments = Regex.Matches(changeDesc, @">([^<]{8,})<")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(t => t.Length >= 8)
                .OrderByDescending(t => t.Length)
                .ToList();
            if (textFragments.Count > 0)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (textFragments.Any(t => lines[i].Contains(t, StringComparison.Ordinal)))
                        candidates.Add((i + 1, 170));
                }
            }
        }
        if (plannerLineNumber > 0 && plannerLineNumber <= lines.Length)
        {
            candidates.Add((plannerLineNumber, 50));
        }
        if (candidates.Count == 0)
            return 0;
        var best = candidates
            .GroupBy(c => c.line)
            .Select(g => (line: g.Key, score: g.Max(x => x.score)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => plannerLineNumber > 0 ? Math.Abs(x.line - plannerLineNumber) : x.line)
            .First();
        System.Diagnostics.Debug.WriteLine($"[ResolveTargetLineNumber] targetSymbol={targetSymbol}, changeDesc={changeDesc?.Substring(0, Math.Min(80, changeDesc?.Length ?? 0))}, plannerLine={plannerLineNumber}, best=line {best.line} score {best.score}, all={string.Join(";", candidates.Select(c => $"{c.line}({c.score})").Distinct())}");
        return best.line;
    }

    internal static void AddInsertionLineCandidates(
        string[] lines, string changeLower, List<(int line, int score)> candidates)
    {
        var containerHints = new List<(string pattern, int weight)>();
        if (changeLower.Contains("faq-container") || changeLower.Contains("faq container"))
            containerHints.Add(("faq-container", 80));
        if (changeLower.Contains("discord-panel") || changeLower.Contains("discord panel"))
            containerHints.Add(("discord-panel", 75));
        if (changeLower.Contains("faq-content") || changeLower.Contains("faq content"))
            containerHints.Add(("faq-content", 78));
        if (containerHints.Count == 0 && changeLower.Contains("faq"))
        {
            containerHints.Add(("faq-container", 70));
            containerHints.Add(("discord-panel", 65));
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var lineLower = lines[i].ToLowerInvariant();
            foreach (var (pattern, weight) in containerHints)
            {
                if (!lineLower.Contains(pattern, StringComparison.Ordinal)) continue;
                var score = weight;
                if (lineLower.Contains("faq", StringComparison.Ordinal)) score += 8;
                if (lineLower.Contains("popup-panel", StringComparison.Ordinal)) score -= 40;
                candidates.Add((i + 1, score));
                for (var j = i; j < Math.Min(i + 15, lines.Length); j++)
                {
                    var probe = lines[j];
                    if (probe.Contains("FAQ entries go here", StringComparison.OrdinalIgnoreCase))
                        candidates.Add((j + 1, weight + 35));
                    if (probe.TrimStart().StartsWith("</details>", StringComparison.OrdinalIgnoreCase))
                        candidates.Add((j + 1, weight + 20));
                }
            }
            if (lineLower.Contains("faq entries go here", StringComparison.Ordinal))
                candidates.Add((i + 1, 90));
        }
    }

    internal static string? ExtractField(string text, string fieldName)
    {
        var pattern = $@"{fieldName}:\s*(.*?)(?=\s*(?:FILE:|CHANGE:|DESCRIPTION:|<<<|$))";
        var m = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    public static bool IsBraceBalanced(string content) => !HasUnbalancedBraces(content);

    public static bool HasUnbalancedBraces(string content)
    {
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        var inTemplate = false;
        var inLineComment = false;
        var inBlockComment = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            var n = i + 1 < content.Length ? content[i + 1] : '\0';
            if (inLineComment && c == '\n') { inLineComment = false; continue; }
            if (inBlockComment && c == '*' && n == '/') { inBlockComment = false; i++; continue; }
            if (inBlockComment) continue;
            if (inLineComment) continue;
            if (!inSingle && !inDouble && !inTemplate)
            {
                if (c == '/' && n == '/') { inLineComment = true; i++; continue; }
                if (c == '/' && n == '*') { inBlockComment = true; i++; continue; }
            }
            if (c == '"' && !inSingle && !inTemplate) { inDouble = !inDouble; continue; }
            if (c == '\'' && !inDouble && !inTemplate) { inSingle = !inSingle; continue; }
            if (c == '`' && !inSingle && !inDouble) { inTemplate = !inTemplate; continue; }
            if (c == '\\' && (inSingle || inDouble || inTemplate)) { i++; continue; }
            if (!inSingle && !inDouble && !inTemplate)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth < 0) return true;
                }
            }
        }
        return depth != 0;
    }

    /// <summary>
    /// True when an oldString is nothing but punctuation/symbol characters (e.g. "}", "{",
    /// ";", "})", "};", ",") — an anchor that can never be reliable: it matches dozens of
    /// places in any real file or deletes structural code. Used to bounce garbage anchors
    /// deterministically BEFORE any LLM round-trip or apply machinery runs.
    /// </summary>
    public static bool IsBarePunctuationAnchor(string? oldStr)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return false;
        var trimmed = oldStr.Trim();
        // 7+ chars of punctuation isn't the classic bare-anchor shape ("}", "};", "},") —
        // leave those to the normal match-count machinery instead of over-blocking.
        if (trimmed.Length == 0 || trimmed.Length > 6) return false;
        return Regex.IsMatch(trimmed, @"^[\p{P}\p{S}]+$");
    }

    /// <summary>First non-blank line of a string, trimmed. Null when the string is blank.</summary>
    public static string? FirstNonBlankLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Split('\n', '\r')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
            ?.Trim();
    }

    /// <summary>
    /// True when an anchor's first real line is a lone closing brace ("}", "})", "};") —
    /// the classic garbage shape where the model starts its oldString with the PREVIOUS
    /// block's closing brace before the real declaration, which either matches dozens of
    /// times or starts the replacement inside the wrong scope.
    /// </summary>
    public static bool IsLoneClosingBraceFirstLine(string? s)
    {
        var first = FirstNonBlankLine(s);
        return first is "}" or "})" or "};";
    }
}
