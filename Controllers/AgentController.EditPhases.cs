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
    // ── Phase: create-file fast path ────────────────────────────────────────
    // If the step targets a file that doesn't exist but carries NewString content
    // with no OldString, treat it as a _create_file: write it, emit, persist, and
    // return the next step index. Returns null when the normal pipeline must run.
    private async Task<int?> TryCreateFileAsync(
        PlanStep step, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex, int planItemIndex, string? cardId,
        string relPath, string fullPath)
    {
        // Never write file content to a path that is an existing DIRECTORY — File.WriteAllText
        // on a folder throws UnauthorizedAccessException on Windows. The ResolveAndApplyEdit
        // directory-target guard redirects/skips first; this is defense-in-depth for other callers.
        if (Directory.Exists(fullPath) && !System.IO.File.Exists(fullPath))
        {
            await EmitLog(emitSse, "info",
                $"✓ Already done: {relPath} — target is an existing directory; nothing to create", ct: ct);
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
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
            return stepIndex + 1;
        }
        if (!System.IO.File.Exists(fullPath) && !string.IsNullOrWhiteSpace(step.NewString) && string.IsNullOrWhiteSpace(step.OldString))
        {
            await EmitLog(emitSse, "info",
                $"⚠ Step targeted '{relPath}' which doesn't exist, but has NewString content. Treating as _create_file.", ct: ct);
            // DUMMY-FILE-FOR-FOLDER GUARD (mirrors the _create_file handler): a planner that
            // can't create a directory directly emits a placeholder file just to materialize
            // the folder. Skip the dummy file and create the directory it implies instead.
            if (await IsDummyFolderCreateStepAsync(relPath, step.NewString, ct))
            {
                return await ExecuteDummyFileAsDirectoryCreateAsync(
                    relPath, step.Change, projectRoot, emitSse, ct, allResults,
                    stepIndex, planItemIndex, cardId, plan: null);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                var fileContent = step.NewString;
                var createExt = Path.GetExtension(relPath).ToLowerInvariant();
                if (createExt == ".html" || createExt == ".htm" || createExt == ".cshtml" || createExt == ".razor" || createExt == ".vue" || createExt == ".svelte")
                {
                    fileContent = AgentCodeFormatting.AutoFixHtmlIndentation(fileContent);
                }
                await System.IO.File.WriteAllTextAsync(fullPath, fileContent, Encoding.UTF8, ct);
                var r = new Dictionary<string, object?>
                {
                    ["index"] = stepIndex,
                    ["type"] = "create",
                    ["status"] = "done",
                    ["path"] = relPath,
                    ["description"] = step.Change,
                    ["planItemIndex"] = planItemIndex
                };
                if (emitSse) await SendSse(Response, "step", r, ct);
                allResults.Add(r);
                await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
                return stepIndex + 1;
            }
            catch (Exception ex)
            {
                await EmitLog(emitSse, "error", $"Failed to create file {relPath}: {ex.Message}", ct: ct);
            }
        }
        return null;
    }

    // ── Phase: dummy-file-for-folder guard ───────────────────────────────────
    // A weak planner that cannot create a directory directly (its mkdir/_create_directory
    // attempt failed or was rejected) falls back to planning a PLACEHOLDER file just to get
    // the folder on disk — the benchmark-23 run wrote "benchmark_test_23/placeholder.txt"
    // containing "Placeholder file for directory creation". Both file-creation routes
    // (_create_file marker and TryCreateFileAsync) detect this and skip the dummy file,
    // creating the directory the step implies instead: the task asked for a folder, not a
    // junk file.
    private async Task<bool> IsDummyFolderCreateStepAsync(string fileName, string? content, CancellationToken ct)
    {
        // Deterministic fast-path first (no LLM round-trip for the obvious cases).
        if (AgentTextUtilities.IsDirectoryScaffoldPlaceholder(fileName, content)) return true;
        // Ambiguous SHORT content only: a real file carries real content, so anything longer
        // than this is never a folder-establishing placeholder. AND only when the step
        // plausibly HINTS at a placeholder (placeholder/dummy/keep/temp words) — without that
        // gate, every short _create_file (a README, a one-line helper) would cost an LLM call.
        if (string.IsNullOrWhiteSpace(content) || content.Trim().Length > 150) return false;
        if (!AgentTextUtilities.HasPlaceholderHint(fileName, content)) return false;
        var sys = "You classify a single file-creation step. A DUMMY PLACEHOLDER file is one whose ONLY purpose is to create the parent folder — it has no real content or purpose of its own (e.g. a file named 'placeholder.txt' containing 'Placeholder file for directory creation', or an empty '.keep'). Reply with exactly one word: YES or NO.";
        var usr = $"File to create: \"{fileName}\"\nContent:\n---\n{content}\n---\nIs this a dummy placeholder file whose only purpose is to establish the parent folder? Reply YES or NO.";
        var (raw, _, _) = await CallLlmRaw(sys, usr, ct, _infiniteTimeout, maxTokens: 8);
        return raw?.Trim().TrimEnd('.', ' ', '\t', '\r', '\n').StartsWith("YES", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Executes a placeholder <c>_create_file</c> step as a directory creation: creates the
    /// directory the step implies (the file's own parent, else the run's implied create
    /// directory) and emits a create result for it WITHOUT writing the dummy file. Returns
    /// the next step index.
    /// </summary>
    private async Task<int> ExecuteDummyFileAsDirectoryCreateAsync(
        string newFileRelPath, string? changeDesc, string projectRoot, bool emitSse,
        CancellationToken ct, List<object> allResults, int stepIndex, int planItemIndex,
        string? cardId, AgentPlan? plan)
    {
        var normPath = newFileRelPath.Replace('\\', '/');
        var slash = normPath.LastIndexOf('/');
        var dirRel = slash > 0 ? normPath[..slash] : null;
        if (string.IsNullOrWhiteSpace(dirRel))
            dirRel = FindImpliedCreateDirectory(projectRoot, plan, planItemIndex, allResults);
        if (string.IsNullOrWhiteSpace(dirRel))
        {
            await EmitLog(emitSse, "warn",
                $"Skipped placeholder file {newFileRelPath} — no directory context to create", ct: ct);
            var skip = new Dictionary<string, object?>
            {
                ["index"] = stepIndex,
                ["type"] = "create",
                ["status"] = "skipped",
                ["path"] = newFileRelPath,
                ["reason"] = "dummy placeholder file with no directory context — nothing to create",
                ["planItemIndex"] = planItemIndex
            };
            if (emitSse) await SendSse(Response, "step", skip, ct);
            allResults.Add(skip);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
            return stepIndex + 1;
        }
        var dirFull = Path.GetFullPath(Path.Combine(projectRoot, dirRel.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(dirFull);
        await EmitLog(emitSse, "success",
            $"Created directory {dirRel} — skipped dummy file {newFileRelPath} (placeholder step used only to establish the folder)", ct: ct);
        var r = new Dictionary<string, object?>
        {
            ["index"] = stepIndex,
            ["type"] = "create",
            ["status"] = "done",
            ["path"] = dirRel,
            ["description"] = changeDesc,
            ["planItemIndex"] = planItemIndex,
            ["reason"] = "placeholder file converted to directory creation"
        };
        if (emitSse) await SendSse(Response, "step", r, ct);
        allResults.Add(r);
        await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
        return stepIndex + 1;
    }

    // ── Phase: pre-edit validation ──────────────────────────────────────────
    // When the file already exists, PreEditValidation may declare the step
    // AlreadyDone (code present) or Irrelevant (out of scope). Either short-circuits
    // to the next step index; null means the edit must proceed.
    private async Task<int?> ValidatePreEditAsync(
        PlanStep step, string projectRoot, bool emitSse, CancellationToken ct,
        List<object> allResults, int stepIndex, int planItemIndex, string? cardId,
        string relPath, string fullPath)
    {
        if (!System.IO.File.Exists(fullPath)) return null;
        var currentContent = await System.IO.File.ReadAllTextAsync(
            fullPath, Encoding.UTF8, ct);
        var (verdict, reason) = PreEditValidation(currentContent, step);
        if (verdict == PreEditVerdict.AlreadyDone)
        {
            await EmitLog(emitSse, "info",
                $"✓ Already done: {relPath} — {reason}", ct: ct);
            var r = new Dictionary<string, object?>
            {
                ["index"] = stepIndex,
                ["type"] = "edit",
                ["status"] = "skipped",
                ["path"] = relPath,
                ["reason"] = reason,
                ["planItemIndex"] = planItemIndex
            };
            if (emitSse) await SendSse(Response, "step", r, ct);
            allResults.Add(r);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
            return stepIndex + 1;
        }
        if (verdict == PreEditVerdict.Irrelevant)
        {
            await EmitLog(emitSse, "warn",
                $"⏭ Skipping {relPath} — {reason}", ct: ct);
            var r = new Dictionary<string, object?>
            {
                ["index"] = stepIndex,
                ["type"] = "edit",
                ["status"] = "skipped",
                ["path"] = relPath,
                ["reason"] = reason,
                ["planItemIndex"] = planItemIndex
            };
            if (emitSse) await SendSse(Response, "step", r, ct);
            allResults.Add(r);
            await PersistBoardDataPlanStepAsync(cardId, planItemIndex, emitSse, ct, projectRoot: projectRoot);
            return stepIndex + 1;
        }
        return null;
    }
}
