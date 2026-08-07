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
                var multi = TryGenerateMultiSwap(fileContent, desc);
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

    private static bool IsMultiMatchDescription(string desc)
    {
        var lower = desc.ToLowerInvariant();
        if (MultiSignalRegex.IsMatch(lower)) return true;
        // "update the timeout values to 60" — plural noun + change verb implies multiple.
        return PluralNounRegex.IsMatch(lower) &&
               Regex.IsMatch(lower, @"\b(update|change|set|bump|increase|decrease|adjust|modify|switch)\b");
    }

    /// <summary>
    /// Splits a repeated-pattern change into N anchored edits — one per occurrence of
    /// the name in the file. Each edit swaps only ITS line (name-relative value match),
    /// skips already-correct lines, and carries the occurrence's line number so the
    /// batch apply path disambiguates identical anchors. Declines when nothing matches
    /// or the value can't be verified per line.
    /// </summary>
    private static DeterministicEdit? TryGenerateMultiSwap(string fileContent, string desc)
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
            if (!ContainsStandaloneName(lines[i], name)) continue;
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

        return new DeterministicEdit(
            EditStrategy.AnchoredEdit, null, name,
            edits[0].OldString, $"(deterministic batch: {edits.Count} edits)", edits[0].LineNumber,
            reason,
            edits);
    }

    /// <summary>True when <paramref name="name"/> appears in the line as a standalone identifier
    /// (not inside a string, trailing comment, or a longer word).</summary>
    private static bool ContainsStandaloneName(string line, string name)
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
            if (okBefore && okAfter && !IsInsideLineComment(line, idx)) return true;
            idx = line.IndexOf(name, idx + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
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

        if (ext == ".cs")
        {
            var anchor = FindCsClassBody(fileContent, className);
            if (anchor == null) return null;
            var (anchorPrefix, closeBraceLine, lineNumber, braceIndent, isInterface) = anchor.Value;

            var t = type ?? InferCsType(name);
            var snippet = isGetterSetter
                ? BuildCsGetterSetter(name, t, braceIndent + "    ")
                : isInterface
                    ? braceIndent + "    " + $"{t} {name} {{ get; set; }}"
                    : braceIndent + "    " + $"public {t} {name} {{ get; set; }}";

            var oldStr = anchorPrefix != null && anchorPrefix != closeBraceLine
                ? anchorPrefix + "\n" + closeBraceLine
                : closeBraceLine;
            var newStr = anchorPrefix != null && anchorPrefix != closeBraceLine
                ? anchorPrefix + "\n" + snippet + "\n" + closeBraceLine
                : snippet + "\n" + closeBraceLine;

            return new DeterministicEdit(
                EditStrategy.FillClassBody, "class", className ?? name, oldStr, newStr, lineNumber,
                isGetterSetter
                    ? $"Synthesized getter/setter pair for '{name}' in {(className ?? "last class")} — no LLM"
                    : $"Synthesized property '{t} {name}' in {(className ?? "last class")} — no LLM");
        }

        if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
        {
            var anchor = FindTsClassBody(fileContent, className);
            if (anchor == null) return null;
            var (anchorPrefix, closeBraceLine, lineNumber, braceIndent, isInterface, _) = anchor.Value;

            var t = type ?? InferJsType(name);
            // TS/JS members are conventionally camelCase — "Name" becomes "name".
            var memberName = Char.ToLowerInvariant(name[0]) + (name.Length > 1 ? name.Substring(1) : "");
            var memberIndent = braceIndent + "  ";
            var isJs = ext is ".js" or ".jsx" or ".mjs" or ".cjs";
            string snippet;
            if (isInterface)
                snippet = memberIndent + $"{memberName}: {t};";
            else if (!isJs)
                snippet = DefaultTsValue(t) != null
                    ? memberIndent + $"public {memberName}: {t} = {DefaultTsValue(t)};"
                    : memberIndent + $"public {memberName}!: {t};";
            else
                snippet = memberIndent + $"{memberName} = {DefaultJsValue(t)};";

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
                EditStrategy.FillClassBody, "class", className ?? name, oldStr, newStr, lineNumber,
                $"Synthesized member '{memberName}: {t}' in {(className ?? "last class")} — no LLM");
        }

        return null;
    }

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

    // ── TS/JS class-body anchor via brace matching ───────────────────────────

    private static (string? anchorPrefix, string closeBraceLine, int lineNumber, string braceIndent, bool isInterface, bool isTs)?
        FindTsClassBody(string source, string? className)
    {
        // Mark which characters are real code (vs strings/comments) so a class/interface
        // mention inside a doc comment, JSDoc tag, or string can't inflate the decl count
        // (G3) or mis-anchor the chosen class.
        var isCode = new bool[source.Length];
        {
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
        }

        var declRx = new Regex(@"\b(class|interface)\s+([A-Za-z_$][\w$]*)\b");
        var matches = declRx.Matches(source).Cast<Match>()
            .Where(m => isCode[m.Index]).ToList();
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
            while (pos > bodyStart)
            {
                var lineStart = source.LastIndexOf('\n', pos - 1) + 1;
                if (lineStart == pos) { pos--; continue; } // empty line — keep scanning
                var t = source.Substring(lineStart, pos - lineStart).Trim();
                if (t.Length > 0 && !t.StartsWith("//") && !t.StartsWith("*") && !t.StartsWith("/*"))
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
            chosen.Groups[1].Value == "interface", chosen.Groups[1].Value == "class");
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
