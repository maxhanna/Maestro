using System.Reflection;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the OS-filesystem discovery gate (Controllers/AgentController.Discovery.cs).
/// IsExternalFilesystemTask detects tasks that operate on the OS filesystem OUTSIDE
/// the repository (desktop/home/Downloads/etc.) so discovery can skip the BM25
/// auto-read — pulling repo files into context for such tasks only anchors the
/// planner on unrelated code (the "wrote an HTTP endpoint to create a desktop
/// folder" failure). It must fire only when BOTH an OS location word AND a
/// filesystem action word are present, so repo-internal phrasing never trips it.
/// </summary>
public class ExternalFilesystemTaskTests
{
    private static readonly MethodInfo Method = typeof(Weaver.Controllers.AgentController)
        .GetMethod("IsExternalFilesystemTask", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("IsExternalFilesystemTask static method not found.");

    private static bool IsOs(string? p) => (bool)(Method.Invoke(null, new object?[] { p }) ?? false);

    [Theory]
    [InlineData("Create a 'Daily Post' folder on the desk")]
    [InlineData("Create a new folder on my desktop called scratch")]
    [InlineData("Delete the Downloads folder")]
    [InlineData("Rename the file report.txt in Documents")]
    [InlineData("Make a directory in the home folder called backups")]
    [InlineData("Move the screenshots folder into Pictures")]
    [InlineData("Clear the temp folder")]
    [InlineData("Create a folder at C:\\Users\\me\\Desktop\\Daily Post")]
    [InlineData("Create a shortcut on the desktop")]
    // Absolute Unix paths (the shape the Linux CI temp dirs use) must trip the gate
    // exactly like a Windows drive path does.
    [InlineData("Check the latest release online and save the version to a file at /tmp/weaver_toolsel_abc/proj/release-version.txt")]
    [InlineData("Write the data into a text file at /tmp/weaver_webtask_abc/dump2/report.txt")]
    public void DetectsExternalFilesystemTasks(string prompt)
    {
        Assert.True(IsOs(prompt));
    }

    // ── OsPromptHintsRepoWork (decides if an OS task pays for LLM adjudication) ──

    private static readonly MethodInfo HintsRepoMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("OsPromptHintsRepoWork", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("OsPromptHintsRepoWork static method not found.");

    private static bool HintsRepo(string? p) => (bool)(HintsRepoMethod.Invoke(null, new object?[] { p }) ?? false);

    [Theory]
    [InlineData("Create a folder on the desk and save a link in the README")]
    [InlineData("Make a Downloads folder and add a note about it in the docs")]
    [InlineData("Create a folder on the desktop, then mention it in the readme")]
    public void OsPromptHintsRepoWork_DetectsRepoMention(string prompt)
    {
        Assert.True(HintsRepo(prompt));
    }

    [Theory]
    [InlineData("Create a 'Daily Post' folder on the desk")]
    [InlineData("Delete the Downloads folder")]
    [InlineData("Clear the temp folder")]
    [InlineData(null)]
    [InlineData("")]
    public void OsPromptHintsRepoWork_PureOsTaskDoesNotHint(string? prompt)
    {
        Assert.False(HintsRepo(prompt));
    }

    // OS phrasing plus a repo/project reference means the "desktop folder" is
    // repo-relative — never an OS task (the _discover short-circuit makes a
    // misclassification costly, so the detector must stay conservative here).
    [Theory]
    [InlineData("Fix the bug in the desktop folder")]
    [InlineData("Read the documents folder in the project")]
    [InlineData("Create the desktop folder at the project root")]
    [InlineData("Move the file to the desktop folder in src")]
    public void BareLocationWordsWithRepoContextDoNotFire(string prompt)
    {
        Assert.False(IsOs(prompt));
    }

    [Theory]
    [InlineData("Create a folder called benchmark_1 at the project root")]
    [InlineData("Search the repo for the desktop component and explain it")]
    [InlineData("Fix the desktop app build")]
    [InlineData("Refactor the login component")]
    [InlineData("Add tests for the file picker")]
    [InlineData("")]
    [InlineData(null)]
    public void IgnoresRepoInternalTasks(string? prompt)
    {
        Assert.False(IsOs(prompt));
    }

    // Plain action verbs are NOT enough — the thing being manipulated must be a
    // filesystem artifact. "Create a desktop app" is repo work, not an OS task,
    // and tripping here would wrongly block discovery for a real code task.
    [Theory]
    [InlineData("Create a desktop app for the login page")]
    [InlineData("Build a desktop client that talks to the API")]
    [InlineData("Fix the desktop file picker")]
    [InlineData("The desktop UI shows the wrong color")]
    [InlineData("Improve the desktop layout responsiveness")]
    public void LocationWithoutFilesystemArtifactDoesNotFire(string prompt)
    {
        Assert.False(IsOs(prompt));
    }
}
