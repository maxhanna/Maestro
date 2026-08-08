using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Weaver.Services;
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
using Weaver;
namespace Weaver.Controllers;

partial class AgentController
{
    private static List<string> ExtractTopLevelCssSelectors(string css)
    {
        var selectors = new List<string>();
        if (string.IsNullOrWhiteSpace(css)) return selectors;
        var i = 0;
        var depth = 0;
        var selectorStart = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var endPos = end >= 0 ? end + 2 : css.Length;
                if (depth == 0) selectorStart = endPos;
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
            if (c == '{' && depth == 0)
            {
                var selector = css[selectorStart..i].Trim();
                if (!string.IsNullOrWhiteSpace(selector))
                    selectors.Add(selector);
                var bodyDepth = 1;
                var j = i + 1;
                while (j < css.Length && bodyDepth > 0)
                {
                    if (css[j] == '{') bodyDepth++;
                    else if (css[j] == '}') bodyDepth--;
                    j++;
                }
                i = j;
                selectorStart = i;
                continue;
            }
            if (c == '@' && depth == 0)
            {
                var j = i;
                while (j < css.Length && css[j] != '{' && css[j] != ';') j++;
                if (j < css.Length && css[j] == ';')
                {
                    i = j + 1;
                    selectorStart = i;
                    continue;
                }
                var blockDepth = 1;
                var k = j + 1;
                while (k < css.Length && blockDepth > 0)
                {
                    if (css[k] == '{') blockDepth++;
                    else if (css[k] == '}') blockDepth--;
                    k++;
                }
                i = k;
                selectorStart = i;
                continue;
            }
            i++;
        }
        return selectors;
    }
    private static (string content, List<string> warnings) MergeDuplicateCssRules(string css)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(css)) return (css, warnings);
        var rules = new List<CssRule>();
        var i = 0;
        var depth = 0;
        var selectorStart = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var endPos = end >= 0 ? end + 2 : css.Length;
                if (depth == 0) selectorStart = endPos;
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
            if (c == '{' && depth == 0)
            {
                var selector = css[selectorStart..i].Trim();
                var bodyStart = i + 1;
                var bodyDepth = 1;
                var j = bodyStart;
                while (j < css.Length && bodyDepth > 0)
                {
                    if (css[j] == '{') bodyDepth++;
                    else if (css[j] == '}') bodyDepth--;
                    if (bodyDepth > 0) j++;
                }
                var body = css[bodyStart..j];
                rules.Add(new CssRule
                {
                    Selector = selector,
                    Body = body,
                    Start = selectorStart,
                    End = j + 1
                });
                i = j + 1;
                selectorStart = i;
                continue;
            }
            if (c == '@' && depth == 0)
            {
                var j = i;
                while (j < css.Length && css[j] != '{' && css[j] != ';') j++;
                if (j < css.Length && css[j] == ';')
                {
                    i = j + 1;
                    selectorStart = i;
                    continue;
                }
                var blockDepth = 1;
                var k = j + 1;
                while (k < css.Length && blockDepth > 0)
                {
                    if (css[k] == '{') blockDepth++;
                    else if (css[k] == '}') blockDepth--;
                    if (blockDepth > 0) k++;
                }
                rules.Add(new CssRule
                {
                    Selector = css[selectorStart..(k + 1)],
                    Body = "",
                    Start = selectorStart,
                    End = k + 1,
                    IsAtRuleBlock = true
                });
                i = k + 1;
                selectorStart = i;
                continue;
            }
            i++;
        }
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<(int firstIdx, int dupIdx)>();
        for (var idx = 0; idx < rules.Count; idx++)
        {
            var rule = rules[idx];
            if (rule.IsAtRuleBlock) continue;
            var norm = Regex.Replace(
                Regex.Replace(rule.Selector.ToLowerInvariant(), @"\s+", " ").Trim(),
                @"\s*,\s*", ",").Trim();
            if (seen.TryGetValue(norm, out var firstIdx))
            {
                duplicates.Add((firstIdx, idx));
                var lineApprox = css[..rule.Start].Count(ch => ch == '\n') + 1;
                warnings.Add($"Duplicate CSS selector '{rule.Selector}' — merging into first occurrence (line ~{lineApprox})");
            }
            else
            {
                seen[norm] = idx;
            }
        }
        if (duplicates.Count == 0) return (css, warnings);
        var merges = new Dictionary<int, List<int>>();
        foreach (var (firstIdx, dupIdx) in duplicates)
        {
            if (!merges.ContainsKey(firstIdx)) merges[firstIdx] = new List<int>();
            merges[firstIdx].Add(dupIdx);
        }
        var skipIndices = new HashSet<int>();
        foreach (var kvp in merges)
            foreach (var di in kvp.Value)
                skipIndices.Add(di);
        var result = new StringBuilder(css.Length);
        var lastEnd = 0;
        for (var idx = 0; idx < rules.Count; idx++)
        {
            var rule = rules[idx];
            if (skipIndices.Contains(idx))
            {
                lastEnd = rule.End;
                continue;
            }
            result.Append(css[lastEnd..rule.Start]);
            if (merges.TryGetValue(idx, out var dupIndices))
            {
                var propMap = new Dictionary<string, (string value, string indent)>(StringComparer.OrdinalIgnoreCase);
                var propOrder = new List<string>();
                foreach (var (prop, value, indent) in ParseCssProperties(rule.Body))
                {
                    if (!propMap.ContainsKey(prop)) propOrder.Add(prop);
                    propMap[prop] = (value, indent);
                }
                foreach (var dupIdx in dupIndices)
                {
                    foreach (var (prop, value, indent) in ParseCssProperties(rules[dupIdx].Body))
                    {
                        if (!propMap.ContainsKey(prop)) propOrder.Add(prop);
                        propMap[prop] = (value, indent.Length > 0 ? indent : "  ");
                    }
                }
                var bodySb = new StringBuilder();
                foreach (var prop in propOrder)
                {
                    var (value, indent) = propMap[prop];
                    bodySb.Append(indent);
                    bodySb.Append(prop);
                    bodySb.Append(": ");
                    bodySb.Append(value);
                    bodySb.Append(";\n");
                }
                if (bodySb.Length > 0 && bodySb[bodySb.Length - 1] == '\n')
                    bodySb.Length--;
                result.Append(rule.Selector);
                result.Append(" {\n");
                result.Append(bodySb);
                result.Append("\n}");
            }
            else
            {
                result.Append(css[rule.Start..rule.End]);
            }
            lastEnd = rule.End;
        }
        result.Append(css[lastEnd..]);
        return (result.ToString(), warnings);
    }
    private static List<(string prop, string value, string indent)> ParseCssProperties(string body)
    {
        var props = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(body)) return props;
        foreach (var line in body.Split('\n'))
        {
            var stripped = line.Trim();
            if (string.IsNullOrWhiteSpace(stripped)) continue;
            if (stripped.StartsWith("/*") || stripped.StartsWith("//")) continue;
            if (!stripped.EndsWith(';')) continue;
            var colonIdx = IndexOfFirstColonOutsideParensCss(stripped);
            if (colonIdx <= 0) continue;
            var prop = stripped[..colonIdx].Trim();
            var value = stripped[(colonIdx + 1)..].TrimEnd(';').Trim();
            var indent = LeadingWhitespaceCss(line);
            props.Add((prop, value, indent));
        }
        return props;
    }
    private sealed class CssRule
    {
        public string Selector { get; set; } = "";
        public string Body { get; set; } = "";
        public int Start { get; set; }
        public int End { get; set; }
        public bool IsAtRuleBlock { get; set; }
    }
    private async Task PersistBoardDataPlanAsync(string? cardId, List<PlanStep> planSteps, bool emitSse, CancellationToken ct,
        string summary = "", int score = 0, bool append = false)
    {
        if (string.IsNullOrWhiteSpace(cardId) || planSteps == null || planSteps.Count == 0)
            return;
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;
            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;
                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;
                    var existingItems = cardObj["_plan"]?.AsObject()?["items"] as JsonArray;
                    var doneLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (existingItems != null)
                    {
                        foreach (var existing in existingItems)
                        {
                            if (existing is JsonObject eo &&
                                eo["done"]?.GetValue<bool>() == true &&
                                eo["file"]?.GetValue<string>() is string ef &&
                                eo["change"]?.GetValue<string>() is string ec)
                            {
                                doneLookup.Add(ef + "|" + ec);
                            }
                        }
                    }
                    var planItems = new JsonArray();
                    if (append && existingItems != null)
                    {
                        foreach (var existing in existingItems)
                        {
                            if (existing is JsonObject eo)
                            {
                                eo["done"] = true;
                                planItems.Add(eo.DeepClone());
                            }
                        }
                    }
                    for (var i = 0; i < planSteps.Count; i++)
                    {
                        var s = planSteps[i];
                        var wasDone = doneLookup.Contains((s.File ?? "") + "|" + (s.Change ?? ""));
                        planItems.Add(new JsonObject
                        {
                            ["index"] = planItems.Count,
                            ["file"] = s.File,
                            ["change"] = s.Change,
                            ["priority"] = s.Priority,
                            ["line"] = s.LineNumber,
                            ["metaGroup"] = s.MetaGroup,
                            ["done"] = wasDone
                        });
                    }
                    cardObj["_plan"] = new JsonObject
                    {
                        ["items"] = planItems,
                        ["summary"] = summary,
                        ["score"] = score
                    };
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "refresh", new
                        {
                            target = "boarddata",
                            reason = "plan-updated",
                            cardId
                        }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist full plan to boarddata", new { cardId, error = ex.Message });
        }
    }
    private async Task PersistCohesionToCardAsync(string? cardId, string relPath, List<string> issues, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || issues == null)
            return;
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;
            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;
                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;
                    cardObj["_cohesion"] = new JsonObject
                    {
                        ["file"] = relPath,
                        ["issues"] = new JsonArray(issues.Select(i => JsonValue.Create(i)).ToArray())
                    };
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "cohesion", new
                        {
                            file = relPath,
                            issues
                        }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist cohesion check to boarddata", new { cardId, error = ex.Message });
        }
    }
    private async Task AttachFilesToCardAsync(string? cardId, List<string> filePaths, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || filePaths == null || filePaths.Count == 0)
            return;
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;
            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;
                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;
                    var attached = cardObj["attached"] as JsonArray ?? new JsonArray();
                    foreach (var fp in filePaths)
                        if (!attached.Any(a => a?.GetValue<string>() == fp))
                            attached.Add(JsonValue.Create(fp));
                    cardObj["attached"] = attached;
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                        await SendSse(Response, "refresh", new { target = "boarddata", reason = "files-attached", cardId }, ct);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to attach files to card", new { cardId, error = ex.Message });
        }
    }
    private async Task<int> HandleMethodSignatureChange(
        string fullPath, string relPath,
        string oldStr, string newStr,
        string projectRoot, bool emitSse, CancellationToken ct,
        int stepIndex, List<object> allResults, string? cardId)
    {
        var oldMatch = MethodDeclRegex.Match(oldStr);
        var newMatch = MethodDeclRegex.Match(newStr);
        if (!oldMatch.Success || !newMatch.Success)
            return stepIndex;
        var oldMethodName = oldMatch.Groups[1].Value;
        var newMethodName = newMatch.Groups[1].Value;
        if (!string.Equals(oldMethodName, newMethodName, StringComparison.Ordinal))
            return stepIndex;
        var oldParams = oldMatch.Groups[2].Value;
        var newParams = newMatch.Groups[2].Value;
        if (string.Equals(oldParams, newParams, StringComparison.Ordinal))
            return stepIndex;
        await EmitLog(emitSse, "info",
            $"Method signature change detected: {oldMethodName}({oldParams}) → {newMethodName}({newParams}). Searching for call sites...", ct: ct);
        var csFiles = new List<string>();
        try
        {
            if (Directory.Exists(projectRoot))
            {
                csFiles = Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\node_modules\\")
                             && !f.Contains("\\dist\\") && !f.Contains("\\.git\\"))
                    .OrderBy(f => f.Length)
                    .ToList();
            }
        }
        catch { return stepIndex; }
        if (csFiles.Count == 0)
        {
            await EmitLog(emitSse, "info", "No .cs files found in project to search for call sites.", ct: ct);
            return stepIndex;
        }
        var methodNameLower = oldMethodName.ToLowerInvariant();
        var candidateFiles = new List<string>();
        foreach (var f in csFiles)
        {
            if (string.Equals(f, fullPath, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var sr = new StreamReader(f, Encoding.UTF8);
                var firstFewKb = new char[4096];
                var read = await sr.ReadAsync(firstFewKb, 0, firstFewKb.Length);
                var head = new string(firstFewKb, 0, read);
                if (head.Contains(methodNameLower, StringComparison.OrdinalIgnoreCase))
                    candidateFiles.Add(f);
            }
            catch { }
        }
        if (candidateFiles.Count == 0)
        {
            await EmitLog(emitSse, "info", "No call site files found.", ct: ct);
            return stepIndex;
        }
        await EmitLog(emitSse, "info",
            $"Found {candidateFiles.Count} file(s) containing '{oldMethodName}' — checking for call sites...", ct: ct);
        foreach (var candidateFile in candidateFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fileContent = await System.IO.File.ReadAllTextAsync(candidateFile, Encoding.UTF8, ct);
            var candidateRelPath = Path.GetRelativePath(projectRoot, candidateFile).Replace('\\', '/');
            var callSitePrompt = $@"File: {candidateRelPath}
METHOD SIGNATURE CHANGED:
Old: `{oldMethodName}({oldParams})`
New: `{newMethodName}({newParams})`
The file above contains one or more calls to `{oldMethodName}` that may need updating because the method's signature changed.
Search through the ENTIRE file content below and find EVERY occurrence of `{oldMethodName}(`. For each call site found:
1. Determine the correct new call based on the new signature
2. Output the edits needed
FILE CONTENT:
```csharp
{fileContent}
```
For each call site that needs updating, output a JSON array:
[
  {{""oldString"": ""exact text of the old call"", ""newString"": ""exact text of the updated call""}}
]
If no call sites need updating, output an empty array [].
Reply ONLY with the JSON array — no explanation, no markdown.";
            var (callSitesJson, _, _) = await CallLlmRaw(
                "You are a code refactoring assistant. Update method call sites to match a changed signature. Output only JSON.",
                callSitePrompt, ct, _infiniteTimeout, maxTokens: 4096);
            if (string.IsNullOrWhiteSpace(callSitesJson))
                continue;
            var cleanJson = callSitesJson.Trim();
            if (cleanJson.StartsWith("```"))
            {
                var m = Regex.Match(cleanJson, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleanJson = m.Groups[1].Value.Trim();
            }
            List<Dictionary<string, string>>? callSiteEdits = null;
            try { callSiteEdits = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(cleanJson); }
            catch { }
            if (callSiteEdits == null || callSiteEdits.Count == 0)
                continue;
            await EmitLog(emitSse, "info",
                $"  {candidateRelPath}: {callSiteEdits.Count} call site edit(s) suggested", ct: ct);
            var fileContentMut = fileContent;
            var appliedCount = 0;
            foreach (var edit in callSiteEdits)
            {
                if (!edit.TryGetValue("oldString", out var callOld) || string.IsNullOrWhiteSpace(callOld))
                    continue;
                if (!edit.TryGetValue("newString", out var callNew))
                    callNew = "";
                var (replaced, newContent, _, _) = TryReplaceSafe(fileContentMut, callOld, callNew);
                if (replaced)
                {
                    fileContentMut = newContent;
                    appliedCount++;
                    stepIndex++;
                    var stepResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "edit",
                        ["status"] = "modified",
                        ["path"] = candidateRelPath,
                        ["description"] = $"Updated call site: {oldMethodName} → {newMethodName}",
                        ["planItemIndex"] = -1,
                        ["parentStep"] = relPath,
                        ["methodSignature"] = $"{oldMethodName}({oldParams}) → {newMethodName}({newParams})"
                    };
                    allResults.Add(stepResult);
                    if (emitSse)
                        await SendSse(Response, "step", stepResult, ct);
                }
            }
            if (appliedCount > 0)
            {
                await System.IO.File.WriteAllTextAsync(candidateFile, fileContentMut, Encoding.UTF8, ct);
                await EmitLog(emitSse, "success",
                    $"  ✓ Updated {appliedCount} call site(s) in {candidateRelPath}", ct: ct);
            }
        }
        return stepIndex;
    }
    private async Task<IncrementalStepProposal?> ProposeNextIncrementalStepAsync(
        string originalPrompt, string discoveryContext, List<PlanStep> planSoFar,
        string? steeringContext, List<string> rejectionFeedback, bool emitSse, CancellationToken ct,
        string stepMode = "all", string? extendedReasoning = null, int? atomicStepEstimate = null)
    {
        var cfg = await LoadConfigAsync();
        var sys = BuildIncrementalStepSystemPrompt(stepMode, await FilterToolsForStepAsync(originalPrompt, cfg.enabledTools, ct), atomicStepEstimate);
        var user = BuildIncrementalStepUserPrompt(originalPrompt, discoveryContext, planSoFar, steeringContext, rejectionFeedback, extendedReasoning, atomicStepEstimate);
        var (raw, _, err) = await CallLlmRawStreaming(sys, user, emitSse, ct, requestTimeout: _infiniteTimeout, maxTokens: 4096);
        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Incremental step proposal returned empty: {err}", ct: ct);
            return null;
        }
        try
        {
            var cleaned = AgentJsonUtilities.ExtractJsonObjectWithKeys(raw, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "step", "planComplete", "exploreFile" });
            using var doc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            var complete = root.TryGetProperty("planComplete", out var pc) && pc.ValueKind == JsonValueKind.True;
            var completionReason = root.TryGetProperty("completionReason", out var cr) ? cr.GetString() : null;
            var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() : null;
            var exploreFile = root.TryGetProperty("exploreFile", out var ef) && ef.ValueKind == JsonValueKind.String
                ? ef.GetString() : null;
            if (complete)
                return new IncrementalStepProposal { PlanComplete = true, CompletionReason = completionReason, Thinking = thinking };
            if (!string.IsNullOrWhiteSpace(exploreFile))
                return new IncrementalStepProposal { PlanComplete = false, ExploreFile = exploreFile, Thinking = thinking };
            if (root.TryGetProperty("step", out var maybeExploreStep) && maybeExploreStep.ValueKind == JsonValueKind.Object)
            {
                var maybeFile = maybeExploreStep.TryGetProperty("file", out var mfEl) ? mfEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(maybeFile) &&
                    (maybeFile.StartsWith("_explore", StringComparison.OrdinalIgnoreCase)))
                {
                    var maybeChange = maybeExploreStep.TryGetProperty("change", out var mcEl) ? mcEl.GetString() : "";
                    var pathMatch = Regex.Match(maybeChange ?? "", @"[\w./\\-]+\.\w{1,5}\b");
                    var target = pathMatch.Success ? pathMatch.Value : maybeChange;
                    if (!string.IsNullOrWhiteSpace(target))
                        return new IncrementalStepProposal { PlanComplete = false, ExploreFile = target, Thinking = thinking };
                }
            }
            if (!root.TryGetProperty("step", out var stepEl) || stepEl.ValueKind != JsonValueKind.Object)
                return new IncrementalStepProposal { PlanComplete = false, Thinking = thinking };
            var file = stepEl.TryGetProperty("file", out var fEl) ? fEl.GetString() : null;
            var change = stepEl.TryGetProperty("change", out var cEl) ? cEl.GetString() : null;
            var targetSymbol = stepEl.TryGetProperty("targetSymbol", out var tsEl) && tsEl.ValueKind == JsonValueKind.String ? tsEl.GetString() : null;
            var line = stepEl.TryGetProperty("line", out var lEl) && lEl.ValueKind == JsonValueKind.Number ? lEl.GetInt32() : 0;
            static string? ReadPlannerString(JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.String)
                    return AgentTextUtilities.UnescapeString(el.GetString() ?? "");
                if (el.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (var item in el.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            lines.Add(AgentTextUtilities.UnescapeString(item.GetString() ?? ""));
                    return lines.Count > 0 ? string.Join("\n", lines) : null;
                }
                return null;
            }
            var oldString = stepEl.TryGetProperty("oldString", out var osEl) ? ReadPlannerString(osEl) : null;
            var newString = stepEl.TryGetProperty("newString", out var nsEl) ? ReadPlannerString(nsEl) : null;
            var refFiles = new List<string>();
            if (stepEl.TryGetProperty("referenceFiles", out var rfArr) && rfArr.ValueKind == JsonValueKind.Array)
                foreach (var rf in rfArr.EnumerateArray())
                    if (rf.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rf.GetString()))
                        refFiles.Add(rf.GetString()!);
            var edits = new List<EditPair>();
            if (stepEl.TryGetProperty("edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var editEl in editsEl.EnumerateArray())
                {
                    if (editEl.ValueKind != JsonValueKind.Object) continue;
                    var editOld = editEl.TryGetProperty("oldString", out var eosEl) ? ReadPlannerString(eosEl) : null;
                    var editNew = editEl.TryGetProperty("newString", out var ensEl) ? ReadPlannerString(ensEl) : null;
                    var editLine = editEl.TryGetProperty("line", out var elnEl) && elnEl.ValueKind == JsonValueKind.Number
                        ? elnEl.GetInt32()
                        : 0;
                    if (!string.IsNullOrWhiteSpace(editOld) || editNew != null)
                    {
                        edits.Add(new EditPair
                        {
                            OldString = editOld ?? "",
                            NewString = editNew ?? "",
                            LineNumber = editLine
                        });
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change))
                return new IncrementalStepProposal { PlanComplete = false, Thinking = thinking };
            var justification = root.TryGetProperty("justification", out var jEl) ? jEl.GetString() : null;
            var targetType = stepEl.TryGetProperty("targetType", out var ttEl) && ttEl.ValueKind == JsonValueKind.String ? ttEl.GetString() : null;
            var targetName = stepEl.TryGetProperty("targetName", out var tnEl) && tnEl.ValueKind == JsonValueKind.String ? tnEl.GetString() : null;
            var insertAfter = stepEl.TryGetProperty("insertAfter", out var iaEl) && iaEl.ValueKind == JsonValueKind.True;
            var newCode = new List<string>();
            if (stepEl.TryGetProperty("newCode", out var ncEl) && ncEl.ValueKind == JsonValueKind.Array)
                foreach (var ncItem in ncEl.EnumerateArray())
                    if (ncItem.ValueKind == JsonValueKind.String)
                        newCode.Add(AgentTextUtilities.UnescapeString(ncItem.GetString() ?? ""));
            var fullFile = stepEl.TryGetProperty("fullFile", out var ffEl) ? ReadPlannerString(ffEl) : null;
            var primaryStep = ParseStepFromJson(file, change, targetSymbol, line, oldString, newString, refFiles, edits,
                targetType, targetName, insertAfter, newCode.Count > 0 ? newCode : null, fullFile);
            var additionalSteps = new List<PlanStep>();
            var allObjects = AgentJsonUtilities.ExtractAllJsonObjects(raw);
            var foundPrimary = false;
            foreach (var objStr in allObjects)
            {
                if (!foundPrimary)
                {
                    if (string.Equals(objStr, cleaned, StringComparison.Ordinal)) { foundPrimary = true; }
                    continue;
                }
                try
                {
                    using var extraDoc = JsonDocument.Parse(objStr, new JsonDocumentOptions { AllowTrailingCommas = true });
                    if (!extraDoc.RootElement.TryGetProperty("step", out var extraStepEl) || extraStepEl.ValueKind != JsonValueKind.Object) continue;
                    var extraFile = extraStepEl.TryGetProperty("file", out var efEl) ? efEl.GetString() : null;
                    var extraChange = extraStepEl.TryGetProperty("change", out var ecEl) ? ecEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(extraFile) || string.IsNullOrWhiteSpace(extraChange)) continue;
                    var extraTargetSymbol = extraStepEl.TryGetProperty("targetSymbol", out var etsEl) && etsEl.ValueKind == JsonValueKind.String ? etsEl.GetString() : null;
                    var extraLine = extraStepEl.TryGetProperty("line", out var elEl) && elEl.ValueKind == JsonValueKind.Number ? elEl.GetInt32() : 0;
                    var extraOld = extraStepEl.TryGetProperty("oldString", out var eosEl2) ? ReadPlannerString(eosEl2) : null;
                    var extraNew = extraStepEl.TryGetProperty("newString", out var ensEl2) ? ReadPlannerString(ensEl2) : null;
                    var extraRefFiles = new List<string>();
                    if (extraStepEl.TryGetProperty("referenceFiles", out var erfArr) && erfArr.ValueKind == JsonValueKind.Array)
                        foreach (var rf in erfArr.EnumerateArray())
                            if (rf.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rf.GetString()))
                                extraRefFiles.Add(rf.GetString()!);
                    var extraEdits = new List<EditPair>();
                    if (extraStepEl.TryGetProperty("edits", out var eeditsEl) && eeditsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var editEl in eeditsEl.EnumerateArray())
                        {
                            if (editEl.ValueKind != JsonValueKind.Object) continue;
                            var eo = editEl.TryGetProperty("oldString", out var eosEl3) ? ReadPlannerString(eosEl3) : null;
                            var en = editEl.TryGetProperty("newString", out var ensEl3) ? ReadPlannerString(ensEl3) : null;
                            if (!string.IsNullOrWhiteSpace(eo) || en != null)
                                extraEdits.Add(new EditPair { OldString = eo ?? "", NewString = en ?? "" });
                        }
                    }
                    var extraTargetType = extraStepEl.TryGetProperty("targetType", out var ettEl) && ettEl.ValueKind == JsonValueKind.String ? ettEl.GetString() : null;
                    var extraTargetName = extraStepEl.TryGetProperty("targetName", out var etnEl) && etnEl.ValueKind == JsonValueKind.String ? etnEl.GetString() : null;
                    var extraInsertAfter = extraStepEl.TryGetProperty("insertAfter", out var eiaEl) && eiaEl.ValueKind == JsonValueKind.True;
                    var extraNewCode = new List<string>();
                    if (extraStepEl.TryGetProperty("newCode", out var encEl) && encEl.ValueKind == JsonValueKind.Array)
                        foreach (var encItem in encEl.EnumerateArray())
                            if (encItem.ValueKind == JsonValueKind.String)
                                extraNewCode.Add(AgentTextUtilities.UnescapeString(encItem.GetString() ?? ""));
                    var extraFullFile = extraStepEl.TryGetProperty("fullFile", out var effEl) ? ReadPlannerString(effEl) : null;
                    additionalSteps.Add(ParseStepFromJson(extraFile, extraChange, extraTargetSymbol, extraLine, extraOld, extraNew, extraRefFiles, extraEdits,
                        extraTargetType, extraTargetName, extraInsertAfter, extraNewCode.Count > 0 ? extraNewCode : null, extraFullFile));
                }
                catch { }
            }
            return new IncrementalStepProposal
            {
                PlanComplete = false,
                Thinking = thinking,
                CompletionReason = justification,
                Step = primaryStep,
                AdditionalSteps = additionalSteps.Count > 0 ? additionalSteps : null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
    private static PlanStep ParseStepFromJson(string file, string change, string? targetSymbol, int line, string? oldString, string? newString, List<string> refFiles, List<EditPair> edits,
        string? targetType = null, string? targetName = null, bool? insertAfter = null, List<string>? newCode = null, string? fullFile = null)
    {
        // Trim leading/trailing whitespace so LLM-emitted file names like " _web_search \n" still
        // match IsWebStep/IsSpecialMarker and the web-step exclusion (a stray trailing newline
        // used to dodge both and get bounced by the research-verb guard as "search is not an
        // actionable edit" — the exact interleaved-loop deadlock seen in web-needing runs). Only
        // Trim, never collapse internal whitespace: a real path like "src/My  Folder/file.cs"
        // must stay byte-identical or edits silently land on the wrong file.
        var normFile = file.Trim().Replace('\\', '/');
        if (normFile.StartsWith("_edit/", StringComparison.OrdinalIgnoreCase))
            normFile = normFile["_edit/".Length..];
        // FORMAT C/D steps carry targetName+newCode (no oldString): map targetName onto
        // TargetSymbol so AST resolution extracts the exact oldString from the real file.
        var effectiveSymbol = string.IsNullOrWhiteSpace(targetSymbol) ? targetName : targetSymbol;
        // fullFile steps carry the complete file content: surface it as NewString so the
        // existing _create_file path (file missing + NewString set + OldString empty) applies it.
        var effectiveNew = newString;
        if (string.IsNullOrWhiteSpace(effectiveNew) && !string.IsNullOrWhiteSpace(fullFile))
            effectiveNew = fullFile;
        return new PlanStep
        {
            File = normFile,
            Change = change,
            TargetSymbol = effectiveSymbol,
            TargetType = targetType,
            TargetName = targetName,
            InsertAfter = insertAfter,
            NewCode = newCode != null && newCode.Count > 0 ? newCode : null,
            FullFile = fullFile,
            Priority = 1,
            LineNumber = line,
            OldString = oldString,
            NewString = effectiveNew,
            ReferenceFiles = refFiles,
            Edits = edits.Count > 0 ? edits : null
        };
    }
    private async Task<(bool valid, string? reason)> ValidateIncrementalStepAsync(
        PlanStep step, string originalPrompt, string discoveryContext, List<PlanStep> planSoFar,
        string projectRoot, bool emitSse, CancellationToken ct,
        bool skipLlm = false, string? lastStepCompletionNote = null)
    {
        if (string.IsNullOrWhiteSpace(step.File) || string.IsNullOrWhiteSpace(step.Change))
            return (false, "Step is missing file or change description.");
        // OS-filesystem tasks: _create_directory/_create_file are repo-relative and CANNOT
        // reach the Desktop — only a _command step touches the OS filesystem. Knowing the OS
        // also lets the _command rejection teach the model the real desktop path instead of
        // letting it invent Linux paths.
        var osTask = IsExternalFilesystemTask(originalPrompt);
        var osDesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var normNew = NormalizeChangeForDedup(step.Change);
        foreach (var existing in planSoFar)
        {
            if (!string.Equals(existing.File, step.File, StringComparison.OrdinalIgnoreCase)) continue;
            var normExisting = NormalizeChangeForDedup(existing.Change);
            if (normNew == normExisting || CalculateChangeSimilarity(normNew, normExisting) >= 0.82)
                return (false, $"Duplicates an already-committed step targeting {existing.File}: \"{existing.Change}\".");
        }
        // Reject _create_file steps with no actual content (hallucinated file creation)
        if (string.Equals(step.File, "_sql_migration", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(step.NewString) ||
                !step.NewString.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                return (false, "_sql_migration step must carry the CREATE TABLE IF NOT EXISTS statement in newString — " +
                                "provide the full DDL (e.g. \"CREATE TABLE IF NOT EXISTS benchmark_scores (...);\") or edit an existing file instead.");
            var tables = SqlMigrationService.ExtractCreateTableStatements(step.NewString);
            if (tables.Count == 0)
                return (false, "_sql_migration step's newString does not contain a parseable CREATE TABLE statement — " +
                                "include the complete DDL with column definitions and a trailing ';'.");
        }
        if (string.Equals(step.File, "_create_file", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(step.NewString))
                return (false, "_create_file step has no file content in newString — provide the full file content or edit an existing file instead.");
            if (step.NewString.Trim().Length < 1)
                return (false, "_create_file step content is too short (" + step.NewString.Trim().Length + " chars) — provide meaningful file content.");
            if (planSoFar.Any(s => string.Equals(s.File, "_create_file", StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(s.Change, step.Change, StringComparison.OrdinalIgnoreCase)))
                return (false, $"File '{step.Change}' was already created by a prior _create_file step — target the existing file instead.");
            var createPathMatch = Regex.Match(step.Change, @"([\w\-/\\]+\.\w{1,10})");
            if (createPathMatch.Success)
            {
                var createCandidate = createPathMatch.Groups[1].Value.Replace('\\', '/');
                var createFileName = Path.GetFileName(createCandidate);
                // Only a same-named file in the SAME directory as the target blocks creation — a
                // same-named file in a different folder (e.g. benchmark_test_4/index.html when
                // creating benchmark_test_7/index.html) is NOT a conflict.
                var existingFile = AgentDiscovery.FindSameDirectoryFile(createCandidate, projectRoot);
                if (existingFile != null)
                {
                    var createFullPath = Path.GetFullPath(Path.Combine(projectRoot, createCandidate.Replace('/', Path.DirectorySeparatorChar)));
                    if (!System.IO.File.Exists(createFullPath))
                        return (false, $"File '{createFileName}' ALREADY EXISTS at '{existingFile}' — do NOT create it. " +
                                        "Target the existing file path in a normal edit step instead.");
                }
            }
        }
        if (string.Equals(step.File, "_command", StringComparison.OrdinalIgnoreCase) &&
            !AgentProjectUtilities.LooksLikeShellCommand(step.Change))
        {
            if (osTask)
                return (false, $"_command step is not an executable shell command. You are on {(OperatingSystem.IsWindows() ? "Windows" : Environment.OSVersion)} and the Desktop is at {osDesktopPath}. A _command step's change must BE the real command with an absolute path, e.g. New-Item -ItemType Directory -Path \"{osDesktopPath}\\<name>\" -Force. Never put planning notes in a _command step.");
            return (false, "_command step is not an executable shell command. Use _command only for real terminal commands such as `dotnet test`, `npm install`, or `cd app; npx ng g c name`. Put planning notes in the thinking field, not in a command step.");
        }
        // OS tasks: _create_directory/_create_file write relative to the PROJECT ROOT — a step
        // whose change names an OS location would silently create the folder/file INSIDE the
        // project (the "/home/user/search_results" failure). Reject and steer to _command.
        if (osTask &&
            (string.Equals(step.File, "_create_directory", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(step.File, "_create_file", StringComparison.OrdinalIgnoreCase)))
        {
            var ch = (step.Change ?? "").Trim();
            var looksOsPath = ch.StartsWith("/")
                || ch.StartsWith("~")
                || ch.StartsWith("\\\\") // UNC \\server\share
                || Regex.IsMatch(ch, @"^[A-Za-z]:[\\/]") // C:\ or C:/
                || Regex.IsMatch(ch, @"\b(desktop|downloads|documents|userprofile|%userprofile%|home dir|home directory)\b", RegexOptions.IgnoreCase);
            if (looksOsPath)
                return (false, $"{step.File} writes RELATIVE TO THE PROJECT ROOT — it cannot create \"{ch}\" on the Desktop/OS filesystem. Use a _command step whose change is a real command with an absolute path, e.g. New-Item -ItemType Directory -Path \"{osDesktopPath}\\<name>\" -Force (Desktop is at {osDesktopPath}).");
        }
        if (string.Equals(step.File, "_show", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(step.File, "_display", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"{step.File} is not an actionable planning/edit step. If more context is needed, use _explore for a specific file; otherwise propose the concrete edit step.");
        }
        // Reject after too many edits to the same file+symbol (hallucination loop detection)
        var sameTargetCount = planSoFar.Count(s =>
            !string.IsNullOrWhiteSpace(s.TargetSymbol) &&
            string.Equals(s.TargetSymbol, step.TargetSymbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.File, step.File, StringComparison.OrdinalIgnoreCase));
        if (sameTargetCount >= 3)
            return (false, $"Already committed {sameTargetCount} steps targeting '{step.TargetSymbol}' in {step.File}. Further edits to the same symbol suggest a hallucination loop — the task is likely complete.");
        var changeLower = step.Change.ToLowerInvariant();
        var rejectedActions = new[] { "move ", "reorder ", "restructure ", "refactor " };
        if (rejectedActions.Any(v => changeLower.StartsWith(v)))
        {
            return (false, $"Step rejected — '{changeLower.Split(' ')[0]}' is a structural change that should be decided by the user, not auto-planned. " +
                            "If the task is functionally complete, return planComplete=true.");
        }
        var researchVerbs = new[] { "locate", "find", "examine", "understand", "read", "explore", "look at", "inspect", "review", "check", "see", "search" };
        // _web_search/_web_fetch are ACTIONABLE research markers — their change field IS the
        // query/URL. The web-need gate decides whether web steps are allowed, not this guard;
        // rejecting them here deadlocks web-needing tasks (the model proposes the right tool
        // and the loop bounces it, exactly like the "Search the web…" run).
        if (!step.File.Equals("_discover", StringComparison.OrdinalIgnoreCase) &&
            !IsWebStep(step.File) &&
            researchVerbs.Any(v => changeLower.StartsWith(v)))
        {
            return (false, $"Research step rejected — '{changeLower.Split(' ')[0]}' is not an actionable edit. " +
                            "All steps must make actual code changes (add/modify/delete/replace). " +
                            "The file content is already available in the discovery context.");
        }
        if (changeLower.StartsWith("remove") || changeLower.StartsWith("delete"))
        {
            var targetMatch = Regex.Match(step.Change, @"remove\s+(?:the\s+)?(\w+)", RegexOptions.IgnoreCase);
            if (targetMatch.Success)
            {
                var target = targetMatch.Groups[1].Value;
                var contradicts = planSoFar.Any(p =>
                    string.Equals(p.File, step.File, StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(p.Change ?? "", $@"\b(add|create|insert)\b.*\b{Regex.Escape(target)}\b", RegexOptions.IgnoreCase));
                if (contradicts)
                    return (false, $"Removes '{target}' but an earlier committed step just added it — contradicts the plan so far.");
            }
        }
        var isSpecial = AgentProjectUtilities.IsSpecialMarker(step.File);
        // TARGETED-ANCHOR GUARD: an edit step whose oldString reproduces a whole block
        // (RULE 17 says 1-3 lines) is unreliable — the LLM drifts when re-outputting a big
        // verbatim block, the plan edit then fails to match at apply time, and the resolver
        // falls back to FORMAT C/D which again forces reproducing the same wall of text
        // (the "group benchmarks" run: a 30-line oldString/newString pair that never
        // matched, then FORMAT D demanded the full section again). Reject oversized anchors
        // deterministically with the targeted-replace pattern so the planner emits a small
        // unique anchor instead. Thresholds mirror GetPlanSizeViolations.
        if (!isSpecial && !string.IsNullOrWhiteSpace(step.OldString))
        {
            var oldAnchorLines = step.OldString.Split('\n').Length;
            var oldAnchorChars = step.OldString.Length;
            if (oldAnchorLines > 10 || oldAnchorChars > 400)
            {
                return (false,
                    $"oldString is {oldAnchorLines} lines/{oldAnchorChars} chars — WAY too large for a reliable targeted edit. " +
                    "Use the TARGETED REPLACE pattern: oldString = the SINGLE most unique line in the target region " +
                    "(1-3 lines MAX, e.g. `<div *ngFor=\"let b of benchmarks\" class=\"benchmark-item\">`), copied verbatim; " +
                    "newString = that same line UNCHANGED followed by your new lines. Do NOT reproduce the whole enclosing " +
                    "block/section in oldString. If you are REPLACING an ENTIRE method, use FORMAT C " +
                    "(targetType/targetName/newCode) instead of oldString/newString.");
            }
        }
        if (!isSpecial && AgentProjectUtilities.IsRelativePath(step.File))
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, step.File.Replace('/', Path.DirectorySeparatorChar)));
            var isModifyVerb = Regex.IsMatch(changeLower, @"^\s*(modify|update|change|replace|fix|add|insert|append|prepend)\b");
            var fileExists = System.IO.File.Exists(fullPath);
            var willBeCreatedEarlier = planSoFar.Any(p =>
                (p.File.Equals("_create_file", StringComparison.OrdinalIgnoreCase) ||
                 p.File.Equals("_command", StringComparison.OrdinalIgnoreCase)) &&
                (p.Change ?? "").Contains(Path.GetFileName(step.File), StringComparison.OrdinalIgnoreCase));
            if (!fileExists)
            {
                var similarExisting = AgentDiscovery.FindSameDirectoryFile(step.File, projectRoot);
                if (similarExisting != null)
                    return (false, $"Path '{step.File}' does not exist. A file with the same name ALREADY EXISTS at '{similarExisting}' — retarget this step to that path (do not create a duplicate).");
                if (!string.IsNullOrWhiteSpace(step.NewString) && string.IsNullOrWhiteSpace(step.OldString))
                {
                    var origPath = step.File;
                    step.File = "_create_file";
                    step.Change = origPath;
                    return (true, null);
                }
            }
            if (fileExists)
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                var (verdict, reason) = PreEditValidation(content, step);
                if (verdict == PreEditVerdict.AlreadyDone)
                {
                    // HALLUCINATED-REMOVAL GUARD: when NO prior step has touched this file,
                    // "code to be removed is already absent from file" cannot mean "already done"
                    // — it means the planner invented a symbol that is NOT in the file (e.g. it
                    // decided a method was 'broken' and tried to delete it although no such method
                    // exists). Reject with corrective feedback that names the file's actual
                    // members, so the next proposal re-grounds in the real content instead of
                    // retrying the same fiction (which previously degraded into a loop of
                    // hallucinated edits). If an earlier step DID touch this file, a removal can
                    // legitimately be a no-op and is left to the resolver. Never intercept
                    // special markers (_command etc.).
                    if (reason.Contains(RemovalTargetAbsentReason, StringComparison.OrdinalIgnoreCase) &&
                        !AgentProjectUtilities.IsSpecialMarker(step.File) &&
                        !planSoFar.Any(p =>
                            !AgentProjectUtilities.IsSpecialMarker(p.File) &&
                            string.Equals(p.File.Replace('\\', '/'), step.File.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    {
                        var inventory = ExtractMemberInventory(content);
                        return (false,
                            $"The removal target in '{step.File}' does NOT exist in the file" +
                            (inventory.Length > 0 ? $" — its members are: {inventory}" : "") +
                            ". Re-read the ATTACHED FILES / DISCOVERY CONTEXT above and propose a step grounded " +
                            "in those exact members. NEVER delete or reference a symbol that is not present in the file. " +
                            "If the task's work is already complete in the file, return planComplete=true instead of proposing further edits.");
                    }
                    // FROZEN-PLAN RULE: once the planner has produced a concrete edit
                    // (oldString/newString) for this step, the plan is frozen — a heuristic
                    // AlreadyDone verdict (often a false positive from oldString drift, e.g.
                    // "code to be removed is already absent") must NOT bounce the step back
                    // into LLM re-planning/re-thinking. Route it to the resolver, which
                    // re-attempts the actual edit with tolerant matchers. If the edit
                    // genuinely cannot apply, the resolver reports a no-op and the pipeline
                    // continues normally (crap edit → ignored).
                    if (HasConcreteEdit(step))
                    {
                        await EmitLog(emitSse, "bypass",
                            $"⚡ Frozen plan — [{step.File}] {step.Change} carries a concrete edit; overriding heuristic '{reason}' and re-attempting the edit via the resolver (no re-planning)", ct: ct);
                    }
                    else
                    {
                        return (false, $"Already satisfied in the current file — {reason}. Move on to the next requirement.");
                    }
                }
            }
        }
        if (isSpecial) return (true, null);
        if (skipLlm)
        {
            await EmitLog(emitSse, "info", $"LLM validator skipped (retry mode) — accepting step: [{step.File}] {step.Change}", ct: ct);
            return (true, null);
        }
        var sb = new StringBuilder();
        sb.AppendLine("### ORIGINAL TASK ###");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("### PLAN SO FAR (already committed, in order) ###");
        if (planSoFar.Count == 0) sb.AppendLine("(none yet — this would be the first step)");
        else for (var i = 0; i < planSoFar.Count; i++) sb.AppendLine($"  Step {i + 1}: [{planSoFar[i].File}] {planSoFar[i].Change}");
        sb.AppendLine();
        sb.AppendLine("### PROPOSED NEXT STEP ###");
        sb.AppendLine($"[{step.File}] {step.Change}");
        sb.AppendLine();
        sb.AppendLine("### RELEVANT DISCOVERY CONTEXT (target file only) ###");
        var validatorDiscovery = StripEditKnowledgeHeader(discoveryContext);
        var fileSection = AgentDiscovery.ExtractFileSectionFromContext(validatorDiscovery, step.File);
        sb.AppendLine(string.IsNullOrWhiteSpace(fileSection) ? validatorDiscovery : fileSection);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(lastStepCompletionNote))
        {
            sb.AppendLine("### PREVIOUS STEP COMPLETED ###");
            sb.AppendLine(lastStepCompletionNote);
            sb.AppendLine();
        }
        sb.AppendLine("Judge the PROPOSED NEXT STEP ONLY. Answer:");
        sb.AppendLine("1. Does it reference any method/property/symbol that does NOT exist in the discovery context " +
                      "AND is NOT introduced by an earlier committed step? (if so: invalid)");
        sb.AppendLine("2. Does it contradict or redo anything already committed? (if so: invalid)");
        sb.AppendLine("3. Does it require a prerequisite step not yet committed (e.g. an endpoint before its DTO)? (if so: invalid)");
        sb.AppendLine("4. Is it a genuinely necessary, atomic step toward the ORIGINAL TASK (not scope creep)? (if not: invalid)");
        sb.AppendLine("5. If a previous step was marked complete (needsExtraStep=false), does this step address a GENUINELY DIFFERENT " +
                      "requirement — not a continuation, cleanup, or refinement of already-completed work? (if not: invalid)");
        sb.AppendLine();
        sb.AppendLine("Output ONLY JSON: {\"valid\": true|false, \"reason\": \"short reason, only if invalid\"}");
        var (raw, _, err) = await CallLlmRaw(
            "You are a strict plan-coherence validator. Output ONLY the requested JSON.",
            sb.ToString(), ct, _infiniteTimeout, maxTokens: 200);
        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Coherence validator call failed ({err}) — accepting step by default.", ct: ct);
            return (true, null);
        }
        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var valid = !doc.RootElement.TryGetProperty("valid", out var v) || v.ValueKind != JsonValueKind.False;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return (valid, valid ? null : (reason ?? "Rejected by coherence validator."));
        }
        catch
        {
            return (true, null);
        }
    }
    private async Task<(AgentPlan plan, string discoveryContext)> RunIncrementalPlanningLoop(
        string prompt, string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct, string? steeringContext, string? cardId = null,
        int? atomicStepEstimate = null)
    {
        var planSoFar = new List<PlanStep>();
        var rejectionFeedback = new List<string>();
        var exploredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var thinkingLog = new StringBuilder();
        var regenAttempts = 0;
        var consecutiveSlotFailures = 0;
        var totalPlanningRounds = 0;
        var stepEventIndex = 0;
        var webNeedVerified = 0; // 0 = unchecked, 1 = task needs web, -1 = no web needed
        var webNeedVerifyFailures = 0; // consecutive failed classifier calls — caps re-confirmation at 2 per run
        string? webInjectedQuery = null; // search query from the same verification call, used if we auto-inject _web_search
        // OS-filesystem task guard state: pure OS tasks reject repo file edits
        // deterministically; hint-y tasks get one memoized LLM verdict per file.
        var osTaskGuard = IsExternalFilesystemTask(prompt);
        var osTaskPure = osTaskGuard && !OsPromptHintsRepoWork(prompt);
        var osRepoEditVerdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await EmitLog(emitSse, "info", "Incremental planning: proposing steps one at a time…", ct: ct);
        if (emitSse)
            await SendSse(Response, "plan", new
            {
                thinking = "",
                summary = "Building plan incrementally — 0 steps so far",
                items = Array.Empty<PlanStep>(),
                incremental = true
            }, ct);
        for (var turn = 0; turn < MAX_INCREMENTAL_STEPS; turn++)
        {
            ct.ThrowIfCancellationRequested();
            if (emitSse)
                await SendSse(Response, "phase", new { message = $"Planning — step {planSoFar.Count + 1}/{MAX_INCREMENTAL_STEPS}" }, ct);
            var proposal = await ProposeNextIncrementalStepAsync(
                prompt, discoveryContext, planSoFar, steeringContext, rejectionFeedback, emitSse, ct,
                atomicStepEstimate: atomicStepEstimate);
            if (proposal == null)
            {
                var jsonFb = "Your previous response could not be parsed as valid JSON. " +
                    "Output ONLY the JSON object described in the system prompt.";
                await EmitRejectedLog(emitSse, "Incremental planning: rejected — response was not valid JSON; retrying with parse feedback", jsonFb, ct);
                rejectionFeedback.Add(jsonFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            if (proposal.PlanComplete)
            {
                // Web-need gate (mirror of the interleaved loop): declaring the plan
                // complete is itself a refusal to use the web tools when the task
                // needs current external info and no web step exists. Reject, and on
                // the regen cap inject the search so web-needing tasks cannot escape
                // by claiming completion.
                if (TaskHintsWebNeed(prompt) && !planSoFar.Any(s => IsWebStep(s.File)))
                {
                    if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                    {
                        await EmitLog(emitSse, "info",
                            "Task hints at needing current external info — verifying with the LLM whether _web_search is required…", ct: ct);
                        var (needsWeb, query, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                        if (verified)
                        {
                            webNeedVerified = needsWeb ? 1 : -1;
                            if (needsWeb) webInjectedQuery = query;
                        }
                        else
                        {
                            webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then stop calling
                        }
                    }
                    if (webNeedVerified == 1)
                    {
                        await EmitRejectedLog(emitSse,
                            "Incremental planning: rejected plan-complete — task needs current external info but the plan has no _web_search step",
                            WebNeedFeedback, ct);
                        rejectionFeedback.Add(WebNeedFeedback);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                        {
                            var queryText = string.IsNullOrWhiteSpace(webInjectedQuery)
                                ? BuildFallbackWebQuery(prompt)
                                : webInjectedQuery.Trim();
                            planSoFar.Add(new PlanStep { File = "_web_search", Change = queryText });
                            thinkingLog.AppendLine($"Step {planSoFar.Count}: [auto-injected] {queryText}");
                            if (emitSse)
                            {
                                await SendSse(Response, "thinking",
                                    new { text = $"Auto-injected _web_search step — the planner declared completion without it after {MAX_STEP_REGEN_ATTEMPTS} rejections." }, ct);
                                await SendSse(Response, "step", new
                                {
                                    index = ++stepEventIndex,
                                    type = "plan",
                                    status = "pending",
                                    path = "_web_search",
                                    description = queryText,
                                    line = 0,
                                    planItemIndex = planSoFar.Count
                                }, ct);
                            }
                            await EmitLog(emitSse, "warn",
                                $"The planner declared the plan complete without using _web_search after {MAX_STEP_REGEN_ATTEMPTS} rejections — auto-injecting a _web_search step: \"{queryText}\"", ct: ct);
                            break; // plan is done — the injected search executes with the rest
                        }
                        continue;
                    }
                }
                if (emitSse)
                    await SendSse(Response, "thinking", new { text = $"Plan complete: {proposal.CompletionReason}" }, ct);
                await EmitLog(emitSse, "success",
                    $"Incremental planning: plan complete after {planSoFar.Count} step(s) — {proposal.CompletionReason}", ct: ct);
                var budgetTxt = atomicStepEstimate is > 0
                    ? $" (estimated {atomicStepEstimate})" : "";
                await EmitLog(emitSse, "metric",
                    $"📊 Card planning: {planSoFar.Count} step(s){budgetTxt}, {totalPlanningRounds} total planning round(s)", ct: ct);
                break;
            }
            if (!string.IsNullOrWhiteSpace(proposal.ExploreFile))
            {
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = ++stepEventIndex,
                        type = "explore",
                        status = "exploring",
                        path = proposal.ExploreFile,
                        description = $"Exploring: {proposal.ExploreFile}",
                        planItemIndex = planSoFar.Count,
                        message = proposal.Thinking ?? ""
                    }, ct);
                if (exploredFiles.Add(proposal.ExploreFile))
                {
                    if (proposal.ExploreFile.Equals("_discover", StringComparison.OrdinalIgnoreCase))
                    {
                        await EmitLog(emitSse, "info", "Planner requested _discover — running project-wide search…", ct: ct);
                        discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                        regenAttempts = 0;
                        continue;
                    }
                    var isMarker = proposal.ExploreFile.StartsWith("_");
                    var alreadyInContext = false;
                    if (!isMarker)
                    {
                        var normPath = proposal.ExploreFile.Replace('\\', '/').TrimStart('/');
                        // A file present ONLY as a focused region is NOT fully in context —
                        // a later re-request may legitimately need a different symbol's region.
                        alreadyInContext = !IsFocusedSectionInContext(discoveryContext, normPath) &&
                                           (discoveryContext.Contains($"### read {normPath}") ||
                                            discoveryContext.Contains($"### {normPath}") ||
                                            Regex.IsMatch(discoveryContext, $@"### (?:read )?\S*{Regex.Escape(Path.GetFileName(normPath))}\b"));
                    }
                    if (alreadyInContext)
                    {
                        var contextMsg = $"STOP — '{proposal.ExploreFile}' is ALREADY in the DISCOVERY CONTEXT above (its full content is already shown). " +
                            "Do NOT request it again. Read the file content from the DISCOVERY CONTEXT and propose the actual edit step now.";
                        await EmitRejectedLog(emitSse,
                            $"Incremental planning: rejected explore — '{proposal.ExploreFile}' already in discovery context", contextMsg, ct);
                        rejectionFeedback.Add(contextMsg);
                        if (emitSse)
                            await SendSse(Response, "step", new
                            {
                                index = ++stepEventIndex,
                                type = "explore",
                                status = "error",
                                path = proposal.ExploreFile,
                                description = $"Already in context: {proposal.ExploreFile}",
                                error = contextMsg
                            }, ct);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                        continue;
                    }
                    await EmitLog(emitSse, "info", $"Incremental planning: exploring {proposal.ExploreFile}", ct: ct);
                    discoveryContext = await ExplorationPipeline(
                        new List<PlanStep> { new() { File = "_explore", Change = proposal.ExploreFile } },
                        discoveryContext, projectRoot, emitSse, ct, prompt);
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = ++stepEventIndex,
                            type = "explore",
                            status = "done",
                            path = proposal.ExploreFile,
                            description = $"Explored: {proposal.ExploreFile}"
                        }, ct);
                    if (!string.IsNullOrWhiteSpace(cardId))
                    {
                        await AutoAttachFileToCardAsync(cardId, proposal.ExploreFile, emitSse, ct);
                    }
                    regenAttempts = 0;
                    continue;
                }
                else
                {
                    // The earlier read may have been only a FOCUSED region (the file was
                    // never shown in full) — then re-exploring is legitimate: the model
                    // can surface a different symbol's region. Only reject when the file
                    // is genuinely shown in full already.
                    var normPath = proposal.ExploreFile.Replace('\\', '/').TrimStart('/');
                    var focusedOnly = !proposal.ExploreFile.StartsWith("_") &&
                        IsFocusedSectionInContext(discoveryContext, normPath);
                    if (!focusedOnly)
                    {
                        var exploreFb =
                            $"You asked to explore '{proposal.ExploreFile}' again — it is ALREADY shown in full in the " +
                            "DISCOVERY CONTEXT above. Do not re-request it. Read it carefully and propose the actual next " +
                            "step now, using the exact symbol/method names visible there.";
                        await EmitRejectedLog(emitSse,
                            $"Incremental planning: rejected explore — '{proposal.ExploreFile}' already shown in full; retrying", exploreFb, ct);
                        rejectionFeedback.Add(exploreFb);
                        if (emitSse)
                        {
                            await SendSse(Response, "step", new
                            {
                                index = ++stepEventIndex,
                                type = "explore",
                                status = "error",
                                path = proposal.ExploreFile,
                                description = $"Already explored: {proposal.ExploreFile}"
                            }, ct);
                        }
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) { break; }
                        continue;
                    }
                    await EmitLog(emitSse, "info",
                        $"Incremental planning: re-exploring {proposal.ExploreFile} for another focused region", ct: ct);
                    discoveryContext = await ExplorationPipeline(
                        new List<PlanStep> { new() { File = "_explore", Change = proposal.ExploreFile } },
                        discoveryContext, projectRoot, emitSse, ct, prompt);
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = ++stepEventIndex,
                            type = "explore",
                            status = "done",
                            path = proposal.ExploreFile,
                            description = $"Explored: {proposal.ExploreFile}"
                        }, ct);
                    if (!string.IsNullOrWhiteSpace(cardId))
                    {
                        await AutoAttachFileToCardAsync(cardId, proposal.ExploreFile, emitSse, ct);
                    }
                    regenAttempts = 0;
                    continue;
                }
            }
            if (proposal.Step == null)
            {
                var neitherFb = "You returned neither planComplete=true, exploreFile, nor a step — return exactly one.";
                await EmitRejectedLog(emitSse, "Incremental planning: rejected — returned neither planComplete, exploreFile, nor a step; retrying", neitherFb, ct);
                rejectionFeedback.Add(neitherFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            if (proposal.Step.File.Equals("_discover", StringComparison.OrdinalIgnoreCase))
            {
                if (exploredFiles.Add("_discover"))
                {
                    await EmitLog(emitSse, "info", "Incremental planning: _discover step — running project-wide search…", ct: ct);
                    discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                    regenAttempts = 0;
                    continue;
                }
                var discoverFb = "You already ran _discover — its results are now in the DISCOVERY CONTEXT. Use them to propose the next step.";
                await EmitRejectedLog(emitSse, "Incremental planning: rejected — _discover was already run this session; retrying", discoverFb, ct);
                rejectionFeedback.Add(discoverFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            // Web-step gate: a _web_search/_web_fetch proposal is allowed freely once the LLM
            // has confirmed the task needs the web (webNeedVerified == 1). Otherwise the first
            // web proposal triggers ONE confirmation round; if the verdict is that the task
            // does NOT need current external info, the web step is rejected with feedback
            // steering back to the repo context (no further web steps this run).
            if (IsWebStep(proposal.Step.File) && webNeedVerified != 1)
            {
                if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                {
                    await EmitLog(emitSse, "info",
                        "The planner proposed a _web_search/_web_fetch step — verifying with the LLM whether the task actually needs current external info…", ct: ct);
                    var (needsWeb, query, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                    if (verified)
                    {
                        webNeedVerified = needsWeb ? 1 : -1;
                        if (needsWeb) webInjectedQuery = query;
                    }
                    else
                    {
                        webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then fail open
                    }
                }
                if (webNeedVerified == -1)
                {
                    await EmitRejectedLog(emitSse,
                        $"Incremental planning: rejected [{proposal.Step.File}] {proposal.Step.Change} — task does not need current external info",
                        WebNotNeededFeedback, ct);
                    rejectionFeedback.Add(WebNotNeededFeedback);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                    continue;
                }
                // webNeedVerified == 1 after the confirmation round → allow the web step freely.
            }
            // Missing-web-search guard (mirror of the interleaved loop): if the task
            // hints at needing CURRENT EXTERNAL information but neither the plan nor
            // this proposal carries a _web_search/_web_fetch step, confirm with the
            // LLM (regex alone is too noisy — "search for" usually means searching
            // the repo) and, when confirmed, reject with feedback steering the model
            // to the web tools. If the model still refuses after
            // MAX_STEP_REGEN_ATTEMPTS rejections, auto-inject a _web_search step so
            // genuinely web-needing tasks always get the search.
            if (proposal.Step.File != null &&
                !IsWebStep(proposal.Step.File) &&
                !planSoFar.Any(s => IsWebStep(s.File)) &&
                TaskHintsWebNeed(prompt))
            {
                if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                {
                    await EmitLog(emitSse, "info",
                        "Task hints at needing current external info — verifying with the LLM whether _web_search is required…", ct: ct);
                    var (needsWeb, query, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                    if (verified)
                    {
                        webNeedVerified = needsWeb ? 1 : -1;
                        if (needsWeb) webInjectedQuery = query;
                    }
                    else
                    {
                        webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then fail closed
                    }
                }
                if (webNeedVerified == 1)
                {
                    var webFb = WebNeedFeedback;
                    await EmitRejectedLog(emitSse,
                        $"Incremental planning: rejected [{proposal.Step.File}] {proposal.Step.Change} — task needs current external info but the plan has no _web_search step",
                        webFb, ct);
                    rejectionFeedback.Add(webFb);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                    {
                        // The planner kept refusing _web_search after MAX_STEP_REGEN_ATTEMPTS
                        // rejections. Genuinely web-needing tasks must still complete, so stop
                        // asking and inject the web step directly into the plan; the model can
                        // then plan the follow-up steps (e.g. writing the results to a file).
                        var queryText = string.IsNullOrWhiteSpace(webInjectedQuery)
                            ? BuildFallbackWebQuery(prompt)
                            : webInjectedQuery.Trim();
                        planSoFar.Add(new PlanStep { File = "_web_search", Change = queryText });
                        thinkingLog.AppendLine($"Step {planSoFar.Count}: [auto-injected] {queryText}");
                        if (emitSse)
                        {
                            await SendSse(Response, "thinking",
                                new { text = $"Auto-injected _web_search step — the planner refused after {MAX_STEP_REGEN_ATTEMPTS} rejections." }, ct);
                            await SendSse(Response, "step", new
                            {
                                index = ++stepEventIndex,
                                type = "plan",
                                status = "pending",
                                path = "_web_search",
                                description = queryText,
                                line = 0,
                                planItemIndex = planSoFar.Count
                            }, ct);
                        }
                        await EmitLog(emitSse, "warn",
                            $"The planner refused to use _web_search after {MAX_STEP_REGEN_ATTEMPTS} rejections — auto-injecting a _web_search step: \"{queryText}\"", ct: ct);
                        regenAttempts = 0;
                        rejectionFeedback.Clear();
                        continue; // model can now plan the follow-up steps around the injected search
                    }
                    continue;
                }
            }
            if (emitSse && !string.IsNullOrWhiteSpace(proposal.Thinking))
            {
                await SendSse(Response, "thinking", new { text = proposal.Thinking }, ct);
            }
            var stepIndex = ++stepEventIndex;
            if (emitSse)
            {
                await SendSse(Response, "step", new
                {
                    index = stepIndex,
                    type = "plan",
                    status = "proposing",
                    path = proposal.Step.File,
                    description = proposal.Step.Change,
                    line = proposal.Step.LineNumber,
                    planItemIndex = planSoFar.Count,
                    thinking = proposal.Thinking,
                    justification = proposal.CompletionReason
                }, ct);
            }
            // Task-tool mismatch guard (mirror of the web-need gate): a task that
            // targets the OS filesystem outside the repo must NOT be implemented by
            // editing repo source files — that is the "wrote an HTTP endpoint to
            // create a desktop folder" failure. Pure OS tasks reject repo edits
            // deterministically; when the task also hints at repo work (e.g. "save a
            // link in the README"), one memoized LLM call per file adjudicates
            // whether the edit is genuinely required.
            if (osTaskGuard &&
                proposal.Step.File != null &&
                !AgentProjectUtilities.IsSpecialMarker(proposal.Step.File))
            {
                var rejectOsEdit = osTaskPure;
                if (!osTaskPure)
                {
                    var allowed = false;
                    if (osRepoEditVerdicts.TryGetValue(proposal.Step.File, out var cached))
                    {
                        allowed = cached;
                    }
                    else
                    {
                        var (isRequired, verified) = await ConfirmRepoEditRequiredAsync(prompt, proposal.Step.File, proposal.Step.Change, emitSse, ct);
                        // Cache only SUCCESSFUL verdicts — a transient LLM failure must
                        // not permanently lock a file out of a legitimately needed edit;
                        // a later re-proposal then retries the verification. A cached
                        // "allowed" also lets subsequent edits to that file through.
                        if (verified) osRepoEditVerdicts[proposal.Step.File] = isRequired;
                        allowed = isRequired;
                    }
                    rejectOsEdit = !allowed;
                }
                if (rejectOsEdit)
                {
                    await EmitRejectedLog(emitSse,
                        $"Incremental planning: rejected [{proposal.Step.File}] {proposal.Step.Change} — task targets the OS filesystem, not the repository",
                        OsTaskEditFeedback, ct);
                    rejectionFeedback.Add(OsTaskEditFeedback);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                    continue;
                }
            }
            var skipLlm = regenAttempts > 0;
            if (!skipLlm && proposal.Step != null && !AgentProjectUtilities.IsSpecialMarker(proposal.Step.File))
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot,
                    proposal.Step.File.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fullPath))
                    skipLlm = true;
            }
            if (proposal.Step != null)
            {
                var (valid, reason) = await ValidateIncrementalStepAsync(
                    proposal.Step, prompt, discoveryContext, planSoFar, projectRoot, emitSse, ct,
                    skipLlm: skipLlm);
                if (!valid)
                {
                    var stepFb = $"REJECTED — [{proposal.Step.File}] {proposal.Step.Change} → {reason}";
                    await EmitRejectedLog(emitSse,
                        $"Incremental planning: rejected [{proposal.Step.File}] {proposal.Step.Change} — {reason}", stepFb, ct);
                    rejectionFeedback.Add(stepFb);
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "plan",
                            status = "rejected",
                            path = proposal.Step.File,
                            description = proposal.Step.Change,
                            error = reason,
                            line = proposal.Step.LineNumber,
                            planItemIndex = planSoFar.Count
                        }, ct);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                    {
                        consecutiveSlotFailures++;
                        await EmitLog(emitSse, "warn",
                            $"Incremental planning: giving up on this slot after {MAX_STEP_REGEN_ATTEMPTS} rejections — moving on. " +
                            $"({consecutiveSlotFailures} consecutive slot failures)", ct: ct);
                        if (emitSse)
                            await SendSse(Response, "thinking", new
                            {
                                text = $"Giving up on this slot after {MAX_STEP_REGEN_ATTEMPTS} rejections — moving on. ({consecutiveSlotFailures} consecutive slot failures)"
                            }, ct);
                        rejectionFeedback.Clear();
                        regenAttempts = 0;
                        if (consecutiveSlotFailures >= 3)
                            throw new InvalidOperationException(
                                "Incremental planner failed 3 slots in a row — the discovery context likely doesn't contain " +
                                "what the task needs (wrong file attached, or the target method/property doesn't exist as described). " +
                                "Attach the correct file(s) and retry.");
                        continue;
                    }
                    continue;
                }
                consecutiveSlotFailures = 0;
                var isDuplicate = false;
                foreach (var existing in planSoFar)
                {
                    if (existing.File == "_noop") continue;
                    var checks = 0;
                    if (string.Equals(existing.File, proposal.Step.File, StringComparison.OrdinalIgnoreCase)) checks++;
                    if (string.Equals(existing.Change, proposal.Step.Change, StringComparison.Ordinal) ||
                        (existing.Change?.Length > 10 && proposal.Step.Change?.Length > 10 &&
                         (existing.Change.Contains(proposal.Step.Change) || proposal.Step.Change.Contains(existing.Change)))) checks++;
                    if ((!string.IsNullOrEmpty(existing.OldString) && string.Equals(existing.OldString, proposal.Step.OldString, StringComparison.Ordinal)) ||
                        (!string.IsNullOrEmpty(existing.NewString) && string.Equals(existing.NewString, proposal.Step.NewString, StringComparison.Ordinal)) ||
                        (!string.IsNullOrEmpty(existing.TargetSymbol) && string.Equals(existing.TargetSymbol, proposal.Step.TargetSymbol, StringComparison.Ordinal))) checks++;
                    if (checks >= 2) { isDuplicate = true; break; }
                }
                if (isDuplicate)
                {
                    await EmitLog(emitSse, "info",
                        $"Plan complete — duplicate step proposed: [{proposal.Step.File}] {proposal.Step.Change} — nothing new to add", ct: ct);
                    break;
                }
                planSoFar.Add(proposal.Step);
                rejectionFeedback.Clear();
                var planningRounds = regenAttempts + 1;
                totalPlanningRounds += planningRounds;
                regenAttempts = 0;
                var retryWord = planningRounds - 1 == 1 ? "retry" : "retries";
                await EmitLog(emitSse, planningRounds >= 3 ? "warn" : "metric",
                    planningRounds > 1
                        ? $"📊 Step {planSoFar.Count} planned in {planningRounds} rounds ({planningRounds - 1} {retryWord}) — [{proposal.Step.File}] {proposal.Step.Change}"
                        : $"📊 Step {planSoFar.Count} planned in 1 round — [{proposal.Step.File}] {proposal.Step.Change}", ct: ct);
                if (!string.IsNullOrWhiteSpace(proposal.Thinking))
                {
                    thinkingLog.AppendLine($"Step {planSoFar.Count}: {proposal.Thinking}");
                }
                await EmitLog(emitSse, "info",
                    $"Incremental planning: committed step {planSoFar.Count} — [{proposal.Step.File}] {proposal.Step.Change}", ct: ct);
                if (emitSse)
                {
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "plan",
                        status = "pending",
                        path = proposal.Step.File,
                        description = proposal.Step.Change,
                        line = proposal.Step.LineNumber,
                        planItemIndex = planSoFar.Count,
                        message = proposal.CompletionReason
                    }, ct);
                    await SendSse(Response, "plan", new
                    {
                        thinking = thinkingLog.ToString(),
                        summary = $"Building plan incrementally — {planSoFar.Count} step(s) so far",
                        items = planSoFar,
                        incremental = true
                    }, ct);
                }
            }
            break;
        }
        if (planSoFar.Count == 0)
            throw new InvalidOperationException("Incremental planner did not produce any actionable steps.");
        var plan = new AgentPlan
        {
            Thinking = thinkingLog.ToString(),
            Summary = $"Built incrementally across {planSoFar.Count} validated step(s)",
            Score = 90,
            Plan = planSoFar
        };
        return (plan, discoveryContext);
    }
    private async Task SendPlanActivityEventAsync(
        StringBuilder thinkingLog, List<PlanStep> planSoFar, bool emitSse,
        string activityFile, string activityChange, string summary, int? runningIndex, CancellationToken ct)
    {
        if (!emitSse) return;
        List<Object> stepItems = planSoFar.Select((s, idx) => new
        {
            File = s.File,
            Change = s.Change,
            Line = s.LineNumber,
            OldString = s.OldString,
            NewString = s.NewString,
            done = runningIndex == null || idx < runningIndex.Value
        }).ToList<object>();
        // Always emit the activity row — even before the first step is committed
        // (stepItems empty) — so the UI streams verbose thinking/planning/editing
        // updates instead of freezing on the initial "Reading task…" placeholder
        // during the (often minutes-long) discovery + pre-plan thinking phase.
        var items = stepItems.Concat(new[] {
            new {
                File = activityFile,
                Change = activityChange,
                Line = 0,
                OldString = "",
                NewString = "",
                done = activityFile != "_planning"
            }
        }).ToList();
        await SendSse(Response, "plan", new
        {
            thinking = thinkingLog.ToString(),
            summary = summary,
            items = items,
            incremental = true,
            live = true
        }, ct);
    }
    // Trigger phrases that hint a task may want CURRENT EXTERNAL information.
    // Deliberately broad — "search for"/"look up" often mean searching the repo,
    // so a hit only opens the LLM verification gate, never rejects by itself.
    private static readonly string[] WebNeedHints =
    {
        "web search", "search the web", "web_search", "web fetch", "web_fetch",
        "internet", "online", "current", "up to date", "up-to-date", "latest",
        "live data", "today's", "todays", "fetch from", "fetch the", "google",
        "api docs", "search for", "look up", "find out"
    };

    /// <summary>Feedback shown whenever a step is rejected because the task needs current external info.</summary>
    private const string WebNeedFeedback =
        "The task needs CURRENT EXTERNAL information (live data, web/API docs, latest versions) that cannot be found inside this repository. " +
        "Use a \"_web_search\" step (put the query in \"change\") or a \"_web_fetch\" step (put the URL in \"change\") — do NOT write code to fetch it.";

    /// <summary>Feedback shown when a _web_search/_web_fetch step is proposed for a task that does NOT need current external info.</summary>
    private const string WebNotNeededFeedback =
        "The task does NOT need CURRENT EXTERNAL information — this _web_search/_web_fetch step is unnecessary and is rejected. " +
        "Work from the DISCOVERY CONTEXT and the repo files already in context: propose a concrete repo edit, _create_file, or _command step instead. " +
        "Only use _web_search/_web_fetch when the task genuinely requires data that cannot come from the project.";

    private static bool TaskHintsWebNeed(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var lower = prompt.ToLowerInvariant();
        foreach (var hint in WebNeedHints)
            if (lower.Contains(hint)) return true;
        return false;
    }

    private static bool IsWebStep(string? file)
    {
        return file != null &&
            (file.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
             file.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Harvests executed _web_search/_web_fetch outputs from a step's results and appends
    /// them to the discovery context so the NEXT planning/thinking round can see what the
    /// search/fetch actually returned. ExecuteWebPlanStep accumulates results into a local
    /// webCtx that is only flushed via ReplanRemainingSteps when the SAME plan has further
    /// steps — in the interleaved loop each step runs as its own single-step plan, so that
    /// flush never fires and the model re-invented scraping code instead of using results.
    /// </summary>
    private static string AppendWebResultsToDiscoveryContext(
        string discoveryContext, IEnumerable<Dictionary<string, object?>> newResults)
    {
        foreach (var r in newResults)
        {
            var type = r.GetValueOrDefault("type")?.ToString();
            if (!IsWebStep(type)) continue;
            // Only successful fetches are real web data — an error result carrying text (e.g. an
            // exception message) must never be presented to the planner as search output.
            if (r.GetValueOrDefault("status")?.ToString() != "done") continue;
            var query = r.GetValueOrDefault("query")?.ToString() ?? r.GetValueOrDefault("url")?.ToString() ?? "";
            var output = r.GetValueOrDefault("output")?.ToString();
            if (string.IsNullOrWhiteSpace(output) || output.Length <= 80) continue;
            var capped = output.Length > 20000 ? output[..20000] + "…" : output;
            discoveryContext += $"\n\n### WEB RESULTS [{query}] ###\n{capped}\n";
        }
        return discoveryContext;
    }

    /// <summary>
    /// Harvests executed web results from the run's results for injection into the
    /// edit-resolution prompt, so FORMAT C/D / oldString-newString generation can copy real
    /// titles, URLs, and facts into newString instead of inventing them. Reuses
    /// AppendWebResultsToDiscoveryContext's filtering (done-status only, 20k cap) and returns
    /// "" when there are no usable web results.
    /// </summary>
    private static string HarvestWebResultsForEditContext(IEnumerable<Dictionary<string, object?>> results)
    {
        var ctx = AppendWebResultsToDiscoveryContext("", results);
        return ctx.TrimStart('\n', ' ', '\t');
    }

    /// <summary>
    /// LLM-confirmed version of the missing-web-search gate. Regex hints alone
    /// false-positive on repo-internal "search"/"look up" phrasing, so a cheap
    /// classifier call decides whether the task genuinely needs current external
    /// information.
    /// Tri-state return: Verified=false when the classifier call failed (empty/parse
    /// error) — callers decide how to fail: the missing-web-search guard fails CLOSED
    /// (keeps the non-web plan, caches no verdict so a re-proposal retries), while the
    /// web-step gate fails OPEN (allows the web step) so a transient LLM blip can
    /// never block a genuinely web-needing task.
    /// When the task DOES need the web, the same single call also yields the search
    /// query used later for the auto-injected _web_search step if the planner
    /// refuses to plan one after repeated rejections.
    /// </summary>
    private async Task<(bool NeedsWeb, string? Query, bool Verified)> ConfirmWebNeedAsync(string prompt, bool emitSse, CancellationToken ct)
    {
        try
        {
            var taskPreview = AgentTextUtilities.Truncate(prompt ?? "", 1200);
            var sb = new StringBuilder();
            sb.AppendLine("TASK:");
            sb.AppendLine(taskPreview);
            sb.AppendLine();
            sb.AppendLine("DECIDE: does this task REQUIRE CURRENT, EXTERNAL information from the web (live prices, breaking news, up-to-date API docs, latest package versions, facts that change over time) — information that CANNOT be found or computed inside the local repository?");
            sb.AppendLine("Tasks about the repo's own code, files, or tests NEVER need the web, even if they contain words like 'search', 'look up', or 'find'.");
            sb.AppendLine("Output ONLY JSON: {\"needsWeb\": true|false, \"reason\": \"one short sentence\", \"query\": \"concise web search query under 80 chars — only when needsWeb is true, else an empty string\"}");
            var (raw, _, err) = await CallLlmRaw(
                "You are a strict task classifier. Output ONLY the requested JSON.",
                sb.ToString(), ct, _infiniteTimeout, maxTokens: 120);
            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn", $"Web-need verification failed ({err}) — no verdict cached.", ct: ct);
                return (false, null, false);
            }
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var needsWeb = doc.RootElement.TryGetProperty("needsWeb", out var nw) && nw.ValueKind == JsonValueKind.True;
            var query = needsWeb && doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
                ? q.GetString()
                : null;
            return (needsWeb, query, true);
        }
        catch
        {
            return (false, null, false);
        }
    }

    /// <summary>Feedback shown whenever a repo file edit is rejected for an OS-filesystem task.</summary>
    private const string OsTaskEditFeedback =
        "This task operates on the OS filesystem OUTSIDE the repository (desktop/home/Downloads/etc.) — the repository's source files are NOT the target. " +
        "Use \"_command\" (e.g. mkdir/New-Item for folders, rm/Move-Item for files) or \"_create_directory\" for the OS operation. Do NOT edit repository source files to perform it.";

    /// <summary>
    /// True when an OS-filesystem task ALSO mentions repo work (README, link, note,
    /// docs, "add to"...). Pure OS tasks reject repo edits deterministically; only
    /// this case pays an LLM call to adjudicate whether a specific repo edit is
    /// genuinely part of the task (e.g. "create the folder AND save a link in the
    /// README").
    /// </summary>
    private static bool OsPromptHintsRepoWork(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var lower = prompt.ToLowerInvariant();
        var hints = new[] { "readme", "link", "mention", "note", "document", "add to" };
        foreach (var h in hints)
        {
            if (lower.Contains(h)) return true;
        }
        return false;
    }

    /// <summary>
    /// LLM adjudication for a repo file edit proposed while the task targets the OS
    /// filesystem: decides whether editing this specific repo file is genuinely part
    /// of the task (e.g. "create the folder AND add a link in the README"). Fails
    /// closed to (false, false) — for an OS task the prior is that repo edits are
    /// wrong, and the rejection feedback steers the model to marker tools. The
    /// Verified flag tells the caller whether to CACHE the verdict: a transient LLM
    /// failure must not permanently lock a file out, so a re-proposal retries.
    /// </summary>
    private async Task<(bool Required, bool Verified)> ConfirmRepoEditRequiredAsync(
        string prompt, string file, string? change, bool emitSse, CancellationToken ct)
    {
        try
        {
            var taskPreview = AgentTextUtilities.Truncate(prompt ?? "", 1200);
            var sb = new StringBuilder();
            sb.AppendLine("TASK:");
            sb.AppendLine(taskPreview);
            sb.AppendLine();
            sb.AppendLine($"PROPOSED STEP: edit repository file \"{file}\" — {(string.IsNullOrWhiteSpace(change) ? "(no description)" : change)}");
            sb.AppendLine();
            sb.AppendLine("The task appears to target the OS filesystem OUTSIDE the repository (desktop/home/Downloads/etc.).");
            sb.AppendLine("DECIDE: is editing this repository source file GENUINELY required by the task (e.g. the task explicitly says to also update a repo file such as a README, add a link, or record something in the repo), or is the model wrongly reaching into the repo when the task is purely about the OS filesystem?");
            sb.AppendLine("Output ONLY JSON: {\"repoEditRequired\": true|false, \"reason\": \"one short sentence\"}");
            var (raw, _, err) = await CallLlmRaw(
                "You are a strict task classifier. Output ONLY the requested JSON.",
                sb.ToString(), ct, _infiniteTimeout, maxTokens: 120);
            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn", $"OS-task repo-edit verification failed ({err}) — rejecting the repo edit this round (re-proposal will retry).", ct: ct);
                return (false, false);
            }
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            return (doc.RootElement.TryGetProperty("repoEditRequired", out var r) && r.ValueKind == JsonValueKind.True, true);
        }
        catch
        {
            return (false, false);
        }
    }

    /// <summary>
    /// Deterministic fallback query for the auto-injected _web_search step when the
    /// LLM verification produced no usable query (or the LLM call failed). The
    /// flattened task text itself is a serviceable search query for the task's most
    /// important topic.
    /// </summary>
    private static string BuildFallbackWebQuery(string? prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            var flat = Regex.Replace(prompt.Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();
            if (flat.Length > 160)
            {
                flat = flat.Substring(0, 160);
                // Don't split a UTF-16 surrogate pair (emoji) at the cutoff.
                if (flat.Length > 0 && char.IsHighSurrogate(flat[^1])) flat = flat[..^1];
                flat = flat.TrimEnd();
            }
            if (flat.Length > 0) return flat;
        }
        return "latest information";
    }

    private async Task<(AgentPlan plan, List<object> results, string discoveryContext, bool planCompleteDeclared)> RunInterleavedPlanExecutionLoop(
        string prompt, string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct, string? steeringContext, string? cardId = null,
        List<string>? attachedFiles = null, int? atomicStepEstimate = null)
    {
        var planSoFar = new List<PlanStep>();
        var allResults = new List<object>();
        var rejectionFeedback = new List<string>();
        var planCompleteDeclared = false;
        var exploredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var thinkingLog = new StringBuilder();
        // Bounded accumulator for the "CHANGES FROM PREVIOUS STEP" section: instead of stacking
        // every step's raw diff forever, we keep ONE section and LLM-summarize it once it passes
        // cfg.diffContextSummaryChars (when cfg.summarizeDiffContext is on).
        var diffContextAccum = new StringBuilder();
        var regenAttempts = 0;
        var consecutiveSlotFailures = 0;
        var totalPlanningRounds = 0;
        var webNeedVerified = 0; // 0 = unchecked, 1 = task needs web, -1 = no web needed
        var webNeedVerifyFailures = 0; // consecutive failed classifier calls — caps re-confirmation at 2 per run
        string? webInjectedQuery = null; // search query from the same verification call, used if we auto-inject _web_search
        // OS-filesystem task guard state: pure OS tasks reject repo file edits
        // deterministically; hint-y tasks get one memoized LLM verdict per file.
        var osTaskGuard = IsExternalFilesystemTask(prompt);
        var osTaskPure = osTaskGuard && !OsPromptHintsRepoWork(prompt);
        var osRepoEditVerdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (emitSse)
        {
            await SendSse(Response, "plan", new
            {
                thinking = "",
                summary = "Plan atomic step → execute it → verify → decide if another step is needed… — 0 done so far",
                items = new[] { new { File = "_planning", Change = "Reading task & discovery context…", Line = 0, OldString = "", NewString = "", done = false } },
                incremental = true
            }, ct);
        }
        var pendingSteps = new Queue<PlanStep>();
        Func<string, Task>? planActivity = null;
        if (emitSse)
        {
            planActivity = async phase =>
            {
                var stepNum = planSoFar.Count;
                if (stepNum == 0) return;
                var isVerifying = phase == "verifying";
                var currentIsConcrete = ShouldApplyDirectly(planSoFar[stepNum - 1]);
                var label = isVerifying
                    ? $"Verifying Step {stepNum} — checking the edit…"
                    : currentIsConcrete
                        ? $"Applying edits — Step {stepNum}…"
                        : $"Thinking for edit — Step {stepNum}…";
                await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                    isVerifying ? "_verifying" : "_executing", label,
                    $"Executed {stepNum - 1} step(s) — {label}", stepNum - 1, ct);
            };
        }
        if (emitSse)
        {
            await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                "_planning", "Reading task & discovery context…",
                "Plan atomic step → execute it → verify → decide if another step is needed… — 0 done so far", null, ct);
        }
        for (var turn = 0; turn < MAX_INCREMENTAL_STEPS; turn++)
        {
            ct.ThrowIfCancellationRequested();
            if (emitSse && pendingSteps.Count == 0 && !planCompleteDeclared)
            {
                await SendSse(Response, "phase", new { message = $"Thinking about Step {planSoFar.Count + 1}…" }, ct);
            }
            if (pendingSteps.Count > 0)
            {
                var queuedStep = pendingSteps.Dequeue();
                planSoFar.Add(queuedStep);
                if (!string.IsNullOrWhiteSpace(queuedStep.Change))
                    thinkingLog.AppendLine($"Step {planSoFar.Count}: {queuedStep.Change}");
                var queuedIsConcrete = ShouldApplyDirectly(queuedStep);
                await EmitLog(emitSse, "info",
                    queuedIsConcrete
                        ? $"▶ Applying edits — Step {planSoFar.Count} — [{queuedStep.File}] {queuedStep.Change}"
                        : $"▶ Thinking for edit — Step {planSoFar.Count} — [{queuedStep.File}] {queuedStep.Change}", ct: ct);
                await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                    "_executing", queuedIsConcrete
                        ? $"Applying edits — Step {planSoFar.Count} — {queuedStep.Change}"
                        : $"Thinking for edit — Step {planSoFar.Count} — {queuedStep.Change}",
                    $"Completed {planSoFar.Count - 1} step(s) — {(queuedIsConcrete ? "applying edits for" : "thinking for edit on")} Step {planSoFar.Count}",
                    planSoFar.Count - 1, ct);
                await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                    summary: $"Interleaved execution — {planSoFar.Count} step(s) so far", score: 90);
                var singleStepPlan = new AgentPlan { Plan = new List<PlanStep> { queuedStep }, Summary = queuedStep.Change, Score = 90 };
                var beforeCount = allResults.Count;
                var preEditContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (queuedStep.File != null && !AgentProjectUtilities.IsSpecialMarker(queuedStep.File))
                {
                    var fp = Path.GetFullPath(Path.Combine(projectRoot, (queuedStep.File ?? "").Replace('/', Path.DirectorySeparatorChar)));
                    if (System.IO.File.Exists(fp)) preEditContents[queuedStep.File!] = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                }
                try
                {
                    await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, singleStepPlan, ct, allResults,
                        steeringContext: steeringContext, attachedFiles: attachedFiles, cardId: cardId,
                        replanBudget: new[] { 0 }, onActivity: planActivity,
                        skipLlmPreResolution: queuedIsConcrete);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await EmitLog(emitSse, "error",
                        $"⛔ Interleaved execution halted — queued step {planSoFar.Count} threw: {ex.Message}", ct: ct);
                    if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                    await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                        summary: $"Execution halted at queued step {planSoFar.Count + 1} — exception: {ex.Message}", score: 0,
                        append: false);
                    break;
                }
                var newResults = allResults.Skip(beforeCount).OfType<Dictionary<string, object?>>().ToList();
                var stepSucceeded = newResults.Any(r => r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created");
                await EmitLog(emitSse, "info",
                    $"DIAG: After queued step — stepSucceeded={stepSucceeded}, planSoFar.Count={planSoFar.Count}", ct: ct);
                discoveryContext = AppendWebResultsToDiscoveryContext(discoveryContext, newResults);
                var globalPlanIdx = planSoFar.Count - 1;
                foreach (var r in newResults)
                {
                    r["planItemIndex"] = globalPlanIdx;
                    if (emitSse && r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
                        await SendSse(Response, "step", r, ct);
                }
                await PersistBoardDataPlanStepAsync(cardId, globalPlanIdx, emitSse, ct);
                if (!stepSucceeded) { break; }
                continue;
            }
            string? extendedReasoning = null;
            var prePlanCfg = await LoadConfigAsync();
            // Retry mode (a step was just rejected): the planner already produced its concrete
            // edit for this step — skip the pre-plan thinking round so rejected edits flow
            // straight back into re-proposal instead of spawning more (often hallucinated)
            // thinking walls. A crap edit is ignored and the edit pipeline continues normally.
            if (prePlanCfg.extendThinking && regenAttempts == 0 && !string.IsNullOrWhiteSpace(cardId))
            {
                if (emitSse && !planCompleteDeclared)
                    await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                        "_planning", $"Deep thinking for plan — Step {planSoFar.Count + 1}…",
                        $"Deep thinking for plan — Step {planSoFar.Count + 1}…", null, ct);
                try
                {
                    extendedReasoning = await ExtendThinkingPrePlanAsync(
                        cardId, prompt, discoveryContext, planSoFar, projectRoot, emitSse, ct,
                        attachedFiles: attachedFiles);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "warn", $"Pre-plan thinking skipped: {ex.Message}",
                        new { reason = ex.Message }, ct: ct);
                }
            }
            else if (prePlanCfg.extendThinking && regenAttempts > 0)
            {
                await EmitLog(emitSse, "bypass",
                    $"Pre-plan thinking skipped (retry {regenAttempts}) — re-proposing directly with rejection feedback", ct: ct);
            }
            if (emitSse && !planCompleteDeclared)
            {
                var proposingText = planSoFar.Count == 0
                    ? "Reading task & proposing the first step…"
                    : $"Proposing step {planSoFar.Count + 1} — planning the edit…";
                await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                    "_planning", proposingText,
                    $"Proposing step {planSoFar.Count + 1}…", null, ct);
            }
            var proposal = await ProposeNextIncrementalStepAsync(prompt, discoveryContext, planSoFar, steeringContext, rejectionFeedback, emitSse, ct, extendedReasoning: extendedReasoning, atomicStepEstimate: atomicStepEstimate);
            if (proposal == null)
            {
                var jsonFb = "Your previous response could not be parsed as valid JSON. " +
                    "Output ONLY the JSON object described in the system prompt.";
                await EmitRejectedLog(emitSse, "Interleaved execution: rejected — response was not valid JSON; retrying with parse feedback", jsonFb, ct);
                rejectionFeedback.Add(jsonFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) { break; }
                continue;
            }
            if (proposal.PlanComplete)
            {
                // Web-need gate: declaring the plan complete is itself a refusal to
                // use the web tools when the task needs current external info and no
                // web step exists. Reject, and on the regen cap inject the search so
                // web-needing tasks cannot escape by claiming completion.
                if (TaskHintsWebNeed(prompt) && !planSoFar.Any(s => IsWebStep(s.File)))
                {
                    if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                    {
                        await EmitLog(emitSse, "info",
                            "Task hints at needing current external info — verifying with the LLM whether _web_search is required…", ct: ct);
                        var (needsWeb, query, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                        if (verified)
                        {
                            webNeedVerified = needsWeb ? 1 : -1;
                            if (needsWeb) webInjectedQuery = query;
                        }
                        else
                        {
                            webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then stop calling
                        }
                    }
                    if (webNeedVerified == 1)
                    {
                        await EmitRejectedLog(emitSse,
                            "Interleaved execution: rejected plan-complete — task needs current external info but the plan has no _web_search step",
                            WebNeedFeedback, ct);
                        rejectionFeedback.Add(WebNeedFeedback);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                        {
                            var queryText = string.IsNullOrWhiteSpace(webInjectedQuery)
                                ? BuildFallbackWebQuery(prompt)
                                : webInjectedQuery.Trim();
                            pendingSteps.Enqueue(new PlanStep { File = "_web_search", Change = queryText });
                            await EmitLog(emitSse, "warn",
                                $"The planner declared the plan complete without using _web_search after {MAX_STEP_REGEN_ATTEMPTS} rejections — auto-injecting a _web_search step: \"{queryText}\"", ct: ct);
                            regenAttempts = 0;
                            rejectionFeedback.Clear();
                            continue; // loop top executes the injected step, then planning resumes
                        }
                        continue;
                    }
                }
                if (emitSse)
                {
                    await SendSse(Response, "thinking", new { text = $"Plan complete: {proposal.CompletionReason}" }, ct);
                    await SendSse(Response, "plan", new
                    {
                        thinking = thinkingLog.ToString(),
                        summary = $"Plan complete — {planSoFar.Count} step(s) executed",
                        items = planSoFar.Select((s, idx) => new
                        {
                            File = s.File,
                            Change = s.Change,
                            Line = s.LineNumber,
                            OldString = s.OldString,
                            NewString = s.NewString,
                            done = true
                        }).ToList(),
                        incremental = true
                    }, ct);
                }
                await EmitLog(emitSse, "success",
                    $"Interleaved execution: complete after {planSoFar.Count} step(s) — {proposal.CompletionReason}", ct: ct);
                var budgetTxt = atomicStepEstimate is > 0
                    ? $" (estimated {atomicStepEstimate})" : "";
                await EmitLog(emitSse, "metric",
                    $"📊 Card planning: {planSoFar.Count} step(s){budgetTxt}, {totalPlanningRounds} total planning round(s)", ct: ct);
                planCompleteDeclared = true;
                break;
            }
            if (!string.IsNullOrWhiteSpace(proposal.ExploreFile))
            {
                if (exploredFiles.Add(proposal.ExploreFile))
                {
                    if (proposal.ExploreFile.Equals("_discover", StringComparison.OrdinalIgnoreCase))
                    {
                        await EmitLog(emitSse, "info", "Planner requested _discover — running project-wide search…", ct: ct);
                        discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                        regenAttempts = 0;
                        continue;
                    }
                    var isMarker = proposal.ExploreFile.StartsWith("_");
                    var alreadyInContext = false;
                    if (!isMarker)
                    {
                        var normPath = proposal.ExploreFile.Replace('\\', '/').TrimStart('/');
                        // A file present ONLY as a focused region is NOT fully in context —
                        // a later re-request may legitimately need a different symbol's region.
                        alreadyInContext = !IsFocusedSectionInContext(discoveryContext, normPath) &&
                                           (discoveryContext.Contains($"### read {normPath}") ||
                                            discoveryContext.Contains($"### {normPath}") ||
                                            Regex.IsMatch(discoveryContext, $@"### (?:read )?\S*{Regex.Escape(Path.GetFileName(normPath))}\b"));
                    }
                    if (alreadyInContext)
                    {
                        var ctxFb = $"STOP — '{proposal.ExploreFile}' is ALREADY in the DISCOVERY CONTEXT above. " +
                            "Do NOT request it again. Read it and propose the actual next step.";
                        await EmitRejectedLog(emitSse,
                            $"Interleaved execution: rejected explore — '{proposal.ExploreFile}' already in discovery context; retrying", ctxFb, ct);
                        rejectionFeedback.Add(ctxFb);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                        continue;
                    }
                    await EmitLog(emitSse, "info", $"Interleaved execution: exploring {proposal.ExploreFile}", ct: ct);
                    await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                        "_exploring", $"Exploring {proposal.ExploreFile}…",
                        $"Exploring {proposal.ExploreFile}…", null, ct);
                    discoveryContext = await ExplorationPipeline(
                        new List<PlanStep> { new() { File = "_explore", Change = proposal.ExploreFile } },
                        discoveryContext, projectRoot, emitSse, ct, prompt);
                    if (!string.IsNullOrWhiteSpace(cardId))
                        await AutoAttachFileToCardAsync(cardId, proposal.ExploreFile, emitSse, ct);
                    regenAttempts = 0;
                    continue;
                }
                else
                {
                    // The earlier read may have been only a FOCUSED region (the file was
                    // never shown in full) — then re-exploring is legitimate: the model
                    // can surface a different symbol's region. Only reject when the file
                    // is genuinely shown in full already.
                    var normPath = proposal.ExploreFile.Replace('\\', '/').TrimStart('/');
                    var focusedOnly = !proposal.ExploreFile.StartsWith("_") &&
                        IsFocusedSectionInContext(discoveryContext, normPath);
                    if (!focusedOnly)
                    {
                        var exploreFb = $"You asked to explore '{proposal.ExploreFile}' again — it is ALREADY shown in full above. " +
                            "Do not re-request it. Propose the actual next step using the exact names visible there.";
                        await EmitRejectedLog(emitSse,
                            $"Interleaved execution: rejected explore — '{proposal.ExploreFile}' already shown in full; retrying", exploreFb, ct);
                        rejectionFeedback.Add(exploreFb);
                        if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                        continue;
                    }
                    await EmitLog(emitSse, "info",
                        $"Interleaved execution: re-exploring {proposal.ExploreFile} for another focused region", ct: ct);
                    await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                        "_exploring", $"Re-exploring {proposal.ExploreFile}…",
                        $"Re-exploring {proposal.ExploreFile}…", null, ct);
                    discoveryContext = await ExplorationPipeline(
                        new List<PlanStep> { new() { File = "_explore", Change = proposal.ExploreFile } },
                        discoveryContext, projectRoot, emitSse, ct, prompt);
                    if (!string.IsNullOrWhiteSpace(cardId))
                        await AutoAttachFileToCardAsync(cardId, proposal.ExploreFile, emitSse, ct);
                    regenAttempts = 0;
                    continue;
                }
            }
            if (proposal.Step == null)
            {
                var neitherFb = "You returned neither planComplete=true, exploreFile, nor a step — return exactly one.";
                await EmitRejectedLog(emitSse, "Interleaved execution: rejected — returned neither planComplete, exploreFile, nor a step; retrying", neitherFb, ct);
                rejectionFeedback.Add(neitherFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            if (proposal.Step.File.Equals("_discover", StringComparison.OrdinalIgnoreCase))
            {
                if (exploredFiles.Add("_discover"))
                {
                    await EmitLog(emitSse, "info", "Planner proposed _discover step — running project-wide search…", ct: ct);
                    discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                    regenAttempts = 0;
                    continue;
                }
                var discoverFb = "You already ran _discover — its results are now in the DISCOVERY CONTEXT. Use them to propose the next step.";
                await EmitRejectedLog(emitSse, "Interleaved execution: rejected — _discover was already run this session; retrying", discoverFb, ct);
                rejectionFeedback.Add(discoverFb);
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            if (proposal.Step.File != null && planSoFar.Count > 0)
            {
                // For special markers (_create_file, _create_directory, _command, etc.),
                // the "File" field is always the marker name — use Change for identity.
                // Two _create_file steps are only duplicates if they target the exact same path.
                var isSpecialStep = AgentProjectUtilities.IsSpecialMarker(proposal.Step.File);
                var duplicateOf = planSoFar.FirstOrDefault(s =>
                {
                    if (!string.Equals(s.File, proposal.Step.File, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (isSpecialStep)
                        // Special markers: duplicate only if the Change (=path/command) is identical
                        return string.Equals(s.Change, proposal.Step.Change, StringComparison.OrdinalIgnoreCase);
                    // Regular file steps: use token overlap on the change description
                    return TokenOverlap(s.Change ?? "", proposal.Step.Change ?? "") > 0.35;
                });
                if (duplicateOf != null)
                {
                    var dupFb =
                        $"STEP ALREADY DONE (IMMUTABLE) — [{proposal.Step.File}] {proposal.Step.Change}\n" +
                        $"is too similar to the COMPLETED step:\n" +
                        $"  [{duplicateOf.File}] {duplicateOf.Change}\n" +
                        $"Completed steps CANNOT be revised, edited, or repeated. " +
                        $"Look at the EDIT LOG — this work is DONE. " +
                        $"If the task needs something ELSE, propose a DIFFERENT step. " +
                        $"If the task is fully satisfied, return planComplete=true.";
                    await EmitRejectedLog(emitSse,
                        $"Interleaved execution: rejected duplicate step — [{proposal.Step.File}] {proposal.Step.Change} repeats completed step [{duplicateOf.File}] {duplicateOf.Change}", dupFb, ct);
                    rejectionFeedback.Add(dupFb);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                    continue;
                }
            }
            // Web-step gate (mirror of the incremental loop): a _web_search/_web_fetch
            // proposal is allowed freely once the LLM has confirmed the task needs the web
            // (webNeedVerified == 1). Otherwise the first web proposal triggers ONE
            // confirmation round; if the verdict is that the task does NOT need current
            // external info, the web step is rejected with feedback steering back to the
            // repo context (no further web steps this run).
            if (IsWebStep(proposal.Step.File) && webNeedVerified != 1)
            {
                if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                {
                    await EmitLog(emitSse, "info",
                        "The planner proposed a _web_search/_web_fetch step — verifying with the LLM whether the task actually needs current external info…", ct: ct);
                    var (needsWeb, query, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                    if (verified)
                    {
                        webNeedVerified = needsWeb ? 1 : -1;
                        if (needsWeb) webInjectedQuery = query;
                    }
                    else
                    {
                        webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then fail open
                    }
                }
                if (webNeedVerified == -1)
                {
                    await EmitRejectedLog(emitSse,
                        $"Interleaved execution: rejected [{proposal.Step.File}] {proposal.Step.Change} — task does not need current external info",
                        WebNotNeededFeedback, ct);
                    rejectionFeedback.Add(WebNotNeededFeedback);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                    continue;
                }
                // webNeedVerified == 1 after the confirmation round → allow the web step freely.
            }
            // Missing-web-search guard: if the task hints at needing CURRENT
            // EXTERNAL information but neither the plan nor this proposal carries a
            // _web_search/_web_fetch step, confirm with the LLM (regex alone is too
            // noisy — "search for" usually means searching the repo) and, when
            // confirmed, reject with feedback steering the model to the web tools.
            // The verification is memoized so it costs at most one call per run.
            if (proposal.Step.File != null &&
                !IsWebStep(proposal.Step.File) &&
                !planSoFar.Any(s => IsWebStep(s.File)) &&
                TaskHintsWebNeed(prompt))
            {
                if (webNeedVerified == 0 && webNeedVerifyFailures < 2)
                {
                    await EmitLog(emitSse, "info",
                        "Task hints at needing current external info — verifying with the LLM whether _web_search is required…", ct: ct);
                    var (needsWeb, query, verified) = await ConfirmWebNeedAsync(prompt, emitSse, ct);
                    if (verified)
                    {
                        webNeedVerified = needsWeb ? 1 : -1;
                        if (needsWeb) webInjectedQuery = query;
                    }
                    else
                    {
                        webNeedVerifyFailures++; // transient classifier failure — retry up to 2×, then fail closed
                    }
                }
                if (webNeedVerified == 1)
                {
                    var webFb = WebNeedFeedback;
                    await EmitRejectedLog(emitSse,
                        $"Interleaved execution: rejected [{proposal.Step.File}] {proposal.Step.Change} — task needs current external info but the plan has no _web_search step",
                        webFb, ct);
                    rejectionFeedback.Add(webFb);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                    {
                        // The planner kept refusing _web_search after MAX_STEP_REGEN_ATTEMPTS
                        // rejections. Genuinely web-needing tasks must still complete, so stop
                        // asking and auto-inject a _web_search step. It is queued, so it flows
                        // through the exact same execution path as any model-proposed step
                        // (loop-top pendingSteps branch → ExecutePlan), board persistence and
                        // SSE included.
                        var queryText = string.IsNullOrWhiteSpace(webInjectedQuery)
                            ? BuildFallbackWebQuery(prompt)
                            : webInjectedQuery.Trim();
                        pendingSteps.Enqueue(new PlanStep { File = "_web_search", Change = queryText });
                        await EmitLog(emitSse, "warn",
                            $"The planner refused to use _web_search after {MAX_STEP_REGEN_ATTEMPTS} rejections — auto-injecting a _web_search step: \"{queryText}\"", ct: ct);
                        regenAttempts = 0;
                        rejectionFeedback.Clear();
                        continue; // loop top picks up the injected step and executes it
                    }
                    continue;
                }
            }
            // Task-tool mismatch guard (mirror of the web-need gate): a task that
            // targets the OS filesystem outside the repo must NOT be implemented by
            // editing repo source files — that is the "wrote an HTTP endpoint to
            // create a desktop folder" failure. Pure OS tasks reject repo edits
            // deterministically; when the task also hints at repo work (e.g. "save a
            // link in the README"), one memoized LLM call per file adjudicates
            // whether the edit is genuinely required.
            if (osTaskGuard &&
                proposal.Step.File != null &&
                !AgentProjectUtilities.IsSpecialMarker(proposal.Step.File))
            {
                var rejectOsEdit = osTaskPure;
                if (!osTaskPure)
                {
                    var allowed = false;
                    if (osRepoEditVerdicts.TryGetValue(proposal.Step.File, out var cached))
                    {
                        allowed = cached;
                    }
                    else
                    {
                        var (isRequired, verified) = await ConfirmRepoEditRequiredAsync(prompt, proposal.Step.File, proposal.Step.Change, emitSse, ct);
                        // Cache only SUCCESSFUL verdicts — a transient LLM failure must
                        // not permanently lock a file out of a legitimately needed edit;
                        // a later re-proposal then retries the verification. A cached
                        // "allowed" also lets subsequent edits to that file through.
                        if (verified) osRepoEditVerdicts[proposal.Step.File] = isRequired;
                        allowed = isRequired;
                    }
                    rejectOsEdit = !allowed;
                }
                if (rejectOsEdit)
                {
                    await EmitRejectedLog(emitSse,
                        $"Interleaved execution: rejected [{proposal.Step.File}] {proposal.Step.Change} — task targets the OS filesystem, not the repository",
                        OsTaskEditFeedback, ct);
                    rejectionFeedback.Add(OsTaskEditFeedback);
                    if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                    continue;
                }
            }
            var skipLlm = regenAttempts > 0;
            if (!skipLlm && !AgentProjectUtilities.IsSpecialMarker(proposal.Step.File))
            {
                var fp = Path.GetFullPath(Path.Combine(projectRoot, (proposal.Step.File ?? "").Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fp)) skipLlm = true;
            }
            // If the last step had needsExtraStep=false, force LLM validation to check if the new step
            // is about a genuinely different concern — the per-step verifier already confirmed completion.
            string? completionNote = null;
            var lastResult = allResults
                .OfType<Dictionary<string, object?>>()
                .Where(r => r.ContainsKey("needsExtraStep") && r.GetValueOrDefault("status")?.ToString() is "modified" or "done" or "created")
                .LastOrDefault();
            if (lastResult != null && lastResult["needsExtraStep"] is false &&
                string.Equals(lastResult.GetValueOrDefault("path")?.ToString(), proposal.Step.File, StringComparison.OrdinalIgnoreCase))
            {
                var lastPath = lastResult.GetValueOrDefault("path")?.ToString() ?? "";
                var lastChange = lastResult.GetValueOrDefault("change")?.ToString() ?? "";
                completionNote = $"The previous step [{lastPath}] \"{lastChange}\" was verified complete (needsExtraStep=false). " +
                    "Your proposed step MUST address a GENUINELY DIFFERENT requirement from the original task, " +
                    "not a continuation or cleanup of already-completed work. If the task is done, return planComplete=true.";
                skipLlm = false; // force LLM validation
            }
            if (emitSse)
                await EmitLog(true, "info", $"Pre-validate plan event: planSoFar.Count={planSoFar.Count}, step.File={proposal.Step.File}", ct: ct);
            var proposedPlanItems = planSoFar.Select((s, idx) => new
            {
                File = s.File,
                Change = s.Change,
                Line = s.LineNumber,
                OldString = s.OldString,
                NewString = s.NewString,
                done = true
            }).ToList<object>().Concat(new object[]
            {
                new
                {
                    File = proposal.Step.File, Change = proposal.Step.Change,
                    Line = proposal.Step.LineNumber,
                    OldString = proposal.Step.OldString, NewString = proposal.Step.NewString,
                    done = false
                }
            }).ToList();
            if (emitSse)
            {
                await EmitLog(true, "info", $"Sending plan event with {proposedPlanItems.Count} item(s)", ct: ct);
                await SendSse(Response, "plan", new
                {
                    thinking = thinkingLog.ToString(),
                    summary = $"Proposing step {planSoFar.Count + 1}",
                    items = proposedPlanItems,
                    incremental = true
                }, ct);
            }
            var (valid, reason) = await ValidateIncrementalStepAsync(
                proposal.Step, prompt, discoveryContext, planSoFar, projectRoot, emitSse, ct,
                skipLlm: skipLlm, lastStepCompletionNote: completionNote);
            if (!valid)
            {
                var stepFb = $"REJECTED — [{proposal.Step.File}] {proposal.Step.Change} → {reason}";
                await EmitRejectedLog(emitSse,
                    $"Interleaved execution: rejected [{proposal.Step.File}] {proposal.Step.Change} — {reason}", stepFb, ct);
                rejectionFeedback.Add(stepFb);
                if (emitSse && planSoFar.Count > 0)
                {
                    var planSummary = $"Step {planSoFar.Count + 1} rejected — {reason}";
                    thinkingLog.AppendLine($"\n[{planSummary}]");
                    await SendSse(Response, "plan", new
                    {
                        thinking = thinkingLog.ToString(),
                        summary = planSummary,
                        items = planSoFar.Select((s, idx) => new
                        {
                            File = s.File,
                            Change = s.Change,
                            Line = s.LineNumber,
                            OldString = s.OldString,
                            NewString = s.NewString,
                            done = true
                        }).ToList(),
                        incremental = true
                    }, ct);
                }
                if (++regenAttempts >= MAX_STEP_REGEN_ATTEMPTS)
                {
                    consecutiveSlotFailures++;
                    rejectionFeedback.Clear();
                    regenAttempts = 0;
                    if (consecutiveSlotFailures >= 3)
                        throw new InvalidOperationException(
                            "Interleaved planner failed 3 slots in a row — attach the correct file(s) and retry.");
                    continue;
                }
                continue;
            }
            consecutiveSlotFailures = 0;
            var planningRounds = regenAttempts + 1;
            totalPlanningRounds += planningRounds;
            regenAttempts = 0;
            rejectionFeedback.Clear();
            var retryWord = planningRounds - 1 == 1 ? "retry" : "retries";
            await EmitLog(emitSse, planningRounds >= 3 ? "warn" : "metric",
                planningRounds > 1
                    ? $"📊 Step {planSoFar.Count + 1} planned in {planningRounds} rounds ({planningRounds - 1} {retryWord}) — [{proposal.Step.File}] {proposal.Step.Change}"
                    : $"📊 Step {planSoFar.Count + 1} planned in 1 round — [{proposal.Step.File}] {proposal.Step.Change}", ct: ct);
            if (HasConcreteEdit(proposal.Step))
                await EmitLog(emitSse, "bypass",
                    "⚡ Step accepted — planner supplied a concrete edit (oldString/newString, FORMAT C/D, or fullFile); executing it directly without further planning", ct: ct);
            if (proposal.AdditionalSteps?.Count > 0)
            {
                foreach (var extraStep in proposal.AdditionalSteps)
                {
                    if (extraStep.File != null && extraStep.File.Equals("_discover", StringComparison.OrdinalIgnoreCase))
                    {
                        if (exploredFiles.Add("_discover"))
                        {
                            await EmitLog(emitSse, "info", "Additional _discover step — running project-wide search…", ct: ct);
                            discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                        }
                        continue;
                    }
                    if (!planSoFar.Any(s => string.Equals(s.File, extraStep.File, StringComparison.OrdinalIgnoreCase) &&
                                            TokenOverlap(s.Change ?? "", extraStep.Change ?? "") > 0.35))
                    {
                        pendingSteps.Enqueue(extraStep);
                        await EmitLog(emitSse, "info",
                            $"Queued additional step for later: [{extraStep.File}] {extraStep.Change}", ct: ct);
                    }
                }
            }
            var stepToRun = proposal.Step;
            if (stepToRun != null)
            {
                var dupStep = planSoFar.FirstOrDefault(s =>
                    s.File != "_noop" &&
                    string.Equals(s.File, stepToRun.File, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(s.Change, stepToRun.Change, StringComparison.Ordinal) ||
                     (s.Change?.Length > 10 && stepToRun.Change?.Length > 10 &&
                      (s.Change.Contains(stepToRun.Change) || stepToRun.Change.Contains(s.Change)))) &&
                    ((!string.IsNullOrEmpty(s.OldString) && string.Equals(s.OldString, stepToRun.OldString, StringComparison.Ordinal)) ||
                     (!string.IsNullOrEmpty(s.NewString) && string.Equals(s.NewString, stepToRun.NewString, StringComparison.Ordinal)) ||
                     (!string.IsNullOrEmpty(s.TargetSymbol) && string.Equals(s.TargetSymbol, stepToRun.TargetSymbol, StringComparison.Ordinal))));
                if (dupStep != null)
                {
                    await EmitLog(emitSse, "info",
                        $"Plan complete — duplicate step in interleaved execution: [{stepToRun.File}] {stepToRun.Change} — nothing new to add",
                        ct: ct);
                    break;
                }
            }
            if (stepToRun != null)
            {
                planSoFar.Add(stepToRun);
                if (!string.IsNullOrWhiteSpace(proposal.Thinking))
                    thinkingLog.AppendLine($"Step {planSoFar.Count}: {proposal.Thinking}");
                var stepToRunIsConcrete = ShouldApplyDirectly(stepToRun);
                await EmitLog(emitSse, "info",
                    stepToRunIsConcrete
                        ? $"▶ Applying edits — Step {planSoFar.Count} — [{stepToRun.File}] {stepToRun.Change}"
                        : $"▶ Thinking for edit — Step {planSoFar.Count} — [{stepToRun.File}] {stepToRun.Change}", ct: ct);
                await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                    "_executing", stepToRunIsConcrete
                        ? $"Applying edits — Step {planSoFar.Count} — {stepToRun.Change}"
                        : $"Thinking for edit — Step {planSoFar.Count} — {stepToRun.Change}",
                    $"Completed {planSoFar.Count - 1} step(s) — {(stepToRunIsConcrete ? "applying edits for" : "thinking for edit on")} Step {planSoFar.Count}",
                    planSoFar.Count - 1, ct);
                await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                    summary: $"Interleaved execution — {planSoFar.Count} step(s) so far", score: 90);
                var singleStepPlan = new AgentPlan { Plan = new List<PlanStep> { stepToRun }, Summary = stepToRun.Change, Score = 90 };
                var beforeCount = allResults.Count;
                var preEditContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (stepToRun?.File != null && !AgentProjectUtilities.IsSpecialMarker(stepToRun.File))
                {
                    var fp = Path.GetFullPath(Path.Combine(projectRoot, (stepToRun.File ?? "").Replace('/', Path.DirectorySeparatorChar)));
                    if (System.IO.File.Exists(fp) && stepToRun.File != null) preEditContents[stepToRun.File] = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                }
                try
                {
                    await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, singleStepPlan, ct, allResults,
                        steeringContext: steeringContext, attachedFiles: attachedFiles, cardId: cardId,
                        replanBudget: new[] { 0 }, onActivity: planActivity,
                        skipLlmPreResolution: stepToRunIsConcrete);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await EmitLog(emitSse, "error",
                        $"⛔ Interleaved execution halted — step {planSoFar.Count} threw: {ex.Message}", ct: ct);
                    if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                    await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                        summary: $"Execution halted at step {planSoFar.Count + 1} — exception: {ex.Message}", score: 0,
                        append: false);
                    break;
                }
                var newResults = allResults.Skip(beforeCount).OfType<Dictionary<string, object?>>().ToList();
                var stepSucceeded = newResults.Any(r => r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created");
                await EmitLog(emitSse, "info",
                    $"DIAG: After ExecutePlan — stepSucceeded={stepSucceeded}, planSoFar.Count={planSoFar.Count}, newResults.Count={newResults.Count}",
                    ct: ct);
                discoveryContext = AppendWebResultsToDiscoveryContext(discoveryContext, newResults);
                var globalPlanIdx = planSoFar.Count - 1;
                foreach (var r in newResults)
                {
                    r["planItemIndex"] = globalPlanIdx;
                    if (emitSse && r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
                        await SendSse(Response, "step", r, ct);
                }
                await PersistBoardDataPlanStepAsync(cardId, globalPlanIdx, emitSse, ct);
                if (singleStepPlan.Plan.Count > 1)
                {
                    var chainIntact = true;
                    var anyNestedGeneration = false;
                    for (var si = 1; si < singleStepPlan.Plan.Count; si++)
                    {
                        var synthStep = singleStepPlan.Plan[si]!;
                        planSoFar.Add(synthStep);
                        await EmitLog(emitSse, "info",
                            $"▶ Resolving Edits for Step {planSoFar.Count} — [{synthStep.File}] {synthStep.Change}", ct: ct);
                        await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                            "_executing", $"Resolving Edits for Step {planSoFar.Count} — {synthStep.Change}",
                            $"Completed {planSoFar.Count - 1} step(s) — resolving edits for Step {planSoFar.Count}",
                            planSoFar.Count - 1, ct);
                        await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                            summary: $"Interleaved execution — {planSoFar.Count} step(s) so far (incl. auto)", score: 90);
                        var synthPlan = new AgentPlan
                        { Plan = new List<PlanStep> { synthStep }, Summary = synthStep.Change, Score = 90 };
                        var synthBefore = allResults.Count;
                        if (synthStep?.File != null && !AgentProjectUtilities.IsSpecialMarker(synthStep.File))
                        {
                            var sfp = Path.GetFullPath(Path.Combine(projectRoot, synthStep.File.Replace('/', Path.DirectorySeparatorChar)));
                            if (System.IO.File.Exists(sfp)) preEditContents[synthStep.File] = await System.IO.File.ReadAllTextAsync(sfp, Encoding.UTF8, ct);
                        }
                        try
                        {
                            await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, synthPlan, ct, allResults,
                                steeringContext: steeringContext, attachedFiles: attachedFiles, cardId: cardId,
                                replanBudget: new[] { 0 }, onActivity: planActivity);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            await EmitLog(emitSse, "error",
                                $"⛔ Auto-generated step {planSoFar.Count} threw: {ex.Message}", ct: ct);
                            if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                            chainIntact = false;
                            break;
                        }
                        var synthGlobalIdx = planSoFar.Count - 1;
                        if (synthGlobalIdx > 0)
                        {
                            var synthResults = allResults.Skip(synthBefore).OfType<Dictionary<string, object?>>().ToList();
                            foreach (var r in synthResults)
                            {
                                r["planItemIndex"] = synthGlobalIdx;
                            }
                        }
                        await PersistBoardDataPlanStepAsync(cardId, synthGlobalIdx, emitSse, ct);
                        if (synthPlan.Plan.Count > 1)
                            anyNestedGeneration = true;
                        if (synthStep!.File != null)
                            discoveryContext = await RefreshFileInDiscoveryContext(synthStep.File, discoveryContext, projectRoot, ct);
                    }
                    if (chainIntact && !anyNestedGeneration)
                    {
                        await EmitLog(emitSse, "info",
                            "Auto-generated follow-up chain exhausted — plan complete without further LLM round-trip", ct: ct);
                        break;
                    }
                }
                var touchedPaths = newResults
                    .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create")
                    // Only edits that actually stuck feed the next planning round — failed/reverted
                    // attempts must never appear as applied changes.
                    .Where(r => r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
                    .Select(r => r.GetValueOrDefault("path")?.ToString())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var stepDiffs = new List<string>();
                if (stepSucceeded)
                {
                    foreach (var touched in touchedPaths)
                    {
                        if (touched == null) continue;
                        var oldContent = preEditContents.GetValueOrDefault(touched);
                        var fp = Path.GetFullPath(Path.Combine(projectRoot, touched.Replace('/', Path.DirectorySeparatorChar)));
                        if (oldContent != null && System.IO.File.Exists(fp))
                        {
                            var newContent = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                            var diff = ComputeSimpleDiff(oldContent, newContent, touched);
                            if (!string.IsNullOrWhiteSpace(diff)) stepDiffs.Add(diff);
                        }
                    }
                }
                foreach (var touched in touchedPaths)
                    discoveryContext = await RefreshFileInDiscoveryContext(touched!, discoveryContext, projectRoot, ct);

                // After each step, inject a live directory listing for any directories that were
                // touched. This prevents the planner from hallucinating that a file was created
                // when it wasn't — it can see exactly what exists on disk.
                var affectedDirs = touchedPaths
                    .Select(p => Path.GetDirectoryName(p?.Replace('/', Path.DirectorySeparatorChar) ?? ""))
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var dir in affectedDirs)
                {
                    var fullDir = Path.GetFullPath(Path.Combine(projectRoot, dir!));
                    if (!Directory.Exists(fullDir)) continue;
                    var files = Directory.GetFiles(fullDir)
                        .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
                        .OrderBy(f => f)
                        .ToList();
                    if (files.Count == 0) continue;
                    var inventoryKey = $"### FILES IN {dir!.Replace('\\', '/')} (current state on disk) ###";
                    // Remove stale entry if present, then append fresh one
                    var staleIdx = discoveryContext.IndexOf(inventoryKey, StringComparison.Ordinal);
                    if (staleIdx >= 0)
                    {
                        var staleEnd = discoveryContext.IndexOf("\n### ", staleIdx + inventoryKey.Length, StringComparison.Ordinal);
                        discoveryContext = staleEnd >= 0
                            ? discoveryContext[..staleIdx] + discoveryContext[staleEnd..]
                            : discoveryContext[..staleIdx];
                    }
                    discoveryContext += $"\n{inventoryKey}\n" +
                        string.Join("\n", files.Select(f => $"  - {f}")) + "\n";
                }
                // Feed ONLY successful diffs into the bounded accumulator, then (optionally) LLM-
                // summarize once the accumulated section passes the user's threshold. One section,
                // replaced in place — never a growing stack of raw diffs.
                if (stepDiffs.Count > 0)
                {
                    diffContextAccum.AppendLine(string.Join("\n", stepDiffs));
                }
                var diffCfg = await LoadConfigAsync();
                if (diffCfg.summarizeDiffContext && diffContextAccum.Length > diffCfg.diffContextSummaryChars)
                {
                    var summary = await SummarizeDiffContextAsync(diffContextAccum.ToString(), emitSse, ct);
                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        diffContextAccum.Clear();
                        diffContextAccum.Append(summary);
                        await EmitLog(emitSse, "metric",
                            $"📊 Diff context summarized: accumulated diffs ({diffCfg.diffContextSummaryChars}+ chars) → compact summary", ct: ct);
                    }
                }
                if (diffContextAccum.Length > 0)
                {
                    var diffSection = "\n### CHANGES FROM PREVIOUS STEP ###\n" +
                        diffContextAccum.ToString().TrimEnd() + "\n" +
                        "(These changes are already reflected in the file content above. " +
                        "The next step MUST build on them, not repeat them.)\n";
                    discoveryContext = ReplaceDiscoveryDiffSection(discoveryContext, diffSection);
                }
                var newEditLogLines = newResults
                    .Where(r => r.GetValueOrDefault("type")?.ToString() == "edit" &&
                                r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
                    .Select(r => $"  · {r.GetValueOrDefault("path")}: {r.GetValueOrDefault("change") ?? r.GetValueOrDefault("editAction") ?? "modified"}")
                    .ToList();
                if (newEditLogLines.Count > 0)
                {
                    var fullLog = "### EDIT LOG (changes applied — do NOT repeat them) ###\n" +
                        string.Join("\n", newEditLogLines) + "\n";
                    if (!discoveryContext.Contains(fullLog))
                        discoveryContext += "\n" + fullLog;
                }
                var editLog = newResults
        .Where(r => r.GetValueOrDefault("type")?.ToString() == "edit" &&
                    r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
        .Select(r => $"  {r.GetValueOrDefault("path")} — " +
            (r.GetValueOrDefault("change")?.ToString() ?? r.GetValueOrDefault("editAction")?.ToString() ?? "modified"))
        .ToList();
                if (editLog.Count > 0)
                {
                    var logSection = "\n### EDIT LOG (changes applied in previous steps) ###\n" +
                        string.Join("\n", editLog) + "\n";
                    discoveryContext += logSection;
                }
                var hadFailure = newResults.Any(r =>
                    r.GetValueOrDefault("status")?.ToString() == "error" ||
                    r.GetValueOrDefault("type")?.ToString() == "plan_halted");
                if (!stepSucceeded || hadFailure)
                {
                    if (!hadFailure)
                    {
                        await EmitLog(emitSse, "warn",
                            $"Step {planSoFar.Count} produced no code changes — skipping and continuing to next step.", ct: ct);
                        // Remove the no-op step from planSoFar so the planner can propose something else.
                        // Do NOT break — a no-op is not a fatal failure; keep planning.
                        if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                        continue;
                    }
                    else
                    {
                        await EmitLog(emitSse, "warn",
                            $"Step {planSoFar.Count} did not complete successfully — stopping interleaved execution here " +
                            "so post-execution verification can assess what genuinely remains.", ct: ct);
                        if (planSoFar.Count > 0) planSoFar.RemoveAt(planSoFar.Count - 1);
                        await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                            summary: $"Execution halted at step {planSoFar.Count + 1} — step failed", score: 0,
                            append: false);
                        if (emitSse)
                            await SendSse(Response, "plan-halted", new
                            {
                                reason = "Step produced an error",
                                failedStep = stepToRun?.File,
                                failedChange = stepToRun?.Change,
                                remainingSteps = 0
                            }, ct);
                        break;
                    }
                }
                var needsExtraResult = newResults
                    .OfType<Dictionary<string, object?>>()
                    .FirstOrDefault(r => r.GetValueOrDefault("needsExtraStep") is true);
                if (needsExtraResult != null && !hadFailure)
                {
                    var extraReason = needsExtraResult.GetValueOrDefault("extraStepReason")?.ToString();
                    var extraFile = needsExtraResult.GetValueOrDefault("extraStepFile")?.ToString()
                                    ?? needsExtraResult.GetValueOrDefault("path")?.ToString()
                                    ?? stepToRun?.File ?? "";
                    var missingSymbolMatch = Regex.Match(extraReason ?? "",
                        @"(?:missing\s+(?:method|property|function)\s*)[\(`]?(?:vm\.)?(\w+)[\)`]?");
                    var missingSymbol = missingSymbolMatch.Success ? missingSymbolMatch.Groups[1].Value : null;
                    if (!string.IsNullOrWhiteSpace(missingSymbol))
                    {
                        var knownBuiltIns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "preventDefault", "stopPropagation", "console", "event", "$event"
                    };
                        if (knownBuiltIns.Contains(missingSymbol))
                        {
                            await EmitLog(emitSse, "warn",
                                $"Skipping verifier auto-step for built-in API: {missingSymbol}", ct: ct);
                        }
                        else
                        {
                            var autoStep = new PlanStep
                            {
                                File = extraFile!,
                                Change = $"Add {missingSymbol} method — referenced by previous step but not yet implemented. " +
                                         $"Verifier reason: {extraReason}",
                                TargetSymbol = missingSymbol,
                                Priority = 1,
                                LineNumber = 0,
                                ReferenceFiles = stepToRun?.ReferenceFiles ?? new List<string>()
                            };
                            var isDup = planSoFar.Any(s =>
                                string.Equals(s.File, autoStep.File, StringComparison.OrdinalIgnoreCase) &&
                                TokenOverlap(s.Change ?? "", autoStep.Change) > 0.5);
                            // Also skip when the method already exists on disk (stale-snapshot re-insertion).
                            if (!isDup && !string.IsNullOrWhiteSpace(autoStep.File))
                            {
                                var autoStepPath = System.IO.Path.GetFullPath(Path.Combine(projectRoot, autoStep.File.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                                if (System.IO.File.Exists(autoStepPath) &&
                                    MethodNameExistsInFile(await System.IO.File.ReadAllTextAsync(autoStepPath, Encoding.UTF8, ct), missingSymbol))
                                {
                                    await EmitLog(emitSse, "info",
                                        $"  ⏭ Verifier auto-step skipped: {missingSymbol}() already exists on disk in {autoStep.File}", ct: ct);
                                    isDup = true;
                                }
                            }
                            if (!isDup)
                            {
                                planSoFar.Add(autoStep);
                                await EmitLog(emitSse, "info",
                                    $"⚡ Verifier flagged needsExtraStep — auto-proposing next step WITHOUT planner LLM: " +
                                    $"[{autoStep.File}] {missingSymbol}()", ct: ct);
                                await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                                    "_executing", $"Resolving Edits for Step {planSoFar.Count} — {autoStep.Change}",
                                    $"Completed {planSoFar.Count - 1} step(s) — resolving edits for Step {planSoFar.Count} from verifier",
                                    planSoFar.Count - 1, ct);
                                await PersistBoardDataPlanAsync(cardId, planSoFar, emitSse, ct,
                                    summary: $"Interleaved execution — {planSoFar.Count} step(s)", score: 90);
                                var autoPlan = new AgentPlan
                                {
                                    Plan = new List<PlanStep> { autoStep },
                                    Summary = autoStep.Change,
                                    Score = 90
                                };
                                var autoBeforeCount = allResults.Count;
                                try
                                {
                                    await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, autoPlan, ct, allResults,
                                        steeringContext: steeringContext, attachedFiles: attachedFiles, cardId: cardId,
                                        replanBudget: new[] { 0 }, onActivity: planActivity);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    await EmitLog(emitSse, "error",
                                        $"⛔ Auto-step from verifier threw: {ex.Message}", ct: ct);
                                }
                                var autoResults = allResults.Skip(autoBeforeCount)
                                    .OfType<Dictionary<string, object?>>()
                                    .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create")
                                    .Select(r => r.GetValueOrDefault("path")?.ToString())
                                    .Where(p => !string.IsNullOrWhiteSpace(p))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                                foreach (var touched in autoResults)
                                    discoveryContext = await RefreshFileInDiscoveryContext(touched!, discoveryContext, projectRoot, ct);
                                continue;
                            }
                        }
                    }
                }
                // ── Between-steps completion verification ──────────────────────────────────
                // The per-step edit verifier marked the last edit complete (needsExtraStep=false).
                // Before proposing yet ANOTHER step, verify whether the WHOLE task is now satisfied
                // by the original prompt + all applied changes. If it is, declare the plan complete
                // instead of blindly planning a redundant follow-up step.
                if (stepSucceeded && !hadFailure && IsLastEditVerifiedComplete(newResults))
                {
                    await EmitLog(emitSse, "info",
                        "🔍 Between-steps verification: last edit verified complete — checking whether the whole task is done…", ct: ct);
                    if (emitSse)
                        await SendPlanActivityEventAsync(thinkingLog, planSoFar, emitSse,
                            "_verifying", $"Verifying whole task — checking if the plan is complete after Step {planSoFar.Count}…",
                            $"Verifying whole task after Step {planSoFar.Count}…", planSoFar.Count - 1, ct);
                    var (isComplete, assessReason) = await AssessCompletion(
                        prompt, allResults, projectRoot, ct,
                        new AgentPlan { Plan = planSoFar.ToList(), Summary = "Interleaved verification", Score = 90 },
                        attachedFiles: attachedFiles, atomicStepEstimate: atomicStepEstimate);
                    // AssessCompletion now uses the configurable LLM timeout and retries once,
                    // so a slow local model can actually finish the assessment. If the assessment
                    // is STILL unavailable (LLM down / unparseable response), do NOT treat that
                    // as a hard "NOT complete" verdict that forces a redundant follow-up step:
                    // the per-step verifier already confirmed the last edit is complete
                    // (needsExtraStep=false) and no step failed — declare the plan complete
                    // instead of planning a meaningless step 2.
                    var shouldDeclareComplete = ShouldDeclarePlanCompleteAfterAssessment(
                        isComplete, assessReason, out var completeReason, out var assessFailed);
                    if (shouldDeclareComplete)
                    {
                        planCompleteDeclared = true;
                        await EmitLog(emitSse, assessFailed ? "warn" : "success",
                            $"✓ Plan complete after step {planSoFar.Count} — {completeReason}", ct: ct);
                        thinkingLog.AppendLine($"\n[Plan complete — {completeReason}]");
                        // Whole-task verified complete → clear any pending unmet-requirement state
                        // so a later restart does not re-plan work that is genuinely finished.
                        if (!string.IsNullOrWhiteSpace(cardId))
                            await PersistCardVerifyStateAsync(cardId, null, null, emitSse, ct);
                        if (emitSse)
                            await SendSse(Response, "plan", new
                            {
                                thinking = thinkingLog.ToString(),
                                summary = $"Plan complete — {completeReason}",
                                items = planSoFar.Select((s, idx) => new
                                {
                                    File = s.File, Change = s.Change, Line = s.LineNumber,
                                    OldString = s.OldString, NewString = s.NewString, done = true
                                }).ToList(),
                                incremental = true
                            }, ct);
                        break;
                    }
                    await EmitLog(emitSse, "metric",
                        $"🔍 Between-steps verification: task NOT complete yet — {assessReason}", ct: ct);
                    // Persist the unmet assessment onto the card so a crash/stop/restart re-plans
                    // the remaining requirement instead of trusting the all-done plan marks.
                    if (!string.IsNullOrWhiteSpace(cardId))
                        await PersistCardVerifyStateAsync(cardId, assessReason, null, emitSse, ct);
                }
            }
        }
        var finalPlan = new AgentPlan
        {
            Thinking = thinkingLog.ToString(),
            Summary = $"Executed {planSoFar.Count} atomic step(s) via interleaved plan → execute → verify loop",
            Score = 90,
            Plan = planSoFar
        };
        return (finalPlan, allResults, discoveryContext, planCompleteDeclared);
    }
    private async Task<string> RefreshFileInDiscoveryContext(
        string relPath, string discoveryContext, string projectRoot, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!System.IO.File.Exists(fullPath)) return discoveryContext;
        string content;
        try { content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct); }
        catch { return discoveryContext; }
        var normPath = relPath.Replace('\\', '/');
        var pattern = new Regex(
            $@"^###\s+(?:read\s+)?{Regex.Escape(normPath)}\b.*?(?=^### |\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        var replacement = $"### read {normPath}\n```\n{content}\n```\n\n";
        if (pattern.IsMatch(discoveryContext))
            return pattern.Replace(discoveryContext, m => replacement, 1);
        return discoveryContext.TrimEnd() + "\n\n" + replacement;
    }
    private static string ComputeSimpleDiff(string oldContent, string newContent, string relPath)
    {
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var oldSet = new HashSet<string>(oldLines, StringComparer.Ordinal);
        var newSet = new HashSet<string>(newLines, StringComparer.Ordinal);
        var added = newLines.Where(l => !string.IsNullOrWhiteSpace(l) && !oldSet.Contains(l)).ToList();
        var removed = oldLines.Where(l => !string.IsNullOrWhiteSpace(l) && !newSet.Contains(l)).ToList();
        if (added.Count == 0 && removed.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"  File: {relPath}");
        if (removed.Count > 0)
        {
            var removeSample = string.Join("\n    ", removed.Take(10));
            sb.AppendLine($"  Removed ({removed.Count} line(s)):\n    {removeSample}");
        }
        if (added.Count > 0)
        {
            var addSample = string.Join("\n    ", added.Take(10));
            sb.AppendLine($"  Added ({added.Count} line(s)):\n    {addSample}");
        }
        return sb.ToString();
    }
    /// <summary>
    /// Replaces the existing "### CHANGES FROM PREVIOUS STEP ###" section in the discovery
    /// context with a fresh one (appending it if none exists yet). Keeps exactly ONE bounded
    /// diff section instead of a growing stack of raw diffs per step.
    /// </summary>
    private static string ReplaceDiscoveryDiffSection(string discoveryContext, string newSection)
    {
        const string marker = "### CHANGES FROM PREVIOUS STEP ###";
        var idx = discoveryContext.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return discoveryContext.TrimEnd() + "\n" + newSection.TrimStart('\n');
        var sectionStart = discoveryContext.LastIndexOf("\n### ", idx, StringComparison.Ordinal);
        var start = sectionStart >= 0 ? sectionStart : discoveryContext.LastIndexOf('\n', idx);
        if (start < 0) start = 0;
        var sectionEnd = discoveryContext.IndexOf("\n### ", idx + marker.Length, StringComparison.Ordinal);
        var end = sectionEnd >= 0 ? sectionEnd : discoveryContext.Length;
        var prefix = discoveryContext[..start].TrimEnd();
        var suffix = discoveryContext[end..];
        return prefix + "\n" + newSection.TrimStart('\n') + "\n" + suffix.TrimStart('\n');
    }

    /// <summary>
    /// LLM compaction for the accumulated per-step diffs. Preserves file paths, symbols and the
    /// essence of each change so later steps can still build on them without the raw text.
    /// </summary>
    private async Task<string?> SummarizeDiffContextAsync(string accumulatedDiffs, bool emitSse, CancellationToken ct)
    {
        var system =
            "You are a context-compaction engine for a coding agent. Below is a raw accumulation of " +
            "code diffs from previous steps of the same task. Rewrite them as ONE compact, high-signal " +
            "summary that preserves: every file path touched, every symbol/method/class added or changed, " +
            "and the essence of each change — enough for the next step to build on them without the raw " +
            "diff text. Keep it under ~1800 chars. Output ONLY the summary text; no markdown fences, no JSON.";
        var user = $"### ACCUMULATED DIFFS ###\n{accumulatedDiffs}";
        var (raw, error) = await CallLlmRawText(system, user, false, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 512);
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim();
        if (cleaned.Length < 20) return null;
        return cleaned.Length > 4000 ? cleaned[..4000] + "\n…[summarized]…" : cleaned;
    }

    /// <summary>
    /// LLM compaction for the accumulated pre-plan thinking carried between steps. Produces a
    /// dense recap (task, files, symbols added/renamed, anchors, decisions, what remains) so the
    /// next thinking round builds on a summary instead of the raw wall of reasoning.
    /// </summary>
    private async Task<string?> CompactThinkingContextAsync(string accumulatedThinking, bool emitSse, CancellationToken ct)
    {
        var system =
            "You are the memory-compaction engine of a multi-step coding agent. Below is the accumulated " +
            "PREVIOUS REASONING from earlier planning steps of the SAME task. Rewrite it as ONE dense " +
            "recap that preserves everything the NEXT thinking round must know: the task, every file " +
            "touched, every symbol/method/class added or renamed, anchors used, decisions made, and what " +
            "remains to do. Keep it under ~1800 chars. Output ONLY the recap prose; no markdown fences, no JSON.";
        var user = $"### ACCUMULATED PREVIOUS REASONING (compact this) ###\n{accumulatedThinking}";
        var (raw, error) = await CallLlmRawText(system, user, false, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 512);
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim();
        if (cleaned.Length < 20) return null;
        return cleaned.Length > 4000 ? cleaned[..4000] + "\n…[compacted]…" : cleaned;
    }

    private async Task<IncrementalSubPlanProposal?> ProposeNextSubPlanAsync(
        string originalPrompt, string discoveryContext, List<MetaPlanSubPlan> subPlansSoFar,
        List<string> rejectionFeedback, bool emitSse, CancellationToken ct)
    {
        var sys = BuildIncrementalSubPlanSystemPrompt();
        var user = BuildIncrementalSubPlanUserPrompt(originalPrompt, discoveryContext, subPlansSoFar, rejectionFeedback);
        var (raw, _, err) = await CallLlmRawStreaming(sys, user, emitSse, ct, _infiniteTimeout, maxTokens: 500);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            var complete = root.TryGetProperty("metaPlanComplete", out var mc) && mc.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("completionReason", out var cr) ? cr.GetString() : null;
            var thinking = root.TryGetProperty("thinking", out var th) ? th.GetString() : null;
            if (complete)
                return new IncrementalSubPlanProposal { MetaPlanComplete = true, CompletionReason = reason, Thinking = thinking };
            if (!root.TryGetProperty("subPlan", out var spEl) || spEl.ValueKind != JsonValueKind.Object)
                return new IncrementalSubPlanProposal { MetaPlanComplete = false, Thinking = thinking };
            var title = spEl.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
            var desc = spEl.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;
            var note = spEl.TryGetProperty("contextNote", out var nEl) ? nEl.GetString() : "";
            var files = new List<string>();
            if (spEl.TryGetProperty("files", out var fArr) && fArr.ValueKind == JsonValueKind.Array)
                foreach (var f in fArr.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()))
                        files.Add(f.GetString()!);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(desc))
                return new IncrementalSubPlanProposal { MetaPlanComplete = false, Thinking = thinking };
            return new IncrementalSubPlanProposal
            {
                MetaPlanComplete = false,
                Thinking = thinking,
                SubPlan = new MetaPlanSubPlan
                {
                    Id = $"sp-{subPlansSoFar.Count + 1}",
                    Title = title!,
                    Description = desc!,
                    ContextNote = note ?? "",
                    Files = files
                }
            };
        }
        catch { return null; }
    }
    private async Task<(bool valid, string? reason)> ValidateSubPlanAsync(
        MetaPlanSubPlan subPlan, string originalPrompt, List<MetaPlanSubPlan> subPlansSoFar, CancellationToken ct)
    {
        foreach (var existing in subPlansSoFar)
        {
            var sim = CalculateChangeSimilarity(NormalizeChangeForDedup(subPlan.Description), NormalizeChangeForDedup(existing.Description));
            if (sim >= 0.82)
                return (false, $"Duplicates already-committed stage '{existing.Title}'.");
        }
        var sb = new StringBuilder();
        sb.AppendLine("### ORIGINAL TASK ###"); sb.AppendLine(originalPrompt);
        sb.AppendLine();
        sb.AppendLine("### STAGES SO FAR ###");
        if (subPlansSoFar.Count == 0) sb.AppendLine("(none)");
        else foreach (var s in subPlansSoFar) sb.AppendLine($"  - {s.Title}: {s.Description}");
        sb.AppendLine();
        sb.AppendLine("### PROPOSED NEXT STAGE ###");
        sb.AppendLine($"{subPlan.Title}: {subPlan.Description}");
        sb.AppendLine();
        sb.AppendLine("Judge ONLY the proposed next stage. Is it atomic (not two concerns split apart, e.g. table " +
                      "creation split from its endpoint)? Does it depend on something not yet staged (e.g. an endpoint " +
                      "before its DTO)? Is it genuinely required by the task (not scope creep)?");
        sb.AppendLine("Output ONLY JSON: {\"valid\": true|false, \"reason\": \"short reason if invalid\"}");
        var (raw, _, _) = await CallLlmRaw(
            "You are a strict meta-plan coherence validator. Output ONLY the requested JSON.",
            sb.ToString(), ct, _infiniteTimeout, maxTokens: 200);
        if (string.IsNullOrWhiteSpace(raw)) return (true, null);
        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var valid = !doc.RootElement.TryGetProperty("valid", out var v) || v.ValueKind != JsonValueKind.False;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return (valid, valid ? null : reason);
        }
        catch { return (true, null); }
    }
    private async Task<MetaPlanResult?> RunIncrementalMetaPlanLoop(
        string prompt, string discoveryContext, string projectRoot, bool emitSse, CancellationToken ct,
        string? cardId = null)
    {
        var (skipMetaPlan, gateScore) = DeterministicMetaPlanGate(prompt);
        if (skipMetaPlan)
        {
            await EmitLog(emitSse, "info", "Meta-plan: deprecated — disabled permanently.", ct: ct);
            return null;
        }
        var subPlansSoFar = new List<MetaPlanSubPlan>();
        var rejectionFeedback = new List<string>();
        var attempts = 0;
        for (var turn = 0; turn < MAX_INCREMENTAL_SUBPLANS; turn++)
        {
            ct.ThrowIfCancellationRequested();
            var proposal = await ProposeNextSubPlanAsync(prompt, discoveryContext, subPlansSoFar, rejectionFeedback, emitSse, ct);
            if (proposal == null)
            {
                if (++attempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            if (proposal.MetaPlanComplete)
            {
                await EmitLog(emitSse, "success", $"Meta-plan: complete after {subPlansSoFar.Count} stage(s) — {proposal.CompletionReason}", ct: ct);
                break;
            }
            if (proposal.SubPlan == null)
            {
                await EmitRejectedLog(emitSse, "Meta-plan: rejected — response contained no subPlan; retrying", "Response contained no subPlan object — must return planComplete or subPlan.", ct);
                if (++attempts >= MAX_STEP_REGEN_ATTEMPTS) break;
                continue;
            }
            var (valid, reason) = await ValidateSubPlanAsync(proposal.SubPlan, prompt, subPlansSoFar, ct);
            if (!valid)
            {
                await EmitRejectedLog(emitSse, $"Meta-plan: rejected stage '{proposal.SubPlan.Title}' — {reason}", reason, ct);
                rejectionFeedback.Add($"REJECTED — '{proposal.SubPlan.Title}' → {reason}");
                if (++attempts >= MAX_STEP_REGEN_ATTEMPTS) { rejectionFeedback.Clear(); attempts = 0; }
                continue;
            }
            subPlansSoFar.Add(proposal.SubPlan);
            rejectionFeedback.Clear();
            attempts = 0;
            await EmitLog(emitSse, "info", $"Meta-plan: committed stage {subPlansSoFar.Count} — {proposal.SubPlan.Title}", ct: ct);
        }
        if (subPlansSoFar.Count <= 1)
            return null;
        var result = new MetaPlanResult
        {
            MetaThinking = "Built incrementally, one validated stage at a time.",
            MetaSummary = $"{subPlansSoFar.Count}-stage plan built incrementally",
            Complexity = Math.Min(10, 6 + subPlansSoFar.Count),
            SubPlans = subPlansSoFar
        };
        if (emitSse)
            await SendSse(Response, "meta-plan", new
            {
                summary = result.MetaSummary,
                complexity = result.Complexity,
                subPlans = result.SubPlans.Select(sp => new { id = sp.Id, title = sp.Title, description = sp.Description, files = sp.Files, contextNote = sp.ContextNote, done = false })
            }, ct);
        if (!string.IsNullOrWhiteSpace(cardId))
            await PersistMetaPlanToCardAsync(cardId, result, emitSse, ct);
        return result;
    }
    /// <summary>
    /// Persists the "verification still has work to do" state onto a card as _verifyPending
    /// ({reason, issues[], updatedAt}). Calling with a null/empty reason and no issues REMOVES
    /// the property (task verified complete). The resume path reads this so a restart re-plans
    /// the unmet requirements instead of declaring the plan complete just because every
    /// persisted plan step is already marked done.
    /// </summary>
    private async Task PersistCardVerifyStateAsync(string? cardId, string? reason, List<string>? issues, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            return;
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;
            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;
                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;
                    var hasContent = !string.IsNullOrWhiteSpace(reason) || (issues != null && issues.Count > 0);
                    if (hasContent)
                    {
                        cardObj["_verifyPending"] = new JsonObject
                        {
                            ["reason"] = reason ?? "",
                            ["issues"] = new JsonArray((issues ?? new List<string>()).Select(i => JsonValue.Create(i)).ToArray()),
                            ["updatedAt"] = DateTime.UtcNow.ToString("o")
                        };
                    }
                    else
                    {
                        cardObj.Remove("_verifyPending");
                    }
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                        await SendSse(Response, "refresh", new { target = "boarddata", reason = "verify-state-updated", cardId }, ct);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist card verify state", new { cardId, error = ex.Message });
        }
    }

    /// <summary>Reads the card's _verifyPending blob (unmet requirements from the last
    /// verification). Returns (reason, issues) — both empty when absent.</summary>
    private async Task<(string? reason, List<string> issues)> LoadCardVerifyStateAsync(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            return (null, new List<string>());
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return (null, new List<string>());
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return (null, new List<string>());
            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;
                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;
                    if (cardObj["_verifyPending"] is not JsonObject vp)
                        return (null, new List<string>());
                    var reason = vp["reason"]?.GetValue<string>() ?? "";
                    var issues = new List<string>();
                    if (vp["issues"] is JsonArray arr)
                        foreach (var i in arr)
                            if (i?.GetValue<string>() is string s && !string.IsNullOrWhiteSpace(s))
                                issues.Add(s);
                    return (string.IsNullOrWhiteSpace(reason) ? null : reason, issues);
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to load card verify state", new { cardId, error = ex.Message });
        }
        return (null, new List<string>());
    }
    private async Task<(AgentPlan? plan, HashSet<int>? completedIndices, bool isBenchmark)> LoadPlanFromBoardDataAsync(string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            return (null, null, false);
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return (null, null, false);
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return (null, null, false);
            var columns = new[] { "todo", "doing", "done", "selfImproving" };
            foreach (var column in columns)
            {
                if (!root.TryGetPropertyValue(column, out var columnNode) || columnNode is not JsonArray columnItems)
                    continue;
                foreach (var item in columnItems)
                {
                    if (item is not JsonObject cardObj || cardObj["id"]?.GetValue<string>() != cardId)
                        continue;
                    var isBenchmark = cardObj["_benchmark"]?.GetValue<bool>() ?? false;
                    if (cardObj["_plan"] is not JsonObject planObj)
                        continue;
                    var itemsArr = planObj["items"] as JsonArray;
                    if (itemsArr == null || itemsArr.Count == 0)
                        continue;
                    var steps = new List<PlanStep>();
                    var completed = new HashSet<int>();
                    for (var i = 0; i < itemsArr.Count; i++)
                    {
                        if (itemsArr[i] is not JsonObject si) continue;
                        var step = new PlanStep
                        {
                            File = si["file"]?.GetValue<string>() ?? "",
                            Change = si["change"]?.GetValue<string>() ?? "",
                            Priority = si["priority"]?.GetValue<int>() ?? 1,
                            OldString = si["oldString"]?.GetValue<string>() ?? "",
                            NewString = si["newString"]?.GetValue<string>() ?? "",
                            LineNumber = si["line"]?.GetValue<int>() ?? 0
                        };
                        var idx = si["index"]?.GetValue<int>() ?? i;
                        steps.Add(step);
                        var done = si["done"]?.GetValue<bool>() ?? false;
                        if (done) completed.Add(idx);
                    }
                    if (steps.Count == 0) return (null, null, isBenchmark);
                    var plan = new AgentPlan
                    {
                        Summary = planObj["summary"]?.GetValue<string>() ?? "",
                        Plan = steps
                    };
                    return (plan, completed.Count > 0 ? completed : null, isBenchmark);
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to load plan from board data", new { cardId, error = ex.Message });
        }
        return (null, null, false);
    }
    private sealed class IncrementalStepProposal
    {
        public bool PlanComplete { get; set; }
        public string? CompletionReason { get; set; }
        public string? Thinking { get; set; }
        public string? ExploreFile { get; set; }
        public PlanStep? Step { get; set; }
        public List<PlanStep>? AdditionalSteps { get; set; }
    }
    private sealed class IncrementalSubPlanProposal
    {
        public bool MetaPlanComplete { get; set; }
        public string? CompletionReason { get; set; }
        public string? Thinking { get; set; }
        public MetaPlanSubPlan? SubPlan { get; set; }
    }
}
