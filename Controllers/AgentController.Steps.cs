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
    private async Task<List<object>> ExecuteSteps(
        List<AgentStep> steps, string projectRoot, int indexOffset, bool emitSse,
        CancellationToken ct = default)
    {
        var results = new List<object>();
        var terminalStarted = false;
        var editContentCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            var displayIndex = indexOffset + step.Index;
            var result = new Dictionary<string, object?>
            {
                ["index"] = displayIndex,
                ["type"] = step.Type,
                ["description"] = step.Description,
                ["status"] = "running"
            };
            if (emitSse)
            {
                var label = step.Description ?? step.Path ?? step.Command ?? step.Query ?? step.Pattern ?? "";
                await EmitLog(emitSse, "step", $"▶ {step.Type}: {label}", new { result }, ct: ct);
                await SendSse(Response, "step", result, ct);
            }
            try
            {
                switch (step.Type?.ToLowerInvariant())
                {
                    case "edit": await ExecuteEditStep(step, projectRoot, result, editContentCache); break;
                    case "command": if (!terminalStarted) { _terminal.Start(); terminalStarted = true; } await ExecuteCommandStep(step, projectRoot, result, emitSse, ct); break;
                    case "rename": await ExecuteRenameStep(step, projectRoot, result); break;
                    case "read": await ExecuteReadStep(step, projectRoot, result); break;
                    case "list": await ExecuteListStep(step, projectRoot, result); break;
                    case "glob": await ExecuteGlobStep(step, projectRoot, result); break;
                    case "grep": await ExecuteGrepStep(step, projectRoot, result); break;
                    case "web": case "web_search": case "web_fetch": await ExecuteWebStep(step, result); break;
                    default: result["status"] = "error"; result["error"] = $"Unknown step type: {step.Type}"; break;
                }
                await EmitLog(true, "log", $"Raw {step.Type?.ToLowerInvariant()} Result", result, ct);
            }
            catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
            result["status"] = AgentTextUtilities.NormalizeUiStatus(result["status"]?.ToString());
            results.Add(result);
            if (emitSse)
            {
                var st = result["status"]?.ToString() ?? "?";
                var outputRaw = result.GetValueOrDefault("output")?.ToString();
                var outputPreview = outputRaw != null && outputRaw.Length > 200 ? outputRaw[..200] + "…" : outputRaw;
                await EmitLog(emitSse, st == "error" ? "error" : "info", $"✓ {step.Type} ({st})",
                    new { path = result.GetValueOrDefault("path"), error = result.GetValueOrDefault("error"), output = outputPreview }, ct: ct);
                // The result dict keeps its FULL output (it flows into allResults → agent context),
                // but the browser gets a capped copy so a multi-megabyte fetched page can't bloat
                // the step card or choke SSE JSON parsing.
                object clientResult = result;
                if (result.GetValueOrDefault("type")?.ToString() is "web" or "web_search" or "web_fetch")
                {
                    var (cappedOut, cappedTrunc) = CapWebStepOutputForClient(outputRaw);
                    clientResult = new Dictionary<string, object?>(result)
                    {
                        ["output"] = cappedOut,
                        ["truncated"] = cappedTrunc
                    };
                }
                await SendSse(Response, "step", clientResult, ct);
            }
        }
        return results;
    }
    private async Task<List<object>> ExecuteDiscoveryStepsConcurrent(
        List<AgentStep> steps, string projectRoot, int indexOffset, bool emitSse,
        Func<string, List<string>, string, (string snippet, string? focusIds)>? focusReader = null,
        List<string>? focusTokens = null)
    {
        var count = steps.Count;
        var results = new Dictionary<string, object?>[count];
        for (var i = 0; i < count; i++)
        {
            var step = steps[i];
            var displayIndex = indexOffset + step.Index;
            var result = new Dictionary<string, object?>
            { ["index"] = displayIndex, ["type"] = step.Type, ["description"] = step.Description, ["status"] = "running" };
            results[i] = result;
            if (emitSse)
            {
                await EmitLog(emitSse, "step", $"▶ {step.Type}: {step.Description ?? step.Path ?? ""}");
                await SendSse(Response, "step", result);
            }
        }
        var tasks = steps.Select((step, i) => Task.Run(async () =>
        {
            var result = results[i];
            try
            {
                switch (step.Type?.ToLowerInvariant())
                {
                    case "list": await ExecuteListStep(step, projectRoot, result); break;
                    case "grep": await ExecuteGrepStep(step, projectRoot, result); break;
                    case "read": await ExecuteReadStep(step, projectRoot, result); break;
                    default: result["status"] = "error"; result["error"] = $"Unknown: {step.Type}"; break;
                }
            }
            catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
            await EmitLog(true, "log", "Raw Discovery Step Result", result);
            result["status"] = AgentTextUtilities.NormalizeUiStatus(result["status"]?.ToString());
        }));
        await Task.WhenAll(tasks);
        // Focused reads: when a focusReader is supplied, large files whose identifiers
        // matched INSIDE get the enclosing-region snippet attached to the step event
        // (focusedOutput + matched focusIds + focused flag), so the UI can show the
        // region with a collapse/expand affordance instead of dumping the whole file.
        AgentDiscovery.AttachFocusedRegions(results, steps, focusReader, focusTokens);
        for (var i = 0; i < count; i++)
        {
            if (emitSse)
            {
                var st = results[i]["status"]?.ToString() ?? "?";
                await EmitLog(emitSse, st == "error" ? "error" : "info", $"✓ {steps[i].Type} ({st})", new { path = results[i].GetValueOrDefault("path"), error = results[i].GetValueOrDefault("error") });
                await SendSse(Response, "step", results[i]);
            }
        }
        return results.Cast<object>().ToList();
    }
    private async Task<List<PlanStep>> ReflectOnAppliedEditAsync(
        string relPath,
        string newStr,
        string fullFileContent,
        string projectRoot,
        List<PlanStep> existingPlanSteps,
        bool emitSse,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newStr)) return new List<PlanStep>();
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var codeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".js", ".tsx", ".jsx", ".html" };
        if (!codeExts.Contains(ext)) return new List<PlanStep>();
        var candidates = ExtractReferencedSymbolsFromCode(newStr, ext);
        if (candidates.Count == 0) return new List<PlanStep>();
        var toCheck = candidates
            .Where(sym => !fullFileContent.Contains(sym, StringComparison.Ordinal))
            .Where(sym => !existingPlanSteps.Any(s =>
                s.Change?.Contains(sym, StringComparison.OrdinalIgnoreCase) == true))
            .Distinct()
            .Take(12)
            .ToList();
        if (toCheck.Count == 0) return new List<PlanStep>();
        var grepCtx = new StringBuilder();
        var missing = new List<string>();
        foreach (var sym in toCheck)
        {
            ct.ThrowIfCancellationRequested();
            var (foundIn, snippet) = await GrepProjectForDefinitionAsync(
                projectRoot, sym, relPath, ct);
            if (foundIn != null)
                grepCtx.AppendLine($"  '{sym}' → found in {foundIn}: {snippet}");
            else
                missing.Add(sym);
        }
        if (missing.Count == 0)
        {
            await EmitLog(emitSse, "info",
                $"  ✓ Reflection: all {toCheck.Count} referenced symbol(s) already defined", ct: ct);
            return new List<PlanStep>();
        }
        await EmitLog(emitSse, "info",
            $"  🔍 Reflection: {missing.Count} potentially missing symbol(s): {string.Join(", ", missing)}", ct: ct);
        var sb = new StringBuilder();
        sb.AppendLine($"FILE JUST EDITED: {relPath}");
        sb.AppendLine();
        sb.AppendLine("NEW CODE ADDED:");
        sb.AppendLine("```");
        sb.AppendLine(newStr.Length > 2500 ? newStr[..2500] + "\n// ... (truncated)" : newStr);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("SYMBOLS REFERENCED BUT NOT FOUND IN THE PROJECT (via grep):");
        foreach (var sym in missing)
            sb.AppendLine($"  - {sym}");
        sb.AppendLine();
        if (grepCtx.Length > 0)
        {
            sb.AppendLine("SYMBOLS THAT WERE FOUND (for context):");
            sb.AppendLine(grepCtx.ToString());
        }
        sb.AppendLine("CURRENT PLAN STEPS (do NOT duplicate any of these):");
        foreach (var step in existingPlanSteps.Take(8))
            sb.AppendLine($"  - {step.File}: {step.Change}");
        sb.AppendLine();
        sb.AppendLine("TASK: For each missing symbol that genuinely needs to be implemented:");
        sb.AppendLine("  1. Decide which file it belongs in (same file, or a companion .ts/.cs file)");
        sb.AppendLine("  2. Write one specific plan step to implement it");
        sb.AppendLine("Do NOT create steps for standard library items, Angular lifecycle hooks,");
        sb.AppendLine("or anything where the absence is intentional (e.g. a placeholder).");
        sb.AppendLine("If nothing is actually missing, return {\"steps\": []}.");
        sb.AppendLine();
        sb.AppendLine("Output ONLY JSON (no markdown):");
        sb.AppendLine("{\"steps\": [{\"file\": \"rel/path.ext\", \"change\": \"precise description\"}]}");
        var (raw, _, _) = await CallLlmRaw(
            "You detect missing code implementations after an edit. Output ONLY JSON.",
            sb.ToString(), ct, _infiniteTimeout, maxTokens: 512);
        if (string.IsNullOrWhiteSpace(raw)) return new List<PlanStep>();
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("steps", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return new List<PlanStep>();
            var result = new List<PlanStep>();
            foreach (var el in arr.EnumerateArray())
            {
                var file = el.TryGetProperty("file", out var f) ? f.GetString() : null;
                var change = el.TryGetProperty("change", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change)) continue;
                var changePrefix = change[..Math.Min(40, change.Length)];
                if (existingPlanSteps.Any(s =>
                    string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase) &&
                    (s.Change ?? "").Contains(changePrefix, StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.Add(new PlanStep { File = file, Change = change, Priority = 1 });
            }
            return result;
        }
        catch { return new List<PlanStep>(); }
    }
    private static List<string> ExtractReferencedSymbolsFromCode(string code, string ext)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        if (ext is ".html" or ".htm")
        {
            foreach (Match m in Regex.Matches(code,
                @"\(\w+\)=""([A-Za-z_]\w*)\s*\("))
                symbols.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(code,
                @"\*ngFor=""let \w+ of ([A-Za-z_]\w*)"))
                symbols.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(code,
                @"\[[\w-]+\]=""([A-Za-z_]\w*)"))
                symbols.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(code,
                @"\[\(ngModel\)\]=""([A-Za-z_]\w*)"))
                symbols.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(code,
                @"\{\{\s*([A-Za-z_]\w*)\s*(?:\||\}\})"))
                symbols.Add(m.Groups[1].Value);
        }
        else if (ext is ".ts" or ".js" or ".tsx" or ".jsx")
        {
            foreach (Match m in Regex.Matches(code, @"this\.([A-Za-z_]\w*)\b"))
                symbols.Add(m.Groups[1].Value);
        }
        else if (ext == ".cs")
        {
            foreach (Match m in Regex.Matches(code, @"\bthis\.([A-Za-z_]\w*)\s*[(\[]"))
                symbols.Add(m.Groups[1].Value);
        }
        var builtins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ngOnInit","ngOnDestroy","ngAfterViewInit","ngOnChanges","ngDoCheck",
        "ngAfterContentInit","ngAfterContentChecked","ngAfterViewChecked","constructor",
        "length","name","value","type","id","url","href","src","target","key","index",
        "push","pop","shift","unshift","splice","slice","map","filter","reduce","find",
        "some","every","includes","indexOf","join","split","trim","toLowerCase","toUpperCase",
        "toString","parseInt","parseFloat","JSON","Math","Object","Array","String","Number",
        "Boolean","Promise","console","log","error","warn","Date","Error","typeof","instanceof",
        "subscribe","next","error","complete","pipe","tap","catchError","takeUntil",
        "ngModel","ngClass","ngStyle","ngIf","ngFor","ngSwitch","trackBy","async",
        "markForCheck","detectChanges","emit","getValue","patchValue","reset","get","set",
        "ToString","GetType","Equals","GetHashCode","Dispose","Task","List","Dictionary",
        "Console","String","Int32","Boolean","DateTime","Guid","Path","File","Directory",
    };
        return symbols
            .Where(s => s.Length >= 3 && !builtins.Contains(s) && !char.IsUpper(s[0]))
            .Distinct()
            .ToList();
    }
    private async Task<List<string>> RunCohesionCheckAsync(
        string relPath, string fileContent, string projectRoot, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileContent) || string.IsNullOrWhiteSpace(relPath))
            return new List<string>();
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var codeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".js", ".tsx", ".jsx", ".html", ".css", ".scss", ".json" };
        if (!codeExts.Contains(ext)) return new List<string>();
        var staticIssues = new List<string>();
        if (ext is ".ts" or ".tsx" or ".js" or ".jsx")
        {
            var lines = fileContent.Split('\n');
            var topLevelFns = new List<(int line, string name, int indent)>();
            var indentWidth = AgentMethodInventory.DetectIndentWidth(fileContent);
            if (indentWidth <= 0) indentWidth = 2;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (Regex.IsMatch(trimmed, @"^(?:vm\.)?\w+\s*(?:[:=])\s*function\s*\(") ||
                    Regex.IsMatch(trimmed, @"^function\s+\w+\s*\("))
                {
                    var indent = line.Length - trimmed.Length;
                    topLevelFns.Add((i, trimmed, indent));
                }
            }
            var indentGroups = topLevelFns.GroupBy(f => f.indent).OrderBy(g => g.Key).ToList();
            if (indentGroups.Count > 1)
            {
                var topLevelIndent = indentGroups[0].Key;
                foreach (var group in indentGroups.Skip(1))
                {
                    if (group.Key > topLevelIndent + indentWidth)
                    {
                        foreach (var fn in group)
                        {
                            var namePart = fn.name.Split('=').Last().Split(':').Last().Trim();
                            namePart = Regex.Replace(namePart, @"\s*function\s*\(.*", "").Trim();
                            var fullName = fn.name.Contains('=') || fn.name.Contains(':')
                                ? (Regex.Match(fn.name, @"^(?:vm\.)?(\w+)").Groups[1].Value)
                                : Regex.Match(fn.name, @"function\s+(\w+)").Groups[1].Value;
                            var lineNum = fn.line + 1;
                            staticIssues.Add($"Function '{fullName}' at line {lineNum} appears to be nested inside another function body (indent level {fn.indent}, expected ~{topLevelIndent}). Move it to the top-level scope.");
                        }
                    }
                }
            }
            var fnNames = topLevelFns.Select(f => Regex.Match(f.name, @"(?:vm\.)?(\w+)\s*(?:[:=])\s*function|function\s+(\w+)").Groups.Values
                .Select(g => g.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "function")).ToList();
            var dupes = fnNames.GroupBy(n => n).Where(g => g.Count() > 1);
            foreach (var dupe in dupes)
            {
                staticIssues.Add($"Duplicate definition of '{dupe.Key}' found at multiple locations. Remove the duplicate.");
            }
        }
        var contentPreview = fileContent.Length > 6000
            ? fileContent[..6000] + "\n// ... (truncated)"
            : fileContent;
        var sb = new StringBuilder();
        sb.AppendLine($"FILE: {relPath}");
        sb.AppendLine();
        sb.AppendLine("Scan the file and list any cohesion issues with the recently added code.");
        sb.AppendLine("Cohesion issues include:");
        sb.AppendLine("  - New method placed at wrong location (not grouped with similar methods)");
        sb.AppendLine("  - Naming or style inconsistent with the rest of the file");
        sb.AppendLine("  - Missing blank lines between methods");
        sb.AppendLine("  - Inconsistent error handling, logging, or return patterns");
        sb.AppendLine("  - Inconsistent attribute/annotation usage");
        sb.AppendLine("  - Inconsistent SQL or query patterns compared to similar existing methods");
        sb.AppendLine("  - Code that looks out of place or doesn't follow the file's conventions");
        sb.AppendLine();
        sb.AppendLine("Do NOT fix anything. Do NOT rewrite anything.");
        sb.AppendLine("If no issues found, output: {\"issues\": []}");
        sb.AppendLine("Otherwise output: {\"issues\": [\"Issue 1\", \"Issue 2\", ...]}");
        sb.AppendLine();
        sb.AppendLine("FILE CONTENT:");
        sb.AppendLine("```");
        sb.AppendLine(contentPreview);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Output ONLY valid JSON. No markdown. No explanations.");
        var (raw, _, _) = await CallLlmRaw(
            "You detect code cohesion issues after an edit. Output ONLY JSON.",
            sb.ToString(), ct, _infiniteTimeout, maxTokens: 512);
        var issues = new List<string>();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var cleaned = raw.Trim();
                if (cleaned.StartsWith("```"))
                {
                    var start = cleaned.IndexOf('\n');
                    if (start > 0) cleaned = cleaned.Substring(start).Trim();
                    if (cleaned.EndsWith("```")) cleaned = cleaned[..^3].Trim();
                }
                var result = JsonSerializer.Deserialize<CohesionCheckResult>(cleaned);
                if (result?.Issues != null)
                    issues = result.Issues;
            }
            catch { }
        }
        if (issues.Count > 0)
        {
            await EmitLog(emitSse, "info",
                $"  🔍 Cohesion check: {issues.Count} issue(s) found in {relPath}", ct: ct);
            foreach (var issue in issues)
                await EmitLog(emitSse, "info", $"    - {issue}", ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "info", $"  🔍 Cohesion check: no issues in {relPath}", ct: ct);
        }
        return issues;
    }
    private async Task<(string? foundInPath, string? snippet)> GrepProjectForDefinitionAsync(
        string projectRoot, string symbol, string excludeRelPath, CancellationToken ct)
    {
        var defPatterns = new[]
        {
        new Regex($@"^\s*(?:(?:public|private|protected|readonly|static|async|override|get|set)\s+)*{Regex.Escape(symbol)}\s*[=(:(<]", RegexOptions.Multiline),
        new Regex($@"\b(?:public|private|protected|internal)\b[^{{}}]*\b{Regex.Escape(symbol)}\s*[({{;]", RegexOptions.Multiline),
        new Regex($@"@(?:Input|Output)\(\)[^;]*\b{Regex.Escape(symbol)}\b", RegexOptions.Multiline),
    };
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "bin", "obj", "dist", ".angular", "packages" };
        var codeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".js", ".tsx", ".jsx" };
        try
        {
            foreach (var file in Directory.EnumerateFiles(
                projectRoot, "*.*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;
                var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                if (string.Equals(rel, excludeRelPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (skipDirs.Any(d => rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) ||
                    rel.Contains("/" + d + "/", StringComparison.OrdinalIgnoreCase))) continue;
                if (!codeExts.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                FileInfo fi;
                try { fi = new FileInfo(file); } catch { continue; }
                if (fi.Length > 300_000) continue;
                string content;
                try { content = await System.IO.File.ReadAllTextAsync(file, Encoding.UTF8, ct); }
                catch { continue; }
                foreach (var rx in defPatterns)
                {
                    var m = rx.Match(content);
                    if (!m.Success) continue;
                    var lineNo = content[..m.Index].Count(c => c == '\n') + 1;
                    var line = m.Value.Trim();
                    if (line.Length > 80) line = line[..80] + "…";
                    return (rel, $"line {lineNo}: {line}");
                }
            }
        }
        catch { }
        return (null, null);
    }
    private async Task ExecuteEditStep(
        AgentStep step, string projectRoot, Dictionary<string, object?> result,
        Dictionary<string, string>? contentCache = null)
    {
        var rawPath = (step.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
        if (rawPath.StartsWith("_edit" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            rawPath = rawPath[6..];
        var isAbs = rawPath.Contains(":\\") || rawPath.StartsWith('/') || rawPath.StartsWith('\\');
        var targetPath = isAbs ? Path.GetFullPath(rawPath) : Path.GetFullPath(Path.Combine(projectRoot, rawPath));
        if (!isAbs && !AgentProjectUtilities.IsPathUnderRoot(targetPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }
        result["path"] = step.Path;
        var oldString = step.OldString ?? ""; var newString = step.NewString ?? "";
        var unsafeReason = GetUnsafeEditPayloadReason(oldString, newString);
        if (unsafeReason != null) { result["status"] = "error"; result["error"] = unsafeReason; return; }
        string content;
        if (contentCache != null && contentCache.TryGetValue(targetPath, out var cached)) content = cached;
        else
        {
            if (!System.IO.File.Exists(targetPath))
            {
                if (string.IsNullOrEmpty(oldString) && !string.IsNullOrEmpty(newString))
                {
                    var d = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
                    await System.IO.File.WriteAllTextAsync(targetPath, newString, Encoding.UTF8);
                    result["oldStartLine"] = 0;
                    PopulateEditResult(result, "created", step.Path!, null, newString, newString);
                    if (contentCache != null) contentCache[targetPath] = newString;
                    return;
                }
                result["status"] = "error"; result["error"] = $"File does not exist: {step.Path}";
                result["suggestions"] = AgentDiscovery.FindSimilarFiles(step.Path ?? "", projectRoot);
                return;
            }
            content = await System.IO.File.ReadAllTextAsync(targetPath, Encoding.UTF8);
        }
        if (string.IsNullOrEmpty(oldString))
        {
            content += newString;
            await System.IO.File.WriteAllTextAsync(targetPath, content, Encoding.UTF8);
            if (contentCache != null) contentCache[targetPath] = content;
            PopulateEditResult(result, "modified", step.Path!, null, newString, newString);
            try { _fileHints.LearnFromAppliedEdit(projectRoot, targetPath, newString); }
            catch { }
            return;
        }
        var (replaced, newContent, matchError, snippet) = TryReplaceSafe(content, oldString, newString);
        if (!replaced)
        {
            result["status"] = "error"; result["error"] = matchError ?? "oldString not found";
            if (snippet != null) result["snippet"] = snippet;
            result["oldStringPreview"] = oldString;
            return;
        }
        if (AgentTextUtilities.NormalizeLineEndings(newContent) == AgentTextUtilities.NormalizeLineEndings(content))
        { result["status"] = "skipped"; result["path"] = step.Path; return; }
        var autoFixExt = Path.GetExtension(targetPath)?.ToLowerInvariant();
        if (autoFixExt is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
        {
            var fixedContent = AstCodeEditorService.AutoFixSyntaxErrors(newContent, autoFixExt);
            if (fixedContent != newContent)
            {
                newContent = fixedContent;
                result["autoFixed"] = true;
            }
        }
        var normOld = AgentTextUtilities.NormalizeLineEndings(content);
        var normNew = AgentTextUtilities.NormalizeLineEndings(newContent);
        var minLen = Math.Min(normOld.Length, normNew.Length);
        var diffIdx = 0;
        while (diffIdx < minLen && normOld[diffIdx] == normNew[diffIdx]) diffIdx++;
        result["oldStartLine"] = normOld[..diffIdx].Count(c => c == '\n');
        await System.IO.File.WriteAllTextAsync(targetPath, newContent, Encoding.UTF8);
        if (contentCache != null) contentCache[targetPath] = newContent;
        PopulateEditResult(result, "modified", step.Path!, oldString, newString, newContent);
        try { _fileHints.LearnFromAppliedEdit(projectRoot, targetPath, newString); }
        catch { }
    }
    private static List<string> GetPlanSizeViolations(AgentPlan plan)
    {
        var violations = new List<string>();
        for (var i = 0; i < plan.Plan.Count; i++)
        {
            var step = plan.Plan[i];
            if (!AgentProjectUtilities.IsRelativePath(step.File ?? "")) continue;
            var old = step.OldString ?? "";
            var lines = old.Split('\n').Length; var chars = old.Length;
            if (lines > 10 || chars > 400)
                violations.Add($"Step {i + 1} ({step.File}): oldString is {lines} lines/{chars} chars — will be resolved via focused call");
        }
        return violations;
    }
    /// <summary>
    /// True when the most recent executed result is an edit/create the per-step
    /// verifier marked complete (needsExtraStep=false) — the gate that triggers
    /// between-steps whole-task verification. Extracted so the interleaved loop's
    /// decision is unit-testable without an LLM.
    /// </summary>
    private static bool IsLastEditVerifiedComplete(List<Dictionary<string, object?>> newResults)
    {
        return newResults
            .Any(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                      r.GetValueOrDefault("status")?.ToString() is "modified" or "done" or "created" &&
                      r.ContainsKey("needsExtraStep") && r.GetValueOrDefault("needsExtraStep") is false);
    }
    /// <summary>
    /// The between-steps verdict after AssessCompletion returns. If the assessment
    /// LLM was unavailable (empty / timed out / unparseable), do NOT force a
    /// redundant follow-up step — the per-step verifier already confirmed the last
    /// edit is complete (needsExtraStep=false) and no step failed, so the plan is
    /// declared complete. A real "not complete" assessment still keeps planning.
    /// </summary>
    private static bool ShouldDeclarePlanCompleteAfterAssessment(
        bool isComplete, string? assessReason, out string completeReason, out bool assessFailed)
    {
        assessFailed = string.IsNullOrWhiteSpace(assessReason) ||
            assessReason.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            assessReason.Contains("Could not parse", StringComparison.OrdinalIgnoreCase);
        completeReason = assessFailed
            ? "last edit verified complete (needsExtraStep=false) — assessment LLM unavailable, stopping instead of planning a redundant step"
            : assessReason ?? "";
        return assessFailed || (isComplete && !assessFailed);
    }
    private async Task<(bool isComplete, string reason)> AssessCompletion(
        string prompt, List<object> executedSteps, string projectRoot, CancellationToken ct,
        AgentPlan? plan = null, List<string>? attachedFiles = null, int? atomicStepEstimate = null)
    {
        var editSteps = executedSteps.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "edit")
            .GroupBy(s => s.GetValueOrDefault("path")?.ToString() ?? Guid.NewGuid().ToString())
            .Select(g => g.Last())
            .ToList();
        if (editSteps.Count == 0) return (true, "No edit steps — command-only task");
        var failed = editSteps.Where(s => !s.TryGetValue("status", out var st) || st?.ToString() is not ("done" or "skipped")).ToList();
        if (failed.Count > 0)
        {
            var failedPaths = string.Join(", ", failed.Select(f => f.GetValueOrDefault("path")?.ToString() ?? "?").Distinct());
            return (false, $"{failed.Count} edit step(s) failed: {failedPaths}");
        }
        var sb = new StringBuilder();
        sb.AppendLine("## Task"); sb.AppendLine(prompt); sb.AppendLine();
        if (atomicStepEstimate is > 0)
        {
            var executed = plan?.Plan?.Count ?? editSteps.Count;
            sb.AppendLine($"## Step budget\nThe planner estimated this task needs ~{atomicStepEstimate} atomic step(s); " +
                $"{executed} step(s) were executed. If the explicit request appears satisfied, prefer complete=true " +
                "— do NOT invent additional requirements just because the estimate suggests more work. " +
                "Exceeding the estimate is not a failure; an unmet EXPLICIT requirement is.\n");
        }
        if (plan?.Plan?.Count > 0)
        {
            sb.AppendLine("## Planned steps");
            foreach (var step in plan.Plan)
                sb.AppendLine($"- {step.File}: {step.Change}");
            sb.AppendLine();
        }
        sb.AppendLine("## Edit results");
        foreach (var s in editSteps.Take(10))
        {
            var path = s.GetValueOrDefault("path")?.ToString() ?? "?";
            var status = s.TryGetValue("status", out var st) ? st?.ToString() : "?";
            var error = s.TryGetValue("error", out var e) ? e?.ToString() : null;
            sb.AppendLine($"- {path}: {status}{(error != null ? $" → {error}" : "")}");
        }
        sb.AppendLine();
        var modifiedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in editSteps)
        {
            var p = s.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p))
                modifiedSet.Add(p.Replace('\\', '/'));
        }
        if (attachedFiles != null && attachedFiles.Count > 0)
        {
            sb.AppendLine("## Unmodified attached files (check each one — does it still need changes to complete the task?)");
            foreach (var relPath in attachedFiles)
            {
                var normalized = relPath.Replace('\\', '/');
                if (modifiedSet.Contains(normalized)) continue;
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, normalized));
                if (!System.IO.File.Exists(fullPath)) continue;
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                sb.AppendLine($"### {normalized}\n```\n{content}\n```\n");
            }
        }
        var allModifiedPaths = editSteps
            .Where(s => s.TryGetValue("status", out var st) && st?.ToString() == "done")
            .Select(s => s.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        if (allModifiedPaths.Count > 0)
        {
            sb.AppendLine("## Modified files (current state after edits)");
            foreach (var relPath in allModifiedPaths)
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath!.Replace('/', Path.DirectorySeparatorChar)));
                if (!System.IO.File.Exists(fullPath)) { sb.AppendLine($"### {relPath}\n*File not found*\n"); continue; }
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                sb.AppendLine($"### {relPath}\n```\n{content}\n```\n");
            }
        }
        sb.AppendLine(@"Evaluate the code changes against the ORIGINAL TASK ONLY. Judge strictly against what the user
EXPLICITLY requested — do NOT invent additional requirements, features, files, or 'best practice' improvements the
user did not ask for. Check for:
1. Does the code address everything the user EXPLICITLY requested?
2. Are there bugs, syntax errors, or logic issues in the modified files that would break the requested change?
3. Did any planned step fail or get left unfinished?
4. Check files in ""Unmodified attached files"" ONLY against the explicit request — mark incomplete only if the user's request clearly required changing them.
A task is complete when the explicit request is satisfied, even if you can imagine further improvements. When in doubt, mark complete=true.
Respond with JSON only:
```json
{
  ""complete"": true|false,
  ""reason"": ""one sentence summary"",
  ""issues"": [""description of each bug or remaining work""]
}
```");
        const string sys = @"You are a thorough code reviewer and task completion verifier. Examine the original task, the changes made, and the current state of all files. Check for bugs, logic errors, and syntax mistakes that would break the requested change. Judge completion ONLY against what the user explicitly requested — never invent new requirements, features, or scope the user did not ask for. When the explicit request is met, mark complete=true even if further improvements are imaginable. Output ONLY valid JSON in the format specified.";
        // Use the configurable LLM timeout (not a hard 30s cap): on slow local models a 30s
        // deadline turns a healthy completion assessment into a fake "timed out" verdict,
        // which then forces the interleaved loop to plan a redundant follow-up step.
        var (raw, _, _) = await CallLlmRaw(sys, sb.ToString(), ct, _infiniteTimeout);
        if (string.IsNullOrWhiteSpace(raw))
        {
            // One retry — transient endpoint slowness shouldn't veto a verified-complete task.
            (raw, _, _) = await CallLlmRaw(sys, sb.ToString(), ct, _infiniteTimeout);
        }
        if (string.IsNullOrWhiteSpace(raw)) return (failed.Count == 0, "Assessment timed out");
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            var s2 = cleaned.IndexOf('{'); var e2 = cleaned.LastIndexOf('}');
            if (s2 >= 0 && e2 > s2) cleaned = cleaned[s2..(e2 + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            var isComplete = root.TryGetProperty("complete", out var c) && c.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            if (root.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            {
                var issueList = new List<string>();
                foreach (var issue in issues.EnumerateArray())
                {
                    if (issue.ValueKind == JsonValueKind.String)
                        issueList.Add(issue.GetString() ?? "");
                }
                if (issueList.Count > 0)
                    reason = reason + " | Issues: " + string.Join("; ", issueList);
            }
            return (isComplete, reason);
        }
        catch { return (failed.Count == 0, "Could not parse assessment"); }
    }
    private AgentPlan MergePlans(AgentPlan existing, AgentPlan replan)
    {
        if (existing == null) return replan;
        if (existing.Plan == null) existing.Plan = new List<PlanStep>();
        var existingKeys = new HashSet<string>(
            existing.Plan.Select(p => $"{p.File}|{NormalizeChangeForDedup(p.Change)}"),
            StringComparer.OrdinalIgnoreCase);
        foreach (var step in replan.Plan)
        {
            var key = $"{step.File}|{NormalizeChangeForDedup(step.Change)}";
            if (existingKeys.Add(key))
            {
                existing.Plan.Add(step);
            }
        }
        return existing;
    }
    private static List<PlanStep> MergePlanSteps(IEnumerable<PlanStep> existing, IEnumerable<PlanStep> additions)
    {
        var result = new List<PlanStep>(existing);
        var existingKeys = new HashSet<string>(existing.Select(s => $"{s.File}|||{s.Change}"), StringComparer.OrdinalIgnoreCase);
        foreach (var step in additions)
        {
            var key = $"{step.File}|||{step.Change}";
            if (existingKeys.Add(key))
                result.Add(step);
        }
        return result;
    }
    private async Task<List<PlanStep>?> CheckpointReplan(
        string originalPrompt, string currentDiscoveryContext, List<PlanStep> remainingSteps,
        List<object> completedResults, string projectRoot, bool emitSse, CancellationToken ct,
        string? steeringContext = null)
    {
        var modifiedPaths = completedResults.OfType<Dictionary<string, object?>>()
            .Where(r => r.TryGetValue("type", out var t) && t?.ToString() is "edit" or "create" &&
                        r.TryGetValue("status", out var s) && s?.ToString() == "done")
            .Select(r => r.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        await EmitLog(emitSse, "info", $"Checkpoint: refreshing {modifiedPaths.Count} file(s)…", ct: ct);
        var enriched = new StringBuilder(currentDiscoveryContext);
        enriched.AppendLine("\n## CHECKPOINT — current file states");
        foreach (var relPath in modifiedPaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath!.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(fullPath)) continue;
            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            enriched.AppendLine($"\n### {relPath} (post-phase)\n```\n{content}\n```");
        }
        if (remainingSteps.Count == 0) return null;
        var remainDesc = new StringBuilder("Intended remaining work (KEEP ALL of these — only add new ones):\n");
        foreach (var step in remainingSteps) remainDesc.AppendLine($"- {step.File}: {step.Change}");
        var replanPrompt = $"## Original task\n{originalPrompt}\n\n{remainDesc}" +
            (string.IsNullOrWhiteSpace(steeringContext) ? "" : $"\n## Steering\n{steeringContext}");
        var newPlan = await AnalyzePromptAndPlanCodeChanges(
            replanPrompt, enriched.ToString(), projectRoot, emitSse, ct, steeringContext);
        return newPlan?.Plan;
    }
    private async Task ExecuteRenameStep(AgentStep step, string projectRoot, Dictionary<string, object?> result)
    {
        var srcRel = (step.Path ?? "").Replace('\\', '/');
        var dstRel = (step.ToPath ?? "").Replace('\\', '/');
        var srcPath = Path.GetFullPath(Path.Combine(projectRoot, srcRel.Replace('/', Path.DirectorySeparatorChar)));
        var dstPath = Path.GetFullPath(Path.Combine(projectRoot, dstRel.Replace('/', Path.DirectorySeparatorChar)));
        result["path"] = srcRel; result["toPath"] = dstRel;
        if (!AgentProjectUtilities.IsPathUnderRoot(srcPath, projectRoot) || !AgentProjectUtilities.IsPathUnderRoot(dstPath, projectRoot))
        { result["status"] = "error"; result["error"] = "Path outside project root"; return; }
        if (!System.IO.File.Exists(srcPath)) { result["status"] = "error"; result["error"] = $"Source not found: {srcRel}"; return; }
        if (System.IO.File.Exists(dstPath)) { result["status"] = "error"; result["error"] = $"Destination exists: {dstRel}"; return; }
        try
        {
            var dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.IO.File.Move(srcPath, dstPath);
            result["status"] = "done"; result["editAction"] = "renamed";
        }
        catch (Exception ex) { result["status"] = "error"; result["error"] = ex.Message; }
    }
    private static void PopulateEditResult(
        Dictionary<string, object?> result, string action, string path,
        string? oldStr, string? newStr, string writtenContent)
    {
        result["type"] = "edit";
        result["status"] = "done";
        result["editAction"] = action;
        result["path"] = path;
        result["linesRemoved"] = (oldStr ?? "").Split('\n').Length;
        result["linesAdded"] = (newStr ?? "").Split('\n').Length;
        if (!string.IsNullOrEmpty(oldStr)) result["oldStringPreview"] = oldStr;
        if (!string.IsNullOrEmpty(newStr)) result["newStringPreview"] = newStr;
        result["diffPreview"] = AgentDiffUtilities.BuildDiffPreview(oldStr, newStr);
        result["oldLines"] = (oldStr ?? "").Split('\n');
        result["newLines"] = (newStr ?? "").Split('\n');
    }
    private async Task<string> EnrichWithTypeChain(
        string projectRoot,
        string relPath,
        string stepChange,
        HashSet<string> alreadyRead,
        bool emitSse,
        CancellationToken ct,
        int maxDepth = 3)
    {
        var buf = new StringBuilder();
        const int MaxEnrichChars = 6000;
        var discoveredTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var typesToFollow = new Queue<(string typeName, int depth)>();
        var targetFullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!System.IO.File.Exists(targetFullPath)) return "";
        var targetContent = await System.IO.File.ReadAllTextAsync(targetFullPath, Encoding.UTF8, ct);
        var typeRefPattern = new Regex(
            @"(?::\s*)([A-Z][A-Za-z0-9_]+)(?:\[\])?(?:\s*[;=})|])",
            RegexOptions.Compiled);
        foreach (Match m in typeRefPattern.Matches(targetContent))
        {
            var typeName = m.Groups[1].Value;
            if (!_builtInTypes.Contains(typeName) && typeName.Length > 2)
            {
                typesToFollow.Enqueue((typeName, 0));
            }
        }
        foreach (Match m in Regex.Matches(stepChange, @"\b([A-Z][A-Za-z0-9_]+)\b"))
        {
            var typeName = m.Groups[1].Value;
            if (!_builtInTypes.Contains(typeName) && typeName.Length > 2)
            {
                typesToFollow.Enqueue((typeName, 0));
            }
        }
        var typeFileExtensions = new[] { ".cs", ".ts", ".tsx", ".js", ".jsx" };
        var allProjectFiles = typeFileExtensions
            .SelectMany(ext => Directory.EnumerateFiles(projectRoot, ext, SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                     && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\")
                     && !f.Contains("\\dist\\"))
            .ToList();
        while (typesToFollow.Count > 0 && buf.Length < MaxEnrichChars)
        {
            var (typeName, depth) = typesToFollow.Dequeue();
            if (depth > maxDepth) continue;
            if (discoveredTypes.Contains(typeName)) continue;
            if (_builtInTypes.Contains(typeName)) continue;
            discoveredTypes.Add(typeName);
            string? definingFile = null;
            string? definingContent = null;
            foreach (var pf in allProjectFiles)
            {
                try
                {
                    var content = await System.IO.File.ReadAllTextAsync(pf, Encoding.UTF8, ct);
                    if (Regex.IsMatch(content,
                        $@"(?:export\s+)?(?:abstract\s+)?(?:class|interface|type|record|struct)\s+{Regex.Escape(typeName)}\b",
                        RegexOptions.IgnoreCase))
                    {
                        definingFile = pf;
                        definingContent = content;
                        break;
                    }
                }
                catch { continue; }
            }
            if (definingFile == null || definingContent == null) continue;
            var rel = Path.GetRelativePath(projectRoot, definingFile).Replace('\\', '/');
            if (alreadyRead.Contains(rel)) continue;
            alreadyRead.Add(rel);
            var excerpt = AgentDiscovery.ExtractRelevantExcerpt(definingContent, typeName, null, 1500);
            buf.AppendLine($"### {rel}  (type: {typeName}, depth: {depth})");
            buf.AppendLine("```");
            buf.AppendLine(excerpt);
            buf.AppendLine("```");
            buf.AppendLine();
            if (depth < maxDepth && !string.IsNullOrEmpty(excerpt))
            {
                foreach (Match m in typeRefPattern.Matches(excerpt))
                {
                    var nestedType = m.Groups[1].Value;
                    if (!discoveredTypes.Contains(nestedType) &&
                        !_builtInTypes.Contains(nestedType) &&
                        nestedType.Length > 2)
                    {
                        typesToFollow.Enqueue((nestedType, depth + 1));
                    }
                }
            }
        }
        if (buf.Length == 0) return "";
        await EmitLog(emitSse, "info",
            $"  🔗 Type-chain enrichment: discovered {discoveredTypes.Count} type(s) " +
            $"[{string.Join(", ", discoveredTypes.Take(8))}]", ct: ct);
        return "\n### AUTO-ENRICHED TYPE CONTEXT (followed type references recursively)\n" +
               "⚠ These type definitions show EXACT property names. Use ONLY these property names in your edit.\n" +
               buf.ToString();
    }
    private static string NormalizeTypeScriptObjectLiterals(string content)
    {
        return Regex.Replace(content, @"(?<=[\{,]\s*)(\w[\w']*)\s*:\s*(?=\S)", "$1: ");
    }
    private async Task<string> EnsureCompleteFullFile(string partialContent, PlanStep step,
        string fullPath, string projectRoot, bool emitSse, CancellationToken ct,
        List<(string old, string @new, string error)>? history = null)
    {
        if (!AgentCodeFormatting.IsFullFileTruncated(partialContent))
            return partialContent;
        var accumulated = partialContent;
        var relPath = step.File.Replace('\\', '/');
        var maxPasses = 5;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"You are continuing a full-file replacement that was interrupted (token limit reached).");
            sb.AppendLine();
            sb.AppendLine($"FILE: {relPath}");
            sb.AppendLine($"CHANGE REQUIRED: {step.Change}");
            sb.AppendLine();
            sb.AppendLine("Here is the PARTIAL output you have generated so far (starting from the last complete brace-balanced point):");
            sb.AppendLine("```");
            var continuationStart = AgentCodeFormatting.FindLastBalancedPrefix(accumulated);
            sb.AppendLine(continuationStart.Length > 2000
                ? continuationStart[^2000..] + "\n... (truncated view — the partial file is already written to disk)"
                : continuationStart);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Continue from where you left off. Output ONLY the REMAINING content — do NOT repeat any already-generated lines.");
            sb.AppendLine("The complete file must have balanced braces (equal number of { and }).");
            sb.AppendLine();
            sb.AppendLine("Output the continuation now (as raw text, no JSON, no markdown fences):");
            var continuationPrompt = sb.ToString();
            var continuationSystem =
                "You are a code completion assistant. Continue the partial file from where it was interrupted. " +
                "Output ONLY the remaining lines needed to complete the file. " +
                "Do NOT repeat any already-output content. The file uses brace-based indentation (C#/JS/TS style).";
            var (raw, _, _) = await CallLlmRaw(continuationSystem, continuationPrompt, ct,
                _infiniteTimeout, maxTokens: 8192);
            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn",
                    $"Full-file continuation pass {pass + 1} returned empty — stopping", ct: ct);
                break;
            }
            raw = StripFullFileFence(raw);
            accumulated += "\n" + raw;
            if (!AgentCodeFormatting.IsFullFileTruncated(accumulated))
            {
                await EmitLog(emitSse, "info",
                    $"Full-file complete after {pass + 2} pass(es) ({accumulated.Length} chars)", ct: ct);
                return accumulated;
            }
        }
        await EmitLog(emitSse, "warn",
            $"Full-file may still be truncated after {maxPasses} continuation passes — brace count: " +
            $"{accumulated.Count(c => c == '{')} / {accumulated.Count(c => c == '}')}", ct: ct);
        return accumulated;
    }
    private async Task<int> ApplyFullFile(string fullContent, PlanStep step, string fullPath, string relPath,
        string projectRoot, int stepIndex, int planItemIndex, string? cardId, bool emitSse, CancellationToken ct,
        List<object> allResults)
    {
        // Safety net: never File.WriteAllText to an existing directory path (throws
        // UnauthorizedAccessException on Windows). The ResolveAndApplyEdit directory-target guard
        // redirects/skips first; this is defense-in-depth for any other call path.
        if (Directory.Exists(fullPath) && !System.IO.File.Exists(fullPath))
        {
            await EmitLog(emitSse, "info",
                $"Skipping full-file write — '{relPath}' is an existing directory, not a file", ct: ct);
            var skip = new Dictionary<string, object?>
            {
                ["index"] = stepIndex,
                ["type"] = "edit",
                ["status"] = "skipped",
                ["path"] = relPath,
                ["reason"] = "target is an existing directory",
                ["planItemIndex"] = planItemIndex
            };
            if (emitSse) await SendSse(Response, "step", skip, ct);
            allResults.Add(skip);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
            return stepIndex + 1;
        }
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        fullContent = await EnsureCompleteFullFile(fullContent, step, fullPath, projectRoot, emitSse, ct);
        var existingLines = System.IO.File.Exists(fullPath)
            ? await System.IO.File.ReadAllLinesAsync(fullPath, Encoding.UTF8, ct)
            : null;
        if (existingLines != null && existingLines.Length > 0)
            fullContent = AgentCodeFormatting.AutoIndentFullFile(fullContent, existingLines);
        var fileExt = Path.GetExtension(relPath).ToLowerInvariant();
        if (fileExt is ".css" or ".scss" or ".less")
        {
            var (merged, mergeWarnings) = MergeDuplicateCssRules(fullContent);
            if (merged != fullContent)
            {
                fullContent = merged;
                foreach (var w in mergeWarnings)
                    await EmitLog(emitSse, "warn", w, ct: ct);
                await EmitLog(emitSse, "info",
                    $"Merged duplicate CSS selectors in {relPath} (fullFile path)", ct: ct);
            }
        }
        if (CodeFormatterService.CanFormat(relPath))
        {
            var before = fullContent;
            var jsLike = fileExt is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs";
            if (!jsLike)
                fullContent = await CodeFormatterService.FormatAsync(relPath, fullContent, ct);
            if (fullContent != before)
                await EmitLog(emitSse, "info",
                    $"Formatted full file in {relPath} via CodeFormatterService", ct: ct);
        }
        var fExt = Path.GetExtension(relPath).ToLowerInvariant();
        if (fExt == ".css" || fExt == ".scss" || fExt == ".less")
            fullContent = LlmCssCleaner.Clean(fullContent);
        await System.IO.File.WriteAllTextAsync(fullPath, fullContent, Encoding.UTF8, ct);
        await EmitLog(emitSse, "success", $"✓ Written {relPath} ({fullContent.Length} chars)", ct: ct);
        var r = new Dictionary<string, object?>();
        PopulateEditResult(r, "modified", relPath, null, fullContent, "");
        r["index"] = stepIndex;
        r["planItemIndex"] = planItemIndex;
        if (emitSse) await SendSse(Response, "step", r, ct);
        allResults.Add(r);
        await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct);
        try { _fileHints.LearnFromAppliedEdit(projectRoot, fullPath, fullContent); }
        catch { }
        _ = Task.Run(async () =>
        {
            try { await _editKnowledge.UpdateArchitectureAsync(projectRoot, relPath, fullContent); }
            catch { }
        }, CancellationToken.None);
        return stepIndex + 1;
    }
}
