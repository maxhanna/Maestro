using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Weaver.Services;

/// <summary>
/// Knows how to BUILD and RUN a scraper for the actual host environment — the antidote to the
/// "the LLM freehanded a broken Python scraper" failure mode (the IndentationError run: the
/// planner invented scraper code, wrote it with _create_file, then ran `python fetch_poke_data.py`).
///
/// The LLM never freehands scraper code. Two sanctioned paths exist:
///   1. the prompt EXPLICITLY asks for a script — then the planner may write it (the scraper-file
///      and run-a-scraper guards stand down), or
///   2. web steps (_web_fetch) keep failing — then the planner plans a "_scraper" step with the
///      URL and this service: it probes the OS, the available interpreters (python/python3/node/
///      pwsh) and the installed scraping packages (requests vs stdlib urllib vs global fetch vs
///      Invoke-RestMethod), picks the best toolchain, generates a KNOWN-GOOD script from a fixed
///      template (correct syntax, correct quoting, no invented imports), and runs it via the
///      correct interpreter invocation with the output directed at the task's demanded file.
///
/// Everything process-based goes through <see cref="ProcessRunner"/> so tests can substitute a
/// fake runner and never spawn a real process; the interpreter/package probes are cached once
/// per process.
/// </summary>
public class ScraperEnvironmentService
{
    public enum Toolchain
    {
        PythonRequests,
        PythonUrllib,
        NodeFetch,
        PowerShell
    }

    public sealed record ScraperResult(
        bool Success, string ScriptText, string Output, string? Error, string? WrittenPath);

    /// <summary>(fileName, arguments, workingDirectory) → (exit code, stdout, stderr).</summary>
    public Func<string, string, string, (int Code, string StdOut, string StdErr)> ProcessRunner { get; set; }

    // PER-INSTANCE probe cache: tests swap the runner per service, so a shared static cache
    // would leak one test's fake probe results into another's. The guard's static probe
    // (StaticInterpreterAvailable) keeps its own cache deliberately.
    private readonly ConcurrentDictionary<string, bool> ProbeCache = new(StringComparer.OrdinalIgnoreCase);

    public ScraperEnvironmentService(
        Func<string, string, string, (int Code, string StdOut, string StdErr)>? processRunner = null)
    {
        ProcessRunner = processRunner ?? RunProcess;
    }

    private static (int Code, string StdOut, string StdErr) RunProcess(string fileName, string arguments, string workDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "", "");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (-1, stdout, stderr);
            }
            return (p.ExitCode, stdout, stderr);
        }
        catch
        {
            return (-1, "", "");
        }
    }

    private bool Probe(string fileName, string arguments)
    {
        var key = fileName + "\u0000" + arguments;
        return ProbeCache.GetOrAdd(key, _ =>
        {
            var (code, _, _) = ProcessRunner(fileName, arguments, "");
            return code == 0;
        });
    }

    private static readonly ConcurrentDictionary<string, bool> StaticProbeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Static cached interpreter probe for the guard feedback paths (where no service instance
    /// exists): answers "does this machine actually have python/node/…?" so rejection feedback
    /// can honestly say the script could not even run. Never throws; cached once per process.
    /// </summary>
    public static bool StaticInterpreterAvailable(string interpreter) =>
        StaticProbeCache.GetOrAdd(interpreter, interp =>
        {
            var (code, _, _) = RunProcess(OperatingSystem.IsWindows() ? "where" : "which", interp, "");
            return code == 0;
        });

    public bool IsInterpreterAvailable(string interpreter) =>
        Probe(OperatingSystem.IsWindows() ? "where" : "which", interpreter);

    /// <summary>The working python invocation name ("python" or "python3"), or null if neither exists.</summary>
    public string? PythonInterpreter() =>
        IsInterpreterAvailable("python") ? "python"
        : IsInterpreterAvailable("python3") ? "python3"
        : null;

    public bool PythonHasPackage(string package)
    {
        var py = PythonInterpreter();
        return py != null && Probe(py, $"-c \"import {package}\"");
    }

    public bool NodeHasGlobalFetch()
    {
        if (!IsInterpreterAvailable("node")) return false;
        return ProbeCache.GetOrAdd("node\u0000fetch", _ =>
        {
            var (code, stdout, _) = ProcessRunner("node", "-e \"console.log(typeof fetch)\"", "");
            return code == 0 && stdout.Contains("function", StringComparison.Ordinal);
        });
    }

    public bool HasPowerShell() =>
        IsInterpreterAvailable("pwsh") || IsInterpreterAvailable("powershell");

    /// <summary>True when ANY interpreter that could run a generated scraper exists.</summary>
    public bool HasAnyToolchain() =>
        PythonInterpreter() != null || NodeHasGlobalFetch() || HasPowerShell();

    /// <summary>
    /// The best scraper toolchain this machine can actually run: python+requests (real HTTP
    /// library), else python+stdlib urllib (always importable), else node global fetch (≥18),
    /// else PowerShell Invoke-RestMethod. Null when no interpreter is installed at all.
    /// </summary>
    public Toolchain? BestToolchain()
    {
        if (PythonInterpreter() != null)
            return PythonHasPackage("requests") ? Toolchain.PythonRequests : Toolchain.PythonUrllib;
        if (NodeHasGlobalFetch()) return Toolchain.NodeFetch;
        if (HasPowerShell()) return Toolchain.PowerShell;
        return null;
    }

    /// <summary>
    /// A compact human-readable summary of the environment for guard feedback and planning
    /// context — "Windows; python ✓ (requests ✓ / urllib ✓); node ✓; pwsh ✗". Never throws.
    /// </summary>
    public string EnvironmentSummary()
    {
        var os = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsLinux() ? "Linux"
            : Environment.OSVersion.ToString();
        var py = PythonInterpreter();
        var sb = new StringBuilder(os).Append("; ");
        if (py != null)
            sb.Append(py).Append(" ✓ (requests ")
              .Append(PythonHasPackage("requests") ? "✓" : "✗")
              .Append(", urllib ✓); ");
        else
            sb.Append("python ✗; ");
        sb.Append("node ").Append(NodeHasGlobalFetch() ? "✓" : "✗").Append("; ");
        sb.Append("pwsh ").Append(HasPowerShell() ? "✓" : "✗");
        return sb.ToString();
    }

    /// <summary>
    /// Generates a KNOWN-GOOD scraper script for the given toolchain: fixed template, correct
    /// syntax, correct quoting, timeout, a User-Agent, and the demanded metadata line (e.g.
    /// "FETCHED_AT: yyyy-MM-dd") prepended to the output. Script takes (url, outputPath) as
    /// argv/parameters. This is what the LLM's freehand scraper never was — tested shape.
    /// </summary>
    public string GenerateScript(Toolchain toolchain, string url, string outputPath, string? metadataLine)
    {
        var meta = string.IsNullOrWhiteSpace(metadataLine) ? "" : metadataLine.TrimEnd('\n') + "\n";
        switch (toolchain)
        {
            case Toolchain.PythonRequests:
                return "import sys\n" +
                       "import requests\n" +
                       "url = sys.argv[1]\n" +
                       "out = sys.argv[2]\n" +
                       "r = requests.get(url, timeout=30, headers={\"User-Agent\": \"Weaver-scraper/1.0\"})\n" +
                       "r.raise_for_status()\n" +
                       "data = r.text\n" +
                       "with open(out, \"w\", encoding=\"utf-8\", newline=\"\") as f:\n" +
                       "    f.write(" + (string.IsNullOrEmpty(meta) ? "data" : "\"" + meta + "\" + data") + ")\n" +
                       "print(\"WROTE\", out, len(data))\n";
            case Toolchain.PythonUrllib:
                return "import sys\n" +
                       "import urllib.request\n" +
                       "url = sys.argv[1]\n" +
                       "out = sys.argv[2]\n" +
                       "req = urllib.request.Request(url, headers={\"User-Agent\": \"Weaver-scraper/1.0\"})\n" +
                       "with urllib.request.urlopen(req, timeout=30) as resp:\n" +
                       "    data = resp.read().decode(\"utf-8\", \"replace\")\n" +
                       "with open(out, \"w\", encoding=\"utf-8\", newline=\"\") as f:\n" +
                       "    f.write(" + (string.IsNullOrEmpty(meta) ? "data" : "\"" + meta + "\" + data") + ")\n" +
                       "print(\"WROTE\", out, len(data))\n";
            case Toolchain.NodeFetch:
                return "const url = process.argv[2];\n" +
                       "const out = process.argv[3];\n" +
                       "const fs = require(\"fs\");\n" +
                       "(async () => {\n" +
                       "  const res = await fetch(url, { headers: { \"User-Agent\": \"Weaver-scraper/1.0\" } });\n" +
                       "  if (!res.ok) throw new Error(\"HTTP \" + res.status);\n" +
                       "  let text = await res.text();\n" +
                       (string.IsNullOrEmpty(meta) ? "" : "  text = " + "\"" + meta + "\" + text;\n") +
                       "  fs.writeFileSync(out, text, \"utf8\");\n" +
                       "  console.log(\"WROTE\", out, text.length);\n" +
                       "})().catch((e) => { console.error(e.message); process.exit(1); });\n";
            case Toolchain.PowerShell:
                return "param([string]$Url = $args[0], [string]$Out = $args[1])\n" +
                       "$r = Invoke-WebRequest -Uri $Url -Headers @{ \"User-Agent\" = \"Weaver-scraper/1.0\" } -TimeoutSec 30 -UseBasicParsing\n" +
                       (string.IsNullOrEmpty(meta) ? "" : "Set-Content -Path $Out -Value \"" + meta + "\" -Encoding UTF8 -NoNewline\n") +
                       "$r.Content | Add-Content -Path $Out -Encoding UTF8\n" +
                       "Write-Output \"WROTE $Out $($r.Content.Length)\"\n";
            default:
                throw new ArgumentOutOfRangeException(nameof(toolchain));
        }
    }

    public string MetadataLineForNow() => $"FETCHED_AT: {DateTime.Now:yyyy-MM-dd}";

    /// <summary>
    /// Builds the known-good scraper for the best available toolchain, writes it next to the
    /// target, runs it via the correct interpreter, and returns the outcome. Writes the fetched
    /// content to <paramref name="outputPath"/> (created if its folder is missing). Never throws:
    /// failures come back in <see cref="ScraperResult.Error"/>.
    /// </summary>
    public virtual async Task<ScraperResult> TryRunScraperAsync(
        string url, string? outputPath, string workDir, string? metadataLine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return new ScraperResult(false, "", "", $"Not a fetchable URL: \"{url}\".", null);
        var toolchain = BestToolchain();
        if (toolchain == null)
            return new ScraperResult(false, "", "",
                "No script interpreter (python/python3/node/pwsh) is installed on this machine, so a scraper cannot run — use a _web_fetch step instead.", null);
        var target = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(workDir, "scraper_output.txt")
            : outputPath;
        var ext = toolchain switch
        {
            Toolchain.PythonRequests or Toolchain.PythonUrllib => ".py",
            Toolchain.NodeFetch => ".js",
            Toolchain.PowerShell => ".ps1",
            _ => ".txt"
        };
        var scriptPath = Path.Combine(workDir, "weaver_scraper_run" + ext);
        var script = GenerateScript(toolchain.Value, url, target, metadataLine);
        try
        {
            var parentDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parentDir))
                System.IO.Directory.CreateDirectory(parentDir);
            await System.IO.File.WriteAllTextAsync(scriptPath, script, ct);
        }
        catch (Exception ex)
        {
            return new ScraperResult(false, script, "", $"Could not prepare the scraper script: {ex.Message}", null);
        }

        var (interp, runArgs) = toolchain switch
        {
            Toolchain.PythonRequests or Toolchain.PythonUrllib =>
                (PythonInterpreter()!, $"\"{scriptPath}\" \"{url}\" \"{target}\""),
            Toolchain.NodeFetch =>
                ("node", $"\"{scriptPath}\" \"{url}\" \"{target}\""),
            Toolchain.PowerShell =>
                (IsInterpreterAvailable("pwsh") ? "pwsh" : "powershell",
                 $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" \"{url}\" \"{target}\""),
            _ => ("", "")
        };

        try
        {
            var (code, stdout, stderr) = ProcessRunner(interp, runArgs, workDir);
            var written = System.IO.File.Exists(target) ? target : null;
            if (code == 0 && written != null)
                return new ScraperResult(true, script, stdout, null, written);
            var err = $"Scraper run failed (exit {code}): {stderr.Trim()}\n{stdout.Trim()}".Trim();
            return new ScraperResult(false, script, stdout, err, written);
        }
        catch (Exception ex)
        {
            return new ScraperResult(false, script, "", $"Scraper run failed: {ex.Message}", null);
        }
    }
}
