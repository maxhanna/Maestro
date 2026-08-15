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

    public static string? DetectHallucinatedProperties(string oldStr, string newStr, string fileContent, string relPath,
        string? relatedFileContent = null)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        // HTML is supported too: the guard scans property accesses inside {{ ... }} template
        // interpolations, so a hallucinated binding like `s.departure.ested` (typo of the real
        // `s.departure.estimated` present elsewhere in the file) is caught the same way a
        // hallucinated `.plularName` in TS is — the exact failure mode that produced a garbage
        // globe.component.html edit in a real run.
        if (ext is not (".ts" or ".tsx" or ".js" or ".jsx" or ".cs" or ".vb" or ".html" or ".htm")) return null;
        // HTML: scan property accesses inside interpolation regions ({{ ... }}), [property]
        // binding VALUES, (event) handler BODIES, and structural-directive EXPRESSIONS
        // (*ngIf="…", *ngFor="…", *ngSwitchCase="…") — so ngClass/ngStyle, click-handler and
        // structural-directive typos get the same treatment as interpolations. The binding
        // TARGET ([class.foo], [style.width], (click)) and the directive NAME (*ngIf) are
        // deliberately excluded: they name a class / style property / event / directive, NOT a
        // property access, so scanning them would false-positive on every `.foo` class token
        // (e.g. a class literally named 'activ' next to a real 'active' property).
        var scanTarget = ext is ".html" or ".htm"
            ? HtmlExpressionScanTarget(newStr)
            : newStr;
        var newProps = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(scanTarget, @"\.([A-Za-z_]\w*)", RegexOptions.Compiled))
        {
            var name = m.Groups[1].Value;
            if (!IsBuiltinIdentifier(name)) newProps.Add(name);
        }
        var oldScan = ext is ".html" or ".htm"
            ? HtmlExpressionScanTarget(oldStr)
            : oldStr;
        var oldProps = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(oldScan, @"\.([A-Za-z_]\w*)", RegexOptions.Compiled))
        {
            oldProps.Add(m.Groups[1].Value);
        }
        var introducedProps = newProps.Except(oldProps).ToList();
        var trulyInvented = new List<string>();
        // ':' must be a split character too — otherwise a property declaration like `items: string[]`
        // yields the token "items:" and the exact-contains check below misses the real word "items",
        // letting the typo heuristic false-positive on it ("did you mean 'items:'?").
        var fileWords = new HashSet<string>(fileContent.Split(new[] { ' ', '\n', '\r', '\t', '.', ';', ',', ':', '(', ')', '[', ']', '{', '}', '<', '>', '=', '!', '?', '|', '&', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries));
        // Resolve bound properties against the component's own file too (Angular: a template
        // references members declared in the sibling `.component.ts`, which the edited HTML
        // never contains). Merging the sibling content into the known-word set means a binding
        // referencing a GENUINELY DECLARED member is exempt (never flagged as a typo of some
        // similar template token — e.g. declaring `vm.item` while the template happens to
        // contain 'items'), while a typo of a real TS member (`vm.opnCard` vs `openCard`)
        // is caught even when the real name appears NOWHERE in the template. Tokens from the
        // related file never become "introduced" — only the edited file's own expressions do.
        if (!string.IsNullOrWhiteSpace(relatedFileContent))
        {
            foreach (var w in relatedFileContent.Split(new[] { ' ', '\n', '\r', '\t', '.', ';', ',', ':', '(', ')', '[', ']', '{', '}', '<', '>', '=', '!', '?', '|', '&', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries))
                fileWords.Add(w);
        }
        foreach (var prop in introducedProps)
        {
            if (Regex.IsMatch(newStr, $@"\b{Regex.Escape(prop)}\s*[:=]")) { continue; }
            if (fileWords.Contains(prop)) { continue; }
            var existingSimilar = fileWords.FirstOrDefault(w =>
                (w.Length > 3) &&
                ((w + "s" == prop) || (w + "es" == prop) ||
                 (prop + "s" == w) || (prop + "es" == w) ||
                 (w + "Array" == prop) || (w + "List" == prop) ||
                 (prop + "Array" == w) || (prop + "List" == w) ||
                 IsPlausibleTypo(prop, w)));
            if (existingSimilar != null)
            {
                trulyInvented.Add($"{prop} (did you mean '{existingSimilar}'?)");
            }
        }
        if (trulyInvented.Count > 0)
        {
            var preview = string.Join(", ", trulyInvented.Take(5));
            return $"HALLUCINATED PROPERTY — newString references [{preview}] which do NOT appear anywhere in {relPath}. " +
                   "The LLM invented properties by modifying the name of existing properties (e.g., pluralizing, or " +
                   "dropping letters from a real name like 'estimated' → 'ested'). " +
                   "Use ONLY properties that already appear in the file. If you need a collection, check if the existing singular property can be used, or explicitly declare the new property in the same edit.";
        }
        return null;
    }

    /// <summary>Collects the EXPRESSION text Angular evaluates in a template: {{ }} interpolation
    /// bodies, [property]="…" binding VALUES, (event)="…" handler BODIES, and structural-directive
    /// EXPRESSIONS (*ngIf="…", *ngFor="…", *ngSwitchCase="…", any *directive="…"). The binding
    /// TARGET ([class.foo], [style.width], (click)) and the directive NAME (*ngIf) are deliberately
    /// excluded — they name a class, style property, event, or directive, not a property access, so
    /// scanning them would false-positive on every `.foo`-style class token. Only the quoted value is
    /// scanned, so a hallucinated property inside ANY evaluated expression gets the same treatment as
    /// one inside {{ }}.</summary>
    private static string HtmlExpressionScanTarget(string s)
    {
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(s, @"\{\{(.*?)\}\}", RegexOptions.Singleline | RegexOptions.Compiled))
        {
            sb.Append('\n').Append(m.Groups[1].Value);
        }
        // The target pattern also admits the banana-in-a-box `[(ngModel)]="…"` shape (name
        // INSIDE the parens), so two-way binding values get scanned too. Only the VALUE is
        // captured — the target is deliberately dropped.
        var bindingRegex = new Regex(
            @"\[(?:\([A-Za-z_][A-Za-z0-9_.:-]*\)|[A-Za-z_][A-Za-z0-9_.:-]*)\]\s*=\s*(?:""([^""]*)""|'([^']*)')",
            RegexOptions.Compiled);
        foreach (Match m in bindingRegex.Matches(s))
        {
            var value = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
            sb.Append('\n').Append(value);
        }
        var eventRegex = new Regex(
            @"\([A-Za-z_][A-Za-z0-9_.:-]*\)\s*=\s*(?:""([^""]*)""|'([^']*)')",
            RegexOptions.Compiled);
        foreach (Match m in eventRegex.Matches(s))
        {
            var value = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
            sb.Append('\n').Append(value);
        }
        // Structural directives (*ngIf, *ngFor, *ngSwitchCase, *ngSwitchDefault, …) carry
        // EVALUATED expressions just like the surfaces above — `*ngIf="vm.isActive"`,
        // `*ngFor="let c of vm.cards"`, `*ngSwitchCase="vm.active"`. The `*`-prefixed name is
        // Angular-specific (Vue uses `v-`), so `*name="…"` can only be a directive; only the
        // quoted expression is captured, never the directive name.
        var structuralRegex = new Regex(
            @"\*[A-Za-z_][A-Za-z0-9_-]*\s*=\s*(?:""([^""]*)""|'([^']*)')",
            RegexOptions.Compiled);
        foreach (Match m in structuralRegex.Matches(s))
        {
            var value = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
            sb.Append('\n').Append(value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A conservative "letter-dropping typo" check: <paramref name="prop"/> is a subsequence of
    /// <paramref name="word"/> (same first character, at most 4 dropped letters, prop &gt;= 4 chars) —
    /// the shape of the real 'ested' vs 'estimated' hallucination. Never flags a prop that is
    /// longer than the known word (a genuine new property like 'deleted' next to 'delete').
    /// </summary>
    internal static bool IsPlausibleTypo(string prop, string word)
    {
        if (prop.Length < 4 || word.Length < 4) return false;
        if (prop.Length > word.Length || word.Length - prop.Length > 4) return false;
        if (prop[0] != word[0]) return false;
        // The word itself is NOT a typo of itself — dropping zero letters isn't a typo.
        // (Unreachable through DetectHallucinatedProperties, where identical props are
        // filtered earlier; kept honest so the fuzz corpus can assert identity is safe.)
        if (string.Equals(prop, word, StringComparison.Ordinal)) return false;
        var wi = 0;
        foreach (var c in prop)
        {
            wi = word.IndexOf(c, wi);
            if (wi < 0) return false;
            wi++;
        }
        return true;
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
            // NOTE: never use ?? here — a non-participating group returns "" (not null), so
            // `??` collapses a BARE-key match (group 3) to "" and the key is silently dropped,
            // making the duplicate guard a no-op for the common `name:` shape. Empty-string
            // checks are required for the alternation to select the participating group.
            var g1 = m.Groups[1].Value;
            var g2 = m.Groups[2].Value;
            var key = (g1.Length > 0 ? g1 : g2.Length > 0 ? g2 : m.Groups[3].Value).Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (!oldCounts.ContainsKey(key)) oldCounts[key] = 0;
            oldCounts[key]++;
        }
        var newCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in keyRegex.Matches(cleanNew))
        {
            // NOTE: never use ?? here — a non-participating group returns "" (not null), so
            // `??` collapses a BARE-key match (group 3) to "" and the key is silently dropped,
            // making the duplicate guard a no-op for the common `name:` shape. Empty-string
            // checks are required for the alternation to select the participating group.
            var g1 = m.Groups[1].Value;
            var g2 = m.Groups[2].Value;
            var key = (g1.Length > 0 ? g1 : g2.Length > 0 ? g2 : m.Groups[3].Value).Trim();
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

    private static readonly Regex GroupingVerbRegex = new(
        @"\b(group|aggregate|bucket|categor|organiz|organis|cluster|partition|consolidat)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Line-start `key: {` / `key: [{` group headers — the shape of a grouped output.
    /// A flat data file (entries on their own lines, `{ name: ... }`) never matches.</summary>
    private static readonly Regex GroupedHeaderRegex = new(
        @"^\s*(?:'[^']*'|""[^""]*""|\w+)\s*:\s*\[?\{",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>A brace-object literal containing a key: pair (no nested braces) — one per data
    /// entry in both the flat and grouped shapes. For a grouped output `key: [{ e1 }, { e2 }],`
    /// the group-array's opening brace pairs with the first entry's closing brace, so every
    /// entry still contributes exactly one match — the count is invariant to the grouping.</summary>
    private static readonly Regex EntryObjectRegex = new(
        @"\{[^{}]*:[^{}]*\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Mirrors <see cref="DetectDuplicatePropertyAddition"/> for the OTHER aggregation
    /// hallucination: grouped output that DROPS entries. The duplicate guard rejects a group
    /// key declared twice (a merge artifact); this guard rejects a grouping whose total entry
    /// count fell below the input's (5 vs 6) — the "confident" merge that keeps every group
    /// key, reads perfectly well, and silently discards a row.
    /// Fires only for an aggregation edit, all four conditions required:
    ///   • the change description names a grouping/aggregation verb (an empty description
    ///     falls back to shape-only so direct callers without one still get the protection),
    ///   • the OLD content is flat (no group headers), the NEW content is grouped
    ///     (line-start `key: [{` headers) — the classic flat→grouped transform,
    ///   • the new string has FEWER object-literal entries than the old.
    /// A delete/remove edit (fewer entries, but not grouped and not described as grouping) and
    /// a regrouping of already-grouped data are deliberately out of scope.
    /// </summary>
    public static string? DetectDroppedEntriesInGroupedOutput(string oldStr, string newStr, string? changeDesc = null)
    {
        if (!string.IsNullOrWhiteSpace(changeDesc) && !GroupingVerbRegex.IsMatch(changeDesc)) return null;
        string StripStrings(string s)
        {
            s = Regex.Replace(s, @"`[^`]*`", "``", RegexOptions.Singleline);
            s = Regex.Replace(s, @"""[^""]*""", "\"\"", RegexOptions.Singleline);
            s = Regex.Replace(s, @"'[^']*'", "''", RegexOptions.Singleline);
            return s;
        }
        var cleanOld = StripStrings(oldStr);
        var cleanNew = StripStrings(newStr);
        if (!GroupedHeaderRegex.IsMatch(cleanNew) || GroupedHeaderRegex.IsMatch(cleanOld)) return null;
        var oldEntries = EntryObjectRegex.Matches(cleanOld).Count;
        var newEntries = EntryObjectRegex.Matches(cleanNew).Count;
        if (newEntries < oldEntries)
        {
            return $"GROUPED OUTPUT DROPS ENTRIES — newString contains {newEntries} entries but oldString had {oldEntries}. " +
                   "Grouping/aggregation must preserve EVERY input entry exactly once — do not merge, collapse, or drop rows. " +
                   "Reproduce each input entry in the grouped structure.";
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
            // COUNTING GUARD: an ALL-OCCURRENCE request ("rename every occurrence of X",
            // "replace all instances") with an ambiguous anchor must NOT guess a single
            // victim — applying to one occurrence renames only 1 of N and silently corrupts
            // the data. This fires before keyword scoring so a description naming the symbol
            // (which always appears in the file and would "disambiguate" to the last match)
            // can never smuggle a partial rename through. Rejected here, the resolver retries
            // with a unique anchor instead.
            {
                // Only when there is NO line hint (targetLine <= 0). Line-numbered batch
                // edits (deterministic multi-match, "update all N defaults") legitimately use
                // each sub-edit's line number as the disambiguation and must stay exempt.
                var changeLower2 = (changeDesc ?? string.Empty).ToLowerInvariant();
                if (targetLine <= 0 && Regex.IsMatch(changeLower2,
                        @"\b(every|each|all|any|both)\b|\boccurrence\b|\beverywhere\b|\bthroughout\b|\ball\s+(?:instances|occurrences|usages)\b"))
                {
                    var cfFirstLine = normOld.Split('\n')[0].Trim();
                    var cfUniqueLine = ExtractMostUniqueLine(normOld, normFile);
                    var cfErr = $"oldString found {matches.Count} times in file and the change requests an ALL-OCCURRENCE edit — " +
                                "applying to one occurrence would rename only 1 of " + matches.Count + " and corrupt the data. " +
                                "Provide a UNIQUE anchor (the whole file, or the full line with its unique surroundings) so every " +
                                "occurrence is replaced in one edit.";
                    if (cfUniqueLine != null)
                        cfErr += $" OR use ONLY this unique line as your entire oldString: `{cfUniqueLine.Trim()}`";
                    return (false, fileContent, cfErr, cfFirstLine);
                }
            }
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

    /// <summary>
    /// Small-anchor "surrounding line" re-anchor. When a small plan oldString (2-3 lines)
    /// fails to match verbatim at apply time, try re-anchoring it against each surrounding
    /// line of the file: shift the anchor up/down by a line, extend it by the line above or
    /// below (the file gained a line the plan missed), or trim its first/last line (the plan
    /// included a stale line that no longer exists). Returns the file-EXACT replacement block
    /// plus its 0-based start line when exactly ONE surrounding alignment is confident — the
    /// caller applies it deterministically instead of escalating to the LLM resolver.
    /// Returns null when the anchor is not small, nothing aligns confidently, or the best
    /// alignment is ambiguous (a tie means the re-anchor would be guesswork).
    /// </summary>
    public static (string correctedBlock, int startLineIdx, int score)? TrySurroundingLineReanchor(
        string fileContent, string oldStr, int targetLine = 0, string? changeDesc = null,
        int maxAnchorLines = 3)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return null;
        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr);
        var oldLines = normOld.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        // Only SMALL anchors get the surrounding-line retry: 2-3 lines (the planner's RULE 17
        // shape). A 1-line anchor has no surrounding-line structure to exploit — the apply
        // loop's whole-file tolerant matcher plus BuildExactMatchBlock already cover its drift.
        if (oldLines.Count < 2 || oldLines.Count > maxAnchorLines) return null;
        var fileLines = normFile.Split('\n');
        // NOTE: no file-length guard against the anchor length here — a trim shape (plan has
        // a stale line) legitimately makes the file SHORTER than the plan. The per-shape
        // bounds checks below keep every window inside the file; a file too small for every
        // shape simply yields no candidates.
        if (fileLines.Length < 2) return null;
        var n = oldLines.Count;

        // Anchor position: the planner's stated line when given (0-indexed), else the best
        // global scan. The surrounding search stays near it — big drift is BuildExactMatchBlock's
        // (whole-file) job, or the LLM resolver's.
        var baseIdx = targetLine > 0
            ? Math.Clamp(targetLine - 1, 0, Math.Max(0, fileLines.Length - 1))
            : FindBestAnchorPosition(fileLines, oldLines);
        if (baseIdx < 0) return null;

        var window = maxAnchorLines;
        // Dedupe by block text: the same file window can be reached by multiple (delta, shape)
        // combos (e.g. extend-above at delta 0 == extend-below at delta -1) with identical
        // scores — those are ONE candidate, not ambiguity.
        var byBlock = new Dictionary<string, (int start, int score)>(StringComparer.Ordinal);
        for (var delta = -window; delta <= window; delta++)
        {
            foreach (var (start, len) in EnumerateAnchorShapes(baseIdx + delta, n))
            {
                if (len < 1 || start < 0 || start + len > fileLines.Length) continue;
                var score = ScoreAnchorWindowAlignment(oldLines, fileLines, start, len);
                if (score < 0) continue;
                var block = string.Join("\n", fileLines.Skip(start).Take(len));
                if (!byBlock.TryGetValue(block, out var existing) || score > existing.score)
                    byBlock[block] = (start, score);
            }
        }
        if (byBlock.Count == 0) return null;
        var ranked = byBlock
            .Select(kv => (block: kv.Key, start: kv.Value.start, score: kv.Value.score))
            .OrderByDescending(c => c.score)
            .ThenBy(c => Math.Abs(c.start - baseIdx))
            .ToList();
        var best = ranked[0];
        // Ambiguity guard: a second alignment with the SAME score means the re-anchor is
        // guesswork — escalate instead of applying at a coin-flip location.
        if (ranked.Count > 1 && ranked[1].score == best.score) return null;
        if (string.Equals(best.block, normOld, StringComparison.Ordinal)) return null;
        return (best.block, best.start, best.score);
    }

    /// <summary>Anchor shapes tried at each surrounding offset: same length, extended above,
    /// extended below, trimmed first line, trimmed last line. Trim shapes only fire for
    /// n ≥ 3 anchors: trimming a 2-line plan leaves just ONE verified line (as weak as a
    /// 1-line anchor, which is deliberately excluded) and ties with the same-length drift
    /// case, manufacturing ambiguity.
    /// </summary>
    private static IEnumerable<(int start, int len)> EnumerateAnchorShapes(int basePos, int n)
    {
        yield return (basePos, n);          // same length, shifted by delta
        yield return (basePos - 1, n + 1);  // extended above — file gained a line before the anchor
        yield return (basePos, n + 1);      // extended below — file gained a line after the anchor
        if (n >= 3)
        {
            yield return (basePos + 1, n - 1);  // trimmed first — plan included a stale first line
            yield return (basePos, n - 1);      // trimmed last — plan included a stale last line
        }
    }

    /// <summary>
    /// Scores a candidate window against the plan anchor. Returns -1 when the alignment is
    /// not confident: same-length allows ONE drifted line (≥ n-1 matches); extend/trim shapes
    /// require FULL alignment (every line matched, with the extra line dropped from one side).
    /// The strict full-alignment bar for extend/trim is what makes those shapes safe — a plan
    /// line that drifted outright (not just an extra/missing line) escalates instead.
    /// </summary>
    private static int ScoreAnchorWindowAlignment(List<string> plan, string[] fileLines, int start, int len)
    {
        var n = plan.Count;
        if (len == n)
        {
            var score = 0;
            for (var i = 0; i < n; i++)
                if (LinesTolerantlyMatch(plan[i], fileLines[start + i])) score++;
            return score >= Math.Max(1, n - 1) ? score : -1;
        }
        if (len == n + 1)
        {
            // File gained a line: the plan must align with the window minus ONE line (the
            // dropped line is the extra one the plan never saw) — every plan line must match.
            for (var drop = 0; drop <= n; drop++)
            {
                var score = 0;
                var wi = 0;
                for (var pi = 0; pi < n; pi++)
                {
                    if (wi == drop) wi++; // skip the dropped window line
                    if (wi >= len) break;
                    if (LinesTolerantlyMatch(plan[pi], fileLines[start + wi])) score++;
                    wi++;
                }
                if (score == n) return n;
            }
            return -1;
        }
        if (len == n - 1)
        {
            // Plan included a stale line: the window must align with the plan minus ONE line
            // — every window line must match.
            for (var drop = 0; drop < n; drop++)
            {
                var score = 0;
                var wi = 0;
                for (var pi = 0; pi < n; pi++)
                {
                    if (pi == drop) continue;
                    if (wi >= len) break;
                    if (LinesTolerantlyMatch(plan[pi], fileLines[start + wi])) score++;
                    wi++;
                }
                if (score == n - 1) return n - 1;
            }
            return -1;
        }
        return -1;
    }

    /// <summary>Best whole-file position for a small anchor (used when the planner gave no
    /// line number): the offset where the most plan lines match in sequence.</summary>
    private static int FindBestAnchorPosition(string[] fileLines, List<string> plan)
    {
        var bestIdx = -1;
        var bestScore = -1;
        for (var i = 0; i < fileLines.Length; i++)
        {
            var score = 0;
            for (var j = 0; j < plan.Count && i + j < fileLines.Length; j++)
                if (LinesTolerantlyMatch(plan[j], fileLines[i + j])) score++;
            if (score > bestScore) { bestScore = score; bestIdx = i; }
        }
        return bestScore > 0 ? bestIdx : 0;
    }

    /// <summary>Tolerant per-line comparison for the surrounding-line re-anchor: trimmed
    /// equality, whitespace-stripped equality, case-insensitive equality, or a prefix match
    /// of a ≥4-char token (short prefixes like "b" over-match "button" and are excluded).</summary>
    private static bool LinesTolerantlyMatch(string a, string b)
    {
        var at = a.Trim();
        var bt = b.Trim();
        if (at == bt) return true;
        if (Regex.Replace(at, @"\s+", "") == Regex.Replace(bt, @"\s+", "")) return true;
        if (string.Equals(at, bt, StringComparison.OrdinalIgnoreCase)) return true;
        return at.Length >= 4 && bt.Length >= 4 &&
               (at.StartsWith(bt, StringComparison.Ordinal) || bt.StartsWith(at, StringComparison.Ordinal));
    }

    /// <summary>Words too generic to serve as an anchor identifier (type names, modifiers,
    /// keywords) — "musicTodoCount" anchors an edit; "number"/"null" would match every
    /// declaration in the file.</summary>
    private static readonly HashSet<string> AnchorStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "number", "null", "true", "false", "void", "string", "boolean", "object", "array",
        "public", "private", "protected", "readonly", "static", "const", "let", "var",
        "this", "return", "import", "export", "async", "await", "default", "class",
        "interface", "function", "new", "undefined", "value", "index", "count", "list",
        "data", "items", "item", "name", "type", "date", "time"
    };

    /// <summary>Identifier tokens (≥5 chars, snake/camel/Pascal/dotted-safe) extracted from
    /// <paramref name="code"/>, minus generic type/keyword words. These are the anchor words
    /// the edit itself names — the most trustworthy pointer to where the edit belongs.</summary>
    public static List<string> ExtractAnchorIdentifierTokens(string code)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(code)) return result;
        foreach (Match m in Regex.Matches(code, @"\b[A-Za-z_][A-Za-z0-9_]{4,}\b"))
        {
            if (AnchorStopWords.Contains(m.Value)) continue;
            result.Add(m.Value);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Grounds an oldString to the real file: picks its RAREST identifier token (the
    /// most distinctive word the edit itself names) and returns that token plus every file
    /// line containing it (whole-word). Returns null when the oldString carries no usable
    /// identifier or the token is too common (&gt; <paramref name="maxOccurrences"/>) to be
    /// distinctive. This is the "introspect on the new variable name" step — instead of
    /// re-bouncing a drifted anchor to the LLM, find where the anchor's own word actually
    /// lives in the file.</summary>
    private static (string token, List<int> lineIdxs)? GroundAnchorToken(
        string fileContent, string oldStr, int maxOccurrences = 3)
    {
        var tokens = ExtractAnchorIdentifierTokens(oldStr);
        if (tokens.Count == 0) return null;
        string? best = null;
        var bestCount = int.MaxValue;
        foreach (var t in tokens)
        {
            var c = CountWordOccurrences(fileContent, t);
            if (c > 0 && c < bestCount) { bestCount = c; best = t; }
        }
        if (best == null || bestCount > maxOccurrences) return null;
        var fileLines = fileContent.Split('\n');
        var lines = new List<int>();
        for (var i = 0; i < fileLines.Length; i++)
            if (ContainsWord(fileLines[i], best)) lines.Add(i);
        return lines.Count > 0 ? (best, lines) : null;
    }

    /// <summary>
    /// Identifier-grounded re-anchor for a drifted oldString. When the anchor failed verbatim
    /// (whitespace/line drift — e.g. the model dropped indentation, or the file gained a
    /// line), this finds the anchor's OWN identifier in the real file and rebuilds the
    /// replacement block from the ACTUAL file text (real indentation, real surrounding
    /// lines), so the edit applies deterministically instead of escalating to the LLM — which
    /// re-emits the same drifted anchor and burns the retry budget (the benchmark-22 loop:
    /// the same 80-char oldString 3× → abort). Because the block is grounded on an identifier
    /// the OLDSTRING itself names, it can never select an unrelated block (e.g. a
    /// "tradeNotifsCount" line) the tolerant matcher would otherwise pick.
    /// Returns null when grounding is ambiguous, the oldString's sibling lines don't map to
    /// real lines (a fabricated line — the LLM must see the real file and fix its own
    /// anchor), or the alignment is not confident.
    /// </summary>
    public static (string correctedBlock, int startLineIdx, int score)? TryIdentifierAnchoredReanchor(
        string fileContent, string oldStr, int targetLine = 0)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return null;
        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr);
        var oldLines = normOld.Split('\n'); // keep blanks — the walk aligns positionally
        var grounded = GroundAnchorToken(normFile, normOld);
        if (grounded == null) return null;
        var (token, candidates) = grounded.Value;

        var aIdx = ResolveAnchorLine(candidates, normFile, normOld, token, targetLine);
        if (aIdx == null) return null;
        var fileLines = normFile.Split('\n');

        // Which old line does the anchor file line correspond to? It must tolerantly match
        // one of the old lines (the token line is that line's content).
        var anchorOldIdx = -1;
        for (var oi = 0; oi < oldLines.Length; oi++)
            if (!string.IsNullOrWhiteSpace(oldLines[oi]) && LinesTolerantlyMatch(oldLines[oi], fileLines[aIdx.Value]))
            { anchorOldIdx = oi; break; }
        if (anchorOldIdx < 0) return null;

        var startIdx = aIdx.Value - anchorOldIdx;
        if (startIdx < 0) return null;

        // Blank-tolerant positional walk: every non-blank old line must map to a matching real
        // line in order (blank differences on either side are absorbed). Any real mismatch
        // (e.g. a fabricated sibling line) → null → the resolver gets the real-content hint.
        var fi = startIdx;
        var o = 0;
        while (o < oldLines.Length)
        {
            if (fi >= fileLines.Length) return null;
            var fBlank = string.IsNullOrWhiteSpace(fileLines[fi]);
            var oBlank = string.IsNullOrWhiteSpace(oldLines[o]);
            if (fBlank && oBlank) { fi++; o++; continue; }
            if (fBlank) { fi++; continue; }
            if (oBlank) { o++; continue; }
            if (!LinesTolerantlyMatch(oldLines[o], fileLines[fi])) return null;
            fi++; o++;
        }
        var block = string.Join("\n", fileLines.Skip(startIdx).Take(fi - startIdx));
        if (string.IsNullOrWhiteSpace(block)) return null;
        if (string.Equals(block, normOld, StringComparison.Ordinal)) return null; // already exact
        return (block, startIdx, oldLines.Count(l => !string.IsNullOrWhiteSpace(l)));
    }

    /// <summary>Resolves which candidate file line is the anchor. One candidate wins outright;
    /// otherwise the candidate whose line trimmed-equals the old line containing the token
    /// (the DECLARATION vs identical-token USAGE lines) wins; otherwise a targetLine hint
    /// picks the nearest (within 50 lines). Ambiguous → null.</summary>
    private static int? ResolveAnchorLine(
        List<int> candidates, string normFile, string normOld, string token, int targetLine)
    {
        if (candidates.Count == 1) return candidates[0];
        var oldLines = normOld.Split('\n');
        var oiOfToken = -1;
        for (var oi = 0; oi < oldLines.Length; oi++)
            if (!string.IsNullOrWhiteSpace(oldLines[oi]) && ContainsWord(oldLines[oi], token))
            { oiOfToken = oi; break; }
        if (oiOfToken >= 0)
        {
            var fileLines = normFile.Split('\n');
            var oldTrim = oldLines[oiOfToken].Trim();
            var trimmed = candidates
                .Where(i => string.Equals(fileLines[i].Trim(), oldTrim, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (trimmed.Count == 1) return trimmed[0];
        }
        if (targetLine > 0)
        {
            var nearest = candidates.OrderBy(i => Math.Abs(i + 1 - targetLine)).First();
            if (Math.Abs(nearest + 1 - targetLine) <= 50) return nearest;
        }
        return null;
    }

    /// <summary>Display-only probe for the resolver feedback: returns the REAL file lines
    /// around the oldString's most distinctive identifier (the anchor line ± its neighbors),
    /// so the model can copy the correct verbatim text when its own oldString drifted.
    /// Unlike <see cref="TryIdentifierAnchoredReanchor"/> it does not require the sibling
    /// lines to map — it shows where the anchor lives even when the rest of the oldString
    /// was fabricated (the model then fixes its own anchor — an "edit an edit").</summary>
    public static string? FindIdentifierGroundedLines(string fileContent, string oldStr)
    {
        if (string.IsNullOrWhiteSpace(oldStr)) return null;
        var normFile = NormalizeLineEndings(fileContent);
        var normOld = NormalizeLineEndings(oldStr);
        var grounded = GroundAnchorToken(normFile, normOld);
        if (grounded == null) return null;
        var (token, candidates) = grounded.Value;
        var best = ResolveAnchorLine(candidates, normFile, normOld, token, 0);
        if (best == null) return null;
        var fileLines = normFile.Split('\n');
        var start = Math.Max(0, best.Value - 1);
        var end = Math.Min(fileLines.Length, best.Value + 2);
        return string.Join("\n", fileLines.Skip(start).Take(end - start));
    }

    private static int CountWordOccurrences(string text, string word) =>
        Regex.Matches(text, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase).Count;

    private static bool ContainsWord(string line, string word) =>
        Regex.IsMatch(line, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase);

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

    /// <summary>
    /// Detects LLM-authored placeholder stubs in replacement code. <paramref name="preExisting"/>
    /// (normally the step's oldString) marks lines that are carried over UNCHANGED from the file —
    /// those lines were authored by the pre-edit code, not by the model, so they must never be
    /// counted as stubs (a plan edit that keeps an idiomatic empty `constructor() { }` while adding
    /// a real method is a perfect example: the constructor line used to trip the empty-body check).
    /// </summary>
    public static bool LooksLikePlaceholderStub(string code, string? preExisting = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        // Carried-over lines (present verbatim in the old code) are pre-existing context, not
        // LLM-authored stubs — drop them before any line-based stub analysis. Comment-only
        // lines are carried over by their RAW form (their comment-stripped form is blank).
        var carriedOver = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(preExisting))
        {
            foreach (var l in preExisting.Split('\n'))
            {
                var raw = l.Trim();
                if (raw.Length == 0) continue;
                var t = Regex.Replace(raw, @"//[^\n]*", " ").Trim();
                if (t.Length > 0) carriedOver.Add(t);
                else carriedOver.Add(raw);
            }
        }
        // The raw-text stub regexes below (placeholder comments, NotImplemented throws) run on
        // the WHOLE code string — they must also see only MODEL-AUTHORED lines, so build the
        // carried-stripped view (original lines kept, comments intact) for them.
        static string NewOnly(string src, HashSet<string> carried) =>
            string.Join("\n", src.Split('\n').Where(l =>
            {
                var raw = l.Trim();
                if (raw.Length == 0) return false;
                var t = Regex.Replace(raw, @"//[^\n]*", " ").Trim();
                return !(t.Length > 0 && carried.Contains(t)) && !carried.Contains(raw);
            }));
        var newOnlyCode = NewOnly(code, carriedOver);
        static List<string> NewLines(string src, HashSet<string> carried) =>
            Regex.Replace(src, @"//[^\n]*", " ").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l != "{" && l != "}" && l != "{ }" && !carried.Contains(l))
                .ToList();

        // Explicit placeholder comments
        if (Regex.IsMatch(newOnlyCode,
            @"//\s*(placeholder\s*(implementation|stub)|TODO\s*:?\s*(implement|add|fill\s*in)|stub\s+implementation|will\s+be\s+wired\s+up|not\s+implemented\s+yet|dummy\s+implementation|temporary\s+implementation)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled))
            return true;

        // NotImplementedException / NotSupportedException stubs
        if (Regex.IsMatch(newOnlyCode, @"throw\s+new\s+(NotImplementedException|NotSupportedException)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled))
            return true;

        // Empty method body: `name(...): type { }` or arrow `(...) => { }`.
        // Whole-line anchored and dominance-guarded so legitimate code like
        // `if (x) { }`, `for (;;) { }`, `while (x) { }`, `new Foo() { }` or an object
        // literal containing one empty helper is NOT flagged — only a stub that is
        // essentially an empty declaration gets rejected.
        var meaningfulLines = NewLines(code, carriedOver);
        if (meaningfulLines.Count <= 3)
        {
            // Bare `() => { }` or a NAMED arrow (`const onSave = () => { };`) — an empty
            // arrow body is a stub no matter which side of the `=` it sits on. The trailing
            // `,?;?` admits array members and statement terminators.
            var arrowEmpty = meaningfulLines.Any(l =>
                Regex.IsMatch(l,
                    @"^(?:(?:const|let|var)\s+\w+\s*=\s*)?\([^)]*\)\s*=>\s*\{\s*\}\s*,?;?\s*$",
                    RegexOptions.Compiled));
            var methodEmpty = meaningfulLines.Any(l =>
            {
                var m = Regex.Match(l,
                    @"^(?:(?:public|private|protected|internal|readonly|static|async|export|default|function|def|const|let|var)\s+)*(?<name>\w+)\s*\([^)]*\)\s*(:\s*[^{}\r\n]{0,80})?\s*\{\s*\}\s*,?\s*$",
                    RegexOptions.Compiled);
                // Empty constructors/destructors are idiomatic (field-initializer classes, DI
                // shells) — they are NOT stubs. Only empty NEW methods get flagged.
                return m.Success &&
                       !ControlFlowKeywords.Contains(m.Groups["name"].Value) &&
                       !m.Groups["name"].Value.Equals("constructor", StringComparison.OrdinalIgnoreCase) &&
                       !m.Groups["name"].Value.Equals("destructor", StringComparison.OrdinalIgnoreCase);
            });
            if (arrowEmpty || methodEmpty)
                return true;
        }

        // Single-line console.log stub: `name(...): void { console.log('x'); }`
        if (Regex.IsMatch(Regex.Replace(code, @"//[^\n]*", " ").Trim(),
            @"^\w+\s*\([^)]*\)\s*(:\s*[^{\r\n]{0,60})?\s*\{\s*console\.(log|error|warn|info)\([^;]*\);?\s*\}\s*;?$",
            RegexOptions.Compiled))
            return true;

        // Console.log-only body: a block whose only meaningful statements are console.* calls.
        // Signature lines like `showMenuPanel(): void {` are stripped (they end with `{`),
        // so a body that only logs still gets caught.
        var strippedComments = Regex.Replace(code, @"//[^\n]*", " ");
        var meaningful = strippedComments.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != "{" && l != "}" && l != "{ }" && !carriedOver.Contains(l))
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
