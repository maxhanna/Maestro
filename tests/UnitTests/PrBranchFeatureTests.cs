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

    private static async Task<JsonElement> CallFinish(PRController controller, string dir, string branchName, string originalBranch)
    {
        var result = await controller.Finish(new PrFinishRequest
        {
            ProjectPath = dir,
            CardId = "card1",
            CardText = "Change",
            BranchName = branchName,
            OriginalBranch = originalBranch
        });
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    private static void TryCleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    [GitAvailableFact]
    public async Task Start_Creates_Weaver_Branch_On_Fresh_Repo()
    {
        var dir = CreateTempRepo();
        try
        {
            // A real project has at least one commit — that's the case where the
            // original branch name is resolvable (on an unborn HEAD git reports
            // "unknown" for rev-parse --abbrev-ref, which the feature passes through).
            CommitFile(dir, "file.txt", "hello");

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallStart(controller, dir, "Test Card 1!");

            Assert.True(resp.GetProperty("success").GetBoolean());
            Assert.Equal("weaver/TestCard1", resp.GetProperty("branchName").GetString());
            Assert.Equal("master", resp.GetProperty("originalBranch").GetString());
            // The repo is actually on the new branch.
            var (exit, outp, _) = RunGit(dir, "rev-parse --abbrev-ref HEAD");
            Assert.True(exit == 0 && outp.Trim() == "weaver/TestCard1", $"not on branch: {outp}");
        }
        finally { TryCleanup(dir); }
    }

    [GitAvailableFact]
    public async Task Start_On_Unborn_Head_Still_Creates_Branch_And_Reports_Unknown_Original()
    {
        // A repo with no commits: git cannot resolve the current branch name, so the
        // feature passes through "unknown" — but branch creation must still succeed.
        var dir = CreateTempRepo();
        try
        {
            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallStart(controller, dir, "card1");

            Assert.True(resp.GetProperty("success").GetBoolean());
            Assert.Equal("unknown", resp.GetProperty("originalBranch").GetString());
            // rev-parse can't resolve an unborn HEAD, so read the symref directly.
            var (exit, outp, _) = RunGit(dir, "symbolic-ref --short HEAD");
            Assert.True(exit == 0 && outp.Trim() == "weaver/card1", $"not on branch: {outp}");
        }
        finally { TryCleanup(dir); }
    }

    [GitAvailableFact]
    public async Task Start_Stashes_Uncommitted_Changes_Before_Branching()
    {
        var dir = CreateTempRepo();
        try
        {
            CommitFile(dir, "file.txt", "hello");
            // Dirty the working tree with a TRACKED modification so the stash has something to hold.
            File.WriteAllText(Path.Combine(dir, "file.txt"), "hello v2");
            Assert.True(RunGit(dir, "status --porcelain").output.Trim().Length > 0);

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallStart(controller, dir, "card1");

            Assert.True(resp.GetProperty("success").GetBoolean());
            var (exit, stashOut, _) = RunGit(dir, "stash list");
            Assert.True(exit == 0 && stashOut.Contains("weaver-auto-stash"), $"stash not found: {stashOut}");
            // The tracked modification is actually captured in the stash — not just an entry existing.
            var (exit2, showOut, _) = RunGit(dir, "stash show --name-only stash@{0}");
            Assert.True(exit2 == 0 && showOut.Contains("file.txt"), $"stash missing file.txt: {showOut}");
        }
        finally { TryCleanup(dir); }
    }

    [GitAvailableFact]
    public async Task Start_Stashes_Untracked_Files_So_Nothing_Is_Lost()
    {
        var dir = CreateTempRepo();
        try
        {
            CommitFile(dir, "file.txt", "hello");
            // A brand-new file is untracked — plain `stash push` would leave it in the
            // working tree to ride along onto the branch; the -u stash must fold it in.
            File.WriteAllText(Path.Combine(dir, "new.txt"), "brand new");
            Assert.Contains("?? new.txt", RunGit(dir, "status --porcelain").output);

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallStart(controller, dir, "card1");

            Assert.True(resp.GetProperty("success").GetBoolean());
            // Working tree is clean after the branch is created — nothing leaked onto it.
            Assert.Equal("", RunGit(dir, "status --porcelain").output.Trim());
            // The stash actually holds the untracked file.
            var (exit, showOut, _) = RunGit(dir, "stash show -u --name-only stash@{0}");
            Assert.True(exit == 0 && showOut.Contains("new.txt"), $"stash missing new.txt: {showOut}");
            // And popping the stash restores it — nothing is lost.
            var pop = RunGit(dir, "stash pop");
            Assert.True(pop.exit == 0, $"stash pop failed: {pop.error}");
            Assert.True(File.Exists(Path.Combine(dir, "new.txt")), "untracked file lost after stash pop");
        }
        finally { TryCleanup(dir); }
    }

    [GitAvailableFact]
    public async Task Start_Falls_Back_To_Timestamped_Name_When_Branch_Exists()
    {
        var dir = CreateTempRepo();
        try
        {
            CommitFile(dir, "file.txt", "hello");
            // Pre-create the branch the Start would pick, then get BACK on master so
            // the first checkout -b actually collides (from an unborn HEAD git would
            // happily rename onto it instead of failing).
            RunGit(dir, "checkout -b weaver/TestCard1");
            RunGit(dir, "checkout master");

            var controller = new PRController(new GitService(), NullLogger<PRController>.Instance);
            var resp = await CallStart(controller, dir, "Test Card 1!");

            Assert.True(resp.GetProperty("success").GetBoolean());
            Assert.Matches(@"^weaver/TestCard1-\d{14}$", resp.GetProperty("branchName").GetString());
            // The pre-existing branch is the "original" the PR flow must restore.
            Assert.Equal("master", resp.GetProperty("originalBranch").GetString());
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
