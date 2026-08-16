using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks RuntimeProbeService — the host runtime-availability probe that discovery surfaces to
/// the planner ("RUNTIME AVAILABILITY") so it picks a language that actually exists on this
/// machine instead of freehanding a Python/Flask server on a box with no Python. All probing
/// goes through an injectable ProcessRunner so tests never spawn a real process; the probe
/// list, version extraction (stdout AND stderr — python2/java print to stderr), not-found
/// handling, caching, and the planner-facing formatting must be deterministic.
/// </summary>
public class RuntimeProbeServiceTests
{
    private static RuntimeProbeService ServiceWith(
        Func<string, string, string, (int Code, string StdOut, string StdErr)> runner) =>
        new(runner);

    private static (int Code, string StdOut, string StdErr) Ok(string stdout = "") => (0, stdout, "");
    private static readonly (int Code, string StdOut, string StdErr) NotFound = (-1, "", "");

    // ── ProbeAll: finds tools, versions, stderr, and not-found ─────────────

    [Fact]
    public void ProbeAll_PythonNodeDotnetFound_GoAndJavaMissing()
    {
        var svc = ServiceWith((file, args, _) => file switch
        {
            "python" => Ok("Python 3.12.4\n"),
            "node" => Ok("v22.5.1\n"),
            "dotnet" => Ok("8.0.403\n"),
            _ => NotFound
        });
        var probes = svc.ProbeAll();
        Assert.Equal("Python 3.12.4", probes.First(p => p.Name == "python").Version);
        Assert.Equal("v22.5.1", probes.First(p => p.Name == "node").Version);
        Assert.Equal("8.0.403", probes.First(p => p.Name == "dotnet").Version);
        Assert.Null(probes.First(p => p.Name == "go").Version);
        Assert.Null(probes.First(p => p.Name == "java").Version);
        Assert.Null(probes.First(p => p.Name == "cargo").Version);
    }

    [Fact]
    public void ProbeAll_VersionFromStderr_WhenStdoutEmpty()
    {
        // python2 / java print version info to stderr; the probe must capture it.
        var svc = ServiceWith((file, _, _) =>
            file == "java" ? (0, "", "openjdk 17.0.10 2024-01-16\n") : NotFound);
        var probes = svc.ProbeAll();
        Assert.Equal("openjdk 17.0.10 2024-01-16", probes.First(p => p.Name == "java").Version);
    }

    [Fact]
    public void ProbeAll_ExitCodeNonZero_IsNotFound()
    {
        // Tool exists on disk but errors out (e.g. broken install) — treat as unavailable.
        var svc = ServiceWith((file, _, _) => file == "git" ? (1, "", "fatal: bad config\n") : NotFound);
        Assert.Null(svc.ProbeAll().First(p => p.Name == "git").Version);
    }

    [Fact]
    public void ProbeAll_RunnerThrows_IsNotFound_DoesNotThrow()
    {
        var svc = ServiceWith((_, _, _) => throw new InvalidOperationException("no such command"));
        var probes = svc.ProbeAll();
        Assert.All(probes, p => Assert.Null(p.Version));
    }

    [Fact]
    public void ProbeAll_EveryKnownRuntimeIsReported()
    {
        var svc = ServiceWith((_, _, _) => NotFound);
        var probes = svc.ProbeAll();
        var expected = new[]
        {
            "python", "python3", "pip", "pip3", "node", "npm", "npx", "dotnet", "go", "java",
            "javac", "ruby", "php", "cargo", "gcc", "g++", "git", "pwsh", "powershell"
        };
        Assert.Equal(expected.Length, probes.Count);
        Assert.All(expected, name => Assert.Contains(probes, p => p.Name == name));
    }

    [Fact]
    public void ProbeAll_CachedPerInstance_RunnerCalledOncePerTool()
    {
        var calls = 0;
        var svc = ServiceWith((_, _, _) => { calls++; return Ok("x"); });
        var first = svc.ProbeAll();
        var second = svc.ProbeAll();
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Count, calls); // each tool probed exactly once across both calls
    }

    // ── FormatForContext: planner-facing shape ─────────────────────────────

    [Fact]
    public void FormatForContext_ListsAvailableAndMissing()
    {
        var probes = new List<RuntimeProbeService.RuntimeInfo>
        {
            new("python", "Python 3.12.4"),
            new("node", "v22.5.1"),
            new("go", null),
            new("dotnet", null)
        };
        var text = RuntimeProbeService.FormatForContext(probes);
        Assert.Contains("RUNTIME AVAILABILITY", text);
        Assert.Contains("python (Python 3.12.4)", text);
        Assert.Contains("node (v22.5.1)", text);
        Assert.Contains("NOT available: go, dotnet", text);
        Assert.Contains("Do NOT assume python, node", text);
    }

    [Fact]
    public void FormatForContext_NothingInstalled_SaysNone()
    {
        var probes = new List<RuntimeProbeService.RuntimeInfo>
        {
            new("python", null),
            new("node", null)
        };
        var text = RuntimeProbeService.FormatForContext(probes);
        Assert.Contains("Available: NONE", text);
        Assert.Contains("NOT available: python, node", text);
    }

    // ── ShortSummary ───────────────────────────────────────────────────────

    [Fact]
    public void ShortSummary_MarksAvailableAndMissing()
    {
        var probes = new List<RuntimeProbeService.RuntimeInfo>
        {
            new("python", "3.12"),
            new("node", null)
        };
        var summary = RuntimeProbeService.ShortSummary(probes);
        Assert.Contains("python ✓", summary);
        Assert.Contains("node ✗", summary);
    }

    // ── Windows shim resolution (npm/npx are .cmd scripts Process.Start can't exec) ──

    [Fact]
    public void ResolveCommandForExecution_WindowsNpmShim_RoutesThroughCmdExe()
    {
        // The benchmark-22 root cause: npm exists on the machine but the probe reported it
        // UNAVAILABLE because Process.Start cannot exec the npm.cmd shim directly, so the
        // planner believed `npm install` was impossible. The shim must route through cmd /c.
        var (file, args) = RuntimeProbeService.ResolveCommandForExecution("npm", "--version");
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith("cmd.exe", file, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("/c npm --version", args);
        }
        else
        {
            Assert.Equal("npm", file);
            Assert.Equal("--version", args);
        }
    }

    [Fact]
    public void ResolveCommandForExecution_NpxShimAndPlainTools_PassThrough()
    {
        var (npxFile, npxArgs) = RuntimeProbeService.ResolveCommandForExecution("npx", "--version");
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith("cmd.exe", npxFile, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("/c npx --version", npxArgs);
        }
        else
        {
            Assert.Equal("npx", npxFile);
        }
        // Non-shim tools (node, python, git) never get rewritten.
        var (nodeFile, nodeArgs) = RuntimeProbeService.ResolveCommandForExecution("node", "--version");
        Assert.Equal("node", nodeFile);
        Assert.Equal("--version", nodeArgs);
    }
}
