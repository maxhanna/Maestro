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
    private async Task<string?> LoadEditKnowledgeHeaderAsync(string projectRoot, bool emitSse, CancellationToken ct)
    {
        var ek = await _editKnowledge.LoadAsync(projectRoot, ct);
        if (ek == null) return null;
        var header = EditKnowledgeService.FormatForContext(ek);
        if (!string.IsNullOrWhiteSpace(header))
        {
            await EmitLog(emitSse, "info",
                $"Loaded edit knowledge for project: {ek.ProjectName} " +
                $"({ek.Do.Count} do, {ek.Dont.Count} dont, " +
                $"{ek.Patterns.Count} pattern categories, " +
                $"{ek.RecentFailures.Count} recent failures)", ct: ct);
        }
        return header;
    }
    private async Task<(List<object> steps, AgentPlan plan, bool complete)> StepResolutionPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null,
        bool skipContextReview = false,
        string? steeringContext = null,
        string? cardId = null,
        Task<bool>? connectivityTask = null)
    {
        var cfg = await LoadConfigAsync();

        // ── Startup: fire all independent disk-bound work concurrently ──
        // Bootstrap discovery (file enumeration), project skeleton generation and the
        // edit-knowledge load share no dependencies, so they run side-by-side instead
        // of one-after-another. The LLM connectivity probe (started in Orchestrate)
        // overlaps them too and is awaited before any LLM call below.
        var allSteps = new List<object>();
        await EmitLog(emitSse, "info", "Phase 1 — DISCOVER", new { prompt, attachedFiles, steeringContext, cardId }, ct: ct);
        // These disk/DB tasks deliberately start before the connectivity probe is
        // awaited: on the rare probe failure their results are discarded, which is
        // acceptable (probe result is cached for 5 minutes) and usually the overlap
        // saves real wall-clock time at startup.
        var bootstrapTask = RunBootstrapDiscovery(prompt, projectRoot, emitSse, attachedFiles, ct);
        // When the user attached specific files, the whole-project skeleton is noise: the agent must
        // think and plan only inside the attached files, not the project at large. Skip skeleton
        // generation so it can't leak unrelated paths (styles.css, other components, etc.) into the
        // discovery context that feeds the thinking phase and the planner.
        var hasAttachedFiles = attachedFiles != null && attachedFiles.Count > 0;
        Task<AgentSkeleton.SkeletonResult>? skeletonTask = null;
        if (cfg.includeProjectSkeleton && !hasAttachedFiles)
            skeletonTask = AgentSkeleton.GenerateSkeletonAsync(projectRoot);
        Task<string?>? editKnowledgeTask = null;
        // Edit knowledge is whole-project context (do/don't/patterns across many files) — it
        // references files outside the attached set, so suppress it for attached-file tasks
        // (mirrors the skeleton suppression above).
        if (cfg.includeEditKnowledge && !hasAttachedFiles)
            editKnowledgeTask = LoadEditKnowledgeHeaderAsync(projectRoot, emitSse, ct);

        // LLM connectivity must pass before any LLM call below.
        connectivityTask ??= CheckLlmConnectivity(projectRoot, emitSse, ct);
        if (!await connectivityTask)
            throw new InvalidOperationException("LLM connectivity check failed.");

        var (discoveryContext, ds) = await bootstrapTask;
        allSteps.AddRange(ds);
        string? editKnowledgeHeader = editKnowledgeTask != null ? await editKnowledgeTask : null;
        AgentSkeleton.SkeletonResult? skeleton = skeletonTask != null ? await skeletonTask : null;

        // ── Startup: fire the three independent LLM calls concurrently ──
        // Complexity assessment, skeleton trimming and the requirement checklist share
        // no dependencies, so they run in parallel instead of back-to-back.
        Task<(int? score, int? atomicSteps)>? complexityTask = (cfg.extendThinking && !string.IsNullOrWhiteSpace(cardId))
            ? AssessComplexityAsync(prompt, cardId, ct, heuristicOnly: hasAttachedFiles)
            : null;
        Task<(string trimmed, string note)>? skeletonTrimTask = null;
        if (skeleton != null && (skeleton.Paths.Count > 0 || !string.IsNullOrWhiteSpace(skeleton.Tree)))
            skeletonTrimTask = TrimSkeletonWithLlm(skeleton, prompt, emitSse, ct);
        var checklistTask = BuildRequirementChecklistAsync(prompt, ct, attachedFiles);

        // Quick complexity assessment for thinking token budgeting (if extendThinking is enabled)
        int? atomicStepEstimate = null;
        if (complexityTask != null)
        {
            try
            {
                var (complexityScore, atomicSteps) = await complexityTask;
                atomicStepEstimate = atomicSteps;
                if (complexityScore.HasValue && emitSse)
                {
                    var tokenCap = GetPlanningTokenCap(complexityScore.Value);
                    await SendSse(Response, "complexity", new
                    {
                        score = complexityScore.Value,
                        tokenCap,
                        maxTokens = cfg.thinkingMaxTokens,
                        atomicSteps,
                        label = complexityScore.Value <= 10 ? "Trivial" :
                                complexityScore.Value <= 25 ? "Simple" :
                                complexityScore.Value <= 45 ? "Moderate" :
                                complexityScore.Value <= 65 ? "Complex" :
                                complexityScore.Value <= 85 ? "Very Complex" : "Extremely Complex"
                    }, ct);
                }
            }
            catch { }
        }

        if (skeleton != null && skeleton.Paths.Count == 0 && string.IsNullOrWhiteSpace(skeleton.Tree))
        {
            await EmitLog(emitSse, "info", "Skeleton generation returned nothing, skipping", ct: ct);
        }
        else if (skeletonTrimTask != null)
        {
            var (trimmed, note) = await skeletonTrimTask;
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                var skeletonSection = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(note))
                    skeletonSection.AppendLine($"### PROJECT ARCHITECTURE NOTE ###\n{note}\n");
                skeletonSection.AppendLine(trimmed);
                discoveryContext = skeletonSection.ToString() + "\n" + discoveryContext;
                await EmitLog(emitSse, "info",
                    $"Skeleton trimmed from {skeleton!.Paths.Count} paths to {trimmed.Length} chars {(string.IsNullOrWhiteSpace(note) ? "" : "— " + note)}", ct: ct);
            }
            else
            {
                await EmitLog(emitSse, "info", "Skeleton trimming produced nothing, skipping", ct: ct);
            }
        }

        string? requirementChecklist = (await checklistTask).Trim();
        if (!string.IsNullOrWhiteSpace(requirementChecklist))
        {
            prompt = prompt + "\n\n" + requirementChecklist;
            await EmitLog(emitSse, "info", "Extracted requirement checklist", new { requirementChecklist }, ct: ct);
        }
        else
        {
            await EmitLog(emitSse, "warn", "Requirement checklist was empty.", ct: ct);
        }
        if (attachedFiles != null && attachedFiles.Count > 0)
        {
            var attachedSteering = "The user has explicitly attached one or more files for editing " +
                                   "(visible in DISCOVERY CONTEXT below).\n" +
                                   "You MUST plan your edits to target only the attached file(s). " +
                                   "Do NOT add _explore steps. Do NOT search for or reference any other files. " +
                                   "Do NOT try to understand how the code is called from elsewhere. " +
                                   "Read the attached files in the DISCOVERY CONTEXT and plan the required edits directly. " +
                                   "If the files are empty, plan steps to populate them with the necessary code based on the user's task.";
            var quotedStrings = Regex.Matches(prompt, @"['""]([^'""]{3,})['""]")
                .Select(m => m.Groups[1].Value)
                .Where(q => !string.IsNullOrWhiteSpace(q) && q.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (quotedStrings.Count > 0)
            {
                var fileContents = new List<(string relPath, string content)>();
                foreach (var af in attachedFiles)
                {
                    var afFullPath = Path.GetFullPath(Path.Combine(projectRoot,
                        af.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
                    if (System.IO.File.Exists(afFullPath))
                        fileContents.Add((af.Replace('\\', '/'), System.IO.File.ReadAllText(afFullPath, Encoding.UTF8)));
                }
                var searchVariants = new List<(string label, string searchText)>();
                foreach (var qs in quotedStrings)
                {
                    searchVariants.Add((qs, qs));
                    var noComma = qs.Replace(",", "").Replace("'", "").Trim();
                    if (noComma != qs && noComma.Length >= 3)
                        searchVariants.Add((qs, noComma));
                    var withInterpolation = Regex.Replace(qs, @"\busername\b", @"\$\{username\}", RegexOptions.IgnoreCase);
                    if (withInterpolation != qs && withInterpolation.Length >= 3)
                        searchVariants.Add((qs, withInterpolation));
                    var firstWord = qs.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (firstWord != null && firstWord.Length >= 3 && firstWord != qs)
                        searchVariants.Add((qs, firstWord));
                    var words = qs.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length >= 2)
                    {
                        var firstTwo = string.Join(" ", words.Take(2));
                        if (firstTwo != qs && firstTwo.Length >= 3)
                            searchVariants.Add((qs, firstTwo));
                    }
                }
                var textMatchHints = new List<string>();
                var matchedQuotedStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (label, searchText) in searchVariants)
                {
                    if (matchedQuotedStrings.Contains(label)) continue;
                    var matchingFiles = fileContents
                        .Where(f => f.content.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(f => f.relPath)
                        .ToList();
                    if (matchingFiles.Count > 0)
                    {
                        matchedQuotedStrings.Add(label);
                        var nonMatching = fileContents
                            .Where(f => f.content.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                            .Select(f => f.relPath)
                            .ToList();
                        var note = nonMatching.Count > 0
                            ? $" (matched via '{searchText}'; NOT found in: {string.Join(", ", nonMatching)})"
                            : " (found in ALL attached files)";
                        textMatchHints.Add($"  - '{label}' found in {string.Join(", ", matchingFiles)}{note}");
                    }
                }
                if (textMatchHints.Count == 0 && fileContents.Count > 1)
                {
                    var taskWords = Regex.Matches(prompt, @"\b([A-Za-z]{4,})\b")
                        .Select(m => m.Groups[1].Value)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(w => !"just from file with that this what have been which your their them into when also than about some then each would make like more than been has could were such only".Contains(w.ToLowerInvariant()))
                        .Take(5)
                        .ToList();
                    foreach (var word in taskWords)
                    {
                        var matchingFiles = fileContents
                            .Where(f => f.content.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(f => f.relPath)
                            .ToList();
                        if (matchingFiles.Count > 0 && matchingFiles.Count < fileContents.Count)
                        {
                            var nonMatching = fileContents
                                .Where(f => f.content.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
                                .Select(f => f.relPath)
                                .ToList();
                            if (nonMatching.Count > 0)
                            {
                                textMatchHints.Add($"  - '{word}' found in {string.Join(", ", matchingFiles)} (NOT found in: {string.Join(", ", nonMatching)})");
                            }
                        }
                    }
                    if (textMatchHints.Count > 0)
                    {
                        await EmitLog(emitSse, "info",
                            $"Cross-file fallback word match: {string.Join("; ", textMatchHints)}", ct: ct);
                    }
                }
                if (textMatchHints.Count > 0)
                {
                    var matchSteering = "\n\n### TARGET TEXT LOCATION ###\n" +
                                        "The task asks to modify/replace existing text. The following text was found in these attached files:\n" +
                                        string.Join("\n", textMatchHints.Distinct(StringComparer.OrdinalIgnoreCase)) + "\n" +
                                        "You MUST edit the file(s) where the text was found. Do NOT add new code in a different file.";
                    attachedSteering += matchSteering;
                    await EmitLog(emitSse, "info",
                        $"Cross-file text match found: {string.Join("; ", textMatchHints.Distinct(StringComparer.OrdinalIgnoreCase))}", ct: ct);
                }
                else
                {
                    await EmitLog(emitSse, "info",
                        $"Cross-file text match searched {quotedStrings.Count} quoted string(s) in {fileContents.Count} file(s) — no matches found. " +
                        $"Quoted strings: {string.Join(", ", quotedStrings)}", ct: ct);
                }
            }
            steeringContext = string.IsNullOrWhiteSpace(steeringContext)
                ? attachedSteering
                : $"{steeringContext}\n\n{attachedSteering}";
        }
        if (!string.IsNullOrWhiteSpace(editKnowledgeHeader))
        {
            discoveryContext = editKnowledgeHeader + "\n\n" + discoveryContext;
        }
        if (emitSse && !skipContextReview)
        {
            await EmitLog(emitSse, "info", $"Reviewing context from {ds.Count} discovery steps ...", ct: ct);
            discoveryContext = await RunContextReview(ds, discoveryContext, allSteps, ct);
        }
        MetaPlanResult? metaPlan = null;
        var planAlreadyExecuted = false;
        var planCompleteDeclared = false;
        AgentPlan plan = new();
        // Pre-edit content of HTML files targeted by this run, captured BEFORE each edit lands.
        // Post-execution template-binding validation uses these to only flag bindings the run
        // INTRODUCED — pre-existing bindings (template refs, array properties, members the
        // regex extractor misses) never false-positive the repair loop. First capture per file
        // wins so a file edited across multiple steps is compared against its ORIGINAL content.
        var preEditSnapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (metaPlan?.SubPlans?.Count > 0)
        {
            await EmitLog(emitSse, "info", $"Phase 2 — META-PLAN ({metaPlan.SubPlans.Count} sub-plans)", ct: ct);
            if (emitSse)
                await SendSse(Response, "phase", new { phase = "metaplan", message = $"Meta-plan: {metaPlan.SubPlans.Count} sub-plans", subPlans = metaPlan.SubPlans }, ct);
            var combinedSteps = new List<PlanStep>();
            var accumulatedContext = new StringBuilder();
            await PersistMetaPlanToCardAsync(cardId, metaPlan, emitSse, ct);
            for (var i = 0; i < metaPlan.SubPlans.Count; i++)
            {
                var subPlan = metaPlan.SubPlans[i];
                var subPrompt = BuildSubPlanPrompt(
                      prompt, subPlan, i + 1, metaPlan.SubPlans.Count,
                      accumulatedContext.Length > 0 ? accumulatedContext.ToString() : null);
                AgentPlan? subPlanResult;
                try
                {
                    // NOTE: do NOT pass the whole-card atomicStepEstimate here — this loop's
                    // planSoFar counts only THIS sub-plan's steps, so a card-level budget would
                    // prematurely truncate a valid multi-step stage. The budget is enforced in
                    // the top-level interleaved loop, which is the actual execution path.
                    var (incSubPlan, updatedCtx) = await RunIncrementalPlanningLoop(
                        subPrompt, discoveryContext, projectRoot, emitSse, ct, subPlan.ContextNote, cardId,
                        atomicStepEstimate: null, attachedFiles: attachedFiles);
                    subPlanResult = incSubPlan;
                    discoveryContext = updatedCtx;
                }
                catch (InvalidOperationException)
                {
                    await EmitLog(emitSse, "info",
                        $"Sub-plan '{subPlan.Title}' produced no actionable steps — treating as already satisfied.", ct: ct);
                    subPlanResult = new AgentPlan { Plan = new List<PlanStep>() };
                }
                if (subPlanResult?.Plan != null && subPlanResult.Plan.Count > 0)
                {
                    subPlanResult.Plan = await PruneIrrelevantPlanStepsAsync(subPlanResult.Plan, projectRoot, ct);
                    if (subPlanResult.Plan.Count > 0)
                    {
                        var subAudit = await PlanPreAuditAsync(subPlanResult, projectRoot, emitSse, ct, prompt);
                        if (subAudit != null && subAudit.Steps.Count > 0)
                        {
                            var alreadyDoneIdx = subAudit.Steps.Where(s => s.AlreadyDone).Select(s => s.Index).ToHashSet();
                            var decoupled = subAudit.Steps.Where(s => s.NeedsDecoupling && s.DecoupledSteps?.Count > 0).ToList();
                            if (alreadyDoneIdx.Count > 0 || decoupled.Count > 0)
                            {
                                var newSubItems = new List<PlanStep>();
                                for (var si = 0; si < subPlanResult.Plan.Count; si++)
                                {
                                    if (alreadyDoneIdx.Contains(si))
                                    {
                                        await EmitLog(emitSse, "info",
                                            $"Sub-plan {subPlan.Title}: step {si + 1} already done — skipping. " +
                                            $"Reason: {subAudit.Steps.First(s => s.Index == si).Reason}", ct: ct);
                                        continue;
                                    }
                                    var dec = decoupled.FirstOrDefault(d => d.Index == si);
                                    if (dec != null)
                                    {
                                        foreach (var sub in dec.DecoupledSteps!)
                                        {
                                            sub.Priority = subPlanResult.Plan[si].Priority;
                                            newSubItems.Add(sub);
                                        }
                                        continue;
                                    }
                                    newSubItems.Add(subPlanResult.Plan[si]);
                                }
                                subPlanResult.Plan = AgentPlanParsing.DeduplicateSimilarSteps(newSubItems);
                            }
                        }
                    }
                    if (subPlanResult.Plan.Count == 0)
                    {
                        await EmitLog(emitSse, "info",
                            $"Sub-plan '{subPlan.Title}' fully collapsed — all target changes already exist. Marking done.", ct: ct);
                        await UpdateMetaPlanSubPlanStatusAsync(cardId, subPlan.Id, true, emitSse, ct);
                        accumulatedContext.AppendLine($"## Sub-plan {i + 1} ({subPlan.Title}) — ALREADY COMPLETE (no changes needed) ##");
                        accumulatedContext.AppendLine();
                        continue;
                    }
                    if (emitSse)
                    {
                        await SendSse(Response, "plan", new
                        {
                            thinking = subPlanResult.Thinking,
                            summary = $"Sub-plan {i + 1}/{metaPlan.SubPlans.Count}: {subPlan.Title}",
                            items = subPlanResult.Plan
                        }, ct);
                    }
                    await PersistBoardDataPlanAsync(cardId, subPlanResult.Plan, emitSse, ct,
                        summary: $"Sub-plan {i + 1}/{metaPlan.SubPlans.Count}: {subPlan.Title}",
                        score: subPlanResult.Score);
                    var subResults = new List<object>();
                    // Snapshot the sub-plan's HTML targets before execution so template-binding
                    // validation only flags bindings these edits introduce (first capture wins).
                    SnapshotPreEditFiles(projectRoot, subPlanResult, preEditSnapshots);
                    await ExecutePlan(prompt, projectRoot, emitSse, "", subPlanResult, ct, subResults,
                        steeringContext: subPlan.ContextNote, attachedFiles: attachedFiles, cardId: cardId);
                    await UpdateMetaPlanSubPlanStatusAsync(cardId, subPlan.Id, true, emitSse, ct);
                    accumulatedContext.AppendLine($"## Sub-plan {i + 1} ({subPlan.Title}) — COMPLETED ##");
                    foreach (var r in subResults.OfType<Dictionary<string, object?>>())
                    {
                        var status = r.GetValueOrDefault("status")?.ToString();
                        var path = r.GetValueOrDefault("path")?.ToString();
                        var desc = r.GetValueOrDefault("description")?.ToString();
                        if (!string.IsNullOrWhiteSpace(path) && status is "done" or "modified" or "created")
                        {
                            accumulatedContext.AppendLine($"  ✓ [{path}] {desc}");
                        }
                    }
                    accumulatedContext.AppendLine();
                }
            }
            plan = new AgentPlan
            {
                Thinking = metaPlan.MetaThinking,
                Summary = metaPlan.MetaSummary,
                Score = 85,
                Plan = combinedSteps
            };
            discoveryContext = accumulatedContext.ToString();
            planAlreadyExecuted = true;
        }
        // Snapshot pre-edit content of plan-targeted HTML files so post-execution template
        // binding validation only flags bindings INTRODUCED by the edit — not pre-existing ones.
        preEditSnapshots = SnapshotPreEditFiles(projectRoot, plan ?? new AgentPlan(), preEditSnapshots);
        if (!planAlreadyExecuted)
        {
            await EmitLog(emitSse, "info", "Phase 2 — PLAN & EXECUTE (interleaved, one atomic step at a time)", ct: ct);
            if (emitSse)
            {
                var ctxBreakdown = BuildContextBreakdown(ds, discoveryContext);
                await SendSse(Response, "phase", new { phase = "plan", message = "Planning & executing one atomic step at a time...", contextSize = AgentTokenMetrics.EstimateTokens(discoveryContext), contextChars = discoveryContext.Length, contextBreakdown = ctxBreakdown, prompt }, ct);
            }
            var (interleavedPlan, interleavedResults, updatedContext, interleavedComplete, interleavedSnapshots) = await RunInterleavedPlanExecutionLoop(
                prompt, discoveryContext, projectRoot, emitSse, ct, steeringContext, cardId, attachedFiles, atomicStepEstimate);
            // Merge the loop's per-step pre-edit captures (captured right before each edit) so
            // post-execution template-binding validation sees the run's pre-edit content even
            // when the pre-loop meta-plan named no HTML files. First capture per file wins.
            foreach (var kv in interleavedSnapshots)
                if (!preEditSnapshots.ContainsKey(kv.Key)) preEditSnapshots[kv.Key] = kv.Value;
            plan = interleavedPlan;
            discoveryContext = updatedContext;
            allSteps.AddRange(interleavedResults);
            planAlreadyExecuted = true;
            planCompleteDeclared = interleavedComplete;
        }
        if (emitSse && !string.IsNullOrWhiteSpace(plan.Thinking))
            await SendSse(Response, "thinking", new { text = plan.Thinking }, ct);
        await EmitLog(emitSse, "info",
            $"Plan: {plan.Plan.Count} step(s) — {string.Join(", ", plan.Plan.Select(p => p.File))}",
            new { plan }, ct: ct);
        if (emitSse)
            await SendSse(Response, "plan",
                new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan }, ct);
        allSteps.Add(new Dictionary<string, object?>
        {
            ["index"] = allSteps.Count,
            ["type"] = "plan",
            ["status"] = "complete",
            ["description"] = "Plan complete"
        });
        if (!planAlreadyExecuted)
        {
            var validationReason = await ValidatePlanAsync(prompt, plan, ct);
            if (_gracefulStop)
            {
                await EmitLog(emitSse, "warn", "User did not respond to command confirmation — skipping card.", ct: ct);
                return (allSteps, plan, false);
            }
            if (validationReason != null)
            {
                await EmitLog(emitSse, "warn",
                    $"Plan validation failed: {validationReason} — replanning…", ct: ct);
                var validationSteering = $"A reviewer flagged the previous plan: {validationReason}. " +
                    "Fix exactly that issue — do not add unrelated files, features, or refactors." +
                    (string.IsNullOrWhiteSpace(steeringContext) ? "" : $"\n\n{steeringContext}");
                var replan = await AnalyzePromptAndPlanCodeChanges(
                    prompt, discoveryContext, projectRoot, emitSse, ct, validationSteering);
                if (replan != null && replan.Plan.Count > 0)
                {
                    plan = MergePlans(plan, replan);
                    if (plan?.Plan?.Count > 0)
                        plan.Plan = await PruneIrrelevantPlanStepsAsync(plan.Plan, projectRoot, ct);
                    if (emitSse && plan != null)
                        await SendSse(Response, "plan",
                            new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan }, ct);
                }
            }
            else
            {
                await EmitLog(emitSse, "success", $"Plan validation passed.", ct: ct);
            }
            if (plan != null && !string.IsNullOrEmpty(projectRoot))
            {
                plan = AgentPlanParsing.EnforceAngularScaffolding(plan, projectRoot) ?? plan;
                plan = AgentPlanParsing.EnforceProxyConfigForControllers(plan, projectRoot) ?? plan;
            }
            if (emitSse && plan?.Plan?.Count > 0)
            {
                await SendSse(Response, "plan",
                    new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan, audited = true }, ct);
            }
            if (!string.IsNullOrWhiteSpace(cardId) && plan?.Plan?.Count > 0)
            {
                await PersistBoardDataPlanAsync(cardId, plan.Plan, emitSse, ct, summary: plan.Summary ?? "", score: plan.Score);
            }
            if (plan?.Plan?.Count > 0)
            {
                var auditResult = await PlanPreAuditAsync(plan, projectRoot, emitSse, ct, prompt);
                if (auditResult != null && auditResult.Steps.Count > 0)
                {
                    var alreadyDoneIndices = auditResult.Steps
                        .Where(s => s.AlreadyDone)
                        .Select(s => s.Index)
                        .ToHashSet();
                    var decoupledSteps = new List<(int originalIndex, List<PlanStep> newSteps)>();
                    foreach (var step in auditResult.Steps)
                    {
                        if (step.NeedsDecoupling && step.DecoupledSteps?.Count > 0)
                        {
                            decoupledSteps.Add((step.Index, step.DecoupledSteps));
                        }
                    }
                    if (alreadyDoneIndices.Count > 0 || decoupledSteps.Count > 0)
                    {
                        var newPlanItems = new List<PlanStep>();
                        for (var i = 0; i < plan.Plan.Count; i++)
                        {
                            if (alreadyDoneIndices.Contains(i))
                            {
                                await EmitLog(emitSse, "info",
                                    $"Plan audit: step {i + 1} already done — skipping. Reason: {auditResult.Steps.First(s => s.Index == i).Reason}", ct: ct);
                                continue;
                            }
                            var decoupled = decoupledSteps.FirstOrDefault(d => d.originalIndex == i);
                            if (decoupled != default)
                            {
                                await EmitLog(emitSse, "info",
                                    $"Plan audit: step {i + 1} decoupled into {decoupled.newSteps.Count} sub-steps", ct: ct);
                                newPlanItems.Add(plan.Plan[i]);
                                foreach (var sub in decoupled.newSteps)
                                {
                                    sub.Priority = plan.Plan[i].Priority;
                                    newPlanItems.Add(sub);
                                }
                                continue;
                            }
                            newPlanItems.Add(plan.Plan[i]);
                        }
                        plan.Plan = newPlanItems;
                        plan.Plan = AgentPlanParsing.RemergeTableCreationSplits(plan.Plan);
                        plan.Plan = AgentPlanParsing.DeduplicateSimilarSteps(plan.Plan);
                        plan.Plan = await PruneIrrelevantPlanStepsAsync(plan.Plan, projectRoot, ct);
                        if (emitSse)
                        {
                            await SendSse(Response, "plan", new { thinking = plan.Thinking, summary = plan.Summary, items = plan.Plan, audited = true }, ct);
                        }
                    }
                }
            }

            if (plan?.Plan?.Count > 1)
            {
                plan = await RunPlanCoherenceCheckAsync(
                    plan, projectRoot, prompt, emitSse, ct);
                if (plan?.Plan?.Count > 0)
                    plan.Plan = await PruneIrrelevantPlanStepsAsync(plan.Plan, projectRoot, ct);
                if (!string.IsNullOrWhiteSpace(cardId) && plan?.Plan?.Count > 0)
                    await PersistBoardDataPlanAsync(cardId, plan.Plan, emitSse, ct,
                        summary: plan.Summary ?? "", score: plan.Score);
            }
            await EmitLog(emitSse, "info", "Phase 3 — EXECUTE", ct: ct);
            if (emitSse)
            {
                await SendSse(Response, "phase", new { phase = "execute", message = "Executing plan…" }, ct);
            }
            try
            {
                await ExecutePlan(prompt, projectRoot, emitSse, discoveryContext, plan ?? new AgentPlan(), ct, allSteps,
                    steeringContext: steeringContext, attachedFiles: attachedFiles,
                    cardId: cardId);
            }
            catch (StepFatalException ex)
            {
                await EmitLog(emitSse, "error",
                    $"⛔ Plan execution halted due to fatal step failure: {ex.Message}", ct: ct);
                if (emitSse)
                {
                    await SendSse(Response, "fatal", new
                    {
                        reason = "A plan step failed irrecoverably — execution halted",
                        failedStep = ex.FailedFilePath,
                        error = ex.Message
                    }, ct);
                }
                return (allSteps, plan ?? new AgentPlan(), false);
            }
        }
        var taskComplete = planCompleteDeclared;
        var verificationDetails = planCompleteDeclared
            ? "Planner declared plan complete — post-execution verification skipped."
            : (string?)null;
        List<string>? verificationIssues = null;
        List<string>? speculativeVerificationIssues = null;

        var anyEditsApplied = allSteps.OfType<Dictionary<string, object?>>().Any(r =>
            r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
            r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created");

        if (anyEditsApplied || planCompleteDeclared)
        {
            (taskComplete, verificationDetails, verificationIssues, speculativeVerificationIssues) =
                 await PostExecuteVerify(prompt, projectRoot, emitSse, allSteps, ct, discoveryContext, atomicStepEstimate, preEditSnapshots);
        }
        else
        {
            taskComplete = false;
            verificationDetails = "No edits were applied — skipping post-execution verification.";
            await EmitLog(emitSse, "warn", verificationDetails, ct: ct);
        }
        if (!taskComplete)
        {
            var stepTruthCompleted = await VerifyCompletedFromStepTruthAsync(allSteps, projectRoot, ct);
            if (stepTruthCompleted && verificationIssues != null && verificationIssues.Count == 0)
            {
                await EmitLog(emitSse, "warn",
                    $"Post-execution verification says task is incomplete despite all steps having status 'done', " +
                    $"but gave no specific issues. Verifier details: {verificationDetails}. Re-running verifier...", ct: ct);
                var (reverifyComplete, reverifyDetails, reverifyIssues, reverifySpeculative) =
                    await PostExecuteVerify(prompt, projectRoot, emitSse, allSteps, ct, discoveryContext, atomicStepEstimate, preEditSnapshots);
                if (reverifyComplete)
                {
                    await EmitLog(emitSse, "info", "Re-verification passed — trusting verifier on retry.", ct: ct);
                    taskComplete = true;
                    verificationDetails = reverifyDetails;
                }
                else if (reverifyIssues.Count == 0)
                {
                    await EmitLog(emitSse, "warn",
                        $"Re-verification still vague with no CONFIRMED issues — trusting step truth. ({reverifyDetails})", ct: ct);
                    taskComplete = true;
                    verificationDetails = reverifyDetails + " (overridden: all steps completed, verifier gave no confirmed actionable issues)";
                }
                else
                {
                    verificationDetails = reverifyDetails;
                    verificationIssues = reverifyIssues;
                    speculativeVerificationIssues = reverifySpeculative;
                }
            }
        }
        if (taskComplete)
        {
            allSteps.Add(new Dictionary<string, object?>
            {
                ["type"] = "verified_complete",
                ["status"] = "done",
                ["reason"] = verificationDetails
            });
        }
        else
        {
            var needsExtraStepResults = allSteps.OfType<Dictionary<string, object?>>()
                .Where(s => s.ContainsKey("needsExtraStep"))
                .Select(s => s["needsExtraStep"])
                .ToList();
            if (needsExtraStepResults.Count > 0 && needsExtraStepResults.All(v => v is false) && verificationIssues != null && verificationIssues.Count == 0)
            {
                var stepCount = needsExtraStepResults.Count;
                await EmitLog(emitSse, "info",
                    $"All {stepCount} step(s) had needsExtraStep=false (step-level verifier confirmed completion) and " +
                    $"post-execution verifier gave no CONFIRMED issues — overriding rejection. Details: {verificationDetails}", ct: ct);
                taskComplete = true;
                allSteps.Add(new Dictionary<string, object?>
                {
                    ["type"] = "verified_complete",
                    ["status"] = "done",
                    ["reason"] = $"Step-level verifier confirmed completion (needsExtraStep=false on all {stepCount} step(s)). {verificationDetails}"
                });
            }
        }
        if (!taskComplete)
        {
            const int MaxPostVerifyRepairIterations = 3;
            const int MaxZeroChangeRepairs = 2; // consecutive repair passes that change NO files trip the churn circuit breaker
            var repairIteration = 0;
            var exhaustedWithNoSteps = false;
            var partialEditFeedback = new StringBuilder();
            var zeroChangeRepairs = 0;
            while (!taskComplete && repairIteration < MaxPostVerifyRepairIterations)
            {
                repairIteration++;
                await EmitLog(emitSse, "warn",
                    $"Post-execution verification incomplete (repair pass {repairIteration}/{MaxPostVerifyRepairIterations}): " +
                    $"{verificationDetails}", ct: ct);
                if (speculativeVerificationIssues is { Count: > 0 })
                {
                    await EmitLog(emitSse, "bypass",
                        $"🔎 {speculativeVerificationIssues.Count} speculative verifier concern(s) excluded from repair (logged only): " +
                        $"{string.Join("; ", speculativeVerificationIssues)}", ct: ct);
                }
                var allFailures = allSteps.OfType<Dictionary<string, object?>>()
                    .Where(s => s.GetValueOrDefault("status")?.ToString() is "error" or "verify-abandoned")
                    .ToList();
                var failureContextForReplan = new StringBuilder();
                foreach (var f in allFailures)
                {
                    var path = f.GetValueOrDefault("path")?.ToString() ?? "?";
                    var reason = f.GetValueOrDefault("reason")?.ToString() ?? f.GetValueOrDefault("error")?.ToString() ?? "";
                    var bestScore = f.GetValueOrDefault("bestScore");
                    var failureCtx = f.GetValueOrDefault("failureContext")?.ToString();
                    failureContextForReplan.AppendLine($"FAILED: {path} — {reason}");
                    if (bestScore != null)
                        failureContextForReplan.AppendLine($"  Best score: {bestScore}/100");
                    if (failureCtx != null)
                        failureContextForReplan.AppendLine($"  Context: {TruncateForLlm(failureCtx, 1000)}");
                    failureContextForReplan.AppendLine();
                }
                // TRIAGE: validate each CONFIRMED issue against the actual file contents before
                // feeding it to the replanner. Phantom claims (symbol present despite 'missing'),
                // event-gated initialization concerns, and 'might/could' wording are dropped with
                // a log entry instead of forcing a repair.
                if (verificationIssues is { Count: > 0 })
                {
                    var triageFiles = LoadFilesForTriage(projectRoot, allSteps, prompt);
                    var keptIssues = new List<string>();
                    foreach (var issue in verificationIssues)
                    {
                        var (keep, reason) = VerifierIssueTriage.TriageVerifierIssue(issue, triageFiles);
                        if (keep)
                        {
                            keptIssues.Add(issue);
                        }
                        else
                        {
                            await EmitLog(emitSse, "bypass",
                                $"🛡️ Verifier issue triaged out (not fed to replanner): \"{TruncateForLlm(issue, 200)}\" — {reason}", ct: ct);
                        }
                    }
                    verificationIssues = keptIssues;
                    if (verificationIssues.Count == 0)
                    {
                        await EmitLog(emitSse, "info",
                            "All verifier issues were triaged out as speculative/phantom — treating verification as complete (changes kept).", ct: ct);
                        // Do NOT add a verified_complete entry here — the post-loop
                        // `if (taskComplete)` block records the single entry with this reason.
                        verificationDetails += " — all verifier issues triaged as non-actionable (speculative/phantom)";
                        taskComplete = true;
                        break;
                    }
                }
                var qualityCheckReason = new StringBuilder();
                if (verificationIssues != null && verificationIssues.Count > 0)
                {
                    qualityCheckReason.AppendLine($"NEXT ISSUE TO FIX (address ONLY this one): {verificationIssues[0]}");
                    if (verificationIssues.Count > 1)
                    {
                        qualityCheckReason.AppendLine();
                        qualityCheckReason.AppendLine("Other known issues — do NOT address these yet, one atomic step at a time:");
                        foreach (var later in verificationIssues.Skip(1))
                            qualityCheckReason.AppendLine($"  - {later}");
                    }
                }
                else
                {
                    qualityCheckReason.AppendLine(verificationDetails);
                }
                if (partialEditFeedback.Length > 0)
                {
                    qualityCheckReason.AppendLine();
                    qualityCheckReason.AppendLine("## PREVIOUS REPAIR STEPS REJECTED AS PARTIAL EDITS — do not repeat these ##");
                    qualityCheckReason.Append(partialEditFeedback);
                }
                var enhancedSteering = (steeringContext ?? "") +
                    "\n\n## PRIOR FAILURES — avoid repeating these approaches ##\n" +
                    failureContextForReplan.ToString();
                var replanSteps = await GenerateReplanStepsAsync(prompt, allSteps, plan,
                    enhancedSteering, projectRoot, emitSse, ct,
                    attachedFiles: attachedFiles,
                    qualityCheckReason: qualityCheckReason.ToString());
                if (replanSteps == null || replanSteps.Count == 0)
                {
                    bool hasVerificationIssues = (verificationIssues != null && verificationIssues.Count > 0 && !string.IsNullOrEmpty(verificationIssues[0]));
                    await EmitLog(emitSse, "warn",
                        $"Repair pass {repairIteration}: replanner returned no steps for issue " +
                        $"\"{(hasVerificationIssues ? verificationIssues![0] : verificationDetails)}\" — stopping repair loop.", ct: ct);
                    exhaustedWithNoSteps = true;
                    break;
                }
                var singleStep = replanSteps[0];
                // PARTIAL-EDIT GATE: verify the repair step's concrete edit actually implements
                // what its Change description claims. An edit that touches far less than the
                // description (e.g. adding an unused AfterViewInit import while describing a full
                // lifecycle implementation) is rejected and retried with feedback instead of
                // being accepted.
                var (isPartialEdit, partialEditReason) = DetectPartialEdit(singleStep);
                if (isPartialEdit)
                {
                    if (repairIteration >= MaxPostVerifyRepairIterations)
                    {
                        // Final repair pass: a false-positive here would hard-fail the card, which
                        // is worse than the status quo (accept + let PostExecuteVerify judge).
                        // Accept with a visible warning and let the re-verify below decide.
                        await EmitLog(emitSse, "warn",
                            $"⚠️ Final repair pass still produced a PARTIAL EDIT — accepting with warning, " +
                            $"letting verification judge: {partialEditReason}. Change: \"{TruncateForLlm(singleStep.Change, 200)}\"", ct: ct);
                    }
                    else
                    {
                        await EmitLog(emitSse, "warn",
                            $"✂️ Repair step rejected as PARTIAL EDIT — {partialEditReason}. " +
                            $"Change: \"{TruncateForLlm(singleStep.Change, 200)}\". Retrying the repair with feedback.", ct: ct);
                        partialEditFeedback.AppendLine(
                            $"- Rejected partial edit \"{TruncateForLlm(singleStep.Change, 160)}\": {partialEditReason}. " +
                            "Produce a concrete oldString/newString that fully implements the described change (a single import/one-line edit is not enough).");
                        continue;
                    }
                }
                var originalStepCount = plan?.Plan?.Count ?? 0;
                plan = MergePlans(plan ?? new AgentPlan(),
                    new AgentPlan { Plan = new List<PlanStep> { singleStep }, Summary = "Repair: " + singleStep.Change, Score = 0 });
                if (plan?.Plan?.Count > 0)
                    plan.Plan = await PruneIrrelevantPlanStepsAsync(plan.Plan, projectRoot, ct);
                if (emitSse && plan != null)
                    await SendSse(Response, "plan",
                        new { thinking = plan.Thinking, summary = "Repair: " + singleStep.Change, items = plan.Plan }, ct);
                if (plan != null)
                    await PersistBoardDataPlanAsync(cardId, plan.Plan, emitSse, ct,
                        summary: plan.Summary ?? ("Repair: " + singleStep.Change), score: plan.Score, append: false);
                var mergedDone = new HashSet<int>();
                for (var i = 0; i < originalStepCount && i < (plan?.Plan?.Count ?? 0); i++)
                    mergedDone.Add(i);
                var successfulEditsBefore = CountSuccessfulEditResults(allSteps);
                if (plan != null)
                {
                    await ExecutePlan(prompt, projectRoot, emitSse, "", plan, ct, allSteps,
                        steeringContext: enhancedSteering, attachedFiles: attachedFiles,
                        completedStepIndices: mergedDone, cardId: cardId);
                }
                // If repair step was "already done", the verifier issue was a phantom —
                // remove it and skip re-verify so the next pass tries the next issue.
                var (isPhantomIssue, phantomText, remainingIssues) =
                    VerifierIssueTriage.TrySkipPhantomIssue(allSteps, verificationIssues);
                if (isPhantomIssue)
                {
                    verificationIssues = remainingIssues;
                    await EmitLog(emitSse, "info",
                        $"Repair step was already done — issue \"{phantomText}\" was a phantom. " +
                        $"Remaining issues: {verificationIssues.Count}", ct: ct);
                    if (verificationIssues.Count == 0)
                    {
                        taskComplete = true;
                        break;
                    }
                    continue;
                }
                // CHURN CIRCUIT BREAKER: the repair executed but changed NO files and the
                // verifier issue survived — consecutive passes like this mean the verifier
                // keeps flagging something no edit can fix (a false positive like the
                // template-binding churn). Stop repairing instead of burning LLM calls on
                // garbage steps; the changes already made are kept and the task finishes.
                var successfulEditsAfter = CountSuccessfulEditResults(allSteps);
                var (newZeroChangeCount, breakerTripped) = AdvanceRepairChurnBreaker(
                    zeroChangeRepairs, successfulEditsAfter != successfulEditsBefore, MaxZeroChangeRepairs);
                zeroChangeRepairs = newZeroChangeCount;
                if (breakerTripped)
                {
                    await EmitLog(emitSse, "warn",
                        $"⛔ Repair circuit breaker tripped — {zeroChangeRepairs} consecutive repair passes produced zero file changes. " +
                        $"The verifier issue is likely a false positive; stopping the repair loop and keeping changes. " +
                        $"Remaining issues: {string.Join("; ", verificationIssues ?? [])}", ct: ct);
                    verificationDetails += $" — repair circuit breaker tripped after {zeroChangeRepairs} consecutive zero-change passes " +
                        $"(verifier issue likely non-actionable); changes kept";
                    taskComplete = true;
                    break;
                }
                if (zeroChangeRepairs > 0)
                {
                    await EmitLog(emitSse, "warn",
                        $"Repair pass {repairIteration}: repair changed NO files ({zeroChangeRepairs}/{MaxZeroChangeRepairs} consecutive) — " +
                        $"verifier issue likely false-positive; next no-change pass trips the circuit breaker.", ct: ct);
                }
                var (reVerified, reDetails, reIssues, reSpeculative) =
                    await PostExecuteVerify(prompt, projectRoot, emitSse, allSteps, ct, discoveryContext, atomicStepEstimate, preEditSnapshots);
                taskComplete = reVerified;
                verificationDetails = reDetails;
                verificationIssues = reIssues;
                speculativeVerificationIssues = reSpeculative;
                if (taskComplete)
                    await EmitLog(emitSse, "success", $"Repair pass {repairIteration}: verification now complete.", ct: ct);
            }
            if (taskComplete)
            {
                allSteps.Add(new Dictionary<string, object?>
                {
                    ["type"] = "verified_complete",
                    ["status"] = "done",
                    ["reason"] = verificationDetails
                });
            }
            else if (exhaustedWithNoSteps)
            {
                await EmitLog(emitSse, "info",
                    "Repair replanner proposed no further steps — treating verification as complete (nothing left to fix). " +
                    "Changes are kept and the task finishes; no fresh plan will be generated.", ct: ct);
                taskComplete = true;
                allSteps.Add(new Dictionary<string, object?>
                {
                    ["type"] = "verified_complete",
                    ["status"] = "done",
                    ["reason"] = verificationDetails + " — replanner proposed no further steps, treating as complete"
                });
            }
            else
            {
                await EmitLog(emitSse, "warn",
                    $"Repair budget exhausted ({MaxPostVerifyRepairIterations} passes) — stopping with remaining issues: " +
                    $"{string.Join("; ", verificationIssues ?? [])}", ct: ct);
            }
        }
        return (allSteps, plan ?? new AgentPlan(), taskComplete);
    }
    private async Task<Dictionary<string, string>> AskUserAsync(string question, List<QuestionField>? fields = null, CancellationToken ct = default, Object? additionalData = null)
    {
        var qId = Guid.NewGuid().ToString();
        var pending = new PendingQuestion
        {
            Id = qId,
            Question = question,
            Fields = fields ?? new List<QuestionField>(),
            CreatedUtc = DateTime.UtcNow,
            Answer = new TaskCompletionSource<Dictionary<string, string>>()
        };
        _pendingQuestions[qId] = pending;
        await SendSse(Response, "ask-question", new
        {
            id = qId,
            question = pending.Question,
            fields = pending.Fields.Select(f => new { f.Key, f.Label, f.Type, f.DefaultValue }).ToList(),
            additionalData
        }, ct);
        try
        {
            var answers = await pending.Answer.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            return answers;
        }
        catch (TimeoutException) { return new Dictionary<string, string>(); }
        catch (OperationCanceledException) { return new Dictionary<string, string>(); }
        finally { _pendingQuestions.TryRemove(qId, out _); }
    }
    /// <summary>
    /// Builds a per-file breakdown of the discovery context for the agent-panel token
    /// counter: each discovery read step contributes its file size (chars + estimated
    /// tokens), and everything else (headers, skeleton note, edit-knowledge header,
    /// web results, steering) is rolled up as "scaffolding". Lets users see WHY the
    /// counter is N tokens instead of guessing — e.g. two 31k-token attached files
    /// showing as ~130k because the old counter sent character counts labeled tokens.
    /// </summary>
    private static List<object> BuildContextBreakdown(List<object> ds, string discoveryContext)
    {
        var rows = new List<object>();
        var accountedChars = 0;
        var accountedTokens = 0;
        foreach (var item in ds.OfType<Dictionary<string, object?>>())
        {
            if (item.GetValueOrDefault("type")?.ToString() != "read") continue;
            var path = item.GetValueOrDefault("path")?.ToString();
            var output = item.GetValueOrDefault("output")?.ToString();
            if (string.IsNullOrEmpty(path) || output == null) continue;
            var fileTokens = AgentTokenMetrics.EstimateTokens(output);
            rows.Add(new
            {
                name = path,
                kind = "file",
                chars = output.Length,
                tokens = fileTokens
            });
            accountedChars += output.Length;
            accountedTokens += fileTokens;
        }
        var scaffoldingChars = Math.Max(0, discoveryContext.Length - accountedChars);
        if (scaffoldingChars > 0)
        {
            rows.Add(new
            {
                name = "headers / skeleton / steering",
                kind = "scaffolding",
                chars = scaffoldingChars,
                // Same estimator as the file rows — derived as the residual so the rows
                // always sum to the reported contextSize.
                tokens = Math.Max(0, AgentTokenMetrics.EstimateTokens(discoveryContext) - accountedTokens)
            });
        }
        return rows;
    }
    private async Task<string> RunContextReview(
        List<object> ds, string discoveryContext, List<object> allSteps, CancellationToken ct)
    {
        var readFiles = ds.OfType<Dictionary<string, object?>>()
            .Where(s => s.TryGetValue("type", out var t) && t?.ToString() == "read")
            .Select(s => s.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (readFiles.Count == 0) return discoveryContext;
        var reviewId = Guid.NewGuid().ToString();
        var review = new PendingContextReview
        {
            Id = reviewId,
            Files = readFiles.Where(f => f != null).ToList()!,
            CreatedUtc = DateTime.UtcNow,
            Answer = new TaskCompletionSource<List<string>>()
        };
        _pendingContextReviews[reviewId] = review;
        await SendSse(Response, "context-review", new
        {
            id = reviewId,
            files = readFiles.Select(f => new { path = f }).ToList(),
            contextSize = AgentTokenMetrics.EstimateTokens(discoveryContext),
            contextChars = discoveryContext.Length,
            contextBreakdown = BuildContextBreakdown(ds, discoveryContext)
        }, ct);
        try
        {
            var confirmedFiles = await review.Answer.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var confirmedSet = new HashSet<string>(confirmedFiles, StringComparer.OrdinalIgnoreCase);
            if (confirmedFiles.Count < readFiles.Count)
            {
                var filtered = ds.Where(item =>
                {
                    if (item is not Dictionary<string, object?> r) return true;
                    var type = r.TryGetValue("type", out var t) ? t?.ToString() : "";
                    if (type != "read") return true;
                    var p = r.GetValueOrDefault("path")?.ToString();
                    return !string.IsNullOrWhiteSpace(p) && confirmedSet.Contains(p);
                }).ToList();
                allSteps.Clear(); allSteps.AddRange(filtered);
                return AgentDiscovery.BuildDiscoveryTextFromSteps(filtered);
            }
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        finally { _pendingContextReviews.TryRemove(reviewId, out _); }
        return discoveryContext;
    }
    private async Task<string> ExplorationPipeline(
        List<PlanStep> exploreSteps, string discoveryContext,
        string projectRoot, bool emitSse, CancellationToken ct, string prompt = "")
    {
        var enriched = new StringBuilder(discoveryContext);
        enriched.AppendLine();
        // A symbol-targeted _explore of a specific file gets the same focused-read rule
        // as the discovery auto-read: a large file with a prompt identifier matched
        // INSIDE contributes only the enclosing method/class/block around each match,
        // so the model sees the region it asked about, not an unrelated 50KB component.
        var identifierTokens = string.IsNullOrWhiteSpace(prompt)
            ? new List<string>()
            : AgentDiscovery.ExtractIdentifierTokens(prompt);
        var focusedCount = 0;
        var focusedCharsSaved = 0L;
        foreach (var step in exploreSteps)
        {
            var target = step.Change?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(target)) continue;
            await EmitLog(emitSse, "info", $"Exploring: {target}", ct: ct);
            if (target.Contains('*') || target.Contains('?'))
            {
                var sep = Path.DirectorySeparatorChar;
                var pattern = target.Replace('/', sep);
                var dir = Path.GetDirectoryName(pattern) ?? ".";
                var searchDir = Path.GetFullPath(Path.Combine(projectRoot, dir));
                if (!Directory.Exists(searchDir)) continue;
                foreach (var match in Directory.EnumerateFiles(searchDir, Path.GetFileName(pattern), SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).Take(10))
                {
                    var fp = Path.GetFullPath(Path.Combine(projectRoot, match.Replace('/', sep)));
                    if (!System.IO.File.Exists(fp)) continue;
                    var content = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                    enriched.AppendLine($"### {match}\n```\n{content}\n```\n");
                }
            }
            else
            {
                var fp = Path.GetFullPath(Path.Combine(projectRoot, target.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(fp) && AgentProjectUtilities.IsPathUnderRoot(fp, projectRoot))
                {
                    var content = await System.IO.File.ReadAllTextAsync(fp, Encoding.UTF8, ct);
                    var (snippet, focusIds) = AgentDiscovery.FocusLargeFileRead(content, identifierTokens, target);
                    if (focusIds != null)
                    {
                        focusedCount++;
                        focusedCharsSaved += content.Length - snippet.Length;
                        await EmitLog(emitSse, "info",
                            $"Exploring {target}: matched identifier(s) \"{focusIds}\" — reading focused regions instead of the full file", ct: ct);
                    }
                    enriched.AppendLine($"### {target}" + (focusIds != null ? $" (focused: {focusIds}; full file via _explore)" : ""));
                    enriched.AppendLine("```");
                    enriched.AppendLine(snippet);
                    enriched.AppendLine("```");
                    enriched.AppendLine();
                }
            }
        }
        if (focusedCount > 0)
        {
            await EmitLog(emitSse, "metric",
                $"🎯 Explore focus: {focusedCount} file(s) read as focused regions, saved ~{focusedCharsSaved:N0} chars",
                new { kind = "focusStats", filesFocused = focusedCount, charsSaved = focusedCharsSaved },
                ct);
        }
        return enriched.ToString();
    }
    /// <summary>
    /// Splits the verifier response's 'issues' array into CONFIRMED (actionable — used to drive repair steps)
    /// and SPECULATIVE (hypothetical risks — logged but never repaired) buckets. Tolerates both
    /// object items ({type, text}) and legacy plain-string items (treated as CONFIRMED for
    /// backward compatibility with older verifier output). Accepts the verifier JSON root object
    /// and reads its 'issues' property.
    /// </summary>
    private static (List<string> confirmed, List<string> speculative) ParseVerifyIssues(JsonElement rootEl)
    {
        var confirmed = new List<string>();
        var speculative = new List<string>();
        if (rootEl.ValueKind != JsonValueKind.Object) return (confirmed, speculative);
        if (!rootEl.TryGetProperty("issues", out var issuesEl) || issuesEl.ValueKind != JsonValueKind.Array)
            return (confirmed, speculative);
        foreach (var issueEl in issuesEl.EnumerateArray())
        {
            if (issueEl.ValueKind == JsonValueKind.Object)
            {
                var type = issueEl.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
                var text = issueEl.TryGetProperty("text", out var xEl) ? xEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) text = issueEl.TryGetProperty("issue", out var iEl) ? iEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (string.Equals(type, "SPECULATIVE", StringComparison.OrdinalIgnoreCase))
                    speculative.Add(text);
                else
                    confirmed.Add(text);
            }
            else
            {
                var text = issueEl.GetString();
                if (!string.IsNullOrWhiteSpace(text)) confirmed.Add(text);
            }
        }
        return (confirmed, speculative);
    }

    /// <summary>Claim words that assert a concrete missing/undefined defect (as opposed to a risk).</summary>
    /// <summary>High-confidence symbol anchors extracted from a change description: backticked
    /// identifiers, vm./this.-qualified names, #template refs, and method calls (foo().</summary>
    private static readonly Regex[] PartialEditAnchorPatterns =
    {
        new(@"`([A-Za-z_$][\w$]*)`"),
        new(@"\b(?:vm|this)\s*\.\s*([A-Za-z_$][\w$]*)"),
        new(@"#([A-Za-z_$][\w$]*)"),
        new(@"\b([A-Za-z_$][\w$]*)\s*\(")
    };

    /// <summary>Control-flow / generic call words that a method-call anchor regex must not
    /// treat as claimed symbols (e.g. 'if (', 'for (', 'function (').</summary>
    private static readonly HashSet<string> PartialEditAnchorStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "while", "switch", "function", "return", "catch", "throw", "with",
        "call", "called", "calls", "calling", "add", "remove", "use", "using", "check", "see", "click"
    };

    /// <summary>
    /// Deterministic consistency gate for REPAIR steps: compares the step's concrete
    /// oldString/newString (or newCode) edit payload against its Change description and flags
    /// PARTIAL edits — where the LLM delivered far less than it described (e.g. claiming to
    /// "initialize a ViewChild with an AfterViewInit lifecycle hook and check for existence"
    /// but only adding an unused AfterViewInit import). Returns (isPartial, reason).
    /// Never flags deletions (empty payload) or resolution-driven steps.
    /// </summary>
    private static (bool isPartial, string reason) DetectPartialEdit(PlanStep? step)
    {
        if (step == null) return (false, "");
        var change = step.Change ?? "";
        if (string.IsNullOrWhiteSpace(change)) return (false, "");

        var payload = step.NewString ?? "";
        if (string.IsNullOrWhiteSpace(payload) && step.NewCode is { Count: > 0 })
            payload = string.Join("\n", step.NewCode);
        if (string.IsNullOrWhiteSpace(payload)) return (false, ""); // deletion / no concrete payload

        // Rule A compares claimed symbols against the FULL touched text (old context + new
        // payload): a refactor that changes a method's body without re-uttering the symbol
        // name (e.g. 'render()' body rewrite) must NOT be flagged, and a claimed symbol that
        // legitimately lives in the untouched old context is not 'missing'.
        var touchedText = payload;
        if (!string.IsNullOrWhiteSpace(step.OldString))
            touchedText = step.OldString + "\n" + payload;

        var changeLower = change.ToLowerInvariant();

        // Rule A — claimed symbol anchors missing from the edit's touched text.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in PartialEditAnchorPatterns)
            foreach (Match m in pattern.Matches(change))
                if (!PartialEditAnchorStopwords.Contains(m.Groups[1].Value))
                    claimed.Add(m.Groups[1].Value);
        if (claimed.Count > 0)
        {
            var missing = claimed.Where(s =>
                !Regex.IsMatch(touchedText, @"\b" + Regex.Escape(s) + @"\b", RegexOptions.IgnoreCase)).ToList();
            var allMissing = missing.Count == claimed.Count;
            var majorityMissing = claimed.Count >= 3 && missing.Count * 2 > claimed.Count; // strict majority
            if (allMissing)
                return (true, $"edit implements NONE of the claimed symbol(s): {string.Join(", ", missing)}");
            if (majorityMissing)
                return (true, $"edit omits most claimed symbol(s): {string.Join(", ", missing)}");
        }

        // Rule B — structural: the description claims a SUBSTANTIVE implementation (a lifecycle
        // hook / ngAfterViewInit / a named method-function-handler being created, or an explicit
        // 'check for existence before calling X') but the payload is a single trivial line with
        // no method body and no guard. Trigger words are deliberately STRONG (not bare
        // 'initialize'/'guard'/'check') and the description must be reasonably long, so genuinely
        // small but complete edits (e.g. 'this.counter = 0;') are never flagged.
        var descriptionWordCount = change.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var claimsImplementation =
            Regex.IsMatch(changeLower, @"\b(ngafterviewinit|ngoninit|ngaftercontentinit|ngafterviewchecked|lifecycle hook|implements\b.*\b(?:afterviewinit|oninit))\b") ||
            Regex.IsMatch(changeLower, @"\b(create|add|write|implement)\b.{0,40}\b(method|function|handler|hook)\b") ||
            Regex.IsMatch(changeLower, @"\bcheck for existence\b.{0,40}\b(before calling|before using|guard)\b");
        var isImportTask = changeLower.Contains("import");
        var payloadIsImportLine = Regex.IsMatch(payload.Trim(), @"^import\b");
        var payloadTrivial = !payload.Contains('{') && !payload.Contains("if (") &&
                             payload.Trim().IndexOf('\n') < 0 && payload.Trim().Length <= 200;
        if (descriptionWordCount >= 6 && claimsImplementation && payloadTrivial && !(isImportTask && payloadIsImportLine))
            return (true, "description claims implementation work but the edit is a single trivial line (no method body / guard)");

        return (false, "");
    }

    /// <summary>
    /// Builds a bounded, fresh-on-disk listing of the project root: top-level directories and
    /// files, plus the contents of any directory created during the run (from create results).
    /// The verifier prompt needs this because the discovery context is captured at run start and
    /// does not reflect directories/files created while executing.
    /// </summary>
    private static string BuildCurrentStructureListing(string projectRoot, IEnumerable<object> allResults)
    {
        var sb = new StringBuilder();
        var noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", ".git", "bin", "obj", "dist", ".vs", ".vscode", ".idea",
            "packages", "coverage", "__pycache__", ".next", ".nuget", ".gitignore"
        };
        try
        {
            var topDirs = Directory.GetDirectories(projectRoot)
                .Select(Path.GetFileName)
                .Where(n => n != null && !noise.Contains(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var d in topDirs) sb.AppendLine(d + "/");
            var topFiles = Directory.GetFiles(projectRoot)
                .Select(Path.GetFileName)
                .Where(n => n != null && !n.StartsWith('.'))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var f in topFiles) sb.AppendLine(f);
            // Contents of directories created during this run (extensionless create-result paths
            // are directories; file results carry a sub-path or extension).
            var createdDirs = new List<string>();
            foreach (var r in allResults.OfType<Dictionary<string, object?>>())
            {
                if (r.GetValueOrDefault("type")?.ToString() == "create" &&
                    r.GetValueOrDefault("status")?.ToString() is "done" or "created" &&
                    r.GetValueOrDefault("path") is string p && !string.IsNullOrWhiteSpace(p))
                {
                    var rel = p.Replace('\\', '/').Trim('/');
                    if (rel.Length > 0 && !rel.Contains('/') && !Path.HasExtension(rel))
                        createdDirs.Add(rel);
                }
            }
            var createdDirList = createdDirs.Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
            if (createdDirs.Count > createdDirList.Count)
                sb.AppendLine("… (further created directories not shown)");
            foreach (var dir in createdDirList)
            {
                var full = Path.GetFullPath(Path.Combine(projectRoot, dir.Replace('/', Path.DirectorySeparatorChar)));
                if (!Directory.Exists(full)) continue;
                sb.AppendLine();
                sb.AppendLine($"[contents of {dir}/]");
                var children = Directory.EnumerateFileSystemEntries(full).Take(40).ToList();
                foreach (var child in children)
                {
                    var name = Path.GetFileName(child);
                    sb.AppendLine("  " + (Directory.Exists(child) ? name + "/" : name));
                }
                if (Directory.EnumerateFileSystemEntries(full).Skip(40).Any())
                    sb.AppendLine("  … (more entries not shown)");
            }
        }
        catch { }
        return sb.ToString();
    }

    /// <summary>Loads the current on-disk contents of all files touched by the run plus files
    /// referenced by the prompt, for verifier-issue triage.</summary>
    private static Dictionary<string, string> LoadFilesForTriage(
        string projectRoot, IEnumerable<object> allSteps, string prompt)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in allSteps.OfType<Dictionary<string, object?>>())
        {
            var p = s.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p)) paths.Add(p.Replace('\\', '/'));
        }
        foreach (Match m in Regex.Matches(prompt, @"[\w/]+\.(html|css|ts|tsx|js|jsx|scss|less|cs)\b", RegexOptions.IgnoreCase))
            paths.Add(m.Value.Replace('\\', '/'));
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in paths)
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(full))
                    files[rel] = System.IO.File.ReadAllText(full);
            }
            catch { }
        }
        return files;
    }

    /// <summary>Counts results in a step/result collection that represent files actually
    /// modified or created this run — used by the repair-loop circuit breaker to detect
    /// passes that changed nothing (verifier churn).</summary>
    private static int CountSuccessfulEditResults(List<object> results) => results
        .OfType<Dictionary<string, object?>>()
        .Count(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                    r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created");

    /// <summary>
    /// Repair-loop churn circuit breaker: after `maxZeroChangeRepairs` CONSECUTIVE repair
    /// passes that changed no files, the breaker trips so a false-positive verifier issue
    /// (e.g. the template-binding churn) cannot keep spawning garbage repair steps. Any pass
    /// that actually changed a file resets the counter. Returns the new count and whether
    /// the breaker tripped.
    /// </summary>
    public static (int count, bool tripped) AdvanceRepairChurnBreaker(
        int zeroChangeRepairs, bool changedFiles, int maxZeroChangeRepairs)
    {
        if (changedFiles) return (0, false);
        var next = zeroChangeRepairs + 1;
        return next >= maxZeroChangeRepairs ? (next, true) : (next, false);
    }

    /// <summary>Snapshots the pre-edit on-disk content of plan-targeted files (HTML templates)
    /// so post-execution template-binding validation only flags bindings INTRODUCED by the edit.</summary>
    private static Dictionary<string, string> SnapshotPreEditFiles(string projectRoot, AgentPlan? plan, Dictionary<string, string>? existing = null)
    {
        var snap = existing ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (plan?.Plan == null) return snap;
        foreach (var step in plan.Plan)
        {
            var file = step.File;
            if (string.IsNullOrWhiteSpace(file)) continue;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".html" && ext != ".htm") continue;
            var rel = file.Replace('\\', '/');
            if (snap.ContainsKey(rel)) continue;
            try
            {
                var full = Path.GetFullPath(Path.Combine(projectRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (System.IO.File.Exists(full))
                    snap[rel] = System.IO.File.ReadAllText(full);
            }
            catch { }
        }
        return snap;
    }

    private async Task<(bool complete, string details, List<string> confirmedIssues, List<string> speculativeIssues)> PostExecuteVerify(
        string originalPrompt, string projectRoot, bool emitSse,
        List<object> allResults, CancellationToken ct,
        string? discoveryContext = null, int? atomicStepEstimate = null,
        Dictionary<string, string>? preEditSnapshots = null)
    {
        var modifiedPaths = allResults
            .OfType<Dictionary<string, object?>>()
            // Include created files too — a _create_file result carries type "create" and its
            // content must be shown in CURRENT STATE for the verifier to judge it.
            .Where(r => r.TryGetValue("type", out var t) && t?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .Select(r => r.GetValueOrDefault("path")?.ToString())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (modifiedPaths.Count == 0)
        {
            var exploredPaths = allResults
                .OfType<Dictionary<string, object?>>()
                .Where(r => r.TryGetValue("type", out var t) && t?.ToString() == "read")
                .Select(r => r.GetValueOrDefault("path")?.ToString())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (exploredPaths.Count == 0)
            {
                return (true, "", new List<string>(), new List<string>());
            }
            modifiedPaths = exploredPaths;
        }
        // Deterministic template-binding validation (independent of the LLM verifier): an
        // edited/new template must reference symbols the sibling component actually exposes,
        // and component logic wired under a UI task whose template was in scope but never
        // edited is flagged as unrendered. These checks are regex-based and cannot
        // hallucinate, so their findings are always CONFIRMED when they fire.
        // preEditSnapshots (captured BEFORE edits) ensures only bindings INTRODUCED by the
        // edit are validated — pre-existing missing bindings are not attributed to the agent.
        var bindingIssues = TemplateBindingValidator.CheckModifiedTemplates(projectRoot, modifiedPaths, preEditSnapshots);
        var unrenderedIssues = TemplateBindingValidator.CheckUnrenderedComponentLogic(
            originalPrompt, projectRoot, modifiedPaths, allResults);
        var deterministicIssues = new List<string>();
        deterministicIssues.AddRange(bindingIssues);
        deterministicIssues.AddRange(unrenderedIssues);
        if (deterministicIssues.Count > 0)
        {
            await EmitLog(emitSse, "warn",
                $"🔧 Deterministic template-binding check: {deterministicIssues.Count} CONFIRMED issue(s): {string.Join("; ", deterministicIssues)}", ct: ct);
        }
        var sb = new StringBuilder();
        sb.AppendLine("### ORIGINAL TASK ###");
        sb.AppendLine(originalPrompt);
        sb.AppendLine();
        var doneEdits = allResults
            .OfType<Dictionary<string, object?>>()
            .Where(r => r.TryGetValue("type", out var t) && t?.ToString() == "edit" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created").ToList();
        if (atomicStepEstimate is > 0)
        {
            sb.AppendLine("### STEP BUDGET ###");
            sb.AppendLine($"The planner estimated this task needs ~{atomicStepEstimate} atomic step(s); " +
                $"{doneEdits.Count} edit step(s) were executed. Classify every issue CONFIRMED vs SPECULATIVE " +
                $"strictly against the ORIGINAL TASK — do NOT invent follow-up work, refactors, or best-practice " +
                $"improvements the user never asked for, and do NOT flag 'might/could/maybe' risks as repairs. " +
                $"If the explicit request is satisfied, complete=true even if you can imagine more.");
            sb.AppendLine();
        }
        var editResults = allResults
            .OfType<Dictionary<string, object?>>()
            .Where(r => r.TryGetValue("type", out var t) && t?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created" &&
                        r.TryGetValue("path", out var p) && p?.ToString() != null)
            .GroupBy(r => r["path"]!.ToString()!)
            .ToList();
        if (editResults.Count > 0)
        {
            sb.AppendLine("### FILES CHANGED ###");
            sb.AppendLine(string.Join(", ", editResults.Select(g => g.Key)));
            sb.AppendLine();
            sb.AppendLine("Pre-edit OLD/NEW snippets are intentionally NOT shown: they are historical and go stale the moment a " +
                          "repair pass edits the file (a verifier re-issued the same defect after the fix landed because it " +
                          "trusted the pre-edit snippet). ");
            sb.AppendLine("The CURRENT STATE OF MODIFIED FILES section below is read fresh from disk AFTER all edits and is the " +
                          "ONLY authoritative view of the code.");
            sb.AppendLine("Never conclude a defect exists because of text from an earlier stage of the run — judge correctness " +
                          "EXCLUSIVELY against CURRENT STATE.");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(discoveryContext))
        {
            sb.AppendLine("### DISCOVERY CONTEXT (explored files) ###");
            sb.AppendLine(TruncateForLlm(discoveryContext, 6000));
            sb.AppendLine();
        }
        sb.AppendLine("### CURRENT STATE OF MODIFIED FILES (AUTHORITATIVE — read from disk after ALL edits) ###");
        var typeFilesToInclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relPath in modifiedPaths)
        {
            var fullPath = Path.GetFullPath(
                Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (System.IO.File.Exists(fullPath))
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                // Cap per-file bodies so a run that creates several large files (new server.py /
                // .cs / .html bodies) can't balloon the verifier prompt. Small files pass through
                // untouched; the marker keeps the truncation explicit so the verifier knows the
                // tail is omitted rather than concluding the file ends there.
                const int MaxFileBodyChars = 12000;
                if (content.Length > MaxFileBodyChars)
                    content = content[..MaxFileBodyChars] +
                        $"\n… [TRUNCATED — file is {content.Length} chars, showing first {MaxFileBodyChars}]";
                sb.AppendLine($"\n### {relPath}");
                sb.AppendLine("```");
                sb.AppendLine(content);
                sb.AppendLine("```");
                var ext = Path.GetExtension(relPath).ToLowerInvariant();
                if (ext is ".ts" or ".tsx")
                {
                    foreach (var importLine in content.Split('\n')
                        .Where(l => l.TrimStart().StartsWith("import ", StringComparison.Ordinal)))
                    {
                        var m = Regex.Match(importLine, @"from\s+['""]([^'""]+)['""]");
                        if (!m.Success) continue;
                        var importPath = m.Groups[1].Value;
                        if (importPath.StartsWith("."))
                        {
                            var baseDir = Path.GetDirectoryName(fullPath) ?? "";
                            var resolved = Path.GetFullPath(Path.Combine(baseDir, importPath));
                            foreach (var suffix in new[] { ".ts", ".tsx", "/index.ts", "/index.tsx" })
                            {
                                var candidate = resolved + suffix;
                                if (System.IO.File.Exists(candidate))
                                {
                                    typeFilesToInclude.Add(candidate);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
        // A fresh, bounded listing of the project root read at VERIFY time — the discovery
        // context above was captured at run start and goes stale the moment a step creates a
        // directory or file. The verifier must never conclude "folder X does not exist" from a
        // pre-run listing (see the benchmark_test_7 case).
        sb.AppendLine();
        sb.AppendLine("### CURRENT PROJECT STRUCTURE (fresh disk listing at verify time) ###");
        sb.AppendLine(BuildCurrentStructureListing(projectRoot, allResults));
        // Include unmodified files referenced in the task so the verifier can check cross-file references
        var taskReferencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(originalPrompt, @"[\w/]+\.(html|css|ts|tsx|js|jsx|scss|less)", RegexOptions.IgnoreCase))
        {
            var candidate = m.Value.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, candidate));
            if (System.IO.File.Exists(fullPath) && !modifiedPaths.Any(mp =>
                string.Equals(mp, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                taskReferencedFiles.Add(fullPath);
            }
        }
        if (taskReferencedFiles.Count > 0)
        {
            sb.AppendLine("\n### TASK-REFERENCED FILES (not modified, shown for context) ###");
            foreach (var fullPath in taskReferencedFiles.Take(3))
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                var rel = Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/');
                sb.AppendLine($"\n### {rel}");
                sb.AppendLine("```");
                sb.AppendLine(content);
                sb.AppendLine("```");
            }
        }
        if (typeFilesToInclude.Count > 0)
        {
            sb.AppendLine("\n### RELATED TYPE DEFINITIONS ###");
            var count = 0;
            foreach (var typeFullPath in typeFilesToInclude.Take(5))
            {
                var content = await System.IO.File.ReadAllTextAsync(typeFullPath, Encoding.UTF8, ct);
                var rel = Path.GetRelativePath(projectRoot, typeFullPath).Replace('\\', '/');
                sb.AppendLine($"\n### {rel}");
                sb.AppendLine("```");
                sb.AppendLine(content);
                sb.AppendLine("```");
                count++;
            }
            if (typeFilesToInclude.Count > count)
                sb.AppendLine($"\n... and {typeFilesToInclude.Count - count} more type files (omitted)");
        }
        sb.AppendLine();
        sb.AppendLine("Based on the original task above and the current state of all modified files and their type definitions,");
        sb.AppendLine("check for ALL of the following:");
        sb.AppendLine("1. Is the original task fully implemented? Evaluate ONLY against what the original task asks for.");
        sb.AppendLine("   Ignore existing code that predates this task — the task may be meant to REPLACE it.");
        sb.AppendLine("   CRITICAL: If the task asks to MOVE code, SEARCH THE ENTIRE FILE for the code in its new location. Do NOT report it as 'missing' or 'not moved' just because it is no longer in its original spot. Verify the actual file content provided above.");
        sb.AppendLine("   CRITICAL: If the requested content is physically present in the file, even if the formatting or nesting is slightly incorrect, the task is COMPLETE. Do NOT report a failure for minor formatting issues.");
        sb.AppendLine("   IMPORTANT: CSS class changes ARE valid modifications for HTML button styling. A task asking to 'make buttons bigger in .html'");
        sb.AppendLine("   is correctly solved by modifying the CSS class (.toolBtn) that those buttons use. Do NOT require inline style attributes");
        sb.AppendLine("   on HTML elements when the styling is already controlled through CSS classes. Modifying the .css file IS sufficient —");
        sb.AppendLine("   the HTML file does NOT need to be edited. CSS-only changes are 100% valid for styling tasks, even when the task mentions the .html file name.");
        sb.AppendLine("   CSS/HTML styling OPINIONS are NOT CONFIRMED defects. Do NOT flag an existing, valid CSS value (e.g. 'justify-content: center',");
        sb.AppendLine("   'gap: 20px') or demand a different property (e.g. 'flex-direction', 'align-items') unless the ORIGINAL TASK explicitly asked to");
        sb.AppendLine("   change it. A styling task is complete once the requested property is present in the stylesheet — it does NOT implicitly require");
        sb.AppendLine("   removing or reworking unrelated pre-existing styles. 'uses X, while requirement is to use Y' / 'should use Z instead' claims are");
        sb.AppendLine("   SPECULATIVE at best and must never set complete=false or drive a repair.");
        sb.AppendLine("   Example: if the task says 'wrap in details/summary' but the file already has per-column");
        sb.AppendLine("   collapse buttons, report that details/summary is missing — do NOT report that");
        sb.AppendLine("   toggleColumnCollapse is unimplemented, because the task has nothing to do with that.");
        sb.AppendLine("2. Do ALL property accesses in the code exist on their respective types/interfaces?");
        sb.AppendLine("3. Are ALL referenced methods, functions, and classes defined or imported?");
        sb.AppendLine("4. Are ALL imports present for every type used?");
        sb.AppendLine("5. Would the code compile without errors?");
        sb.AppendLine();
        sb.AppendLine("Answer with a single JSON object:");
        sb.AppendLine("{ \"complete\": true|false, \"reason\": \"short explanation\", \"issues\": [{\"type\": \"CONFIRMED\" | \"SPECULATIVE\", \"text\": \"issue description\"}] }");
        sb.AppendLine("Set complete=true only if the task is fully implemented AND the code would compile.");
        sb.AppendLine("Set complete=false if anything is missing, broken, or would cause compilation errors.");
        sb.AppendLine("Judge the modified files against their CURRENT STATE content (the authoritative post-edit text); any historical/edit-history text is NOT the current code — ignore it. Other sections are context for cross-file references.");
        sb.AppendLine("Classify EVERY item in the 'issues' array as exactly one of:");
        sb.AppendLine("  - CONFIRMED: the requirement is objectively unmet right now — the code is physically missing/incorrect,");
        sb.AppendLine("    or there is a reproducible defect you can point to in the actual file content above (a missing symbol,");
        sb.AppendLine("    a broken reference, a syntax error). CONFIRMED issues are acted on.");
        sb.AppendLine("  - SPECULATIVE: a hypothetical risk phrased with 'might/could/maybe/possibly' or a general best-practice concern");
        sb.AppendLine("    with no evidence of an actual problem in the code shown (e.g. 'this could be null at runtime').");
        sb.AppendLine("    SPECULATIVE issues are logged for the user but are NEVER used to generate repair steps.");
        sb.AppendLine("Include a brief list of specific issues in the 'issues' array when complete=false; each issue MUST carry");
        sb.AppendLine("a type. If the only concerns you have are speculative, set complete=true and list them as SPECULATIVE so they are not acted on.");
        var verifySystemPrompt = "You are a meticulous code reviewer verifying if a task is fully complete based ONLY on the original task prompt. " +
       "Do NOT invent new requirements or check for things not explicitly mentioned in the task. " +
       "If the original task asked to modify a specific method, and that method was modified, the task is complete. " +
       "Check if the code would compile (no syntax errors, missing brackets, or undefined variables). " +
       "Styling preferences are never CONFIRMED: do not flag an existing CSS value or demand a different property unless the task explicitly asked for it. " +
       "Distinguish CONFIRMED issues (objectively unmet requirements you can see broken in the code above) from SPECULATIVE ones " +
       "(hypothetical risks like 'might not be initialized' with no evidence of an actual bug). ONLY CONFIRMED issues cause a repair. " +
       "Output ONLY a JSON object: {\"complete\": true/false, \"reason\": \"...\", \"issues\": [{\"type\": \"CONFIRMED\" | \"SPECULATIVE\", \"text\": \"...\"}]}.";
        var (raw, _, error) = await CallLlmRawStreaming(
            verifySystemPrompt, sb.ToString(), emitSse, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 1024);
        if (string.IsNullOrWhiteSpace(raw))
        {
            await EmitLog(emitSse, "warn", $"Verification LLM returned empty: {error}", ct: ct);
            if (deterministicIssues.Count > 0)
            {
                return (false,
                    $"Verification LLM call failed: {error}. Deterministic template-binding check found: {string.Join("; ", deterministicIssues)}",
                    deterministicIssues, new List<string>());
            }
            return (false, $"Verification LLM call failed: {error}", new List<string>(), new List<string>());
        }
        try
        {
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (m.Success) cleaned = m.Groups[1].Value.Trim();
            }
            cleaned = ExtractFirstJsonObject(cleaned);
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("complete", out var completeEl))
            {
                var isComplete = completeEl.GetBoolean();
                var reason = doc.RootElement.TryGetProperty("reason", out var rEl) ? rEl.GetString() : "";
                var (confirmedIssues, speculativeIssues) = ParseVerifyIssues(doc.RootElement);
                if (deterministicIssues.Count > 0)
                {
                    // Deterministic findings always fail verification and are never triaged away.
                    isComplete = false;
                    confirmedIssues.AddRange(deterministicIssues);
                    if (!string.IsNullOrWhiteSpace(reason)) reason = reason.Trim() + " ";
                    reason += "Deterministic template-binding check: " + string.Join("; ", deterministicIssues);
                }
                var issuesJoined = string.Join("; ", confirmedIssues);
                var details = reason + (string.IsNullOrWhiteSpace(issuesJoined) ? "" : $"\nIssues: {issuesJoined}");
                await EmitLog(emitSse, isComplete ? "info" : "warn",
                    $"Verification: complete={isComplete}, reason={reason}{(string.IsNullOrWhiteSpace(issuesJoined) ? "" : $", issues=[{issuesJoined}]")}", ct: ct);
                if (speculativeIssues.Count > 0)
                {
                    await EmitLog(emitSse, "bypass",
                        $"🔎 Speculative verifier concern(s) — logged, NOT acted on: {string.Join("; ", speculativeIssues)}", ct: ct);
                }
                return (isComplete, details, confirmedIssues, speculativeIssues);
            }
        }
        catch { }
        // LLM output unparseable — deterministic findings still fail the run.
        if (deterministicIssues.Count > 0)
        {
            return (false,
                "Verification LLM output unparseable. Deterministic template-binding check found: " + string.Join("; ", deterministicIssues),
                deterministicIssues, new List<string>());
        }
        return (true, "", new List<string>(), new List<string>());
    }
    private async Task<List<PlanStep>> TryReplanAfterStep(
        string prompt, List<object> allResults, AgentPlan plan,
        string? steeringContext, string projectRoot, bool emitSse,
        CancellationToken ct, List<PlanStep> planItems, int itemIdx,
        bool stepSkipped, bool stepSucceeded, List<string>? attachedFiles,
        int[] replanBudget, string? cardId = null)
    {
        if (!stepSkipped && !stepSucceeded) return planItems;
        var remainingSteps = planItems.Skip(itemIdx + 1)
            .Where(p => !string.IsNullOrWhiteSpace(p.File)).ToList();
        if (remainingSteps.Count > 0) return planItems;
        if (replanBudget[0] <= 0)
        {
            await EmitLog(emitSse, "info",
                "Replan budget exhausted — any remaining gaps will be handled by post-execution verification.", ct: ct);
            return planItems;
        }
        var moreSteps = await GenerateReplanStepsAsync(prompt, allResults, plan,
            steeringContext, projectRoot, emitSse, ct, attachedFiles: attachedFiles);
        if (moreSteps != null && moreSteps.Count > 0)
        {
            var anyEditsDone = allResults.OfType<Dictionary<string, object?>>()
                .Any(r => r.GetValueOrDefault("type")?.ToString() is "edit" &&
                          r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created");
            if (anyEditsDone)
            {
                var createSteps = moreSteps.Where(s => "_create_file".Equals(s.File, StringComparison.OrdinalIgnoreCase)).ToList();
                if (createSteps.Count > 0)
                {
                    await EmitLog(emitSse, "warn",
                        $"Rejecting {createSteps.Count} _create_file step(s) injected after code edits already started — " +
                        $"file creation must happen first. ({string.Join("; ", createSteps.Select(s => s.Change))})", ct: ct);
                    moreSteps = moreSteps.Where(s => !"_create_file".Equals(s.File, StringComparison.OrdinalIgnoreCase)).ToList();
                }
            }
        }
        if (moreSteps != null && moreSteps.Count > 0)
        {
            replanBudget[0]--;
            planItems = MergePlanSteps(planItems, moreSteps);
            if (emitSse)
            {
                await SendSse(Response, "plan", new { summary = $"Added {moreSteps.Count} step(s)", items = planItems }, ct);
            }
            await PersistBoardDataPlanAsync(cardId, planItems, emitSse, ct, summary: $"Added {moreSteps.Count} step(s)", score: 0);
        }
        return planItems;
    }
    private async Task<bool> ClassifyIsBuildRepairPromptAsync(string prompt, CancellationToken ct)
    {
        const string sys =
            "You classify a single user request. Answer ONLY with JSON: {\"isBuildRepair\": true|false}.\n" +
            "isBuildRepair = true ONLY if the user is asking to fix compilation/build errors or warnings " +
            "in the existing project — i.e. the build is currently broken and they want it fixed, with " +
            "no new feature or code-change request attached.\n" +
            "isBuildRepair = false if the user is asking for a new feature, a UI change, a refactor, or " +
            "any request that happens to mention words like 'build', 'error', 'warning' in an unrelated " +
            "sense (e.g. 'build out this feature', 'wire it up like X', 'add a popup panel').";
        var (raw, _, _) = await CallLlmRaw(sys, prompt, ct, _infiniteTimeout, maxTokens: 32);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            var cleaned = ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            return doc.RootElement.TryGetProperty("isBuildRepair", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }
    private async Task<AgentPlan?> RecoverPlanFromRamblingAsync(
        bool emitSse, CancellationToken ct, string ramblingRaw)
    {
        if (ramblingRaw.Contains('{')) return null;
        await EmitLog(emitSse, "warn",
            "Planner produced pure prose with no JSON — attempting recovery from its own reasoning", ct: ct);
        var tail = ramblingRaw.Length > 3000 ? ramblingRaw[^3000..] : ramblingRaw;
        var recoveryPrompt =
            "You were asked to plan code changes and output ONLY a JSON object, but instead you wrote " +
            "free-form reasoning and never produced the JSON. Here is the reasoning you already wrote:\n\n" +
            $"```\n{tail}\n```\n\n" +
            "STOP reasoning further. Based on the analysis above, output ONLY the JSON plan now. " +
            "Start your response with '{' as the very first character. No prose, no markdown fences.";
        var cfg = await LoadConfigAsync();
        var (raw, _, _) = await CallLlmRawStreaming(
            BuildPlanningPrompt(await FilterToolsForStepAsync(ramblingRaw, cfg.enabledTools, ct)), recoveryPrompt, emitSse, ct,
            requestTimeout: _infiniteTimeout, maxTokens: 2048);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var plan = AgentPlanParsing.ParsePlan(raw);
        if (plan == null && raw.Contains("<<<STEP", StringComparison.OrdinalIgnoreCase))
            plan = AgentPlanParsing.ParseDelimitedPlan(raw);
        if (plan != null)
            await EmitLog(emitSse, "success", "Recovery pass produced a valid plan from prior reasoning", ct: ct);
        return plan;
    }
    private static string? GetStepSignature(string file, string change)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(change)) return null;
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var methodMatches = Regex.Matches(change,
            @"\b(Post|Add|Get|Put|Delete|Create|Insert|Update)[A-Z][A-Za-z0-9]*\b");
        foreach (Match m in methodMatches)
        {
            parts.Add(m.Value);
            var entity = Regex.Replace(m.Value, @"^(Post|Add|Get|Put|Delete|Create|Insert|Update)", "");
            entity = entity.TrimEnd('s');
            parts.Add("e:" + entity);
        }
        if (parts.Count == 0) return null;
        var normalized = parts.OrderBy(x => x);
        return file.Replace('\\', '/') + "::" + string.Join("|", normalized);
    }
    private static string TruncateForLog(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = Regex.Replace(s, @"\s+", " ").Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
    }

}
