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
                var segment = SanitizeBranchSegment(req.CardId);
                var worktreePath = BuildWorktreePath(req.ProjectPath, segment);

                // ── Isolated git worktree mode (default) ───────────────────────
                // The new branch is checked out in its OWN working directory (a sibling
                // folder) so the shared checkout — and everyone else working in it — is
                // never switched, stashed or swept into the agent's commit. The shared
                // repo stays exactly as it was: branch, uncommitted changes and all.
                // Falls back to the legacy stash+checkout flow when worktrees can't be
                // used (e.g. an unborn HEAD with no commits, or an unwritable parent
                // directory).
                await _git.PruneWorktreesAsync(req.ProjectPath);
                var wtResult = await _git.CreateWorktreeAsync(req.ProjectPath, branchName, worktreePath);
                if (!wtResult.Success)
                {
                    // Collision (branch from a previous run, or a stale folder at the
                    // worktree path): retry ONCE with a timestamped identity so the
                    // shared checkout still never gets switched. Legacy is a last resort.
                    branchName = BuildBranchNameWithTimestamp(req.CardId);
                    worktreePath = BuildWorktreePathWithTimestamp(req.ProjectPath, segment);
                    wtResult = await _git.CreateWorktreeAsync(req.ProjectPath, branchName, worktreePath);
                }
                if (wtResult.Success)
                {
                    return Ok(new
                    {
                        success = true,
                        mode = "worktree",
                        branchName,
                        originalBranch,
                        worktreePath,
                        output = wtResult.Output,
                        error = wtResult.Error
                    });
                }

                // ── Legacy fallback: switch the shared checkout (stash + branch) ──
                // Only reached when worktrees are unavailable; the branch then lives in
                // the shared folder exactly as it did before worktree support.
                var legacyBranch = BuildBranchName(req.CardId);
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
                var branchResult = await _git.CreateBranchAsync(req.ProjectPath, legacyBranch);
                if (!branchResult.Success)
                {
                    // Branch may already exist — try with timestamp suffix
                    legacyBranch = BuildBranchNameWithTimestamp(req.CardId);
                    branchResult = await _git.CreateBranchAsync(req.ProjectPath, legacyBranch);
                }
                return Ok(new
                {
                    success = branchResult.Success,
                    mode = "legacy",
                    branchName = branchResult.Success ? legacyBranch : null,
                    originalBranch = branchResult.Success ? originalBranch : null,
                    worktreePath = (string?)null,
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

                // ── Worktree mode: remove the isolated working copy ─────────────
                // The shared checkout was never switched or stashed, so there is nothing
                // to restore — just tear the throwaway worktree down and delete the
                // branch. Mid-run uncommitted changes are preserved in a weaver-abort
                // stash (a shared-repo ref) so nothing the agent did is silently lost.
                if (!string.IsNullOrWhiteSpace(req.WorktreePath))
                {
                    if (Directory.Exists(req.WorktreePath))
                    {
                        if (await _git.HasUncommittedChangesAsync(req.WorktreePath))
                        {
                            var stashResult = await _git.RunGitAsync(req.WorktreePath, "stash push -u -m \"weaver-abort\"");
                            if (stashResult.Success) { log.Add("stashed mid-run changes (weaver-abort) — recover with `git stash pop`"); }
                            else { log.Add("could not stash mid-run changes: " + (stashResult.Error ?? stashResult.Output)); }
                        }
                        var removeResult = await _git.RemoveWorktreeAsync(req.ProjectPath, req.WorktreePath);
                        if (!removeResult.Success)
                            return Ok(new { success = false, error = removeResult.Error ?? removeResult.Output, worktreeRemoved = false, branchDeleted = false, log });
                        log.Add("removed isolated worktree " + req.WorktreePath);
                    }
                    else
                    {
                        log.Add("worktree already removed — nothing to clean up");
                    }
                    // Drop stale worktree metadata so branch -D isn't refused as
                    // "checked out in another worktree".
                    await _git.PruneWorktreesAsync(req.ProjectPath);
                    var delWt = await _git.DeleteBranchAsync(req.ProjectPath, req.BranchName);
                    var deleteErrorWt = delWt.Success ? null : (delWt.Error ?? delWt.Output);
                    if (deleteErrorWt != null && deleteErrorWt.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0)
                        return Ok(new { success = false, error = deleteErrorWt, branchDeleted = false, worktreeRemoved = true, log });
                    log.Add("deleted branch " + req.BranchName);
                    return Ok(new
                    {
                        success = true,
                        restoredBranch = await _git.GetCurrentBranchAsync(req.ProjectPath),
                        branchDeleted = true,
                        worktreeRemoved = true,
                        log
                    });
                }

                // ── Legacy flow: restore the original branch, pop the pre-branch
                // stash, and delete the branch (unchanged behavior) ───────────────
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

                // Worktree mode (default): the branch lives in an isolated working copy
                // and the agent's edits are committed THERE, so the shared checkout is
                // never staged, committed or switched — other people's work stays put.
                // Legacy mode (no worktreePath): operate on the shared checkout as before.
                var worktreePath = req.WorktreePath;
                var isWorktree = !string.IsNullOrWhiteSpace(worktreePath) && Directory.Exists(worktreePath);
                var workDir = isWorktree ? worktreePath! : req.ProjectPath;

                // Commit all changes (in the isolated worktree when present)
                var commitResult = await _git.CommitAllAsync(workDir, req.CardText ?? "Weaver agent changes");
                if (!commitResult.Success && !commitResult.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) && !commitResult.Error.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Commit warning: {Output} {Error}", commitResult.Output, commitResult.Error);
                }
                // Push
                var pushResult = await _git.PushAsync(workDir, branchName);
                if (!pushResult.Success)
                {
                    // Keep the worktree + branch so the user can retry — nothing is
                    // deleted on a failed push.
                    return Ok(new { success = false, error = pushResult.Error, branchName, commitHash = ExtractCommitHash(commitResult.Output), worktreePath = isWorktree ? worktreePath : null });
                }
                // Create PR via gh CLI
                var prBody = $"Automated PR by Weaver agent.\n\n{req.Summary ?? req.CardText ?? ""}";
                var prResult = await _git.CreatePullRequestAsync(workDir, req.CardText ?? "Weaver agent changes", prBody, branchName);
                string? prUrl = null;
                if (prResult.Success)
                {
                    // gh returns the PR URL on success
                    prUrl = ExtractPrUrl(prResult.Output);
                }

                // Cleanup depends on the mode the branch was created in.
                string? restoreError = null;
                string? cleanupError = null;
                var undoDiffsCopied = 0;
                var worktreeRemoved = false;
                if (isWorktree)
                {
                    if (prResult.Success)
                    {
                        // Preserve the card's diff trail: copy the worktree's data/undo
                        // snapshots into the SHARED repo before the worktree is removed, so
                        // the card's diff viewer keeps working from the shared checkout.
                        undoDiffsCopied = CopyUndoDiffs(worktreePath!, req.ProjectPath);
                        var removeResult = await _git.RemoveWorktreeAsync(req.ProjectPath, worktreePath!);
                        if (removeResult.Success)
                        {
                            worktreeRemoved = true;
                            // The branch is pushed (PR created) — drop the local ref. The
                            // shared checkout never moved, so there is nothing to restore.
                            var delResult = await _git.DeleteBranchAsync(req.ProjectPath, branchName);
                            if (!delResult.Success) cleanupError = delResult.Error;
                        }
                        else
                        {
                            cleanupError = removeResult.Error;
                        }
                    }
                    // On push/PR failure the worktree is intentionally left in place for
                    // retry/abort — no cleanup, no restore (nothing was touched here).
                }
                else
                {
                    // Legacy: restore the original branch + pop the pre-branch stash.
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
                    restoreError = restoreError,
                    worktreePath = isWorktree ? worktreePath : null,
                    worktreeRemoved = worktreeRemoved,
                    cleanupError = cleanupError,
                    undoDiffsCopied = undoDiffsCopied
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
        /// Isolated worktree location for a card's branch: a SIBLING folder of the repo,
        /// named &lt;repo&gt;-weaver-&lt;cardId&gt;. Living next to the shared checkout keeps the
        /// path predictable for humans and on the same volume (worktrees can't span
        /// drives for the same repo anyway — git refuses a worktree on a different
        /// filesystem than the main repo).
        /// </summary>
        private static string BuildWorktreePath(string repoPath, string segment)
        {
            var trimmed = (repoPath ?? "").TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = "repo";
            var parent = Path.GetDirectoryName(trimmed);
            return Path.Combine(parent ?? trimmed, $"{name}-weaver-{segment}");
        }

        private static string BuildWorktreePathWithTimestamp(string repoPath, string segment)
            => BuildWorktreePath(repoPath, segment) + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        /// <summary>
        /// Preserves a card's diff trail across worktree cleanup: copies every *.diff
        /// snapshot from the isolated worktree's data/undo into the SHARED repo's
        /// data/undo (skipping names that already exist there — another card's diff
        /// is never clobbered). The client rewrites its stored diff paths from the
        /// worktree prefix to the shared repo prefix after finish, so the diff viewer
        /// keeps working once the worktree is gone. Returns how many files were copied.
        /// </summary>
        private static int CopyUndoDiffs(string worktreePath, string repoPath)
        {
            var src = Path.Combine(worktreePath, "data", "undo");
            if (!Directory.Exists(src)) return 0;
            var dst = Path.Combine(repoPath, "data", "undo");
            var copied = 0;
            try
            {
                Directory.CreateDirectory(dst);
                foreach (var f in Directory.GetFiles(src, "*.diff"))
                {
                    var target = Path.Combine(dst, Path.GetFileName(f));
                    // Fully qualified: `File` here is System.IO.File, but inside a
                    // controller it would otherwise resolve to ControllerBase.File.
                    if (System.IO.File.Exists(target)) continue;
                    System.IO.File.Copy(f, target);
                    copied++;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CopyUndoDiffs failed: {ex.Message}");
            }
            return copied;
        }

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
        /// <summary>Isolated worktree the branch lives in (null for legacy shared-checkout branches).</summary>
        public string? WorktreePath { get; set; }
    }
    public class PrAbortRequest
    {
        public string? ProjectPath { get; set; }
        public string? CardId { get; set; }
        public string? BranchName { get; set; }
        public string? OriginalBranch { get; set; }
        /// <summary>Isolated worktree the branch lives in (null for legacy shared-checkout branches).</summary>
        public string? WorktreePath { get; set; }
    }
