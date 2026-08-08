using System.Text.RegularExpressions;
using Weaver.Services;

namespace Weaver;

// ═══════════════════════════════════════════════════════════════════════════════
//  EDIT CLASSIFIER  — single place that maps (step, file, ext) → EditStrategy
//
//  This is the ONLY place change-description regex lives.
//  Delete every duplicate copy in AgentController.cs:
//    isNewMethodInsertion, classIsNewCsMethod, isNewMethodInsert,
//    rawMethodCodeDetect, isClassPropertyFill, changeLowerForFormat, isActualDeletion
// ═══════════════════════════════════════════════════════════════════════════════

public static class EditClassifier
{
    // ── Primary entry point ──────────────────────────────────────────────────

    /// <summary>
    /// Classify a plan step into an EditStrategy based on the file's existence,
    /// extension, and the change description. Call once per step — all downstream
    /// logic (prompt builder, response parser, applier, escalation) switches on
    /// the returned strategy. Never re-derives it from scratch.
    /// </summary>
    public static EditStrategy Classify(PlanStep step, bool fileExists, string ext)
    {
        if (!fileExists) return EditStrategy.CreateFile;

        if (HtmlDomEditor.IsHtmlDomFile(step.File))
            return ClassifyHtml(step.Change ?? "");

        var change = (step.Change ?? "").ToLowerInvariant().Replace('_', ' ');
        var (_, supportsFormatC, _) = AgentMethodInventory.GetLanguageProfile(ext);

        if (IsDeletion(change))           return EditStrategy.DeleteLines;
        // A single-variable/expression swap ("replace `b` with `group`", "change X to Y") is
        // a tiny targeted edit — NEVER a method rewrite. Must be checked before
        // IsFullMethodRewrite, which would otherwise FORMAT C the whole enclosing method
        // when the planner names the swapped variable as targetSymbol (the benchmarks-ngFor
        // failure: a 30-line wall-of-text oldString). AnchoredEdit keeps oldString small.
        if (IsVariableSwap(change, step.TargetSymbol)) return EditStrategy.AnchoredEdit;
        if (IsClassPropertyFill(change))
            return ext == ".cs" ? EditStrategy.FillClassBody : EditStrategy.AnchoredEdit;
        if (supportsFormatC && IsNewMethodOrEndpoint(change, step.TargetSymbol))
                                           return EditStrategy.InsertMethod;
        if (supportsFormatC && IsFullMethodRewrite(change, step.TargetSymbol))
                                           return EditStrategy.ReplaceMethod;

        return EditStrategy.AnchoredEdit; // safe default
    }

    /// <summary>
    /// Classify and produce a full <see cref="EditIntent"/> — richer than just strategy,
    /// used by <see cref="EditStrategyResolver.Decide"/> for AST-assisted resolution.
    /// </summary>
    public static EditIntent ClassifyIntent(PlanStep step, string ext)
    {
        var change = (step.Change ?? "").ToLowerInvariant().Replace('_', ' ');

        if (IsDeletion(change))
            return new EditIntent(EditIntentKind.DeleteContent, null, null);

        // A variable swap on an HTML file is a TARGETED DOM replace — ReplaceSymbol keeps
        // EditStrategyResolver.Decide mapping it to HtmlReplace (targeted single-line
        // replace). TargetedEdit would map to HtmlInsertBefore, which is wrong for a swap.
        // For code files the swap is a tiny anchored edit (never a method rewrite).
        if (IsVariableSwap(change, step.TargetSymbol))
            return HtmlDomEditor.IsHtmlDomFile(step.File)
                ? new EditIntent(EditIntentKind.ReplaceSymbol, step.TargetSymbol, null)
                : new EditIntent(EditIntentKind.TargetedEdit, step.TargetSymbol, null);

        if (IsClassPropertyFill(change))
            return new EditIntent(EditIntentKind.AddProperty, step.TargetSymbol, "property");

        if (IsNewMethodOrEndpoint(change, step.TargetSymbol))
            return new EditIntent(EditIntentKind.InsertNearSymbol, step.TargetSymbol, "method");

        if (IsFullMethodRewrite(change, step.TargetSymbol))
            return new EditIntent(EditIntentKind.ReplaceSymbol, step.TargetSymbol, "method");

        return new EditIntent(EditIntentKind.TargetedEdit, step.TargetSymbol, null);
    }

    // ── HTML subclassification ────────────────────────────────────────────────

    private static EditStrategy ClassifyHtml(string change)
    {
        var lower = change.ToLowerInvariant();
        // HTML removal: FORMAT D rejects empty newCode, so the ONLY executable route
        // for stripping an HTML block is oldString → empty newString (DeleteLines).
        // Route removals here explicitly instead of the insert-before default.
        if (IsDeletion(lower))
            return EditStrategy.DeleteLines;
        if (Regex.IsMatch(lower, @"\b(replace|update|modify|change)\b"))
            return EditStrategy.HtmlReplace;
        if (Regex.IsMatch(lower, @"\b(after|below|append|append after)\b"))
            return EditStrategy.HtmlInsertAfter;
        return EditStrategy.HtmlInsertBefore; // safe default for HTML additions
    }

    // ── Change-description predicates ────────────────────────────────────────

    /// <summary>True when the step is removing lines/blocks with nothing replacing them.</summary>
    public static bool IsDeletion(string changeLower) =>
        Regex.IsMatch(changeLower,
            @"^\s*(remove|delete|strip|erase|drop)\b") &&
        !Regex.IsMatch(changeLower,
            @"\b(add|insert|replace|implement|return|and add|then add)\b");

    /// <summary>
    /// True when the step adds a brand-new method, endpoint, function, or handler
    /// that does not yet exist in the file.
    /// </summary>
    public static bool IsNewMethodOrEndpoint(string changeLower, string? targetSymbol = null)
    {
        // Explicit "add new method / create endpoint / implement function" phrasing
        if (Regex.IsMatch(changeLower,
            @"\b(add|create|implement|introduce|define|new)\b.{0,50}\b(method|function|endpoint|handler|action|route|api|async)\b"))
            return true;

        // "add a Get*/Post*/Put*/Delete* method" patterns
        if (Regex.IsMatch(changeLower,
            @"\b(add|create|implement)\b.{0,30}\b(get|post|put|delete|patch)[a-z]+"))
            return true;

        // Target symbol is explicitly named and change says "add" or "create"
        if (!string.IsNullOrWhiteSpace(targetSymbol) &&
            Regex.IsMatch(changeLower, @"\b(add|create|implement|introduce)\b"))
            return true;

        return false;
    }

    /// <summary>
    /// True when the step swaps ONE variable/expression for another — "replace `b` with
    /// `group`", "change X to Y", "swap A for B", "from X to Y". These are the smallest
    /// possible edits: the single line containing the token is the whole oldString. The
    /// planner is prompted to name both tokens in the change description, so this predicate
    /// can recognize the intent without ever needing a whole-block anchor.
    /// </summary>
    public static bool IsVariableSwap(string changeLower, string? targetSymbol = null)
    {
        // Whole-method language is NEVER a variable swap — "replace the entire save method"
        // must stay a ReplaceMethod even when the symbol matches ("save").
        if (Regex.IsMatch(changeLower, @"\b(method|function|class|body|entire|whole|endpoint|handler|implementation)\b"))
            return false;
        // "replace/swap X with/for Y" where X is a short identifier (backticks/quotes optional)
        if (Regex.IsMatch(changeLower,
            @"\b(replace|swap)\b\s+[`']?[\w.$]{1,24}[`']?\s+(?:with|for)\s+[`']?[\w.$]{1,24}[`']?"))
            return true;
        // "swap/replace the X( ...) with/for Y" — a determiner and maybe one more word may
        // sit between the verb and the swapped token ("swap the loop variable for a grouped one").
        if (Regex.IsMatch(changeLower,
            @"\b(replace|swap)\b\s+(?:the|a|an|this|that)\s+(?:[\w.$]{1,24}\s+)?[`']?[\w.$]{1,24}[`']?\s+(?:with|for)\s+[`']?[\w.$]{1,24}[`']?"))
            return true;
        // "change/rename X to Y"
        if (Regex.IsMatch(changeLower,
            @"\b(change|rename)\b\s+[`']?[\w.$]{1,24}[`']?\s+to\s+[`']?[\w.$]{1,24}[`']?"))
            return true;
        // "change/rename the X to Y" — determiner between verb and token.
        if (Regex.IsMatch(changeLower,
            @"\b(change|rename)\b\s+(?:the|a|an|this|that)\s+[`']?[\w.$]{1,24}[`']?\s+to\s+[`']?[\w.$]{1,24}[`']?"))
            return true;
        // "... from X to Y" (e.g. "change the loop variable from `b` to `group`")
        if (Regex.IsMatch(changeLower,
            @"\bfrom\s+[`']?[\w.$]{1,24}[`']?\s+to\s+[`']?[\w.$]{1,24}[`']?"))
            return true;
        // Named symbol + "swap/replace" phrasing when the planner set a variable targetSymbol
        // (e.g. targetSymbol="b", change="swap the benchmark loop variable").
        if (!string.IsNullOrWhiteSpace(targetSymbol) &&
            Regex.IsMatch(changeLower, @"\b(swap|replace|rename)\b") &&
            changeLower.Contains(targetSymbol.ToLowerInvariant()))
            return true;
        return false;
    }

    /// <summary>
    /// True when the step adds one or more properties or fields to an existing class —
    /// never appropriate for FORMAT C class-replace (data-loss risk).
    /// </summary>
    public static bool IsClassPropertyFill(string changeLower) =>
        Regex.IsMatch(changeLower,
            @"\b(add|append|include|insert)\b.{0,40}\b(property|field|attribute|column|prop)\b") ||
        Regex.IsMatch(changeLower,
            @"\b(new|additional)\b.{0,20}\b(property|field)\b");

    /// <summary>
    /// True when the step rewrites the body of an EXISTING method — appropriate for
    /// FORMAT C targetType/targetName when the symbol is resolvable.
    /// </summary>
    public static bool IsFullMethodRewrite(string changeLower, string? targetSymbol = null)
    {
        if (Regex.IsMatch(changeLower,
            @"\b(rewrite|refactor|overhaul|restructure|rebuild)\b.{0,50}\b(method|function|body|logic|implementation)\b"))
            return true;

        if (Regex.IsMatch(changeLower,
            @"\b(replace|update|modify|change)\b.{0,40}\b(entire|whole|full|complete)\b.{0,30}\b(method|function|body)\b"))
            return true;

        // Named symbol + update/modify phrasing → likely a full-method rewrite
        if (!string.IsNullOrWhiteSpace(targetSymbol) &&
            Regex.IsMatch(changeLower,
                @"\b(update|modify|change|fix|rewrite|refactor)\b") &&
            changeLower.Contains(targetSymbol.ToLowerInvariant()))
            return true;

        return false;
    }
}
