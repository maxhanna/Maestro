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
