using System.Reflection;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the AVAILABLE STEPS section of the incremental planner's system prompt: every enabled
/// step marker appears WITH its tool description (e.g. _command → "Run a terminal command",
/// _web_search → "Search the web"), so the planner reasons about the tool calls it can directly
/// make instead of drifting into "which class method do I call" and inventing application code
/// (the classic Selenium/Python drift on web/OS tasks). Disabled tools are omitted, and the
/// edit-only mode (path-only steps, no tools) gets no section.
/// </summary>
public class IncrementalStepToolsPromptTests
{
    private static readonly MethodInfo BuildPromptMethod = typeof(AgentController).GetMethod(
        "BuildIncrementalStepSystemPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string Build(string stepMode = "all", List<string>? enabledTools = null)
        => (string)BuildPromptMethod.Invoke(null, new object[] { stepMode, enabledTools, null })!;

    [Fact]
    public void SystemPrompt_IncludesAvailableSteps_WithToolDescriptions()
    {
        var prompt = Build("all", new List<string> { "_command", "_web_search", "_web_fetch" });
        Assert.Contains("### AVAILABLE STEPS", prompt);
        Assert.Contains("Run a terminal command", prompt);   // _command description
        Assert.Contains("Search the web", prompt);            // _web_search description
        Assert.Contains("Fetch a URL", prompt);               // _web_fetch description
    }

    [Fact]
    public void SystemPrompt_DisabledTools_AreOmitted()
    {
        var prompt = Build("all", new List<string> { "_command" });
        Assert.Contains("Run a terminal command", prompt);
        Assert.DoesNotContain("Search the web", prompt);
        Assert.DoesNotContain("Fetch a URL", prompt);
        Assert.DoesNotContain("\"_web_search\"", prompt);
    }

    [Fact]
    public void SystemPrompt_NullEnabledTools_ListsAllToolDescriptions()
    {
        var prompt = Build("all");
        Assert.Contains("### AVAILABLE STEPS", prompt);
        Assert.Contains("Run a terminal command", prompt);
        Assert.Contains("Search the web", prompt);
        Assert.Contains("Fetch a URL", prompt);
        Assert.Contains("\"_checkpoint\"", prompt);
    }

    [Fact]
    public void SystemPrompt_EditMode_HasNoToolSection()
    {
        // Edit-only mode proposes path-only steps — the tool surface is irrelevant there.
        Assert.DoesNotContain("### AVAILABLE STEPS", Build("edit"));
    }
}
