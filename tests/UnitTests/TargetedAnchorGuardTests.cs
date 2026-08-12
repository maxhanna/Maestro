using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the TARGETED-ANCHOR GUARD in ValidateIncrementalStepAsync. Came out of the
/// "group benchmarks by benchmark name" run: the planner emitted a ~30-line oldString/
/// newString pair reproducing the whole benchmarks-section block, the verbatim match
/// drifted at apply time, and the resolver's FORMAT D prompt then demanded the model
/// reproduce the same wall of text again. The guard rejects oversized anchors
/// deterministically (mirroring GetPlanSizeViolations: &gt;10 lines or &gt;400 chars)
/// and teaches the targeted-replace pattern so the planner emits a small unique anchor.
/// </summary>
public class TargetedAnchorGuardTests
{
    private static readonly MethodInfo ValidateMethod = typeof(AgentController).GetMethod(
        "ValidateIncrementalStepAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Runs the REAL private validator on an uninitialized controller — the early
    /// guard paths are pure (static helpers only), so no DI/state is needed. The step's target
    /// file is materialized as an empty file in a temp project root: the validator resolves
    /// relative paths against the project root (which cannot be the test host's CWD), and the
    /// invented-file guard rejects edits to paths that do not exist — so this guard's tests
    /// must target files the sandbox contains.</summary>
    private static (bool valid, string? reason) Validate(PlanStep step, string prompt, bool skipLlm = false, List<string>? attachedFiles = null)
    {
        var controller = RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        var projectRoot = MaterializeSandbox(step.File);
        try
        {
            var task = (Task<(bool valid, string? reason)>)ValidateMethod.Invoke(controller, new object?[]
            {
                step, prompt, /*discoveryContext*/ "", /*planSoFar*/ new List<PlanStep>(),
                /*projectRoot*/ projectRoot, /*emitSse*/ false, CancellationToken.None, /*skipLlm*/ skipLlm, /*lastStepCompletionNote*/ null, /*attachedFiles*/ attachedFiles
            })!;
            var result = task.GetAwaiter().GetResult();
            return (result.valid, result.reason);
        }
        finally
        {
            try { Directory.Delete(projectRoot, true); } catch { }
        }
    }

    /// <summary>Creates a temp project root containing the step's target file (as an empty
    /// file) so the file-existence gate in the validator sees a real file and the guard under
    /// test — the anchor-size guard — is what decides.</summary>
    private static string MaterializeSandbox(string? targetFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_anchor_guard_" + Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrWhiteSpace(targetFile))
        {
            var full = Path.GetFullPath(Path.Combine(root, targetFile.Replace('/', Path.DirectorySeparatorChar)));
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "");
            }
        }
        return root;
    }

    private static string HugeBlock(int lines)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines; i++)
            sb.AppendLine($"    <div class=\"benchmark-item\" data-idx=\"{i}\">some benchmark row content that pads the line out</div>");
        return sb.ToString().TrimEnd('\r', '\n');
    }

    // ── oversized oldString is rejected deterministically ───────────────────

    [Fact]
    public void HugeOldString_30Lines_IsRejected()
    {
        // The exact shape from the failing run: a whole section block as oldString.
        var step = new PlanStep
        {
            File = "src/app/weaver/weaver.component.html",
            Change = "Group benchmarks by benchmark name",
            OldString = HugeBlock(30),
            NewString = HugeBlock(30)
        };
        var (valid, reason) = Validate(step, "Group benchmarks by benchmark name in the weaver component");
        Assert.False(valid);
        Assert.Contains("WAY too large", reason);
        Assert.Contains("TARGETED REPLACE", reason);
        Assert.Contains("1-3 lines", reason);
    }

    [Fact]
    public void HugeOldString_Over400Chars_FewLines_IsRejected()
    {
        // Few lines but over the 400-char ceiling — still an unreliable anchor.
        var longLine = new string('x', 500);
        var step = new PlanStep
        {
            File = "src/Login.ts",
            Change = "Update the login handler",
            OldString = longLine,
            NewString = "replacement"
        };
        var (valid, reason) = Validate(step, "Update the login handler");
        Assert.False(valid);
        Assert.Contains("WAY too large", reason);
    }

    [Fact]
    public void HugeOldString_StillRejectedInRetryMode()
    {
        // skipLlm=true (retry mode) must NOT bypass the deterministic anchor guard —
        // same contract as the OS-task / _command guards that run before the LLM validator.
        var step = new PlanStep
        {
            File = "src/app/weaver/weaver.component.html",
            Change = "Group benchmarks by benchmark name",
            OldString = HugeBlock(20),
            NewString = HugeBlock(20)
        };
        var (valid, reason) = Validate(step, "Group benchmarks", skipLlm: true);
        Assert.False(valid);
        Assert.Contains("WAY too large", reason);
    }

    [Fact]
    public void HugeOldString_DeletionChange_IsRejected()
    {
        // A huge oldString is just as unreliable for a deletion — the targeted pattern
        // applies there too (oldString = the exact 1-3 lines being deleted).
        var step = new PlanStep
        {
            File = "src/app/weaver/weaver.component.html",
            Change = "Remove the benchmarks section",
            OldString = HugeBlock(25),
            NewString = ""
        };
        var (valid, reason) = Validate(step, "Remove the benchmarks section from the weaver component");
        Assert.False(valid);
        Assert.Contains("WAY too large", reason);
    }

    // ── small anchors are NOT rejected by this guard ────────────────────────

    [Fact]
    public void SmallOldString_TwoLines_NotRejectedByAnchorGuard()
    {
        // RULE 17-compliant anchor passes the guard (step accepted on the skipLlm path
        // once all deterministic guards clear — proving it's THIS guard, not a later one).
        var step = new PlanStep
        {
            File = "src/Login.ts",
            Change = "Add escape key handler",
            OldString = "  await this.loadRecipes();\n  this.registerEscapeHandler();",
            NewString = "  await this.loadRecipes();\n  this.registerEscapeHandler();\n  this.registerCloseOnEsc();"
        };
        var (valid, reason) = Validate(step, "Add an escape key handler to the login component", skipLlm: true);
        Assert.True(valid, reason);
    }

    [Fact]
    public void SmallOldString_OneUniqueLine_NotRejectedByAnchorGuard()
    {
        // The ideal targeted-replace shape from the failure report: one unique line as
        // the anchor, replacement carries the anchor line unchanged plus new lines.
        var step = new PlanStep
        {
            File = "src/app/weaver/weaver.component.html",
            Change = "Group benchmarks by benchmark name",
            OldString = "<div *ngFor=\"let b of benchmarks\" class=\"benchmark-item\">",
            NewString = "<div *ngFor=\"let group of groupedBenchmarks | keyvalue\" class=\"benchmark-group\">\n<h3>{{ group.key }}</h3>\n<div *ngFor=\"let b of group.value\" class=\"benchmark-item\">",
            LineNumber = 42
        };
        var (valid, reason) = Validate(step, "Group benchmarks by benchmark name", skipLlm: true);
        Assert.True(valid, reason);
    }

    [Fact]
    public void EmptyOldString_NotRejectedByAnchorGuard()
    {
        // New-file / append steps have no oldString — the guard must not fire.
        var step = new PlanStep
        {
            File = "src/new-file.ts",
            Change = "Create a new config file",
            OldString = null,
            NewString = "export const config = {};"
        };
        var (valid, reason) = Validate(step, "Create a new config file", skipLlm: true);
        Assert.True(valid, reason);
    }
}
