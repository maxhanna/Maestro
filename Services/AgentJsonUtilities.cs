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
public static class AgentJsonUtilities
{
    public static string? RepairJsonString(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        var sb = new StringBuilder(json.Length);
        var inString = false; var depth = 0; var valueStartDepth = 0; var changed = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (!inString)
            {
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                if (c == '"') { inString = true; valueStartDepth = depth; }
                sb.Append(c);
                continue;
            }
            if (c == '\\') { sb.Append(c); i++; if (i < json.Length) sb.Append(json[i]); continue; }
            if (c == '"')
            {
                var nextNonWs = -1;
                for (var j = i + 1; j < json.Length; j++)
                    if (!char.IsWhiteSpace(json[j])) { nextNonWs = j; break; }
                if (nextNonWs >= 0 && depth == valueStartDepth &&
                    (json[nextNonWs] == ',' || json[nextNonWs] == '}' || json[nextNonWs] == ']' || json[nextNonWs] == ':'))
                {
                    sb.Append(c);
                    inString = false;
                }
                else { sb.Append("\\\""); changed = true; }
                continue;
            }
            if (c == '\n') { sb.Append("\\n"); changed = true; continue; }
            if (c == '\r') { sb.Append("\\r"); changed = true; continue; }
            if (c == '\t') { sb.Append("\\t"); changed = true; continue; }
            sb.Append(c);
        }
        if (inString) { sb.Append('"'); changed = true; }
        return changed ? sb.ToString() : null;
    }

    public static List<string> ExtractJsonBlocks(string text)
    {
        var blocks = new List<string>();
        var depth = 0; var start = -1; var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (inString)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') inString = false;
                continue;
            }
            if (text[i] == '"') { inString = true; continue; }
            if (text[i] == '{') { if (depth == 0) start = i; depth++; }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0 && start >= 0) { blocks.Add(text.Substring(start, i - start + 1)); start = -1; }
            }
        }
        return blocks;
    }

    public static string? RepairJsonStringValues(string json)
    {
        var sb = new StringBuilder(json.Length + 64);
        var inString = false;
        var changed = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (!inString)
            {
                if (c == '"') inString = true;
                sb.Append(c);
                continue;
            }
            if (c == '\\') { sb.Append(c); i++; if (i < json.Length) sb.Append(json[i]); continue; }
            if (c == '"') { sb.Append(c); inString = false; continue; }
            switch (c)
            {
                case '\n': sb.Append("\\n"); changed = true; break;
                case '\r': sb.Append("\\r"); changed = true; break;
                case '\t': sb.Append("\\t"); changed = true; break;
                default: sb.Append(c); break;
            }
        }
        return changed ? sb.ToString() : null;
    }

    internal static (string Text, int EndPos)? ExtractJsonStringValue(string text, int keyEndPos)
    {
        var pos = keyEndPos;
        while (pos < text.Length && text[pos] != ':') pos++;
        if (pos >= text.Length) return null;
        pos++;
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        if (pos >= text.Length || text[pos] != '"') return null;
        pos++;
        var start = pos;
        var afterKeyStart = keyEndPos + 5;
        var nextKeyPos = int.MaxValue;
        foreach (var key in new[] { "\"oldString\"", "\"newString\"", "\"path\"", "\"toPath\"", "\"description\"", "\"edits\"" })
        {
            var kpos = text.IndexOf(key, afterKeyStart, StringComparison.OrdinalIgnoreCase);
            if (kpos >= 0 && kpos < nextKeyPos) nextKeyPos = kpos;
        }
        var structureEnd = Math.Min(
            nextKeyPos < int.MaxValue ? nextKeyPos : int.MaxValue,
            text.Length);
        while (pos < text.Length && pos <= structureEnd)
        {
            if (text[pos] == '\\') { pos += 2; continue; }
            if (text[pos] == '"')
            {
                var afterPos = pos + 1;
                while (afterPos < text.Length && char.IsWhiteSpace(text[afterPos])) afterPos++;
                if (afterPos >= text.Length || text[afterPos] == ',' || text[afterPos] == '}' || text[afterPos] == ']')
                    return (UnescapeJsonString(text.Substring(start, pos - start)), pos + 1);
                if (text[afterPos] == '"' && afterPos + 3 < text.Length)
                {
                    var keyEnd = text.IndexOf('"', afterPos + 1);
                    if (keyEnd > afterPos + 1)
                    {
                        var afterKey = keyEnd + 1;
                        while (afterKey < text.Length && char.IsWhiteSpace(text[afterKey])) afterKey++;
                        if (afterKey < text.Length && text[afterKey] == ':')
                            return (UnescapeJsonString(text.Substring(start, pos - start)), pos + 1);
                    }
                }
            }
            pos++;
        }
        if (nextKeyPos > start + 1 && nextKeyPos < int.MaxValue)
        {
            var end = nextKeyPos - 1;
            while (end > start && text[end] != '"') end--;
            if (end > start && text[end] == '"')
                return (UnescapeJsonString(text.Substring(start, end - start)), end + 1);
        }
        return null;
    }

    public static string UnescapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            i++;
            if (i >= s.Length) { sb.Append('\\'); break; }
            switch (s[i])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }

    public static List<string> ExtractAllJsonObjects(string raw)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return results;
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) cleaned = m.Groups[1].Value.Trim();
            else
            {
                cleaned = cleaned.TrimStart('`');
                var firstNl = cleaned.IndexOf('\n');
                if (firstNl >= 0) cleaned = cleaned[(firstNl + 1)..];
                if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            }
        }
        var searchFrom = 0;
        while (true)
        {
            var fb = cleaned.IndexOf('{', searchFrom);
            if (fb < 0) break;
            var depth = 0;
            var inString = false;
            var escape = false;
            var end = -1;
            for (var i = fb; i < cleaned.Length; i++)
            {
                var c = cleaned[i];
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = !inString;
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            if (end < 0) break;
            results.Add(cleaned.Substring(fb, end - fb + 1));
            searchFrom = end + 1;
        }
        return results;
    }

    public static string ExtractFirstJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) cleaned = m.Groups[1].Value.Trim();
            else
            {
                cleaned = cleaned.TrimStart('`');
                var firstNl = cleaned.IndexOf('\n');
                if (firstNl >= 0) cleaned = cleaned[(firstNl + 1)..];
                if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            }
        }
        var fb = cleaned.IndexOf('{');
        if (fb < 0) return "{}";
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = fb; i < cleaned.Length; i++)
        {
            var c = cleaned[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') inString = !inString;
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return cleaned.Substring(fb, i - fb + 1);
            }
        }
        return cleaned.Substring(fb);
    }

    public static string ExtractJsonObjectWithKeys(string raw, HashSet<string> requiredKeys)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) cleaned = m.Groups[1].Value.Trim();
            else
            {
                cleaned = cleaned.TrimStart('`');
                var firstNl = cleaned.IndexOf('\n');
                if (firstNl >= 0) cleaned = cleaned[(firstNl + 1)..];
                if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            }
        }
        var searchFrom = 0;
        while (true)
        {
            var fb = cleaned.IndexOf('{', searchFrom);
            if (fb < 0) return "{}";
            var depth = 0;
            var inString = false;
            var escape = false;
            var end = -1;
            for (var i = fb; i < cleaned.Length; i++)
            {
                var c = cleaned[i];
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = !inString;
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            if (end < 0) return "{}";
            var candidate = cleaned.Substring(fb, end - fb + 1);
            try
            {
                using var doc = JsonDocument.Parse(candidate, new JsonDocumentOptions { AllowTrailingCommas = true });
                foreach (var key in requiredKeys)
                {
                    if (doc.RootElement.TryGetProperty(key, out _)) return candidate;
                }
            }
            catch { }
            searchFrom = end + 1;
        }
    }

    public static string RepairJsonNewlines(string json)
    {
        var sb = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (escaped) { sb.Append(c); escaped = false; continue; }
            if (c == '\\' && inString) { sb.Append(c); escaped = true; continue; }
            if (c == '"' && !escaped)
            {
                if (!inString) { inString = true; sb.Append(c); continue; }
                var lookahead = json.Length > i + 1 ? json[i + 1] : '\0';
                if (lookahead == ',' || lookahead == ']' || lookahead == '}' ||
                    lookahead == ':' || lookahead == '\t' ||
                    lookahead == '\n' || lookahead == '\r' || lookahead == ' ')
                {
                    inString = false; sb.Append(c);
                }
                else
                {
                    sb.Append("\\\"");
                }
                continue;
            }
            if (inString && (c == '\n' || c == '\r'))
            {
                sb.Append("\\n");
                if (c == '\r' && i + 1 < json.Length && json[i + 1] == '\n') i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static List<string> ExtractQuotedStrings(string raw)
    {
        var result = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim().TrimEnd(',');
            if (trimmed.Length < 2 || !trimmed.StartsWith("\"")) continue;
            var lastQuote = trimmed.LastIndexOf('"');
            if (lastQuote <= 0) continue;
            result.Add(trimmed.Substring(1, lastQuote - 1).Replace("\\\"", "\""));
        }
        return result;
    }
}
