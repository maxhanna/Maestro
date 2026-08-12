using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Weaver.Services;

public class GitResult
{
        public bool Success { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    public class GitService
    {
        public async Task<string> GetCurrentBranchAsync(string repoPath)
        {
            var result = await RunGitAsync(repoPath, "rev-parse --abbrev-ref HEAD");
            return result.Success ? result.Output.Trim() : "unknown";
        }

        public async Task<GitResult> CreateBranchAsync(string repoPath, string branchName)
        {
            var result = await RunGitAsync(repoPath, $"checkout -b \"{branchName}\"");
            return result;
        }

        // ── Isolated git worktrees (the BRANCH feature's default mode) ────────
        // A worktree is a separate working directory checked out to its own branch
        // of the SAME repository (shared .git object database). Creating one never
        // touches the main checkout, so other people working in the shared project
        // folder keep their uncommitted changes and their branch — nothing to stash,
        // nothing to switch, nothing that can ride along into the agent's branch.
        public async Task<GitResult> CreateWorktreeAsync(string repoPath, string branchName, string worktreePath)
        {
            return await RunGitAsync(repoPath, $"worktree add -b \"{branchName}\" \"{worktreePath}\"");
        }

        public async Task<GitResult> RemoveWorktreeAsync(string repoPath, string worktreePath)
        {
            // --force: the throwaway working copy may hold untracked files / build
            // output the agent left behind; it is isolated, so force removal is safe.
            return await RunGitAsync(repoPath, $"worktree remove --force \"{worktreePath}\"");
        }

        public async Task<GitResult> PruneWorktreesAsync(string repoPath)
        {
            return await RunGitAsync(repoPath, "worktree prune");
        }

        public async Task<GitResult> DeleteBranchAsync(string repoPath, string branchName)
        {
            return await RunGitAsync(repoPath, $"branch -D \"{branchName}\"");
        }

        public async Task<bool> BranchExistsAsync(string repoPath, string branchName)
        {
            var result = await RunGitAsync(repoPath, $"rev-parse --verify --quiet \"refs/heads/{branchName}\"");
            return result.Success;
        }

        public async Task<GitResult> CommitAllAsync(string repoPath, string message)
        {
            await RunGitAsync(repoPath, "add -A");
            var escaped = message.Replace("\"", "\\\"");
            var result = await RunGitAsync(repoPath, $"commit -m \"{escaped}\"");
            return result;
        }

        public async Task<GitResult> PushAsync(string repoPath, string branchName)
        {
            var result = await RunGitAsync(repoPath, $"push origin \"{branchName}\"");
            return result;
        }

        public async Task<GitResult> CreatePullRequestAsync(string repoPath, string title, string body, string branchName)
        {
            var escapedTitle = title.Replace("\"", "\\\"");
            var escapedBody = (body ?? "").Replace("\"", "\\\"").Replace("\n", "\\n");
            var result = await RunGhAsync(repoPath, $"pr create --title \"{escapedTitle}\" --body \"{escapedBody}\" --head \"{branchName}\"");
            return result;
        }

        public async Task<bool> HasUncommittedChangesAsync(string repoPath)
        {
            var result = await RunGitAsync(repoPath, "status --porcelain");
            return result.Success && !string.IsNullOrWhiteSpace(result.Output);
        }

        public async Task<GitResult> RunGitAsync(string repoPath, string args)
        {
            return await RunProcessAsync("git", args, repoPath);
        }

        public async Task<GitResult> RunGhAsync(string repoPath, string args)
        {
            return await RunProcessAsync("gh", args, repoPath);
        } 

        private async Task<GitResult> RunProcessAsync(string command, string args, string workingDir)
        {
            var result = new GitResult();
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());

                result.Success = process.ExitCode == 0;
                result.Output = outputTask.Result ?? string.Empty;
                result.Error = errorTask.Result ?? string.Empty;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            return result;
        }
    }
