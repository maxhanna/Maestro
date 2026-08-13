using System.IO;
using System.Text;
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

    // ── Pagination: a ?limit=/offset= URL gets the page-looping template ──

    [Theory]
    [InlineData("https://pokeapi.co/api/v2/pokemon?limit=1025", true)]
    [InlineData("https://api.example.com/list?limit=100&offset=0", true)]
    [InlineData("https://api.example.com/list?offset=200", true)]
    [InlineData("https://api.example.com/list&limit=50", true)]
    [InlineData("https://example.com/alphafold3", false)]
    [InlineData("https://pokeapi.co/api/v2/pokemon/25", false)]
    [InlineData("", false)]
    public void ShouldPaginate_DetectsLimitOrOffsetUrls(string url, bool expected)
    {
        Assert.Equal(expected, ScraperEnvironmentService.ShouldPaginate(url));
    }

    [Fact]
    public void GenerateScript_PaginatedPython_LoopsOverOffsetAndFollowsNext()
    {
        var script = new ScraperEnvironmentService().GenerateScript(
            ScraperEnvironmentService.Toolchain.PythonRequests,
            "https://pokeapi.co/api/v2/pokemon?limit=1025", "benchmark_test_16/pokemon_data.json", null,
            paginate: true);
        // The pagination loop, the cursor/offset advance, the merge, and the JSON output.
        Assert.Contains("while cur and page < 50:", script);
        Assert.Contains("data.get(\"next\")", script);
        Assert.Contains("offset=" + "\" + str(off + n)", script);
        Assert.Contains("\"results\" in data", script);
        Assert.Contains("\"count\": count, \"results\": all_items", script);
        Assert.Contains("json.dumps(payload, indent=2)", script);
        // Still the correct write shape (no IndentationError, metadata handled).
        Assert.Contains("with open(out, \"w\", encoding=\"utf-8\", newline=\"\") as f:\n    f.write(", script);
        // Non-paginated payloads degrade to verbatim output.
        Assert.Contains("all_items = data", script);
    }

    [Fact]
    public void GenerateScript_PaginatedNode_LoopsOverOffsetAndFollowsNext()
    {
        var script = new ScraperEnvironmentService().GenerateScript(
            ScraperEnvironmentService.Toolchain.NodeFetch,
            "https://api.example.com/list?limit=100&offset=0", "out.json", null,
            paginate: true);
        Assert.Contains("while (cur && page < 50) {", script);
        Assert.Contains("data.next", script);
        Assert.Contains("offset=\" + (off + items.length)", script);
        Assert.Contains("{ count, results: all }", script);
        Assert.Contains("JSON.stringify(payload, null, 2)", script);
    }

    [Fact]
    public void GenerateScript_PaginatedPowerShell_LoopsOverOffsetAndFollowsNext()
    {
        var script = new ScraperEnvironmentService().GenerateScript(
            ScraperEnvironmentService.Toolchain.PowerShell,
            "https://api.example.com/list?limit=100", "out.json", null,
            paginate: true);
        Assert.Contains("while ($cur -and $page -lt 50) {", script);
        Assert.Contains("$data.next", script);
        Assert.Contains("offset=\" + ($off + $n)", script);
        Assert.Contains("ConvertTo-Json -Depth 10", script);
    }

    [Fact]
    public void GenerateScript_MetadataLine_EscapedIntoValidStringLiteral()
    {
        // The meta line contains a REAL newline; embedded raw it produced a Python
        // SyntaxError inside the "known-good" script (a literal line break inside
        // f.write("...")). It must be escaped to backslash-n in the generated literal.
        var script = new ScraperEnvironmentService().GenerateScript(
            ScraperEnvironmentService.Toolchain.PythonRequests,
            "https://pokeapi.co/api/v2/pokemon?limit=1025", "out.json", "FETCHED_AT: 2026-08-13");
        // Backslash-n inside the string literal (two characters), not a real newline.
        Assert.Contains("f.write(\"FETCHED_AT: 2026-08-13\\n\" + data)", script);
        // No real newline between the date and the closing quote anywhere.
        Assert.DoesNotContain("FETCHED_AT: 2026-08-13\n\"", script);

        var node = new ScraperEnvironmentService().GenerateScript(
            ScraperEnvironmentService.Toolchain.NodeFetch,
            "https://pokeapi.co/api/v2/pokemon?limit=1025", "out.json", "FETCHED_AT: 2026-08-13");
        Assert.Contains("text = \"FETCHED_AT: 2026-08-13\\n\" + text;", node);
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

    // ── Real-interpreter syntax check (gated on availability) ──
    // The templates must be MORE than shape-plausible: they are code the system will actually
    // run against real APIs, so when a real interpreter is present this test compiles every
    // generated script (single + paginated, with the metadata line) and fails on any syntax
    // error — the IndentationError run's exact failure class.

    [Fact]
    public void GeneratedScripts_CompileWithTheRealInterpreter_WhenAvailable()
    {
        var py = ScraperEnvironmentService.StaticInterpreterAvailable("python")
            ? "python"
            : ScraperEnvironmentService.StaticInterpreterAvailable("python3") ? "python3" : null;
        var node = ScraperEnvironmentService.StaticInterpreterAvailable("node") ? "node" : null;
        if (py == null && node == null) return; // no interpreter on this host — nothing to compile

        var dir = Path.Combine(Path.GetTempPath(), "scraper-syntax-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = new ScraperEnvironmentService();
            var url = "https://pokeapi.co/api/v2/pokemon?limit=1025";
            var cases = new List<(string file, string script, string? checkWith)>
            {
                ("single_py_requests.py", svc.GenerateScript(ScraperEnvironmentService.Toolchain.PythonRequests, url, "out.json", "FETCHED_AT: 2026-08-13"), py),
                ("single_py_urllib.py", svc.GenerateScript(ScraperEnvironmentService.Toolchain.PythonUrllib, url, "out.json", "FETCHED_AT: 2026-08-13"), py),
                ("paged_py.py", svc.GenerateScript(ScraperEnvironmentService.Toolchain.PythonRequests, url, "out.json", "FETCHED_AT: 2026-08-13", paginate: true), py),
                ("paged_node.js", svc.GenerateScript(ScraperEnvironmentService.Toolchain.NodeFetch, url, "out.json", "FETCHED_AT: 2026-08-13", paginate: true), node)
            };
            foreach (var (file, script, checkWith) in cases)
            {
                if (checkWith == null) continue;
                var path = Path.Combine(dir, file);
                File.WriteAllText(path, script, Encoding.UTF8);
                var checkArgs = file.EndsWith(".py", StringComparison.Ordinal)
                    ? $"-m py_compile \"{path}\""
                    : $"--check \"{path}\"";
                var (code, stdout, stderr) = svc.ProcessRunner(checkWith, checkArgs, dir);
                Assert.True(code == 0,
                    $"{file} failed the real-interpreter syntax check ({checkWith} {checkArgs}):\n{stderr}\n{stdout}\n--- script ---\n{script}");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
