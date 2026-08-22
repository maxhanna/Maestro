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
    private async Task<PlanAuditResult?> PlanPreAuditAsync(
     AgentPlan plan, string projectRoot, bool emitSse,
     CancellationToken ct, string? originalPrompt = null)
    {
        if (plan?.Plan == null || plan.Plan.Count == 0) return null;
        var auditSteps = new List<AuditPlanStepResult>();
        var sb = new StringBuilder();
        sb.AppendLine("You are auditing a code-change plan BEFORE execution. Your job: detect problems that would waste time or cause bugs.");
        sb.AppendLine();
        sb.AppendLine("For EACH step in the plan, examine the target file's content, the original prompt, and determine:");
        sb.AppendLine("1. alreadyDone = true/false — Is the proposed change ALREADY present in the file?");
        sb.AppendLine("2. needsDecoupling = true/false — Does this step combine TWO OR MORE distinct");
        sb.AppendLine("   changes at DIFFERENT LOCATIONS that should be separate steps?");
        sb.AppendLine();
        sb.AppendLine("   PATTERNS THAT NEED DECOUPLING:");
        sb.AppendLine("   a) Cross-file: \"Add property to X AND wire up in Y\" → 2 steps");
        sb.AppendLine("   b) Move: \"Move X from A to B\" → 2 steps (add at B, remove from A)");
        sb.AppendLine("   c) SAME-FILE MULTI-LOCATION: \"Add field AND initialize in constructor AND add method\"");
        sb.AppendLine("      → 3 steps (field decl, constructor init, method def)");
        sb.AppendLine("   d) MIRROR/COPY: If the step says 'mirror X' or 'copy pattern from X', it usually requires");
        sb.AppendLine("      modifying EXISTING elements (like a title bar) to emit events, AND adding the new UI structure.");
        sb.AppendLine("      This MUST be split into separate steps for each location (e.g., 1. Modify title bar, 2. Add panel).");
        sb.AppendLine("   e) WRAPPING: \"Wrap X in a container\" → 2 steps (open tag + close tag)");
        sb.AppendLine("   f) INSERT + WIRE: If a step says to insert/mirror/copy a UI component AND also");
        sb.AppendLine("      mentions wiring it up (event binding, click handler, attribute on a DIFFERENT");
        sb.AppendLine("      existing element), that is TWO steps: one to add the new structure, one to");
        sb.AppendLine("      modify the existing element to wire it up. Example:");
        sb.AppendLine("      \"Insert popup panel AND add menuClicked event binding to title bar\"");
        sb.AppendLine("      → 2 steps: 1. Add popup panel HTML, 2. Add menuClicked binding to title bar");
        sb.AppendLine("      Look for phrases like 'including event binding', 'with wiring', 'wire up',");
        sb.AppendLine("      'add X and connect Y', 'matching event syntax', 'including click handler'.");
        sb.AppendLine();
        sb.AppendLine("   Example: \"Add _fifteenMinuteTimer field and initialize it in the constructor,");
        sb.AppendLine("   then add a RunFifteenMinuteTasks method\"");
        sb.AppendLine("   needsDecoupling = true. decoupledSteps = [");
        sb.AppendLine("     { file: \"...\", change: \"Add _fifteenMinuteTimer field declaration after the last existing timer field\" },");
        sb.AppendLine("     { file: \"...\", change: \"Initialize _fifteenMinuteTimer in the constructor after existing timer initializations\" },");
        sb.AppendLine("     { file: \"...\", change: \"Add RunFifteenMinuteTasks method after the last existing RunXxxTasks method\" }");
        sb.AppendLine("   ]");
        sb.AppendLine();
        sb.AppendLine("   Example: \"Add isMenuPanelOpen property and showMenuPanel() and closeMenuPanel() methods\"");
        sb.AppendLine("   needsDecoupling = true. decoupledSteps = [");
        sb.AppendLine("     { file: \"...\", change: \"Add isMenuPanelOpen property declaration after the last existing property\" },");
        sb.AppendLine("     { file: \"...\", change: \"Add showMenuPanel() method after the last existing method\" },");
        sb.AppendLine("     { file: \"...\", change: \"Add closeMenuPanel() method after showMenuPanel()\" }");
        sb.AppendLine("   ]");
        sb.AppendLine();
        sb.AppendLine("   CRITICAL: Even within the SAME FILE, if a step requires changes at DIFFERENT");
        sb.AppendLine("   LOCATIONS (e.g., modify a title bar AND add a popup panel at the bottom), that is MULTIPLE distinct edits.");
        sb.AppendLine("   Combining them forces the editor into massive block replacements that fail. Each location needs its own step.");
        sb.AppendLine("   When a step says it will 'insert X and wire it up' or 'add Y including event binding on Z',");
        sb.AppendLine("   the wiring/event-binding is a SEPARATE location from the new structure — split it off.");
        sb.AppendLine();
        sb.AppendLine("   g) DECLARE + DISPLAY + POPULATE: If steps add a property/field that shows fetched data");
        sb.AppendLine("      (e.g., 'youtubeTotalResults') and display it in a template, but NO step assigns/populates");
        sb.AppendLine("      that property after the data fetch, GENERATE a missing step to wire it up.");
        sb.AppendLine("      Example: Step 1 adds 'youtubeTotalResults: number = 0;'. Step 2 adds '{{youtubeTotalResults}}'");
        sb.AppendLine("      to the template. The file has 'youtubeResults: YoutubeVideo[]' populated by a search method.");
        sb.AppendLine("      A step 3 is MISSING: 'Set youtubeTotalResults = youtubeResults.length in the search");
        sb.AppendLine("      method after the YouTube results are returned.' — add it to decoupledSteps.");
        sb.AppendLine("      Look for properties declared as counters/display variables with no assignment");
        sb.AppendLine("      in any method body, whose value can be derived from an existing data source.");
        sb.AppendLine();
        sb.AppendLine("   h) REJECT COMMENT/EXPLANATION STEPS: If a step says to 'add a comment block' or");
        sb.AppendLine("      'add an explanation' or 'add documentation' or 'create a script comment'");
        sb.AppendLine("      that is NOT functional code (no logic change, no new method, no new endpoint),");
        sb.AppendLine("      set alreadyDone = true and reason = 'Comment-only step — not a functional change.'");
        sb.AppendLine("      These steps waste execution time and confuse the editor. The verification");
        sb.AppendLine("      system already handles SQL table schema detection; table creation SQL belongs");
        sb.AppendLine("      INSIDE the method that uses the table, not as a standalone comment.");
        sb.AppendLine("      Example: 'Create BenchmarksTableCreation SQL comment block before GetVersion'");
        sb.AppendLine("      → alreadyDone = true.");
        sb.AppendLine();
        sb.AppendLine("   DO NOT decouple if the step is a single coherent edit in one location (e.g., 'Modify the CalculateTotal method to include tax').");
        sb.AppendLine("   DO NOT decouple filling in a class/record/struct with multiple properties or fields.");
        sb.AppendLine("   'Fill in UserDTO with Name, Email, Age' is ONE step — add all properties at once in the same location.");
        sb.AppendLine("   'Add properties Token, Date, Score to BenchmarkDataDTO' is ONE step, not 12 individual property steps.");
        sb.AppendLine("   Multiple properties grouped in the same step are intentionally one coherent change.");
        sb.AppendLine("   DO NOT decouple method route attributes from the method itself. In C#/Java/Python, route attributes");
        sb.AppendLine("   ([HttpGet], @app.get, @RequestMapping, etc.) ARE part of the method declaration at the same");
        sb.AppendLine("   location — they are NOT a separate 'endpoint registration'. 'Add GetBenchmarks method with [HttpGet]'");
        sb.AppendLine("   is ONE step, not two.");
        sb.AppendLine("   i) NEW SQL TABLES/COLUMNS GO IN A _sql_migration STEP: If a step mentions adding a method/endpoint that");
        sb.AppendLine("      inserts/updates data AND the table does not exist yet, add a separate _sql_migration step");
        sb.AppendLine("      FIRST (file=\"_sql_migration\", newString=CREATE TABLE IF NOT EXISTS ...). A new COLUMN on an");
        sb.AppendLine("      existing table is the SAME step with newString=ALTER TABLE ... ADD COLUMN .... The DDL is written");
        sb.AppendLine("      to migrations/schema_changes.md for the user to apply manually — do NOT inline CREATE TABLE /");
        sb.AppendLine("      ALTER TABLE inside the method body. The endpoint method only contains INSERT/UPDATE/SELECT.");
        sb.AppendLine("      GOOD: Step 1: \"_sql_migration: benchmark_scores table\", Step 2: \"Add PostBenchmarks method with INSERT\"");
        sb.AppendLine();
        sb.AppendLine("SPECIAL RULE FOR REMOVAL/DELETE STEPS:");
        sb.AppendLine("  For steps that say 'Remove X' or 'Delete X':");
        sb.AppendLine("  - alreadyDone = true ONLY if X is NOT present in the file content shown below.");
        sb.AppendLine("  - If X IS present in the file (even once), alreadyDone MUST be false.");
        sb.AppendLine("  - Do NOT reason about 'whether it would be present after other steps run'.");
        sb.AppendLine("  - Check the ACTUAL file content shown above — search for the exact code pattern.");
        sb.AppendLine("  - If the file content is truncated or shows excerpts, do NOT assume the pattern is absent — set alreadyDone = false if you can't see the whole file.");
        sb.AppendLine();
        sb.AppendLine("Output ONLY valid JSON:");
        sb.AppendLine("{");
        sb.AppendLine("  \"steps\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"index\": 0,");
        sb.AppendLine("      \"alreadyDone\": false,");
        sb.AppendLine("      \"needsDecoupling\": false,");
        sb.AppendLine("      \"reason\": \"\",");
        sb.AppendLine("      \"decoupledSteps\": []");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(originalPrompt))
        {
            sb.AppendLine("### ORIGINAL TASK ###");
            sb.AppendLine(originalPrompt);
            sb.AppendLine();
        }
        for (var i = 0; i < plan.Plan.Count; i++)
        {
            var step = plan.Plan[i];
            sb.AppendLine($"--- STEP {i + 1} ---");
            sb.AppendLine($"File:   {step.File}");
            sb.AppendLine($"Change: {step.Change}");
            sb.AppendLine();
            if (AgentProjectUtilities.IsRelativePath(step.File) && !AgentProjectUtilities.IsSpecialMarker(step.File))
            {
                var relPath = step.File.Replace('\\', '/');
                var fullPath = Path.GetFullPath(
                    Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fullPath) && AgentProjectUtilities.IsPathUnderRoot(fullPath, projectRoot))
                {
                    var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                    var changeLower = (step.Change ?? "").ToLowerInvariant();
                    sb.AppendLine("TARGET FILE CONTENT:");
                    sb.AppendLine("```");
                    if (content.Length > 8000)
                    {
                        if (changeLower.Contains("remove") ||
                            (changeLower.Contains("delete") && !Regex.IsMatch(changeLower, @"\b(add|create|insert|implement)\b")))
                        {
                            var keywords = Regex.Matches(step.Change ?? "", @"[\w-]+")
                                .Select(m => m.Value)
                                .Where(w => w.Length > 4 && !new HashSet<string> { "remove", "delete" }.Contains(w.ToLowerInvariant()))
                                .Take(3).ToList();
                            var sb2 = new StringBuilder();
                            var lines = content.Split('\n');
                            for (var li = 0; li < lines.Length; li++)
                            {
                                if (keywords.Any(k => lines[li].Contains(k, StringComparison.OrdinalIgnoreCase)))
                                {
                                    var start = Math.Max(0, li - 2);
                                    var end = Math.Min(lines.Length - 1, li + 2);
                                    sb2.AppendLine($"--- around line {li + 1} ---");
                                    for (var j = start; j <= end; j++)
                                        sb2.AppendLine($"{j + 1}: {lines[j]}");
                                    sb2.AppendLine();
                                }
                            }
                            sb.AppendLine(sb2.ToString());
                            sb.AppendLine($"... (showing relevant excerpts, full file is {content.Length} chars)");
                        }
                        else
                        {
                            sb.AppendLine(content);
                        }
                    }
                    else
                    {
                        sb.AppendLine(content);
                    }
                    sb.AppendLine("```");
                    var addClassMatch = Regex.Match(step.Change ?? @"", @"(?:add|insert|create)\s+(?:a\s+)?(?:new\s+)?class\s+(\w+)", RegexOptions.IgnoreCase);
                    if (addClassMatch.Success)
                    {
                        var className = addClassMatch.Groups[1].Value;
                        if (Regex.IsMatch(content, $@"\bclass\s+{Regex.Escape(className)}\b"))
                        {
                            sb.AppendLine($"⚠ PRE-CHECK: Class '{className}' already exists in the file. Step {i + 1} may be alreadyDone.");
                        }
                    }
                    var addMethodMatch2 = Regex.Match(step.Change ?? @"", @"(?:add|insert|create)\s+(?:a\s+)?(?:new\s+)?method\s+(\w+)", RegexOptions.IgnoreCase);
                    if (addMethodMatch2.Success)
                    {
                        var methodName = addMethodMatch2.Groups[1].Value;
                        if (Regex.IsMatch(content, $@"\b{Regex.Escape(methodName)}\s*\("))
                        {
                            sb.AppendLine($"⚠ PRE-CHECK: Method '{methodName}' already exists in the file. Step {i + 1} may be alreadyDone.");
                        }
                    }
                }
                else
                {
                    sb.AppendLine("(file does not exist yet — will be created)");
                }
            }
            else
            {
                sb.AppendLine("(special marker step — no file to check)");
            }
            sb.AppendLine();
        }
        var (raw, _, error) = await CallLlmRaw(
            "You are a plan auditor. Output ONLY the JSON object described below. No markdown, no extra text.",
            sb.ToString(), ct, requestTimeout: _infiniteTimeout, maxTokens: 2048);
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Plan audit LLM call failed: {error ?? "empty response"}", ct: ct);
            return null;
        }
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (m.Success) cleaned = m.Groups[1].Value.Trim();
        }
        var fb = cleaned.IndexOf('{');
        var lb = cleaned.LastIndexOf('}');
        if (fb >= 0 && lb > fb) cleaned = cleaned[fb..(lb + 1)];
        try
        {
            using var jDoc = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = jDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("steps", out var stepsArr) ||
                stepsArr.ValueKind != JsonValueKind.Array)
            {
                await EmitLog(emitSse, "warn", "Plan audit response missing 'steps' array", ct: ct);
                return null;
            }
            var preCheckedIndices = new HashSet<int>();
            for (var i = 0; i < plan.Plan.Count; i++)
            {
                var step = plan.Plan[i];
                var changeLower = (step.Change ?? "").ToLowerInvariant();
                if (!changeLower.StartsWith("remove ") && !changeLower.StartsWith("delete "))
                    continue;
                var relPath = step.File.Replace('\\', '/');
                var fullPath = Path.GetFullPath(
                    Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!System.IO.File.Exists(fullPath)) continue;
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                // Shared with PreEditValidation: a removal is already-done ONLY when the FULL
                // removal target is absent (exact → trimmed → collapsed) AND keyword evidence is
                // gone. A survivor fragment (oldString = survivor + target → newString = survivor)
                // is NOT evidence of a completed removal — the auditor agrees with the executor.
                // This deterministically overrides the LLM verdict for EVERY removal carrier
                // (oldString, FORMAT D targetName, or description-quoted code), not just HTML
                // blocks embedded in the change description as before.
                if (IsRemovalAlreadyApplied(content, step))
                {
                    await EmitLog(emitSse, "info",
                        $"Audit: step {i + 1} — removal target already absent from file, already done (deterministic override)", ct: ct);
                    auditSteps.Add(new AuditPlanStepResult
                    {
                        Index = i,
                        AlreadyDone = true,
                        NeedsDecoupling = false,
                        Reason = "Removal target is already absent from file — step already done",
                        DecoupledSteps = null
                    });
                    preCheckedIndices.Add(i);
                }
                else
                {
                    await EmitLog(emitSse, "info",
                        $"Audit: step {i + 1} — removal target IS present in file, NOT already done (deterministic override)", ct: ct);
                    auditSteps.Add(new AuditPlanStepResult
                    {
                        Index = i,
                        AlreadyDone = false,
                        NeedsDecoupling = false,
                        Reason = "Removal target is present in file — step is needed",
                        DecoupledSteps = null
                    });
                    preCheckedIndices.Add(i);
                }
            }
            foreach (var stepEl in stepsArr.EnumerateArray())
            {
                if (stepEl.ValueKind != JsonValueKind.Object) continue;
                var idx = stepEl.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                    ? idxEl.GetInt32() : -1;
                if (idx < 0 || idx >= plan.Plan.Count) continue;
                if (preCheckedIndices.Contains(idx))
                {
                    await EmitLog(emitSse, "info",
                        $"Audit: step {idx + 1} — using deterministic check (LLM verdict ignored)", ct: ct);
                    continue;
                }
                var alreadyDone = stepEl.TryGetProperty("alreadyDone", out var adEl) &&
                                  adEl.ValueKind == JsonValueKind.True && adEl.GetBoolean();
                var needsDecoupling = stepEl.TryGetProperty("needsDecoupling", out var ndEl) &&
                                      ndEl.ValueKind == JsonValueKind.True && ndEl.GetBoolean();
                string? reason = stepEl.TryGetProperty("reason", out var rEl) && rEl.ValueKind == JsonValueKind.String
                    ? rEl.GetString() : null;
                if (alreadyDone)
                {
                    var step = plan.Plan[idx];
                    var changeLower = (step.Change ?? "").ToLowerInvariant();
                    if (changeLower.StartsWith("remove ") || changeLower.StartsWith("delete "))
                    {
                        var relPath = step.File.Replace('\\', '/');
                        var fullPath = Path.GetFullPath(
                            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
                        if (System.IO.File.Exists(fullPath))
                        {
                            var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                            // Same shared rule as the pre-check and PreEditValidation: if the FULL
                            // removal target is still present (survivor fragment included), the LLM's
                            // 'already done' verdict is wrong and must be overridden.
                            if (!IsRemovalAlreadyApplied(content, step))
                            {
                                await EmitLog(emitSse, "warn",
                                    $"Audit sanity check: step {idx + 1} was marked 'already done' but the removal target is still present — overriding", ct: ct);
                                alreadyDone = false;
                                reason = "Override: code to be removed is still present in file";
                            }
                        }
                    }
                }
                List<PlanStep>? decoupled = null;
                if (needsDecoupling && stepEl.TryGetProperty("decoupledSteps", out var dcArr) && dcArr.ValueKind == JsonValueKind.Array)
                {
                    decoupled = new List<PlanStep>();
                    foreach (var dc in dcArr.EnumerateArray())
                    {
                        if (dc.ValueKind != JsonValueKind.Object) continue;
                        var dcFile = dc.TryGetProperty("file", out var fEl) && fEl.ValueKind == JsonValueKind.String
                            ? fEl.GetString() ?? plan.Plan[idx].File : plan.Plan[idx].File;
                        var dcChange = dc.TryGetProperty("change", out var cEl) && cEl.ValueKind == JsonValueKind.String
                            ? cEl.GetString() ?? plan.Plan[idx].Change : plan.Plan[idx].Change;
                        if (!string.IsNullOrWhiteSpace(dcChange) && dcChange != plan.Plan[idx].Change)
                        {
                            var dcChangeLower = dcChange.ToLowerInvariant();
                            var researchVerbs = new[] { "locate", "find", "examine", "understand", "read", "explore", "look at", "inspect", "review", "check", "see", "search" };
                            if (researchVerbs.Any(v => dcChangeLower.StartsWith(v)))
                            {
                                await EmitLog(emitSse, "warn",
                                    $"Audit rejected research sub-step: \"{dcChange}\" — every step must make actual code changes.", ct: ct);
                            }
                            else
                            {
                                decoupled.Add(new PlanStep
                                {
                                    File = dcFile,
                                    Change = dcChange,
                                    Priority = plan.Plan[idx].Priority,
                                    ReferenceFiles = plan.Plan[idx].ReferenceFiles,
                                    LineNumber = plan.Plan[idx].LineNumber
                                });
                            }
                        }
                    }
                    if (decoupled.Count > 1)
                    {
                        var deduped = new List<PlanStep>();
                        foreach (var step in decoupled)
                        {
                            var isDuplicate = false;
                            var stepWords = step.Change?.ToLowerInvariant().Split(' ',
                                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                            foreach (var existing in deduped)
                            {
                                if (!string.Equals(step.File, existing.File, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                var existingWords = existing.Change?.ToLowerInvariant().Split(' ',
                                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                                var commonWords = stepWords.Intersect(existingWords, StringComparer.OrdinalIgnoreCase).Count();
                                var maxLen = Math.Max(stepWords.Length, existingWords.Length);
                                if (maxLen > 0 && (double)commonWords / maxLen >= 0.70)
                                {
                                    isDuplicate = true;
                                    await EmitLog(emitSse, "warn",
                                        $"Audit dedup: removed duplicate decoupled step [{step.Change}] " +
                                        $"(~{commonWords * 100 / maxLen}% overlap with [{existing.Change}])", ct: ct);
                                    break;
                                }
                            }
                            if (!isDuplicate)
                                deduped.Add(step);
                        }
                        decoupled = deduped;
                    }
                    if (decoupled.Count == 0)
                    {
                        needsDecoupling = false;
                    }
                }
                auditSteps.Add(new AuditPlanStepResult
                {
                    Index = idx,
                    AlreadyDone = alreadyDone,
                    NeedsDecoupling = needsDecoupling,
                    Reason = reason,
                    DecoupledSteps = decoupled
                });
                if (alreadyDone)
                    await EmitLog(emitSse, "info",
                        $"Audit: step {idx + 1} already done — {reason}", ct: ct);
                if (needsDecoupling)
                    await EmitLog(emitSse, "info",
                        $"Audit: step {idx + 1} needs decoupling ({decoupled?.Count ?? 0} sub-steps) — {reason}", ct: ct);
            }
            return new PlanAuditResult { Steps = auditSteps };
        }
        catch (JsonException ex)
        {
            await EmitLog(emitSse, "warn", $"Plan audit JSON parse failed: {ex.Message}", ct: ct);
            return null;
        }
    }
    private static List<string> ScanMissingTypes(string fullFileContent, string newCode)
    {
        var declaredTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(fullFileContent,
            @"\b(class|record|struct|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline))
            declaredTypes.Add(m.Groups[2].Value);
        var usingNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(fullFileContent, @"using\s+([A-Za-z_.][A-Za-z0-9_.]*)\s*;"))
            usingNamespaces.Add(m.Groups[1].Value);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(newCode, @"\b[A-Z][a-zA-Z0-9_]+\b"))
        {
            var name = m.Value;
            if (name.Length < 3) continue;
            if (_builtInTypes.Contains(name)) continue;
            if (declaredTypes.Contains(name)) continue;
            if (usingNamespaces.Any(ns => name.StartsWith(ns.Split('.').Last(), StringComparison.OrdinalIgnoreCase)))
                continue;
            // Declared as a member rather than used as a type — "public string Name { get; set; }"
            // would otherwise be misread as a missing type "Name" and spawn a bogus `public class Name {}`.
            // (Guarded against `new Foo { ... }` object initializers via the (?!new\s) lookbehind.)
            if (Regex.IsMatch(newCode,
                    @"(?<!new\s)\b[A-Za-z_]\w*(?:\s*<[^>]*>)?\s+" + Regex.Escape(name) + @"\s*(?:\{|;|=)",
                    RegexOptions.IgnoreCase))
                continue;
            // ...or used as a member's declared type — "public DateTime CreatedAt { get; set; }"
            // is a property whose type is DateTime, not a missing class named DateTime.
            if (Regex.IsMatch(newCode,
                    @"(?<!new\s)\b" + Regex.Escape(name) + @"(?:\s*<[^>]*>)?\s+[A-Za-z_]\w*\s*(?:\{|;|=)",
                    RegexOptions.IgnoreCase))
                continue;
            candidates.Add(name);
        }
        var result = new List<string>();
        foreach (var c in candidates)
        {
            var isMethodCall = Regex.IsMatch(newCode, @"\b" + Regex.Escape(c) + @"\s*\(");
            var isConstructor = Regex.IsMatch(newCode, @"new\s+" + Regex.Escape(c) + @"\s*\(");
            if (isMethodCall && !isConstructor)
                continue;
            if (Regex.IsMatch(newCode, @"\." + Regex.Escape(c) + @"\b"))
                continue;
            var fbPattern = @"\[FromBody\]\s+\b" + Regex.Escape(c) + @"\b";
            if (Regex.IsMatch(newCode, fbPattern))
            { result.Add(c); continue; }
            if (c.EndsWith("Request", StringComparison.OrdinalIgnoreCase) ||
                c.EndsWith("Response", StringComparison.OrdinalIgnoreCase) ||
                c.EndsWith("Result", StringComparison.OrdinalIgnoreCase))
            { result.Add(c); continue; }
            var genericPattern = @"<" + Regex.Escape(c) + @"\s*>";
            if (Regex.IsMatch(newCode, genericPattern))
            { result.Add(c); continue; }
        }
        return result.Distinct().ToList();
    }
    private async Task<StepExplorationResult> RunStepExplorationLoop(
        PlanStep step,
        string projectRoot,
        string originalPrompt,
        AgentPlan? fullPlan,
        int planItemIndex,
        bool emitSse,
        CancellationToken ct,
        string? cardId = null,
        List<string>? attachedFiles = null)
    {
        if (!string.IsNullOrWhiteSpace(step.OldString) &&
            !string.IsNullOrWhiteSpace(step.NewString))
        {
            return new StepExplorationResult
            {
                EnrichedStep = step,
                FilesRead = new List<string>(),
                RefinedChange = step.Change,
                Confidence = 100
            };
        }
        const int MaxRounds = 4;
        const int ConfidenceThreshold = 80;
        var cfg4 = await LoadConfigAsync();
        var MaxContextChars = cfg4.maxContextChars;
        var relPath = step.File.Replace('\\', '/');
        var fullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        var ctx = new StringBuilder();
        var filesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string AbsNormalize(string p)
        {
            try
            {
                var full = Path.IsPathRooted(p)
                    ? Path.GetFullPath(p)
                    : Path.GetFullPath(Path.Combine(projectRoot, p));
                return full.Replace('\\', '/').TrimEnd('/');
            }
            catch { return p.Replace('\\', '/').TrimEnd('/'); }
        }
        string RelNormalize(string p)
        {
            try
            {
                var full = Path.IsPathRooted(p)
                    ? Path.GetFullPath(p)
                    : Path.GetFullPath(Path.Combine(projectRoot, p));
                if (full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                    return Path.GetRelativePath(projectRoot, full).Replace('\\', '/');
                return full.Replace('\\', '/').TrimEnd('/');
            }
            catch { return p.Replace('\\', '/').TrimEnd('/'); }
        }
        bool AddFileRead(string path)
        {
            var rel = RelNormalize(path);
            var abs = AbsNormalize(path);
            var addedToFiles = filesRead.Add(rel);
            normalizedPaths.Add(abs);
            return addedToFiles;
        }
        List<string> FindLikelyProjectFiles(string requested, int max = 5)
        {
            var normalized = (requested ?? "").Replace('\\', '/').Trim().Trim('"', '\'', '`');
            if (string.IsNullOrWhiteSpace(normalized)) return new List<string>();
            var requestedName = Path.GetFileName(normalized);
            var requestedStem = Path.GetFileNameWithoutExtension(requestedName);
            var rawTokens = Regex.Matches(normalized + " " + requestedStem, @"[A-Za-z_][A-Za-z0-9_]{2,}")
                .Select(m => m.Value)
                .Where(t => !new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "read", "file", "path", "source", "component", "service", "class", "method",
                    "function", "interface", "model", "controller", "style", "template"
                }.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            var skipSegments = new[] { "/bin/", "/obj/", "/node_modules/", "/dist/", "/packages/", "/.git/", "/.vs/" };
            var textExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".ts", ".tsx", ".js", ".jsx", ".html", ".css", ".scss", ".less",
                ".json", ".xml", ".yaml", ".yml", ".md", ".razor", ".cshtml", ".sql"
            };
            var scored = new List<(string rel, int score)>();
            foreach (var file in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                var relLow = "/" + rel.ToLowerInvariant();
                if (skipSegments.Any(relLow.Contains)) continue;
                var ext = Path.GetExtension(rel);
                if (!textExts.Contains(ext)) continue;
                var name = Path.GetFileName(rel);
                var stem = Path.GetFileNameWithoutExtension(rel);
                var score = 0;
                if (string.Equals(rel, normalized, StringComparison.OrdinalIgnoreCase)) score += 1000;
                if (rel.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase)) score += 800;
                if (!string.IsNullOrWhiteSpace(requestedName) &&
                    string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase)) score += 650;
                if (!string.IsNullOrWhiteSpace(requestedStem) &&
                    string.Equals(stem, requestedStem, StringComparison.OrdinalIgnoreCase)) score += 500;
                foreach (var token in rawTokens)
                {
                    if (stem.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 120;
                    if (rel.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 45;
                }
                if (score > 0)
                    scored.Add((rel, score));
            }
            if (scored.Count < max && rawTokens.Count > 0)
            {
                var already = scored.Select(s => s.rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                    if (already.Contains(rel)) continue;
                    var relLow = "/" + rel.ToLowerInvariant();
                    if (skipSegments.Any(relLow.Contains)) continue;
                    if (!textExts.Contains(Path.GetExtension(rel))) continue;
                    try
                    {
                        var content = System.IO.File.ReadAllText(file);
                        var score = rawTokens.Sum(token =>
                            Regex.IsMatch(content, $@"\b{Regex.Escape(token)}\b") ? 35 : 0);
                        if (score > 0) scored.Add((rel, score));
                    }
                    catch { }
                }
            }
            return scored
                .OrderByDescending(s => s.score)
                .ThenBy(s => s.rel.Length)
                .Select(s => s.rel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }
        if (attachedFiles != null && attachedFiles.Count > 0)
        {
            await EmitLog(emitSse, "info", $"  ⊕ Seeding {attachedFiles.Count} attached file(s) into exploration context", ct: ct);
            foreach (var af in attachedFiles)
            {
                try
                {
                    var afPath = Path.GetFullPath(Path.Combine(projectRoot, af.TrimStart('/', '\\')));
                    if (System.IO.File.Exists(afPath) && AddFileRead(afPath))
                    {
                        var afContent = await System.IO.File.ReadAllTextAsync(afPath, ct);
                        var afRel = Path.GetRelativePath(projectRoot, afPath).Replace('\\', '/');
                        ctx.AppendLine($"--- {afRel} (attached) ---");
                        ctx.AppendLine(afContent);
                        ctx.AppendLine($"--- end {afRel} ---");
                        ctx.AppendLine();
                    }
                }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "warn", $"  ⚠ Could not read attached file '{af}': {ex.Message}", ct: ct);
                }
            }
        }
        var serviceCallMatch = Regex.Match(step.Change ?? "", @"(?:this\.)?([A-Za-z]\w*Service)\b", RegexOptions.IgnoreCase);
        if (!serviceCallMatch.Success && fullPlan?.Summary != null)
        {
            serviceCallMatch = Regex.Match(fullPlan.Summary, @"([A-Za-z]\w*Service)\b", RegexOptions.IgnoreCase);
        }
        if (!serviceCallMatch.Success)
        {
            serviceCallMatch = Regex.Match(ctx.ToString(), @"this\.(\w+Service)\b", RegexOptions.IgnoreCase);
        }
        if (serviceCallMatch.Success)
        {
            var serviceName = serviceCallMatch.Groups[1].Value;
            var serviceFiles = Directory.EnumerateFiles(projectRoot, "*.ts", SearchOption.AllDirectories)
                .Where(f => !f.Contains("node_modules", StringComparison.OrdinalIgnoreCase) &&
                            !f.Contains("dist", StringComparison.OrdinalIgnoreCase))
                .Where(f =>
                {
                    try
                    {
                        var content = System.IO.File.ReadAllText(f, Encoding.UTF8);
                        return Regex.IsMatch(content,
                            $@"\b(?:export\s+)?(?:abstract\s+)?class\s+{Regex.Escape(serviceName)}\b",
                            RegexOptions.IgnoreCase);
                    }
                    catch { return false; }
                })
                .Take(1)
                .ToList();
            foreach (var sf in serviceFiles)
            {
                var rel = Path.GetRelativePath(projectRoot, sf).Replace('\\', '/');
                if (AddFileRead(rel))
                {
                    var content = await System.IO.File.ReadAllTextAsync(sf, Encoding.UTF8, ct);
                    ctx.AppendLine($"### {rel} (deterministic service injection)");
                    ctx.AppendLine("```typescript ");
                    ctx.AppendLine(content);
                    ctx.AppendLine("```");
                    ctx.AppendLine();
                    await EmitLog(emitSse, "info", $"  🎯 Auto-injected service: {rel}", ct: ct);
                }
            }
        }
        var refinedChange = step.Change;
        string? targetSymbol = !string.IsNullOrWhiteSpace(step.TargetSymbol)
            ? step.TargetSymbol
            : AgentMethodInventory.ExtractTargetSymbolFromChange(step.Change ?? "");
        string? lineRange = null;
        var confidence = 0;
        var roundsCompleted = 0;
        await EmitLog(emitSse, "info", $"🔍 Exploring: {relPath}", ct: ct);
        if (emitSse)
            await SendSse(Response, "step", new
            {
                index = planItemIndex,
                type = "edit",
                status = "exploring",
                path = relPath,
                description = step.Change,
                planItemIndex
            }, ct);
        await PersistStepStatusAsync(cardId, planItemIndex, "exploring", emitSse, ct);
        var fileContent = string.Empty;
        if (System.IO.File.Exists(fullPath) &&
            AgentProjectUtilities.IsPathUnderRoot(fullPath, projectRoot))
        {
            fileContent = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
            var excerpt = fileContent.Length > cfg4.fileBodyTruncationChars
                ? AgentDiscovery.ExtractRelevantExcerpt(fileContent, step.Change ?? "", step.OldString, cfg4.fileBodyTruncationChars, Path.GetExtension(relPath).ToLowerInvariant())
                : fileContent;
            var ext = Path.GetExtension(relPath).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(targetSymbol) && AstCodeEditorService.IsSupportedExtension(ext))
            {
                var changeWords = AgentDiscovery.ExtractMeaningfulKeywords((step.Change ?? "").ToLowerInvariant())
                    .Where(w => w.Length >= 4)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var allFuncs = AstCodeEditorService.FindAllFunctions(fileContent, ext);
                (string name, string source, int startLine)? funcBest = null;
                var funcBestScore = 0;
                Func<string, string> stem = w =>
                {
                    if (w.EndsWith("ies")) return w[..^3] + "y";
                    if (w.EndsWith("ves")) return w[..^3] + "f";
                    if (w.EndsWith("es")) return w[..^2];
                    if (w.EndsWith("s") && !w.EndsWith("ss")) return w[..^1];
                    if (w.EndsWith("ing")) return w[..^3];
                    if (w.EndsWith("ed")) return w[..^2];
                    if (w.EndsWith("ly")) return w[..^2];
                    if (w.EndsWith("er")) return w[..^2];
                    if (w.EndsWith("est")) return w[..^2];
                    return w;
                };
                foreach (var func in allFuncs)
                {
                    var score = 0;
                    var funcLower = func.name.ToLowerInvariant();
                    var funcStem = stem(funcLower);
                    var tokens = Regex.Matches(func.name, @"[a-z]+|[A-Z][a-z]*")
                        .Select(m => m.Value.ToLowerInvariant())
                        .ToHashSet();
                    foreach (var cw in changeWords)
                    {
                        var cwStem = stem(cw);
                        if (funcLower == cw || funcStem == cwStem)
                            score += 10;
                        if (tokens.Contains(cw) || tokens.Contains(cwStem))
                            score += 5;
                        if (funcLower.Contains(cw) || funcLower.Contains(cwStem) ||
                            cw.Contains(funcLower) || cw.Contains(funcStem))
                            if (cw.Length >= 4 || cwStem.Length >= 4)
                                score += 3;
                    }
                    if (score > funcBestScore)
                    {
                        funcBestScore = score;
                        funcBest = func;
                    }
                }
                if (funcBest != null && funcBestScore >= 3)
                {
                    targetSymbol = funcBest.Value.name;
                    await EmitLog(emitSse, "info",
                        $"  🎯 Inferred target symbol '{targetSymbol}' from {relPath} using AST scoring (score {funcBestScore})", ct: ct);
                }
            }
            if (!string.IsNullOrWhiteSpace(targetSymbol))
            {
                var symbolMatches = Regex.Matches(fileContent, $@"\b{Regex.Escape(targetSymbol)}\b");
                if (symbolMatches.Count > 0)
                {
                    var matchLine = fileContent[..symbolMatches[0].Index].Count(c => c == '\n') + 1;
                    var startLine = Math.Max(1, matchLine - 20);
                    var endLine = Math.Min(fileContent.Split('\n').Length, matchLine + 40);
                    var lines = fileContent.Split('\n');
                    excerpt = string.Join("\n", lines.Skip(startLine - 1).Take(endLine - startLine + 1));
                }
            }
            ctx.AppendLine($"### TARGET FILE: {relPath}  ({fileContent.Length:N0} chars total)");
            ctx.AppendLine("```");
            ctx.AppendLine(excerpt);
            ctx.AppendLine("```");
            ctx.AppendLine();
            AddFileRead(relPath);
            await EmitLog(emitSse, "info", $"  📄 {relPath}", ct: ct);
        }
        var targetWasAttached = attachedFiles != null &&
            attachedFiles.Any(af =>
            {
                var normAf = af.TrimStart('/', '\\').Replace('\\', '/');
                return string.Equals(normAf, relPath, StringComparison.OrdinalIgnoreCase);
            });
        if (targetWasAttached)
        {
            var fallbackSymbol = AgentMethodInventory.ExtractTargetSymbolFromChange(step.Change ?? "");
            var finalTargetSymbol = !string.IsNullOrWhiteSpace(targetSymbol)
                ? targetSymbol
                : fallbackSymbol;
            var hasValidSymbolInFile = AgentMethodInventory.SymbolExistsInContent(finalTargetSymbol ?? "", fileContent);
            if (!hasValidSymbolInFile && !string.IsNullOrWhiteSpace(fallbackSymbol) && fallbackSymbol != targetSymbol)
            {
                finalTargetSymbol = fallbackSymbol;
                hasValidSymbolInFile = AgentMethodInventory.SymbolExistsInContent(finalTargetSymbol, fileContent);
                if (hasValidSymbolInFile)
                {
                    await EmitLog(emitSse, "info",
                        $"  🎯 Planner targetSymbol '{targetSymbol}' not found in file — fallback to '{finalTargetSymbol}' extracted from change description", ct: ct);
                }
            }
            if (!hasValidSymbolInFile)
            {
                finalTargetSymbol = null;
                await EmitLog(emitSse, "info",
                    "  ✓ Target file was attached by user — skipping LLM exploration and passing the full discovered context through to the resolver because no trustworthy target symbol could be validated in the file.",
                    ct: ct);
            }
            else
            {
                await EmitLog(emitSse, "info",
                    $"  ✓ Target file was attached by user — skipping LLM exploration, returning content directly (target symbol: '{finalTargetSymbol}')", ct: ct);
            }
            return new StepExplorationResult
            {
                EnrichedStep = step,
                ExplorationContext = ctx.ToString(),
                FilesRead = filesRead.ToList(),
                RefinedChange = step?.Change ?? "",
                TargetSymbol = finalTargetSymbol,
                EstimatedLineRange = null,
                Confidence = 100,
                LowConfidenceWarning = null
            };
        }
        for (var round = 0; round < MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            roundsCompleted = round + 1;
            if (emitSse)
                await SendSse(Response, "step-explore", new
                {
                    planItemIndex,
                    round,
                    filesRead = filesRead.ToList(),
                    message = $"Exploration round {round + 1}/{MaxRounds}"
                }, ct);
            var (raw, _, _) = await CallLlmRaw(
                BuildStepExplorationSystemPrompt(),
                BuildStepExplorationPrompt(step, originalPrompt, fullPlan, planItemIndex, ctx.ToString(), filesRead, round),
                ct, requestTimeout: _infiniteTimeout, maxTokens: 1024
            );
            if (string.IsNullOrWhiteSpace(raw)) break;
            var parsed = AgentPlanParsing.ParseStepExplorationResponse(raw);
            if (!string.IsNullOrWhiteSpace(parsed.RefinedChange))
            {
                refinedChange = parsed.RefinedChange;
            }
            if (!string.IsNullOrWhiteSpace(parsed.TargetSymbol))
                targetSymbol = parsed.TargetSymbol;
            if (!string.IsNullOrWhiteSpace(parsed.LineRange))
                lineRange = parsed.LineRange;
            if (parsed.Confidence > 0)
                confidence = parsed.Confidence;
            if (parsed.Ready || parsed.Confidence >= ConfidenceThreshold)
            {
                await EmitLog(emitSse, "info",
                    $"  ✓ Ready — round {round + 1}, confidence {parsed.Confidence}%", ct: ct);
                break;
            }
            if (parsed.FilesToRead.Count == 0)
            {
                await EmitLog(emitSse, "info",
                    $"  ✓ No more files requested (round {round + 1})", ct: ct);
                break;
            }
            var newlyRead = 0;
            foreach (var requested in parsed.FilesToRead.Take(3))
            {
                if (normalizedPaths.Contains(AbsNormalize(requested)) ||
                    filesRead.Contains(RelNormalize(requested))) continue;
                var fp = Path.GetFullPath(
                    Path.Combine(projectRoot, requested.Replace('/', Path.DirectorySeparatorChar)));
                if (!System.IO.File.Exists(fp) ||
                    !AgentProjectUtilities.IsPathUnderRoot(fp, projectRoot))
                {
                    var matches = FindLikelyProjectFiles(requested, max: 5);
                    if (matches.Count > 0)
                    {
                        var readMatches = 0;
                        foreach (var correctPath in matches.Take(2))
                        {
                            if (normalizedPaths.Contains(AbsNormalize(correctPath)) ||
                                filesRead.Contains(RelNormalize(correctPath))) continue;
                            var matchFull = Path.GetFullPath(Path.Combine(projectRoot, correctPath.Replace('/', Path.DirectorySeparatorChar)));
                            var matchContent = await System.IO.File.ReadAllTextAsync(matchFull, Encoding.UTF8, ct);
                            var matchExt = Path.GetExtension(correctPath).ToLowerInvariant();
                            var matchExcerpt = matchContent.Length > cfg4.fileBodyTruncationChars
                                ? AgentDiscovery.ExtractRelevantExcerpt(matchContent, step.Change ?? "", step.OldString, cfg4.fileBodyTruncationChars, matchExt)
                                : matchContent;
                            if (ctx.Length + matchExcerpt.Length <= MaxContextChars)
                            {
                                ctx.AppendLine($"### {correctPath}  (resolved from `{requested}`)");
                                ctx.AppendLine("```");
                                ctx.AppendLine(matchExcerpt);
                                ctx.AppendLine("```");
                                ctx.AppendLine();
                                readMatches++;
                            }
                            else
                            {
                                ctx.AppendLine($"⚠ `{requested}` resolved to `{correctPath}` (skipped — context budget exhausted)");
                                ctx.AppendLine();
                            }
                            AddFileRead(correctPath);
                        }
                        if (matches.Count > 2)
                        {
                            var suggestions = string.Join(", ", matches.Skip(2).Select(m => $"`{m}`"));
                            ctx.AppendLine($"Other likely matches for `{requested}`: {suggestions}.");
                            ctx.AppendLine();
                        }
                        await EmitLog(emitSse, "info",
                            $"  🔍 {requested} → {string.Join(", ", matches.Take(2))}" +
                            (readMatches == 0 ? " (not read: context budget or duplicate)" : ""), ct: ct);
                    }
                    else
                    {
                        await EmitLog(emitSse, "warn",
                            $"  ⚠ Not found: {requested}", ct: ct);
                        ctx.AppendLine($"⚠ The path `{requested}` does not exist. Use an exact relative path from the project root.");
                        ctx.AppendLine();
                    }
                    continue;
                }
                var fc = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                var fcExt = Path.GetExtension(requested).ToLowerInvariant();
                var excerpt = fc.Length > cfg4.fileBodyTruncationChars
                    ? AgentDiscovery.ExtractRelevantExcerpt(fc, step.Change ?? "", step.OldString, cfg4.fileBodyTruncationChars, fcExt)
                    : fc;
                if (ctx.Length + excerpt.Length > MaxContextChars)
                {
                    var budget = MaxContextChars - ctx.Length;
                    if (budget < 400)
                    {
                        await EmitLog(emitSse, "info",
                            "  Context budget exhausted", ct: ct);
                        goto ExplorationComplete;
                    }
                    excerpt = excerpt[..budget] + "\n... [context limit]";
                }
                ctx.AppendLine($"### {requested}");
                ctx.AppendLine("```");
                ctx.AppendLine(excerpt);
                ctx.AppendLine("```");
                ctx.AppendLine();
                AddFileRead(requested);
                newlyRead++;
                await EmitLog(emitSse, "info", $"  📄 {requested}", ct: ct);
            }
            if (newlyRead == 0 && parsed.FilesToRead.Count == 0) break;
        }
    ExplorationComplete:
        if (ctx.Length > 0 && !string.IsNullOrWhiteSpace(step.Change))
        {
            var contextFiles = Regex.Matches(ctx.ToString(), @"^###\s+([^\n]+)", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p) &&
                    !p.StartsWith("TARGET FILE:", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(p.Split(' ')[0], relPath, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Split(' ')[0].TrimEnd(')', '('))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (contextFiles.Count > 2)
            {
                var filterPrompt = new StringBuilder();
                filterPrompt.AppendLine("You are a file relevance filter. Given a task and a list of files, identify which files are UNNECESSARY for completing the task. Return ONLY the file paths that are NOT useful, one per line. If all files are useful, return empty.");
                filterPrompt.AppendLine();
                filterPrompt.AppendLine($"TASK: {step.Change}");
                filterPrompt.AppendLine();
                filterPrompt.AppendLine("FILES IN CONTEXT:");
                foreach (var f in contextFiles)
                    filterPrompt.AppendLine($"  {f}");
                filterPrompt.AppendLine();
                filterPrompt.AppendLine("Return the paths of files that are NOT useful, one per line. If all are useful, return nothing.");
                var (filterRaw, _, _) = await CallLlmRaw(
                    "You are a file relevance filter. Be concise and accurate.",
                    filterPrompt.ToString(),
                    ct, _infiniteTimeout, maxTokens: 512);
                if (!string.IsNullOrWhiteSpace(filterRaw))
                {
                    var toRemove = filterRaw.Split('\n')
                        .Select(l => l.Trim().Trim('"', '*', '`', '-', ' '))
                        .Where(l => !string.IsNullOrWhiteSpace(l) &&
                            contextFiles.Any(cf => l.Contains(cf, StringComparison.OrdinalIgnoreCase)))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (var removePath in toRemove)
                    {
                        var matchFile = contextFiles.FirstOrDefault(cf =>
                            removePath.Contains(cf, StringComparison.OrdinalIgnoreCase));
                        if (matchFile == null) continue;
                        var sectionMatch = Regex.Match(ctx.ToString(),
                            $@"^### {Regex.Escape(matchFile)}[^\n]*\n.*?(?=^### |\Z)",
                            RegexOptions.Multiline | RegexOptions.Singleline);
                        if (sectionMatch.Success)
                        {
                            ctx.Remove(sectionMatch.Index, sectionMatch.Length);
                            await EmitLog(emitSse, "info",
                                $"  🗑️ LLM filter dropped: {matchFile} (not useful for task)", ct: ct);
                        }
                    }
                }
            }
        }
        string? astOldStringHint = null;
        if (!string.IsNullOrWhiteSpace(targetSymbol) &&
            System.IO.File.Exists(fullPath))
        {
            var ext = Path.GetExtension(relPath).ToLowerInvariant();
            var supportedExt = ext is ".cs" or ".ts" or ".js" or ".tsx" or ".jsx";
            if (supportedExt)
            {
                string? astOld = null;
                string? astErr = null;
                var resolvedType = "";
                foreach (var tryType in new[] { "method", "class", "interface", "property" })
                {
                    (astOld, astErr) = AstResolveEdit(fullPath, tryType, targetSymbol);
                    if (astOld != null) { resolvedType = tryType; break; }
                }
                if (astOld != null)
                {
                    var lineCount = astOld.Split('\n').Length;
                    var changeLower = (refinedChange ?? step.Change ?? "").ToLowerInvariant();
                    var isPropertyAdd = (changeLower.Contains("add") || changeLower.Contains("fill") || changeLower.Contains("populate")) &&
                        (changeLower.Contains("propert") || changeLower.Contains("field") ||
                         changeLower.Contains("column") || changeLower.Contains("setting") ||
                         changeLower.Contains("option") || changeLower.Contains("bool") ||
                         changeLower.Contains("string") || changeLower.Contains("int") ||
                         changeLower.Contains("{ get;") || changeLower.Contains("{get;"));
                    if (resolvedType == "class" && (lineCount > 20 || isPropertyAdd))
                    {
                        await EmitLog(emitSse, "info",
                            $"  🎯 AST resolved '{targetSymbol}' as class " +
                            $"({lineCount} lines) — {(isPropertyAdd ? "change targets a property add — skipping class AST hint to avoid full-class rewrite" : "too large for hint")}, " +
                            $"skipping to keep excerpt focused", ct: ct);
                    }
                    else
                    {
                        astOldStringHint = astOld;
                        await EmitLog(emitSse, "info",
                            $"  🎯 AST resolved '{targetSymbol}' " +
                            $"({lineCount} lines)", ct: ct);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(astErr))
                {
                    await EmitLog(emitSse, "info",
                        $"  AST hint failed ({astErr}) — will use text matching", ct: ct);
                }
            }
        }
        string? lowConfidenceWarning = null;
        if (roundsCompleted >= MaxRounds && confidence > 0 && confidence < 30)
        {
            lowConfidenceWarning =
                $"Exploration exhausted {MaxRounds} rounds at only {confidence}% confidence — " +
                $"step description may be too vague for a reliable edit";
            var reDerived = await ReDeriveStepDescription(
                step, originalPrompt, ctx.ToString(), refinedChange ?? step.Change ?? "", ct);
            if (!string.IsNullOrWhiteSpace(reDerived) &&
                reDerived.Length > (refinedChange?.Length ?? 0) / 3)
            {
                await EmitLog(emitSse, "info",
                    $"  🔄 Re-derived from original prompt " +
                    $"(was {confidence}% after {roundsCompleted} rounds)", ct: ct);
                refinedChange = reDerived;
            }
        }
        var enrichedStep = new PlanStep
        {
            File = step.File,
            Change = (string.IsNullOrWhiteSpace(refinedChange) ? step.Change : refinedChange) ?? "",
            Priority = step.Priority,
            LineNumber = step.LineNumber,
            OldString = astOldStringHint ?? step.OldString ?? "",
            NewString = step.NewString ?? ""
        };
        await PersistStepExplorationAsync(cardId, planItemIndex, new
        {
            status = "ready",
            filesRead = filesRead.ToList(),
            rounds = roundsCompleted,
            refinedChange,
            originalChange = step.Change,
            targetSymbol,
            estimatedLineRange = lineRange,
            confidence,
            astResolved = astOldStringHint != null,
            lowConfidenceWarning
        }, emitSse, ct);
        await EmitLog(emitSse, "info",
            $"  ✅ Exploration done — {filesRead.Count} file(s), confidence {confidence}%", filesRead.ToList(),
            ct: ct);
        return new StepExplorationResult
        {
            EnrichedStep = enrichedStep,
            ExplorationContext = ctx.ToString(),
            FilesRead = filesRead.ToList(),
            RefinedChange = refinedChange ?? "",
            TargetSymbol = targetSymbol,
            EstimatedLineRange = lineRange,
            Confidence = confidence,
            RoundsCompleted = roundsCompleted,
            LowConfidenceWarning = lowConfidenceWarning
        };
    }
    private async Task<string?> ReDeriveStepDescription(
        PlanStep step,
        string originalPrompt,
        string explorationContext,
        string vagueDescription,
        CancellationToken ct)
    {
        var sysPrompt = "You are an expert code reviewer. Given the original user request, "
            + "the exploration context (files read and their content), and the current step "
            + "description (which may be vague), produce a crisp, specific, one-sentence "
            + "re-description of exactly what code change this step requires. Be concrete — "
            + "include the file path, the symbol or code region, and the exact nature of the "
            + "change (add, modify, delete, rename). Output ONLY the re-derived description, "
            + "no JSON, no explanation.";
        var userPrompt =
            $"## Original User Request\n{originalPrompt}\n\n"
            + $"## Exploration Context (files read)\n{explorationContext}\n\n"
            + $"## Current Step Description (may be vague)\n{vagueDescription}\n\n"
            + "Produce a crisp, specific, one-sentence re-description of this step's code change:";
        var (raw, _, _) = await CallLlmRaw(
            sysPrompt, userPrompt, ct,
            _infiniteTimeout, maxTokens: 256);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim().Trim('"').Trim();
        if (cleaned.Length > 250) cleaned = cleaned[..250] + "…";
        return cleaned;
    }
    private async Task<string> EnrichContextWithProjectTypesAndSql(
        string projectRoot, string relPath, string stepChange, string explorationContext,
        HashSet<string> alreadyRead, bool emitSse, CancellationToken ct,
        string? targetSymbol = null)
    {
        var buf = new StringBuilder();
        const int MaxEnrichChars = 6000;
        var targetFullPath = Path.GetFullPath(
            Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!System.IO.File.Exists(targetFullPath)) return explorationContext;
        var targetContent = await System.IO.File.ReadAllTextAsync(targetFullPath, Encoding.UTF8, ct);
        var methodName = targetSymbol;
        if (methodName == null)
        {
            var methodNameMatch = Regex.Match(stepChange,
                @"(?:Modify|Update|Change|Edit|Replace|Add|Remove|Delete)\s+(?:the|this|that|a|an)?\s*(\w+)",
                RegexOptions.IgnoreCase);
            methodName = methodNameMatch.Success ? methodNameMatch.Groups[1].Value : null;
        }
        string? methodBody = null;
        if (methodName != null && methodName != "?")
        {
            var methodStartMatch = Regex.Match(targetContent,
                $@"({Regex.Escape(methodName)}\s*\()", RegexOptions.IgnoreCase);
            if (methodStartMatch.Success)
            {
                var startIdx = methodStartMatch.Index;
                var searchFrom = startIdx + methodStartMatch.Length;
                var parenDepth = 1;
                var braceIdx = -1;
                for (var i = searchFrom; i < targetContent.Length; i++)
                {
                    if (targetContent[i] == '(') parenDepth++;
                    else if (targetContent[i] == ')') parenDepth--;
                    else if (targetContent[i] == '{' && parenDepth == 0)
                    { braceIdx = i; break; }
                }
                if (braceIdx > 0)
                {
                    var depth = 0;
                    var endIdx = -1;
                    for (var i = braceIdx; i < targetContent.Length; i++)
                    {
                        if (targetContent[i] == '{') depth++;
                        else if (targetContent[i] == '}') { depth--; if (depth == 0) { endIdx = i; break; } }
                    }
                    if (endIdx > 0)
                        methodBody = targetContent.Substring(braceIdx, endIdx - braceIdx + 1);
                }
            }
        }
        var searchScope = methodBody ?? targetContent;
        var sqlStrings = new List<string>();
        foreach (Match sm in Regex.Matches(searchScope,
            @"@?""(?:[^""\\]*(?:\\.[^""\\]*)*)""", RegexOptions.Singleline))
        {
            var raw = sm.Value;
            if (Regex.IsMatch(raw, @"\b(SELECT|INSERT|UPDATE|DELETE|CREATE\s+TABLE|ALTER\s+TABLE)\b",
                RegexOptions.IgnoreCase))
                sqlStrings.Add(raw);
        }
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(string.Join("\n", sqlStrings),
            @"(?:FROM|JOIN|INTO|UPDATE|TABLE(?:\s+IF\s+NOT\s+EXISTS)?)\s+`?(\w+(?:\.\w+)?)`?",
            RegexOptions.IgnoreCase))
        {
            var rawTbl = m.Groups[1].Value;
            var tbl = rawTbl.Contains('.') ? rawTbl.Split('.')[^1] : rawTbl;
            if (tbl.Length > 2 && !notTableWords.Contains(tbl) &&
                tbl[0] != '@' && !char.IsDigit(tbl[0]))
                tableNames.Add(tbl);
        }
        var typeRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(searchScope,
            @"(?:public|private|protected|readonly|static)?\s*(?:\w+)\s*:\s*([A-Z][A-Za-z0-9_]+)",
            RegexOptions.Compiled))
        {
            var name = m.Groups[1].Value;
            if (!skipTypes.Contains(name) && name.Length > 2 &&
                !serviceSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)))
                typeRefs.Add(name);
        }
        foreach (Match m in Regex.Matches(searchScope,
            @"<\s*([A-Z][A-Za-z0-9_]+)\s*>",
            RegexOptions.Compiled))
        {
            var name = m.Groups[1].Value;
            if (!skipTypes.Contains(name) && name.Length > 2)
                typeRefs.Add(name);
        }
        await EmitLog(emitSse, "info",
            $"  🔎 Enrichment: {tableNames.Count} table(s) [{string.Join(", ", tableNames.Take(5))}], " +
            $"{typeRefs.Count} model type(s) from method '{(methodName ?? "?")}'", new { typeRefs, tableNames }, ct: ct);
        if (typeRefs.Count == 0 && tableNames.Count == 0)
            return explorationContext;
        var typeFileExtensions = new[] { "*.cs", "*.ts", "*.tsx", "*.js", "*.jsx" };
        var projectFiles = typeFileExtensions
            .SelectMany(ext => Directory.EnumerateFiles(projectRoot, ext, SearchOption.AllDirectories))
            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
                     && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\")
                     && !f.Contains("\\dist\\"))
            .ToList();
        foreach (var tblName in tableNames)
        {
            if (buf.Length > MaxEnrichChars) break;
            foreach (var pf in projectFiles)
            {
                if (buf.Length > MaxEnrichChars) break;
                try
                {
                    var content = await System.IO.File.ReadAllTextAsync(pf, Encoding.UTF8, ct);
                    var rel = Path.GetRelativePath(projectRoot, pf).Replace('\\', '/');
                    if (rel == relPath || alreadyRead.Contains(rel) || alreadyRead.Contains(pf))
                        continue;
                    var sqlFound = new List<string>();
                    foreach (Match sm in Regex.Matches(content,
                        @"@?""(?:[^""\\]*(?:\\.[^""\\]*)*)""", RegexOptions.Singleline))
                    {
                        var val = sm.Value;
                        if (!Regex.IsMatch(val, @"\b(SELECT|INSERT|UPDATE|DELETE)\b",
                            RegexOptions.IgnoreCase)) continue;
                        if (Regex.IsMatch(val, @"\b" + Regex.Escape(tblName) + @"\b",
                            RegexOptions.IgnoreCase))
                        {
                            var clean = val.Length > 300 ? val[..297] + "..." : val;
                            sqlFound.Add(clean);
                        }
                    }
                    if (sqlFound.Count == 0) continue;
                    alreadyRead.Add(rel);
                    buf.AppendLine($"### {rel}  (table: {tblName})");
                    buf.AppendLine("```sql");
                    foreach (var s in sqlFound.Take(5))
                        buf.AppendLine(s);
                    buf.AppendLine("```");
                    buf.AppendLine();
                }
                catch { continue; }
            }
        }
        foreach (var typeName in typeRefs.OrderByDescending(t => t.Length))
        {
            if (buf.Length > MaxEnrichChars) break;
            foreach (var pf in projectFiles)
            {
                if (buf.Length > MaxEnrichChars) break;
                try
                {
                    var content = await System.IO.File.ReadAllTextAsync(pf, Encoding.UTF8, ct);
                    if (Regex.IsMatch(content,
                        $@"(?:class|record|struct|interface|type)\s+{Regex.Escape(typeName)}\b",
                        RegexOptions.IgnoreCase))
                    {
                        var rel = Path.GetRelativePath(projectRoot, pf).Replace('\\', '/');
                        if (alreadyRead.Contains("_type:" + rel) || alreadyRead.Contains("_type:" + pf))
                            continue;
                        alreadyRead.Add("_type:" + rel);
                        var excerpt = AgentDiscovery.ExtractRelevantExcerpt(content, typeName, null, 800);
                        buf.AppendLine($"### {rel}  (model: {typeName})");
                        buf.AppendLine("```csharp");
                        buf.AppendLine(excerpt);
                        buf.AppendLine("```");
                        buf.AppendLine();
                        break;
                    }
                }
                catch { continue; }
            }
        }
        if (buf.Length == 0) return explorationContext;
        var enrichment = buf.ToString();
        await EmitLog(emitSse, "info",
            $"  📄 Auto-enriched context ({enrichment.Length:N0} chars)", new { enrichment }, ct: ct);
        var propertyWarning = "\n⚠ CRITICAL: The type definitions below show the EXACT property names. " +
            "Every `.PropertyName` you write in your edit MUST match these definitions exactly. " +
            "For example, if CalendarEntry shows `Note` property, use `.Note` not `.Description`. " +
            "If it shows `Type`, use `.Type` not `.Title`. Cross-reference EVERY property access.\n";
        return explorationContext + "\n### AUTO-ENRICHED CONTEXT\n" + propertyWarning + enrichment;
    }
    private async Task PersistStepExplorationAsync(
        string? cardId, int planItemIndex, object explorationData,
        bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || planItemIndex < 0) return;
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;
            foreach (var column in new[] { "todo", "doing", "done", "selfImproving" })
            {
                if (!root.TryGetPropertyValue(column, out var colNode) ||
                    colNode is not JsonArray colItems) continue;
                foreach (var item in colItems)
                {
                    if (item is not JsonObject card ||
                        card["id"]?.GetValue<string>() != cardId) continue;
                    if (card["_plan"] is not JsonObject plan ||
                        plan["items"] is not JsonArray items) continue;
                    var target = items.FirstOrDefault(i =>
                        i is JsonObject o &&
                        o["index"]?.GetValue<int>() == planItemIndex);
                    if (target is not JsonObject stepObj) continue;
                    stepObj["exploration"] = JsonNode.Parse(
                        JsonSerializer.Serialize(explorationData));
                    stepObj["status"] = "ready";
                    await _boardData.SaveRawAsync(
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    if (emitSse)
                        await SendSse(Response, "refresh", new
                        {
                            target = "boarddata",
                            reason = "step-exploration-complete",
                            cardId,
                            planItemIndex
                        }, ct);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            await EmitLog(true, "error", "Failed to persist step status - halting to prevent data loss", new { cardId, planItemIndex, error = ex.Message });
            throw;
        }
    }
    private async Task PersistStepStatusAsync(
        string? cardId, int planItemIndex, string status,
        bool emitSse, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cardId) || planItemIndex < 0) return;
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var jsonDoc = JsonDocument.Parse(raw);
            var root = JsonNode.Parse(jsonDoc.RootElement.GetRawText())?.AsObject();
            if (root == null) return;
            foreach (var column in new[] { "todo", "doing", "done", "selfImproving" })
            {
                if (!root.TryGetPropertyValue(column, out var colNode) ||
                    colNode is not JsonArray colItems) continue;
                foreach (var item in colItems)
                {
                    if (item is not JsonObject card ||
                        card["id"]?.GetValue<string>() != cardId) continue;
                    if (card["_plan"] is not JsonObject plan ||
                        plan["items"] is not JsonArray items) continue;
                    var target = items.FirstOrDefault(i =>
                        i is JsonObject o && o["index"]?.GetValue<int>() == planItemIndex);
                    if (target is not JsonObject stepObj) continue;
                    stepObj["status"] = status;
                    await _boardData.SaveRawAsync(
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    if (emitSse)
                        await SendSse(Response, "refresh", new
                        {
                            target = "boarddata",
                            reason = "step-status-update",
                            cardId,
                            planItemIndex,
                            status
                        }, ct);
                    return;
                }
            }
        }
        catch { }
    }
    private async Task AutoAttachFileToCardAsync(string cardId, string filePath, bool emitSse, CancellationToken ct)
    {
        try
        {
            var raw = await _boardData.LoadRawAsync();
            if (raw == null) return;
            var root = JsonNode.Parse(raw)?.AsObject();
            if (root == null) return;
            var columns = root["columns"]?.AsArray();
            if (columns == null) return;
            foreach (var col in columns)
            {
                var cards = col?["cards"]?.AsArray();
                if (cards == null) continue;
                foreach (var c in cards)
                {
                    var id = c?["id"]?.GetValue<string>();
                    if (id != cardId) continue;
                    var attached = c!["attached"];
                    if (attached == null)
                        c["attached"] = new JsonArray { filePath };
                    else
                    {
                        var arr = attached.AsArray();
                        var exists = arr.Any(e => e?.GetValue<string>() == filePath);
                        if (!exists)
                            arr.Add(filePath);
                    }
                    await _boardData.SaveRawAsync(
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    if (emitSse)
                        await SendSse(Response, "refresh", new
                        {
                            target = "boarddata",
                            reason = "auto-attach",
                            cardId,
                            filePath
                        }, ct);
                    return;
                }
            }
        }
        catch { }
    }
}
