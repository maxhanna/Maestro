using System.IO;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks ScraperEnvironmentService — the system-owned scraper builder that replaces freehand
/// LLM scraper code. Toolchain selection and the known-good script templates must be
/// deterministic given a fake process runner (no real processes ever spawn in tests), and the
/// generated scripts must have the shape that actually runs (correct indentation, correct
/// write modes, metadata line prepended) — the antidote to the IndentationError run.
/// </summary>
public class ScraperEnvironmentServiceTests
{
    private static ScraperEnvironmentService ServiceWith(
        Func<string, string, string, (int Code, string StdOut, string StdErr)> runner) =>
        new(runner);

    private static (int Code, string StdOut, string StdErr) Ok(string stdout = "") => (0, stdout, "");

    private static readonly (int Code, string StdOut, string StdErr) NotFound = (-1, "", "");

    // ── BestToolchain: probes decide which toolchain the machine can actually run ──

    [Fact]
    public void BestToolchain_PythonWithRequests_Wins()
    {
        // python exists; `import requests` exits 0 → PythonRequests.
        var svc = ServiceWith((file, args, _) =>
            file is "where" or "which" ? Ok()
            : args.Contains("import requests") ? Ok()
            : NotFound);
        Assert.Equal(ScraperEnvironmentService.Toolchain.PythonRequests, svc.BestToolchain());
    }

    [Fact]
    public void BestToolchain_PythonWithoutRequests_FallsBackToUrllib()
    {
        // python exists but requests import fails → stdlib urllib (always available).
        var svc = ServiceWith((file, args, _) =>
            file is "where" or "which" ? Ok()
            : NotFound);
        Assert.Equal(ScraperEnvironmentService.Toolchain.PythonUrllib, svc.BestToolchain());
    }

    [Fact]
    public void BestToolchain_OnlyNode_FallsBackToNodeFetch()
    {
        // node is on PATH and `console.log(typeof fetch)` prints "function".
        var svc = ServiceWith((file, args, _) =>
            file is "where" or "which" && args == "node" ? Ok()
            : file == "node" ? Ok("function")
            : NotFound);
        Assert.Equal(ScraperEnvironmentService.Toolchain.NodeFetch, svc.BestToolchain());
    }

    [Fact]
    public void BestToolchain_OnlyPowerShell_FallsBackToInvokeRestMethod()
    {
        var svc = ServiceWith((file, args, _) =>
            file is "where" or "which" && args == "pwsh" ? Ok() : NotFound);
        Assert.Equal(ScraperEnvironmentService.Toolchain.PowerShell, svc.BestToolchain());
    }

    [Fact]
    public void BestToolchain_NoInterpreter_IsNull()
    {
        var svc = ServiceWith((_, _, _) => NotFound);
        Assert.Null(svc.BestToolchain());
        Assert.False(svc.HasAnyToolchain());
    }

    // ── GenerateScript: known-good templates, correct shape, metadata prepended ──

    [Theory]
    [InlineData(ScraperEnvironmentService.Toolchain.PythonRequests)]
    [InlineData(ScraperEnvironmentService.Toolchain.PythonUrllib)]
    public void GenerateScript_Python_WellFormedAndWritesOutput(ScraperEnvironmentService.Toolchain tc)
    {
        var script = new ScraperEnvironmentService().GenerateScript(tc, "https://pokeapi.co/api/v2/pokemon?limit=1025",
            "benchmark_test_16/pokemon_data.csv", "FETCHED_AT: 2026-08-13");
        // The exact failure from the run: the freehand script died on an IndentationError right
        // after "with open(...) as f:". The template must indent the body and open write-mode.
        Assert.Contains("with open(out, \"w\", encoding=\"utf-8\", newline=\"\") as f:\n    f.write(", script);
        Assert.Contains("FETCHED_AT: 2026-08-13", script);
        Assert.DoesNotContain("def fetch_and_write_csv(filename):\n with open", script);
    }

    [Fact]
    public void GenerateScript_NodeFetch_UsesGlobalFetchAndWritesFile()
    {
        var script = new ScraperEnvironmentService().GenerateScript(
            ScraperEnvironmentService.Toolchain.NodeFetch, "https://example.com/data", "out.json", null);
        Assert.Contains("const res = await fetch(url", script);
        Assert.Contains("fs.writeFileSync(out, text, \"utf8\");", script);
        Assert.DoesNotContain("FETCHED_AT", script);
    }

    [Fact]
    public void GenerateScript_PowerShell_UsesInvokeWebRequestAndSetContent()
    {
        var script = new ScraperEnvironmentService().GenerateScript(
            ScraperEnvironmentService.Toolchain.PowerShell, "https://example.com/data", "out.txt", "FETCHED_AT: 2026-08-13");
        Assert.Contains("Invoke-WebRequest", script);
        Assert.Contains("Add-Content -Path $Out", script);
        Assert.Contains("FETCHED_AT: 2026-08-13", script);
    }

    // ── TryRunScraperAsync: runs via the best toolchain and reports the written path ──

    [Fact]
    public async Task TryRunScraperAsync_Success_ReportsWrittenPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scraper-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "pokemon_data.csv");
            // The fake runner behaves like the real python run: the requests import probe
            // succeeds (so the requests toolchain is chosen), and the actual run writes the
            // target file and prints WROTE.
            var svc = ServiceWith((file, args, _) =>
            {
                if (file is "where" or "which") return Ok();
                if (args.Contains("import requests")) return Ok();
                var parts = args.Split('"');
                // parts[1]=script, parts[3]=url, parts[5]=target
                var t = parts.Length >= 6 ? parts[5] : target;
                File.WriteAllText(t, "FETCHED_AT: 2026-08-13\nid,name\n");
                return Ok("WROTE " + t + " 21");
            });
            var result = await svc.TryRunScraperAsync("https://pokeapi.co/api/v2/pokemon?limit=1025", target, dir, "FETCHED_AT: 2026-08-13", CancellationToken.None);
            Assert.True(result.Success, result.Error);
            Assert.Equal(target, result.WrittenPath);
            Assert.Contains("import requests", result.ScriptText);
            Assert.True(File.Exists(target));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TryRunScraperAsync_NoInterpreter_ReturnsActionableError()
    {
        var svc = ServiceWith((_, _, _) => NotFound);
        var result = await svc.TryRunScraperAsync("https://pokeapi.co", null, Path.GetTempPath(), null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("_web_fetch", result.Error ?? "");
    }

    [Fact]
    public async Task TryRunScraperAsync_NonHttpUrl_Rejected()
    {
        var svc = ServiceWith((_, _, _) => Ok());
        var result = await svc.TryRunScraperAsync("not-a-url", null, Path.GetTempPath(), null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Not a fetchable URL", result.Error ?? "");
    }

    [Fact]
    public async Task TryRunScraperAsync_FailingRun_ReturnsErrorAndKeepsWorkingScript()
    {
        // Interpreter + requests probe succeed, but the actual script run crashes.
        var svc = ServiceWith((file, args, _) =>
            file is "where" or "which" ? Ok()
            : args.Contains("import requests") ? Ok()
            : (1, "", "Traceback: boom"));
        var result = await svc.TryRunScraperAsync("https://pokeapi.co", null, Path.GetTempPath(), null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("boom", result.Error ?? "");
        Assert.Contains("import requests", result.ScriptText);
    }

    // ── EnvironmentSummary: honest, never throws ──

    [Fact]
    public void EnvironmentSummary_ListsOsAndToolchains()
    {
        var svc = ServiceWith((file, args, _) =>
            file is "where" or "which" ? Ok()
            : args.Contains("import requests") ? Ok()
            : NotFound);
        var summary = svc.EnvironmentSummary();
        Assert.Contains("python", summary);
        Assert.Contains("requests", summary);
        Assert.Contains("node", summary);
    }

    [Fact]
    public void EnvironmentSummary_NoToolchain_StillReturnsOsLine()
    {
        var svc = ServiceWith((_, _, _) => NotFound);
        var summary = svc.EnvironmentSummary();
        Assert.Contains("python", summary);
        Assert.Contains("pwsh", summary);
    }
}
