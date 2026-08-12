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
        => (string)BuildPromptMethod.Invoke(null, new object?[] { stepMode, enabledTools, null })!;

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

    [Fact]
    public void SystemPrompt_WebChainExample_Included_WhenWebSearchEnabled()
    {
        var prompt = Build("all", new List<string> { "_command", "_web_search", "_web_fetch" });
        Assert.Contains("### TOOL USE EXAMPLE", prompt);
        // The complete chain must be visible end to end: search → fetch → write to disk.
        Assert.Contains("\"file\":\"_web_search\"", prompt);
        Assert.Contains("\"file\":\"_web_fetch\"", prompt);
        Assert.Contains("\"file\":\"_command\"", prompt);
        // The write step of the chain is taught in the host's shell: Set-Content on
        // Windows, a bash echo redirect on Unix hosts.
        if (OperatingSystem.IsWindows())
            Assert.Contains("Set-Content -Path", prompt);
        else
            Assert.Contains("> \"<desktop-path>", prompt);
        Assert.Contains("declare planComplete only after the file is written", prompt);
    }

    [Fact]
    public void SystemPrompt_WebChainExample_Omitted_WhenWebSearchDisabled()
    {
        // The chain starts with _web_search — if the classifier excluded it, showing a web
        // chain would push the model toward a tool it doesn't have.
        var prompt = Build("all", new List<string> { "_command" });
        Assert.DoesNotContain("### TOOL USE EXAMPLE", prompt);
        Assert.DoesNotContain("Set-Content -Path", prompt);
    }

    [Fact]
    public void SystemPrompt_WebChainExample_NeverTeachesUrlInvention()
    {
        // Regression: the tool-use example previously showed a concrete fake URL
        // (https://example.com/ai-article) as the step-2 _web_fetch target — and the model
        // copied the pattern, inventing "www.example.com/latest-ai-breakthrough" in the field
        // (the exact failure in the web-task run). The example must demand a REAL URL copied
        // verbatim from the search results and never present a fetchable invented URL.
        var prompt = Build("all", new List<string> { "_command", "_web_search", "_web_fetch" });
        Assert.Contains("### TOOL USE EXAMPLE", prompt);
        Assert.Contains("copy it verbatim, NEVER invent one", prompt);
        Assert.Contains("Inventing a URL (e.g. www.example.com/...)", prompt);
        // The example must not contain a fetchable invented URL pattern that a model could
        // lift into its own step (the exact trap the field failure exposed).
        Assert.DoesNotContain("example.com/ai-article", prompt);
        Assert.DoesNotContain("example.com/api", prompt);
        Assert.DoesNotContain("\"_web_fetch\",\"change\":\"https://example.com", prompt);
    }

    [Fact]
    public void SystemPrompt_EditMode_HasNoWebChainExample()
    {
        Assert.DoesNotContain("### TOOL USE EXAMPLE", Build("edit"));
        Assert.DoesNotContain("Set-Content -Path", Build("edit"));
    }
}
