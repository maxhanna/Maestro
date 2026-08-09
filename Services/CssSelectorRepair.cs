using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Deterministic CSS auto-repair for the classic missing-dot bug: an edit adds a rule whose
/// selector names a class WITHOUT the leading '.' (e.g. 'favoritesTable tbody tr td a {'
/// when the file defines '.favouritesTable'). The LLM verifier can't catch it — it only
/// sees old/new snippets, never the real file — so the rule silently never applies. This
/// repair scans the ACTUAL file content: for every bare, class-like token in a selector it
/// looks up classes DEFINED in the same file and, on an exact / case-insensitive / small
/// edit-distance match, rewrites the token to '.&lt;correct-spelling&gt;'. HTML element
/// names, pseudo-classes, attributes, comments and strings are never touched, and no token
/// is rewritten unless a matching class actually exists — so it cannot invent selectors.
/// Rules nested inside @media/@supports at-rule blocks (and CSS-nested rules) are descended
/// into and get the same repair.
/// </summary>
public static class CssSelectorRepair
{
    private static readonly Regex ClassTokenRegex = new(@"\.([A-Za-z_][A-Za-z0-9_-]*)");
    private const int MaxEditDistance = 2;
    private const int MaxIssuesPerFile = 10;

    private static readonly HashSet<string> HtmlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "html","head","body","title","base","link","meta","style","script","noscript","template",
        "main","section","article","aside","header","footer","nav","div","span","p","a","em","strong",
        "i","b","u","s","small","sub","sup","br","hr","wbr","ul","ol","li","dl","dt","dd","figure",
        "figcaption","table","caption","colgroup","col","tbody","thead","tfoot","tr","td","th","form",
        "fieldset","legend","label","input","button","select","datalist","optgroup","option","textarea",
        "output","progress","meter","details","summary","dialog","menu","img","picture","source","audio",
        "video","iframe","embed","object","canvas","map","area","svg","math","mark","time","data","code",
        "var","samp","kbd","ruby","rt","rp","bdi","bdo","address","blockquote","q","cite","del","ins",
        "abbr","dfn","pre","h1","h2","h3","h4","h5","h6"
    };

    private sealed class Rule
    {
        public string Selector = "";
        public int Start;      // index of the first non-whitespace char of the selector in css
        public int SelectorLen; // length of the (trimmed) selector text
        public bool IsAtRule;
        public List<Rule>? Nested; // rules inside an at-rule block (@media/@supports/…)
    }

    /// <summary>
    /// Rewrites bare class tokens in selectors to '.&lt;defined class&gt;' using classes defined
    /// in the same file. Returns the (possibly unchanged) content plus one warning per repair.
    /// </summary>
    public static (string content, List<string> warnings) RepairBareClassSelectors(string css)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(css)) return (css, warnings);
        var rules = ParseRules(css);
        var definedClasses = ExtractDefinedClasses(rules);
        if (definedClasses.Count == 0) return (css, warnings);

        var edits = new List<(int start, int len, string replacement)>();
        CollectRepairs(rules, definedClasses, warnings, edits);
        if (edits.Count == 0) return (css, warnings);

        var result = css;
        // Apply from the END so earlier offsets stay valid.
        foreach (var e in edits.OrderByDescending(e => e.start))
            result = result[..e.start] + e.replacement + result[(e.start + e.len)..];
        return (result, warnings);
    }

    private static string? RepairSelector(string selector, HashSet<string> definedClasses, List<string> warnings)
    {
        var result = selector;
        var repairedAny = false;
        foreach (var tok in SelectorTokens(selector))
        {
            var match = FindClassMatch(tok, definedClasses);
            if (match == null) continue;
            var pattern = new Regex(@"\b" + Regex.Escape(tok) + @"\b");
            var updated = pattern.Replace(result, "." + match);
            if (updated == result) continue;
            result = updated;
            repairedAny = true;
            warnings.Add($"CSS selector '{selector}' uses bare '{tok}' — repaired to '.{match}' (a class defined in the same file). A selector without the '.' never matches; verify the repaired rule still says what you intended.");
        }
        return repairedAny ? result : null;
    }

    private static string? FindClassMatch(string token, HashSet<string> definedClasses)
    {
        if (definedClasses.Contains(token)) return token;
        foreach (var cls in definedClasses)
            if (string.Equals(token, cls, StringComparison.OrdinalIgnoreCase)) return cls;
        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var cls in definedClasses)
        {
            // Fuzzy fallback only for reasonably long classes and small diffs — 'favoritesTable'
            // → 'favouritesTable' is one transposed/inserted character; anything bigger is
            // probably a different word, not a missing dot.
            if (cls.Length < 5) continue;
            if (Math.Abs(cls.Length - token.Length) > MaxEditDistance) continue;
            var d = Levenshtein(token, cls, MaxEditDistance);
            if (d <= MaxEditDistance && d < bestDist) { bestDist = d; best = cls; }
        }
        return best;
    }

    /// <summary>
    /// Deterministic post-execution check (mirrors the template-binding check): scans CSS for
    /// bare class-like selector tokens — a class name WITHOUT the leading '.' (e.g.
    /// 'favoritesTable tbody tr td a {' when the file defines '.favouritesTable') — that match
    /// a class defined in the same file, and returns CONFIRMED issue strings with the repair
    /// suggestion. Uses the SAME matching as <see cref="RepairBareClassSelectors"/>, so anything
    /// flagged here is exactly what the deterministic repair would fix; it never rewrites, it
    /// only reports. When <paramref name="preEditCss"/> is supplied, tokens already present in
    /// the pre-edit content are skipped so pre-existing broken selectors are not attributed to
    /// the current run's edits.
    /// </summary>
    public static List<string> FindBareClassSelectorIssues(string relPath, string css, string? preEditCss = null)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(css)) return issues;
        var rules = ParseRules(css);
        var definedClasses = ExtractDefinedClasses(rules);
        if (definedClasses.Count == 0) return issues;
        var preTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (preEditCss != null)
        {
            var preRules = ParseRules(preEditCss);
            var preClasses = ExtractDefinedClasses(preRules);
            if (preClasses.Count > 0)
                foreach (var (_, tok, _) in FindBareClassTokens(preEditCss, preClasses))
                    preTokens.Add(tok);
        }
        foreach (var (selector, tok, match) in FindBareClassTokens(css, definedClasses))
        {
            // Pre-existing bare tokens (present before this run) are not the agent's doing —
            // skip them exactly like pre-existing template bindings are skipped.
            if (preEditCss != null && preTokens.Contains(tok)) continue;
            issues.Add(
                $"CSS selector '{selector}' in {relPath} uses bare '{tok}' — prefix with '.' ('.{match}') or the rule never matches. " +
                $"(Deterministic bare-selector check — a class named '{match}' is defined in the same file.)");
            if (issues.Count >= MaxIssuesPerFile) break;
        }
        return issues;
    }

    /// <summary>
    /// Scans the modified files of a run for bare class-like selector tokens (mirrors
    /// <c>TemplateBindingValidator.CheckModifiedTemplates</c>). Returns CONFIRMED issue strings
    /// for every bare token in a modified .css file that matches a class defined in that file;
    /// pre-edit snapshots (keyed by relative path) restrict findings to tokens INTRODUCED by the
    /// run.
    /// </summary>
    public static List<string> CheckModifiedCss(string projectRoot, IEnumerable<string> modifiedRelPaths,
        Dictionary<string, string>? preEditSnapshots = null)
    {
        var issues = new List<string>();
        foreach (var rel in modifiedRelPaths)
        {
            if (!string.Equals(Path.GetExtension(rel), ".css", StringComparison.OrdinalIgnoreCase)) continue;
            var normRel = rel.Replace('\\', '/');
            var full = SafeFullPath(projectRoot, rel);
            if (full == null || !System.IO.File.Exists(full)) continue;
            var css = System.IO.File.ReadAllText(full);
            string? preEdit = null;
            if (preEditSnapshots != null)
            {
                // Normalize both sides so 'maxhanna.client\\src\\app\\x.css' matches
                // 'maxhanna.client/src/app/x.css' regardless of which the caller used.
                foreach (var kv in preEditSnapshots)
                {
                    if (string.Equals(kv.Key.Replace('\\', '/'), normRel, StringComparison.OrdinalIgnoreCase))
                    {
                        preEdit = kv.Value;
                        break;
                    }
                }
            }
            issues.AddRange(FindBareClassSelectorIssues(normRel, css, preEdit));
        }
        return issues;
    }

    private static string? SafeFullPath(string projectRoot, string relPath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(projectRoot);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }
        catch { return null; }
    }

    /// <summary>Finds (selector, token, matched class) for every bare class-like token in the
    /// file that matches a defined class. Dedupes repeated tokens so a class misspelled in many
    /// rules yields one finding, not a wall of noise.</summary>
    private static List<(string selector, string token, string match)> FindBareClassTokens(string css, HashSet<string> definedClasses)
    {
        var found = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(css) || definedClasses.Count == 0) return found;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(List<Rule> rules)
        {
            foreach (var r in rules)
            {
                if (!r.IsAtRule)
                {
                    foreach (var tok in SelectorTokens(r.Selector))
                    {
                        var match = FindClassMatch(tok, definedClasses);
                        if (match == null) continue;
                        if (seen.Add(tok + "|" + match))
                            found.Add((r.Selector, tok, match));
                    }
                }
                if (r.Nested != null) Walk(r.Nested);
            }
        }
        Walk(ParseRules(css));
        return found;
    }

    private static IEnumerable<string> SelectorTokens(string selector)
    {
        var parts = selector.Split(
            new[] { ' ', '\t', '\n', '\r', '>', '+', '~', ',', '(', '[', '*', '&' },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in parts)
        {
            var tok = raw.Trim();
            if (tok.Length < 3) continue;
            var first = tok[0];
            if (first is '.' or '#' or ':' or '@' or '-' or '"' or '\'') continue;
            if (char.IsDigit(first)) continue;
            // Pseudo-classes (:last-child), attribute selectors, quoted strings — not classes.
            if (tok.IndexOfAny(new[] { ':', '.', '(', ')', '[', ']', '!', '"', '\'', '#' }) >= 0) continue;
            if (HtmlElements.Contains(tok)) continue;
            yield return tok;
        }
    }

    private static void CollectRepairs(List<Rule> rules, HashSet<string> definedClasses, List<string> warnings,
        List<(int start, int len, string replacement)> edits)
    {
        foreach (var r in rules)
        {
            if (r.Nested != null) CollectRepairs(r.Nested, definedClasses, warnings, edits);
            // At-rule selectors themselves (e.g. '@media …') are never candidates — only the
            // rules nested inside their blocks are (collected above).
            if (r.IsAtRule) continue;
            var repaired = RepairSelector(r.Selector, definedClasses, warnings);
            if (repaired != null)
                edits.Add((r.Start, r.SelectorLen, repaired));
        }
    }

    private static HashSet<string> ExtractDefinedClasses(List<Rule> rules)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            foreach (Match m in ClassTokenRegex.Matches(r.Selector))
                classes.Add(m.Groups[1].Value);
            if (r.Nested != null)
                classes.UnionWith(ExtractDefinedClasses(r.Nested));
        }
        return classes;
    }

    /// <summary>Comment/string-aware scan of top-level rules; mirrors MergeDuplicateCssRules.</summary>
    private static List<Rule> ParseRules(string css)
    {
        var rules = new List<Rule>();
        var i = 0;
        var selectorStart = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                // A comment between rules advances the pending selector start so the next
                // selector excludes it (mirrors MergeDuplicateCssRules).
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var endPos = end >= 0 ? end + 2 : css.Length;
                selectorStart = endPos;
                i = endPos;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                i++;
                while (i < css.Length && css[i] != c)
                {
                    if (css[i] == '\\') i += 2;
                    else i++;
                }
                i++;
                continue;
            }
            if (c == '{')
            {
                var selector = css[selectorStart..i].Trim();
                var selStart = selectorStart;
                while (selStart < i && char.IsWhiteSpace(css[selStart])) selStart++;
                var (bodyEnd, nested) = ScanBlock(css, i + 1);
                rules.Add(new Rule
                {
                    Selector = selector,
                    Start = selStart,
                    SelectorLen = selector.Length,
                    IsAtRule = selector.StartsWith('@'),
                    Nested = nested.Count > 0 ? nested : null
                });
                i = bodyEnd;
                selectorStart = i;
                continue;
            }
            i++;
        }
        return rules;
    }

    /// <summary>
    /// Scans the body of a block starting just after its opening '{' (absolute index
    /// <paramref name="start"/>) down to the matching '}'. Returns the index just past that
    /// closing brace plus every rule found directly inside the block (at-rule or not); rules
    /// nested inside those carry their own <see cref="Rule.Nested"/>. Comment/string-aware,
    /// so a '}' inside a comment or string never closes the block.
    /// </summary>
    private static (int end, List<Rule> nested) ScanBlock(string css, int start)
    {
        var rules = new List<Rule>();
        var i = start;
        var selectorStart = start;
        var depth = 1;
        while (i < css.Length && depth > 0)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var endPos = end >= 0 ? end + 2 : css.Length;
                selectorStart = endPos;
                i = endPos;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                i++;
                while (i < css.Length && css[i] != c)
                {
                    if (css[i] == '\\') i += 2;
                    else i++;
                }
                i++;
                continue;
            }
            if (c == '{')
            {
                var selector = css[selectorStart..i].Trim();
                var selStart = selectorStart;
                while (selStart < i && char.IsWhiteSpace(css[selStart])) selStart++;
                var (innerEnd, innerRules) = ScanBlock(css, i + 1);
                rules.Add(new Rule
                {
                    Selector = selector,
                    Start = selStart,
                    SelectorLen = selector.Length,
                    IsAtRule = selector.StartsWith('@'),
                    Nested = innerRules.Count > 0 ? innerRules : null
                });
                i = innerEnd;
                selectorStart = i;
                continue;
            }
            if (c == '}')
            {
                depth--;
                if (depth == 0) return (i + 1, rules);
            }
            i++;
        }
        return (i, rules);
    }

    private static int Levenshtein(string a, string b, int cap)
    {
        if (Math.Abs(a.Length - b.Length) > cap) return cap + 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (cur[j] < rowMin) rowMin = cur[j];
            }
            if (rowMin > cap) return cap + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
