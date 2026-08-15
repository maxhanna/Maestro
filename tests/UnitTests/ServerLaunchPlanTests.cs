using System.Net;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks ServerLauncherService — the deterministic "how does THIS project's server
/// start?" detector and the process/static launcher behind the live web-test pipeline.
/// Detection must be pure filesystem logic: the same project layout always yields the
/// same plan, for ANY project type (C#, Node, Python, Go, Rust, Java, PHP, Ruby,
/// static HTML). Launch tests use the in-process static server (no external tools) and
/// a process that exits immediately (cross-platform, no real server needed).
/// </summary>
[Collection("LiveProcessTests")]
public class ServerLaunchPlanTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "weaver-launch-tests-" + Guid.NewGuid().ToString("N"));

    public ServerLaunchPlanTests()
    {
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private string NewProject()
    {
        var dir = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string rel, string content)
    {
        var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ── detection: C# / .NET ─────────────────────────────────────────────────

    [Fact]
    public void Detect_CsprojAtRoot_DotnetRunWithProject()
    {
        var dir = NewProject();
        Write(dir, "MyApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.NotNull(plan);
        Assert.Equal("dotnet", plan!.Kind);
        Assert.Equal("dotnet", plan.Command);
        Assert.Contains("run --project MyApp.csproj", plan.Arguments);
        Assert.Contains("{port}", plan.Arguments);
        Assert.Equal(5000, plan.PortHint);
        Assert.Equal(dir, plan.WorkingDirectory);
    }

    [Fact]
    public void Detect_CsprojInSubdir_QuotedRelativeProject()
    {
        var dir = NewProject();
        Write(dir, "src/My App/My App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.NotNull(plan);
        Assert.Equal("dotnet", plan!.Kind);
        Assert.Contains("src/My App/My App.csproj", plan.Arguments);
    }

    [Fact]
    public void Detect_SolutionOnly_ResolvesProjectNextToSln()
    {
        var dir = NewProject();
        Write(dir, "All.sln", "solution");
        Write(dir, "Entry/Entry.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.NotNull(plan);
        Assert.Equal("dotnet", plan!.Kind);
        Assert.Contains("run --project Entry/Entry.csproj", plan.Arguments);
    }

    [Fact]
    public void Detect_CsprojInIgnoredDir_Ignored()
    {
        var dir = NewProject();
        Write(dir, "bin/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        Assert.Null(ServerLauncherService.DetectLaunchPlan(dir));
    }

    // ── detection: Node ──────────────────────────────────────────────────────

    [Fact]
    public void Detect_PackageJson_StartScript()
    {
        var dir = NewProject();
        Write(dir, "package.json", "{\"scripts\":{\"start\":\"node server.js\"}}");

        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.NotNull(plan);
        Assert.Equal("node", plan!.Kind);
        Assert.Equal("npm", plan.Command);
        Assert.Equal("run start", plan.Arguments);
        Assert.Equal(3000, plan.PortHint);
    }

    [Fact]
    public void Detect_PackageJson_DevPreferredOverStart()
    {
        var dir = NewProject();
        Write(dir, "package.json", "{\"scripts\":{\"start\":\"node server.js\",\"dev\":\"vite\"}}");

        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("run dev", plan!.Arguments);
    }

    [Fact]
    public void Detect_PackageJson_NoScripts_NodeEntry()
    {
        var dir = NewProject();
        Write(dir, "package.json", "{}");
        Write(dir, "server.js", "console.log('x')");

        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.NotNull(plan);
        Assert.Equal("node", plan!.Kind);
        Assert.Equal("node", plan.Command);
        Assert.Equal("server.js", plan.Arguments);
    }

    [Fact]
    public void Detect_PackageJson_NoScriptsNoEntry_Null()
    {
        var dir = NewProject();
        Write(dir, "package.json", "{}");
        Assert.Null(ServerLauncherService.DetectLaunchPlan(dir));
    }

    // ── detection: Python ────────────────────────────────────────────────────

    [Fact]
    public void Detect_ManagePy_Django()
    {
        var dir = NewProject();
        Write(dir, "manage.py", "");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("python", plan!.Kind);
        Assert.Equal("manage.py runserver 127.0.0.1:{port}", plan.Arguments);
        Assert.Equal(8000, plan.PortHint);
    }

    [Fact]
    public void Detect_StreamlitImport_StreamlitRun()
    {
        var dir = NewProject();
        Write(dir, "app.py", "import streamlit as st\nst.title('hi')\n");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("streamlit", plan!.Command);
        Assert.Contains("run app.py", plan.Arguments);
        Assert.Equal(8501, plan.PortHint);
    }

    [Fact]
    public void Detect_FastApiImport_UvicornModuleApp()
    {
        var dir = NewProject();
        Write(dir, "main.py", "from fastapi import FastAPI\napp = FastAPI()\n");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("uvicorn", plan!.Command);
        Assert.Equal("main:app --host 127.0.0.1 --port {port}", plan.Arguments);
        Assert.Equal(8000, plan.PortHint);
    }

    [Fact]
    public void Detect_FlaskImport_FlaskRun()
    {
        var dir = NewProject();
        Write(dir, "app.py", "from flask import Flask\napp = Flask(__name__)\n");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("flask", plan!.Command);
        Assert.Equal("run --host 127.0.0.1 --port {port}", plan.Arguments);
        Assert.Equal(5000, plan.PortHint);
    }

    [Fact]
    public void Detect_PlainPyEntry_PythonApp()
    {
        var dir = NewProject();
        Write(dir, "app.py", "print('hi')");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("python", plan!.Command);
        Assert.Equal("app.py", plan.Arguments);
    }

    // ── detection: Go / Rust / Java / PHP / Ruby ─────────────────────────────

    [Fact]
    public void Detect_GoMod_GoRun()
    {
        var dir = NewProject();
        Write(dir, "go.mod", "module x");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("go", plan!.Kind);
        Assert.Equal("run .", plan.Arguments);
        Assert.Equal(8080, plan.PortHint);
    }

    [Fact]
    public void Detect_CargoToml_CargoRun()
    {
        var dir = NewProject();
        Write(dir, "Cargo.toml", "[package]");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("cargo", plan!.Kind);
        Assert.Equal("run", plan.Arguments);
    }

    [Fact]
    public void Detect_PomXml_MvnSpringBoot()
    {
        var dir = NewProject();
        Write(dir, "pom.xml", "<project/>");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("mvn", plan!.Command);
        Assert.Contains("spring-boot:run", plan.Arguments);
        Assert.Contains("{port}", plan.Arguments);
    }

    [Fact]
    public void Detect_IndexPhp_PhpBuiltInServer()
    {
        var dir = NewProject();
        Write(dir, "index.php", "<?php echo 'hi';");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("php", plan!.Kind);
        Assert.Contains("-S 127.0.0.1:{port}", plan.Arguments);
    }

    [Fact]
    public void Detect_GemfileWithConfigRu_Rackup()
    {
        var dir = NewProject();
        Write(dir, "Gemfile", "source 'https://rubygems.org'");
        Write(dir, "config.ru", "run lambda { |env| [200, {}, ['hi']] }");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("rackup", plan!.Command);
        Assert.Contains("-p {port}", plan.Arguments);
    }

    [Fact]
    public void Detect_GemfileWithAppDir_Rails()
    {
        var dir = NewProject();
        Write(dir, "Gemfile", "source 'https://rubygems.org'");
        Directory.CreateDirectory(Path.Combine(dir, "app"));
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.Equal("bundle", plan!.Command);
        Assert.Contains("exec rails server", plan.Arguments);
    }

    // ── detection: static HTML ───────────────────────────────────────────────

    [Fact]
    public void Detect_BareServerJsInSubfolder_NodePlanRunsTheServer()
    {
        // The benchmark-22 shape: NO package.json — the agent wrote
        // benchmark_test_22/{index.html,server.js} inside a sandbox root. The plan must
        // run THAT server (working dir = the subfolder, entry = server.js) instead of
        // silently serving index.html statically (which would 404 /api/health and never
        // exercise the agent's actual server).
        var dir = NewProject();
        Write(dir, "benchmark_test_22/index.html", "<html><body><h1>Benchmark 22</h1></body></html>");
        Write(dir, "benchmark_test_22/server.js", "require('http')");

        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.NotNull(plan);
        Assert.Equal("node", plan!.Kind);
        Assert.Equal("node", plan.Command);
        Assert.Equal("server.js", plan.Arguments);
        Assert.Equal(Path.Combine(dir, "benchmark_test_22"), plan.WorkingDirectory);
    }

    [Fact]
    public void Detect_BareServerJsInSubfolder_StillStaticWhenNoNodeEntry()
    {
        // Only index.html (no server.js anywhere) — the static fallback is unchanged.
        var dir = NewProject();
        Write(dir, "benchmark_test_22/index.html", "<html><body>hi</body></html>");

        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.NotNull(plan);
        Assert.Equal("static", plan!.Kind);
        Assert.Equal(Path.Combine(dir, "benchmark_test_22"), plan.WorkingDirectory);
    }

    [Fact]
    public void Detect_BareServerJs_NodeModulesEntryIgnored()
    {
        // A server.js copy inside node_modules must never win the detection.
        var dir = NewProject();
        Write(dir, "benchmark_test_22/index.html", "<html><body>hi</body></html>");
        Write(dir, "benchmark_test_22/server.js", "require('http')");
        Write(dir, "node_modules/left-pad/server.js", "throw new Error('never run')");

        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.NotNull(plan);
        Assert.Equal("node", plan!.Kind);
        Assert.Equal(Path.Combine(dir, "benchmark_test_22"), plan.WorkingDirectory);
    }

    [Fact]
    public void Detect_IndexHtmlOnly_StaticKind()
    {
        var dir = NewProject();
        Write(dir, "index.html", "<html><body>hi</body></html>");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.NotNull(plan);
        Assert.Equal("static", plan!.Kind);
        Assert.Equal("", plan.Command);
        Assert.Equal(dir, plan.WorkingDirectory);
        Assert.Equal(0, plan.PortHint);
    }

    [Fact]
    public void Detect_IndexHtmlInSubdir_StaticWithSubdirRoot()
    {
        var dir = NewProject();
        Write(dir, "site/index.html", "<html><body>hi</body></html>");
        var plan = ServerLauncherService.DetectLaunchPlan(dir);
        Assert.NotNull(plan);
        Assert.Equal("static", plan!.Kind);
        Assert.Equal(Path.Combine(dir, "site"), plan.WorkingDirectory);
    }

    [Fact]
    public void Detect_EmptyDir_Null()
    {
        Assert.Null(ServerLauncherService.DetectLaunchPlan(NewProject()));
    }

    [Fact]
    public void Detect_MissingRoot_Null()
    {
        Assert.Null(ServerLauncherService.DetectLaunchPlan(Path.Combine(_tmp, "nope")));
        Assert.Null(ServerLauncherService.DetectLaunchPlan(null!));
    }

    // ── launch: in-process static server ─────────────────────────────────────

    [Fact]
    public async Task LaunchAsync_StaticSite_ServesAndStops()
    {
        var dir = NewProject();
        Write(dir, "index.html", "<html><head><title>Fixture Site</title></head><body><h1>Hello Static</h1></body></html>");
        Write(dir, "about.html", "<html><body><h2>About page</h2></body></html>");
        var plan = ServerLauncherService.DetectLaunchPlan(dir)!;

        await using var server = await (new ServerLauncherService()).LaunchAsync(plan, timeout: TimeSpan.FromSeconds(30));
        Assert.Equal("static", server.Kind);
        Assert.NotNull(server.Url);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var root = await http.GetStringAsync(server.Url + "/");
        Assert.Contains("Hello Static", root);

        // SPA fallback: unknown paths serve index.html (client-side routing renders).
        var deep = await http.GetStringAsync(server.Url + "/some/deep/link");
        Assert.Contains("Hello Static", deep);

        server.Stop();
        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
            await http.GetAsync(server.Url + "/"));
    }

    [Fact]
    public async Task LaunchAsync_StaticSite_ServesSubpageDirectly()
    {
        var dir = NewProject();
        Write(dir, "index.html", "<html><body><h1>Home</h1></body></html>");
        Write(dir, "about.html", "<html><body><h2>About page</h2></body></html>");
        var plan = ServerLauncherService.DetectLaunchPlan(dir)!;

        await using var server = await (new ServerLauncherService()).LaunchAsync(plan, timeout: TimeSpan.FromSeconds(30));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var about = await http.GetStringAsync(server.Url + "/about.html");
        Assert.Contains("About page", about);
    }

    // ── launch: external process that dies immediately ───────────────────────

    [Fact]
    public async Task LaunchAsync_ProcessExitsBeforeReady_ThrowsWithOutput()
    {
        var dir = NewProject();
        Write(dir, "app.py", "print('starting...')\nimport sys\nsys.exit(3)\n");
        var plan = ServerLauncherService.DetectLaunchPlan(dir)!;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            (new ServerLauncherService()).LaunchAsync(plan, timeout: TimeSpan.FromSeconds(30)));
        Assert.Contains("exited before becoming ready", ex.Message);
        Assert.Contains("starting...", ex.Message);
    }

[Fact]
    public async Task LaunchAsync_NodeProcessThatNeverListens_ThrowsWithOutput()
    {
        var dir = NewProject();
        Write(dir, "package.json", "{\"scripts\":{\"start\":\"node server.js\"}}");
        Write(dir, "server.js", "console.log('node up'); setInterval(() => {}, 1000);");
        var plan = ServerLauncherService.DetectLaunchPlan(dir)!;
        // The process never listens on HTTP — the launcher must time out and kill it.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            (new ServerLauncherService()).LaunchAsync(plan, timeout: TimeSpan.FromSeconds(8)));
        Assert.Contains("did not become ready", ex.Message);
    }

    // ── port helpers ─────────────────────────────────────────────────────────

    [Fact]
    public void FindFreePort_ReturnsFreePort()
    {
        var port = ServerLauncherService.FindFreePort();
        Assert.InRange(port, 1, 65535);
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        listener.Start(); // binds → proves the port was free
        listener.Stop();
    }
}
