using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Weaver.Controllers;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for the "BRANCH" feature (the card.autoPr toggle): when a card with
/// autoPr completes, the client calls POST api/pr/start to create a weaver/
/// branch and api/pr/finish to commit, push and open a PR. These tests cover
/// the server half — the deterministic branch-name / PR-URL / commit-hash
/// helpers (via reflection) and the real-git orchestration of Start/Finish
/// against throwaway repositories.
/// </summary>
public class PrBranchFeatureTests
{
    // ── Deterministic helpers (private statics on PRController) ──────────────

    private static T InvokeStatic<T>(string name, params object?[]? args)
    {
        var method = typeof(PRController).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"PRController.{name} not found");
        // A single literal `null` argument collapses the params array to null —
        // treat that as one null argument, not zero.
        return (T)method.Invoke(null, args ?? new object?[] { null })!;
    }

    [Fact]
    public void SanitizeBranchSegment_Keeps_Alnum_Dash_Underscore()
    {
        Assert.Equal("TestCard1", InvokeStatic<string>("SanitizeBranchSegment", "Test Card 1!"));
        Assert.Equal("card-1_2", InvokeStatic<string>("SanitizeBranchSegment", "card-1_2"));
    }

    [Fact]
    public void SanitizeBranchSegment_Empty_Or_Garbage_Falls_Back_To_Task()
    {
        Assert.Equal("task", InvokeStatic<string>("SanitizeBranchSegment", null));
        Assert.Equal("task", InvokeStatic<string>("SanitizeBranchSegment", ""));
        Assert.Equal("task", InvokeStatic<string>("SanitizeBranchSegment", "   "));
        // All-punctuation ids would previously produce the invalid branch name
        // "weaver/" (git rejects the trailing slash) — the guard keeps it valid.
        Assert.Equal("task", InvokeStatic<string>("SanitizeBranchSegment", "!!!"));
    }

    [Fact]
    public void BuildBranchName_Prefixes_Weaver_Slash()
    {
        Assert.Equal("weaver/TestCard1", InvokeStatic<string>("BuildBranchName", "Test Card 1!"));
        Assert.Equal("weaver/task", InvokeStatic<string>("BuildBranchName", null));
    }

    [Fact]
    public void BuildBranchNameWithTimestamp_Appends_Utc_Timestamp()
    {
        var name = InvokeStatic<string>("BuildBranchNameWithTimestamp", "card 1");
        Assert.Matches(@"^weaver/card1-\d{14}$", name);
    }

    [Fact]
    public void ExtractPrUrl_Returns_First_Http_Url()
    {
        Assert.Equal("https://github.com/o/r/pull/5",
            InvokeStatic<string?>("ExtractPrUrl", "https://github.com/o/r/pull/5\n"));
        Assert.Equal("https://gitlab.com/x/y/-/merge_requests/3",
            InvokeStatic<string?>("ExtractPrUrl", "Merge request created: https://gitlab.com/x/y/-/merge_requests/3"));
    }

    [Fact]
    public void ExtractPrUrl_No_Url_Returns_Null()
    {
        Assert.Null(InvokeStatic<string?>("ExtractPrUrl", null));
        Assert.Null(InvokeStatic<string?>("ExtractPrUrl", ""));
        Assert.Null(InvokeStatic<string?>("ExtractPrUrl", "No pull request created here"));
    }

    [Fact]
    public void ExtractCommitHash_Extracts_Hash_From_Commit_Output()
    {
        Assert.Equal("abc1234", InvokeStatic<string?>("ExtractCommitHash", "[master abc1234] message"));
        Assert.Equal("0123456789abcdef",
            InvokeStatic<string?>("ExtractCommitHash", "[feature/x 0123456789abcdef] some change"));
    }

    [Fact]
    public void ExtractCommitHash_Returns_Null_When_No_Hash()
    {
        Assert.Null(InvokeStatic<string?>("ExtractCommitHash", null));
        Assert.Null(InvokeStatic<string?>("ExtractCommitHash", "nothing to commit, working tree clean"));
    }

    // ── FindWeaverAutoStashRef (abort must only pop the weaver stash) ────────

    [Fact]
    public void FindWeaverAutoStashRef_Finds_The_Auto_Stash_On_Top()
    {
        var list = "stash@{0}: On master: weaver-auto-stash\n";
        Assert.Equal("stash@{0}", InvokeStatic<string?>("FindWeaverAutoStashRef", list));
    }

    [Fact]
    public void FindWeaverAutoStashRef_Skips_Newer_Abort_Stash_On_Top()
    {
        // A weaver-abort stash pushed after Start sits at stash@{0}; the auto-stash
        // is below it and must still be found (per-line anchor keeps the message regex
        // from matching the "weaver-abort" entry).
        var list = "stash@{0}: On weaver/card1: weaver-abort\nstash@{1}: On master: weaver-auto-stash\n";
        Assert.Equal("stash@{1}", InvokeStatic<string?>("FindWeaverAutoStashRef", list));
    }

    [Fact]
    public void FindWeaverAutoStashRef_Returns_Null_Without_Auto_Stash()
    {
        Assert.Null(InvokeStatic<string?>("FindWeaverAutoStashRef", null));
        Assert.Null(InvokeStatic<string?>("FindWeaverAutoStashRef", ""));
        // Unrelated user stashes must never be popped by abort.
        Assert.Null(InvokeStatic<string?>("FindWeaverAutoStashRef", "stash@{0}: On master: WIP on settings\nstash@{1}: On master: stash-2\n"));
    }

    // ── GitService resilience (works whether or not git is installed) ────────

    [Fact]
    public async Task GetCurrentBranchAsync_Missing_Repo_Returns_Unknown()
    {
        var missing = Path.Combine(Path.GetTempPath(), "weaver-pr-tests-missing-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Equal("unknown", await new GitService().GetCurrentBranchAsync(missing));
    }

    // ── Real-git orchestration (Start/Finish on throwaway repos) ─────────────

    private static string CreateTempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-pr-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        RunGit(dir, "init");
        // Pin the initial branch name so assertions never depend on the machine's init.defaultBranch.
        RunGit(dir, "symbolic-ref HEAD refs/heads/master");
        RunGit(dir, "config user.email weaver-test@example.com");
        RunGit(dir, "config user.name weaver-test");
        return dir;
    }

    private static (int exit, string output, string error) RunGit(string dir, string args)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        p.Start();
        var outp = p.StandardOutput.ReadToEndAsync();
        var errp = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(30_000), $"git {args} timed out");
        return (p.ExitCode, outp.Result, errp.Result);
    }

    private static void CommitFile(string dir, string name, string content)
    {
        File.WriteAllText(Path.Combine(dir, name), content);
        RunGit(dir, "add -A");
        var (exit, _, err) = RunGit(dir, "commit -m \"initial\"");
        Assert.True(exit == 0, $"git commit failed: {err}");
    }

    private static async Task<JsonElement> CallStart(PRController controller, string dir, string cardId)
    {
        var result = await controller.Start(new PrStartRequest { ProjectPath = dir, CardId = cardId });
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    private static async Task<JsonElement> CallFinish(PRController controller, string dir, string branchName, string originalBranch, string? worktreePath = null)
    {
        var result = await controller.Finish(new PrFinishRequest
        {
            ProjectPath = dir,
            CardId = "card1",
            CardText = "Change",
            BranchName = branchName,
            OriginalBranch = originalBranch,
            WorktreePath = worktreePath
        });
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    private static void TryCleanup(string dir)
    {
        try { if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    // Cleans up BOTH the throwaway repo and any isolated worktree created beside it.
    private static void TryCleanupAll(params string?[] dirs)
    {
        foreach (var d in dirs) TryCleanup(d ?? "");
    }

    [GitAvailableFact]
    public async Task Start_Creates_Isolated_Worktree_And_Leaves_Shared_Checkout_Put()
    {
        var dir = CreateTempRepo();
        string? wt = null;
        try
        {
            // A real project has at least one commit — that's the case where the
            // original branch name is resolvable (on an unborn HEAD git reports
            // "unknown" for rev-parse --abbrev-ref, which the feature passes through).
            CommitFile(dir, "file.txt", "hello");

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallStart(controller, dir, "Test Card 1!");

            Assert.True(resp.GetProperty("success").GetBoolean());
            Assert.Equal("worktree", resp.GetProperty("mode").GetString());
            Assert.Equal("weaver/TestCard1", resp.GetProperty("branchName").GetString());
            Assert.Equal("master", resp.GetProperty("originalBranch").GetString());
            wt = resp.GetProperty("worktreePath").GetString();
            Assert.False(string.IsNullOrWhiteSpace(wt), "worktreePath missing");
            // The shared checkout was NEVER switched: it is still on the original branch.
            var (exit, outp, _) = RunGit(dir, "rev-parse --abbrev-ref HEAD");
            Assert.True(exit == 0 && outp.Trim() == "master", $"shared checkout moved: {outp}");
            // The new branch is checked out in the isolated worktree instead.
            var (exit2, wtOut, _) = RunGit(wt!, "rev-parse --abbrev-ref HEAD");
            Assert.True(exit2 == 0 && wtOut.Trim() == "weaver/TestCard1", $"worktree not on branch: {wtOut}");
            // Nothing was stashed — nobody's work was swept out of the shared checkout.
            Assert.Equal("", RunGit(dir, "stash list").output.Trim());
        }
        finally { TryCleanupAll(dir, wt); }
    }

    [GitAvailableFact]
    public async Task Start_On_Unborn_Head_Still_Creates_Branch_And_Reports_Unknown_Original()
    {
        // A repo with no commits: git cannot resolve the current branch name, so the
        // feature passes through "unknown" — but branch creation must still succeed
        // (in an isolated worktree when git supports it, legacy checkout otherwise).
        var dir = CreateTempRepo();
        string? wt = null;
        try
        {
            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallStart(controller, dir, "card1");

            Assert.True(resp.GetProperty("success").GetBoolean());
            Assert.Equal("unknown", resp.GetProperty("originalBranch").GetString());
            wt = resp.GetProperty("worktreePath").GetString();
            if (string.IsNullOrWhiteSpace(wt))
            {
                // Legacy fallback — the shared checkout is on the branch.
                var (exit, outp, _) = RunGit(dir, "symbolic-ref --short HEAD");
                Assert.True(exit == 0 && outp.Trim() == "weaver/card1", $"not on branch: {outp}");
            }
            else
            {
                // Worktree mode — the isolated copy holds the branch, shared stays put.
                var (exit, wtOut, _) = RunGit(wt, "symbolic-ref --short HEAD");
                Assert.True(exit == 0 && wtOut.Trim() == "weaver/card1", $"worktree not on branch: {wtOut}");
            }
        }
        finally { TryCleanupAll(dir, wt); }
    }

    [GitAvailableFact]
    public async Task Start_Leaves_Uncommitted_Changes_In_The_Shared_Checkout()
    {
        var dir = CreateTempRepo();
        string? wt = null;
        try
        {
            CommitFile(dir, "file.txt", "hello");
            // Dirty the shared working tree with a TRACKED modification.
            File.WriteAllText(Path.Combine(dir, "file.txt"), "hello v2");
            Assert.True(RunGit(dir, "status --porcelain").output.Trim().Length > 0);

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallStart(controller, dir, "card1");

            Assert.True(resp.GetProperty("success").GetBoolean());
            wt = resp.GetProperty("worktreePath").GetString();
            // The uncommitted change is STILL in the shared checkout — isolation means
            // nothing is stashed, moved or lost: other people's work is never touched.
            // (TrimEnd only: a leading space in porcelain marks an unstaged change, and
            // Trim() would eat it.)
            Assert.Equal(" M file.txt", RunGit(dir, "status --porcelain").output.TrimEnd());
            Assert.Equal("hello v2", File.ReadAllText(Path.Combine(dir, "file.txt")));
            Assert.Equal("", RunGit(dir, "stash list").output.Trim());
            // The isolated worktree checks out the committed state — clean, no leaks.
            Assert.Equal("", RunGit(wt!, "status --porcelain").output.Trim());
            Assert.Equal("hello", File.ReadAllText(Path.Combine(wt!, "file.txt")));
        }
        finally { TryCleanupAll(dir, wt); }
    }

    [GitAvailableFact]
    public async Task Start_Leaves_Untracked_Files_In_The_Shared_Checkout()
    {
        var dir = CreateTempRepo();
        string? wt = null;
        try
        {
            CommitFile(dir, "file.txt", "hello");
            // A brand-new file is untracked in the shared checkout.
            File.WriteAllText(Path.Combine(dir, "new.txt"), "brand new");
            Assert.Contains("?? new.txt", RunGit(dir, "status --porcelain").output);

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallStart(controller, dir, "card1");

            Assert.True(resp.GetProperty("success").GetBoolean());
            wt = resp.GetProperty("worktreePath").GetString();
            // The untracked file stays in the shared checkout — it never rides along
            // into the isolated worktree (where it could be committed into the branch).
            Assert.Contains("?? new.txt", RunGit(dir, "status --porcelain").output);
            Assert.True(File.Exists(Path.Combine(dir, "new.txt")), "untracked file lost");
            Assert.False(File.Exists(Path.Combine(wt!, "new.txt")), "untracked file leaked into the worktree");
        }
        finally { TryCleanupAll(dir, wt); }
    }

    [GitAvailableFact]
    public async Task Start_Falls_Back_To_Timestamped_Name_When_Branch_Exists()
    {
        var dir = CreateTempRepo();
        string? wt = null;
        try
        {
            CommitFile(dir, "file.txt", "hello");
            // Pre-create the branch the Start would pick, then get BACK on master so
            // a fresh branch must be chosen (from an unborn HEAD git would
            // happily rename onto it instead of failing).
            RunGit(dir, "checkout -b weaver/TestCard1");
            RunGit(dir, "checkout master");

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallStart(controller, dir, "Test Card 1!");

            Assert.True(resp.GetProperty("success").GetBoolean());
            Assert.Matches(@"^weaver/TestCard1-\d{14}$", resp.GetProperty("branchName").GetString());
            wt = resp.GetProperty("worktreePath").GetString();
            Assert.False(string.IsNullOrWhiteSpace(wt));
            // The pre-existing branch is the "original" the PR flow leaves untouched.
            Assert.Equal("master", resp.GetProperty("originalBranch").GetString());
        }
        finally { TryCleanupAll(dir, wt); }
    }

    private static async Task<JsonElement> CallAbort(PRController controller, string dir, string branchName, string originalBranch, string? worktreePath = null)
    {
        var result = await controller.Abort(new PrAbortRequest
        {
            ProjectPath = dir,
            CardId = "card1",
            BranchName = branchName,
            OriginalBranch = originalBranch,
            WorktreePath = worktreePath
        });
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    [GitAvailableFact]
    public async Task Abort_Removes_Worktree_Deletes_Branch_And_Leaves_Shared_Checkout_Put()
    {
        var dir = CreateTempRepo();
        string? wt = null;
        try
        {
            CommitFile(dir, "file.txt", "hello v1");

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var startResp = await CallStart(controller, dir, "card1");
            Assert.True(startResp.GetProperty("success").GetBoolean());
            var branch = startResp.GetProperty("branchName").GetString()!;
            wt = startResp.GetProperty("worktreePath").GetString();
            Assert.Equal("weaver/card1", branch);
            Assert.True(Directory.Exists(wt), "worktree folder was not created");
            // The shared checkout stayed on its original branch throughout.
            Assert.Equal("master", RunGit(dir, "rev-parse --abbrev-ref HEAD").output.Trim());

            var resp = await CallAbort(controller, dir, branch, "master", wt);

            Assert.True(resp.GetProperty("success").GetBoolean(), resp.TryGetProperty("error", out var errEl) ? errEl.GetString() : "no error");
            Assert.True(resp.GetProperty("worktreeRemoved").GetBoolean());
            Assert.True(resp.GetProperty("branchDeleted").GetBoolean());
            // The shared checkout never moved and never accumulated a stash.
            Assert.Equal("master", RunGit(dir, "rev-parse --abbrev-ref HEAD").output.Trim());
            Assert.Equal("", RunGit(dir, "stash list").output.Trim());
            // The isolated worktree folder is gone and the weaver branch is deleted.
            Assert.False(Directory.Exists(wt), "worktree folder still exists");
            Assert.Equal("", RunGit(dir, "branch --list weaver/card1").output.Trim());
            wt = null; // already removed — avoid double cleanup
        }
        finally { TryCleanupAll(dir, wt); }
    }

    [GitAvailableFact]
    public async Task Abort_Worktree_Preserves_MidRun_Changes_In_WeaverAbort_Stash()
    {
        var dir = CreateTempRepo();
        string? wt = null;
        try
        {
            CommitFile(dir, "file.txt", "hello v1");

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var startResp = await CallStart(controller, dir, "card1");
            var branch = startResp.GetProperty("branchName").GetString()!;
            wt = startResp.GetProperty("worktreePath").GetString();

            // The agent edits a file mid-run in the ISOLATED worktree — the shared
            // checkout must stay pristine while this edit is preserved on abort.
            File.WriteAllText(Path.Combine(wt!, "file.txt"), "agent mid-run edit");
            Assert.Equal("hello v1", File.ReadAllText(Path.Combine(dir, "file.txt")));

            var resp = await CallAbort(controller, dir, branch, "master", wt);

            Assert.True(resp.GetProperty("success").GetBoolean(), resp.TryGetProperty("error", out var errEl) ? errEl.GetString() : "no error");
            // The shared checkout is exactly as it was before the run.
            Assert.Equal("hello v1", File.ReadAllText(Path.Combine(dir, "file.txt")));
            // The mid-run edit is recoverable from the weaver-abort stash.
            var (exit, stashOut, _) = RunGit(dir, "stash list");
            Assert.True(exit == 0 && stashOut.Contains("weaver-abort"), $"weaver-abort stash missing: {stashOut}");
            var (exit2, showOut, _) = RunGit(dir, "stash show --name-only stash@{0}");
            Assert.True(exit2 == 0 && showOut.Contains("file.txt"), $"abort stash missing file.txt: {showOut}");
            // Worktree folder gone, branch deleted.
            Assert.False(Directory.Exists(wt), "worktree folder still exists");
            Assert.Equal("", RunGit(dir, "branch --list weaver/card1").output.Trim());
            wt = null;
        }
        finally { TryCleanupAll(dir, wt); }
    }

    [GitAvailableFact]
    public async Task Finish_Worktree_Commit_Only_Contains_Worktree_Changes()
    {
        // The core isolation guarantee: while the agent works in the isolated worktree,
        // OTHER people's changes in the shared checkout never make it into the branch
        // commit — Finish stages and commits ONLY the worktree.
        var dir = CreateTempRepo();
        string? wt = null;
        try
        {
            CommitFile(dir, "file.txt", "hello");

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var startResp = await CallStart(controller, dir, "card1");
            wt = startResp.GetProperty("worktreePath").GetString();
            var branch = startResp.GetProperty("branchName").GetString()!;

            // Another person edits the SHARED checkout while the agent works.
            File.WriteAllText(Path.Combine(dir, "file.txt"), "other person's edit");
            File.WriteAllText(Path.Combine(dir, "theirs.txt"), "their new file");
            // The agent edits the ISOLATED worktree.
            File.WriteAllText(Path.Combine(wt!, "file.txt"), "agent's edit");
            File.WriteAllText(Path.Combine(wt!, "agent.txt"), "agent file");

            var resp = await CallFinish(controller, dir, branch, "master", wt);

            // No origin remote → push fails; success false and the worktree + branch are
            // deliberately kept for retry (nothing is deleted on a failed push).
            Assert.False(resp.GetProperty("success").GetBoolean());
            Assert.Contains("origin", resp.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(wt), "worktree must be kept for retry after a failed push");

            // The branch commit (in the worktree) contains ONLY the agent's changes.
            var (exit, logOut, logErr) = RunGit(wt!, "log --oneline -1 --stat");
            Assert.True(exit == 0, $"git log failed: {logErr}");
            Assert.Contains("agent.txt", logOut);
            Assert.Contains("file.txt", logOut);
            Assert.DoesNotContain("theirs.txt", logOut);

            // The other person's work is still in the shared checkout, uncommitted,
            // on the original branch — never staged, never committed, never moved.
            var (exit2, statusOut, _) = RunGit(dir, "status --porcelain");
            Assert.True(exit2 == 0);
            Assert.Contains("theirs.txt", statusOut);
            Assert.Contains(" M file.txt", statusOut);
            Assert.Equal("master", RunGit(dir, "rev-parse --abbrev-ref HEAD").output.Trim());
        }
        finally { TryCleanupAll(dir, wt); }
    }

    [GitAvailableFact]
    public async Task Abort_When_Already_Back_On_Original_Still_Deletes_Branch()
    {
        var dir = CreateTempRepo();
        try
        {
            CommitFile(dir, "file.txt", "hello");
            RunGit(dir, "checkout -b weaver/card1");
            RunGit(dir, "checkout master"); // already restored, branch left behind

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallAbort(controller, dir, "weaver/card1", "master");

            Assert.True(resp.GetProperty("success").GetBoolean(), resp.TryGetProperty("error", out var errEl) ? errEl.GetString() : "no error");
            Assert.Equal("master", resp.GetProperty("restoredBranch").GetString());
            Assert.Equal("", RunGit(dir, "branch --list weaver/card1").output.Trim());
        }
        finally { TryCleanup(dir); }
    }

    [GitAvailableFact]
    public async Task Abort_When_Branch_Already_Gone_Reports_Success()
    {
        var dir = CreateTempRepo();
        try
        {
            CommitFile(dir, "file.txt", "hello");
            // Branch was already deleted elsewhere — abort's end state is already true.
            RunGit(dir, "checkout -b weaver/card1");
            RunGit(dir, "checkout master");
            RunGit(dir, "branch -D weaver/card1");

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallAbort(controller, dir, "weaver/card1", "master");

            Assert.True(resp.GetProperty("success").GetBoolean(), resp.TryGetProperty("error", out var errEl) ? errEl.GetString() : "no error");
            Assert.True(resp.GetProperty("branchDeleted").GetBoolean());
        }
        finally { TryCleanup(dir); }
    }

    [GitAvailableFact]
    public async Task Finish_Without_Remote_Reports_Push_Failure()
    {
        var dir = CreateTempRepo();
        try
        {
            CommitFile(dir, "file.txt", "hello");
            RunGit(dir, "checkout -b weaver/TestCard1");

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallFinish(controller, dir, "weaver/TestCard1", "master");

            // Pushing to a nonexistent 'origin' must fail before gh is ever invoked,
            // and the response carries the push error + branch name for the client.
            Assert.False(resp.GetProperty("success").GetBoolean());
            var error = resp.GetProperty("error").GetString() ?? "";
            Assert.Contains("origin", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("weaver/TestCard1", resp.GetProperty("branchName").GetString());
        }
        finally { TryCleanup(dir); }
    }
}

/// <summary>
/// xUnit 2.x has no built-in runtime skip, so a Fact subclass checks for git on
/// PATH and marks itself skipped when git is absent — the git-backed BRANCH
/// tests then degrade gracefully on machines without git instead of failing
/// the whole suite.
/// </summary>
public sealed class GitAvailableFactAttribute : FactAttribute
{
    public GitAvailableFactAttribute()
    {
        if (!GitOnPath()) Skip = "git executable not found on PATH — skipping git-backed BRANCH feature test";
    }

    private static bool GitOnPath()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "git.exe" } : new[] { "git" };
        var dirs = new List<string> { Environment.CurrentDirectory };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            foreach (var name in names)
            {
                if (File.Exists(Path.Combine(dir.Trim(), name))) return true;
            }
        }
        return false;
    }
}
