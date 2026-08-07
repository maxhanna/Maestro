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
    private static bool IsVisualLayoutTask(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) { return false; }
        var p = prompt.ToLowerInvariant();
        if (Regex.IsMatch(p, @"\bmove\b.{0,60}\b(inside|into|to|under|below|above|after|before)\b"))
        { return false; }
        return Regex.IsMatch(p, @"\b(position|layout|align(?:ed|ment)?|margin|padding|spacing)\b") ||
               Regex.IsMatch(p, @"\b(overlap|z[- ]?index|float|sticky|fixed|absolute|relative)\b") ||
               Regex.IsMatch(p, @"\b(grid|flex|width|height|overflow)\b") ||
               p.Contains("move ");
    }
    private static bool IsStylesheetPath(string file)
    {
        var ext = Path.GetExtension(file ?? "").ToLowerInvariant();
        return ext is ".css" or ".scss" or ".sass" or ".less" or ".styl";
    }
    private async Task<string?> ValidatePlanAsync(string userPrompt, AgentPlan plan, CancellationToken ct)
    {
        if (plan?.Plan != null)
        {
            string? lastImpliedDir = null;
            for (var i = 0; i < plan.Plan.Count; i++)
            {
                var step = plan.Plan[i];
                if (string.Equals(step.File, "_command", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(step.Change))
                {
                    var cmd = step.Change.ToLowerInvariant();
                    if (cmd.Contains("mkdir") || cmd.Contains("rmdir") || cmd.Contains("rm -rf") ||
                        cmd.Contains("del /") || cmd.Contains("rd /"))
                    {
                        var mkdirMatch = Regex.Match(step.Change, @"(?:mkdir|md)\s+([^\s;|&]+)", RegexOptions.IgnoreCase);
                        if (mkdirMatch.Success)
                        {
                            lastImpliedDir = mkdirMatch.Groups[1].Value.Trim('/', '\\', '"', '\'');
                            step.File = "_create_directory";
                            step.Change = lastImpliedDir;
                            await EmitLog(true, "warn",
                                $"Converted directory manipulation command '{step.Change}' to a _create_directory step.", ct: ct);
                        }
                        else
                        {
                            plan.Plan.RemoveAt(i);
                            i--;
                            await EmitLog(true, "warn", "Removed unparseable directory manipulation command.", ct: ct);
                        }
                    }
                }
                // A _create_directory step (not just a converted mkdir command) also establishes
                // the implied directory for subsequent pathless _create_file steps.
                if (string.Equals(step.File, "_create_directory", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(step.Change))
                {
                    lastImpliedDir = step.Change.Trim('/', '\\', '"', '\'');
                }
                if (string.Equals(step.File, "_create_file", StringComparison.OrdinalIgnoreCase) && lastImpliedDir != null)
                {
                    if (!step.Change.Contains("/") && !step.Change.Contains("\\"))
                    {
                        step.Change = $"Create file in directory {lastImpliedDir}: {step.Change}";
                    }
                }
            }
        }
        if (plan?.Plan != null && IsVisualLayoutTask(userPrompt))
        {
            var allChanges = string.Join(" ", plan.Plan.Select(s => s.Change ?? ""));
            if (Regex.IsMatch(allChanges, @"\b(remove|delete|hide)\b", RegexOptions.IgnoreCase))
                goto SkipLayoutCheck;
            var editFiles = plan.Plan
                .Select(s => (s.File ?? "").Replace('\\', '/'))
                .Where(AgentProjectUtilities.IsRelativePath)
                .Where(f => !AgentProjectUtilities.IsSpecialMarker(f))
                .ToList();
            var hasMarkup = editFiles.Any(f => Path.GetExtension(f).Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                                               Path.GetExtension(f).Equals(".cshtml", StringComparison.OrdinalIgnoreCase) ||
                                               Path.GetExtension(f).Equals(".razor", StringComparison.OrdinalIgnoreCase));
            var hasStylesheet = editFiles.Any(IsStylesheetPath);
            var hasScript = editFiles.Any(f => Path.GetExtension(f) is ".ts" or ".tsx" or ".js" or ".jsx");
            if (hasMarkup && !hasStylesheet)
            {
                return "Visual layout/positioning request is planned only against markup. Replan with a stylesheet/CSS step for positioning instead of moving DOM order. Keep markup edits only for missing elements or missing event wiring.";
            }
            var changes = string.Join(" ", plan.Plan.Select(s => s.Change ?? ""));
            if (Regex.IsMatch(changes, @"\b(click|touchstart|touchend|handler|method|function|wire|wiring|event)\b", RegexOptions.IgnoreCase) &&
                hasMarkup && !hasScript)
            {
                return "Template event wiring is planned without the component script/context. Replan to inspect or edit the .ts/.js component before changing handlers, so method names are verified instead of invented.";
            }
        }
    SkipLayoutCheck:;
        if (plan?.Plan != null && plan.Plan.Count >= 2)
        {
            var splitStopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "method", "endpoint", "after", "before", "using", "into", "with",
                "this", "that", "from", "which", "their", "there", "about",
                "would", "could", "should", "inline", "also", "will", "been",
                "have", "does", "just", "more", "than", "then", "when", "what"
            };
            for (var i = 0; i < plan.Plan.Count - 1; i++)
            {
                var s1 = plan.Plan[i];
                var s2 = plan.Plan[i + 1];
                if (string.IsNullOrWhiteSpace(s1.File) || string.IsNullOrWhiteSpace(s2.File)) continue;
                if (!string.Equals(s1.File, s2.File, StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(s1.File.Replace('\\', '/'));
                if (!string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase)) continue;
                var c1 = s1.Change ?? "";
                var c2 = s2.Change ?? "";
                if (!Regex.IsMatch(c1, @"\b(add|create|insert)\b.*\b(method|endpoint|handler)\b", RegexOptions.IgnoreCase)) continue;
                if (!Regex.IsMatch(c2, @"\b(add|create|insert)\b.*\b(method|endpoint|handler)\b", RegexOptions.IgnoreCase)) continue;
                var words1 = Regex.Matches(c1, @"\b[a-zA-Z]{4,}\b")
                    .Select(m => m.Value.ToLowerInvariant())
                    .Where(w => !splitStopWords.Contains(w) && !Regex.IsMatch(w, @"^(add|create|insert|post|get|put|delete)$"))
                    .ToHashSet();
                var words2 = Regex.Matches(c2, @"\b[a-zA-Z]{4,}\b")
                    .Select(m => m.Value.ToLowerInvariant())
                    .Where(w => !splitStopWords.Contains(w) && !Regex.IsMatch(w, @"^(add|create|insert|post|get|put|delete)$"))
                    .ToHashSet();
                var overlap = words1.Intersect(words2, StringComparer.OrdinalIgnoreCase).Count();
                if (overlap >= 3)
                {
                    return $"Steps {i + 1} and {i + 2} both target {s1.File} and share {overlap} overlapping keywords " +
                           $"({string.Join(", ", words1.Intersect(words2, StringComparer.OrdinalIgnoreCase).Take(5))}). " +
                           "They describe the same endpoint/feature and should be one step. " +
                           "If one step is a setup/prerequisite, keep it as its own _sql_migration step for schema " +
                           "(CREATE TABLE goes in a migrations/*.sql file) instead of making it a separate endpoint.";
                }
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine("You are validating a code-change plan. Determine if the plan makes sense and is complete given the user's request.");
        sb.AppendLine("Check each step for:");
        sb.AppendLine("- Does the file path look reasonable for the change?");
        sb.AppendLine("- Is the change description clear and actionable?");
        sb.AppendLine("- Are steps in the right order (commands before edits)?");
        sb.AppendLine("- Are any steps identical (invalid)?");
        sb.AppendLine("- Are any steps incomplete?");
        sb.AppendLine("- Does the plan actually address the user's request?");
        sb.AppendLine();
        sb.AppendLine("Respond with a single JSON object:");
        sb.AppendLine("{\"valid\": true}  or  {\"valid\": false, \"reason\": \"short explanation of what's wrong\"}");
        sb.AppendLine();
        sb.AppendLine("### USER REQUEST ###");
        sb.AppendLine(userPrompt);
        sb.AppendLine();
        sb.AppendLine("### PLAN ###");
        sb.AppendLine(JsonSerializer.Serialize(plan!.Plan, new JsonSerializerOptions { WriteIndented = true }));
        var (raw, _, err) = await CallLlmRaw(
            "You validate code-change plans. Output ONLY a JSON object with a \"valid\" boolean and optional \"reason\". No extra text, no markdown fences.",
            sb.ToString(), ct, _infiniteTimeout, maxTokens: 256);
        if (!string.IsNullOrWhiteSpace(err) || string.IsNullOrWhiteSpace(raw))
            return null;
        var cleaned = raw.Trim();
        if (cleaned.StartsWith('{') == false)
        {
            var fb = cleaned.IndexOf('{');
            var lb = cleaned.LastIndexOf('}');
            if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];
        }
        try
        {
            using var jDoc = JsonDocument.Parse(cleaned);
            var root = jDoc.RootElement;
            if (root.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.False)
            {
                var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : "Plan validation failed";
                return reason;
            }
        }
        catch { }
        return null;
    }
    private async Task<AgentPlan?> AnalyzePromptAndPlanCodeChanges(
     string prompt, string discoveryContext, string projectRoot, bool emitSse,
     CancellationToken ct = default, string? steeringContext = null)
    {
        var cfg = await LoadConfigAsync();
        var planningPrompt = BuildPlanningPrompt(await FilterToolsForStepAsync(prompt, cfg.enabledTools, ct));
        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("### TASK ###");
        userPrompt.AppendLine(prompt);
        if (!string.IsNullOrWhiteSpace(steeringContext))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("### USER STEERING ###");
            userPrompt.AppendLine(steeringContext);
        }
        userPrompt.AppendLine();
        userPrompt.AppendLine("### PROJECT ROOT ###");
        userPrompt.AppendLine(projectRoot);
        userPrompt.AppendLine("### DISCOVERY CONTEXT (only use paths listed here) ###");
        userPrompt.AppendLine(BuildPlannerDiscoveryContext(discoveryContext));
        const int MaxRetries = 3;
        string? raw = null;
        string? llmError = null;
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 1)
                await EmitLog(emitSse, "warn", $"Retrying plan generation (attempt {attempt}/{MaxRetries})...", ct: ct);
            else
                await EmitLog(emitSse, "info", "Generating plan...", ct: ct);
            Console.WriteLine($"### CALLING LLM WITH PROMPT >>> {planningPrompt} >>> {userPrompt}");
            (raw, _, llmError) = await CallLlmRawStreaming(
                planningPrompt, userPrompt.ToString(), emitSse, ct,
                requestTimeout: _infiniteTimeout, maxTokens: 2048);
            if (string.IsNullOrWhiteSpace(raw))
            {
                await EmitLog(emitSse, "error",
                    $"LLM returned empty plan response: {llmError ?? "no content"}", ct: ct);
                continue;
            }
            AgentPlan? plan = AgentPlanParsing.ParsePlan(raw);
            if (plan == null && (raw.Contains("<<<STEP", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("### STEP", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("STEP", StringComparison.OrdinalIgnoreCase)))
            {
                plan = AgentPlanParsing.ParseDelimitedPlan(raw);
            }
            if (plan == null)
            {
                plan = await RecoverPlanFromRamblingAsync(emitSse, ct, raw);
            }
            if (plan == null)
            {
                bool containsLLMError = false;
                bool containsLLMLoading = false;
                if (!string.IsNullOrEmpty(raw))
                {
                    if (raw.ToLower().Contains("error"))
                    {
                        containsLLMError = true;
                    }
                    if (raw.ToLower().Contains("loading model"))
                    {
                        containsLLMLoading = true;
                    }
                }
                string errorMessage = containsLLMLoading ? " Model Loading. Please retry after a short period of time."
                                        : containsLLMError ? " LLM Returned Error state. Check LLM."
                                        : "";
                await EmitLog(emitSse, "error", "Failed to parse plan." + errorMessage, raw, ct: ct);
                continue;
            }
            var webViolation = DetectMissingWebSearch(prompt, plan);
            if (webViolation != null)
                await EmitLog(emitSse, "warn", $"Plan may need web search: {webViolation}", ct: ct);
            if (plan.Plan != null && plan.Plan.Count > 1)
            {
                var uniqueSteps = new List<PlanStep>();
                for (var i = 0; i < plan.Plan.Count; i++)
                {
                    var step = plan.Plan[i];
                    var normChange = NormalizeChangeForDedup(step.Change);
                    var isDuplicate = false;
                    foreach (var existing in uniqueSteps)
                    {
                        if (!string.Equals(step.File, existing.File, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var existingNorm = NormalizeChangeForDedup(existing.Change);
                        var similarity = CalculateChangeSimilarity(normChange, existingNorm);
                        if (similarity >= 0.8)
                        {
                            isDuplicate = true;
                            await EmitLog(emitSse, "warn", $"Removed duplicate plan step (similarity {similarity:P0}): [{step.File}] {step.Change}", ct: ct);
                            break;
                        }
                    }
                    if (!isDuplicate)
                    {
                        uniqueSteps.Add(step);
                    }
                }
                plan.Plan = uniqueSteps;
            }
            await EmitLog(emitSse, "info",
                $"Plan: {plan?.Plan?.Count ?? 0} step(s) — score {plan?.Score ?? 0}/100", new { plan }, ct: ct);
            return plan;
        }
        await EmitLog(emitSse, "error",
            $"LLM failed to produce a valid plan after {MaxRetries} attempts.", ct: ct);
        return null;
    }
    private async Task<(AgentPlan? plan, string? error)> ParseAndScore(
        string raw, bool emitSse, CancellationToken ct)
    {
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var m = Regex.Match(cleaned, @"```(?:text|json)?\s*([\s\S]*?)```",
                RegexOptions.IgnoreCase);
            cleaned = m.Success ? m.Groups[1].Value.Trim() : cleaned.TrimStart('`');
        }
        AgentPlan? parsed = null;
        if (cleaned.Contains("<<<STEP", StringComparison.OrdinalIgnoreCase))
            parsed = AgentPlanParsing.ParseDelimitedPlan(cleaned);
        if (parsed == null)
            parsed = AgentPlanParsing.ParsePlan(cleaned);
        if (parsed == null)
        {
            await EmitLog(emitSse, "error", "Failed to parse plan.", cleaned, ct: ct);
            return (null, "Response was unparseable.");
        }
        var violations = GetPlanSizeViolations(parsed);
        if (violations.Count > 0)
        {
            await EmitLog(emitSse, "warn",
                $"{violations.Count} oversized anchor(s) — will attempt resolve at execution time",
                ct: ct);
        }
        return (parsed, null);
    }
    private static string? DetectMissingWebSearch(string prompt, AgentPlan plan)
    {
        var lower = prompt.ToLowerInvariant();
        var triggers = new[] { "search for", "look up", "find out", "up to date", "up-to-date" };
        var hit = triggers.FirstOrDefault(t => lower.Contains(t));
        if (hit == null) return null;
        var hasWebStep = plan.Plan?.Any(s =>
            s.File.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
            s.File.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase)) ?? false;
        if (hasWebStep) return null;
        return $"Prompt contains \"{hit}\" but plan has no _web_search step.";
    }

    private async Task<(string discoveryText, List<object> steps)> RunLightBootstrap(
        string prompt, List<string> attachedFiles, string projectRoot, bool emitSse, CancellationToken ct = default)
    {
        await EmitLog(emitSse, "info", "Fast-path bootstrap: reading attached files only");
        var files = (attachedFiles ?? new List<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();
        if (files.Count == 0) return ("", new List<object>());
        var sb = new StringBuilder();
        sb.AppendLine("Attached files (edit these paths only):");
        foreach (var f in files)
            sb.AppendLine($"  - {f.Replace('\\', '/')}");
        var allResults = new List<object>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var relPath = f.Replace('\\', '/');
            var fullPath = Path.GetFullPath(
                Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            var result = new Dictionary<string, object?>
            {
                ["index"] = allResults.Count,
                ["type"] = "read",
                ["description"] = $"Read attached {f}",
                ["status"] = "running"
            };
            try
            {
                if (!AgentProjectUtilities.IsPathUnderRoot(fullPath, projectRoot))
                {
                    result["status"] = "error";
                    result["error"] = "Path outside root";
                }
                else if (!System.IO.File.Exists(fullPath))
                {
                    result["status"] = "error";
                    result["error"] = "File not found";
                }
                else
                {
                    result["path"] = relPath;
                    result["output"] = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                    result["status"] = "done";
                    sb.AppendLine($"\n### {relPath}\n```\n{result["output"]}\n```");
                }
            }
            catch (Exception ex)
            {
                result["status"] = "error";
                result["error"] = ex.Message;
            }
            result["status"] = AgentTextUtilities.NormalizeUiStatus(result["status"]?.ToString());
            allResults.Add(result);
        }
        if (emitSse)
        {
            var succeeded = allResults.Count(r =>
                r is Dictionary<string, object?> d &&
                d.GetValueOrDefault("status")?.ToString() == "done");
            var total = allResults.Count;
            await EmitLog(emitSse, "info",
                $"Read {total} attached file(s), {succeeded} succeeded — no auto discovery; the agent can _explore or _discover if it needs more context");
            var fileList = allResults
                .Select(r => r is Dictionary<string, object?> d
                    ? new { index = d.GetValueOrDefault("index"), path = d.GetValueOrDefault("path"), status = d.GetValueOrDefault("status") }
                    : null)
                .Where(x => x != null)
                .ToList();
            await SendSse(Response, "batch-read", new
            {
                total,
                succeeded,
                files = fileList
            }, ct);
        }
        return (sb.ToString(), allResults);
    }
    private async Task<(string discoveryText, List<object> steps)> RunBootstrapDiscovery(
        string prompt, string projectRoot, bool emitSse,
        List<string>? attachedFiles = null, CancellationToken ct = default)
    {
        if (attachedFiles != null && attachedFiles.Count > 0)
            return await RunLightBootstrap(prompt, attachedFiles, projectRoot, emitSse, ct);
        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER: enumerating project files…", ct: ct);
        var allSteps = new List<object>();
        var listStep = new AgentStep { Index = 0, Type = "list", Path = "", Description = "Auto: list project root" };
        var listResults = await ExecuteDiscoveryStepsConcurrent(
            new List<AgentStep> { listStep }, projectRoot, 0, emitSse);
        allSteps.AddRange(listResults);
        if (!Directory.Exists(projectRoot)) return ("", allSteps);
        var allFiles = EnumerateProjectFiles(projectRoot);
        if (allFiles.Count == 0) return ("", allSteps);
        // NEW: BM25-first auto-read — rank the whole project against the task lexically and
        // auto-read the top files so the planner starts with the right context instead of
        // flailing with _explore/_discover one file at a time.
        var bm25Top = Bm25Scorer.ScoreProjectFiles(prompt, projectRoot, allFiles, ct);
        var toRead = bm25Top.Select(x => x.file).Take(6).ToList();
        toRead = AddTemplateStyleSiblings(toRead, projectRoot);
        toRead = toRead
            .Where(f =>
            {
                var full = Path.GetFullPath(Path.Combine(projectRoot, f.Replace('/', Path.DirectorySeparatorChar)));
                return System.IO.File.Exists(full) && AgentProjectUtilities.IsPathUnderRoot(full, projectRoot);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        var rankedList = toRead
            .Select(f =>
            {
                var hit = bm25Top.FirstOrDefault(x => string.Equals(x.file, f, StringComparison.OrdinalIgnoreCase));
                return hit.file != null ? Bm25Scorer.FormatHits(hit.file, hit.score, hit.tokenHits) : f;
            })
            .ToList();
        await EmitLog(emitSse, "info",
            $"Phase 1 — {allFiles.Count} file(s) indexed ({_fileHints.GetFilesForPrompt(prompt, projectRoot).Count} hint(s)); " +
            $"BM25 auto-read {toRead.Count} task-relevant file(s): {string.Join("; ", rankedList)}",
            new { RankedBm25 = bm25Top }, ct: ct);
        var sb = new StringBuilder();
        sb.AppendLine("ONLY use paths that appear below. Do NOT invent paths.");
        sb.AppendLine();
        foreach (var item in allSteps)
        {
            if (item is not Dictionary<string, object?> r) continue;
            var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
            if (!r.TryGetValue("output", out var output) ||
                output == null || string.IsNullOrEmpty(output.ToString())) continue;
            sb.AppendLine($"### {type} {r.GetValueOrDefault("path") ?? r.GetValueOrDefault("description")}");
            sb.AppendLine(output.ToString());
            sb.AppendLine();
        }
        if (toRead.Count > 0)
        {
            var readPlan = toRead.Select((f, i) => new AgentStep
            {
                Index = i,
                Type = "read",
                Path = f,
                Description = $"Bootstrap: read {f}",
                Prompt = prompt
            }).ToList();
            var readResults = await ExecuteDiscoveryStepsConcurrent(readPlan, projectRoot, allSteps.Count, emitSse);
            var fileCharBudget = (await LoadConfigAsync()).maxFullFileTokens * 4;
            // Aggregate budget: stop pulling files once the auto-read section approaches the
            // overall context target so a big repo can't flood the prompt with top-ranked files.
            var totalBudget = Math.Max(40000, fileCharBudget * 3);
            var usedChars = 0;
            var mergedCount = 0;
            foreach (var r in readResults)
            {
                if (r is not Dictionary<string, object?> d) continue;
                var path = d.GetValueOrDefault("path")?.ToString();
                var output = d.GetValueOrDefault("output")?.ToString();
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(output)) continue;
                if (d.GetValueOrDefault("status")?.ToString() != "done") continue;
                // THINKING MUST NEVER SEE TRUNCATED CONTEXT: keep the auto-read file whole.
                // The per-file cap below silently clipped large components (e.g. a globe
                // component) so the planner could not see the method it had to edit. Rely on
                // the aggregate budget to drop whole files instead of slicing them.
                var snippet = output;
                if (usedChars + snippet.Length > totalBudget) break;
                usedChars += snippet.Length;
                sb.AppendLine($"### read {path}");
                sb.AppendLine("```");
                sb.AppendLine(snippet);
                sb.AppendLine("```");
                sb.AppendLine();
                allSteps.Add(d);
                mergedCount++;
                _fileHints.LearnFromGrepOutput(prompt, path, projectRoot);
            }
            await EmitLog(emitSse, "info",
                $"Phase 1 complete — {allSteps.Count} step(s), BM25 auto-read {mergedCount} task-relevant file(s) into context", ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "info",
                $"Phase 1 complete — {allSteps.Count} step(s), no files auto-read (BM25 found no strong task matches; exploration is on-demand via _explore/_discover)", ct: ct);
        }
        return (sb.ToString(), allSteps);
    }
    private static List<string> EnumerateProjectFiles(string projectRoot)
    {
        if (!Directory.Exists(projectRoot)) return new List<string>();
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", "dist", ".angular", "packages", ".vs", ".idea", "out-tsc", "out-tsc-e2e", "node_modules" };
        return Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/'))
            .Where(rel => !skipDirs.Any(d =>
                rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase) ||
                rel.Contains("/" + d + "/", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// BM25 lexical cross-check shared by the full discovery path and the fast
    /// attached-files path: scans the whole project, ranks files against the task,
    /// and any file BM25 ranks highly but that isn't already being read gets a
    /// close LLM evaluation before being added to the read list.
    /// </summary>
    private async Task<List<string>> RunBm25CrossCheck(
        string prompt, List<string> toRead, string projectRoot, List<string> allFiles,
        bool emitSse, CancellationToken ct)
    {
        if (allFiles.Count == 0) return toRead;
        await SendSse(Response, "phase", new { phase = "discover", message = "BM25 scanning project files...", contextSize = 0 }, ct);
        var bm25Top = Bm25Scorer.ScoreProjectFiles(prompt, projectRoot, allFiles, ct);
        if (bm25Top.Count > 0)
        {
            var ranked = bm25Top.Select((x, i) => $"[{i + 1}] {Bm25Scorer.FormatHits(x.file, x.score, x.tokenHits)} (score {x.score:0.0})");
            await EmitLog(emitSse, "info", $"🔎 BM25 ranked {bm25Top.Count} file(s) by lexical scoring: {string.Join(", ", ranked)}",
                new { RankedBm25 = bm25Top }, ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "info", "🔎 BM25 scan: no files matched the task lexically", ct: ct);
            return toRead;
        }
        var bm25Only = bm25Top
            .Select(x => x.file)
            .Where(f => !toRead.Any(t => string.Equals(t, f, StringComparison.OrdinalIgnoreCase)))
            .Take(5)
            .ToList();
        if (bm25Only.Count > 0)
        {
            await EmitLog(emitSse, "info",
                $"🔎 BM25 cross-check: {bm25Only.Count} file(s) ranked highly but not in the current read list — evaluating them closely: {string.Join(", ", bm25Only)}",
                new { Bm25Only = bm25Only, CurrentList = toRead }, ct: ct);
            var kept = await EvaluateBm25DiscrepanciesWithLlm(prompt, bm25Only, projectRoot, emitSse, ct);
            if (kept.Count > 0)
            {
                toRead = toRead.Concat(kept).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
                await EmitLog(emitSse, "info", $"🔎 BM25 cross-check kept {kept.Count} file(s): {string.Join(", ", kept)}", new { Kept = kept }, ct: ct);
            }
            else
            {
                await EmitLog(emitSse, "info", "🔎 BM25 cross-check: evaluated, no additional files worth reading", ct: ct);
            }
        }
        else
        {
            await EmitLog(emitSse, "info", "🔎 BM25 cross-check: all top lexical matches already in the read list", ct: ct);
        }
        return toRead;
    }

    /// <summary>
    /// For every selected .ts component, also surface its same-name template/style
    /// siblings (.html, .css, .scss, .less, .vue) — e.g. selecting music.component.ts
    /// must also read music.component.html, where templates like the popupPanel div
    /// actually live.
    /// </summary>
    private static List<string> AddTemplateStyleSiblings(List<string> toRead, string projectRoot)
    {
        var withSiblings = new List<string>();
        foreach (var f in toRead)
        {
            withSiblings.Add(f);
            var lower = f.ToLowerInvariant();
            if (!lower.EndsWith(".ts") || lower.Contains(".spec.") || lower.Contains(".test.")) continue;
            var basePath = f[..^3];
            foreach (var ext in new[] { ".html", ".css", ".scss", ".less", ".vue" })
            {
                var sibling = basePath + ext;
                try
                {
                    if (System.IO.File.Exists(Path.GetFullPath(
                            Path.Combine(projectRoot, sibling.Replace('/', Path.DirectorySeparatorChar)))))
                        withSiblings.Add(sibling);
                }
                catch { }
            }
        }
        return withSiblings.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The _discover tool: a project-wide context exploration the planner invokes
    /// STRICTLY when it needs more context and doesn't know which file to read.
    /// Runs deterministic candidates (persisted hints + task-type heuristics), an
    /// LLM selection pass if the pool is large, the BM25 lexical scan (ranked,
    /// scores logged), template/style sibling expansion, then reads everything and
    /// merges the sections into the discovery context.
    /// </summary>
    private async Task<string> RunDiscoveryToolAsync(
        string prompt, string discoveryContext, string projectRoot, bool emitSse, CancellationToken ct)
    {
        await EmitLog(emitSse, "info", "🔎 Discovery tool: scanning project for task-relevant files…", ct: ct);
        await SendSse(Response, "phase", new { phase = "discover", message = "Discovery tool: scanning project files...", contextSize = 0 }, ct);
        var allFiles = EnumerateProjectFiles(projectRoot);
        if (allFiles.Count == 0)
        {
            await EmitLog(emitSse, "info", "🔎 Discovery tool: no project files found", ct: ct);
            return discoveryContext;
        }
        var hintedFiles = _fileHints.GetFilesForPrompt(prompt, projectRoot)
            .Where(f => allFiles.Any(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase)))
            .Take(4).ToList();
        var heuristicCandidates = AgentDiscovery.ApplyTaskTypeHeuristics(prompt, allFiles);
        var pool = hintedFiles.Concat(heuristicCandidates).Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList();
        var toRead = pool.Take(6).ToList();
        if (pool.Count > 6)
        {
            var candidatesText = string.Join(", ", pool);
            if (candidatesText.Length > 75) candidatesText = candidatesText[..75] + "...";
            await EmitLog(emitSse, "info", $"🔎 Discovery tool: selecting from {pool.Count} candidates…", new { Candidates = candidatesText }, ct: ct);
            var selected = await SelectRelevantFilesWithLlm(prompt, pool, emitSse, ct);
            toRead = hintedFiles.Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        }
        toRead = toRead.Where(f =>
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, f.Replace('/', Path.DirectorySeparatorChar)));
            return System.IO.File.Exists(full) && AgentProjectUtilities.IsPathUnderRoot(full, projectRoot);
        }).ToList();
        toRead = await RunBm25CrossCheck(prompt, toRead, projectRoot, allFiles, emitSse, ct);
        var preSiblingCount = toRead.Count;
        toRead = AddTemplateStyleSiblings(toRead, projectRoot);
        var addedSiblings = toRead.Skip(preSiblingCount).ToList();
        if (addedSiblings.Count > 0)
        {
            await EmitLog(emitSse, "info",
                $"🔎 Discovery tool: adding {addedSiblings.Count} template/style sibling(s): {string.Join(", ", addedSiblings)}",
                ct: ct);
        }
        toRead = toRead
            .Where(f => !discoveryContext.Contains($"### read {f.Replace('\\', '/')}"))
            .ToList();
        if (toRead.Count == 0)
        {
            await EmitLog(emitSse, "info", "🔎 Discovery tool: all relevant files are already in context", ct: ct);
            return discoveryContext;
        }
        await EmitLog(emitSse, "info", $"🔎 Discovery tool: reading {toRead.Count} file(s): {string.Join(", ", toRead)}", ct: ct);
        var readPlan = toRead.Select((f, i) => new AgentStep
        {
            Index = i,
            Type = "read",
            Path = f,
            Description = $"Discover: read {f}",
            Prompt = prompt
        }).ToList();
        var readResults = await ExecuteDiscoveryStepsConcurrent(readPlan, projectRoot, 0, emitSse);
        var merged = new StringBuilder(discoveryContext);
        foreach (var r in readResults)
        {
            if (r is not Dictionary<string, object?> d) continue;
            var path = d.GetValueOrDefault("path")?.ToString();
            var output = d.GetValueOrDefault("output")?.ToString();
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(output)) continue;
            if (d.GetValueOrDefault("status")?.ToString() != "done") continue;
            merged.AppendLine($"### read {path}");
            merged.AppendLine("```");
            merged.AppendLine(output);
            merged.AppendLine("```");
            merged.AppendLine();
            _fileHints.LearnFromGrepOutput(prompt, path, projectRoot);
        }
        return merged.ToString();
    }

    private async Task<List<string>> SelectRelevantFilesWithLlm(
        string prompt, List<string> candidates, bool emitSse, CancellationToken ct)
    {
        if (candidates.Count == 0) return new List<string>();
        var promptTokens = AgentDiscovery.ExtractMeaningfulKeywords(prompt.ToLowerInvariant());
        var deterministic = candidates
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var score = 0;
                foreach (var token in promptTokens)
                {
                    if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 6;
                    if (f.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 2;
                }
                return (file: f, score);
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.file.Length)
            .Take(3)
            .Select(x => x.file)
            .ToList();
        const string system =
            "You are a file relevance selector for a coding agent. Given a task and candidate files, pick the 3-7 files most likely to own the requested change or define types/imports needed for it. " +
            "Prefer exact filename/path/symbol matches, neighboring component/template/style files, and files named in the task. Avoid generated, minified, dependency, build, or broad entry-point files unless the task clearly targets them. " +
            "CRITICAL: a keyword match in the filename is NOT enough — the word must be used in the SAME SENSE as the file's purpose. For example, if the task says \"let the user increase\", 'user' is a generic person, NOT the user.component. Ignore incidental matches. " +
            "Output ONLY valid JSON, no markdown: {\"files\": [\"path1\", \"path2\"]}";
        var user = $"Task: {prompt}\n\nCandidate files:\n{string.Join("\n", candidates)}\n\nDeterministic keyword-matched files (already counted below — include ONLY if genuinely relevant to the task, not just because a word matches):\n{string.Join("\n", deterministic)}\n\nSelect 3-7 max.";
        var (raw, _, err) = await CallLlmRaw(system, user, ct, _infiniteTimeout);
        if (string.IsNullOrWhiteSpace(raw))
            return deterministic.Concat(candidates).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var s = cleaned.IndexOf('{'); var e = cleaned.LastIndexOf('}');
            if (s >= 0 && e > s) cleaned = cleaned[s..(e + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("files", out var filesEl) &&
                filesEl.ValueKind == JsonValueKind.Array)
            {
                var selected = filesEl.EnumerateArray()
                    .Select(el => el.GetString()?.Replace('\\', '/') ?? "")
                    .Where(f => !string.IsNullOrWhiteSpace(f) &&
                                candidates.Any(c => string.Equals(c, f, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                selected = selected.Take(7).ToList();
                if (selected.Count > 0) return selected;
            }
        }
        catch { }
        return deterministic.Concat(candidates).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
    }
    private async Task<List<string>> EvaluateBm25DiscrepanciesWithLlm(
        string prompt, List<string> bm25OnlyFiles, string projectRoot, bool emitSse, CancellationToken ct)
    {
        if (bm25OnlyFiles.Count == 0) return new List<string>();
        const string system =
            "You are a file relevance evaluator for a coding agent. A lexical search (BM25) flagged the files below as " +
            "potentially relevant to the task, but the agent's LLM selector did NOT pick them. Your job: read each file " +
            "CLOSELY and judge whether it genuinely matters for the task — it owns part of the requested change, defines " +
            "types/symbols/templates/styles the change depends on, or is the place where behavior visible to the user lives. " +
            "Do NOT keep a file merely because a keyword appears; judge its actual content against the task. " +
            "Output ONLY valid JSON, no markdown: {\"keep\": [\"path1\"], \"drop\": [\"path2\"]} — use paths exactly as listed.";
        var sb = new StringBuilder();
        sb.AppendLine($"Task: {prompt}");
        sb.AppendLine();
        sb.AppendLine("Candidate files (BM25-flagged, LLM missed):");
        var existing = new List<string>();
        foreach (var rel in bm25OnlyFiles)
        {
            var full = Path.GetFullPath(Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!System.IO.File.Exists(full) || !AgentProjectUtilities.IsPathUnderRoot(full, projectRoot)) continue;
            existing.Add(rel);
            string text;
            try
            {
                var fi = new FileInfo(full);
                if (fi.Length > 512 * 1024) { text = "(file too large to inline — read it if needed)"; }
                else text = System.IO.File.ReadAllText(full);
            }
            catch { text = "(unreadable)"; }
            // Full file content — this evaluator decides whether a file matters to the task;
            // an 8k-char slice could hide the very code the file is relevant for.
            sb.AppendLine($"\n=== {rel} ===\n```\n{text}\n```");
        }
        if (existing.Count == 0) return new List<string>();
        var (raw, _, _) = await CallLlmRaw(system, sb.ToString(), ct, _infiniteTimeout, maxTokens: 600);
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            var s = cleaned.IndexOf('{'); var e = cleaned.LastIndexOf('}');
            if (s >= 0 && e > s) cleaned = cleaned[s..(e + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            var kept = new List<string>();
            if (doc.RootElement.TryGetProperty("keep", out var keepEl) && keepEl.ValueKind == JsonValueKind.Array)
            {
                kept = keepEl.EnumerateArray()
                    .Select(el => el.GetString()?.Replace('\\', '/') ?? "")
                    .Where(f => !string.IsNullOrWhiteSpace(f) &&
                                existing.Any(c => string.Equals(c, f, StringComparison.OrdinalIgnoreCase)))
                    .Take(4)
                    .ToList();
            }
            if (kept.Count > 0) return kept;
        }
        catch { }
        return new List<string>();
    }
    private async Task<(string trimmedSkeleton, string architectureNote)> TrimSkeletonWithLlm(
        AgentSkeleton.SkeletonResult skeleton, string prompt, bool emitSse, CancellationToken ct)
    {
        try
        {
            if (skeleton.Paths.Count == 0) return ("", "");

            var keywords = AgentDiscovery.ExtractMeaningfulKeywords(prompt.ToLowerInvariant());
            var scored = skeleton.Paths
                .Select(p =>
                {
                    var name = Path.GetFileNameWithoutExtension(p);
                    var score = 0;
                    foreach (var token in keywords)
                    {
                        if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 6;
                        if (p.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 2;
                    }
                    return (path: p, score);
                })
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.path.Length)
                .ToList();

            List<string> selected;
            string? note = null;

            if (scored.Count <= 12)
            {
                selected = scored.Select(x => x.path).ToList();
                note = await ExtractArchitectureNote(skeleton.Tree, prompt, ct);
            }
            else
            {
                var topCandidates = scored.Take(60).Select(x => x.path).ToList();
                var (streamedResponse, streamErr) = await StreamTrimLlm(prompt, topCandidates, ct);
                if (!string.IsNullOrWhiteSpace(streamedResponse))
                {
                    try
                    {
                        var cleaned = streamedResponse.Trim();
                        if (cleaned.StartsWith("```"))
                        {
                            var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                            if (m.Success) cleaned = m.Groups[1].Value.Trim();
                        }
                        var sIdx = cleaned.IndexOf('{'); var eIdx = cleaned.LastIndexOf('}');
                        if (sIdx >= 0 && eIdx > sIdx) cleaned = cleaned[sIdx..(eIdx + 1)];
                        using var doc = JsonDocument.Parse(cleaned);
                        if (doc.RootElement.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                        {
                            var llmSelected = filesEl.EnumerateArray()
                                .Select(el => el.GetString()?.Replace('\\', '/') ?? "")
                                .Where(f => !string.IsNullOrWhiteSpace(f) &&
                                            topCandidates.Any(c => string.Equals(c, f, StringComparison.OrdinalIgnoreCase)))
                                .ToList();
                            selected = scored.Where(s => llmSelected.Contains(s.path, StringComparer.OrdinalIgnoreCase))
                                .Select(s => s.path).Take(8).ToList();
                        }
                        else { selected = scored.Take(6).Select(x => x.path).ToList(); }
                        if (doc.RootElement.TryGetProperty("architectureNote", out var an))
                            note = TruncateArchitectureNote(an.GetString());
                    }
                    catch { selected = scored.Take(6).Select(x => x.path).ToList(); }
                }
                else
                {
                    selected = scored.Take(6).Select(x => x.path).ToList();
                }
            }

            if (selected.Count == 0)
                selected = skeleton.Paths.OrderBy(p => p.Length).Take(8).ToList();

            note ??= await ExtractArchitectureNote(skeleton.Tree, prompt, ct);
            var trimmedTree = BuildTrimmedTree(selected);
            return (trimmedTree, note);
        }
        catch
        {
            return ("", "");
        }
    }

    private async Task<(string raw, string? error)> StreamTrimLlm(
        string prompt, List<string> candidates, CancellationToken ct)
    {
        var system =
            "You are a project architect reviewing a file tree for a coding agent. " +
            "Given the user's task and candidate files, select the 3-7 files most relevant to the task and provide a one-sentence architecture note.\n" +
            "Prefer exact filename/path/symbol matches, neighboring files, and files named in the task. Avoid generated/dependency files.\n" +
            "Architecture note: mention the platform, testing framework, and any notable conventions. Max 200 characters.\n" +
            "Output ONLY valid JSON, no markdown: {\"files\": [\"path1\", \"path2\"], \"architectureNote\": \"...\"}";
        var user = $"Task: {prompt}\n\nCandidates:\n{string.Join("\n", candidates)}\n\nSelect 3-7 files and write the architecture note.";
        return await StreamLlmThinking(system, user, ct, _infiniteTimeout, 500);
    }

    private async Task<(string raw, string? error)> StreamLlmThinking(
        string systemPrompt, string userMessage, CancellationToken ct,
        TimeSpan? requestTimeout = null, int? maxTokens = null)
    {
        var baseUrl = await GetLlamaBaseUrl();
        var model = await GetLlamaModel();
        var client = _clientFactory.CreateClient("llama");
        client.Timeout = _infiniteTimeout;
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userMessage  }
        };
        var timeout = requestTimeout ?? _infiniteTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var cfg = await LoadConfigAsync();
        var mt = maxTokens ?? cfg.defaultMaxTokens;
        var reqBody = new
        {
            model,
            messages,
            stream = true,
            temperature = 0.05,
            max_tokens = mt,
            repeat_penalty = 1.3,
            repeat_last_n = 256
        };
        var httpContent = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions") { Content = httpContent };
            var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            if (!resp.IsSuccessStatusCode)
            { var t = await resp.Content.ReadAsStringAsync(ct); return ("", $"HTTP {resp.StatusCode}"); }
            var stream = await resp.Content.ReadAsStreamAsync(linkedCts.Token);
            var reader = new StreamReader(stream);
            var sb = new StringBuilder();
            while (true)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().WaitAsync(linkedCts.Token);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("[DONE]")) break;
                if (!line.StartsWith("data: ")) continue;
                var data = line[6..].Trim();
                if (string.IsNullOrWhiteSpace(data)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                sb.Append(token);
                                await SendSse(Response, "token", new { token }, ct);
                            }
                        }
                    }
                }
                catch { }
            }
            var raw = sb.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return ("", "Empty LLM response");
            return (raw, null);
        }
        catch (TaskCanceledException) { return ("", "LLM request timed out"); }
        catch (Exception ex) { return ("", ex.Message); }
    }

    private static string BuildTrimmedTree(List<string> selectedFiles)
    {
        var sorted = selectedFiles
            .Select(f => (path: f, parts: f.Split('/')))
            .OrderBy(x => x.path)
            .ToList();
        var sb = new StringBuilder();
        sb.AppendLine("### PROJECT SKELETON (relevant files) ###");
        sb.AppendLine();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, parts) in sorted)
        {
            var indent = "";
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var dirPath = string.Join("/", parts.Take(i + 1));
                if (shown.Add(dirPath))
                    sb.Append(indent).AppendLine(parts[i] + "/");
                indent += "  ";
            }
            if (shown.Add(path))
                sb.Append(indent).AppendLine(parts.Last());
        }
        return sb.ToString();
    }

    private async Task<string> ExtractArchitectureNote(string fullTree, string prompt, CancellationToken ct)
    {
        var lines = fullTree.Split('\n')
            .Where(l => !l.Contains("node_modules") && !l.Contains("bin/") && !l.Contains("obj/"))
            .Take(100);
        var compact = string.Join("\n", lines);
        var system = "You are a project architect. Given a project file tree and the user's task, write ONE short sentence about the project's architecture that would help a developer new to this codebase. Mention the platform (e.g. Angular/.NET/Python), testing framework, and any notable conventions you observe. Max 200 characters. Output ONLY the sentence, no markdown, no JSON.";
        var user = $"Task: {prompt}\n\nFile tree (first 100 lines):\n{compact}";
        var (raw, err) = await StreamLlmThinking(system, user, ct, _infiniteTimeout, 100);
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return TruncateArchitectureNote(raw);
    }
    private static string TruncateArchitectureNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return "";
        note = note.Trim();
        if (note.Length <= 200) return note;
        var lastSpace = note.LastIndexOf(' ', 197);
        return note[..(lastSpace > 0 ? lastSpace : 197)] + "...";
    }
    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> Orchestrate(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct = default,
        List<string>? attachedFiles = null, bool skipContextReview = false,
        string? steeringContext = null, bool skipQualityCheck = false,
        AgentPlan? existingPlan = null, HashSet<int>? completedStepIndices = null,
        string? cardId = null, bool createTests = false, string? buildCommands = null)
    {
        _gracefulStop = false;
        var connectivityTask = CheckLlmConnectivity(projectRoot, emitSse, ct);

        var lower = prompt.ToLowerInvariant();
        var mightBeBuildRepair = lower.Contains("build") || lower.Contains("compile") ||
                                 lower.Contains("error") || lower.Contains("warning");
        if (mightBeBuildRepair)
        {
            if (buildCommands != null && buildCommands.Trim().Length > 0)
            {
                if (!await connectivityTask)
                    throw new InvalidOperationException("LLM connectivity check failed.");
                var isBuildRepair = await ClassifyIsBuildRepairPromptAsync(prompt, ct);
                if (isBuildRepair)
                {
                    return await RepairBuildPipeline(prompt, projectRoot, emitSse, buildCommands, ct);
                }
            }
            else
            {
                await EmitLog(emitSse, "warn", "Possible build repair prompt detected but no build commands provided — skipping repair.", new { prompt, buildCommands }, ct: ct);
            }
        }

        if (existingPlan != null && existingPlan.Plan.Count > 0)
        {
            if (!await connectivityTask)
                throw new InvalidOperationException("LLM connectivity check failed.");
            var resumeSteps = new List<object>();
            await ExecutePlan(prompt, projectRoot, emitSse, "", existingPlan, ct, resumeSteps,
                steeringContext: steeringContext, attachedFiles: attachedFiles,
                completedStepIndices: completedStepIndices, cardId: cardId);
            var resumeHasErrors = resumeSteps.OfType<Dictionary<string, object?>>()
                .Any(s => s.TryGetValue("status", out var st) && st?.ToString() == "error");
            var allStepsAlreadyDone = completedStepIndices != null && completedStepIndices.Count >= existingPlan.Plan.Count;
            bool resumeComplete = !resumeHasErrors && ((resumeSteps.Count > 0) || allStepsAlreadyDone);
            if (resumeHasErrors)
            {
                await EmitLog(emitSse, "error", "Resumed plan has step errors — task NOT complete", ct: ct);
            }
            return (resumeSteps, existingPlan, resumeComplete);
        }

        List<object> allSteps = new();
        AgentPlan? plan = null;
        bool pipelineComplete = true;

        var (unifiedSteps, unifiedPlan, unifiedComplete) = await StepResolutionPipeline(prompt, projectRoot, emitSse, ct,
                attachedFiles: attachedFiles, skipContextReview: skipContextReview,
                steeringContext: steeringContext, cardId: cardId, connectivityTask: connectivityTask);
        allSteps = unifiedSteps;
        plan = unifiedPlan;
        pipelineComplete = unifiedComplete;

        if (_gracefulStop)
        {
            _gracefulStop = false;
            return (allSteps, plan, false);
        }

        bool complete = pipelineComplete;
        var hasFatalStepErrors = allSteps.OfType<Dictionary<string, object?>>()
            .Any(s => s.TryGetValue("status", out var status) && s.TryGetValue("type", out var type)
                && status?.ToString() == "error" && type?.ToString() != "list");
        if (hasFatalStepErrors)
        {
            complete = false;
            await EmitLog(emitSse, "warn",
                "Task marked INCOMPLETE — one or more steps failed with errors. " +
                "Skipping LLM quality check since step failures are deterministic.", ct: ct);
        }
        if (complete && !skipQualityCheck && allSteps.Count > 0)
        {
            var hasDone = allSteps.OfType<Dictionary<string, object?>>()
                .Any(s => s.TryGetValue("type", out var t) && t?.ToString() == "done_signal");
            var verified = allSteps.OfType<Dictionary<string, object?>>()
                .Any(s => s.TryGetValue("type", out var t) && t?.ToString() == "verified_complete");
            if (verified) hasDone = true;
            if (!hasDone)
            {
                var (ok, reason) = await AssessCompletion(prompt, allSteps, projectRoot, ct, plan, attachedFiles: attachedFiles);
                if (ok && hasFatalStepErrors)
                {
                    ok = false;
                    reason = "Step errors present — overriding LLM completion assessment";
                }
                complete = ok;
                if (!ok)
                {
                    await EmitLog(emitSse, "warn", $"Quality check: {reason}", ct: ct);
                    var doneIndices = new HashSet<int>();
                    for (var i = 0; i < (plan?.Plan?.Count ?? 0); i++)
                    {
                        var result = allSteps.OfType<Dictionary<string, object?>>()
                            .LastOrDefault(s =>
                                s.TryGetValue("planItemIndex", out var pIdxObj) &&
                                pIdxObj is int pIdx &&
                                pIdx == i &&
                                s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                        if (result != null) doneIndices.Add(i);
                    }
                    var hasIncomplete = plan != null && doneIndices.Count < plan.Plan.Count;
                    if (hasIncomplete)
                    {
                        await EmitLog(emitSse, "info",
                           $"Replan: retrying {plan!.Plan.Count - doneIndices.Count} incomplete step(s)…", ct: ct);
                        var retryResults = new List<object>();
                        await ExecutePlan(prompt, projectRoot, emitSse, "", plan, ct, retryResults,
                            steeringContext: steeringContext, attachedFiles: attachedFiles,
                            completedStepIndices: doneIndices, cardId: cardId);
                        allSteps.AddRange(retryResults);
                        var (ok2, _) = await AssessCompletion(prompt, allSteps, projectRoot, ct, plan, attachedFiles: attachedFiles);
                        complete = ok2;
                    }
                    if (!complete && plan?.Plan?.Count > 0)
                    {
                        for (var i = 0; i < plan.Plan.Count; i++)
                        {
                            var step = plan.Plan[i];
                            var result = allSteps.OfType<Dictionary<string, object?>>()
                                .LastOrDefault(s =>
                                    s.TryGetValue("planItemIndex", out var pIdxObj) &&
                                    pIdxObj is int pIdx &&
                                    pIdx == i &&
                                    s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                    s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                            if (result != null) doneIndices.Add(i);
                        }
                    }
                    if (!complete && (plan?.Plan?.Count == 0 || doneIndices.Count == (plan?.Plan?.Count ?? 0)))
                    {
                        await EmitLog(emitSse, "info", "All steps done — checking for genuinely missing work…", ct: ct);
                        var scopedSteering = "The original plan's steps all succeeded. Only add steps for work the " +
                            "user EXPLICITLY requested that is still genuinely missing. Do NOT invent extra files, " +
                            "features, refactors, or improvements the user did not ask for. If nothing explicit is " +
                            "missing, return an empty plan." +
                            (string.IsNullOrWhiteSpace(steeringContext) ? "" : $"\n\n{steeringContext}");
                        var newSteps = await GenerateReplanStepsAsync(prompt, allSteps, plan,
                            scopedSteering, projectRoot, emitSse, ct,
                            attachedFiles: attachedFiles, qualityCheckReason: reason);
                        if (newSteps?.Count > 0)
                        {
                            var revertKeywords = new[] { "revert", "undo", "restore", "roll back", "rollback", "replace current content with" };
                            var safeSteps = new List<PlanStep>();
                            foreach (var s in newSteps)
                            {
                                var changeLower = (s.Change ?? "").ToLowerInvariant();
                                if (revertKeywords.Any(k => changeLower.Contains(k)))
                                {
                                    await EmitLog(emitSse, "warn", $"🚫 Replan generated a revert/undo step: '{s.Change}'. Blocking to prevent infinite loop.", ct: ct);
                                }
                                else
                                {
                                    safeSteps.Add(s);
                                }
                            }
                            if (safeSteps.Count == 0)
                            {
                                await EmitLog(emitSse, "warn", "Replan only generated revert/undo steps — ignoring.", ct: ct);
                                newSteps = null;
                            }
                            else
                            {
                                newSteps = safeSteps;
                            }
                        }
                        if (newSteps?.Count > 0)
                        {
                            var preMergeKeys = (plan?.Plan ?? new List<PlanStep>())
                                .Select(p => $"{p.File}|{NormalizeChangeForDedup(p.Change)}")
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            var filteredNewSteps = newSteps
                                .Where(s => !preMergeKeys.Contains($"{s.File}|{NormalizeChangeForDedup(s.Change)}"))
                                .ToList();
                            if (filteredNewSteps.Count == 0)
                            {
                                await EmitLog(emitSse, "warn", "Replan generated duplicate steps of completed work — ignoring.", ct: ct);
                                newSteps = null;
                            }
                            else
                            {
                                newSteps = filteredNewSteps;
                            }
                        }
                        if (newSteps?.Count > 0)
                        {
                            plan = MergePlans(plan ?? new AgentPlan(), new AgentPlan { Plan = newSteps });
                            if (emitSse)
                                await SendSse(Response, "plan",
                                    new { thinking = plan.Thinking, summary = "Replan: added steps", items = plan.Plan }, ct);
                            await PersistBoardDataPlanAsync(cardId, plan.Plan, emitSse, ct,
                                summary: plan.Summary ?? "Replan: added steps", score: plan.Score);
                            var mergedDone = new HashSet<int>();
                            for (var i = 0; i < plan.Plan.Count; i++)
                            {
                                var step = plan.Plan[i];
                                var result = allSteps.OfType<Dictionary<string, object?>>()
                                    .LastOrDefault(s =>
                                        s.TryGetValue("planItemIndex", out var pIdxObj) &&
                                        pIdxObj is int pIdx &&
                                        pIdx == i &&
                                        s.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" or "skipped" &&
                                        s.GetValueOrDefault("type")?.ToString() is "edit" or "create" or "rename");
                                if (result != null) mergedDone.Add(i);
                            }
                            var newResults = new List<object>();
                            await ExecutePlan(prompt, projectRoot, emitSse, "", plan, ct, newResults,
                                steeringContext: steeringContext, attachedFiles: attachedFiles,
                                completedStepIndices: mergedDone, cardId: cardId);
                            allSteps.AddRange(newResults);
                            var (ok3, _) = await AssessCompletion(prompt, allSteps, projectRoot, ct, plan, attachedFiles: attachedFiles);
                            complete = ok3;
                        }
                        else
                        {
                            await EmitLog(emitSse, "warn",
                                "No additional replan steps found, and the quality check still reports issues — " +
                                "leaving task INCOMPLETE for review rather than silently marking it done.", ct: ct);
                        }
                    }
                }
                else
                {
                    await EmitLog(emitSse, "success", "Quality check passed.", ct: ct);
                }
            }
        }
        bool isEdited = allSteps.OfType<Dictionary<string, object?>>().Any(s => s.GetValueOrDefault("type")?.ToString() == "edit");
        if (createTests && isEdited)
        {
            await RunTestCreationPipeline(projectRoot, allSteps, emitSse, ct);
        }
        bool buildOk = true;
        if (allSteps.Count > 0 && isEdited && buildCommands != null && !string.IsNullOrWhiteSpace(buildCommands))
        {
            var cmds = ParseBuildCommands(buildCommands);
            if (cmds.Count > 0)
            {
                if (emitSse)
                    await SendSse(Response, "phase",
                        new { phase = "build", message = $"Running {cmds.Count} build command(s)" }, ct);
                foreach (var cmd in cmds)
                {
                    var ok = await RunSmartBuildCheck(projectRoot, cmd, emitSse, ct);
                    if (!ok) { buildOk = false; }
                }
            }
        }
        if (!buildOk && isEdited)
        {
            var answer = await AskUserAsync(
                "Build errors detected. Would you like the AI to analyze and attempt to fix them?",
                new List<QuestionField>
                {
                    new() { Key = "confirm", Label = "Auto-repair build errors?", Type = "select", DefaultValue = "no" }
                }, ct);
            var wantsRepair = answer.Count > 0 &&
                answer.TryGetValue("confirm", out var val) &&
                val?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
            if (wantsRepair)
                await RepairPipeline(projectRoot, emitSse, ct, prompt, steeringContext, buildCommands);
        }
        return (allSteps, plan, complete);
    }
    private async Task<List<PlanStep>?> GenerateReplanStepsAsync(
        string originalPrompt, List<object> executedSteps, AgentPlan? existingPlan,
        string? steeringContext, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null, string qualityCheckReason = "")
    {
        var failHist = BuildFailedEditHistory(executedSteps);
        var failedCodeSnippets = new StringBuilder();
        foreach (var step in executedSteps.OfType<Dictionary<string, object?>>())
        {
            var status = step.GetValueOrDefault("status")?.ToString();
            if (status != "error" && status != "verify-abandoned") continue;
            var path = step.GetValueOrDefault("path")?.ToString() ?? "?";
            var error = step.GetValueOrDefault("error")?.ToString() ??
                        step.GetValueOrDefault("reason")?.ToString() ?? "";
            var failureCtx = step.GetValueOrDefault("failureContext")?.ToString();
            var attemptScores = step.GetValueOrDefault("attemptScores");
            var bestScore = step.GetValueOrDefault("bestScore");
            failedCodeSnippets.AppendLine($"### FAILED STEP: {path} ###");
            failedCodeSnippets.AppendLine($"Error: {error}");
            if (bestScore != null)
                failedCodeSnippets.AppendLine($"Best quality score achieved: {bestScore}/100");
            if (failureCtx != null)
                failedCodeSnippets.AppendLine($"Detailed failure context:\n{failureCtx}");
            failedCodeSnippets.AppendLine();
        }
        (StringBuilder fileContents, string warn) = await AgentProjectUtilities.GetReplanFileContents(executedSteps, projectRoot, attachedFiles, ct);
        if (!string.IsNullOrEmpty(warn) && emitSse)
        {
            await EmitLog(emitSse, "warn", warn, ct: ct);
        }
        var replanPrompt = BuildReplanPrompt(originalPrompt, new List<string> { failHist },
            steeringContext, existingPlan, executedSteps, qualityCheckReason,
            fileContents.ToString() + "\n\n## FAILED CODE SNIPPETS (do NOT reproduce)\n" + failedCodeSnippets.ToString());
        var (raw, _, llmError) = await CallLlmRaw(
                "You are a plan-fixer. Output ONLY valid JSON with a 'plan' array. Example: {\"plan\": [{\"file\": \"path/to/file.js\", \"change\": \"describe the change\", \"priority\": 1, \"line\": 42}]}. For every edit step include the 1-based line number. Max 1-2 steps. Empty array if all done. CRITICAL: Do NOT generate steps that revert or redo completed work. If the CURRENT FILE CONTENT matches the final requested state, return an EMPTY plan.",
                replanPrompt, ct, requestTimeout: _infiniteTimeout);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
            var s2 = cleaned.IndexOf('{'); var e2 = cleaned.LastIndexOf('}');
            if (s2 >= 0 && e2 > s2) cleaned = cleaned[s2..(e2 + 1)];
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (!root.TryGetProperty("plan", out var planEl) || planEl.ValueKind != JsonValueKind.Array)
                return null;
            var steps = new List<PlanStep>();
            foreach (var item in planEl.EnumerateArray())
            {
                var file = item.TryGetProperty("file", out var f) ? f.GetString() : null;
                var change = item.TryGetProperty("change", out var c) ? c.GetString() : null;
                var priority = item.TryGetProperty("priority", out var p) ? p.GetInt32() : 1;
                var line = item.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(change))
                    steps.Add(new PlanStep { File = file, Change = change, Priority = priority, LineNumber = line });
            }
            return steps.Count > 0 ? steps : null;
        }
        catch
        {
            await EmitLog(emitSse, "warn", "Failed to parse replan steps from LLM response", ct: ct);
            return null;
        }
    }
    private async Task<List<object>> QuickPipeline(
        string prompt, string projectRoot, bool emitSse, AgentPlan fastPlan, CancellationToken ct,
        string? cardId = null)
    {
        await EmitLog(emitSse, "info", $"Fast-path → {fastPlan.Summary}", ct: ct);
        if (emitSse)
            await SendSse(Response, "plan",
                new { thinking = fastPlan.Thinking, summary = fastPlan.Summary, items = fastPlan.Plan }, ct);
        var allResults = new List<object>();
        await ExecutePlan(prompt, projectRoot, emitSse, "", fastPlan, ct, allResults,
            cardId: cardId);
        return allResults;
    }
    private async Task<(AgentPlan plan, string discoveryContext)> RunPlanningConvergenceLoop(
        string prompt, string discoveryContext, string projectRoot, bool emitSse,
        CancellationToken ct, string? steeringContext)
    {
        AgentPlan? best = null;
        var steering = steeringContext;
        var exploredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var iter = 1; iter <= MAX_PLANNING_ITERATIONS; iter++)
        {
            var plan = await AnalyzePromptAndPlanCodeChanges(
                prompt, discoveryContext, projectRoot, emitSse, ct, steering);
            if (plan == null || plan.Plan.Count == 0)
            {
                if (best != null) break;
                throw new InvalidOperationException("LLM returned an empty or unparseable plan.");
            }
            plan.Plan = DeduplicateSteps(plan.Plan);
            plan.Plan = AgentPlanParsing.DeduplicateSimilarSteps(plan.Plan);
            var exploreSteps = plan.Plan
                .Where(p => p.File.Equals("_explore", StringComparison.OrdinalIgnoreCase)).ToList();
            var discoverSteps = plan.Plan
                .Where(p => p.File.Equals("_discover", StringComparison.OrdinalIgnoreCase)).ToList();
            if (discoverSteps.Count > 0)
            {
                await EmitLog(emitSse, "info",
                    $"Planning {iter}/{MAX_PLANNING_ITERATIONS}: planner requested _discover — running project-wide search…", ct: ct);
                discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                continue;
            }
            var readOnlyPrefixes = new[] { "read", "look at", "examine", "inspect", "review",
                "understand", "study", "browse", "view", "check how", "see how",
                "get familiar", "explore" };
            foreach (var p in plan.Plan)
            {
                if (AgentProjectUtilities.IsRelativePath(p.File) &&
                    readOnlyPrefixes.Any(prefix =>
                        (p.Change ?? "").Trim().ToLowerInvariant().StartsWith(prefix)))
                {
                    exploreSteps.Add(new PlanStep { File = "_explore", Change = p.File });
                }
            }
            var newExploreSteps = exploreSteps
                .Where(s => !string.IsNullOrWhiteSpace(s.Change) && exploredFiles.Add(s.Change))
                .ToList();
            if (newExploreSteps.Count > 0)
            {
                await EmitLog(emitSse, "info",
                    $"Planning {iter}/{MAX_PLANNING_ITERATIONS}: planner requested {newExploreSteps.Count} new exploration target(s) — gathering context…", ct: ct);
                discoveryContext = await ExplorationPipeline(newExploreSteps, discoveryContext, projectRoot, emitSse, ct);
                if (iter == MAX_PLANNING_ITERATIONS)
                    steering = AppendExploreSteering(steeringContext);
                continue;
            }
            if (best == null || plan.Score > best.Score) best = plan;
            await EmitLog(emitSse, "info",
                $"Planning {iter}/{MAX_PLANNING_ITERATIONS} — score {plan.Score}/100 ({plan.Plan.Count} step(s))",
                new { plan.Score }, ct: ct);
            if (plan.Score >= PLAN_SCORE_THRESHOLD)
            {
                await EmitLog(emitSse, "success",
                    $"Plan converged: score {plan.Score} ≥ {PLAN_SCORE_THRESHOLD}.", ct: ct);
                best = plan;
                break;
            }
            if (iter < MAX_PLANNING_ITERATIONS)
            {
                await EmitLog(emitSse, "info",
                    $"Plan score {plan.Score} below {PLAN_SCORE_THRESHOLD} — refining…", ct: ct);
                steering = BuildLowScoreSteering(plan, steeringContext);
            }
            else
            {
                await EmitLog(emitSse, "warn",
                    $"Planning budget exhausted at score {best!.Score} — proceeding with best plan.", ct: ct);
            }
        }
        if (best == null)
        {
            var forced = await AnalyzePromptAndPlanCodeChanges(
                prompt, discoveryContext, projectRoot, emitSse, ct, AppendExploreSteering(steeringContext));
            best = forced?.Plan.Count > 0
                ? forced
                : throw new InvalidOperationException("Planner did not produce an actionable plan after exploration.");
            best.Plan = best.Plan
                .Where(p => !p.File.Equals("_explore", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return (best, discoveryContext);
    }
    private static List<PlanStep> DeduplicateSteps(List<PlanStep> steps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<PlanStep>();
        foreach (var step in steps)
        {
            var normChange = (step.Change ?? "").Trim().ToLowerInvariant();
            normChange = string.Join(" ", normChange.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
            var key = $"{step.File}|{normChange}";
            if (seen.Add(key))
                deduped.Add(step);
        }
        return deduped;
    }
}
