using System.Reflection;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the nbsp-for-literal-space guidance in the planner and editor prompts (the
/// benchmark-23 "heading is always 'Benchmark23' instead of 'Benchmark 23'" regression):
/// the weak model keeps DROPPING the raw space inside a required heading, so every prompt
/// that can emit file content must tell it to write the space as `&nbsp;` — which the apply
/// pipeline then converts back to a REAL space deterministically
/// (AgentTextUtilities.NormalizeNbsp / NormalizeNbspInStep).
/// </summary>
public class NbspHeadingPromptTests
{
    private static string Invoke(string methodName, Type[] paramTypes, params object?[] args)
        => (string)typeof(AgentController).GetMethod(
                methodName, BindingFlags.NonPublic | BindingFlags.Static, null, paramTypes, null)!
            .Invoke(null, args)!;

    [Fact]
    public void EditSystemPrompt_TrainsNbspForLiteralHeadingSpaces()
    {
        var prompt = Invoke("BuildEditSystemPrompt", new[] { typeof(string) }, "old_new");
        Assert.Contains("&nbsp;", prompt);
        Assert.Contains("Benchmark 23", prompt);
        Assert.Contains("never drop the space", prompt);
        Assert.Contains("converted to a", prompt);
    }

    [Fact]
    public void FullFileSystemPrompt_TrainsNbspForLiteralHeadingSpaces()
    {
        var prompt = Invoke("BuildFullFileSystemPrompt", Type.EmptyTypes);
        Assert.Contains("&nbsp;", prompt);
        Assert.Contains("never drop the space", prompt);
    }

    [Fact]
    public void IncrementalPlannerPrompt_TrainsNbspForLiteralHeadingSpaces()
    {
        var prompt = Invoke("BuildIncrementalStepSystemPrompt",
            new[] { typeof(string), typeof(List<string>), typeof(int?) }, "all", new List<string>(), (int?)null);
        Assert.Contains("&nbsp;", prompt);
        Assert.Contains("never drop the space", prompt);
    }

    [Fact]
    public void PlanningPrompt_TrainsNbspForLiteralHeadingSpaces()
    {
        var prompt = Invoke("BuildPlanningPrompt", new[] { typeof(List<string>) }, (List<string>?)null);
        Assert.Contains("&nbsp;", prompt);
        Assert.Contains("never drop the space", prompt);
    }

    [Fact]
    public void ReplanPrompt_TrainsNbspForLiteralHeadingSpaces()
    {
        // The repair-loop prompt is the key gap: a repair that REWRITES the <h1> must not
        // re-merge 'Benchmark 23' into 'Benchmark23' in its newString.
        var prompt = Invoke("BuildReplanPrompt", new[] { typeof(string), typeof(List<string>), typeof(string), typeof(AgentPlan), typeof(List<object>), typeof(string), typeof(string), typeof(string) },
            "original task", new List<string>(), null, null, null, "", "", null);
        Assert.Contains("&nbsp;", prompt);
        Assert.Contains("never drop the space", prompt);
    }

    [Fact]
    public void IncrementalSubPlanSystemPrompt_TrainsNbspForLiteralHeadingSpaces()
    {
        var prompt = Invoke("BuildIncrementalSubPlanSystemPrompt", Type.EmptyTypes);
        Assert.Contains("&nbsp;", prompt);
        Assert.Contains("never drop the space", prompt);
    }

    [Fact]
    public void VerifyEditUserPrompt_AcceptsNbspNormalizedToRealSpace()
    {
        // Regression for the run where the repair DID produce 'Benchmark&nbsp;23', the apply
        // pipeline normalized it to a REAL space (correct!), and then the LLM VERIFIER rejected
        // the edit because it saw a real space where the step description wrote '&nbsp;' —
        // reverting the correct fix. The verifier must know that real space IS the intended
        // end state when the step used the &nbsp; stand-in.
        var prompt = Invoke("BuildVerifyEditUserPrompt", Type.EmptyTypes);
        Assert.Contains("&nbsp;", prompt);
        Assert.Contains("real space", prompt);
        Assert.Contains("intended end state", prompt);
        Assert.Contains("Do NOT abandon an edit", prompt);
    }
}
