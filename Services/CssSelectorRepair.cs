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
    private const int MaxUnwiredIssuesPerFile = 10;

    /// <summary>True for stylesheet paths the CSS-wiring checks understand: .css, .scss and
    /// .less. SCSS/LESS compile to CSS but their rules style through the same connected
    /// templates/components, so a class defined there must be wired up exactly like a plain
    /// .css class.</summary>
    private static bool IsStylesheetPath(string rel)
    {
        var ext = Path.GetExtension(rel);
        return string.Equals(ext, ".css", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".scss", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".less", StringComparison.OrdinalIgnoreCase);
    }

    // Custom-property definitions ('--name:') and usages ('var(--name)'), plus comment/string
    // strippers used so definitions inside comments or content strings are never counted.
    private static readonly Regex CustomPropDefRegex = new(@"--([A-Za-z0-9_-]+)\s*:");
    private static readonly Regex CustomPropUseRegex = new(@"var\(\s*--([A-Za-z0-9_-]+)");
    private static readonly Regex CommentRegex = new(@"/\*[\s\S]*?\*/");
    private static readonly Regex StringLiteralRegex = new(@"""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'");

    // Classes inside :not(...) are matched elements that must NOT have the class — flagging them
    // as "defined" would demand a template reference that would actually break the rule.
    private static readonly Regex NotPseudoRegex = new(@":not\s*\([^)]*\)");

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

    /// <summary>
    /// Deterministic post-execution check that every class / custom property DEFINED by this
    /// run's stylesheet edits is actually WIRED UP: the class must appear in the connected
    /// template / component (the files the stylesheet affects), and a custom property must be
    /// consumed via <c>var(--name)</c> in the same file or referenced from the connected files.
    /// A run that creates a rule like '.flight-detail-body { … }' but never uses the class
    /// anywhere is dead code — the rule can never apply — so the job must not be marked
    /// complete until the class is referenced. Applies to .css, .scss and .less alike: SCSS /
    /// LESS compile to CSS but style through the same connected templates, so a class defined
    /// there must be wired up exactly like a plain .css class (the selector parser is
    /// comment/string-aware and descends into nested rules, so preprocessor nesting parses
    /// correctly). Mirrors <see cref="CheckModifiedCss"/>: only definitions that were
    /// NOT present in the pre-edit snapshot are judged (pre-existing unused classes are not
    /// attributed to the run); a file with no snapshot was created by the run, so every
    /// definition counts as new. Files with no connected template/component (standalone global
    /// stylesheets) are skipped — there is no wiring surface to judge.
    /// </summary>
    public static List<string> CheckUnwiredCssDefinitions(string projectRoot, IEnumerable<string> modifiedRelPaths,
        Dictionary<string, string>? preEditSnapshots = null)
    {
        var issues = new List<string>();
        foreach (var rel in modifiedRelPaths)
        {
            if (!IsStylesheetPath(rel)) continue;
            var normRel = rel.Replace('\\', '/');
            var full = SafeFullPath(projectRoot, rel);
            if (full == null || !System.IO.File.Exists(full)) continue;
            var css = System.IO.File.ReadAllText(full);
            string? preEdit = null;
            var haveSnapshot = false;
            if (preEditSnapshots != null)
            {
                foreach (var kv in preEditSnapshots)
                {
                    if (string.Equals(kv.Key.Replace('\\', '/'), normRel, StringComparison.OrdinalIgnoreCase))
                    {
                        preEdit = kv.Value;
                        haveSnapshot = true;
                        break;
                    }
                }
            }
            var (newClasses, newProps) = NewlyDefined(css, haveSnapshot ? preEdit : null);
            if (newClasses.Count == 0 && newProps.Count == 0) continue;
            var connected = ConnectedFileContents(projectRoot, full);
            if (connected.Count == 0) continue; // global/standalone stylesheet — no wiring surface.
            var haystack = string.Join("\n", connected.Select(c => c.content));
            var connectedNames = string.Join(", ", connected.Select(c => c.rel));
            var consumedInSameFile = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in CustomPropUseRegex.Matches(css))
                consumedInSameFile.Add(m.Groups[1].Value);
            foreach (var cls in newClasses.OrderBy(c => c, StringComparer.Ordinal))
            {
                if (IsReferenced(haystack, cls)) continue;
                // Not a literal reference — but the sibling component may still apply the class
                // at runtime through a variable: classList.add(this.stateClass),
                // querySelector('.' + cls), or a className assignment where the class name
                // arrives as a variable (@Input, computed value, composed string) and may
                // appear NOWHERE as a literal. Without this resolution a genuinely wired
                // dynamic class (e.g. el.classList.add(this.stateClass) with stateClass an
                // @Input) false-positives as unwired.
                if (IsDynamicallyWired(connected)) continue;
                issues.Add(
                    $"Newly created CSS class '.{cls}' in {normRel} is never referenced in the connected " +
                    $"template/component ({connectedNames}) — the rule will never apply. Wire it up: add the class to " +
                    $"the element it should style (class=\"{cls}\", [class.{cls}], ng-class, classList.add, or querySelector), or remove " +
                    $"the rule. (Deterministic unwired-CSS check — a class defined by this run must be used by the file it styles.)");
                if (issues.Count >= MaxUnwiredIssuesPerFile) return issues;
            }
            foreach (var p in newProps.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (consumedInSameFile.Contains(p)) continue;      // consumed via var() in the same file — wired.
                if (IsReferenced(haystack, "--" + p)) continue;    // set/consumed from the template/component.
                issues.Add(
                    $"Newly created CSS variable '--{p}' in {normRel} is never consumed (var(--{p})) in the same file nor " +
                    $"referenced in the connected template/component ({connectedNames}) — the variable does nothing. " +
                    $"Define it where it is consumed or remove it. (Deterministic unwired-CSS check.)");
                if (issues.Count >= MaxUnwiredIssuesPerFile) return issues;
            }
        }
        return issues;
    }

    /// <summary>
    /// Deterministic post-execution check, the mirror of <see cref="CheckUnwiredCssDefinitions"/>:
    /// a CSS class REMOVED by this run must not still be referenced by the connected template /
    /// component (the files the stylesheet affects). Deleting a rule while the template keeps
    /// using the class leaves the element pointing at a class that no longer exists — its styling
    /// silently breaks, and the LLM verifier can miss it because it judges snippets, not the
    /// cross-file picture. Only classes present in the pre-edit snapshot and absent from the
    /// current stylesheet are judged (a removal that predates the run is not attributed to it); a
    /// file with no snapshot was created by the run, so nothing was removed from it. Files with no
    /// connected template/component (standalone global stylesheets) are skipped — there is no
    /// wiring surface whose references could dangle. Uses the same whole-token reference test as
    /// the unwired check, so 'card' never matches 'card-body'.
    /// </summary>
    public static List<string> CheckOrphanedTemplateReferences(string projectRoot, IEnumerable<string> modifiedRelPaths,
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
            var haveSnapshot = false;
            if (preEditSnapshots != null)
            {
                foreach (var kv in preEditSnapshots)
                {
                    if (string.Equals(kv.Key.Replace('\\', '/'), normRel, StringComparison.OrdinalIgnoreCase))
                    {
                        preEdit = kv.Value;
                        haveSnapshot = true;
                        break;
                    }
                }
            }
            // No snapshot ⇒ the file was created by the run — nothing was removed from it.
            if (!haveSnapshot || string.IsNullOrWhiteSpace(preEdit)) continue;
            var removed = RemovedClasses(css, preEdit);
            if (removed.Count == 0) continue;
            var connected = ConnectedFileContents(projectRoot, full);
            if (connected.Count == 0) continue; // global/standalone stylesheet — no wiring surface.
            var haystack = string.Join("\n", connected.Select(c => c.content));
            var connectedNames = string.Join(", ", connected.Select(c => c.rel));
            foreach (var cls in removed.OrderBy(c => c, StringComparer.Ordinal))
            {
                if (!IsReferenced(haystack, cls)) continue;
                issues.Add(
                    $"CSS class '.{cls}' was REMOVED from {normRel} by this run, but the connected " +
                    $"template/component ({connectedNames}) still references it (class=\"{cls}\", [class.{cls}], " +
                    $"ng-class, or querySelector) — the element now points at a class that no longer exists, so its " +
                    $"styling silently breaks. Remove the reference from the template/component or restore the rule. " +
                    $"(Deterministic orphaned-template-reference check — a class removed by this run must not stay " +
                    $"referenced by the files it used to style.)");
                if (issues.Count >= MaxUnwiredIssuesPerFile) return issues;
            }
        }
        return issues;
    }

    /// <summary>Returns the classes REMOVED by THIS run's edit: the diff between the current
    /// content and the pre-edit snapshot. A class absent from both was gone before the run and
    /// is not attributed to it.</summary>
    private static HashSet<string> RemovedClasses(string css, string preEdit)
    {
        var current = ExtractDefinitions(css).classes;
        var pre = ExtractDefinitions(preEdit).classes;
        return new HashSet<string>(pre.Where(c => !current.Contains(c)), StringComparer.Ordinal);
    }

    /// <summary>Returns (classes, custom properties) defined by THIS run's edit: the diff between
    /// the pre-edit snapshot and current content. No snapshot ⇒ the file was created by the run,
    /// so every definition counts as new.</summary>
    private static (HashSet<string> classes, HashSet<string> props) NewlyDefined(string css, string? preEdit)
    {
        var current = ExtractDefinitions(css);
        if (preEdit == null) return current;
        var pre = ExtractDefinitions(preEdit);
        var newClasses = new HashSet<string>(current.classes.Where(c => !pre.classes.Contains(c)), StringComparer.Ordinal);
        var newProps = new HashSet<string>(current.props.Where(p => !pre.props.Contains(p)), StringComparer.Ordinal);
        return (newClasses, newProps);
    }

    /// <summary>Extracts every class token from selectors (skipping :not(...) contexts) plus every
    /// custom-property definition. Comment/string-aware, so definitions inside comments or content
    /// strings never count.</summary>
    private static (HashSet<string> classes, HashSet<string> props) ExtractDefinitions(string css)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        var props = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(css)) return (classes, props);
        void Walk(List<Rule> rules)
        {
            foreach (var r in rules)
            {
                if (!r.IsAtRule)
                {
                    var sel = NotPseudoRegex.Replace(r.Selector, "");
                    foreach (Match m in ClassTokenRegex.Matches(sel))
                        classes.Add(m.Groups[1].Value);
                }
                if (r.Nested != null) Walk(r.Nested);
            }
        }
        Walk(ParseRules(css));
        var clean = CommentRegex.Replace(css, " ");
        clean = StringLiteralRegex.Replace(clean, " ");
        foreach (Match m in CustomPropDefRegex.Matches(clean))
            props.Add(m.Groups[1].Value);
        return (classes, props);
    }

    /// <summary>
    /// The files a stylesheet affects: the sibling template and component source by name
    /// (foo.component.css → foo.component.html / .ts / …). When no name-matching sibling exists
    /// (a shared/standalone stylesheet), falls back to every HTML/JS/TS file in the same
    /// directory so cross-file usage is still seen; if that yields nothing the file is global
    /// and skipped by the caller.
    /// </summary>
    private static List<(string rel, string content)> ConnectedFileContents(string projectRoot, string cssFullPath)
    {
        var dir = Path.GetDirectoryName(cssFullPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(cssFullPath);
        var result = new List<(string, string)>();
        foreach (var candidate in new[]
        {
            baseName + ".html", baseName + ".htm", baseName + ".component.html",
            baseName + ".ts", baseName + ".tsx", baseName + ".component.ts",
            baseName + ".js", baseName + ".jsx"
        })
        {
            var full = Path.Combine(dir, candidate);
            if (!System.IO.File.Exists(full)) continue;
            TryAdd(full);
        }
        if (result.Count > 0) return result;
        // No name-matching sibling — scan the directory for any connected file.
        try
        {
            foreach (var ext in new[] { ".html", ".htm", ".js", ".jsx", ".ts", ".tsx" })
            {
                foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*" + ext)
                    .Where(f => !string.Equals(f, cssFullPath, StringComparison.OrdinalIgnoreCase))
                    .Take(20))
                {
                    TryAdd(f);
                }
            }
        }
        catch { }
        return result;

        void TryAdd(string full)
        {
            try
            {
                var content = System.IO.File.ReadAllText(full);
                var rel = Path.GetRelativePath(projectRoot, full).Replace('\\', '/');
                result.Add((rel, content));
            }
            catch { }
        }
    }

    /// <summary>Whole-token reference test: the name must appear as a complete token, with hyphens
    /// treated as part of the token, so 'card' never matches 'card-body' and '.flight-detail-body'
    /// matches class="flight-detail-body", [class.flight-detail-body], ng-class and
    /// querySelector('.flight-detail-body').</summary>
    private static bool IsReferenced(string haystack, string name)
    {
        if (string.IsNullOrWhiteSpace(haystack)) return false;
        var pattern = @"(?<![A-Za-z0-9_-])" + Regex.Escape(name) + @"(?![A-Za-z0-9_-])";
        return Regex.IsMatch(haystack, pattern);
    }

    /// <summary>
    /// True when a class that is never referenced LITERALLY in the connected files is still
    /// applied to the DOM at runtime through a dynamic class variable — e.g.
    /// el.classList.add(this.stateClass), querySelector('.' + cls) or a className assignment
    /// where the class name arrives as a variable (@Input, computed value, composed string)
    /// and may appear nowhere as a literal. Refusing to credit the wiring would
    /// false-positive every dynamically-composed class as unwired.
    /// Only script files (.ts/.tsx/.js/.jsx) are scanned — templates have no variables to
    /// chase. String literals are stripped FIRST, so querySelector('#someId'),
    /// querySelector('.other-cls') and classList.add('other') leave no identifier behind and
    /// never credit this class; only a genuine VARIABLE argument (this.stateClass, cls,
    /// '.' + cls, `class-${suffix}`) survives. The whole-token discipline of
    /// <see cref="IsReferenced"/> is preserved upstream — this runs only for a class with no
    /// literal occurrence anywhere — and a bare literal call here applies a DIFFERENT class,
    /// which must not wire this one.
    /// </summary>
    private static bool IsDynamicallyWired(List<(string rel, string content)> connected)
    {
        // A class-application call whose argument is a VARIABLE (this.x, a bare identifier, or
        // a concatenation like '.' + cls). '{'/'}' are allowed inside the window so
        // template-literal forms like querySelector(`.${cls}`) match; ';' and parens bound it
        // to the call itself. Applied to literal-stripped content, so only a genuine variable
        // argument survives.
        var dynamicCallRegex = new Regex(
            @"classList\.(?:add|remove|toggle|replace|contains)\s*\([^;()]*\b[A-Za-z_$][A-Za-z0-9_$]*\b[^;()]*\)|" +
            @"querySelector(?:All)?\s*\([^;()]*\b[A-Za-z_$][A-Za-z0-9_$]*\b[^;()]*\)|" +
            @"getElementsByClassName\s*\([^;()]*\b[A-Za-z_$][A-Za-z0-9_$]*\b[^;()]*\)|" +
            @"className\s*=\s*[^;()]*\b[A-Za-z_$][A-Za-z0-9_$]*\b",
            RegexOptions.Compiled);
        foreach (var (rel, content) in connected)
        {
            var ext = Path.GetExtension(rel);
            if (!string.Equals(ext, ".ts", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".tsx", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".js", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ext, ".jsx", StringComparison.OrdinalIgnoreCase))
                continue;
            if (dynamicCallRegex.IsMatch(StringLiteralRegex.Replace(content, " "))) return true;
        }
        return false;
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
