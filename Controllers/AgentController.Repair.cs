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
    private async Task RepairPipeline(
        string projectRoot, bool emitSse, CancellationToken ct,
        string originalPrompt, string? steeringContext, string? buildCommands)
    {
        var buildOutput = _terminal.ReadAll();
        var resultSteps = new List<object>();
        await RunRepairPlan(projectRoot, emitSse, ct, originalPrompt, buildOutput, resultSteps, steeringContext);
        var cmds = !string.IsNullOrWhiteSpace(buildCommands) ? ParseBuildCommands(buildCommands) : new List<string>();
        bool repairOk = true;
        foreach (var cmd in cmds)
        {
            var ok = await RunSmartBuildCheck(projectRoot, cmd, emitSse, ct);
            if (!ok) { repairOk = false; }
        }
        if (repairOk)
            await EmitLog(emitSse, "success", "RepairPipeline: build fixed successfully.", ct: ct);
        else
            await EmitLog(emitSse, "warn", "RepairPipeline: build still has errors after repair attempt.", ct: ct);
        if (emitSse)
            await SendSse(Response, "done_signal", new { message = "Build repair completed" }, ct);
    }
    private async Task RunTestCreationPipeline(
        string projectRoot, List<object> allSteps, bool emitSse, CancellationToken ct)
    {
        var editedFiles = allSteps
            .OfType<Dictionary<string, object?>>()
            .Where(s => s.GetValueOrDefault("type")?.ToString() is "edit" or "create")
            .Select(s => s.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (editedFiles.Count == 0) return;
        await EmitLog(emitSse, "info", $"TestCreation: preparing tests for {editedFiles.Count} file(s)", ct: ct);
        var existingTestFiles = AgentProjectUtilities.FindExistingTestFiles(projectRoot);
        var hasExistingTests = existingTestFiles.Count > 0;
        var testFramework = await AgentProjectUtilities.DetectTestFramework(projectRoot, ct);
        if (!hasExistingTests && testFramework == null)
        {
            if (emitSse)
                await SendSse(Response, "phase", new { phase = "test-creation", message = "No test framework detected" }, ct);
            var answer = await AskUserAsync(
                "No test files found. Enter framework name to set up (xunit, nunit, mstest) or leave empty to skip:",
                new List<QuestionField>
                {
                    new() { Key = "framework", Label = "Test framework", Type = "text", DefaultValue = "xunit" }
                }, ct);
            var framework = answer.GetValueOrDefault("framework")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(framework) || framework is "none" or "skip")
            {
                await EmitLog(emitSse, "info", "Test creation skipped by user.", ct: ct);
                return;
            }
            testFramework = framework;
        }
        testFramework ??= "xunit";
        if (existingTestFiles.Count > 0)
        {
            if (existingTestFiles.Any(f => AgentProjectUtilities.FileContains(f, "xunit", "Fact"))) testFramework = "xunit";
            else if (existingTestFiles.Any(f => AgentProjectUtilities.FileContains(f, "nunit", "TestFixture"))) testFramework = "nunit";
            else if (existingTestFiles.Any(f => AgentProjectUtilities.FileContains(f, "mstest", "TestClass", "TestMethod"))) testFramework = "mstest";
        }
        await EmitLog(emitSse, "info", $"TestCreation: using '{testFramework}'", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "test-creation", message = $"Generating tests ({testFramework})" }, ct);
        var existingContext = new StringBuilder();
        foreach (var tf in existingTestFiles.Take(3))
        {
            try
            {
                var rel = Path.GetRelativePath(projectRoot, tf);
                var content = await System.IO.File.ReadAllTextAsync(tf, Encoding.UTF8, ct);
                existingContext.AppendLine($"// File: {rel}");
                existingContext.AppendLine(content);
                existingContext.AppendLine();
            }
            catch { }
        }
        var testDir = AgentProjectUtilities.FindOrDetermineTestDir(projectRoot, existingTestFiles);
        foreach (var filePath in editedFiles)
        {
            var fullPath = Path.Combine(projectRoot, filePath);
            if (!System.IO.File.Exists(fullPath))
            {
                await EmitLog(emitSse, "warn", $"TestCreation: file not found: {filePath}", ct: ct);
                continue;
            }
            var fileContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            var testFilePath = AgentProjectUtilities.GetTestFilePath(projectRoot, filePath, testDir);
            var sysMsg = "You are a test-generation assistant. Generate unit tests for the given source code. Return ONLY the code, no explanations or markdown formatting.";
            var userMsg = new StringBuilder();
            userMsg.AppendLine($"Test framework: {testFramework}");
            userMsg.AppendLine($"Source file: {filePath}");
            if (existingContext.Length > 0)
            {
                userMsg.AppendLine();
                userMsg.AppendLine("Existing test files in the project (match style):");
                userMsg.Append(existingContext);
            }
            userMsg.AppendLine();
            userMsg.AppendLine("Source code to test:");
            userMsg.AppendLine(fileContent);
            userMsg.AppendLine();
            userMsg.AppendLine($"Generate a complete {testFramework} test file. Return ONLY the code.");
            var (raw, error) = await CallLlmRawText(sysMsg, userMsg.ToString(), emitSse, ct,
                requestTimeout: _infiniteTimeout, maxTokens: 4096);
            if (error != null || string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "warn", $"TestCreation: LLM failed for {filePath}: {error}", ct: ct);
                continue;
            }
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:\w+)?\s*([\s\S]*?)```");
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var testFullDir = Path.GetDirectoryName(testFilePath);
            if (!string.IsNullOrWhiteSpace(testFullDir))
                Directory.CreateDirectory(testFullDir);
            await System.IO.File.WriteAllTextAsync(testFilePath, cleaned, Encoding.UTF8, ct);
            var relPath = Path.GetRelativePath(projectRoot, testFilePath);
            await EmitLog(emitSse, "success", $"Test file created: {relPath}", ct: ct);
            if (emitSse)
                await SendSse(Response, "step", new { type = "create", path = relPath, status = "created" }, ct);
        }
    }
    private async Task RunRepairPlan(
        string projectRoot, bool emitSse, CancellationToken ct,
        string prompt, string buildOutput, List<object> resultSteps,
        string? steeringContext = null)
    {
        var cfg9 = await LoadConfigAsync();
        await EmitLog(emitSse, "info", "RunRepairPlan: analyzing build errors…", ct: ct);
        if (emitSse)
            await SendSse(Response, "phase", new { phase = "repair", message = "Analyzing build errors and planning fixes…" }, ct);
        var tail = buildOutput.Length > cfg9.buildOutputTailChars ? buildOutput[^cfg9.buildOutputTailChars..] : buildOutput;
        var repairPrompt = $"BUILD OUTPUT:\n```\n{tail}\n```\n\nAnalyze the build output above, identify compilation errors, and fix them by editing the source files. Do not add new features — only fix compilation errors/warnings.";
        var repairSteering = $"BUILD REPAIR: Fix the compilation errors shown in the build output. {(string.IsNullOrWhiteSpace(steeringContext) ? "" : $"\nOriginal task: {steeringContext}")}";
        var plan = await AnalyzePromptAndPlanCodeChanges(
            repairPrompt, tail, projectRoot, emitSse, ct, repairSteering);
        if (plan == null || plan.Plan.Count == 0)
        {
            await EmitLog(emitSse, "warn", "RunRepairPlan: no repair plan generated.", ct: ct);
            return;
        }
        if (emitSse)
            await SendSse(Response, "plan",
                new { thinking = plan.Thinking, summary = $"Build repair: {plan.Summary}", items = plan.Plan }, ct);
        await ExecutePlan(repairPrompt, projectRoot, emitSse, tail, plan, ct, resultSteps,
            steeringContext: repairSteering);
    }
    private async Task<string?> AnalyzePreservationAndDependenciesAsync(
        PlanStep step, string projectRoot, string relPath, string? targetSymbol,
        string explorationContext, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetSymbol)) return null;
        var callSites = new List<string>();
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        var codeFiles = ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx"
            ? Directory.EnumerateFiles(projectRoot, "*" + ext, SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\node_modules\\"))
                .ToList()
            : new List<string>();
        foreach (var file in codeFiles)
        {
            try
            {
                var content = await System.IO.File.ReadAllTextAsync(file, ct);
                if (content.Contains(targetSymbol + "(") || content.Contains(targetSymbol + " ("))
                {
                    var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                    if (rel != relPath) callSites.Add(rel);
                }
            }
            catch { }
        }
        var fullPath = Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        string? existingMethodBody = null;
        if (System.IO.File.Exists(fullPath))
        {
            var (oldStr, _) = AstResolveEdit(fullPath, "method", targetSymbol);
            if (!string.IsNullOrWhiteSpace(oldStr))
            {
                if (!oldStr.Contains(targetSymbol, StringComparison.Ordinal))
                {
                    await EmitLog(emitSse, "warn",
                        $"  ⚠ AST-resolved body for '{targetSymbol}' does not contain the symbol name — likely wrong method",
                        ct: ct);
                }
                else
                {
                    existingMethodBody = oldStr;
                }
            }
        }
        if (existingMethodBody == null && callSites.Count == 0) return null;
        var sysPrompt =
            "You are a Code Preservation and Dependency Analysis Agent. " +
            "Your job is to analyze an existing method and a proposed change, then output a strict 'PRESERVATION DIRECTIVE'. " +
            "This directive will be fed to an Editor Agent to ensure it reshapes existing logic rather than inventing new logic or breaking dependencies.\n\n" +
            "Output ONLY valid JSON: " +
            "{\"preservationDirective\": \"...\", \"performanceNotes\": \"...\"}\n\n" +
            "In the directive, explicitly state:\n" +
            "1. Whether the method signature MUST be preserved (if there are call sites).\n" +
            "2. What existing logic must be retained (e.g., 'must still return a valid User object').\n" +
            "3. How the new logic should integrate with the old logic (e.g., 'add the new filter BEFORE the existing loop').";
        var sb = new StringBuilder();
        sb.AppendLine("## TASK CONTEXT");
        sb.AppendLine($"File: {relPath}");
        sb.AppendLine($"Proposed Change: {step.Change}");
        sb.AppendLine();
        if (existingMethodBody != null)
        {
            sb.AppendLine("## EXISTING METHOD IMPLEMENTATION (Target Symbol: " + targetSymbol + ")");
            sb.AppendLine("```");
            sb.AppendLine(existingMethodBody.Length > 2000 ? existingMethodBody[..2000] + "..." : existingMethodBody);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        if (callSites.Count > 0)
        {
            sb.AppendLine("## DEPENDENCIES / CALL SITES");
            sb.AppendLine($"This method is called in {callSites.Count} other file(s): {string.Join(", ", callSites.Take(5))}");
            sb.AppendLine("The method signature and return type MUST be preserved to avoid breaking these files.");
            sb.AppendLine();
        }
        var (raw, _, err) = await CallLlmRawStreaming(sysPrompt, sb.ToString(), emitSse, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 512);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("preservationDirective", out var pdEl))
            {
                var directive = pdEl.GetString();
                if (!string.IsNullOrWhiteSpace(directive))
                {
                    await EmitLog(emitSse, "info", $"  🛡️ Preservation Directive generated: {directive}", ct: ct);
                    return directive;
                }
            }
        }
        catch { }
        return null;
    }
    private async Task RunSelfImprovingPipeline(
        string prompt, string projectRoot, List<object> allSteps,
        AgentPlan? plan, bool complete, bool editsApplied)
    {
        List<JsonElement> features = new();
        var ex = _db.GetImprovementData(projectRoot);
        if (!string.IsNullOrWhiteSpace(ex))
        {
            try
            {
                var root = JsonSerializer.Deserialize<JsonElement>(ex);
                if (root.TryGetProperty("features", out var feats) && feats.ValueKind == JsonValueKind.Array)
                    features = feats.EnumerateArray().ToList();
            }
            catch { }
        }
        var now = DateTime.UtcNow.ToString("o");
        var filesEdited = ExtractFilesEdited(allSteps);
        var filePaths = filesEdited.Select(f =>
        {
            if (f is Dictionary<string, object?> d && d.TryGetValue("path", out var p) && p is string ps) return ps;
            if (f is JsonElement je && je.TryGetProperty("path", out var pp)) return pp.GetString() ?? "";
            return "";
        }).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        var entry = new Dictionary<string, object?> { ["description"] = plan?.Summary ?? "No summary", ["complete"] = complete && editsApplied, ["date"] = now };
        var existIdx = features.FindIndex(f => f.TryGetProperty("feature", out var ft) && ft.GetString() == prompt);
        Dictionary<string, object?> featureEntry;
        List<object> improvements;
        if (existIdx >= 0)
        {
            featureEntry = JsonSerializer.Deserialize<Dictionary<string, object?>>(features[existIdx].GetRawText()) ?? new();
            improvements = new List<object>();
            featureEntry["lastUpdated"] = now;
        }
        else
        {
            featureEntry = new Dictionary<string, object?> { ["feature"] = prompt, ["files"] = filePaths, ["improvements"] = new List<object>(), ["lastUpdated"] = now };
            improvements = new List<object>();
        }
        improvements.Add(entry); featureEntry["improvements"] = improvements;
        if (existIdx >= 0) features[existIdx] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(featureEntry));
        else features.Add(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(featureEntry)));
        var output = new Dictionary<string, object?> { ["features"] = features.Select(f => JsonSerializer.Deserialize<Dictionary<string, object?>>(f.GetRawText())).ToList() };
        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
        _db.SetImprovementData(projectRoot, json);
        await EmitLog(true, "info", $"Self-improving data written for: {prompt}");
    }
    private async Task PersistMetaPlanToCardAsync(string? cardId, MetaPlanResult metaPlan, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || metaPlan == null) return;
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
                    cardObj["_metaPlan"] = new JsonObject
                    {
                        ["summary"] = metaPlan.MetaSummary,
                        ["complexity"] = metaPlan.Complexity,
                        ["thinking"] = metaPlan.MetaThinking,
                        ["subPlans"] = new JsonArray(
                            metaPlan.SubPlans.Select(sp => new JsonObject
                            {
                                ["id"] = sp.Id,
                                ["title"] = sp.Title,
                                ["description"] = sp.Description,
                                ["files"] = JsonNode.Parse(JsonSerializer.Serialize(sp.Files ?? new List<string>())),
                                ["contextNote"] = sp.ContextNote,
                                ["done"] = false
                            }).ToArray()
                        )
                    };
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "refresh", new { target = "boarddata", reason = "meta-plan-updated", cardId }, ct);
                    }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "warn", "Failed to persist meta-plan to boarddata", new { cardId, error = ex.Message });
        }
    }
    private async Task UpdateMetaPlanSubPlanStatusAsync(string? cardId, string subPlanId, bool isDone, bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || string.IsNullOrWhiteSpace(subPlanId)) return;
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
                    if (cardObj["_metaPlan"] is JsonObject metaPlanObj &&
                        metaPlanObj["subPlans"] is JsonArray subPlansArr)
                    {
                        foreach (var sp in subPlansArr)
                        {
                            if (sp is JsonObject spObj && spObj["id"]?.GetValue<string>() == subPlanId)
                            {
                                spObj["done"] = isDone;
                                break;
                            }
                        }
                    }
                    var saved = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await _boardData.SaveRawAsync(saved);
                    if (emitSse)
                    {
                        await SendSse(Response, "meta-plan-step-updated", new { subPlanId, done = isDone, cardId }, ct);
                        await SendSse(Response, "refresh", new { target = "boarddata", reason = "meta-plan-step-updated", cardId }, ct);
                    }
                    return;
                }
            }
        }
        catch { }
    }
}
