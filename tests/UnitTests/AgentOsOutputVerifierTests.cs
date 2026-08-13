using System.Text;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the deterministic OS-output verifier (Services/AgentOsOutputVerifier.cs) — the
/// guard that stops a run from declaring planComplete when the task demanded a file be
/// written to the OS filesystem ("write the data into a text file on my desktop") but
/// no file was ever created. Detection must fire only on write-verb + file-artifact +
/// OS-location (with the repo-context escape hatch), the written-check must require the
/// demanded file to exist with meaningful content whenever the target directory resolves
/// (command evidence only when the OS folder can't be resolved, e.g. headless runners),
/// and the auto-dump must write the harvested web results deterministically.
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

    [Fact]
    public void Demand_CreateVerb_Detected()
    {
        // The scheduled-card wording that previously slipped past the gate: "create" is a
        // write verb, "text file" an artifact, "on the desktop" an OS location.
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(
            "Fetch a recent AI news article and create a text file on the desktop.", out var demand));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), demand.DirectoryPath);
        Assert.Null(demand.FileNameHint);
    }

    // ── Written-check ──────────────────────────────────────────────────────────

    [Fact]
    public void Written_NoResults_ReturnsFalse()
    {
        var demand = NewDemand(Path.GetTempPath());
        Assert.False(AgentOsOutputVerifier.IsOsOutputWritten(demand, Array.Empty<Dictionary<string, object?>>()));
    }

    [Fact]
    public void Written_ResolvableDir_CommandMentionsDirButFileMissing_NotWritten()
    {
        // The exact false-completion hole: a done command that merely mentions the target
        // directory (here writing a DIFFERENTLY-named file) must not count — the demanded
        // file itself was never created.
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        var demand = NewDemand(dir);
        var results = new[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "command",
                ["status"] = "done",
                ["command"] = $"Set-Content -Path \"{Path.Combine(dir, "other_name.txt")}\" -Value \"data\" -Encoding UTF8"
            }
        };
        Assert.False(AgentOsOutputVerifier.IsOsOutputWritten(demand, results));
    }

    [Fact]
    public void Written_HeadlessUnresolvableDir_CommandReferencingFile_Done_True()
    {
        // Headless runners have no Desktop/Documents folder — the directory resolves to ""
        // and there is no file to inspect, so a done command naming the target file is the
        // only evidence and counts as written.
        var demand = new AgentOsOutputVerifier.OsOutputDemand("test", "", "out.txt");
        var results = new[]
        {
            new Dictionary<string, object?>
            {
                ["type"] = "command",
                ["status"] = "done",
                ["command"] = "Set-Content -Path \"C:\\Users\\me\\Desktop\\out.txt\" -Value \"data\" -Encoding UTF8"
            }
        };
        Assert.True(AgentOsOutputVerifier.IsOsOutputWritten(demand, results));
    }

    [Fact]
    public void Written_FileExistsWithHollowContent_NotWritten()
    {
        // A failed PowerShell fetch (Select-Object on an HTML response) saves exactly this
        // string — every property empty. That is evidence of a failed fetch, not of the
        // demanded data being written.
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, AgentOsOutputVerifier.DefaultDumpFileName);
            File.WriteAllText(target, "@{title=; summary=; publishedDate=}");
            var demand = NewDemand(dir);
            Assert.False(AgentOsOutputVerifier.IsOsOutputWritten(demand, Array.Empty<Dictionary<string, object?>>()));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Written_FileExistsEmpty_NotWritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, AgentOsOutputVerifier.DefaultDumpFileName);
            File.WriteAllText(target, "");
            var demand = NewDemand(dir);
            Assert.False(AgentOsOutputVerifier.IsOsOutputWritten(demand, Array.Empty<Dictionary<string, object?>>()));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Written_FileExistsWithRealContent_True()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, AgentOsOutputVerifier.DefaultDumpFileName);
            File.WriteAllText(target, "Meta's CEO lays out new AI strategy for personal intelligence.");
            var demand = NewDemand(dir);
            Assert.True(AgentOsOutputVerifier.IsOsOutputWritten(demand, Array.Empty<Dictionary<string, object?>>()));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
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

    [Fact]
    public void CheckOsOutputWritten_HollowFile_ReturnsConfirmedIssue()
    {
        // The demanded file EXISTS but contains only a hollow empty-object rendering — a
        // failed fetch must not count as delivering the data.
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "out.txt");
            File.WriteAllText(target, "@{title=; summary=; publishedDate=}");
            var issue = AgentOsOutputVerifier.CheckOsOutputWritten(
                $"create a text file at \"{target}\" with the article data",
                Array.Empty<Dictionary<string, object?>>());
            Assert.NotNull(issue);
            Assert.Contains("empty/hollow", issue);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CheckOsOutputWritten_WrongNamedFile_ReturnsConfirmedIssue()
    {
        // The end-to-end scheduled-card scenario: the agent's done command wrote a file with
        // a name IT chose (and that write was hollow), but the demanded file — the default
        // name at the demanded location — was never created. The gate must reject completion.
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var wrong = Path.Combine(dir, "ai_news_article.txt");
            File.WriteAllText(wrong, "@{title=; summary=; publishedDate=}");
            var results = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "command",
                    ["status"] = "done",
                    ["command"] = $"Invoke-RestMethod -Uri \"https://www.wired.com/story/x/\" | Select-Object title,summary,publishedDate | Set-Content -Path \"{wrong}\""
                }
            };
            var issue = AgentOsOutputVerifier.CheckOsOutputWritten(
                $"write the data into a text file at \"{Path.Combine(dir, AgentOsOutputVerifier.DefaultDumpFileName)}\"",
                results);
            Assert.NotNull(issue);
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

    [Fact]
    public void AutoDump_CreatesParentDirectoryWhenMissing()
    {
        // The task may name an arbitrary nested path whose folders do not exist yet
        // (e.g. "benchmark_test_16/pokemon_data.csv" on a fresh machine) — the dump
        // must create the parent directory instead of failing on File.WriteAllText.
        var root = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "nested", "deep", "out.txt");
        Assert.False(Directory.Exists(root));
        try
        {
            var prompt = $"create a text file with the data at \"{target}\"";
            var results = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "_web_fetch",
                    ["status"] = "done",
                    ["url"] = "https://example.com/data",
                    ["output"] = "Fresh harvested payload fetched from the web and ready to be dumped into the file for the task."
                }
            };
            var (dumped, path, error) = AgentOsOutputVerifier.TryAutoDumpWebResults(prompt, GetDemand(prompt), results);
            Assert.True(dumped, error);
            Assert.Equal(target, path);
            Assert.True(Directory.Exists(Path.Combine(root, "nested", "deep")));
            Assert.True(File.Exists(target));
            Assert.Contains("Fresh harvested payload", File.ReadAllText(target, Encoding.UTF8));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void AutoDump_ExistingFile_AppendsInsteadOfClobbering()
    {
        // When the demanded file already exists (a notes doc, a CSV with its header row,
        // an earlier dump), the fresh sections must be INSERTED at the end — never
        // clobbering the existing content.
        var dir = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "notes.md");
            File.WriteAllText(target, "# Existing user notes\n- keep me\n", Encoding.UTF8);
            var prompt = $"search the web and append the latest headlines to my notes file at \"{target}\"";
            var results = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "_web_search",
                    ["status"] = "done",
                    ["query"] = "latest AI headlines",
                    ["output"] = "Headline one: new model released. Headline two: safety research published today. This text is long enough to count as a real dump section."
                }
            };
            var (dumped, path, error) = AgentOsOutputVerifier.TryAutoDumpWebResults(prompt, GetDemand(prompt), results);
            Assert.True(dumped, error);
            Assert.Equal(target, path);
            var content = File.ReadAllText(target, Encoding.UTF8);
            Assert.Contains("- keep me", content);
            Assert.Contains("Headline one", content);
            // Existing content stays BEFORE the appended fresh section — inserted at the right location.
            Assert.True(content.IndexOf("- keep me", StringComparison.Ordinal) <
                        content.IndexOf("Headline one", StringComparison.Ordinal));
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

    // ── File-output target (repo-relative dump destination, used to pre-create
    //    the folder before the web step) ──────────────────────────────────────

    [Fact]
    public void FileOutputTarget_RepoRelative_FolderAndFile_ResolvesUnderProjectRoot()
    {
        // The benchmark-task phrasing: "create a folder called X … a file called Y.csv" —
        // a REPO-relative demand the OS-only detector refuses (repo escape), which is
        // exactly why the web step previously had nowhere to dump. The target must resolve
        // to <projectRoot>/benchmark_test_16/pokemon_data.csv with kind "repo".
        var root = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        var prompt = "Create a folder called benchmark_test_16 at the project root. Inside it, create a file called pokemon_data.csv. Fetch real Pokemon data and write the data into pokemon_data.csv.";
        Assert.True(AgentOsOutputVerifier.TryGetFileOutputTarget(prompt, root, out var demand));
        Assert.Equal("repo", demand.LocationKind);
        Assert.Equal("pokemon_data.csv", demand.FileNameHint);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "benchmark_test_16")), demand.DirectoryPath);
    }

    [Fact]
    public void FileOutputTarget_BareFileName_ScopesToProjectRoot()
    {
        // A file name with no folder and no preceding "folder called X" resolves to the
        // project root itself.
        var root = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        var prompt = "write the data into report.md";
        Assert.True(AgentOsOutputVerifier.TryGetFileOutputTarget(prompt, root, out var demand));
        Assert.Equal("report.md", demand.FileNameHint);
        Assert.Equal(Path.GetFullPath(root), demand.DirectoryPath);
    }

    [Fact]
    public void FileOutputTarget_OsDemand_WinsOverRepoResolution()
    {
        // An absolute path (or any OS demand) must win — the repo-relative branch only
        // runs when the OS detector found nothing.
        var root = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "abs", "out.txt");
        var prompt = $"create a text file with the data at \"{target}\"";
        Assert.True(AgentOsOutputVerifier.TryGetFileOutputTarget(prompt, root, out var demand));
        Assert.Equal("absolute", demand.LocationKind);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "abs")), demand.DirectoryPath);
        Assert.Equal("out.txt", demand.FileNameHint);
    }

    [Fact]
    public void FileOutputTarget_NoWriteVerb_ReturnsFalse()
    {
        // "fix the bug in the controller" has a file artifact only via "controller" — no
        // write verb, so no target.
        Assert.False(AgentOsOutputVerifier.TryGetFileOutputTarget(
            "fix the bug in the controller", "C:/repo", out _));
    }

    [Fact]
    public void PrepareDumpDirectory_CreatesNestedParentDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_osv_" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(root));
        try
        {
            var demand = new AgentOsOutputVerifier.OsOutputDemand("repo",
                Path.Combine(root, "benchmark_test_16"), "pokemon_data.csv");
            var dir = AgentOsOutputVerifier.PrepareDumpDirectory(demand);
            Assert.NotNull(dir);
            Assert.True(Directory.Exists(Path.Combine(root, "benchmark_test_16")));
            // Idempotent: a second call is a no-op, not an error.
            Assert.Equal(dir, AgentOsOutputVerifier.PrepareDumpDirectory(demand));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void PrepareDumpDirectory_EmptyDirectory_ReturnsNull()
    {
        var demand = new AgentOsOutputVerifier.OsOutputDemand("test", "", null);
        Assert.Null(AgentOsOutputVerifier.PrepareDumpDirectory(demand));
    }
}
