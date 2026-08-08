using Weaver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
namespace Weaver.Controllers;
[ApiController]
[Route("api/[controller]")]
public class PRController : ControllerBase
{
        private readonly GitService _git;
        private readonly ILogger<PRController> _logger;
        public PRController(GitService git, ILogger<PRController> logger)
        {
            _git = git;
            _logger = logger;
        }
        [HttpPost("start")]
        public async Task<IActionResult> Start([FromBody] PrStartRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ProjectPath))
                    return BadRequest(new { success = false, error = "ProjectPath required" });
                var originalBranch = await _git.GetCurrentBranchAsync(req.ProjectPath);
                var branchName = BuildBranchName(req.CardId);
                var hasChanges = await _git.HasUncommittedChangesAsync(req.ProjectPath);
                if (hasChanges)
                {
                    // Stash ALL existing work so nothing leaks into the PR branch and nothing is
                    // lost: plain `stash push` only captures tracked changes, so untracked files
                    // (e.g. a file the agent just created) would ride along onto the new branch.
                    // -u / --include-untracked folds those in too; they are restored by the
                    // `stash pop` in Finish once the original branch is checked back out.
                    await _git.RunGitAsync(req.ProjectPath, "stash push -u -m \"weaver-auto-stash\"");
                }
                var branchResult = await _git.CreateBranchAsync(req.ProjectPath, branchName);
                if (!branchResult.Success)
                {
                    // Branch may already exist — try with timestamp suffix
                    branchName = BuildBranchNameWithTimestamp(req.CardId);
                    branchResult = await _git.CreateBranchAsync(req.ProjectPath, branchName);
                }
                return Ok(new
                {
                    success = branchResult.Success,
                    branchName = branchResult.Success ? branchName : null,
                    originalBranch = branchResult.Success ? originalBranch : null,
                    output = branchResult.Output,
                    error = branchResult.Error
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PR start failed");
                return Ok(new { success = false, error = ex.Message });
            }
        }
        /// <summary>
        /// Aborts a weaver branch created by /api/pr/start, leaving the repo exactly
        /// as it was before the branch: the original branch is checked back out, the
        /// pre-branch stash (weaver-auto-stash) is popped, and the weaver branch is
        /// deleted. Triggered ONLY from the card's abort button — never automatically,
        /// so a completed PR or a run the user wants to keep is never undone behind
        /// their back. Mid-run working-tree changes (a card stopped while still
        /// editing) are never destroyed: they are swept into a weaver-abort stash the
        /// user can recover from with `git stash pop`.
        /// </summary>
        [HttpPost("abort")]
        public async Task<IActionResult> Abort([FromBody] PrAbortRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ProjectPath))
                    return BadRequest(new { success = false, error = "ProjectPath required" });
                if (string.IsNullOrWhiteSpace(req.BranchName))
                    return BadRequest(new { success = false, error = "BranchName required" });

                var log = new List<string>();
                var currentBranch = await _git.GetCurrentBranchAsync(req.ProjectPath);
                var onBranch = string.Equals(currentBranch, req.BranchName, StringComparison.OrdinalIgnoreCase);
                string? restoredBranch = null;

                if (onBranch)
                {
                    // Protect any mid-run working-tree changes (e.g. a card stopped mid-edit)
                    // so they are never lost: they go into a weaver-abort stash, kept for
                    // the user to recover with `git stash pop` after the abort.
                    if (await _git.HasUncommittedChangesAsync(req.ProjectPath))
                    {
                        var stashResult = await _git.RunGitAsync(req.ProjectPath, "stash push -u -m \"weaver-abort\"");
                        if (stashResult.Success) { log.Add("stashed mid-run changes (weaver-abort)"); }
                        else { log.Add("could not stash mid-run changes: " + (stashResult.Error ?? stashResult.Output)); }
                    }
                    // Check out the original branch — falling back to master/main for repos
                    // where the original name wasn't recorded (e.g. an unborn HEAD at start).
                    var candidates = new[] { req.OriginalBranch, "master", "main" };
                    foreach (var candidate in candidates)
                    {
                        if (string.IsNullOrWhiteSpace(candidate) || candidate == "unknown") continue;
                        var co = await _git.RunGitAsync(req.ProjectPath, $"checkout \"{candidate}\"");
                        if (co.Success) { restoredBranch = candidate; break; }
                    }
                    if (restoredBranch == null)
                        return Ok(new { success = false, error = "Abort failed: could not check out the original branch — resolve the working tree and retry", log });
                }
                else
                {
                    // Already back on the original branch (e.g. a finish that pushed but
                    // never got cleaned up) — nothing to restore, just delete the branch.
                    restoredBranch = currentBranch;
                }

                // Restore the pre-branch stash created by /api/pr/start (only the
                // weaver-auto-stash entry — unrelated user stashes are never touched).
                string? stashRef = null;
                var stashList = await _git.RunGitAsync(req.ProjectPath, "stash list");
                stashRef = FindWeaverAutoStashRef(stashList.Output);
                string? popError = null;
                if (stashRef != null)
                {
                    var popResult = await _git.RunGitAsync(req.ProjectPath, $"stash pop {stashRef}");
                    if (popResult.Success) { log.Add($"restored pre-branch stash ({stashRef})"); }
                    else { popError = popResult.Error ?? popResult.Output; }
                }

                // The pre-branch changes must come back cleanly before the branch is
                // deleted — a conflicted pop means the working tree isn't "as it was",
                // so stop and let the user resolve it first (the branch stays put).
                if (popError != null)
                    return Ok(new { success = false, error = "Abort failed: could not restore the pre-branch stash — resolve the conflict and retry", popError, stashRef, restoredBranch, log });

                // -D (force): the branch may hold unmerged commits; a plain -d would refuse.
                // If it's already gone, that IS the end state abort aims for, so treat
                // "not found" as success.
                var del = await _git.RunGitAsync(req.ProjectPath, $"branch -D \"{req.BranchName}\"");
                var deleteError = del.Success ? null : (del.Error ?? del.Output);
                if (deleteError != null && deleteError.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0)
                    return Ok(new { success = false, error = deleteError, stashRef, restoredBranch, branchDeleted = false, log });

                log.Add("deleted branch " + req.BranchName);
                return Ok(new
                {
                    success = true,
                    restoredBranch,
                    branchDeleted = true,
                    stashRef,
                    log
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PR abort failed");
                return Ok(new { success = false, error = ex.Message });
            }
        }

        [HttpPost("finish")]
        public async Task<IActionResult> Finish([FromBody] PrFinishRequest req)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.ProjectPath))
                    return BadRequest(new { success = false, error = "ProjectPath required" });
                var branchName = req.BranchName ?? BuildBranchName(req.CardId);
                // Commit all changes
                var commitResult = await _git.CommitAllAsync(req.ProjectPath, req.CardText ?? "Weaver agent changes");
                if (!commitResult.Success && !commitResult.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) && !commitResult.Error.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Commit warning: {Output} {Error}", commitResult.Output, commitResult.Error);
                }
                // Push
                var pushResult = await _git.PushAsync(req.ProjectPath, branchName);
                if (!pushResult.Success)
                {
                    return Ok(new { success = false, error = pushResult.Error, branchName, commitHash = ExtractCommitHash(commitResult.Output) });
                }
                // Create PR via gh CLI
                var prBody = $"Automated PR by Weaver agent.\n\n{req.Summary ?? req.CardText ?? ""}";
                var prResult = await _git.CreatePullRequestAsync(req.ProjectPath, req.CardText ?? "Weaver agent changes", prBody, branchName);
                string? prUrl = null;
                if (prResult.Success)
                {
                    // gh returns the PR URL on success
                    prUrl = ExtractPrUrl(prResult.Output);
                }
                // Restore original branch
                string? restoreError = null;
                if (!string.IsNullOrWhiteSpace(req.OriginalBranch))
                {
                    var checkoutResult = await _git.RunGitAsync(req.ProjectPath, $"checkout \"{req.OriginalBranch}\"");
                    if (checkoutResult.Success)
                    {
                        await _git.RunGitAsync(req.ProjectPath, "stash pop");
                    }
                    else
                    {
                        restoreError = checkoutResult.Error;
                    }
                }
                return Ok(new
                {
                    success = prResult.Success,
                    prUrl = prUrl,
                    branchName = branchName,
                    originalBranch = req.OriginalBranch,
                    commitHash = ExtractCommitHash(commitResult.Output),
                    commitOutput = commitResult.Output,
                    pushOutput = pushResult.Output,
                    prOutput = prResult.Output,
                    prError = prResult.Error,
                    pushError = pushResult.Error,
                    restoreError = restoreError
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PR finish failed");
                return Ok(new { success = false, error = ex.Message });
            }
        }
        /// <summary>
        /// Turns a card id (or any free text) into the sanitized segment used in weaver/
        /// branch names: only [a-zA-Z0-9_-] survive. Null/empty/all-punctuation ids fall
        /// back to "task" so the branch name is always valid — git rejects a trailing
        /// slash, so the old behavior ("weaver/") would fail checkout -b outright.
        /// </summary>
        private static string SanitizeBranchSegment(string? cardId)
        {
            var sanitized = Regex.Replace(cardId ?? "task", @"[^a-zA-Z0-9_-]", "");
            return string.IsNullOrEmpty(sanitized) ? "task" : sanitized;
        }

        private static string BuildBranchName(string? cardId) => $"weaver/{SanitizeBranchSegment(cardId)}";

        private static string BuildBranchNameWithTimestamp(string? cardId)
            => $"weaver/{SanitizeBranchSegment(cardId)}-{DateTime.UtcNow:yyyyMMddHHmmss}";

        /// <summary>
        /// Finds the stash@{N} reference of the weaver-auto-stash entry created by
        /// /api/pr/start (stash message "weaver-auto-stash"). Returns null when no such
        /// entry exists, so abort never pops an unrelated user stash. The message regex
        /// is anchored per line: a newer weaver-abort stash pushed on top does not
        /// shadow the older weaver-auto-stash entry below it.
        /// </summary>
        private static string? FindWeaverAutoStashRef(string? stashListOutput)
        {
            if (string.IsNullOrEmpty(stashListOutput)) return null;
            var match = Regex.Match(stashListOutput, @"(stash@\{\d+\})[^\r\n]*weaver-auto-stash");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// gh prints the PR URL on success; pull the first http(s) URL out of its output.
        /// Returns null when there is nothing URL-shaped (so the client shows its
        /// "check your repository" fallback instead of raw gh output).
        /// </summary>
        private static string? ExtractPrUrl(string? output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            var match = Regex.Match(output, @"https?://[^\s]+");
            return match.Success ? match.Value : null;
        }

        private static string? ExtractCommitHash(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            var match = Regex.Match(output, @"\[[^\]]+ ([a-f0-9]{7,40})\]");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
    public class PrStartRequest
    {
        public string? ProjectPath { get; set; }
        public string? CardId { get; set; }
    }
    public class PrFinishRequest
    {
        public string? ProjectPath { get; set; }
        public string? CardId { get; set; }
        public string? CardText { get; set; }
        public string? BranchName { get; set; }
        public string? Summary { get; set; }
        public string? OriginalBranch { get; set; }
    }
    public class PrAbortRequest
    {
        public string? ProjectPath { get; set; }
        public string? CardId { get; set; }
        public string? BranchName { get; set; }
        public string? OriginalBranch { get; set; }
    }
