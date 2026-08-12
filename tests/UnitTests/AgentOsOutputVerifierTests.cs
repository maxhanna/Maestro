using System.Text;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the deterministic OS-output verifier (Services/AgentOsOutputVerifier.cs) — the
/// guard that stops a run from declaring planComplete when the task demanded a file be
/// written to the OS filesystem ("write the data into a text file on my desktop") but
/// no file was ever created. Detection must fire only on write-verb + file-artifact +
/// OS-location (with the repo-context escape hatch), the written-check must trust only
/// successful commands referencing the target directory or an existing file, and the
/// auto-dump must write the harvested web results deterministically.
/// </summary>
public class AgentOsOutputVerifierTests
{
    // ── Demand detection ────────────────────────────────────────────────────────

    [Fact]
    public void Demand_NoOsLocation_ReturnsFalse()
    {
        Assert.False(AgentOsOutputVerifier.TryGetOsFileOutputDemand(
            "Search the web for an interesting and relevant AI article.", out _));
    }

    [Fact]
    public void Demand_NoWriteVerb_ReturnsFalse()
    {
        // "read" is not a write verb — opening a file on the desktop is not an output demand.
        Assert.False(AgentOsOutputVerifier.TryGetOsFileOutputDemand(
            "read the file on my desktop and summarize it", out _));
    }

    [Fact]
    public void Demand_RepoContextEscape_ReturnsFalse()
    {
        Assert.False(AgentOsOutputVerifier.TryGetOsFileOutputDemand(
            "save the results to a text file in the project's desktop folder", out _));
    }

    [Fact]
    public void Demand_MyDesktop_ResolvesToDesktopDirectory()
    {
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(
            "Search the web for an interesting AI article and write the data into a text file on my desktop.",
            out var demand));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), demand.DirectoryPath);
        Assert.Null(demand.FileNameHint);
    }

    [Fact]
    public void Demand_DownloadsFolder_ResolvesToDownloadsDirectory()
    {
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(
            "save the search results to my downloads folder", out var demand));
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            demand.DirectoryPath);
    }

    [Fact]
    public void Demand_DocumentsFolder_ResolvesToDocumentsDirectory()
    {
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(
            "write a summary to my documents folder", out var demand));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), demand.DirectoryPath);
    }

    [Fact]
    public void Demand_AbsolutePath_CapturesDirectoryAndFileName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(dir, "report.txt");
        var prompt = $"search the web and write the data into a text file at \"{target}\"";
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(prompt, out var demand));
        Assert.Equal(dir, demand.DirectoryPath);
        Assert.Equal("report.txt", demand.FileNameHint);
    }

    [Fact]
    public void Demand_AbsoluteDirectory_NoFileName_ReturnsDirectoryOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(
            $"write the results to the folder \"{dir}\"", out var demand));
        Assert.Equal(dir, demand.DirectoryPath);
        Assert.Null(demand.FileNameHint);
    }

    [Fact]
    public void Demand_FileArtifactMissing_ReturnsFalse()
    {
        // A write verb and an OS location but no file artifact — nothing to write.
        Assert.False(AgentOsOutputVerifier.TryGetOsFileOutputDemand(
            "put the shortcut on my desktop", out _));
    }

    // ── Written-check ──────────────────────────────────────────────────────────

    [Fact]
    public void Written_NoResults_ReturnsFalse()
    {
        var demand = NewDemand(Path.GetTempPath());
        Assert.False(AgentOsOutputVerifier.IsOsOutputWritten(demand, Array.Empty<Dictionary<string, object?>>()));
    }

    [Fact]
    public void Written_CommandReferencingDirectory_Done_True()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        var demand = NewDemand(dir);
        var results = new[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "command",
                ["status"] = "done",
                ["command"] = $"Set-Content -Path \"{Path.Combine(dir, "out.txt")}\" -Value \"data\" -Encoding UTF8"
            }
        };
        Assert.True(AgentOsOutputVerifier.IsOsOutputWritten(demand, results));
    }

    [Fact]
    public void Written_FailedCommand_NotWritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        var demand = NewDemand(dir);
        var results = new[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "command",
                ["status"] = "error",
                ["command"] = $"Set-Content -Path \"{Path.Combine(dir, "out.txt")}\""
            }
        };
        Assert.False(AgentOsOutputVerifier.IsOsOutputWritten(demand, results));
    }

    [Fact]
    public void Written_FileExistsOnDisk_True()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, AgentOsOutputVerifier.DefaultDumpFileName);
            File.WriteAllText(target, "data");
            var demand = NewDemand(dir);
            Assert.True(AgentOsOutputVerifier.IsOsOutputWritten(demand, Array.Empty<Dictionary<string, object?>>()));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Written_TaskNamedFileExists_True()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "report.txt");
            File.WriteAllText(target, "data");
            var demand = new AgentOsOutputVerifier.OsOutputDemand("absolute", dir, "report.txt");
            Assert.True(AgentOsOutputVerifier.IsOsOutputWritten(demand, Array.Empty<Dictionary<string, object?>>()));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── CheckOsOutputWritten (the PostExecuteVerify issue) ────────────────────

    [Fact]
    public void CheckOsOutputWritten_NoDemand_ReturnsNull()
    {
        Assert.Null(AgentOsOutputVerifier.CheckOsOutputWritten(
            "Fix the typo in the README", Array.Empty<Dictionary<string, object?>>()));
    }

    [Fact]
    public void CheckOsOutputWritten_DemandUnwritten_ReturnsConfirmedIssue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        var issue = AgentOsOutputVerifier.CheckOsOutputWritten(
            $"write the data into a text file at \"{Path.Combine(dir, "out.txt")}\"",
            Array.Empty<Dictionary<string, object?>>());
        Assert.NotNull(issue);
        Assert.Contains("never created", issue);
        Assert.Contains("does not exist", issue);
    }

    [Fact]
    public void CheckOsOutputWritten_FileWritten_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "out.txt");
            File.WriteAllText(target, "data");
            Assert.Null(AgentOsOutputVerifier.CheckOsOutputWritten(
                $"write the data into a text file at \"{target}\"",
                Array.Empty<Dictionary<string, object?>>()));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── Auto-dump ──────────────────────────────────────────────────────────────

    [Fact]
    public void AutoDump_WebResults_WritesFileWithHarvestedContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var prompt = $"search the web and write the data into a text file at \"{Path.Combine(dir, "out.txt")}\"";
            var demand = GetDemand(prompt);
            var results = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "_web_search",
                    ["status"] = "done",
                    ["query"] = "AI research breakthroughs latest",
                    ["output"] = "A survey of recent AI research breakthroughs covering large language models and multimodal systems published this quarter."
                }
            };
            var (dumped, path, error) = AgentOsOutputVerifier.TryAutoDumpWebResults(prompt, demand, results);
            Assert.True(dumped, error);
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path!, Encoding.UTF8);
            Assert.Contains("AI research breakthroughs latest", content);
            Assert.Contains("### WEB RESULTS", content);
            Assert.Contains("Task: search the web and write", content);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void AutoDump_NoWebResults_NotDumped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var prompt = $"write the data into a text file at \"{Path.Combine(dir, "out.txt")}\"";
            var (dumped, _, error) = AgentOsOutputVerifier.TryAutoDumpWebResults(
                prompt, GetDemand(prompt), Array.Empty<Dictionary<string, object?>>());
            Assert.False(dumped);
            Assert.Contains("no web results", error);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void AutoDump_FailedWebStep_NotHarvested()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var prompt = $"write the data into a text file at \"{Path.Combine(dir, "out.txt")}\"";
            var results = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "_web_fetch",
                    ["status"] = "error",
                    ["url"] = "https://example.com/x",
                    ["output"] = "Exception: connection refused"
                }
            };
            var (dumped, _, error) = AgentOsOutputVerifier.TryAutoDumpWebResults(prompt, GetDemand(prompt), results);
            Assert.False(dumped);
            Assert.Contains("no web results", error);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void AutoDump_UsesTaskNamedFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var prompt = $"save the results to \"{Path.Combine(dir, "custom_name.md")}\"";
            var results = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "_web_search",
                    ["status"] = "done",
                    ["query"] = "q",
                    ["output"] = "Some harvested search output that is long enough to be worth dumping into the file."
                }
            };
            var (dumped, path, _) = AgentOsOutputVerifier.TryAutoDumpWebResults(prompt, GetDemand(prompt), results);
            Assert.True(dumped);
            Assert.EndsWith("custom_name.md", path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void AutoDump_CapsOversizedOutput()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var prompt = $"write the data into a text file at \"{Path.Combine(dir, "out.txt")}\"";
            var big = new string('x', 500_000);
            var results = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "_web_fetch",
                    ["status"] = "done",
                    ["url"] = "https://example.com/huge",
                    ["output"] = big
                }
            };
            var (dumped, path, _) = AgentOsOutputVerifier.TryAutoDumpWebResults(prompt, GetDemand(prompt), results);
            Assert.True(dumped);
            var content = File.ReadAllText(path!, Encoding.UTF8);
            // The 500k input is first section-capped at 20k chars — far below the 100k total cap.
            Assert.True(content.Length <= 20_000 + 2_000, $"dump too large: {content.Length}");
            Assert.Contains("truncated", content);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static AgentOsOutputVerifier.OsOutputDemand NewDemand(string dir)
        => new("test", dir, null);

    private static AgentOsOutputVerifier.OsOutputDemand GetDemand(string prompt)
    {
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(prompt, out var demand), "demand should be detected");
        return demand;
    }
}
