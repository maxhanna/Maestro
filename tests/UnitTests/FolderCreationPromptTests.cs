using System.Reflection;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the FOLDER-CREATION guidance in every planner prompt (the benchmark-23
/// "planner plans a placeholder file instead of a directory" regression): a weak model
/// that cannot create a folder directly plans a dummy file (<c>benchmark_test_23/placeholder.txt</c>
/// containing "Placeholder file for directory creation") or a <c>_create_directory</c> step
/// whose path is a file placeholder — both are converted to the directory by the executor,
/// but planning <c>_create_directory</c> directly is always correct. Every prompt that can
/// emit a step must tell the model: when the task needs a folder, plan a <c>_create_directory</c>
/// step with the relative folder path — never a placeholder file.
/// </summary>
public class FolderCreationPromptTests
{
    private static string Invoke(string methodName, Type[] paramTypes, params object?[] args)
        => (string)typeof(AgentController).GetMethod(
                methodName, BindingFlags.NonPublic | BindingFlags.Static, null, paramTypes, null)!
            .Invoke(null, args)!;

    private static void AssertTrainsDirectoryOverPlaceholderFile(string prompt)
    {
        Assert.Contains("_create_directory", prompt);
        Assert.Contains("placeholder", prompt);
        Assert.Contains("materialize the folder", prompt);
        Assert.Contains("NEVER", prompt);
    }

    [Fact]
    public void IncrementalPlanner_TrainsDirectoryOverPlaceholderFile()
    {
        // The atomic per-step planner (the route benchmark runs use) must not plan a
        // placeholder file just to get the folder on disk.
        var prompt = Invoke("BuildIncrementalStepSystemPrompt",
            new[] { typeof(string), typeof(List<string>), typeof(int?) }, "all", new List<string>(), (int?)null);
        AssertTrainsDirectoryOverPlaceholderFile(prompt);
        Assert.Contains("plan _create_directory directly", prompt);
        Assert.Contains("placeholder.txt", prompt);
    }

    [Fact]
    public void PlanningPrompt_TrainsDirectoryOverPlaceholderFile()
    {
        var prompt = Invoke("BuildPlanningPrompt", new[] { typeof(List<string>) }, (List<string>?)null);
        AssertTrainsDirectoryOverPlaceholderFile(prompt);
    }

    [Fact]
    public void ReplanPrompt_TrainsDirectoryOverPlaceholderFile()
    {
        // The repair loop must also plan _create_directory, not a placeholder file, when a
        // repair pass needs a folder.
        var prompt = Invoke("BuildReplanPrompt", new[] { typeof(string), typeof(List<string>), typeof(string), typeof(AgentPlan), typeof(List<object>), typeof(string), typeof(string), typeof(string) },
            "original task", new List<string>(), null, null, null, "", "", null);
        AssertTrainsDirectoryOverPlaceholderFile(prompt);
    }

    [Fact]
    public void IncrementalSubPlanSystemPrompt_TrainsDirectoryOverPlaceholderFile()
    {
        var prompt = Invoke("BuildIncrementalSubPlanSystemPrompt", Type.EmptyTypes);
        AssertTrainsDirectoryOverPlaceholderFile(prompt);
    }

    [Fact]
    public void PlanningPrompt_ToolDescriptions_CarryPlaceholderFileWarning()
    {
        // The _create_directory/_create_file tool descriptions embedded in the planner's STEP
        // TYPES section carry the same warning, so the guidance is visible at the tool surface
        // itself, not just in the rules.
        var prompt = Invoke("BuildPlanningPrompt", new[] { typeof(List<string>) }, (List<string>?)null);
        Assert.Contains("_create_directory", prompt);
        Assert.Contains("plan a placeholder file", prompt);
        Assert.Contains("plan a _create_directory step instead", prompt);
    }
}
