using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Weaver;

namespace Weaver.Services;

// ═══════════════════════════════════════════════════════════════════════════════
//  DETERMINISTIC EDIT GENERATOR — small code bodies synthesized WITHOUT the LLM
// ═══════════════════════════════════════════════════════════════════════════════
//
// Produces fully-resolved (oldStr → newStr) edits for mechanically-describable
// changes, so these steps never need a single LLM round-trip:
//
//   • Literal swap        "change retryCount from 3 to 5"   →  `retryCount = 3` → `retryCount = 5`
//                         "set timeout to 60"               →  current value read from the file
//   • C# auto-property    "add a string Email property"     →  `public string Email { get; set; }`
//   • C# getter/setter    "add a getter and setter for X"   →  backing field + explicit get/set
//   • TS/JS class member  "add an Age property to the User class" → `public age: number = 0;`
//
// Every generator is a pure function of (file path, content, description) and
// declines (returns null) when it cannot verify its assumptions against the file
// — the LLM pipeline then handles the step as usual.

public static class DeterministicEditGenerator
{
    /// <summary>A fully-resolved, apply-ready edit pair.</summary>
    public sealed record DeterministicEdit(
        EditStrategy Strategy,
        string? TargetType,
        string? TargetName,
        string? OldStr,
        string? NewStr,
        int LineNumber,
        string Reason,
        List<EditPair>? Edits = null); // multi-match: one anchored edit per occurrence, applied via the batch path

    // ── Entry point ──────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to synthesize a fully-resolved edit for the change description.
    /// Returns null when the change is not deterministically describable (or the
    /// assumptions can't be verified against the file content) — the caller then
    /// falls back to the normal LLM pipeline.
    /// </summary>
    public static DeterministicEdit? TryGenerate(
        string relPath, bool fileExists, string fileContent, string changeDescription)
    {
        if (!fileExists || string.IsNullOrWhiteSpace(changeDescription) || string.IsNullOrWhiteSpace(fileContent))
            return null;
        if (HtmlDomEditor.IsHtmlDomFile(relPath))
            return null; // HTML/template family stays in HtmlDomEditor's lane

        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var desc = changeDescription.Trim();
        var lower = desc.ToLowerInvariant();

        // Removals route through DeleteLines — a swap must never hijack them, even
        // when the wording contains "from N to M" ("remove the timeout from 30 to 60").
        if (Regex.IsMatch(lower.TrimStart(), @"^(remove|delete|drop|erase|strip)\b"))
            return null;

        // Property / field / getter-setter additions → class-body anchored insert.
        var wantsMember = Regex.IsMatch(lower,
            @"\b(add|create|insert|define)\b.{0,60}\b(property|field|getter|get\s+and\s+set|get/set)\b");
        if (wantsMember && ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
        {
            // Multi-class: "add a string Email property to every DTO class" → one anchored
            // FillClassBody edit per matching class, applied via the batch path. A multi
            // request that can't be safely generated must NOT degrade to a single-class edit.
            if (IsMultiClassMemberAdd(desc))
            {
                var multiMember = TryGenerateMultiMember(relPath, ext, fileContent, desc);
                if (multiMember != null) return multiMember;
                return null;
            }
            var prop = TryGenerateMember(relPath, ext, fileContent, desc);
            if (prop != null) return prop;
        }

        // Literal swap — line-based, works for any text language (ts/cs/json/css/...).
        if (Regex.IsMatch(lower, @"\b(set|change|update|bump|increase|decrease|switch)\b|\bfrom\b|\bto\b"))
        {
            // Multi-match: "update all five RetryCount defaults" → one anchored edit per
            // occurrence, applied in a single batch. A multi request that can't be safely
            // generated must NOT degrade to editing just the first match.
            if (IsMultiMatchDescription(desc))
            {
                var multi = TryGenerateMultiSwap(fileContent, desc, ext);
                if (multi != null) return multi;
                return null;
            }
            var swap = TryGenerateLiteralSwap(fileContent, desc);
            if (swap != null) return swap;
        }

        return null;
    }

    // ── Multi-match: "update all five X defaults" → one edit per occurrence ──

    private static readonly Regex MultiSignalRegex = new(
        @"\b(all|every|each|both|multiple|several|numerous|various|every\s+single|a\s+couple\s+of)\b" +
        @"|\b(two|three|four|five|six|seven|eight|nine|ten|twenty|thirty|forty|fifty)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Strictly PLURAL nouns — "the default timeout" is singular and must stay single-edit.
    private static readonly Regex PluralNounRegex = new(
        @"\b(occurrences|instances|defaults|values|sections|columns|rows|fields|entries|properties)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "update the timeout values to 60" / "set all five RetryCount defaults to 5" — the
    // name sits immediately BEFORE the plural noun. Quantifier filler ("all five", "the")
    // is stripped from the description up front so a camelCase name like "retryCount" is
    // never truncated by an optional lowercase-word group.
    private static readonly Regex MultiSetToRegex = new(
        @"\b(?:set|change|update|bump|switch|increase|decrease|adjust|modify)\b\s+" +
        @"([A-Za-z_][A-Za-z0-9_.]*)\s+" +
        @"(?:occurrence|instance|default|value|section|column|row|field|entry|property)s\s+to\s+" +
        @"([+-]?\d[\d_.]*[a-zA-Z%]*|true|false|null)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MultiFillerRegex = new(
        @"\b(?:all|every|each|both|the|these|those|multiple|several|numerous|various|every\s+single|a\s+couple\s+of|all\s+of\s+the|a\s+few)\b\s*" +
        @"|\b(?:two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|twenty|thirty|forty|fifty)\b\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Multi-class member add: "add a string Email property to every DTO class" ──

    // Target spec: quantifier form ("every DTO class", "all the DTO classes", "both
    // interfaces") or plural+in-file form ("the DTO classes in this file"). The optional
    // name-filter word ("DTO") restricts which classes get the member.
    private static readonly Regex MultiClassTargetRegex = new(
        @"\b(?:every|each|all|both)\b(?:\s+(?:of\s+the|the))?\s+" +
        @"(?:(?<filter>[A-Za-z_][A-Za-z0-9_]*)\s+)?(?<kind>class|interface|record|struct)(?:es|s)?\b" +
        @"|\b(?:(?<filter2>[A-Za-z_][A-Za-z0-9_]*)\s+)?(?<kind2>classes|interfaces|records|structs)\b" +
        @".{0,25}\b(?:in\s+(?:this|the)\s+file|throughout)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Per-class member naming (multi-class add only):
    //   "... but NameKey on the first one"  → the Nth matching class gets a differently-named member.
    //   "... named after the class"          → every member is prefixed with its own class's name.
    // Both clauses are optional; without them every class gets the description's base member name.
    private static readonly Regex PerClassOverrideRegex = new(
        @"\b(?:but|except)\b\s+(?<name>(?!on\b|the\b|one\b|a\b|an\b)[A-Za-z_][A-Za-z0-9_]*)\s+on\s+(?:the\s+)?(?<ordinal>first|second|third|fourth|fifth|last|[1-9]\d*)\b" +
        @"|\b(?:but|except)\b\s+on\s+(?:the\s+)?(?<ordinal>first|second|third|fourth|fifth|last|[1-9]\d*)\b(?:\s+one)?\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClassNameAdaptiveRegex = new(
        @"\bnamed\s+after\b" +
        @"|\b(?:following|matching|mirroring)\s+(?:the\s+)?(?:class|record|interface|struct)s?\s+names?\b" +
        @"|\b(?:adapt|adapted|adapting|adjusts?)\b.{0,15}\b(?:class|record|interface|struct)s?\s+names?\b" +
        @"|\bper\s+(?:[a-z0-9_]*\s+)?(?:class|record|interface|struct)\s+names?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Class-set narrowing (multi-class add only):
    //   "all classes ending in Repository"       → only names carrying the suffix
    //   "every class starting with Api"          → only names carrying the prefix
    //   "every DTO class except the base one"    → drop names containing the excluded word
    private static readonly Regex ClassSuffixFilterRegex = new(
        @"\b(?:ending|end)\s+(?:in|with)\s+(?<suffix>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClassPrefixFilterRegex = new(
        @"\b(?:starting|beginning)\s+with\s+(?<prefix>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClassExclusionRegex = new(
        @"\b(?:except|excluding|other\s+than|apart\s+from)\b\s+(?:the\s+)?(?<excluded>(?!a\b|an\b|one\b|the\b|class\b|classes\b|record\b|records\b|interface\b|interfaces\b|struct\b|structs\b)[A-Za-z_][A-Za-z0-9_]*)\b" +
        @"|\b(?:except|excluding)\b\s+(?:the\s+)?one\s+(?:named|called)\s+(?<excluded>(?!a\b|an\b|one\b|the\b|class\b|classes\b|record\b|records\b|interface\b|interfaces\b|struct\b|structs\b)[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsMultiClassMemberAdd(string desc)
        => MultiClassTargetRegex.IsMatch(desc);

    private static bool IsMultiMatchDescription(string desc)
    {
        var lower = desc.ToLowerInvariant();
        if (MultiSignalRegex.IsMatch(lower)) return true;
        // "update the timeout values to 60" — plural noun + change verb implies multiple.
        return PluralNounRegex.IsMatch(lower) &&
               Regex.IsMatch(lower, @"\b(update|change|set|bump|increase|decrease|adjust|modify|switch)\b");
    }

    /// <summary>
    /// Splits a member addition across every matching class in the file — "add a string
    /// Email property to every DTO class" → one FillClassBody anchored edit per matching
    /// class, applied via the same batch path as multi-swap. The class-set spec is
    /// parsed from the description (kind: class/interface/record/struct, optional name
    /// filter like "DTO") and optional narrowing (suffix: "ending in Repository", prefix:
    /// "starting with Api", exclusion: "except the base one"); each edit carries its
    /// class's close-brace line number so identical anchors are disambiguated. Optional
    /// per-class naming clauses rename one class's member ("but NameKey on the first one")
    /// or prefix every member with its own class name ("named after the class"). Declines
    /// (null) when the spec doesn't match any anchorable class — a multi request must
    /// NEVER degrade to a single-class edit.
    /// </summary>
    private static DeterministicEdit? TryGenerateMultiMember(string relPath, string ext, string fileContent, string desc)
    {
        var req = ParseMemberRequest(desc);
        if (req == null) return null;

        // Class-set spec: kind ("class", "interface", ...) + optional name filter ("DTO").
        // Only the PLURAL spec form ("the DTO classes in this file" → kind2 group) is
        // normalized to the singular — the singular form already yields "class" and must
        // not be naively de-pluralized ("class".TrimEnd('s') would break it).
        string? kind = null;
        string? nameFilter = null;
        var m = MultiClassTargetRegex.Match(desc);
        if (m.Success)
        {
            if (m.Groups["kind2"].Success)
            {
                kind = m.Groups["kind2"].Value;
                kind = kind.EndsWith("es", StringComparison.OrdinalIgnoreCase) ? kind[..^2] : kind[..^1];
            }
            else if (m.Groups["kind"].Success)
            {
                kind = m.Groups["kind"].Value;
            }
            nameFilter = m.Groups["filter"].Success ? m.Groups["filter"].Value
                       : m.Groups["filter2"].Success ? m.Groups["filter2"].Value : null;
        }

        // Narrowing filters: "all classes ending in Repository", "every class starting
        // with Api", "every DTO class except the base one" — all optional, all composable.
        string? suffix = null, prefix = null, excluded = null;
        var sufM = ClassSuffixFilterRegex.Match(desc);
        if (sufM.Success) suffix = sufM.Groups["suffix"].Value;
        var preM = ClassPrefixFilterRegex.Match(desc);
        if (preM.Success) prefix = preM.Groups["prefix"].Value;
        var excM = ClassExclusionRegex.Match(desc);
        if (excM.Success) excluded = excM.Groups["excluded"].Value;

        var bodies = ext == ".cs"
            ? FindAllCsClassBodies(fileContent, nameFilter, kind)
            : ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs"
                ? FindAllTsClassBodies(fileContent, nameFilter, kind)
                : null;
        if (bodies is not { Count: > 0 }) return null;

        if (suffix != null || prefix != null || excluded != null)
        {
            bodies = bodies.Where(b =>
                    (suffix == null || b.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) &&
                    (prefix == null || b.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) &&
                    (excluded == null || !b.Name.Contains(excluded, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (bodies.Count == 0) return null; // the narrowed set is empty — never degrade
        }

        var isJs = ext is ".js" or ".jsx" or ".mjs" or ".cjs";
        var isCs = ext == ".cs";
        var t = isCs ? req.Type ?? InferCsType(req.Name) : req.Type ?? InferJsType(req.Name);

        // Per-class member naming — optional clauses on the multi description:
        //   "... but NameKey on the first one" → the Nth matching class gets a renamed member
        //   "... named after the class"         → every member is prefixed with its class name
        var (overrideIndex, overrideName, overrideOrdinal) = ParseOverrideClause(desc, bodies.Count);
        if (overrideIndex > bodies.Count) return null; // an unhonorable override must decline, never silently drop
        if (overrideIndex > 0 && bodies[overrideIndex - 1].CloseBraceLine == null)
            return null; // the overridden class is unanchorable — the NameKey intent must not silently vanish
        var adaptive = ClassNameAdaptiveRegex.IsMatch(desc);

        var edits = new List<EditPair>();
        var matched = 0;
        var skipped = 0;
        for (var i = 0; i < bodies.Count; i++)
        {
            var b = bodies[i];
            matched++;
            if (b.CloseBraceLine == null) { skipped++; continue; } // unanchorable (single-line) body
            var memberName = overrideIndex == i + 1
                ? overrideName!
                : adaptive ? ClassNameBase(b.Name) + Capitalize(req.Name) : req.Name;
            var perClassReq = req with { Name = memberName };
            var useAnchor = b.AnchorPrefix != null && b.AnchorPrefix != b.CloseBraceLine;
            var snippet = isCs
                ? BuildCsMemberSnippet(perClassReq, t,
                    MemberIndentFor(useAnchor ? b.AnchorPrefix : null, b.BraceIndent, isTs: false), b.IsInterface)
                : BuildTsMemberSnippet(perClassReq, t,
                    MemberIndentFor(useAnchor ? b.AnchorPrefix : null, b.BraceIndent, isTs: true), b.IsInterface, isJs);

            string oldStr, newStr;
            if (isCs)
            {
                oldStr = b.AnchorPrefix != null && b.AnchorPrefix != b.CloseBraceLine
                    ? b.AnchorPrefix + "\n" + b.CloseBraceLine
                    : b.CloseBraceLine;
                newStr = b.AnchorPrefix != null && b.AnchorPrefix != b.CloseBraceLine
                    ? b.AnchorPrefix + "\n" + snippet + "\n" + b.CloseBraceLine
                    : snippet + "\n" + b.CloseBraceLine;
            }
            else
            {
                oldStr = b.AnchorPrefix != null ? b.AnchorPrefix + b.CloseBraceLine : b.CloseBraceLine;
                newStr = b.AnchorPrefix != null
                    ? b.AnchorPrefix + snippet + "\n" + b.CloseBraceLine
                    : snippet + "\n" + b.CloseBraceLine;
            }
            edits.Add(new EditPair { OldString = oldStr, NewString = newStr, LineNumber = b.LineNumber });
        }
        if (edits.Count == 0) return null;

        var kindWord = kind ?? "class";
        var kindLabel = kindWord + (matched == 1 ? "" : kindWord == "class" ? "es" : "s");
        var skipText = skipped > 0 ? $", skipped {skipped} unanchorable" : "";
        var specText = (suffix != null ? $", ending in '{suffix}'" : "")
                     + (prefix != null ? $", starting with '{prefix}'" : "")
                     + (excluded != null ? $", excluding '{excluded}'" : "");
        var variation = overrideIndex > 0
            ? $", '{t} {overrideName}' on the {overrideOrdinal}"
            : adaptive ? ", class-prefixed names" : "";
        var reason = $"Synthesized {edits.Count} member edits: '{t} {req.Name}'{variation} " +
                     $"(applied {edits.Count}/{matched} matching {kindLabel}{specText}{skipText}) — no LLM";

        // The marker carries the applied/total counts so the meeting ticker can render a
        // single compact "N/M classes updated" line instead of a line per edit.
        return new DeterministicEdit(
            EditStrategy.FillClassBody, "class", req.Name,
            edits[0].OldString,
            $"(deterministic batch: {edits.Count} edits, applied {edits.Count}/{matched} {kindLabel})",
            edits[0].LineNumber,
            reason, edits);
    }

    /// <summary>Reads the optional "... but &lt;name&gt; on the &lt;ordinal&gt;" clause. Returns the
    /// 1-based index of the matching class the override applies to (or -1 when absent), the
    /// override member name, and the literal ordinal word the user wrote ("first"/"last"/…).
    /// "last" resolves to <paramref name="bodyCount"/>.</summary>
    private static (int index1Based, string? name, string ordinal) ParseOverrideClause(string desc, int bodyCount)
    {
        var m = PerClassOverrideRegex.Match(desc);
        if (!m.Success || !m.Groups["name"].Success || !m.Groups["ordinal"].Success)
            return (-1, null, "");
        var ordinal = m.Groups["ordinal"].Value.ToLowerInvariant();
        var index = ordinal switch
        {
            "first" => 1, "second" => 2, "third" => 3, "fourth" => 4, "fifth" => 5,
            "last" => bodyCount,
            _ => int.TryParse(ordinal, out var n) ? n : -1
        };
        return (index, m.Groups["name"].Value, ordinal);
    }

    private static string Capitalize(string s)
        => char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s.Substring(1) : "");

    /// <summary>Class name minus a trailing DTO-ish suffix, so "add a Name property ... named
    /// after the class" turns UserDto → "User" + "Name" → UserName instead of UserDtoName.</summary>
    private static string ClassNameBase(string className)
    {
        foreach (var suffix in new[] { "Dto", "DTO", "Entity", "Model", "Vm", "VM", "Info" })
        {
            if (className.Length > suffix.Length && className.EndsWith(suffix, StringComparison.Ordinal))
                return className.Substring(0, className.Length - suffix.Length);
        }
        return className;
    }

    /// <summary>
    /// Splits a repeated-pattern change into N anchored edits — one per occurrence of
    /// the name in the file. Each edit swaps only ITS line (name-relative value match),
    /// skips already-correct lines, and carries the occurrence's line number so the
    /// batch apply path disambiguates identical anchors. Declines when nothing matches
    /// or the value can't be verified per line.
    /// </summary>
    private static DeterministicEdit? TryGenerateMultiSwap(string fileContent, string desc, string ext)
    {
        // Quantifier filler ("all five", "the", "every") is pure emphasis — removing it
        // cannot change which edit is described, and lets a camelCase name like
        // "retryCount" sit directly before the plural noun for clean matching.
        var stripped = MultiFillerRegex.Replace(desc, "");

        string name;
        string? toRaw;
        string? fromRaw;

        // Form 1: "update the timeout values to 60" / "set all five RetryCount defaults to 5"
        var multiSet = MultiSetToRegex.Match(stripped);
        if (multiSet.Success)
        {
            name = multiSet.Groups[1].Value;
            fromRaw = null; // read each occurrence's current value from the file
            toRaw = Unquote(multiSet.Groups[2].Value);
        }
        else
        {
            // Form 2: "update all five RetryCount from 3 to 5" — reuse the single-swap parsers.
            var fromTo = SwapFromToRegex.Match(stripped);
            var setTo = fromTo.Success ? null : SetToRegex.Match(stripped);
            if (!fromTo.Success && (setTo == null || !setTo.Success))
                return null;
            if (fromTo.Success)
            {
                name = fromTo.Groups[1].Value;
                fromRaw = Unquote(fromTo.Groups[2].Value);
                toRaw = Unquote(fromTo.Groups[3].Value);
            }
            else
            {
                name = setTo!.Groups[1].Value;
                fromRaw = null;
                toRaw = Unquote(setTo.Groups[2].Value);
            }
        }

        if (string.IsNullOrEmpty(toRaw) || (fromRaw != null && toRaw.Equals(fromRaw, StringComparison.OrdinalIgnoreCase)))
            return null;

        var lines = fileContent.Split('\n');
        var edits = new List<EditPair>();
        var occurrences = 0;      // real name occurrences found (comment/string mentions excluded)
        var alreadyCorrect = 0;   // already the target value — no-op
        var multiLine = 0;        // value sits on the next line — too fiddly for batch mode
        var valueMismatch = 0;    // a different value — left alone
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*")
                || trimmed.StartsWith("#"))
                continue;
            // Quoted-key recognition is gated to JSON-family files only: in code files a line
            // like '"maxRetries": 3' could live INSIDE a template/string literal, so letting it
            // match there would edit string content. JSON files are data — a quoted key is the key.
            if (!ContainsStandaloneName(lines[i], name, ext is ".json" or ".jsonc" or ".json5")) continue;
            occurrences++;
            var next = i + 1 < lines.Length ? lines[i + 1] : null;

            string? from;
            if (fromRaw != null)
            {
                if (!HasLiteralAfterName(lines[i], name, fromRaw))
                {
                    // Read the line's actual literal to tell "already the target" apart
                    // from "a different value" and "value on the next line".
                    var cur = ReadCurrentLiteral(lines[i], next, name);
                    if (cur != null && cur.Value.line != lines[i]) { multiLine++; continue; }
                    if (cur != null && StripUnitSuffix(Unquote(cur.Value.raw))
                            .Equals(StripUnitSuffix(toRaw), StringComparison.OrdinalIgnoreCase))
                    { alreadyCorrect++; continue; }
                    valueMismatch++;
                    continue;
                }
                from = fromRaw;
            }
            else
            {
                var cur = ReadCurrentLiteral(lines[i], next, name);
                if (cur == null) { valueMismatch++; continue; }
                if (cur.Value.line != lines[i]) { multiLine++; continue; } // multi-line value — too fiddly for batch mode
                from = StripUnitSuffix(cur.Value.raw);
            }

            var (swapped, newValueLine) = SwapLiteralInLine(lines[i], name, from, toRaw);
            if (!swapped) { valueMismatch++; continue; }
            if (newValueLine == lines[i]) { alreadyCorrect++; continue; } // already the target — no-op
            edits.Add(new EditPair { OldString = lines[i], NewString = newValueLine, LineNumber = i + 1 });
        }
        if (edits.Count == 0) return null;

        // G6 — batch-partial transparency: report how many occurrences were applied vs
        // skipped and why, so a partial batch is visible instead of silently partial.
        // (Surfaces in the SSE log via the deterministic-synthesis EmitLog of Reason.)
        var skipped = occurrences - edits.Count;
        var skipDetails = new List<string>();
        if (alreadyCorrect > 0) skipDetails.Add($"{alreadyCorrect} already-correct");
        if (multiLine > 0) skipDetails.Add($"{multiLine} multi-line value");
        if (valueMismatch > 0) skipDetails.Add($"{valueMismatch} value mismatch");
        var skipText = skipped > 0
            ? $"skipped {skipped}: {string.Join(", ", skipDetails)}"
            : "skipped 0";
        var reason = $"Synthesized {edits.Count} anchored edits: '{name}' → {toRaw} " +
                     $"(applied {edits.Count}/{occurrences} occurrences, {skipText}) — no LLM";

        // The marker carries the applied/total counts so the meeting ticker can render a
        // single compact "N/M occurrences updated" line instead of a line per edit.
        return new DeterministicEdit(
            EditStrategy.AnchoredEdit, null, name,
            edits[0].OldString,
            $"(deterministic batch: {edits.Count} edits, applied {edits.Count}/{occurrences} occurrences)",
            edits[0].LineNumber,
            reason,
            edits);
    }

    /// <summary>True when <paramref name="name"/> appears in the line as a standalone identifier
    /// (not inside a string, trailing comment, or a longer word).</summary>
    private static bool ContainsStandaloneName(string line, string name, bool allowJsonQuotedKey = false)
    {
        var idx = line.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var before = idx > 0 ? line[idx - 1] : '\0';
            var afterIdx = idx + name.Length;
            var after = afterIdx < line.Length ? line[afterIdx] : '\0';
            var okBefore = !(char.IsLetterOrDigit(before) || before == '_' || before == '.' || before == '"' || before == '\'');
            var okAfter = !(char.IsLetterOrDigit(after) || after == '_');
            // A trailing comment on the SAME line is not a real occurrence — "const x = 1; // retryCount = 3"
            // must not be edited (the name is inside the comment, the real variable has a different value).
            if (!IsInsideLineComment(line, idx) &&
                ((okBefore && okAfter) || (allowJsonQuotedKey && IsJsonQuotedKey(line, idx, name.Length))))
                return true;
            idx = line.IndexOf(name, idx + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

/// <summary>True when the identifier at <paramref name="idx"/> (length <paramref name="len"/>)
    /// in <paramref name="line"/> is a JSON-style QUOTED KEY of a key:value pair — '"maxRetries": 3' —
    /// the name wrapped in matching quotes, then (after whitespace) a ':', with a SCALAR value (not
    /// '{'/'[') so a nested object's literals are never mis-swapped as the key's value. The closing
    /// quote also doubles as the word boundary, so a longer key ("maxRetriesX") never matches "maxRetries".
    /// Consulted only for JSON-family files (the caller gates it) — in code files the same text could
    /// live inside a string/template literal.</summary>
    private static bool IsJsonQuotedKey(string line, int idx, int len)
    {
        if (idx <= 0) return false;
        var open = line[idx - 1];
        if (open != '"' && open != '\'') return false;
        var closeIdx = idx + len;
        if (closeIdx >= line.Length || line[closeIdx] != open) return false;
        var p = closeIdx + 1;
        while (p < line.Length && char.IsWhiteSpace(line[p])) p++;
        if (p >= line.Length || line[p] != ':') return false;
        p++;
        while (p < line.Length && char.IsWhiteSpace(line[p])) p++;
        return p < line.Length && line[p] != '{' && line[p] != '[';
    }

    /// <summary>True when <paramref name="position"/> in <paramref name="line"/> sits inside a
    /// '//' line comment — string-aware, so '//' inside quotes/backticks ("http://…") is not
    /// treated as a comment marker.</summary>
    private static bool IsInsideLineComment(string line, int position)
    {
        if (position <= 0) return false;
        var inString = '\0';
        for (var i = 0; i < position; i++)
        {
            var c = line[i];
            if (inString != '\0')
            {
                if (c == '\\') { i++; continue; }
                if (c == inString) inString = '\0';
                continue;
            }
            if (c == '"' || c == '\'' || c == '`') { inString = c; continue; }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return true;
        }
        return false;
    }

    // ── Literal swap: "X from N to M" / "set X to M" ─────────────────────────

    private static readonly Regex SwapFromToRegex = new(
        @"\b([A-Za-z_][A-Za-z0-9_.]*)\s+(?:from|of)\s+(" +
        NumberPattern + @")\s+(?:to|→|->)\s+(" + NumberPattern + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SetToRegex = new(
        @"\b(?:set|change|update|bump|switch|increase|decrease)\s+(?:the\s+|it\s+)?([A-Za-z_][A-Za-z0-9_.]*)\s+to\s+(" +
        @"[+-]?\d[\d_.]*[a-zA-Z%]*|true|false|null)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Number literal with optional sign, decimals, underscores and a unit suffix ("12px", "30", "-1.5f", "3m").
    private const string NumberPattern = @"[""']?[+-]?\d[\d_.]*[a-zA-Z%]*[""']?";

    private static DeterministicEdit? TryGenerateLiteralSwap(string fileContent, string desc)
    {
        var fromTo = SwapFromToRegex.Match(desc);
        var setTo = fromTo.Success ? null : SetToRegex.Match(desc);
        if (!fromTo.Success && (setTo == null || !setTo.Success))
            return null;

        string name;
        string? toRaw;
        string? fromRaw;
        if (fromTo.Success)
        {
            name = fromTo.Groups[1].Value;
            fromRaw = Unquote(fromTo.Groups[2].Value);
            toRaw = Unquote(fromTo.Groups[3].Value);
        }
        else
        {
            name = setTo!.Groups[1].Value;
            fromRaw = null; // read current value from the file
            toRaw = Unquote(setTo.Groups[2].Value);
        }
        if (string.IsNullOrEmpty(toRaw) || toRaw.Equals(fromRaw, StringComparison.OrdinalIgnoreCase))
            return null;

        var hit = FindSwapLocation(fileContent, name, fromRaw);
        if (hit == null) return null;

        var (lineIdx, oldLine, nextLine) = hit.Value;
        var valueLine = fromRaw != null && HasLiteralAfterName(oldLine, name, fromRaw)
            ? oldLine
            : (fromRaw != null && nextLine != null && ContainsLiteral(nextLine, fromRaw) ? nextLine : null);

        if (fromRaw == null)
        {
            // "set X to M": current value read from the file, after the name's position.
            var current = ReadCurrentLiteral(oldLine, nextLine, name);
            if (current == null) return null;
            var currentClean = current.Value.raw;
            if (currentClean.Equals(toRaw, StringComparison.OrdinalIgnoreCase))
                return null; // already the target — no-op
            valueLine = current.Value.line;
            // Drop any unit suffix ("12px" → "12"): SwapLiteralInLine re-derives the
            // unit from the file, so "set fontSize to 14" turns 12px into 14px.
            fromRaw = StripUnitSuffix(currentClean);
        }

        if (valueLine == null)
            return null; // name found but the from-value isn't on the same/next line — don't guess

        var (swapped, newValueLine) = SwapLiteralInLine(valueLine, name, fromRaw, toRaw);
        if (!swapped) return null;

        string oldStr, newStr;
        int lineNumber;
        if (valueLine == oldLine)
        {
            oldStr = oldLine;
            newStr = newValueLine;
            lineNumber = lineIdx + 1;
        }
        else
        {
            oldStr = oldLine + "\n" + valueLine;
            newStr = oldLine + "\n" + newValueLine;
            lineNumber = lineIdx + 1;
        }

        return new DeterministicEdit(
            EditStrategy.AnchoredEdit, null, name, oldStr, newStr, lineNumber,
            $"Literal swap: '{name}' {fromRaw} → {toRaw}");
    }

    private static (int lineIdx, string line, string? nextLine)? FindSwapLocation(
        string fileContent, string name, string? fromRaw)
    {
        var lines = fileContent.Split('\n');
        // The name must be a real identifier, not text inside a string or comment:
        // the lookbehind blocks quotes, and comment-prefixed lines are skipped.
        var nameRx = new Regex(@"(?<!['""\w.])" + Regex.Escape(name) + @"(?![\w])", RegexOptions.IgnoreCase);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*")
                || trimmed.StartsWith("#"))
                continue;
            if (!nameRx.IsMatch(lines[i])) continue;
            var next = i + 1 < lines.Length ? lines[i + 1] : null;
            if (fromRaw == null)
            {
                // "set X to M": need a readable literal on this or the next line.
                var cur = ReadCurrentLiteral(lines[i], next, name);
                if (cur != null) return (i, lines[i], next);
                continue;
            }
            if (HasLiteralAfterName(lines[i], name, fromRaw)) return (i, lines[i], next);
            if (next != null && ContainsLiteral(next, fromRaw)) return (i, lines[i], next);
        }
        return null;
    }

    /// <summary>Reads the first standalone literal that appears AFTER the name's position in the line.</summary>
    private static (string raw, string line)? ReadCurrentLiteral(string line, string? nextLine, string name)
    {
        foreach (var candidate in new[] { line, nextLine ?? "" })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var namePos = candidate.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            var start = namePos >= 0 ? namePos + name.Length : 0;
            if (start >= candidate.Length) continue;
            var m = Regex.Match(candidate.Substring(start),
                @"[""']?((?:[+-]?\d[\d_.]*[a-zA-Z%]*)|(?:true|false|null))[""']?(?![\w.])",
                RegexOptions.IgnoreCase);
            if (m.Success) return (m.Groups[1].Value, candidate);
        }
        return null;
    }

    private static bool ContainsLiteral(string line, string literal)
    {
        return Regex.IsMatch(line,
            @"(?<![\w])[""']?" + Regex.Escape(literal) + @"[a-zA-Z%]*[""']?(?![\w.])",
            RegexOptions.IgnoreCase);
    }

    private static bool HasLiteralAfterName(string line, string name, string literal)
    {
        var namePos = line.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        var start = namePos >= 0 ? namePos + name.Length : 0;
        if (start >= line.Length) return false;
        // Only the FIRST literal after the name counts — "const retryCount = 9; // retryCount = 3"
        // must not match a "3" that only appears inside the trailing comment.
        var m = Regex.Match(line.Substring(start),
            @"[""']?((?:[+-]?\d[\d_.]*[a-zA-Z%]*)|(?:true|false|null))[""']?(?![\w.])",
            RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var matchAbs = start + m.Index;
        if (IsInsideLineComment(line, matchAbs)) return false; // literal inside a trailing comment
        var matchValue = StripUnitSuffix(Unquote(m.Groups[1].Value));
        return matchValue.Equals(StripUnitSuffix(literal), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripUnitSuffix(string value)
    {
        // Only strip trailing letters that follow a digit ("12px" → "12");
        // pure-word values like "false"/"null" must survive untouched.
        var m = Regex.Match(value, @"\d[a-zA-Z%]+$");
        return m.Success ? value.Substring(0, m.Index + 1) : value;
    }

    /// <summary>Swaps the standalone occurrence of <paramref name="from"/> that appears AFTER
    /// <paramref name="name"/>'s position in the line, preserving any surrounding quotes and
    /// unit suffix (12px → 14px). "x = 1; y = 1;" with name y swaps the SECOND literal.</summary>
    private static (bool swapped, string newLine) SwapLiteralInLine(string line, string name, string from, string to)
    {
        var namePos = line.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        var start = namePos >= 0 ? namePos + name.Length : 0;
        if (start >= line.Length) return (false, line);
        var m = Regex.Match(line.Substring(start),
            @"[""']?(" + Regex.Escape(from) + @")([a-zA-Z%]*)([""']?)(?![\w.])",
            RegexOptions.IgnoreCase);
        if (!m.Success) return (false, line);
        var absIndex = start + m.Index;
        var matchValue = line.Substring(absIndex, m.Length);
        var openingQuote = matchValue.Length > 0 && (matchValue[0] == '"' || matchValue[0] == '\'') ? matchValue[0].ToString() : "";
        var closingQuote = m.Groups[3].Success && (m.Groups[3].Value == "\"" || m.Groups[3].Value == "'") ? m.Groups[3].Value : "";
        var unit = m.Groups[2].Success ? m.Groups[2].Value : "";
        var replacement = openingQuote + to + unit + closingQuote;
        var newLine = line.Remove(absIndex, m.Length).Insert(absIndex, replacement);
        return (true, newLine);
    }

    // ── Class-member addition (property / field / getter-setter) ─────────────

    private static readonly Regex MemberBeforeRegex = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s+(?:property|field)\b", RegexOptions.IgnoreCase);
    private static readonly Regex MemberAfterRegex = new(
        @"\b(?:property|field)\s+([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.IgnoreCase);
    private static readonly Regex ClassAnchorRegex = new(
        @"\b(?:to|in|on)\s+(?:the\s+)?([A-Za-z_][A-Za-z0-9_]*)\s+(class|record|struct|interface)\b",
        RegexOptions.IgnoreCase);

    private static DeterministicEdit? TryGenerateMember(string relPath, string ext, string fileContent, string desc)
    {
        var req = ParseMemberRequest(desc);
        if (req == null) return null;

        if (ext == ".cs")
        {
            var anchor = FindCsClassBody(fileContent, req.ClassName);
            if (anchor == null) return null;
            var (anchorPrefix, closeBraceLine, lineNumber, braceIndent, isInterface) = anchor.Value;

            var t = req.Type ?? InferCsType(req.Name);
            var snippet = BuildCsMemberSnippet(req, t,
                MemberIndentFor(anchorPrefix != null && anchorPrefix != closeBraceLine ? anchorPrefix : null, braceIndent, isTs: false),
                isInterface);

            var oldStr = anchorPrefix != null && anchorPrefix != closeBraceLine
                ? anchorPrefix + "\n" + closeBraceLine
                : closeBraceLine;
            var newStr = anchorPrefix != null && anchorPrefix != closeBraceLine
                ? anchorPrefix + "\n" + snippet + "\n" + closeBraceLine
                : snippet + "\n" + closeBraceLine;

            return new DeterministicEdit(
                EditStrategy.FillClassBody, "class", req.ClassName ?? req.Name, oldStr, newStr, lineNumber,
                req.IsGetterSetter
                    ? $"Synthesized getter/setter pair for '{req.Name}' in {(req.ClassName ?? "last class")} — no LLM"
                    : $"Synthesized property '{t} {req.Name}' in {(req.ClassName ?? "last class")} — no LLM");
        }

        if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
        {
            var anchor = FindTsClassBody(fileContent, req.ClassName);
            if (anchor == null) return null;
            var (anchorPrefix, closeBraceLine, lineNumber, braceIndent, isInterface, _) = anchor.Value;

            var t = req.Type ?? InferJsType(req.Name);
            var snippet = BuildTsMemberSnippet(req, t,
                MemberIndentFor(anchorPrefix != null && anchorPrefix != closeBraceLine ? anchorPrefix : null, braceIndent, isTs: true),
                isInterface, ext is ".js" or ".jsx" or ".mjs" or ".cjs");

            // Widen the anchor beyond a lone '}' (G2): prefixing the class close brace
            // with the contiguous body slice (last member line — or class-open line when
            // the body is empty — through to the close brace) makes the anchor unique, so
            // a duplicate '}' can't silently place the member in a nested block or the
            // wrong class. The prefix ends with the line break, so oldStr is an exact,
            // contiguous slice of the file and always matches.
            var oldStr = anchorPrefix != null
                ? anchorPrefix + closeBraceLine
                : closeBraceLine;
            var newStr = anchorPrefix != null
                ? anchorPrefix + snippet + "\n" + closeBraceLine
                : snippet + "\n" + closeBraceLine;

            return new DeterministicEdit(
                EditStrategy.FillClassBody, "class", req.ClassName ?? req.Name, oldStr, newStr, lineNumber,
                $"Synthesized member '{GetTsMemberName(req.Name)}: {t}' in {(req.ClassName ?? "last class")} — no LLM");
        }

        return null;
    }

    /// <summary>The parsed member-request vocabulary: getter/setter form, the member
    /// name, the explicit type (or null to infer), and the optional single-class anchor.</summary>
    private sealed record MemberRequest(bool IsGetterSetter, string Name, string? Type, string? ClassName);

    /// <summary>Parses a member-request description into its vocabulary. Returns null
    /// when no member name can be extracted (the caller then declines).</summary>
    private static MemberRequest? ParseMemberRequest(string desc)
    {
        var isGetterSetter = Regex.IsMatch(desc,
            @"\b(getter|get\s+and\s+set|get/set)\b", RegexOptions.IgnoreCase);

        // name — the identifier immediately adjacent to "property"/"field", or after "for".
        string? name = null;
        if (isGetterSetter)
        {
            var forM = Regex.Match(desc, @"\bfor\s+(?:the\s+)?([A-Za-z_][A-Za-z0-9_]*)\b",
                RegexOptions.IgnoreCase);
            name = forM.Success ? forM.Groups[1].Value : null;
        }
        if (name == null)
        {
            var before = MemberBeforeRegex.Match(desc);
            if (before.Success && !IsModifierWord(before.Groups[1].Value))
                name = before.Groups[1].Value;
            else
            {
                var after = MemberAfterRegex.Match(desc);
                if (after.Success) name = after.Groups[1].Value;
            }
        }
        if (name == null) return null;

        // explicit type: "of type string", "type string", or the token before the name.
        string? type = null;
        var typeM = Regex.Match(desc,
            @"\b(?:of\s+)?type\s+([A-Za-z_][A-Za-z0-9_<>\[\],.]*?)(?:\s|$)",
            RegexOptions.IgnoreCase);
        if (typeM.Success)
        {
            var t = typeM.Groups[1].Value.TrimEnd('.', ',');
            if (t.Length > 0) type = t;
        }
        if (type == null)
        {
            var beforeName = Regex.Match(desc,
                @"\b([A-Za-z_][A-Za-z0-9_<>\[\],.]*)\s+" + Regex.Escape(name) + @"\s+(?:property|field)\b",
                RegexOptions.IgnoreCase);
            if (beforeName.Success && !IsModifierWord(beforeName.Groups[1].Value))
                type = beforeName.Groups[1].Value;
        }

        // class anchor: named in the description ("to the User class"), else the last one.
        string? className = null;
        var anchorM = ClassAnchorRegex.Match(desc);
        if (anchorM.Success) className = anchorM.Groups[1].Value;

        return new MemberRequest(isGetterSetter, name, type, className);
    }

    /// <summary>The indentation for a newly-synthesized member: mirrors the LAST EXISTING member
    /// line's leading whitespace (so a formatter-reindented file gets style-consistent members
    /// instead of a hardcoded default), falling back to the class indent + the language's member
    /// indent when the class body is empty (no anchor).</summary>
    private static string MemberIndentFor(string? anchorPrefix, string braceIndent, bool isTs)
        => anchorPrefix != null
            ? Regex.Match(anchorPrefix, @"^\s*").Value
            : braceIndent + (isTs ? "  " : "    ");

    private static string BuildCsMemberSnippet(MemberRequest req, string type, string memberIndent, bool isInterface)
    {
        return req.IsGetterSetter
            ? BuildCsGetterSetter(req.Name, type, memberIndent)
            : isInterface
                ? memberIndent + $"{type} {req.Name} {{ get; set; }}"
                : memberIndent + $"public {type} {req.Name} {{ get; set; }}";
    }

    private static string BuildTsMemberSnippet(MemberRequest req, string type, string memberIndent,
        bool isInterface, bool isJs)
    {
        var memberName = GetTsMemberName(req.Name);
        if (isInterface)
            return memberIndent + $"{memberName}: {type};";
        if (!isJs)
            return DefaultTsValue(type) != null
                ? memberIndent + $"public {memberName}: {type} = {DefaultTsValue(type)};"
                : memberIndent + $"public {memberName}!: {type};";
        return memberIndent + $"{memberName} = {DefaultJsValue(type)};";
    }

    /// <summary>TS/JS members are conventionally camelCase — "Name" becomes "name".</summary>
    private static string GetTsMemberName(string name)
        => Char.ToLowerInvariant(name[0]) + (name.Length > 1 ? name.Substring(1) : "");

    private static string BuildCsGetterSetter(string name, string type, string indent)
    {
        var field = "_" + Char.ToLowerInvariant(name[0]) + (name.Length > 1 ? name.Substring(1) : "");
        return $"{indent}private {type} {field};\n\n" +
               $"{indent}public {type} {name}\n" +
               $"{indent}{{\n" +
               $"{indent}    get {{ return {field}; }}\n" +
               $"{indent}    set {{ {field} = value; }}\n" +
               $"{indent}}}";
    }

    // ── C# class-body anchor via Roslyn ──────────────────────────────────────

    private static (string? anchorPrefix, string closeBraceLine, int lineNumber, string braceIndent, bool isInterface)?
        FindCsClassBody(string source, string? className)
    {
        SyntaxTree tree;
        try { tree = CSharpSyntaxTree.ParseText(source); }
        catch { return null; }

        var root = tree.GetRoot();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().ToList();
        var structs = root.DescendantNodes().OfType<StructDeclarationSyntax>().ToList();
        var records = root.DescendantNodes().OfType<RecordDeclarationSyntax>().ToList();

        // Guard G3: an unnamed member add with more than one type declaration in the
        // file is ambiguous — declining is safer than silently anchoring the last one.
        if (string.IsNullOrWhiteSpace(className))
        {
            var typeCount = classes.Count + interfaces.Count + structs.Count + records.Count;
            if (typeCount > 1) return null;
        }

        TypeDeclarationSyntax? target = null;
        var isInterface = false;
        if (!string.IsNullOrWhiteSpace(className))
        {
            target = (TypeDeclarationSyntax?)classes.FirstOrDefault(c => c.Identifier.Text == className)
                  ?? (TypeDeclarationSyntax?)interfaces.FirstOrDefault(i => i.Identifier.Text == className)
                  ?? (TypeDeclarationSyntax?)structs.FirstOrDefault(s => s.Identifier.Text == className)
                  ?? (TypeDeclarationSyntax?)records.FirstOrDefault(r => r.Identifier.Text == className);
            isInterface = target is InterfaceDeclarationSyntax;
        }
        if (target == null)
        {
            target = classes.LastOrDefault();
            if (target == null) { target = interfaces.LastOrDefault(); isInterface = true; }
            if (target == null) { target = structs.LastOrDefault(); }
            if (target == null) { target = records.LastOrDefault(); }
        }
        if (target == null) return null;

        var closePos = target.CloseBraceToken.SpanStart;
        var closeBraceLine = ExtractLine(source, closePos, out var lineNumber);
        // Close brace must open its own line — single-line classes are too risky to anchor.
        if (!Regex.IsMatch(closeBraceLine, @"^\s*}\s*;?(?:\s*//.*)?$")) return null;
        var braceIndent = Regex.Match(closeBraceLine, @"^\s*").Value;

        string? anchorPrefix = null;
        var lastMember = target.Members.LastOrDefault();
        if (lastMember != null)
        {
            var lastPos = lastMember.GetLastToken().SpanStart;
            var memberLine = ExtractLine(source, lastPos, out _);
            if (memberLine != closeBraceLine &&
                Regex.IsMatch(memberLine, @"^\s*[^\s]", RegexOptions.Singleline))
                anchorPrefix = memberLine;
        }

        return (anchorPrefix, closeBraceLine, lineNumber, braceIndent, isInterface);
    }

    /// <summary>One anchorable class body for the multi-member batch path. CloseBraceLine
    /// is null when the body can't be safely anchored (single-line declaration) — such
    /// classes are counted as skipped, not silently edited.</summary>
    private sealed record ClassBody(
        string Name, string Kind, string? AnchorPrefix, string? CloseBraceLine,
        int LineNumber, string BraceIndent, bool IsInterface);

    /// <summary>Finds every C# type declaration (class/interface/struct/record) whose name
    /// matches <paramref name="nameFilter"/> (optional substring, case-insensitive) and
    /// whose kind matches <paramref name="kind"/> (optional: "class"/"interface"/"struct"/"record").
    /// Uses the same Roslyn anchor rules as the single-class path — each returned body
    /// carries its close-brace line number so the batch apply disambiguates identical anchors.</summary>
    private static List<ClassBody>? FindAllCsClassBodies(string source, string? nameFilter, string? kind)
    {
        SyntaxTree tree;
        try { tree = CSharpSyntaxTree.ParseText(source); }
        catch { return null; }

        var root = tree.GetRoot();
        var result = new List<ClassBody>();
        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            AddCsClassBody(source, cls, "class", result, nameFilter, kind);
        foreach (var itf in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
            AddCsClassBody(source, itf, "interface", result, nameFilter, kind);
        foreach (var st in root.DescendantNodes().OfType<StructDeclarationSyntax>())
            AddCsClassBody(source, st, "struct", result, nameFilter, kind);
        foreach (var rec in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
            AddCsClassBody(source, rec, "record", result, nameFilter, kind);
        return result.Count > 0 ? result : null;
    }

    private static void AddCsClassBody(string source, TypeDeclarationSyntax decl, string kind,
        List<ClassBody> result, string? nameFilter, string? kindFilter)
    {
        if (kindFilter != null && !kind.Equals(kindFilter, StringComparison.OrdinalIgnoreCase)) return;
        if (nameFilter != null && !decl.Identifier.Text.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) return;

        var closePos = decl.CloseBraceToken.SpanStart;
        var closeBraceLine = ExtractLine(source, closePos, out var lineNumber);
        // Close brace must open its own line — single-line classes are too risky to anchor.
        if (!Regex.IsMatch(closeBraceLine, @"^\s*}\s*;?(?:\s*//.*)?$"))
        {
            result.Add(new ClassBody(decl.Identifier.Text, kind, null, null, 0, "", false));
            return;
        }
        var braceIndent = Regex.Match(closeBraceLine, @"^\s*").Value;

        string? anchorPrefix = null;
        var lastMember = decl.Members.LastOrDefault();
        if (lastMember != null)
        {
            var lastPos = lastMember.GetLastToken().SpanStart;
            var memberLine = ExtractLine(source, lastPos, out _);
            if (memberLine != closeBraceLine &&
                Regex.IsMatch(memberLine, @"^\s*[^\s]", RegexOptions.Singleline))
                anchorPrefix = memberLine;
        }

        result.Add(new ClassBody(decl.Identifier.Text, kind, anchorPrefix, closeBraceLine,
            lineNumber, braceIndent, decl is InterfaceDeclarationSyntax));
    }

    // ── TS/JS class-body anchor via brace matching ───────────────────────────

    private static (string? anchorPrefix, string closeBraceLine, int lineNumber, string braceIndent, bool isInterface, bool isTs)?
        FindTsClassBody(string source, string? className)
    {
        var isCode = BuildIsCodeMask(source);
        var matches = FindTsDeclarations(source, isCode);
        if (matches.Count == 0) return null;

        // Guard G3: an unnamed member add with multiple class/interface declarations is
        // ambiguous — decline rather than silently anchoring the last one.
        if (string.IsNullOrWhiteSpace(className) && matches.Count > 1)
            return null;

        Match? chosen = null;
        if (!string.IsNullOrWhiteSpace(className))
            chosen = matches.FirstOrDefault(m => m.Groups[2].Value == className);
        chosen ??= matches.LastOrDefault();
        if (chosen == null) return null;

        var anchor = TryAnchorTsBody(source, chosen);
        if (anchor == null) return null;
        return (anchor.Value.AnchorPrefix, anchor.Value.CloseBraceLine, anchor.Value.LineNumber,
            anchor.Value.BraceIndent, anchor.Value.IsInterface, anchor.Value.Kind == "class");
    }

    /// <summary>Finds every class/interface declaration in a TS/JS file whose name matches
    /// <paramref name="nameFilter"/> (optional substring, case-insensitive) and whose kind
    /// matches <paramref name="kind"/> (optional: "class"/"interface"/...), returning one
    /// anchorable body per declaration for the multi-member batch path. Comment/string
    /// mentions are excluded via the code mask, so a JSDoc @class tag can't inflate the set.</summary>
    private static List<ClassBody>? FindAllTsClassBodies(string source, string? nameFilter, string? kind)
    {
        var isCode = BuildIsCodeMask(source);
        var matches = FindTsDeclarations(source, isCode);
        if (matches.Count == 0) return null;

        var result = new List<ClassBody>();
        foreach (var m in matches)
        {
            var declKind = m.Groups[1].Value;
            var declName = m.Groups[2].Value;
            if (kind != null && !declKind.Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (nameFilter != null && !declName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
            var anchor = TryAnchorTsBody(source, m);
            result.Add(new ClassBody(declName, declKind,
                anchor?.AnchorPrefix,
                anchor?.CloseBraceLine,
                anchor?.LineNumber ?? 0,
                anchor?.BraceIndent ?? "",
                anchor?.IsInterface ?? false));
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Marks which characters are real code (vs strings/comments) so a
    /// class/interface mention inside a doc comment, JSDoc tag, or string can't inflate
    /// the decl count (G3) or mis-anchor the chosen class.</summary>
    private static bool[] BuildIsCodeMask(string source)
    {
        var isCode = new bool[source.Length];
        var mode = 0; // 0 = code, 1 = string/template, 2 = line comment, 3 = block comment
        var strCh = '\0';
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (mode == 2) { if (c == '\n') mode = 0; continue; }
            if (mode == 3)
            {
                if (c == '*' && i + 1 < source.Length && source[i + 1] == '/') { mode = 0; i++; }
                continue;
            }
            if (mode == 1)
            {
                if (c == '\\') { i++; continue; }
                if (c == strCh) { mode = 0; continue; }
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') { mode = 2; i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*') { mode = 3; i++; continue; }
            if (c == '"' || c == '\'' || c == '`') { mode = 1; strCh = c; continue; }
            isCode[i] = true;
        }
        return isCode;
    }

    private static List<Match> FindTsDeclarations(string source, bool[] isCode)
    {
        var declRx = new Regex(@"\b(class|interface)\s+([A-Za-z_$][\w$]*)\b");
        return declRx.Matches(source).Cast<Match>()
            .Where(m => isCode[m.Index]).ToList();
    }

    /// <summary>Builds the anchor for one chosen TS/JS declaration: the close-brace line
    /// plus the contiguous G2 anchor prefix. Returns null when the body can't be safely
    /// anchored (no brace, close brace not alone on its line, or indentation mismatch).</summary>
    private static (string? AnchorPrefix, string CloseBraceLine, int LineNumber, string BraceIndent, bool IsInterface, string Kind)?
        TryAnchorTsBody(string source, Match chosen)
    {
        var open = source.IndexOf('{', chosen.Index + chosen.Length);
        if (open < 0) return null;
        var close = FindMatchingBrace(source, open);
        if (close == null) return null;

        var closeBraceLine = ExtractLine(source, close.Value, out var lineNumber);
        if (!Regex.IsMatch(closeBraceLine, @"^\s*}\s*(?:\s*//.*)?$")) return null;
        var braceIndent = Regex.Match(closeBraceLine, @"^\s*").Value;
        // The close brace must sit at the SAME indentation as the class declaration —
        // a stray '}' from a regex literal or nested block would otherwise mis-anchor.
        var declLine = ExtractLine(source, chosen.Index, out _);
        if (Regex.Match(declLine, @"^\s*").Value != braceIndent) return null;

        // Guard G2 — widen the anchor beyond a lone '}': scan backward through the
        // class body for the last real line (skipping blank and comment-only lines) and
        // use everything from that line's start up to the close brace as the anchor
        // prefix. The prefix is a CONTIGUOUS slice of the source, so the composed
        // oldStr always exists in the file — blank/comment lines trailing the last
        // member stay inside the anchor instead of breaking it. When the body is empty
        // the class-open line (with its line break) is used instead, so the anchor
        // carries the class name.
        string? anchorPrefix;
        {
            var closeLineStart = source.LastIndexOf('\n', Math.Max(0, close.Value - 1)) + 1;
            var bodyStart = open + 1;
            var pos = closeLineStart;
            var lastRealStart = -1;
            var depth = 0;
            while (pos > bodyStart)
            {
                var lineStart = source.LastIndexOf('\n', pos - 1) + 1;
                if (lineStart == pos) { pos--; continue; } // empty line — keep scanning
                var t = source.Substring(lineStart, pos - lineStart).Trim();
                if (t.Length == 0 || t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("/*"))
                {
                    pos = lineStart;
                    continue;
                }
                // Track brace nesting — lines at depth > 0 are inside methods/blocks,
                // not class-level declarations. We need a class-level member to anchor on.
                foreach (var ch in t)
                {
                    if (ch == '{') depth--;
                    else if (ch == '}') depth++;
                }
                if (depth <= 0)
                {
                    lastRealStart = lineStart;
                    break;
                }
                pos = lineStart;
            }
            if (lastRealStart >= 0)
                anchorPrefix = source.Substring(lastRealStart, closeLineStart - lastRealStart);
            else
            {
                var openLineStart = source.LastIndexOf('\n', Math.Max(0, open - 1)) + 1;
                anchorPrefix = source.Substring(openLineStart, closeLineStart - openLineStart);
            }
        }

        return (anchorPrefix, closeBraceLine, lineNumber, braceIndent,
            chosen.Groups[1].Value == "interface", chosen.Groups[1].Value);
    }

    /// <summary>Finds the brace matching <paramref name="openBraceIndex"/>, skipping strings,
    /// comments and template literals (including `${...}` interpolations).</summary>
    private static int? FindMatchingBrace(string source, int openBraceIndex)
    {
        var depth = 0;
        var mode = 0; // 0 = code, 1 = string/template, 2 = line comment, 3 = block comment
        var strCh = '\0';
        var templateDepth = 0; // open `${...}` interpolations inside template literals
        for (var i = openBraceIndex; i < source.Length; i++)
        {
            var c = source[i];
            if (mode == 2)
            {
                if (c == '\n') mode = 0;
                continue;
            }
            if (mode == 3)
            {
                if (c == '*' && i + 1 < source.Length && source[i + 1] == '/') { mode = 0; i++; }
                continue;
            }
            if (mode == 1)
            {
                if (c == '\\') { i++; continue; }
                if (c == strCh) { mode = 0; continue; }
                if (strCh == '`' && c == '$' && i + 1 < source.Length && source[i + 1] == '{')
                {
                    // Enter a `${...}` interpolation: count its braces, then resume
                    // template-string mode when the matching '}' is found below.
                    templateDepth++;
                    depth++;
                    i++;
                    mode = 0;
                }
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') { mode = 2; i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*') { mode = 3; i++; continue; }
            if (c == '"' || c == '\'' || c == '`') { mode = 1; strCh = c; continue; }
            if (c == '{') { depth++; continue; }
            if (c == '}')
            {
                depth--;
                if (templateDepth > 0)
                {
                    // This '}' closes a `${...}` interpolation — resume template-string mode.
                    templateDepth--;
                    mode = 1;
                    strCh = '`';
                    continue;
                }
                if (depth == 0) return i;
            }
        }
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsModifierWord(string word)
    {
        return word.ToLowerInvariant() is "a" or "an" or "the" or "new" or "public" or "private"
            or "protected" or "internal" or "static" or "readonly" or "const" or "of" or "type"
            or "add" or "create" or "insert" or "define" or "with" or "and" or "volatile" or "sealed";
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s.Substring(1, s.Length - 2);
        return s;
    }

    private static string InferCsType(string name)
    {
        var l = name.ToLowerInvariant();
        if (l.StartsWith("is") || l.StartsWith("has") || l.StartsWith("can") || l.StartsWith("should")
            || l.Contains("enabled") || l.Contains("visible") || l.Contains("active") || l.Contains("checked")
            || l.Contains("flag") || l.EndsWith("able"))
            return "bool";
        if (l.Contains("count") || l.Contains("total") || l.Contains("max") || l.Contains("min")
            || l.Contains("index") || l.Contains("size") || l.Contains("number") || l.Contains("amount")
            || l.Contains("limit") || l.Contains("age") || l.Contains("year") || l.Contains("month")
            || l.Contains("day") || l.Contains("port") || l.Contains("timeout") || l.Contains("retry"))
            return "int";
        if (l.Contains("price") || l.Contains("rate") || l.Contains("score") || l.Contains("duration")
            || l.Contains("weight") || l.Contains("percent") || l.Contains("balance") || l.Contains("salary"))
            return "double";
        return "string";
    }

    private static string InferJsType(string name)
    {
        var t = InferCsType(name);
        if (t == "double") return "number";
        if (t == "int") return "number";
        return t == "bool" ? "boolean" : "string";
    }

    private static string? DefaultTsValue(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "string" => "''",
            "number" or "int" or "double" or "float" => "0",
            "boolean" or "bool" => "false",
            "string[]" or "number[]" or "any[]" => "[]",
            _ => null
        };
    }

    private static string DefaultJsValue(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "string" => "''",
            "number" or "int" or "double" or "float" => "0",
            "boolean" or "bool" => "false",
            _ => "null"
        };
    }

    private static string ExtractLine(string source, int pos, out int lineNumber)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
        var lineEnd = source.IndexOf('\n', pos);
        if (lineEnd < 0) lineEnd = source.Length;
        lineNumber = 1;
        for (var i = 0; i < lineStart; i++)
            if (source[i] == '\n') lineNumber++;
        return source.Substring(lineStart, lineEnd - lineStart);
    }
}
