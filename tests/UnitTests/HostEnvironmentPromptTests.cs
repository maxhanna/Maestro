using System.Reflection;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the HOST ENVIRONMENT block in the incremental planner's system prompt: OS name and
/// the real desktop path are included for ALL tasks (not just OS-filesystem ones), so a repo
/// task that touches an absolute path doesn't make the model assume Linux paths like
/// /home/user/... on a Windows host.
/// </summary>
public class HostEnvironmentPromptTests
{
    private static readonly MethodInfo BuildPromptMethod = typeof(AgentController).GetMethod(
        "BuildIncrementalStepSystemPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string Build(string stepMode = "all")
        => (string)BuildPromptMethod.Invoke(null, new object[] { stepMode, null, null })!;

    private static string ExpectedOsName()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsMacOS()) return "macOS";
        if (OperatingSystem.IsLinux()) return "Linux";
        return Environment.OSVersion.ToString()!;
    }

    [Fact]
    public void SystemPrompt_IncludesHostEnvironmentForAllTasks()
    {
        var prompt = Build("all");
        Assert.Contains("### HOST ENVIRONMENT ###", prompt);
        Assert.Contains("You are running on " + ExpectedOsName(), prompt);
    }

    [Fact]
    public void SystemPrompt_IncludesRealDesktopPath()
    {
        var prompt = Build("all");
        // The section must always be present — even a headless host falls back to a usable
        // anchor (UserProfile/Desktop or $HOME/Desktop) instead of an empty string.
        Assert.Contains("desktop directory is:", prompt);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrWhiteSpace(desktop))
            Assert.Contains(desktop, prompt);
    }

    [Fact]
    public void SystemPrompt_DesktopPathIsNeverEmpty()
    {
        // The fallback chain must guarantee a non-empty anchor on every host.
        var prompt = Build("all");
        Assert.Contains("desktop directory is: \"", prompt);
        Assert.DoesNotContain("desktop directory is: \"\"", prompt);
    }

    [Fact]
    public void SystemPrompt_GuardsAgainstInventingUnixPaths()
    {
        // The anti-hallucination instruction must be present regardless of step mode.
        Assert.Contains("NEVER invent a Unix-style path", Build("all"));
        Assert.Contains("NEVER invent a Unix-style path", Build("edit"));
        Assert.Contains("NEVER invent a Unix-style path", Build("command"));
    }

    [Fact]
    public void SystemPrompt_StatesPathStyleForCurrentOs()
    {
        var prompt = Build("all");
        if (OperatingSystem.IsWindows())
            Assert.Contains("backslashes and drive letters", prompt);
        else
            Assert.Contains("forward slashes", prompt);
    }
}
