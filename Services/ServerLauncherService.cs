using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Spins up the web server a project sits in — for ANY project type (C#/dotnet, Node,
/// Python, Go, Rust, Java, PHP, Ruby, plain static HTML). Detection is deterministic:
/// the same project layout always yields the same launch plan, so a basic model never
/// has to guess how a project starts. The launcher owns the spawned process, waits for
/// the server to actually answer HTTP, and stops it (killing the whole process tree)
/// when the test is done.
///
/// Detection rules (first match wins):
///  • .sln / .csproj        → `dotnet run --project <csproj> --urls http://127.0.0.1:{port}`
///  • package.json          → `npm run <dev|start|serve|first script>` (+ PORT env)
///  • manage.py             → `python manage.py runserver 127.0.0.1:{port}`
///  • FastAPI import        → `uvicorn <main|app>:app --port {port}`
///  • streamlit import      → `streamlit run <app.py> --server.port {port}`
///  • Flask import          → `flask run --host 127.0.0.1 --port {port}`
///  • other .py entry       → `python <app|main|server|run>.py` (+ PORT env)
///  • go.mod                → `go run .` (+ PORT env)
///  • Cargo.toml            → `cargo run` (+ PORT env)
///  • pom.xml / gradlew     → `mvn spring-boot:run` / `./gradlew bootRun` (+ --server.port)
///  • index.php             → `php -S 127.0.0.1:{port} -t <root>`
///  • Gemfile + config.ru   → `rackup -p {port}` / rails → `bundle exec rails server`
///  • index.html            → in-process static file server (Kestrel, no external tool)
///
/// The {port} placeholder in an arguments string is replaced with the free port the
/// launcher reserves; PORT is also injected as an environment variable as a fallback
/// for frameworks that only read the env var. Nothing here requires an LLM — it is
/// pure filesystem + process logic.
/// </summary>
public class ServerLauncherService
{
    /// <summary>How a project's server is started (the deterministic detection result).</summary>
    public sealed record ServerLaunchPlan(
        string Kind,               // dotnet | node | python | go | cargo | java | php | ruby | static
        string Command,            // executable to spawn ("" for in-process static)
        string Arguments,          // argument string; "{port}" is replaced with the chosen port
        string WorkingDirectory,
        int PortHint,              // the project's default port (0 = launcher picks)
        string Description);       // human-readable "how this project starts"

    private static readonly string[] IgnoredDirs =
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".svn", "dist", "build", "out",
        ".idea", ".vscode", "__pycache__", ".next", ".nuget", "coverage", ".venv", "venv",
        "vendor", "target", ".gradle", "artifacts", "packages"
    };

    // Inject a process factory so tests can substitute a recorder without spawning
    // anything real; default is a real Process.Start.
    public Func<ProcessStartInfo, Process> ProcessFactory { get; set; } = psi =>
    {
        var p = Process.Start(psi);
        if (p == null) throw new InvalidOperationException("Process.Start returned null");
        return p;
    };

    /// <summary>
    /// Detect the launch plan for a project root, or null when the project has no web
    /// server and no index.html to serve. Deterministic: identical layouts → identical plans.
    /// </summary>
    public static ServerLaunchPlan? DetectLaunchPlan(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            return null;

        // ── C# / .NET ────────────────────────────────────────────────────────
        var csproj = FindShallowest(projectRoot, "*.csproj");
        if (csproj != null)
        {
            var rel = Path.GetRelativePath(projectRoot, csproj);
            var arg = rel == Path.GetFileName(csproj)
                ? Path.GetFileName(csproj)
                : rel.Replace('\\', '/');
            return new ServerLaunchPlan("dotnet", "dotnet",
                $"run --project {Quote(arg)} --urls http://127.0.0.1:{{port}}",
                projectRoot, 5000,
                $"dotnet run (project {Path.GetFileName(csproj)})");
        }
        var sln = FindShallowest(projectRoot, "*.sln");
        if (sln != null)
        {
            var proj = FindShallowestCsprojNear(projectRoot, sln);
            var target = proj != null
                ? Path.GetRelativePath(projectRoot, proj).Replace('\\', '/')
                : Path.GetFileName(sln);
            return new ServerLaunchPlan("dotnet", "dotnet",
                $"run --project {Quote(target)} --urls http://127.0.0.1:{{port}}",
                projectRoot, 5000,
                $"dotnet run (solution {Path.GetFileName(sln)})");
        }

        // ── Node ─────────────────────────────────────────────────────────────
        var packageJson = Path.Combine(projectRoot, "package.json");
        if (File.Exists(packageJson))
        {
            var script = FindNodeStartScript(packageJson);
            if (script != null)
                return new ServerLaunchPlan("node", "npm", $"run {script}", projectRoot, 3000,
                    $"npm run {script} (package.json)");
            var entry = FindFirstExisting(projectRoot, "index.js", "server.js", "app.js", "main.js", "index.ts");
            if (entry != null)
                return new ServerLaunchPlan("node", "node", entry, projectRoot, 3000,
                    $"node {entry} (no start script)");
        }

        // ── Python ───────────────────────────────────────────────────────────
        if (FindFirstExisting(projectRoot, "manage.py") != null)
            return new ServerLaunchPlan("python", "python",
                "manage.py runserver 127.0.0.1:{port}", projectRoot, 8000,
                "python manage.py runserver (Django)");
        var pySource = FindPythonEntry(projectRoot);
        if (pySource != null)
        {
            if (ContainsImport(projectRoot, "streamlit"))
                return new ServerLaunchPlan("python", "streamlit",
                    $"run {pySource} --server.port {{port}} --server.headless true", projectRoot, 8501,
                    $"streamlit run {pySource}");
            if (ContainsImport(projectRoot, "fastapi"))
            {
                var mod = Path.GetFileNameWithoutExtension(pySource);
                return new ServerLaunchPlan("python", "uvicorn",
                    $"{mod}:app --host 127.0.0.1 --port {{port}}", projectRoot, 8000,
                    $"uvicorn {mod}:app (FastAPI)");
            }
            if (ContainsImport(projectRoot, "flask"))
                return new ServerLaunchPlan("python", "flask",
                    "run --host 127.0.0.1 --port {port}", projectRoot, 5000,
                    "flask run (Flask)");
            return new ServerLaunchPlan("python", "python", pySource, projectRoot, 8000,
                $"python {pySource}");
        }

        // ── Go ───────────────────────────────────────────────────────────────
        if (File.Exists(Path.Combine(projectRoot, "go.mod")))
            return new ServerLaunchPlan("go", "go", "run .", projectRoot, 8080, "go run .");

        // ── Rust ─────────────────────────────────────────────────────────────
        if (File.Exists(Path.Combine(projectRoot, "Cargo.toml")))
            return new ServerLaunchPlan("cargo", "cargo", "run", projectRoot, 8080, "cargo run");

        // ── Java ─────────────────────────────────────────────────────────────
        if (File.Exists(Path.Combine(projectRoot, "pom.xml")))
            return new ServerLaunchPlan("java", "mvn",
                "spring-boot:run -Dspring-boot.run.arguments=--server.port={port}", projectRoot, 8080,
                "mvn spring-boot:run");
        if (FindFirstExisting(projectRoot, "gradlew", "gradlew.bat") != null)
            return new ServerLaunchPlan("java",
                OperatingSystem.IsWindows() ? "gradlew.bat" : "./gradlew",
                "bootRun --args=--server.port={port}", projectRoot, 8080, "gradlew bootRun");

        // ── PHP ──────────────────────────────────────────────────────────────
        if (FindFirstExisting(projectRoot, "index.php") != null)
            return new ServerLaunchPlan("php", "php",
                "-S 127.0.0.1:{port} -t .", projectRoot, 8000, "php -S (built-in server)");

        // ── Ruby ─────────────────────────────────────────────────────────────
        if (File.Exists(Path.Combine(projectRoot, "Gemfile")))
        {
            if (ContainsText(projectRoot, "config.ru") && FindFirstExisting(projectRoot, "config.ru") != null)
                return new ServerLaunchPlan("ruby", "rackup",
                    "-p {port} -o 127.0.0.1", projectRoot, 3000, "rackup -p (Rack app)");
            if (Directory.Exists(Path.Combine(projectRoot, "app")))
                return new ServerLaunchPlan("ruby", "bundle",
                    "exec rails server -p {port} -b 127.0.0.1", projectRoot, 3000,
                    "bundle exec rails server (Rails)");
            var rubyEntry = FindFirstExisting(projectRoot, "app.rb", "server.rb", "main.rb");
            if (rubyEntry != null)
                return new ServerLaunchPlan("ruby", "ruby", rubyEntry, projectRoot, 4567,
                    $"ruby {rubyEntry}");
        }

        // ── Bare Node entry (no package.json) ────────────────────────────────
        // An agent-written server in a SUBFOLDER — the benchmark-22 shape
        // (benchmark_test_22/server.js reading process.env.PORT) — must actually run.
        // Without this, the static fallback below serves index.html from disk and 404s
        // every route the server owns (/api/health), so the live browser test shows a
        // page that never went through the agent's real server. Shallowest-first so the
        // most project-like entry wins; node_modules/build output is already filtered.
        var bareNode = FindShallowestNodeEntry(projectRoot);
        if (bareNode != null)
        {
            var dir = Path.GetDirectoryName(bareNode) ?? projectRoot;
            var entry = Path.GetFileName(bareNode);
            var relDir = Path.GetRelativePath(projectRoot, dir);
            return new ServerLaunchPlan("node", "node", entry, dir, 3000,
                $"node {entry} ({(relDir == "." ? "project root" : relDir)})");
        }

        // ── Static HTML (no server — serve the index ourselves) ─────────────
        var index = FindIndexHtml(projectRoot);
        if (index != null)
            return new ServerLaunchPlan("static", "", "", index, 0,
                "static site (in-process file server over " + Path.GetFileName(index) + ")");

        return null;
    }

    /// <summary>
    /// Launch the plan: reserve a free port, spawn the process (or start the in-process
    /// static server), and poll HTTP until the server answers or the timeout elapses.
    /// Returns a <see cref="RunningServer"/> whose StopAsync kills the process tree.
    /// Throws <see cref="InvalidOperationException"/> when the server never comes up —
    /// with the captured process output attached to the message so failures are
    /// inspectable, not silent.
    /// </summary>
    public async Task<RunningServer> LaunchAsync(ServerLaunchPlan plan, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var port = plan.Kind == "static"
            ? FindFreePort()
            : FindFreePort(plan.PortHint);
        var url = $"http://127.0.0.1:{port}";
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));

        if (plan.Kind == "static")
            return await StaticSiteServer.StartAsync(plan.WorkingDirectory, port, ct);

        var psi = new ProcessStartInfo
        {
            FileName = ResolveCommand(plan.Command),
            Arguments = plan.Arguments.Replace("{port}", port.ToString()),
            WorkingDirectory = plan.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["PORT"] = port.ToString();
        psi.Environment["HOST"] = "127.0.0.1";

        var log = new StringBuilder();
        var npmInstallAttempted = false;
        var pipInstallAttempted = false;
        var process = ProcessFactory(psi);
        var pump = PumpOutputAsync(process, log);

        // Poll the health endpoint until the server answers (or the process died).
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                try { await pump.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
                string tail;
                lock (log) tail = log.ToString();
                // AGILE DEPENDENCY RECOVERY: the process died because a require() module is
                // missing (e.g. the agent wrote `require('express')` but never ran `npm install`
                // — the exact benchmark-22 failure "Cannot find module 'express'"). The launcher
                // installs the project's declared dependencies deterministically and re-spawns
                // ONCE — no model round, no planner step — so a server that merely lacks its
                // node_modules still gets live-tested instead of failing the whole run.
                if (!npmInstallAttempted && IsNodeMissingModuleFailure(tail) &&
                    File.Exists(Path.Combine(plan.WorkingDirectory, "package.json")))
                {
                    npmInstallAttempted = true;
                    var installed = RunNpmInstall(plan.WorkingDirectory);
                    log.AppendLine($"[launcher] npm install {(installed ? "succeeded" : "failed")} — re-spawning {plan.Command} {plan.Arguments}");
                    if (installed)
                    {
                        KillTree(process);
                        process = ProcessFactory(psi);
                        pump = PumpOutputAsync(process, log);
                        continue;
                    }
                }
                // Python mirror of the same recovery: the process died because an import is
                // missing (e.g. the agent wrote `from flask import Flask` but never ran
                // `pip install`). Install the named module deterministically and re-spawn
                // ONCE — no model round — so a server that merely lacks its dependency still
                // gets live-tested. requirements.txt, when present, covers every declared
                // dependency in one shot (mirrors `npm install` pulling package.json).
                if (!pipInstallAttempted && IsPythonMissingModuleFailure(tail))
                {
                    pipInstallAttempted = true;
                    var module = ExtractMissingPythonModule(tail);
                    var installed = RunPipInstall(plan.WorkingDirectory, module);
                    log.AppendLine($"[launcher] pip install {(installed ? "succeeded" : "failed")} — re-spawning {plan.Command} {plan.Arguments}");
                    if (installed)
                    {
                        KillTree(process);
                        process = ProcessFactory(psi);
                        pump = PumpOutputAsync(process, log);
                        continue;
                    }
                }
                throw new InvalidOperationException(
                    $"Server process exited before becoming ready (code {process.ExitCode}). " +
                    $"Command: {plan.Command} {plan.Arguments}.\nOutput:\n{tail}");
            }
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode)
                {
                    return new RunningServer(url, plan.Kind, process, log);
                }
                lastError = $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                lastError = ex.GetType().Name;
            }
            await Task.Delay(400, ct);
        }
        string output;
        try { await pump.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
        lock (log) output = log.ToString();
        KillTree(process);
        throw new InvalidOperationException(
            $"Server did not become ready within {(timeout ?? TimeSpan.FromSeconds(120)).TotalSeconds}s " +
            $"at {url} (last error: {lastError}). Command: {plan.Command} {plan.Arguments}.\nOutput:\n{output}");
    }

    /// <summary>True when the process output is a Node MODULE_NOT_FOUND failure (a required
    /// dependency is missing — "Cannot find module 'express'").</summary>
    internal static bool IsNodeMissingModuleFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        return output.Contains("Cannot find module", StringComparison.Ordinal) ||
               output.Contains("MODULE_NOT_FOUND", StringComparison.Ordinal) ||
               output.Contains("module not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the process output is a Python missing-module failure (a required
    /// dependency is missing — "ModuleNotFoundError: No module named 'flask'"). Bare
    /// ImportError is NOT enough — "cannot import name X from Y" is a different bug inside
    /// an installed module, not an installable dependency.</summary>
    internal static bool IsPythonMissingModuleFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var o = output.ToLowerInvariant();
        return o.Contains("modulenotfounderror", StringComparison.Ordinal) ||
               o.Contains("no module named", StringComparison.Ordinal);
    }

    /// <summary>Extracts the missing module name from a Python ModuleNotFoundError
    /// ("No module named 'flask'"), or null when the output names no module. Mirrors
    /// AgentController.ExtractMissingModule but lives in the launcher so the launch
    /// recovery needs no controller dependency.</summary>
    internal static string? ExtractMissingPythonModule(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = Regex.Match(output, @"No module named\s*'([^']+)'", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Runs the python dependency install for the missing-module recovery: `pip install -r
    /// requirements.txt` when the project declares one (mirrors `npm install` pulling
    /// package.json), else `pip install <module>` for the exact module the error named.
    /// Uses `python -m pip` so the pip .cmd shim on Windows never has to be exec'd directly;
    /// bounded timeout. Returns true when the install exited 0.
    /// </summary>
    internal static bool RunPipInstall(string workDir, string? module)
    {
        try
        {
            var reqFile = Path.Combine(workDir, "requirements.txt");
            string args;
            if (File.Exists(reqFile))
                args = "-m pip install --no-input --no-cache-dir -r requirements.txt";
            else if (!string.IsNullOrWhiteSpace(module))
                args = $"-m pip install --no-input --no-cache-dir {module}";
            else
                return false; // nothing to install and nothing named — let the error surface
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = args,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(240)))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            Task.WaitAll(stdout, stderr);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs `npm install` in the project (cmd /c on Windows — npm is a .cmd shim
    /// Process.Start cannot exec directly) with a bounded timeout. Returns true when the
    /// install exited 0.</summary>
    internal static bool RunNpmInstall(string workDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows()
                    ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                    : "npm",
                Arguments = OperatingSystem.IsWindows()
                    ? "/c npm install --no-audit --no-fund"
                    : "install --no-audit --no-fund",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(180)))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            Task.WaitAll(stdout, stderr);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads stdout/stderr line-by-line into the log so failures always carry
    /// the server's output, even when the pipes never close (orphaned grandchildren).</summary>
    private static Task PumpOutputAsync(Process process, StringBuilder log)
    {
        async Task Drain(System.IO.StreamReader reader)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lock (log)
                    {
                        if (log.Length < 20000) log.AppendLine(line);
                    }
                }
            }
            catch { }
        }
        var stdout = Drain(process.StandardOutput);
        var stderr = Drain(process.StandardError);
        return Task.WhenAll(stdout, stderr);
    }

    /// <summary>Kills a process and its ENTIRE descendant tree. On Windows, .NET's
    /// Kill(entireProcessTree) misses grandchildren spawned via cmd shims (npm → node),
    /// leaving orphans that hold the output pipes open — taskkill /T enumerates and kills
    /// the tree natively and reliably.</summary>
    internal static void KillTree(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows() && !process.HasExited)
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/PID {process.Id} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                killer?.WaitForExit(5000);
                return;
            }
        }
        catch { }
        try { process.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>Finds a free TCP port on loopback, preferring <paramref name="preferred"/>.</summary>
    public static int FindFreePort(int preferred = 0)
    {
        if (preferred > 0 && IsPortFree(preferred)) return preferred;
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch { return false; }
    }

    // ── detection helpers ────────────────────────────────────────────────────

    private static string? FindShallowest(string root, string pattern)
    {
        var best = (Depth: int.MaxValue, Path: (string?)null);
        foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            if (IsIgnoredPath(root, file)) continue;
            var depth = file.Split(Path.DirectorySeparatorChar).Length;
            if (depth < best.Depth) best = (depth, file);
        }
        return best.Path;
    }

    private static string? FindShallowestCsprojNear(string root, string slnPath)
    {
        var slnDir = Path.GetDirectoryName(slnPath) ?? root;
        var entries = Directory.EnumerateFiles(slnDir, "*.csproj", SearchOption.TopDirectoryOnly).ToList();
        if (entries.Count == 1) return entries[0];
        if (entries.Count > 1)
        {
            // Prefer the entry project of the solution by name similarity, else the first.
            var slnName = Path.GetFileNameWithoutExtension(slnPath);
            return entries.FirstOrDefault(e =>
                Path.GetFileNameWithoutExtension(e).Contains(slnName, StringComparison.OrdinalIgnoreCase)) ?? entries[0];
        }
        return FindShallowest(root, "*.csproj");
    }

    private static string? FindNodeStartScript(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
                return null;
            var names = scripts.EnumerateObject().Select(p => p.Name).ToList();
            foreach (var preferred in new[] { "dev", "start", "serve", "preview", "run" })
            {
                var hit = names.FirstOrDefault(n => n.Equals(preferred, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            // Any script that mentions a server-ish file, else the first script.
            foreach (var name in names)
            {
                var body = scripts.GetProperty(name).GetString() ?? "";
                if (body.Contains("server") || body.Contains("index.") || body.Contains("app.") ||
                    body.Contains("vite") || body.Contains("next") || body.Contains("nuxt"))
                    return name;
            }
            return names.Count > 0 ? names[0] : null;
        }
        catch { return null; }
    }

    private static string? FindPythonEntry(string root)
    {
        // Prefer manage.py already handled; then streamlit/flask entry candidates.
        foreach (var name in new[] { "app.py", "main.py", "server.py", "run.py", "index.py", "wsgi.py" })
        {
            var hit = FindFirstExisting(root, name);
            if (hit != null) return hit;
        }
        // Shallowest .py that looks like an entry point (has app/main/run in the name).
        var best = (Depth: int.MaxValue, Path: (string?)null);
        foreach (var file in Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories))
        {
            if (IsIgnoredPath(root, file)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            if (name is "app" or "main" or "server" or "run" or "index" or "wsgi" or "application")
            {
                var depth = file.Split(Path.DirectorySeparatorChar).Length;
                if (depth < best.Depth) best = (depth, file);
            }
        }
        return best.Path;
    }

    private static bool ContainsImport(string root, string module)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.py", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var text = File.ReadAllText(file);
                if (Regex.IsMatch(text, $@"^\s*(from\s+{Regex.Escape(module)}\s+import|import\s+{Regex.Escape(module)}\b)", RegexOptions.Multiline))
                    return true;
            }
            catch { }
        }
        return false;
    }

    private static bool ContainsText(string root, string fileName)
    {
        var path = Path.Combine(root, fileName);
        return File.Exists(path);
    }

    private static string? FindFirstExisting(string root, params string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path)) return name;
        }
        return null;
    }

    /// <summary>Node entry names in priority order (mirrors the package.json branch).</summary>
    private static readonly string[] NodeEntryNames = { "index.js", "server.js", "app.js", "main.js", "index.ts" };

    /// <summary>
    /// Recursively finds the shallowest bare node entry (no package.json required) — a
    /// server the agent wrote in a subfolder (benchmark_test_22/server.js). Returns the
    /// full path, or null. At equal depth the name-priority order wins; ignored dirs
    /// (node_modules, dist, …) are skipped so a stray dependency copy never wins.
    /// </summary>
    private static string? FindShallowestNodeEntry(string root)
    {
        var best = (Depth: int.MaxValue, NameIndex: int.MaxValue, Path: (string?)null);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (IsIgnoredPath(root, file)) continue;
            var nameIndex = Array.IndexOf(NodeEntryNames, Path.GetFileName(file));
            if (nameIndex < 0) continue;
            var depth = file.Split(Path.DirectorySeparatorChar).Length;
            if (depth < best.Depth || (depth == best.Depth && nameIndex < best.NameIndex))
                best = (depth, nameIndex, file);
        }
        return best.Path;
    }

    private static string? FindIndexHtml(string root)
    {
        var best = (Depth: int.MaxValue, Path: (string?)null);
        foreach (var file in Directory.EnumerateFiles(root, "index.html", SearchOption.AllDirectories))
        {
            if (IsIgnoredPath(root, file)) continue;
            var depth = file.Split(Path.DirectorySeparatorChar).Length;
            if (depth < best.Depth) best = (depth, file);
        }
        if (best.Path != null && best.Depth > 0)
            return Path.GetDirectoryName(best.Path) ?? root;
        return best.Path != null ? root : null;
    }

    private static bool IsIgnoredPath(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        foreach (var segment in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (IgnoredDirs.Contains(segment, StringComparer.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    /// <summary>
    /// Resolves a command name to a runnable path. On Windows many launchers (npm, mvn,
    /// uvicorn, streamlit, …) are .cmd/.bat shims that plain CreateProcess cannot execute
    /// by bare name — walk PATH for the standard extensions. Returns the input unchanged
    /// when nothing is found so Process.Start reports its own error.
    /// </summary>
    private static string ResolveCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return command;
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : command;
        if (!OperatingSystem.IsWindows()) return command;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com" })
            {
                var full = Path.Combine(dir.Trim('"'), command + ext);
                if (File.Exists(full)) return full;
            }
        }
        return command;
    }
}

/// <summary>A live, running server owned by the launcher — URL, process handle, and
/// captured output. Dispose/StopAsync kills the whole process tree and frees the port.</summary>
public sealed class RunningServer : IAsyncDisposable
{
    public string Url { get; }
    public string Kind { get; }
    public Process? Process { get; }
    public StringBuilder Log { get; }
    public bool IsStopped { get; private set; }
    /// <summary>Optional ownerless cleanup (e.g. the in-process static host) run on Stop.</summary>
    public Action? OnStop { get; set; }

    internal RunningServer(string url, string kind, Process? process, StringBuilder log)
    {
        Url = url;
        Kind = kind;
        Process = process;
        Log = log;
    }

    public string LogTail(int maxChars = 4000)
    {
        lock (Log)
        {
            var text = Log.ToString();
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
    }

    public void Stop()
    {
        if (IsStopped) return;
        IsStopped = true;
        try { OnStop?.Invoke(); } catch { }
        if (Process != null && !Process.HasExited)
        {
            ServerLauncherService.KillTree(Process);
            try { Process.WaitForExit(3000); } catch { }
            Process.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-process Kestrel static file server for projects that have no server of
/// their own (plain index.html sites) — the "else open the index" branch of the test
/// pipeline. No external tool, cross-platform, and it serves the SPA-style paths too
/// (unknown routes fall back to index.html so client-side routing still renders).</summary>
public static class StaticSiteServer
{
    public static async Task<RunningServer> StartAsync(string root, int port, CancellationToken ct = default)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder(
            new Microsoft.AspNetCore.Builder.WebApplicationOptions
            {
                ContentRootPath = root,
                WebRootPath = root
            });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        // SPA fallback: unknown paths serve index.html so deep links render the app.
        app.MapFallbackToFile("index.html");
        var log = new StringBuilder();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                await app.StartAsync(ct);
                started.TrySetResult(true);
                await app.WaitForShutdownAsync(ct);
            }
            catch (Exception ex)
            {
                lock (log) log.AppendLine(ex.ToString());
                started.TrySetException(ex);
            }
        }, CancellationToken.None);

        // Wait until the port actually answers (or the host failed to start).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        while (DateTime.UtcNow < deadline)
        {
            if (started.Task.IsFaulted || started.Task.IsCanceled)
            {
                try { await started.Task; } catch (Exception ex)
                {
                    throw new InvalidOperationException($"Static site server failed to start: {ex.Message}");
                }
            }
            try
            {
                using var resp = await http.GetAsync($"http://127.0.0.1:{port}/", HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    break;
            }
            catch { }
            await Task.Delay(150, ct);
        }
        var server = new RunningServer($"http://127.0.0.1:{port}", "static", null, log);
        // Register an ownerless stop: dispose the host when the server handle is stopped.
        var host = app;
        server.OnStop = () =>
        {
            try { host.StopAsync().GetAwaiter().GetResult(); } catch { }
            host.DisposeAsync().AsTask().Wait(2000);
        };
        return server;
    }
}