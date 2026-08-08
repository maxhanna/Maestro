using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Deterministic post-execution check that template bindings reference symbols the sibling
/// component actually exposes. Runs alongside the LLM verifier so runs that wire template
/// bindings to invented members — or add component logic that a UI task never renders —
/// fail verification instead of slipping through with a plausible LLM verdict.
///
/// Two checks:
///   A. ValidateTemplateBindings — every symbol referenced by a binding in an edited/new
///      template must exist as a member (property, method, getter/setter) of the sibling
///      component class. Pure and regex-based: cannot hallucinate.
///   B. CheckUnrenderedComponentLogic — when the run edited a component whose sibling
///      template was in scope (read/attached or named by the task) but never edited, and
///      the task targets the UI surface, the new logic is never rendered → CONFIRMED issue.
///      This is the "wired but never rendered" failure mode (e.g. a getter added to the
///      component while the template keeps iterating the old flat list).
/// </summary>
public static class TemplateBindingValidator
{
    private const int MaxIssuesPerTemplate = 5;

    /// <summary>Words that never name component members in template expressions.</summary>
    private static readonly HashSet<string> _keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "let", "var", "of", "in", "track", "by", "as", "and", "or", "not",
        "true", "false", "null", "undefined", "this", "new", "typeof", "instanceof",
        "void", "delete", "if", "else", "for", "switch", "case", "default", "empty",
        "index", "count", "first", "last", "even", "odd", "middle", "then", "catch",
        "do", "while", "return", "break", "continue", "await", "async", "function",
        "class", "import", "from", "export", "constructor"
    };

    /// <summary>AngularJS controller aliases — the next chain segment is the real member.</summary>
    private static readonly HashSet<string> _controllerAliases = new(StringComparer.Ordinal)
        { "vm", "$ctrl" };

    /// <summary>Lines that start like a statement, not a class member declaration.</summary>
    private static readonly Regex _statementStartRegex = new(
        @"^\s*(const|let|var|return|if|for|while|switch|try|catch|throw|break|continue|import|from|export|function|class|interface|type|enum|new|delete|typeof|instanceof|void|await|yield|this)\b",
        RegexOptions.Compiled);

    /// <summary>First non-whitespace char disqualifies a line from being a member declaration.</summary>
    private static readonly Regex _bracketStartRegex = new(@"^\s*[\{\}\[\]\(\)\,;:\.\|&\+\-=\*\/<>!?]", RegexOptions.Compiled);

    /// <summary>Member declaration: optional modifiers + optional decorator, then name followed by
    /// `(`, `:`, `?:` or `=` (method or property, incl. get/set and @Input/@Output fields).</summary>
    private static readonly Regex _memberDeclRegex = new(
        @"^\s*(?:(?:public|private|protected|static|readonly|abstract|override|async|get|set)\s+)*(?:@\w+(?:\s*\([^)]*\))?\s+)*([A-Za-z_$][\w$]*)\s*(?:\(|\??[:=])",
        RegexOptions.Compiled);

    private static readonly Regex _classDeclRegex = new(
        @"\bclass\s+[A-Za-z_$][\w$]*", RegexOptions.Compiled);

    private static readonly Regex _attrRegex = new(
        @"(?<name>[\w\-\:\.\[\]\(\)\*\#]+)\s*=\s*(?<quote>['""])(?<value>[\s\S]*?)\k<quote>",
        RegexOptions.Compiled);

    private static readonly Regex _interpolationRegex = new(
        @"\{\{\s*([\s\S]*?)\s*\}\}", RegexOptions.Compiled);

    private static readonly Regex _controlFlowRegex = new(
        @"@(?:else\s+if|if|for|switch|case)\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _stringLiteralRegex = new(
        @"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""|`(?:[^`\\]|\\.)*`", RegexOptions.Compiled);

    private static readonly Regex _chainRegex = new(
        @"[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*", RegexOptions.Compiled);

    private static readonly Regex _objectKeyRegex = new(
        @"(?<=[\{,(]\s*)([A-Za-z_$][\w$]*)\s*:", RegexOptions.Compiled);

    /// <summary>Task prompts that target the rendered UI surface (panel/template/bindings).</summary>
    private static readonly Regex _uiTargetRegex = new(
        @"\b(panel|template|render|ng[-]?(?:for|if|repeat|model|click|class|show|hide|switch|style|change|blur|init|disabled|options|value|bind|href|src))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ─── Check A: template bindings must reference component members ──────────

    /// <summary>
    /// Extracts the member names (properties, methods, getters/setters, decorated fields)
    /// of every class in a TypeScript component file. Class-body scanning with brace-depth
    /// tracking; statement and continuation lines are excluded so method-body locals never
    /// pollute the member set.
    /// </summary>
    public static List<string> ExtractComponentMembers(string content)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        var inClass = false;
        var depth = 0;
        var inDecorator = false;
        var decoratorParenDepth = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (inDecorator)
            {
                decoratorParenDepth += line.Count(c => c == '(') - line.Count(c => c == ')');
                if (decoratorParenDepth <= 0) inDecorator = false;
                continue;
            }
            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith('@'))
            {
                // Strip a single-line decorator (@Input() userId?: number;) and keep scanning
                // the remainder for the member declaration. Multi-line decorators (@Component({
                // ...}) balance > 0) enter skip mode until their parens close.
                var decMatch = Regex.Match(trimmedStart, @"^@\w+(\([^)]*\))");
                var balance = line.Count(c => c == '(') - line.Count(c => c == ')');
                if (balance > 0)
                {
                    inDecorator = true;
                    decoratorParenDepth = balance;
                    continue;
                }
                if (decMatch.Success)
                {
                    var remainder = line.Substring(line.Length - trimmedStart.Length + decMatch.Length);
                    if (inClass)
                    {
                        CollectMemberFromLine(remainder, members);
                        depth += line.Count(c => c == '{') - line.Count(c => c == '}');
                        if (depth <= 0) { inClass = false; depth = 0; }
                    }
                }
                continue;
            }
            if (!inClass)
            {
                if (_classDeclRegex.IsMatch(line))
                {
                    inClass = true;
                    depth = line.Count(c => c == '{') - line.Count(c => c == '}');
                    if (depth <= 0) depth = 0;
                }
                continue;
            }
            // Inside a class body.
            if (depth <= 1 && line.TrimStart().StartsWith('}'))
            {
                inClass = false;
                depth = 0;
                continue;
            }
            CollectMemberFromLine(line, members);
            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
            if (depth <= 0) { inClass = false; depth = 0; }
        }
        members.Remove("constructor");
        return members.ToList();
    }

    private static void CollectMemberFromLine(string line, HashSet<string> members)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (_statementStartRegex.IsMatch(line)) return;
        if (_bracketStartRegex.IsMatch(line)) return;
        var m = _memberDeclRegex.Match(line);
        if (m.Success) members.Add(m.Groups[1].Value);
    }

    /// <summary>
    /// Extracts the symbols a template references through its bindings: interpolations,
    /// structural directives, property/event bindings, AngularJS ng-* attributes, and
    /// @if/@for/@switch control flow. Loop variables, template refs, pipe names, string
    /// literals, $-prefixed locals, object-literal keys and sub-property segments are
    /// excluded so only real component members are reported.
    /// </summary>
    public static List<string> ExtractTemplateSymbols(string html)
    {
        var symbols = new List<string>();
        var locals = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = Regex.Replace(html, @"<!--[\s\S]*?-->", " ");
        foreach (Match m in _attrRegex.Matches(cleaned))
        {
            var name = m.Groups["name"].Value.Trim();
            var value = m.Groups["value"].Value;
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("let-", StringComparison.Ordinal) || name.StartsWith('#'))
                continue; // template context var / template reference — not component symbols
            if (lower.StartsWith("*ngfor", StringComparison.Ordinal))
            {
                AddSymbolsFromLoop(value, symbols, locals);
                continue;
            }
            if (lower.StartsWith("ng-repeat", StringComparison.Ordinal))
            {
                AddSymbolsFromLoop(value, symbols, locals);
                continue;
            }
            if (lower.StartsWith("*", StringComparison.Ordinal)
                || name.StartsWith('[') || name.StartsWith('(')
                || lower.StartsWith("bind-", StringComparison.Ordinal) || lower.StartsWith("on-", StringComparison.Ordinal)
                || lower.StartsWith("ng-", StringComparison.Ordinal))
            {
                var expr = value.Trim();
                if (lower.StartsWith("ng-attr-", StringComparison.Ordinal))
                {
                    var im = _interpolationRegex.Match(expr);
                    if (im.Success) expr = im.Groups[1].Value;
                }
                if (expr.Length > 0) AddSymbolsFromExpr(expr, symbols, locals);
            }
        }
        foreach (Match m in _interpolationRegex.Matches(cleaned))
            AddSymbolsFromExpr(m.Groups[1].Value, symbols, locals);
        foreach (Match m in _controlFlowRegex.Matches(cleaned))
        {
            if (m.Value.TrimStart().StartsWith("@for", StringComparison.OrdinalIgnoreCase)
                || m.Value.TrimStart().StartsWith("@else if for", StringComparison.OrdinalIgnoreCase))
            {
                AddSymbolsFromLoop(m.Groups[1].Value, symbols, locals);
            }
            else
            {
                AddSymbolsFromExpr(m.Groups[1].Value, symbols, locals);
            }
        }
        return symbols
            .Distinct(StringComparer.Ordinal)
            .Where(s => !locals.Contains(s) && !_keywords.Contains(s) && !s.StartsWith("$"))
            .ToList();
    }

    private static void AddSymbolsFromLoop(string value, List<string> symbols, HashSet<string> locals)
    {
        var v = value.Trim();
        // "let a, let b = index of expr" / "let a of expr" (Angular 2+)
        var letMatch = Regex.Match(v, @"^let\s+([\s\S]*?)\s+(?:of|in)\s+");
        if (letMatch.Success)
        {
            foreach (var part in letMatch.Groups[1].Value.Split(','))
            {
                var nm = Regex.Match(part, @"([A-Za-z_$][\w$]*)");
                if (nm.Success) locals.Add(nm.Value);
            }
            v = v.Substring(letMatch.Index + letMatch.Length);
        }
        else
        {
            // "(key, value) in expr" / "item in expr" (AngularJS ng-repeat)
            var repeatMatch = Regex.Match(v, @"^\(?\s*([A-Za-z_$][\w$]*)(?:\s*,\s*([A-Za-z_$][\w$]*))?\s*\)?\s+(?:in|of)\s+");
            if (repeatMatch.Success)
            {
                locals.Add(repeatMatch.Groups[1].Value);
                if (repeatMatch.Groups[2].Success) locals.Add(repeatMatch.Groups[2].Value);
                v = v.Substring(repeatMatch.Index + repeatMatch.Length);
            }
        }
        // "... as alias"
        var asMatch = Regex.Match(v, @"\s+as\s+([A-Za-z_$][\w$]*)\s*;?$");
        if (asMatch.Success)
        {
            locals.Add(asMatch.Groups[1].Value);
            v = v.Substring(0, asMatch.Index);
        }
        foreach (var clause in v.Split(';'))
        {
            var c = clause.Trim();
            if (c.Length == 0) continue;
            // "let i = $index" — declares a loop local; only its RHS can reference symbols.
            var letEqMatch = Regex.Match(c, @"^let\s+([A-Za-z_$][\w$]*)\s*=\s*([\s\S]*)$");
            if (letEqMatch.Success)
            {
                locals.Add(letEqMatch.Groups[1].Value);
                AddSymbolsFromExpr(letEqMatch.Groups[2].Value, symbols, locals);
                continue;
            }
            var trackMatch = Regex.Match(c, @"^track\s*(?:by\b|\s*:)?\s*([\s\S]*)$", RegexOptions.IgnoreCase);
            if (trackMatch.Success)
            {
                AddSymbolsFromExpr(trackMatch.Groups[1].Value, symbols, locals);
                continue;
            }
            AddSymbolsFromExpr(c, symbols, locals);
        }
    }

    private static void AddSymbolsFromExpr(string expr, List<string> symbols, HashSet<string> locals)
    {
        var cleaned = _stringLiteralRegex.Replace(expr, " ");
        var isPipeTarget = false;
        foreach (var segment in cleaned.Split('|'))
        {
            var seg = segment.Trim();
            if (seg.Length == 0) continue;
            if (isPipeTarget)
            {
                // The first identifier of a pipe segment is the PIPE NAME — skip it.
                var pipeMatch = Regex.Match(seg, @"^[A-Za-z_$][\w$]*\s*");
                if (pipeMatch.Success) seg = seg.Substring(pipeMatch.Length);
            }
            AddSymbolsFromSegment(seg, symbols);
            isPipeTarget = true;
        }
    }

    private static void AddSymbolsFromSegment(string seg, List<string> symbols)
    {
        if (seg.Length == 0) return;
        // Member-access chains: only the ROOT is a component symbol; sub-property segments
        // (c.id, group.key) are data fields on the loop variable, not component members.
        foreach (Match m in _chainRegex.Matches(seg))
        {
            var parts = m.Value.Split('.');
            var root = parts[0];
            if (root.StartsWith("$")) continue;
            if (_controllerAliases.Contains(root))
            {
                if (parts.Length > 1) AddCandidate(parts[1], symbols);
                continue;
            }
            AddCandidate(root, symbols);
        }
        // Object-literal keys ({active: vm.x}) are CSS classes / config keys, not symbols.
        foreach (Match m in _objectKeyRegex.Matches(seg))
        {
            var key = m.Groups[1].Value;
            symbols.RemoveAll(s => string.Equals(s, key, StringComparison.Ordinal));
        }
    }

    private static void AddCandidate(string name, List<string> symbols)
    {
        if (name.Length == 0 || _keywords.Contains(name) || name.StartsWith("$")) return;
        symbols.Add(name);
    }

    /// <summary>
    /// Validates every binding symbol of an edited/new template against the sibling component's
    /// members. Returns CONFIRMED issue strings (capped) for symbols the component does not
    /// expose. Skips when the component can't be identified (no @Component decorator) so static
    /// HTML / non-Angular projects are never flagged.
    /// </summary>
    public static List<string> ValidateTemplateBindings(string templateRelPath, string htmlContent, string componentContent)
    {
        if (!componentContent.Contains("@Component(", StringComparison.Ordinal)
            && !componentContent.Contains("@Component (", StringComparison.Ordinal))
            return new List<string>();
        var members = new HashSet<string>(ExtractComponentMembers(componentContent), StringComparer.Ordinal);
        if (members.Count == 0) return new List<string>();
        var referenced = ExtractTemplateSymbols(htmlContent);
        var issues = new List<string>();
        foreach (var sym in referenced.OrderBy(s => s, StringComparer.Ordinal))
        {
            if (members.Contains(sym)) continue;
            issues.Add(
                $"Template binding in {templateRelPath} references '{sym}' which is missing from the component class — " +
                $"add it as a property/method (or fix the binding) or the template will not compile.");
            if (issues.Count >= MaxIssuesPerTemplate) break;
        }
        return issues;
    }

    /// <summary>
    /// Scans the modified files of a run and validates any Angular template against its sibling
    /// component (same directory, .ts/.tsx counterpart). Returns the union of Check A issues.
    /// </summary>
    public static List<string> CheckModifiedTemplates(string projectRoot, IEnumerable<string> modifiedRelPaths)
    {
        var issues = new List<string>();
        foreach (var rel in modifiedRelPaths)
        {
            if (!string.Equals(Path.GetExtension(rel), ".html", StringComparison.OrdinalIgnoreCase)) continue;
            var full = SafeFullPath(projectRoot, rel);
            if (full == null || !System.IO.File.Exists(full)) continue;
            var componentPath = ResolveSiblingComponent(full);
            if (componentPath == null || !System.IO.File.Exists(componentPath)) continue;
            var htmlContent = System.IO.File.ReadAllText(full);
            var componentContent = System.IO.File.ReadAllText(componentPath);
            issues.AddRange(ValidateTemplateBindings(rel, htmlContent, componentContent));
        }
        return issues;
    }

    // ─── Check B: component wired but never rendered ─────────────────────────

    /// <summary>
    /// Fails verification when the run edited a component whose sibling template was in scope
    /// (read/attached this run or named by the task) but was never edited, while the task
    /// targets the UI surface — the new logic is never rendered. Fires only when ALL signals
    /// agree, so a pure-logic .ts fix on a task that never mentions the UI is never flagged.
    /// </summary>
    public static List<string> CheckUnrenderedComponentLogic(
        string prompt, string projectRoot, IEnumerable<string> modifiedRelPaths, IEnumerable<object> allResults)
    {
        var issues = new List<string>();
        if (!_uiTargetRegex.IsMatch(prompt ?? "")) return issues;
        var modified = new HashSet<string>(modifiedRelPaths.Select(NormRel), StringComparer.OrdinalIgnoreCase);
        var inScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in allResults.OfType<Dictionary<string, object?>>())
        {
            var p = r.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p)) inScope.Add(NormRel(p));
        }
        foreach (var tsRel in modifiedRelPaths)
        {
            var ext = Path.GetExtension(tsRel).ToLowerInvariant();
            if (ext != ".ts" && ext != ".tsx") continue;
            var full = SafeFullPath(projectRoot, tsRel);
            if (full == null) continue;
            var htmlSibling = ResolveSiblingTemplate(full);
            if (htmlSibling == null || !System.IO.File.Exists(htmlSibling)) continue;
            var htmlRel = NormRel(Path.GetRelativePath(projectRoot, htmlSibling));
            if (modified.Contains(htmlRel)) continue;
            var namedInPrompt = prompt != null && prompt.IndexOf(htmlRel, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!inScope.Contains(htmlRel) && !namedInPrompt) continue;
            issues.Add(
                $"{tsRel} was modified but its template {htmlRel} was not — the task targets the UI and {htmlRel} was " +
                $"in scope for this run, so the new logic is never rendered. Update the template to consume the new " +
                $"symbol(s) (e.g. in an *ngFor/*ngIf binding or interpolation), or if the UI genuinely needs no change, " +
                $"state that explicitly and the binding check will pass.");
        }
        return issues;
    }

    // ─── Path helpers ─────────────────────────────────────────────────────────

    private static string? SafeFullPath(string projectRoot, string relPath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }
        catch { return null; }
    }

    private static string NormRel(string p) => p.Replace('\\', '/');

    /// <summary>foo.component.html → foo.component.ts / .tsx; foo.html → foo.ts / foo.component.ts.</summary>
    private static string? ResolveSiblingComponent(string htmlFullPath)
    {
        var dir = Path.GetDirectoryName(htmlFullPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(htmlFullPath);
        foreach (var candidate in new[]
        {
            baseName + ".ts", baseName + ".tsx", baseName + ".component.ts", baseName + ".component.tsx"
        })
        {
            var full = Path.Combine(dir, candidate);
            if (System.IO.File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>foo.component.ts → foo.component.html; foo.ts → foo.html / foo.component.html.</summary>
    private static string? ResolveSiblingTemplate(string tsFullPath)
    {
        var dir = Path.GetDirectoryName(tsFullPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(tsFullPath);
        foreach (var candidate in new[]
        {
            baseName + ".html", baseName + ".component.html"
        })
        {
            var full = Path.Combine(dir, candidate);
            if (System.IO.File.Exists(full)) return full;
        }
        return null;
    }
}
