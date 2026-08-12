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
    /// <summary>
    /// Resolves the implied target directory for a pathless _create_file step (a bare filename
    /// extracted from its change description). Prefers the nearest preceding _create_directory
    /// step in the same plan when exactly one exists (unambiguous), otherwise falls back to the
    /// most recently created directory already executed in this run (covers the interleaved
    /// path, where each step executes as its own single-step plan). Returns null when there is
    /// no directory context, preserving the current behavior of placing the file at the
    /// project root for genuinely root-level files.
    /// </summary>
    private static string? FindImpliedCreateDirectory(
        string projectRoot, AgentPlan? plan, int beforeIndex, IEnumerable<object> allResults)
    {
        // Only accept candidates that are ACTUAL directories on disk — an extensionless file
        // created at root (LICENSE, Makefile, Dockerfile) produces the same result shape as a
        // created directory and must never be mistaken for one.
        static bool IsRealDir(string root, string rel)
        {
            try
            {
                return Directory.Exists(Path.GetFullPath(
                    Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))));
            }
            catch { return false; }
        }

        string? planDir = null;
        var dirCount = 0;
        if (plan?.Plan != null)
        {
            for (var i = 0; i < beforeIndex && i < plan.Plan.Count; i++)
            {
                var s = plan.Plan[i];
                if (string.Equals(s.File, "_create_directory", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(s.Change))
                {
                    var cand = s.Change.Trim('/', '\\', '"', '\'');
                    if (IsRealDir(projectRoot, cand))
                    {
                        planDir = cand;
                        dirCount++;
                    }
                }
            }
            if (dirCount == 1) return planDir;
        }
        string? lastExecutedDir = null;
        foreach (var r in allResults.OfType<Dictionary<string, object?>>())
        {
            if (r.GetValueOrDefault("type")?.ToString() == "create" &&
                r.GetValueOrDefault("status")?.ToString() is "done" or "created" &&
                r.GetValueOrDefault("path") is string p && !string.IsNullOrWhiteSpace(p))
            {
                var rel = p.Replace('\\', '/').Trim('/');
                // A created-directory result carries an extensionless path (e.g. "benchmark_test_6");
                // created FILE results carry an extension or a sub-path, so they never match.
                if (rel.Length > 0 && !rel.Contains('/') && !Path.HasExtension(rel) && IsRealDir(projectRoot, rel))
                    lastExecutedDir = rel;
            }
        }
        return lastExecutedDir;
    }

    private async Task ExecutePlan(
        string prompt, string projectRoot, bool emitSse, string discoveryContext,
        AgentPlan plan, CancellationToken ct, List<object> allResults,
        string? steeringContext = null, List<string>? attachedFiles = null,
        HashSet<int>? completedStepIndices = null, string? cardId = null,
        int[]? replanBudget = null,
        Func<string, Task>? onActivity = null,
        bool skipLlmPreResolution = false)
    {
        var stepIndex = 0;
        var planItems = plan.Plan.ToList();
        var webCtx = new StringBuilder();
        var checkpointCount = 0;
        const int MaxCheckpoints = 3;
        completedStepIndices ??= new HashSet<int>();
        replanBudget ??= new[] { 1 };
        var alreadyDecoupled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completedStepSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var itemIdx = 0; itemIdx < planItems.Count; itemIdx++)
        {
            ct.ThrowIfCancellationRequested();
            if (onActivity != null)
            {
                try { await onActivity("executing"); } catch { }
            }
            var item = planItems[itemIdx];
            if (completedStepIndices.Contains(itemIdx))
            {
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "plan",
                        description = item.Change,
                        path = item.File,
                        status = "done",
                        skipped = true,
                        planItemIndex = itemIdx,
                        message = "Already completed in a previous run"
                    }, ct);
                stepIndex++;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(cardId) && _cancelledSteps.TryGetValue(cardId, out var cancelled))
            {
                bool isCancelled;
                lock (cancelled) { isCancelled = cancelled.Contains(itemIdx); }
                if (isCancelled)
                {
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "plan",
                            description = item.Change,
                            path = item.File,
                            status = "skipped",
                            planItemIndex = itemIdx,
                            message = "Cancelled by user"
                        }, ct);
                    stepIndex++;
                    continue;
                }
            }
            var planFile = item.File;
            var changeDesc = item.Change;
            if (planFile.Equals("_done", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "success", $"Task self-reported complete: {changeDesc}", ct: ct);
                if (emitSse) await SendSse(Response, "done_signal", new { message = changeDesc }, ct);
                allResults.Add(new Dictionary<string, object?> { ["type"] = "done_signal", ["status"] = "done", ["output"] = changeDesc });
                return;
            }
            if (planFile.Equals("_checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                if (++checkpointCount > MaxCheckpoints) { await EmitLog(emitSse, "warn", "Max checkpoints reached", ct: ct); continue; }
                await EmitLog(emitSse, "info", $"Checkpoint {checkpointCount}/{MaxCheckpoints}: {changeDesc}", ct: ct);
                if (emitSse) await SendSse(Response, "phase", new { phase = "checkpoint", message = $"Checkpoint {checkpointCount}" }, ct);
                allResults.Add(new Dictionary<string, object?> { ["type"] = "checkpoint", ["status"] = "done", ["output"] = changeDesc });
                var remaining = planItems.Skip(itemIdx + 1).ToList();
                if (remaining.Count > 0)
                {
                    var newSteps = await CheckpointReplan(prompt, discoveryContext, remaining, allResults, projectRoot, emitSse, ct, steeringContext);
                    if (newSteps?.Count > 0)
                    {
                        var anyEditsDone = allResults.OfType<Dictionary<string, object?>>()
                            .Any(r => r.GetValueOrDefault("type")?.ToString() is "edit" &&
                                      r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created");
                        if (anyEditsDone)
                        {
                            var createSteps = newSteps.Where(s => "_create_file".Equals(s.File, StringComparison.OrdinalIgnoreCase)).ToList();
                            if (createSteps.Count > 0)
                            {
                                await EmitLog(emitSse, "warn",
                                    $"Checkpoint replan: rejecting {createSteps.Count} _create_file step(s) after edits already started. " +
                                    $"({string.Join("; ", createSteps.Select(s => s.Change))})", ct: ct);
                                newSteps = newSteps.Where(s => !"_create_file".Equals(s.File, StringComparison.OrdinalIgnoreCase)).ToList();
                            }
                        }
                        if (newSteps.Count > 0)
                        {
                            planItems = MergePlanSteps(planItems, newSteps);
                            if (emitSse) await SendSse(Response, "plan", new { summary = $"Phase {checkpointCount + 1}", items = planItems }, ct);
                        }
                    }
                }
                continue;
            }
            if (planFile.Equals("_continue", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "info", $"Continuation: {changeDesc}", ct: ct);
                allResults.Add(new Dictionary<string, object?> { ["type"] = "continue_signal", ["status"] = "done", ["output"] = changeDesc });
                continue;
            }
            if (planFile.Equals("_rename", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_rename_file", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecuteRenameFromChange(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                continue;
            }
            if (planFile.Equals("_delete_file", StringComparison.OrdinalIgnoreCase))
            {
                var target = changeDesc.Trim().Trim('"', '\'').Replace('\\', '/');
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, target.Replace('/', Path.DirectorySeparatorChar)));
                if (AgentProjectUtilities.IsPathUnderRoot(fullPath, projectRoot) && System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                    await EmitLog(emitSse, "success", $"Deleted {target}", ct: ct);
                    allResults.Add(new Dictionary<string, object?> { ["type"] = "rename", ["status"] = "done", ["path"] = target, ["editAction"] = "deleted" });
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                }
                else await EmitLog(emitSse, "warn", $"Delete target not found: {target}", ct: ct);
                continue;
            }
            if (planFile.Equals("_git", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecuteGitStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                continue;
            }
            if (planFile.Equals("_show", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_display", StringComparison.OrdinalIgnoreCase))
            {
                var text = changeDesc.Trim().Trim('`', '"', '\'');
                await EmitLog(emitSse, "info", text, ct: ct);
                if (emitSse) await SendSse(Response, "show", new { text }, ct);
                allResults.Add(new Dictionary<string, object?> { ["status"] = "done", ["type"] = "show", ["output"] = text });
                continue;
            }
            if (planFile.Equals("_create_directory", StringComparison.OrdinalIgnoreCase))
            {
                var dirRelPath = changeDesc.Replace('\\', '/');
                var dirFullPath = Path.GetFullPath(Path.Combine(projectRoot, dirRelPath.Replace('/', Path.DirectorySeparatorChar)));
                await EmitLog(emitSse, "info", $"Creating directory: {dirRelPath}", ct: ct);
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "create",
                        status = "running",
                        path = dirRelPath,
                        description = item.Change,
                        planItemIndex = itemIdx
                    }, ct);
                try
                {
                    Directory.CreateDirectory(dirFullPath);
                    await EmitLog(emitSse, "success", $"Created directory {dirRelPath}", ct: ct);
                    var createResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "create",
                        ["status"] = "done",
                        ["path"] = dirRelPath,
                        ["description"] = item.Change,
                        ["planItemIndex"] = itemIdx
                    };
                    if (emitSse) await SendSse(Response, "step", createResult, ct);
                    allResults.Add(createResult);
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "error", $"Failed to create directory {dirRelPath}: {ex.Message}", ct: ct);
                    var errResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "create",
                        ["status"] = "error",
                        ["path"] = dirRelPath,
                        ["error"] = ex.Message,
                        ["planItemIndex"] = itemIdx
                    };
                    if (emitSse) await SendSse(Response, "step", errResult, ct);
                    allResults.Add(errResult);
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                }
                stepIndex++;
                continue;
            }
            if (planFile.Equals("_sql_migration", StringComparison.OrdinalIgnoreCase))
            {
                // New SQL table → write a migrations/*.sql file the user applies manually.
                // The CREATE TABLE statement comes from newString; the table name is parsed
                // out of it (falling back to a name token in the change description).
                await EmitLog(emitSse, "info", $"SQL migration: {changeDesc}", ct: ct);
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "sql_migration",
                        status = "running",
                        path = "migrations/",
                        description = item.Change,
                        planItemIndex = itemIdx
                    }, ct);
                var statements = SqlMigrationService.ExtractCreateTableStatements(item.NewString ?? "");
                var written = new List<string>();
                if (statements.Count == 0)
                {
                    // No DDL supplied — draft one from the description so the user still gets a usable file.
                    var tableName = Regex.Match(changeDesc, @"\b(?:create\s+)?(?:table\s+)?([\w_]+)\b", RegexOptions.IgnoreCase).Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(tableName) || tableName.Length < 2) tableName = "new_table";
                    var draft = await DraftCreateTableAsync(tableName, changeDesc, ct);
                    var rel = SqlMigrationService.WriteMigration(projectRoot, tableName, draft);
                    if (rel != null) written.Add(rel);
                }
                else
                {
                    foreach (var (table, sql) in statements)
                    {
                        var rel = SqlMigrationService.WriteMigration(projectRoot, table, sql);
                        if (rel != null) written.Add(rel);
                    }
                }
                if (written.Count == 0)
                {
                    await EmitLog(emitSse, "warn", $"SQL migration skipped — table already covered by an existing migrations/*.sql file: {changeDesc}", ct: ct);
                }
                foreach (var rel in written)
                    await EmitLog(emitSse, "success", $"📦 SQL migration written: {rel} — apply it to your database manually, then delete the file", ct: ct);
                var migResult = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex,
                    ["type"] = "sql_migration",
                    ["status"] = "done",
                    ["path"] = written.Count > 0 ? string.Join(", ", written) : "(already migrated)",
                    ["description"] = item.Change,
                    ["planItemIndex"] = itemIdx
                };
                if (emitSse) await SendSse(Response, "step", migResult, ct);
                allResults.Add(migResult);
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                stepIndex++;
                continue;
            }
            if (planFile.Equals("_create_file", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "info", $"Creating file: {changeDesc}", ct: ct);
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "create",
                        status = "running",
                        path = changeDesc,
                        description = item.Change,
                        planItemIndex = itemIdx
                    }, ct);
                var extractSysPrompt = "You extract file paths from instructions. Output ONLY the relative file path (e.g., 'folder/file.ext'). No quotes, no markdown, no explanation.";
                var (extractedPath, _, _) = await CallLlmRaw(extractSysPrompt, changeDesc, ct, _infiniteTimeout, maxTokens: 64);
                var newFileRelPath = extractedPath.Trim().Trim('"', '\'', '`', ' ');
                if (string.IsNullOrWhiteSpace(newFileRelPath) || newFileRelPath.Contains(' ') || !newFileRelPath.Contains('.'))
                {
                    var match = Regex.Match(changeDesc, @"([\w\-/\\]+\.\w{1,5})");
                    if (match.Success)
                    {
                        newFileRelPath = match.Groups[1].Value;
                    }
                    else
                    {
                        await EmitLog(emitSse, "error", $"Could not extract valid file path from _create_file description: {changeDesc}", ct: ct);
                        var errResult = new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex,
                            ["type"] = "create",
                            ["status"] = "error",
                            ["path"] = changeDesc,
                            ["error"] = "Missing filename in _create_file description",
                            ["planItemIndex"] = itemIdx
                        };
                        if (emitSse) await SendSse(Response, "step", errResult, ct);
                        allResults.Add(errResult);
                        await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                        stepIndex++;
                        continue;
                    }
                }
                // DETERMINISTIC DIRECTORY SCOPING: a planner often omits the target folder from a
                // _create_file change description (e.g. "Create README markdown document..."), so the
                // path-extraction LLM guesses a bare filename and the file lands at the PROJECT ROOT
                // even when the run just created a dedicated directory for it. If the extracted path
                // is a bare filename, scope it to the implied directory — the nearest preceding
                // _create_directory step in the plan, or the most recently created directory in this
                // run (the interleaved path executes each step as its own single-step plan, so the
                // plan scan sees nothing but allResults already holds the created-directory result).
                if (!newFileRelPath.Contains('/') && !newFileRelPath.Contains('\\'))
                {
                    var impliedDir = FindImpliedCreateDirectory(projectRoot, plan, itemIdx, allResults);
                    if (!string.IsNullOrWhiteSpace(impliedDir))
                    {
                        var scoped = impliedDir.Trim('/', '\\', '"', '\'') + "/" + newFileRelPath;
                        await EmitLog(emitSse, "info",
                            $"Pathless _create_file '{newFileRelPath}' scoped to implied directory '{impliedDir}' → {scoped}", ct: ct);
                        newFileRelPath = scoped;
                    }
                }
                var newFileFullPath = Path.GetFullPath(Path.Combine(projectRoot, newFileRelPath.Replace('/', Path.DirectorySeparatorChar)));
                var contentToWrite = item.NewString ?? "";
                try
                {
                    // Directory-target guard: never File.WriteAllText to an existing directory path
                    // (throws UnauthorizedAccessException on Windows). Same bug class as the
                    // ResolveAndApplyEdit guard — an extraction that lands on a folder name.
                    if (Directory.Exists(newFileFullPath) && !System.IO.File.Exists(newFileFullPath))
                    {
                        await EmitLog(emitSse, "info",
                            $"✓ Already done: {newFileRelPath} — target is an existing directory; nothing to create", ct: ct);
                        var dirSkip = new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex,
                            ["type"] = "create",
                            ["status"] = "skipped",
                            ["path"] = newFileRelPath,
                            ["reason"] = "target is an existing directory",
                            ["planItemIndex"] = itemIdx
                        };
                        if (emitSse) await SendSse(Response, "step", dirSkip, ct);
                        allResults.Add(dirSkip);
                        await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                        stepIndex++;
                        continue;
                    }
                    if (System.IO.File.Exists(newFileFullPath))
                    {
                        await EmitLog(emitSse, "error", $"Cannot create {newFileRelPath} — file already exists at that exact path. Convert this step to an edit of the existing file.", ct: ct);
                        var existResult = new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex,
                            ["type"] = "create",
                            ["status"] = "error",
                            ["path"] = newFileRelPath,
                            ["error"] = "File already exists — convert to an edit step",
                            ["planItemIndex"] = itemIdx
                        };
                        if (emitSse) await SendSse(Response, "step", existResult, ct);
                        allResults.Add(existResult);
                        await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                        stepIndex++;
                        continue;
                    }
                    // Same-name check is scoped to the SAME directory: a same-named file in a
                    // different folder (e.g. benchmark_test_4/index.html) must not block creating
                    // benchmark_test_7/index.html.
                    var similarExistingFile = AgentDiscovery.FindSameDirectoryFile(newFileRelPath, projectRoot);
                    if (similarExistingFile != null)
                    {
                        await EmitLog(emitSse, "error", $"Cannot create {newFileRelPath} — a file with the same name ALREADY EXISTS at '{similarExistingFile}'. Retarget to the existing file.", ct: ct);
                        var dupResult = new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex,
                            ["type"] = "create",
                            ["status"] = "error",
                            ["path"] = newFileRelPath,
                            ["error"] = $"Same-named file exists at {similarExistingFile} — retarget",
                            ["planItemIndex"] = itemIdx
                        };
                        if (emitSse) await SendSse(Response, "step", dupResult, ct);
                        allResults.Add(dupResult);
                        await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                        stepIndex++;
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(newFileFullPath)!);
                    await System.IO.File.WriteAllTextAsync(newFileFullPath, contentToWrite, Encoding.UTF8, ct);
                    await EmitLog(emitSse, "success", $"Created {newFileRelPath} ({contentToWrite.Length} chars)", ct: ct);
                    var createResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "create",
                        ["status"] = "done",
                        ["path"] = newFileRelPath,
                        ["description"] = item.Change,
                        ["planItemIndex"] = itemIdx
                    };
                    if (emitSse) await SendSse(Response, "step", createResult, ct);
                    allResults.Add(createResult);
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                }
                catch (Exception ex)
                {
                    await EmitLog(emitSse, "error", $"Failed to create {newFileRelPath}: {ex.Message}", ct: ct);
                    var errResult = new Dictionary<string, object?>
                    {
                        ["index"] = stepIndex,
                        ["type"] = "create",
                        ["status"] = "error",
                        ["path"] = newFileRelPath,
                        ["error"] = ex.Message,
                        ["planItemIndex"] = itemIdx
                    };
                    if (emitSse) await SendSse(Response, "step", errResult, ct);
                    allResults.Add(errResult);
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                }
                stepIndex++;
                continue;
            }
            if (planFile.Equals("_discover", StringComparison.OrdinalIgnoreCase))
            {
                await EmitLog(emitSse, "info", $"_discover: running project-wide search for the remaining plan steps…", ct: ct);
                var beforeCtx = discoveryContext.Length;
                discoveryContext = await RunDiscoveryToolAsync(prompt, discoveryContext, projectRoot, emitSse, ct);
                var addedChars = discoveryContext.Length - beforeCtx;
                allResults.Add(new Dictionary<string, object?> { ["index"] = stepIndex, ["type"] = "_discover", ["status"] = "done", ["output"] = changeDesc });
                if (emitSse)
                    await SendSse(Response, "step", new
                    {
                        index = stepIndex,
                        type = "_discover",
                        status = "done",
                        path = "_discover",
                        description = changeDesc,
                        planItemIndex = itemIdx,
                        message = $"Discovery added {addedChars} chars to context"
                    }, ct);
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                var remainingAfterDiscover = planItems.Skip(itemIdx + 1).ToList();
                if (remainingAfterDiscover.Count > 0)
                {
                    var rp = await ReplanRemainingSteps(prompt, remainingAfterDiscover, discoveryContext, emitSse, ct);
                    if (rp?.Count > 0)
                    {
                        planItems = MergePlanSteps(planItems, rp);
                        if (emitSse)
                            await SendSse(Response, "plan", new { summary = "Plan updated after _discover", items = planItems }, ct);
                    }
                }
                stepIndex++;
                continue;
            }
            if (planFile.Equals("_ping", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecutePingStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                continue;
            }
            if (planFile.Equals("_package_install", StringComparison.OrdinalIgnoreCase))
            {
                stepIndex = await ExecutePackageInstallStep(changeDesc, projectRoot, emitSse, ct, allResults, stepIndex);
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                continue;
            }
            if (planFile.Equals("_command", StringComparison.OrdinalIgnoreCase))
            {
                var stepSkipped = false;
                var cmd = changeDesc.Trim().Trim('`', '"', '\'');
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    await EmitLog(emitSse, "info", $"Command: {cmd}", ct: ct);
                    _terminal.Start();
                    var cs = new AgentStep { Index = 0, Type = "command", Command = cmd, Description = cmd };
                    var prevCount = allResults.Count;
                    var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
                    stepIndex += cr.Count; allResults.AddRange(cr);
                    await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                    planItems = await TryReplanAfterStep(prompt, allResults, plan,
                        steeringContext, projectRoot, emitSse, ct, planItems, itemIdx,
                        stepSkipped, allResults.Count > prevCount, attachedFiles, replanBudget, cardId: cardId);
                }
                continue;
            }
            if (planFile.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase) ||
                planFile.Equals("_news", StringComparison.OrdinalIgnoreCase))
            {
                (stepIndex, discoveryContext) = await ExecuteWebPlanStep(planFile, changeDesc, prompt, projectRoot, emitSse, ct,
                    allResults, planItems, itemIdx, stepIndex, discoveryContext, webCtx);
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                continue;
            }
            if (planFile.Equals("_move_file", StringComparison.OrdinalIgnoreCase))
            {
                var dst = AgentDiscovery.ExtractTargetPath(changeDesc, planFile, projectRoot);
                if (dst != null)
                {
                    var rs = new AgentStep { Index = 0, Type = "rename", Path = planFile, ToPath = dst, Description = $"Move {planFile} → {dst}" };
                    var rr = await ExecuteSteps(new List<AgentStep> { rs }, projectRoot, stepIndex, emitSse, ct);
                    stepIndex += rr.Count; allResults.AddRange(rr);
                }
                await PersistBoardDataPlanStepAsync(cardId, itemIdx, emitSse, ct);
                continue;
            }
            if (AgentProjectUtilities.IsRelativePath(planFile))
            {
                var readOnlyPrefixes = new[] { "read", "look at", "examine", "inspect", "review", "understand",
                    "study", "browse", "view", "check how", "see how", "get familiar", "explore" };
                var changeLower = (item.Change ?? "").Trim().ToLowerInvariant();
                if (readOnlyPrefixes.Any(p => changeLower.StartsWith(p)))
                {
                    await EmitLog(emitSse, "info",
                        $"⏭ Read-only step (change starts with '{changeLower.Split(' ')[0]}') — exploring instead of editing", ct: ct);
                    var fp = Path.GetFullPath(Path.Combine(projectRoot, planFile.Replace('/', Path.DirectorySeparatorChar)));
                    var relPath = planFile.Replace('\\', '/');
                    if (System.IO.File.Exists(fp) && AgentProjectUtilities.IsPathUnderRoot(fp, projectRoot))
                    {
                        if (emitSse)
                            await SendSse(Response, "step", new
                            {
                                index = stepIndex,
                                type = "read",
                                status = "done",
                                path = relPath,
                                description = item.Change,
                                planItemIndex = itemIdx
                            }, ct);
                        allResults.Add(new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex,
                            ["type"] = "read",
                            ["status"] = "done",
                            ["path"] = relPath,
                            ["description"] = item.Change,
                            ["planItemIndex"] = itemIdx
                        });
                    }
                    else
                    {
                        if (emitSse)
                            await SendSse(Response, "step", new
                            {
                                index = stepIndex,
                                type = "read",
                                status = "error",
                                path = relPath,
                                error = "File not found",
                                planItemIndex = itemIdx
                            }, ct);
                        allResults.Add(new Dictionary<string, object?>
                        {
                            ["index"] = stepIndex,
                            ["type"] = "read",
                            ["status"] = "error",
                            ["path"] = relPath,
                            ["error"] = "File not found",
                            ["planItemIndex"] = itemIdx
                        });
                    }
                    await PersistStepStatusAsync(cardId, itemIdx, "done", emitSse, ct);
                    stepIndex++;
                    continue;
                }
                if (!alreadyDecoupled.Contains(item.Change ?? ""))
                {
                    alreadyDecoupled.Add(item.Change ?? "");
                }
                var stepSig = GetStepSignature(item.File, item.Change ?? "");
                if (stepSig != null && completedStepSignatures.Contains(stepSig))
                {
                    await EmitLog(emitSse, "info",
                        $"⏭ Step skipped — already accomplished by a prior step (signature: {stepSig})", ct: ct);
                    if (emitSse)
                        await SendSse(Response, "step", new
                        {
                            index = stepIndex,
                            type = "plan",
                            description = item.Change,
                            path = item.File,
                            status = "done",
                            skipped = true,
                            planItemIndex = itemIdx,
                            message = "Already accomplished by a prior step"
                        }, ct);
                    stepIndex++;
                    continue;
                }
                var prevCount = allResults.Count;
                try
                {
                    var prevSigCount = completedStepSignatures.Count;
                    stepIndex = await ResolveAndApplyEdit(
                        item, projectRoot, emitSse, ct, allResults, stepIndex,
                        prompt: prompt, plan: plan, planItemIndex: itemIdx,
                        cardId: cardId, attachedFiles: attachedFiles,
                        onActivity: onActivity,
                        skipLlmPreResolution: skipLlmPreResolution);
                    if (stepSig != null &&
                        (allResults.Count > prevCount &&
                         allResults[^1] is Dictionary<string, object?> lastResult &&
                         lastResult.TryGetValue("status", out var st) &&
                         st?.ToString() == "done"))
                    {
                        completedStepSignatures.Add(stepSig);
                    }
                }
                catch (StepFatalException ex)
                {
                    await EmitLog(emitSse, "error",
                        $"⛔ FATAL STEP FAILURE — halting plan execution. " +
                        $"Failed step: {ex.FailedFilePath} — {ex.FailedChangeDescription}",
                        new
                        {
                            error = ex.Message,
                            failedFile = ex.FailedFilePath,
                            failureContext = ex.FailureContext
                        }, ct: ct);
                    List<string> haltedDiffs = new();
                    if (emitSse)
                    {
                        haltedDiffs = await CollectRecentDiffPathsAsync(ex.FailedFilePath ?? "", projectRoot, ct);
                        await SendSse(Response, "plan-halted", new
                        {
                            reason = "A plan step failed irrecoverably",
                            failedStep = ex.FailedFilePath,
                            failedChange = ex.FailedChangeDescription,
                            error = ex.Message,
                            remainingSteps = planItems.Count - itemIdx - 1,
                            diffs = haltedDiffs
                        }, ct);
                    }
                    allResults.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "plan_halted",
                        ["status"] = "error",
                        ["reason"] = $"Fatal step failure: {ex.Message}",
                        ["failedFile"] = ex.FailedFilePath,
                        ["remainingSteps"] = planItems.Count - itemIdx - 1,
                        ["diffs"] = haltedDiffs
                    });
                    return;
                }
                var stepSkipped = false;
                string? status = null;
                if (allResults.Count > prevCount &&
                    allResults[^1] is Dictionary<string, object?> lastDict2 &&
                    lastDict2.TryGetValue("status", out var st2))
                {
                    status = st2?.ToString();
                    if (status == "error")
                    {
                        await EmitLog(emitSse, "error",
                            $"✗ Step permanently failed for {planFile} — {lastDict2.GetValueOrDefault("error")}", ct: ct);
                    }
                    else if (status == "skipped" || status == "done")
                    {
                        stepSkipped = true;
                    }
                }
                if (status == "done" && planFile.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    var editNewStr = allResults.Count > prevCount &&
                        allResults[^1] is Dictionary<string, object?> lastEditDict
                        ? lastEditDict.GetValueOrDefault("newStringPreview")?.ToString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(editNewStr))
                    {
                        var funcMatches = Regex.Matches(editNewStr,
                            @"\(\w+\)=\""[^\""]*?(\w+)\s*\(");
                        if (funcMatches.Count > 0)
                        {
                            var htmlFullPath2 = Path.GetFullPath(
                                Path.Combine(projectRoot, planFile.Replace('/', Path.DirectorySeparatorChar)));
                            var baseDir = Path.GetDirectoryName(htmlFullPath2) ?? "";
                            var nameNoExt = Path.GetFileNameWithoutExtension(planFile);
                            var tsPath = Path.Combine(baseDir, nameNoExt + ".ts");
                            var tsContent = System.IO.File.Exists(tsPath)
                                ? await System.IO.File.ReadAllTextAsync(tsPath, Encoding.UTF8, ct)
                                : "";
                            var missingSteps = new List<PlanStep>();
                            var seenMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (System.Text.RegularExpressions.Match m in funcMatches)
                            {
                                var funcName = m.Groups[1].Value;
                                if (funcName is "ngOnInit" or "ngOnDestroy" or "ngAfterViewInit" or "ngOnChanges" or "ngDoCheck" or "ngAfterContentInit" or "ngAfterContentChecked" or "ngAfterViewChecked" or "toggle" or "open" or "close" or "preventDefault" or "stopPropagation" or "console")
                                { continue; }
                                if (!seenMethods.Add(funcName)) { continue; }
                                var foundInProject = false;
                                if (!string.IsNullOrWhiteSpace(tsContent))
                                {
                                    var methodRx = new Regex($@"\b{Regex.Escape(funcName)}\s*[=:(<]|\b(get|set)\s+{Regex.Escape(funcName)}\b");
                                    if (methodRx.IsMatch(tsContent))
                                        foundInProject = true;
                                }
                                if (!foundInProject)
                                {
                                    var (grepPath, _) = await GrepProjectForDefinitionAsync(
                                        projectRoot, funcName, planFile, ct);
                                    if (grepPath != null)
                                        foundInProject = true;
                                }
                                if (foundInProject)
                                {
                                    var argsText = "";
                                    try
                                    {
                                        var callStart = m.Index + m.Length;
                                        if (callStart > 0 && callStart < (editNewStr?.Length ?? 0))
                                        {
                                            var remaining = editNewStr!.Substring(callStart);
                                            var closeParen = remaining.IndexOf(')');
                                            if (closeParen >= 0)
                                                argsText = remaining.Substring(0, closeParen).Trim();
                                        }
                                    }
                                    catch { }
                                    if (!string.IsNullOrWhiteSpace(argsText) && !string.IsNullOrWhiteSpace(tsContent))
                                    {
                                        var argProps = Regex.Matches(argsText, @"\b[a-z]+[a-zA-Z0-9]*\b")
                                            .Cast<Match>().Select(x => x.Value)
                                            .Where(x => !Regex.IsMatch(x, @"^\d+$"))
                                            .Distinct().ToList();
                                        var methodBodyProps = Regex.Matches(tsContent, @"\bthis\.([a-zA-Z_]\w*)")
                                            .Cast<Match>().Select(x => x.Value)
                                            .Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
                                        var mismatchedArgs = argProps
                                            .Where(a => !a.Contains("this.") &&
                                                !methodBodyProps.Any(b =>
                                                    b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase)))
                                            .ToList();
                                        if (mismatchedArgs.Count > 0 && mismatchedArgs.Count >= argProps.Count / 2)
                                        {
                                            var newFuncName = funcName;
                                            var youtubeArg = mismatchedArgs
                                                .FirstOrDefault(a => a.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0
                                                    || a.IndexOf("yt", StringComparison.OrdinalIgnoreCase) >= 0);
                                            if (youtubeArg != null)
                                            {
                                                var prefix = funcName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ? "onYoutube" : "youtube";
                                                var suffix = funcName.Length > 2 && char.IsUpper(funcName[2]) ? funcName.Substring(2) : funcName;
                                                newFuncName = prefix + char.ToUpper(suffix[0]) + suffix.Substring(1);
                                                foundInProject = false;
                                                funcName = newFuncName;
                                            }
                                        }
                                    }
                                    if (foundInProject)
                                        continue;
                                }
                                if (planItems.Any(p => p.File != null && p.File.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
                                    p.Change != null && p.Change.Contains(funcName, StringComparison.OrdinalIgnoreCase)))
                                    continue;
                                var relDir2 = Path.GetDirectoryName(planFile)?.Replace('\\', '/') ?? "";
                                var relTsPath2 = string.IsNullOrWhiteSpace(relDir2)
                                    ? nameNoExt + ".ts"
                                    : relDir2 + "/" + nameNoExt + ".ts";
                                missingSteps.Add(new PlanStep
                                {
                                    File = relTsPath2,
                                    Change = $"Implement the missing {funcName}() method referenced in {Path.GetFileName(planFile)}",
                                    TargetSymbol = funcName,
                                    Priority = item.Priority
                                });
                            }
                            if (missingSteps.Count > 0)
                            {
                                planItems.InsertRange(itemIdx + 1, missingSteps);
                                await EmitLog(emitSse, "info",
                                    $"Added {missingSteps.Count} step(s) for missing method(s): " +
                                    string.Join(", ", missingSteps.Select(s => s.Change)), ct: ct);
                                var planItemsJson2 = new JsonArray();
                                for (var pi = 0; pi < planItems.Count; pi++)
                                {
                                    planItemsJson2.Add(new JsonObject
                                    {
                                        ["index"] = pi,
                                        ["file"] = planItems[pi].File ?? "",
                                        ["change"] = planItems[pi].Change ?? "",
                                        ["priority"] = planItems[pi].Priority,
                                        ["line"] = planItems[pi].LineNumber,
                                        ["done"] = allResults.Any(r => r is Dictionary<string, object?> dict && dict.TryGetValue("planItemIndex", out var pii) && pii is int piiVal2 && piiVal2 == pi && dict.TryGetValue("status", out var st2b) && st2b is string stStr2b && stStr2b == "done")
                                    });
                                }
                                if (emitSse)
                                    await SendSse(Response, "plan", new
                                    {
                                        thinking = $"Added {missingSteps.Count} step(s) for missing method(s) referenced in HTML",
                                        summary = $"Added {missingSteps.Count} step(s)",
                                        items = planItemsJson2
                                    }, ct);
                                if (!string.IsNullOrWhiteSpace(cardId))
                                    await PersistBoardDataPlanAsync(cardId, planItems, emitSse, ct);
                            }
                        }
                    }
                }
                if (status is "done" or "modified")
                {
                    var lastEditResult = allResults.Count > prevCount
                        ? allResults[^1] as Dictionary<string, object?> : null;
                    var appliedNewStr = lastEditResult?.GetValueOrDefault("newStringPreview")?.ToString();
                    if (!string.IsNullOrWhiteSpace(appliedNewStr))
                    {
                        var currentFullPath = Path.GetFullPath(
                            Path.Combine(projectRoot, planFile.Replace('/', Path.DirectorySeparatorChar)));
                        string currentContent = "";
                        try
                        {
                            currentContent = await System.IO.File.ReadAllTextAsync(
                            currentFullPath, Encoding.UTF8, ct);
                        }
                        catch { }
                        var reflectedSteps = await ReflectOnAppliedEditAsync(
                            planFile, appliedNewStr, currentContent,
                            projectRoot, planItems, emitSse, ct);
                        if (reflectedSteps.Count > 0)
                        {
                            var completedSig = GetStepSignature(planFile, item.Change ?? "");
                            if (completedSig != null)
                            {
                                var before = reflectedSteps.Count;
                                reflectedSteps = reflectedSteps
                                    .Where(rs => GetStepSignature(rs.File ?? "", rs.Change ?? "") != completedSig)
                                    .ToList();
                                if (reflectedSteps.Count < before)
                                    await EmitLog(emitSse, "info",
                                        $"  ↪ Skipped {before - reflectedSteps.Count} reflected step(s) with same signature as completed step", ct: ct);
                            }
                            if (reflectedSteps.Count > 0)
                            {
                                await EmitLog(emitSse, "info",
                                    $"  ➕ Reflection added {reflectedSteps.Count} step(s): " +
                                    string.Join(" | ", reflectedSteps.Select(s => s.Change)), ct: ct);
                                planItems.InsertRange(itemIdx + 1, reflectedSteps);
                                await PersistBoardDataPlanAsync(cardId, planItems, emitSse, ct,
                                    summary: $"Reflection: +{reflectedSteps.Count} step(s)", score: 0,
                                    append: false);
                                if (emitSse)
                                    await SendSse(Response, "plan", new
                                    {
                                        thinking = $"Reflection after editing {planFile}",
                                        summary = $"Added {reflectedSteps.Count} follow-up step(s)",
                                        items = planItems.Select((p, i) => new
                                        {
                                            index = i,
                                            file = p.File,
                                            change = p.Change,
                                            priority = p.Priority,
                                            line = p.LineNumber,
                                            done = false
                                        }).ToList()
                                    }, ct);
                            }
                        }
                        if (AgentProjectUtilities.IsRelativePath(planFile))
                        {
                            var cohesionIssues = await RunCohesionCheckAsync(
                                planFile, currentContent, projectRoot, emitSse, ct);
                            await PersistCohesionToCardAsync(
                                cardId, planFile, cohesionIssues, emitSse, ct);
                        }
                    }
                }
                if (status != "skipped")
                {
                    planItems = await TryReplanAfterStep(prompt, allResults, plan,
                        steeringContext, projectRoot, emitSse, ct, planItems, itemIdx,
                        stepSkipped, allResults.Count > prevCount, attachedFiles, replanBudget, cardId: cardId);
                }
                continue;
            }
            if (string.IsNullOrWhiteSpace(planFile))
            {
                await EmitLog(emitSse, "warn", "Plan item with empty file — skipping", new { item }, ct: ct);
            }
        }
    }
    private async Task<int> ExecuteRenameFromChange(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        string? src = null, dst = null;
        var arrow = changeDesc.IndexOf('→');
        if (arrow > 0) { src = changeDesc[..arrow].Trim(); dst = changeDesc[(arrow + 1)..].Trim(); }
        else
        {
            var toIdx = changeDesc.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (toIdx > 0) { src = changeDesc[..toIdx].Trim(); dst = changeDesc[(toIdx + 4)..].Trim(' ', '"', '\''); }
        }
        if (!string.IsNullOrWhiteSpace(src) && !string.IsNullOrWhiteSpace(dst))
        {
            src = src.Replace('\\', '/').Trim('/');
            dst = dst.Replace('\\', '/').TrimEnd('/');
            if (!dst.Contains('/') && src.Contains('/'))
                dst = src[..(src.LastIndexOf('/') + 1)] + dst;
            var rs = new AgentStep { Index = 0, Type = "rename", Path = src, ToPath = dst, Description = $"Rename {src} → {dst}" };
            var rr = await ExecuteSteps(new List<AgentStep> { rs }, projectRoot, stepIndex, emitSse, ct);
            stepIndex += rr.Count; allResults.AddRange(rr);
        }
        else await EmitLog(emitSse, "error", $"_rename: could not parse src/dst from: {changeDesc}", ct: ct);
        return stepIndex;
    }
    private async Task<int> ExecuteGitStep(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        var lower = (changeDesc.Trim().Trim('`', '"', '\'') + " ").ToLowerInvariant();
        string gitCmd;
        if (lower.StartsWith("commit") || lower.Contains("commit all"))
        {
            var mm = Regex.Match(changeDesc, "\"([^\"]+)\"");
            var msg = mm.Success ? mm.Groups[1].Value : $"Auto-commit {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            gitCmd = $"git add -A && git commit -m \"{msg.Replace("\"", "\\\"")}\"";
        }
        else if (lower.StartsWith("revert") || lower.Contains("discard")) gitCmd = "git checkout -- .";
        else if (lower.StartsWith("pull")) gitCmd = "git pull";
        else if (lower.StartsWith("sync") || lower.Contains("push")) gitCmd = "git pull && git push";
        else
        {
            gitCmd = changeDesc.Trim().Trim('`', '"', '\'');
            if (!gitCmd.StartsWith("git ", StringComparison.OrdinalIgnoreCase)) gitCmd = "git " + gitCmd;
        }
        await EmitLog(emitSse, "info", $"Git: {gitCmd}", ct: ct);
        _terminal.Start();
        var gs = new AgentStep { Index = 0, Type = "command", Command = gitCmd, Description = gitCmd };
        var gr = await ExecuteSteps(new List<AgentStep> { gs }, projectRoot, stepIndex, emitSse, ct);
        stepIndex += gr.Count; allResults.AddRange(gr);
        return stepIndex;
    }
    private async Task<int> ExecutePingStep(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        var pingCmd = changeDesc.Trim().Trim('`', '"', '\'');
        if (pingCmd.Contains("<llamaUrl>", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = await GetLlamaBaseUrl();
            var uri = new Uri(baseUrl);
            pingCmd = OperatingSystem.IsWindows()
                ? $"powershell -Command \"Test-NetConnection {uri.Host} -Port {uri.Port} -WarningAction SilentlyContinue | Select-Object TcpTestSucceeded | Format-List\""
                : $"nc -zv -w 2 {uri.Host} {uri.Port} 2>&1";
        }
        await EmitLog(emitSse, "info", $"Ping: {pingCmd}", ct: ct);
        _terminal.Start();
        var cs = new AgentStep { Index = 0, Type = "command", Command = pingCmd, Description = pingCmd };
        var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
        stepIndex += cr.Count; allResults.AddRange(cr);
        return stepIndex;
    }
    private async Task<int> ExecutePackageInstallStep(
        string changeDesc, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex)
    {
        var installCmd = changeDesc.Trim().Trim('`', '"', '\'');
        await EmitLog(emitSse, "info", $"Package install: {installCmd}", ct: ct);
        _terminal.Start();
        var cs = new AgentStep { Index = 0, Type = "command", Command = installCmd, Description = installCmd };
        var cr = await ExecuteSteps(new List<AgentStep> { cs }, projectRoot, stepIndex, emitSse, ct);
        stepIndex += cr.Count; allResults.AddRange(cr);
        return stepIndex;
    }
    /// <summary>
    /// Caps web step output before it is sent to the client over SSE. A _web_fetch returns
    /// an entire page's text (megabytes after tag-stripping), which bloats the step card and
    /// can choke JSON parsing in the browser. Only the SSE payload is capped — the full output
    /// stays in allResults so the agent's context (AppendWebResultsToDiscoveryContext, which
    /// applies its own 20k cap) is never starved.
    /// </summary>
    private static (string capped, bool truncated) CapWebStepOutputForClient(string? output)
    {
        const int MaxClientWebChars = 12000;
        if (string.IsNullOrEmpty(output)) return ("", false);
        if (output.Length <= MaxClientWebChars) return (output, false);
        return (output[..MaxClientWebChars] + "\n\n… [truncated — full results kept for the agent's context]", true);
    }

    private async Task<(int stepIndex, string discoveryContext)> ExecuteWebPlanStep(
        string planFile, string changeDesc, string prompt,
        string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, List<PlanStep> planItems, int itemIdx,
        int stepIndex, string discoveryContext, StringBuilder webCtx)
    {
        var isSearch = planFile.Equals("_web_search", StringComparison.OrdinalIgnoreCase);
        var isNews = planFile.Equals("_news", StringComparison.OrdinalIgnoreCase);
        var query = changeDesc.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return (stepIndex, discoveryContext);
        await EmitLog(emitSse, "info", $"{(isNews ? "News" : "Web " + (isSearch ? "search" : "fetch"))}: {query}", ct: ct);
        var (outp, err) = isNews
            ? await _newsService.FetchNewsAsync(query, ct: ct)
            : isSearch ? await WebSearchAsync(query, ct) : await WebFetchAsync(query, ct);
        var curIdx = stepIndex;
        var wr = new Dictionary<string, object?>
        {
            ["index"] = curIdx,
            ["type"] = planFile,
            [isNews ? "query" : isSearch ? "query" : "url"] = query,
            ["status"] = err == null ? "done" : "error",
            ["output"] = outp // FULL output — allResults feeds the agent's context
        };
        allResults.Add(wr);
        if (emitSse)
        {
            // The client gets a capped copy so a multi-megabyte page can't bloat the step card.
            var (displayOutp, truncated) = CapWebStepOutputForClient(outp);
            await SendSse(Response, "step", new Dictionary<string, object?>(wr)
            {
                ["output"] = displayOutp,
                ["truncated"] = truncated
            }, ct);
        }
        if (!string.IsNullOrWhiteSpace(outp) && outp.Length > 80)
            webCtx.AppendLine($"\n## Web [{query}]\n{outp}");
        var nextIsWeb = itemIdx + 1 < planItems.Count &&
            (planItems[itemIdx + 1].File.Equals("_web_search", StringComparison.OrdinalIgnoreCase) ||
             planItems[itemIdx + 1].File.Equals("_web_fetch", StringComparison.OrdinalIgnoreCase) ||
             planItems[itemIdx + 1].File.Equals("_news", StringComparison.OrdinalIgnoreCase));
        if (!nextIsWeb && webCtx.Length > 0)
        {
            var remaining = planItems.Skip(itemIdx + 1).ToList();
            if (remaining.Any(r => AgentProjectUtilities.IsRelativePath(r.File ?? "") || r.File == "_create_file"))
            {
                var uctx = discoveryContext + "\n\n" + webCtx;
                var rp = await ReplanRemainingSteps(prompt, remaining, uctx, emitSse, ct);
                if (rp?.Count > 0)
                {
                    planItems = MergePlanSteps(planItems, rp);
                    discoveryContext = uctx;
                    if (emitSse)
                        await SendSse(Response, "plan", new { summary = "Plan updated after web results", items = planItems }, ct);
                }
                webCtx.Clear();
            }
        }
        return (stepIndex + 1, discoveryContext);
    }
    private static (bool approved, string reason, int score) VerifyEdit(
        string oldString, string newString, string oldContent, string newContent, bool fromFormatC = false)
    {
        if (oldContent == newContent) return (false, "Edit produced no change", 3);
        if (!string.IsNullOrWhiteSpace(oldContent) && string.IsNullOrWhiteSpace(newContent))
            return (false, "Edit would produce empty file — rejected to prevent data loss", 1);
        if (oldContent.Length > 200 && newContent.Length > 0 &&
            newContent.Length < oldContent.Length * 0.10)
            return (false, $"Edit would reduce file by {100 - (int)(newContent.Length * 100.0 / oldContent.Length)}% — suspicious content loss", 1);
        var normOld = AgentTextUtilities.NormalizeLineEndings(oldString);
        var normNew = AgentTextUtilities.NormalizeLineEndings(newString);
        var normOldContent = AgentTextUtilities.NormalizeLineEndings(oldContent);
        var normNewContent = AgentTextUtilities.NormalizeLineEndings(newContent);
        if (!string.IsNullOrEmpty(normNew) &&
            !normNewContent.Contains(normNew, StringComparison.Ordinal))
        {
            var strippedNew = AgentTextUtilities.StripLineLeadingWhitespace(normNew);
            var strippedContent = AgentTextUtilities.StripLineLeadingWhitespace(normNewContent);
            var trimmedNew = string.Join("\n", strippedNew.Split('\n').Select(l => l.TrimEnd()));
            var trimmedContent = string.Join("\n", strippedContent.Split('\n').Select(l => l.TrimEnd()));
            if (!trimmedContent.Contains(trimmedNew, StringComparison.Ordinal))
                return (false, "newString not found after replacement", 4);
        }
        var hallucinatedPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EventTitle", "EventDescription",
            "UserName", "UserEmail",
            "Attendees", "Organizer",
        };
        var newlyIntroducedProps = hallucinatedPropertyNames
            .Where(p => !Regex.IsMatch(normOldContent, $@"\b{Regex.Escape(p)}\b", RegexOptions.IgnoreCase))
            .Where(p => Regex.IsMatch(normNewContent, $@"\.{Regex.Escape(p)}\b", RegexOptions.IgnoreCase))
            .ToList();
        if (newlyIntroducedProps.Count > 0)
        {
            return (false,
                $"Newly introduced property(s) [{string.Join(", ", newlyIntroducedProps)}] not found in any " +
                $"model, SQL column, or comment in the original file. These are common LLM hallucination names. " +
                $"Cross-reference the type definition in AUTO-ENRICHED CONTEXT and use the EXACT property names " +
                $"shown there (e.g. CalendarEntry uses 'Type' and 'Note', not 'Title' and 'Description').", 2);
        }
        if (!string.IsNullOrEmpty(normOld) && normOld.Length >= 10 && !normNew.Contains(normOld) &&
            normNew.Length <= normOld.Length * 1.3)
        {
            var strippedOld = AgentTextUtilities.StripLineLeadingWhitespace(normOld);
            var strippedOldContent = AgentTextUtilities.StripLineLeadingWhitespace(normOldContent);
            var strippedNewContent = AgentTextUtilities.StripLineLeadingWhitespace(normNewContent);
            var oldCount = 0; var newCount = 0; var pos = 0;
            while ((pos = strippedOldContent.IndexOf(strippedOld, pos, StringComparison.Ordinal)) >= 0)
            { oldCount++; pos += strippedOld.Length; }
            pos = 0;
            while ((pos = strippedNewContent.IndexOf(strippedOld, pos, StringComparison.Ordinal)) >= 0)
            { newCount++; pos += strippedOld.Length; }
            if (oldCount > 0 && newCount >= oldCount)
                return (false, "oldString still fully present after replacement — edit hit wrong location", 4);
        }
        if (string.Equals(normOld.Trim(), normNew.Trim(), StringComparison.Ordinal))
            return (false, "oldString and newString are identical after normalization", 3);
        if (!string.IsNullOrWhiteSpace(normNew))
        {
            var garbageTokens = new[] { "</s>", "<|endoftext|>", "<|im_end|>", "|im_end|", "<|endofprompt|>" };
            foreach (var tok in garbageTokens)
            {
                if (normNew.Contains(tok, StringComparison.OrdinalIgnoreCase))
                    return (false, $"newString contains leaked LLM token '{tok}' — edit is corrupted", 1);
            }
        }
        if (!fromFormatC)
        {
            var newRoutes = Regex.Matches(newContent,
                @"\[Http(?:Get|Post|Put|Delete|Patch)\(""([^""]+)""")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var oldRoutes = Regex.Matches(oldContent,
                @"\[Http(?:Get|Post|Put|Delete|Patch)\(""([^""]+)""")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var introducedDups = newRoutes
                .GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .Where(r => !oldRoutes.Contains(r, StringComparer.OrdinalIgnoreCase) ||
                             oldRoutes.Count(o => o.Equals(r, StringComparison.OrdinalIgnoreCase)) < 2)
                .ToList();
            if (introducedDups.Count > 0)
                return (false,
                    $"Edit introduces duplicate route(s): {string.Join(", ", introducedDups)}. " +
                    "LLM likely copied an entire existing method instead of inserting new code. " +
                    "Use a precise insertion anchor or insertAfter instead.", 1);
        }
        var emptyDeclPattern =
             @"(?:public|private|internal|protected)?\s*(?:class|struct|interface|record)\s+\w+\s*\{\s*(?:\/\*[\s\S]*?\*\/)?\s*\}|" +
             @"(?:public|private|internal|protected)?\s*(?:class|struct|interface|record)\s+\w+\s*\n\s*\{\s*\n\s*\}";
        var emptyDeclsNew = Regex.Matches(newContent, emptyDeclPattern).Cast<Match>().Select(m => m.Value.Trim()).ToHashSet(StringComparer.Ordinal);
        var emptyDeclsOld = Regex.Matches(oldContent, emptyDeclPattern).Cast<Match>().Select(m => m.Value.Trim()).ToHashSet(StringComparer.Ordinal);
        var introducedEmpty = emptyDeclsNew.Except(emptyDeclsOld).ToList();
        if (introducedEmpty.Count > 0)
        {
            return (false,
                $"Edit introduces NEW empty type(s): {string.Join(", ", introducedEmpty)}. These types already exist in the project — " +
                "find their definition and use the existing type instead of creating a stub.", 1);
        }
        var specificSqlPatterns = new[]
        {
            @"\bINTERVAL\d",
            @"\bDAY\d", @"\bHOUR\d", @"\bMINUTE\d", @"\bSECOND\d",
            @"\bLIMIT\d",
            @"\bOFFSET\d",
        };
        foreach (var line in newContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 10) continue;
            if (!Regex.IsMatch(trimmed, @"\b(SELECT|FROM|WHERE|AND|INSERT|UPDATE|DELETE|JOIN|INTERVAL|DATE_ADD|LIMIT)\b", RegexOptions.IgnoreCase))
                continue;
            foreach (var pattern in specificSqlPatterns)
            {
                var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var ctx = match.Value;
                    return (false,
                        $"SQL whitespace collapsed: '{ctx}' — likely missing a space. " +
                        "Copy the exact whitespace from the original SQL. 'INTERVAL 15' is correct, 'INTERVAL15' is not.", 2);
                }
            }
        }
        var fixedOld = AgentCodeFormatting.AutoFixSqlWhitespace(normOldContent);
        var fixedNew = AgentCodeFormatting.AutoFixSqlWhitespace(normNewContent);
        var oldTables = AgentProjectUtilities.ExtractSqlTableNames(fixedOld);
        var newTables = AgentProjectUtilities.ExtractSqlTableNames(fixedNew);
        if (oldTables.Count > 0 && newTables.Count > 0)
        {
            var missingTables = oldTables.Where(t => !newTables.Contains(t)).ToList();
            if (missingTables.Count > 0)
            {
                var returnAnchor = AgentMethodInventory.FindLastReturnLine(normOld);
                var anchorHint = returnAnchor != null
                    ? $" Anchor on the return statement: oldString=\"{returnAnchor.Trim()}\""
                    : "";
                return (false,
                    $"Edit replaces existing SQL table(s) [{string.Join(", ", missingTables.Take(3))}] with different tables. " +
                    "Preserve the original query structure; only add the required logic." + anchorHint, 1);
            }
        }
        if (AgentProjectUtilities.IsAngularTemplate(newContent))
        {
            var bannedInAngular = new[] { "Math.min(", "Math.max(", "Math.floor(", "Math.ceil(",
                "Math.round(", "Math.random(", "parseInt(", "parseFloat(", "JSON.parse", "JSON.stringify" };
            foreach (var banned in bannedInAngular)
            {
                if (newContent.Contains(banned, StringComparison.OrdinalIgnoreCase))
                {
                    var match = Regex.Match(newContent, $@"\b{Regex.Escape(banned[..^1])}\s*\(", RegexOptions.IgnoreCase);
                    if (match.Success)
                        return (false,
                            $"Angular template uses `{match.Value}()` which is not available in Angular templates. " +
                            "Only component properties and methods are accessible in template expressions. " +
                            $"Move this logic to the component's .ts file.", 2);
                }
            }
        }
        return (true, "Programmatic check passed", 10);
    }
    private async Task<List<string>> CollectRecentDiffPathsAsync(string relPath, string projectRoot, CancellationToken ct)
    {
        var diffs = new List<string>();
        try
        {
            var undoDir = Path.Combine(projectRoot, "data", "undo");
            if (!Directory.Exists(undoDir)) return diffs;
            var safeName = relPath.Replace('/', '_').Replace('\\', '_');
            var files = Directory.GetFiles(undoDir, $"{safeName}*.diff")
                .OrderByDescending(f => f)
                .Take(10)
                .ToList();
            Console.WriteLine($"[CollectDiffs] {relPath}: found {files.Count} diff(s) in {undoDir}");
            foreach (var f in files)
            {
                var rel = f.Replace(projectRoot, "").TrimStart('\\', '/');
                diffs.Add(rel);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[CollectDiffs] EXCEPTION for {relPath}: {ex.Message}"); }
        return diffs;
    }
    private async Task<List<PlanStep>?> ReplanRemainingSteps(
        string originalPrompt, List<PlanStep> remaining,
        string updatedContext, bool emitSse, CancellationToken ct)
    {
        if (remaining.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine("Revise remaining steps given web results. Keep ALL existing steps and add any new ones needed. Original task: " + originalPrompt);
        foreach (var s in remaining) sb.AppendLine($"  {s.File}: {s.Change}");
        sb.AppendLine(updatedContext);
        const string sys = "Revise remaining execution steps. NEVER remove existing steps. Output ONLY JSON: {\"plan\":[{\"file\":\"...\",\"change\":\"...\",\"priority\":1}]}";
        var (raw, _, _) = await CallLlmRaw(sys, sb.ToString(), ct, _infiniteTimeout, maxTokens: 2048);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```")) { var m = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase); if (m.Success) cleaned = m.Groups[1].Value.Trim(); }
        var parsed = AgentPlanParsing.ParsePlan(cleaned);
        return parsed?.Plan?.Count > 0 ? parsed.Plan : null;
    }
    private async Task<string?> SaveEditWithUndoAsync(
        string fullPath, string newContent, string relPath,
        string projectRoot, string preEditContent, CancellationToken ct)
    {
        string? diffPath = null;
        try
        {
            var undoDir = Path.Combine(projectRoot, "data", "undo");
            Directory.CreateDirectory(undoDir);
            var safeName = relPath.Replace('/', '_').Replace('\\', '_');
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff");
            diffPath = Path.Combine(undoDir, $"{safeName}.{timestamp}.diff");
            var gitDir = Path.Combine(projectRoot, ".git");
            string? diffOutput = null;
            if (Directory.Exists(gitDir))
            {
                var proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = $"diff --no-color \"{relPath.Replace('/', Path.DirectorySeparatorChar)}\"",
                        WorkingDirectory = projectRoot,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var gitDiffOutput = await proc.StandardOutput.ReadToEndAsync();
                var diffError = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit(5000);
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(gitDiffOutput))
                    diffOutput = gitDiffOutput;
            }
            if (diffOutput == null)
            {
                var currentContent = System.IO.File.Exists(fullPath)
                    ? await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct)
                    : "";
                if (currentContent != newContent)
                    diffOutput = AgentDiffUtilities.BuildUnifiedDiff(currentContent, newContent, relPath);
            }
            if (!string.IsNullOrWhiteSpace(diffOutput))
            {
                var undoHeader = $"; Undo for {relPath} @ {DateTime.UtcNow:O}\n" +
                                 $"; Restore with: git apply --reverse \"{diffPath}\"\n" +
                                 $"; Or use: git checkout -- \"{relPath.Replace('/', Path.DirectorySeparatorChar)}\"\n" +
                                 $"; To APPLY this diff: git apply \"{diffPath}\"\n";
                await System.IO.File.WriteAllTextAsync(
                    diffPath, undoHeader + diffOutput, Encoding.UTF8, ct);
            }
            else
            {
                diffPath = null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SaveEditWithUndo] EXCEPTION for {relPath}: {ex.Message}");
            diffPath = null;
        }
        await System.IO.File.WriteAllTextAsync(fullPath, newContent, Encoding.UTF8, ct);
        return diffPath;
    }
    private async Task<List<PlanStep>> PruneIrrelevantPlanStepsAsync(List<PlanStep> steps, string projectRoot, CancellationToken ct)
    {
        if (steps == null || steps.Count == 0) return steps ?? [];
        var pruned = new List<PlanStep>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.File) || string.IsNullOrWhiteSpace(step.Change))
                continue;
            var changeLower = step.Change.ToLowerInvariant().Trim();
            if (changeLower.StartsWith("already done") || changeLower.StartsWith("no change") ||
                changeLower.StartsWith("skip") || changeLower.StartsWith("none") ||
                changeLower == "done" || changeLower == "n/a")
                continue;
            var removeMatch = Regex.Match(changeLower, @"remove\s+(?:the\s+)?(?:existing\s+)?(\w+)", RegexOptions.IgnoreCase);
            if (removeMatch.Success)
            {
                var target = $"{step.File}|{removeMatch.Groups[1].Value.ToLowerInvariant()}";
                removedTargets.Add(target);
            }
            var addMatch = Regex.Match(changeLower, @"(?:add|insert|create)\s+(?:a\s+)?(?:new\s+)?(\w+)", RegexOptions.IgnoreCase);
            if (addMatch.Success)
            {
                var target = $"{step.File}|{addMatch.Groups[1].Value.ToLowerInvariant()}";
                if (removedTargets.Contains(target))
                {
                    await EmitLog(false, "warn", $"Prune: removing add-after-remove loop for '{target}'", ct: ct);
                    continue;
                }
            }
            var normChange = NormalizeChangeForDedup(step.Change);
            var key = $"{step.File}|{normChange}";
            if (!seenKeys.Add(key))
            {
                await EmitLog(false, "warn", $"Prune: duplicate step '{step.Change}'", ct: ct);
                continue;
            }
            var isCreation = changeLower.Contains("create file") || changeLower.Contains("new file") ||
                            step.File.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                            changeLower.StartsWith("add ") || changeLower.StartsWith("create ");
            var fullPath = Path.GetFullPath(
                Path.Combine(projectRoot, step.File.Replace('/', Path.DirectorySeparatorChar)));
            var fileExists = System.IO.File.Exists(fullPath);
            var isModify = changeLower.StartsWith("modify ") || changeLower.StartsWith("change ") ||
                           changeLower.StartsWith("update ") || changeLower.StartsWith("replace ");
            if (isModify && !fileExists)
            {
                await EmitLog(false, "warn", $"Prune: modify step targets non-existent file '{step.File}'", ct: ct);
                continue;
            }
            if (fileExists)
            {
                var content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                var contentLower = content.ToLowerInvariant();
                var endpointMatch = Regex.Match(step.Change ?? "",
                    @"add\s+.*(?:httppost|httpget|httpput|httpdelete)\(""(.*?)""\)",
                    RegexOptions.IgnoreCase);
                if (endpointMatch.Success)
                {
                    var route = endpointMatch.Groups[1].Value.ToLowerInvariant();
                    if (contentLower.Contains($"httppost(\"{route}\"") ||
                        contentLower.Contains($"httpget(\"{route}\"") ||
                        contentLower.Contains($"httpput(\"{route}\"") ||
                        contentLower.Contains($"httpdelete(\"{route}\""))
                    {
                        await EmitLog(false, "warn", $"Prune: endpoint '{route}' already exists in '{step.File}'", ct: ct);
                        continue;
                    }
                }
                var methodMatch = Regex.Match(step.Change ?? @"", @"add\s+(?:method\s+)?(\w+)(?:\s*method)?\s*(?:endpoint|method|function)?", RegexOptions.IgnoreCase);
                if (methodMatch.Success)
                {
                    var methodName = methodMatch.Groups[1].Value;
                    if (Regex.IsMatch(content, $@"\b{Regex.Escape(methodName)}\s*\("))
                    {
                        await EmitLog(false, "warn", $"Prune: method '{methodName}' already exists in '{step.File}'", ct: ct);
                        continue;
                    }
                }
                var propMatch = Regex.Match(step.Change ?? @"", @"add\s+(?:property\s+)?(\w+)(?:\s*property)?", RegexOptions.IgnoreCase);
                if (propMatch.Success)
                {
                    var propName = propMatch.Groups[1].Value;
                    if (Regex.IsMatch(content, $@"\b{Regex.Escape(propName)}\b\s*(?::\s*\w+|;\s*$|\s*{{)"))
                    {
                        await EmitLog(false, "warn", $"Prune: property '{propName}' already exists in '{step.File}'", ct: ct);
                        continue;
                    }
                }
                if (step.LineNumber > 0)
                {
                    var locKey = $"{step.File}|L{step.LineNumber}";
                    if (!seenLocations.Add(locKey))
                    {
                        await EmitLog(false, "warn", $"Prune: another step already targets line {step.LineNumber} in '{step.File}'", ct: ct);
                        continue;
                    }
                }
            }
            pruned.Add(step);
        }
        return pruned;
    }
    private async Task<bool> VerifyCompletedFromStepTruthAsync(
        List<object> allSteps, string projectRoot, CancellationToken ct)
    {
        var doneEdits = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.TryGetValue("type", out var t) && t?.ToString() == "edit" &&
                        r.TryGetValue("status", out var st) && st?.ToString() == "done" &&
                        r.TryGetValue("editAction", out var a) && a?.ToString() == "modified")
            .ToList();
        if (doneEdits.Count == 0) return false;
        var fileCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in doneEdits)
        {
            var relPath = edit.GetValueOrDefault("path")?.ToString();
            var oldPreview = edit.GetValueOrDefault("oldStringPreview")?.ToString();
            var newPreview = edit.GetValueOrDefault("newStringPreview")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(relPath) || string.IsNullOrWhiteSpace(oldPreview)) continue;
            if (!string.IsNullOrWhiteSpace(newPreview)) continue;
            if (!fileCache.TryGetValue(relPath, out var content))
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!System.IO.File.Exists(fullPath)) continue;
                content = await System.IO.File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
                fileCache[relPath] = content;
            }
            var normContent = AgentTextUtilities.NormalizeLineEndings(content);
            var normOld = AgentTextUtilities.NormalizeLineEndings(oldPreview);
            if (normContent.Contains(normOld, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
