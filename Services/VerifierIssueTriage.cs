using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Pure triage of verifier issues against the actual file contents before they are fed to
/// the replanner. Extracted from AgentController.Pipeline.cs so the phantom/speculative/
/// event-gated rules are unit-testable without reflecting into the controller.
/// </summary>
public static class VerifierIssueTriage
{
    /// <summary>Claim words that assert a concrete missing/undefined defect (as opposed to a risk).</summary>
    public static readonly Regex PhantomClaimRegex = new(
        @"\b(missing|not found|doesn't exist|does not exist|not defined|undefined|not present|absent|never used|unused|not declared)\b",
        RegexOptions.IgnoreCase);

    /// <summary>Hedge words that mark an issue as speculative ('might/could/maybe') rather than a proven defect.</summary>
    public static readonly Regex SpeculativeHedgeRegex = new(
        @"\b(might|could|maybe|possibly|potentially|may|likely|probably|perhaps|risk|risks|concern|concerns|worried|unsure|unclear|not sure|seems|appears)\b",
        RegexOptions.IgnoreCase);

    /// <summary>Timing/lifecycle words that trigger the event-gated reachability check.</summary>
    public static readonly Regex TimingConcernRegex = new(
        @"\b(initialized|initialization|render cycle|lifecycle|not yet available|not available|at runtime|before.*(?:load|render|init))\b",
        RegexOptions.IgnoreCase);

    /// <summary>Code-shaped identifiers — camelCase/PascalCase/snake_case tokens that look like
    /// real symbols rather than English prose words.</summary>
    public static readonly Regex CodeSymbolTokenRegex = new(
        @"[a-z][a-zA-Z0-9]{2,}|[A-Z][a-zA-Z0-9]*|_[a-z][a-zA-Z0-9_]*",
        RegexOptions.IgnoreCase);

    /// <summary>Checklist-echo substitution claims: the verifier asserts the code "uses X, while
    /// requirement is to use Y" — quoting its own invented requirement checklist rather than the
    /// ORIGINAL TASK, and flagging a present, valid value as wrong (e.g. "'.notificationContainer'
    /// uses 'justify-content: center', while requirement is to use proper horizontal alignment").
    /// The phrase "while (the) requirement is to use" is verifier-checklist-ese, not task language.</summary>
    public static readonly Regex ChecklistEchoPreferenceRegex = new(
        @"\bwhile\s+(?:the\s+)?requirement\s+is\s+to\s+use\b",
        RegexOptions.IgnoreCase);

    /// <summary>Matches "X should be Y" / "X must be Y" rename claims where the verifier asserts a
    /// symbol was supposed to be named something else (e.g. "'do_get' should be 'do_GET'").
    /// Captures group 1 = the symbol the verifier claims is wrong, group 2 = the corrected name.</summary>
    public static readonly Regex ShouldBeRenameRegex = new(
        // The leading quote/backtick is REQUIRED so prose issues like "the panel should be visible"
        // never match — only quoted symbol renames the verifier actually emits ("'do_get' should be 'do_GET'").
        @"[`'""]\b([A-Za-z_$][\w$]*)(?:\([^)]*\))?[`'""]?\s+(?:should|must|ought to)\s+be\s+(?:the\s+)?[`'""]?([A-Za-z_$][\w$]*)",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Implements the repair-loop skip-phantom rule: when the most recent executed step was
    /// skipped with reason 'already done', the verifier issue that drove it was a phantom — it
    /// is dropped and re-verification is SKIPPED so the next pass moves to the next issue.
    /// Returns (isPhantom, droppedIssueText, remainingIssues).
    /// </summary>
    public static (bool isPhantom, string? phantom, List<string> remainingIssues) TrySkipPhantomIssue(
        IEnumerable<object> allSteps, List<string>? verificationIssues)
    {
        var repairStep = allSteps.OfType<Dictionary<string, object?>>().LastOrDefault();
        var stepWasAlreadyDone = repairStep != null
            && repairStep.GetValueOrDefault("status")?.ToString() == "skipped"
            && repairStep.GetValueOrDefault("reason")?.ToString() == "already done";
        if (!stepWasAlreadyDone || verificationIssues == null || verificationIssues.Count == 0)
            return (false, null, verificationIssues ?? new List<string>());
        var phantom = verificationIssues[0];
        var remaining = new List<string>(verificationIssues);
        remaining.RemoveAt(0);
        return (true, phantom, remaining);
    }

    /// <summary>
    /// Triage a verifier issue against the ACTUAL current file contents before it is fed to the
    /// replanner. Returns (keep, reason). Drops, with a reason, issues that are:
    ///  1. PHANTOM — the issue claims a symbol is missing/undefined but the symbol IS present in
    ///     a relevant file (the verifier hallucinated; e.g. 'centerCurrentLocation not found' when
    ///     the method exists).
    ///  2. EVENT-GATED — the concern is about initialization timing of a symbol that is only ever
    ///     referenced from event handlers in the template (a ViewChild used solely inside a
    ///     (click) handler is inherently safe at click time).
    ///  3. SPECULATIVE WORDING — the issue is phrased with 'might/could/maybe' and contains no
    ///     concrete defect claim (e.g. 'globeComponent might not be initialized').
    /// Everything else is kept and treated as actionable.
    /// </summary>
    public static (bool keep, string reason) TriageVerifierIssue(
        string issue, IReadOnlyDictionary<string, string> filesByPath)
    {
        if (string.IsNullOrWhiteSpace(issue)) return (false, "empty issue");

        // 1) PHANTOM: claim + high-confidence symbol (backticked / vm.this.-qualified / #ref /
        //    method-call) present in a real file → verifier hallucinated. Bare prose tokens are
        //    NOT used here: 'centerCurrentLocation is missing' must not be excused just because
        //    the class name 'GlobeComponent' happens to appear in the file.
        if (PhantomClaimRegex.IsMatch(issue))
        {
            foreach (var symbol in ExtractCodeSymbols(issue, includeBareTokens: false))
            {
                if (filesByPath.Values.Any(content =>
                        Regex.IsMatch(content, @"\b" + Regex.Escape(symbol) + @"\b")))
                {
                    return (false, $"phantom: symbol '{symbol}' IS present in the file despite the claim");
                }
            }
        }

        // 1a) ALREADY-RESOLVED RENAME: the verifier says "X should be Y" (e.g. 'do_get should be
        //     do_GET'). When X is no longer present anywhere AND Y now exists in the file, the
        //     claimed rename has already been applied — the issue was re-issued because the
        //     verifier read stale/historical text, not because the code is wrong. Drop it so the
        //     repair loop does not burn a pass re-fixing an already-fixed defect.
        if (filesByPath.Count > 0)
        {
            var rename = ShouldBeRenameRegex.Match(issue);
            if (rename.Success)
            {
                var wrong = rename.Groups[1].Value;
                var right = rename.Groups[2].Value;
                var wrongStillPresent = filesByPath.Values.Any(content =>
                    Regex.IsMatch(content, @"\b" + Regex.Escape(wrong) + @"\b"));
                var rightNowPresent = filesByPath.Values.Any(content =>
                    Regex.IsMatch(content, @"\b" + Regex.Escape(right) + @"\b", RegexOptions.IgnoreCase));
                if (!wrongStillPresent && rightNowPresent)
                {
                    return (false, $"already resolved: verifier claims '{wrong} should be {right}' but '{wrong}' is no longer present and '{right}' exists in the file");
                }
            }
        }

        // 1b) HALLUCINATED REFERENCE: the issue REFERENCES high-confidence symbol(s) that exist
        //     in NO provided file → the verifier named code that isn't in the workspace (and
        //     there is no 'present despite claim' phantom evidence to catch it). Drop as
        //     speculative/unverifiable. Bare prose tokens are not used, and the rule only fires
        //     when files were actually loaded (an empty files map must not nuke everything).
        //     CRITICAL: an explicit ABSENCE CLAIM ('X is missing', 'does not exist') suppresses
        //     this rule — reporting a genuinely-missing symbol is the verifier's core job and
        //     must stay actionable, even when the symbol is truly absent.
        if (filesByPath.Count > 0 && !PhantomClaimRegex.IsMatch(issue))
        {
            var referenced = ExtractCodeSymbols(issue, includeBareTokens: false).ToList();
            if (referenced.Count > 0 && referenced.All(symbol =>
                    !filesByPath.Values.Any(content =>
                        Regex.IsMatch(content, @"\b" + Regex.Escape(symbol) + @"\b"))))
            {
                return (false, $"references symbol(s) [{string.Join(", ", referenced)}] not present in any file — unverifiable/hallucinated");
            }
        }

        // 1c) CHECKLIST-ECHO SUBSTITUTION: the verifier claims the code "uses X, while
        //     requirement is to use Y" — echoing its own invented requirement checklist rather
        //     than the original task, and flagging a present, valid value as wrong (the
        //     flex-wrap CSS case: "'.notificationContainer' uses 'justify-content: center',
        //     while requirement is to use proper horizontal alignment"). Requires (a) at least
        //     one named code symbol that EXISTS in the files (so the preference is about real
        //     code we can see) and (b) no concrete absence claim — a genuinely missing feature
        //     must stay actionable even when the wording happens to echo the checklist.
        if (ChecklistEchoPreferenceRegex.IsMatch(issue) && !PhantomClaimRegex.IsMatch(issue))
        {
            // The verifier quotes symbols AND CSS values with single quotes ("'notificationContainer'
            // uses 'justify-content: center'"), so augment the shared extractor (backticks/qualifiers/
            // method calls only) with quoted identifiers — dashes allowed for CSS property names.
            var namedSymbols = ExtractCodeSymbols(issue, includeBareTokens: false)
                .Concat(ExtractQuotedIdentifiers(issue))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (namedSymbols.Count > 0 && namedSymbols.Any(symbol =>
                    filesByPath.Values.Any(content =>
                        Regex.IsMatch(content, @"\b" + Regex.Escape(symbol) + @"\b"))))
            {
                return (false, "checklist-echo substitution ('uses X, while requirement is to use Y') — preference about present code, not a concrete defect");
            }
        }

        // 2) EVENT-GATED: timing concern about a symbol only referenced from event handlers.
        if (TimingConcernRegex.IsMatch(issue))
        {
            foreach (var symbol in ExtractCodeSymbols(issue, includeBareTokens: true))
            {
                foreach (var (path, content) in filesByPath)
                {
                    if (!IsHtmlLikePath(path)) continue;
                    if (IsSymbolOnlyEventGated(content, symbol))
                        return (false, $"event-gated: '{symbol}' is only referenced from event handlers — inherently safe at trigger time");
                }
            }
        }

        // 3) SPECULATIVE WORDING: hedged risk with no concrete defect claim. A concrete claim
        //    (missing/undefined/not found) suppresses the hedge — 'The concern is that saveCards
        //    is missing' is a real defect, not speculation, and must stay actionable even though
        //    it contains the word 'concern'.
        if (SpeculativeHedgeRegex.IsMatch(issue) && !PhantomClaimRegex.IsMatch(issue))
            return (false, "speculative wording (might/could/maybe) with no concrete defect");

        return (true, "");
    }

    /// <summary>Extracts single/backtick-quoted identifiers from an issue, allowing dashes so
    /// CSS property names ('justify-content') survive. The verifier quotes symbols with single
    /// quotes just as often as backticks, and the shared extractor deliberately ignores quotes.
    /// A trailing colon is trimmed so 'justify-content:' still resolves against the stylesheet.</summary>
    public static IEnumerable<string> ExtractQuotedIdentifiers(string issue)
    {
        foreach (Match m in Regex.Matches(issue, @"['""`]([A-Za-z_$][\w$:-]*)['""`]"))
        {
            var value = m.Groups[1].Value.TrimEnd(':');
            if (value.Length > 0) yield return value;
        }
    }

    /// <summary>English prose words that never name code symbols. Applied to BARE tokens and
    /// METHOD-CALL matches so natural-language parentheticals ("the component (see above)",
    /// "should style (via …)") can never masquerade as code references — otherwise the
    /// hallucinated-reference rule drops genuine issues that merely mention a word followed by
    /// '('. Backticked / vm.-qualified / #ref symbols are unambiguous and never filtered.</summary>
    public static readonly HashSet<string> CommonProseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "this", "that", "with", "from", "have", "has", "been", "should", "would",
        "could", "might", "will", "there", "their", "which", "when", "where", "what", "does",
        "not", "but", "also", "only", "code", "file", "files", "method", "function", "class",
        "button", "task", "card", "issue", "issues", "error", "reason", "missing", "found",
        "expected", "location", "review", "check", "verify", "verifier", "reference", "referenced",
        "style", "styles", "component", "template", "element", "css", "rule", "rules", "wire",
        "remove", "apply", "applies", "add", "use", "used", "panel", "list", "item", "row", "div",
        // Pronouns/function words that commonly precede a natural-language parenthetical —
        // "still references it (class=\"...\")" must NOT read 'it' as a method call, or the
        // hallucinated-reference rule drops a genuine issue whose symbol is absent from the
        // files (the orphaned-template-reference case). Real 'it(...)' calls in code still
        // resolve: the symbol is present in the file, so no drop happens either way.
        "it", "its", "they", "them", "we", "you", "our", "your", "he", "she", "him", "her",
        "his", "who", "whom", "as", "like", "such", "via", "into", "onto", "over", "under"
    };

    /// <summary>Extracts code-shaped identifiers from an issue (backticked, vm./this.-qualified,
    /// #template-refs, method calls, and — when <paramref name="includeBareTokens"/> is set —
    /// bare camelCase/PascalCase tokens).</summary>
    public static IEnumerable<string> ExtractCodeSymbols(string issue, bool includeBareTokens)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(issue, @"`([A-Za-z_$][\w$]*)`"))
            symbols.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(issue, @"\b(?:vm|this)\s*\.\s*([A-Za-z_$][\w$]*)"))
            symbols.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(issue, @"#([A-Za-z_$][\w$]*)"))
            symbols.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(issue, @"\b([A-Za-z_$][\w$]*)\s*\("))
        {
            var name = m.Groups[1].Value;
            if (CommonProseWords.Contains(name)) continue;
            symbols.Add(name);
        }
        if (includeBareTokens)
        {
            // Bare code-shaped tokens (camelCase/PascalCase/snake_case), excluding common prose words.
            foreach (Match m in CodeSymbolTokenRegex.Matches(issue))
            {
                var tok = m.Value;
                if (tok.Length >= 3 && !CommonProseWords.Contains(tok))
                    symbols.Add(tok);
            }
        }
        return symbols;
    }

    public static bool IsHtmlLikePath(string path) =>
        path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when every occurrence of <paramref name="symbol"/> in the HTML sits inside an
    /// event-binding attribute value (e.g. (click)="...symbol...", ng-click="..."). Counts
    /// symbol occurrences inside each event-attribute value span (not whole-attribute matches),
    /// so a single handler referencing the symbol multiple times still counts every occurrence.</summary>
    public static bool IsSymbolOnlyEventGated(string html, string symbol)
    {
        var word = @"\b" + Regex.Escape(symbol) + @"\b";
        var total = Regex.Matches(html, word, RegexOptions.IgnoreCase).Count;
        if (total == 0) return false;
        var eventBound = 0;
        foreach (Match attr in Regex.Matches(html,
            "(?:\\([a-z][a-z0-9.\\-]*\\)|ng-[a-z0-9\\-]+)=\\s*[\"'][^\"']*[\"']",
            RegexOptions.IgnoreCase))
        {
            var value = attr.Value;
            var eq = value.IndexOf('=');
            if (eq < 0) continue;
            var v = value[(eq + 1)..].Trim();
            if (v.Length >= 2) v = v[1..^1]; // strip surrounding quote
            eventBound += Regex.Matches(v, word, RegexOptions.IgnoreCase).Count;
        }
        return eventBound == total;
    }
}
