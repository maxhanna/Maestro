using Weaver.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
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
