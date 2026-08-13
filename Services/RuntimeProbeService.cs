using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Weaver.Services;

/// <summary>
/// Probes the host for installed runtimes/tools (python, node, dotnet, go, …) and their
/// versions — the antidote to the benchmark-4 failure mode where the planner freehanded a
/// Python/Flask server without knowing whether this machine even has Python. Discovery
/// surfaces a compact "RUNTIME AVAILABILITY" section so the planner can choose a language
/// whose runtime actually exists (or steer away from writing scripts entirely).
///
/// Everything process-based goes through <see cref="ProcessRunner"/> so tests substitute a
/// fake runner and never spawn a real process. Probe results are cached per instance (the
/// controller caches per project in the DB on top of this, with a TTL). Never throws —
/// a machine with nothing installed yields an empty "available" list, not an exception.
/// </summary>
public class RuntimeProbeService
{
    /// <summary>One probed runtime: Name + Version (null when not found).</summary>
    public sealed record RuntimeInfo(string Name, string? Version);

    /// <summary>(fileName, arguments, workingDirectory) → (exit code, stdout, stderr).</summary>
    public Func<string, string, string, (int Code, string StdOut, string StdErr)> ProcessRunner { get; set; }

    private readonly ConcurrentDictionary<string, RuntimeInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeProbeService(
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
            if (!p.WaitForExit(5000))
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

    /// <summary>
    /// The probes run per runtime: (display name, executable, version flag). Version flags
    /// match each tool's real CLI (go/java differ from the --version convention).
    /// </summary>
    private static readonly (string Name, string Cmd, string Args)[] Probes =
    {
        ("python", "python", "--version"),
        ("python3", "python3", "--version"),
        ("pip", "pip", "--version"),
        ("pip3", "pip3", "--version"),
        ("node", "node", "--version"),
        ("npm", "npm", "--version"),
        ("npx", "npx", "--version"),
        ("dotnet", "dotnet", "--version"),
        ("go", "go", "version"),
        ("java", "java", "-version"),
        ("javac", "javac", "-version"),
        ("ruby", "ruby", "--version"),
        ("php", "php", "--version"),
        ("cargo", "cargo", "--version"),
        ("gcc", "gcc", "--version"),
        ("g++", "g++", "--version"),
        ("git", "git", "--version"),
        ("pwsh", "pwsh", "--version"),
        ("powershell", "powershell", "-NoProfile -Command \"$PSVersionTable.PSVersion.ToString()\"")
    };

    /// <summary>
    /// Probes every known runtime once (cached per instance). A tool counts as available when
    /// the process exits 0 AND produces any output; the version is the first non-empty line of
    /// stdout (stderr fallback — python2/java print version info to stderr). Missing tools and
    /// probes that throw/time out become RuntimeInfo(Name, null).
    /// </summary>
    public virtual List<RuntimeInfo> ProbeAll()
    {
        var result = new List<RuntimeInfo>(Probes.Length);
        foreach (var (name, cmd, args) in Probes)
        {
            result.Add(_cache.GetOrAdd(name, _ => ProbeOne(name, cmd, args)));
        }
        return result;
    }

    private RuntimeInfo ProbeOne(string name, string cmd, string args)
    {
        try
        {
            var (code, stdout, stderr) = ProcessRunner(cmd, args, "");
            if (code != 0) return new RuntimeInfo(name, null);
            var version = FirstNonEmptyLine(stdout, stderr);
            return new RuntimeInfo(name, version);
        }
        catch
        {
            // The runner threw (command not found, spawn failure, …) — the tool is unavailable.
            return new RuntimeInfo(name, null);
        }
    }

    private static string? FirstNonEmptyLine(params string[] blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var line in block.Split('\n'))
            {
                var trimmed = line.Trim('\r', ' ', '\t');
                if (trimmed.Length > 0)
                    return trimmed.Length > 120 ? trimmed[..120] : trimmed;
            }
        }
        return null;
    }

    /// <summary>
    /// A compact, planner-facing summary: the runtimes actually found on this machine plus the
    /// ones that are NOT installed, so the planner picks a language that can really run here
    /// instead of inventing toolchains. Empty result (nothing installed) still returns a
    /// meaningful block telling the planner not to assume a runtime exists.
    /// </summary>
    public static string FormatForContext(List<RuntimeInfo> probes)
    {
        var available = probes.Where(p => p.Version != null).ToList();
        var missing = probes.Where(p => p.Version == null).Select(p => p.Name).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("### RUNTIME AVAILABILITY (host machine) ###");
        if (available.Count > 0)
        {
            sb.AppendLine("Available: " + string.Join("; ", available.Select(p => $"{p.Name} ({p.Version})")));
        }
        else
        {
            sb.AppendLine("Available: NONE — no probed runtime/tool is installed on this machine.");
        }
        if (missing.Count > 0)
        {
            sb.AppendLine("NOT available: " + string.Join(", ", missing));
        }
        sb.AppendLine("Choose a language/runtime from the AVAILABLE list above. Do NOT assume python, node, or any other tool exists unless it is listed as available.");
        return sb.ToString();
    }

    /// <summary>Short one-line summary for log rows, e.g. "python ✓, node ✓, go ✗".</summary>
    public static string ShortSummary(List<RuntimeInfo> probes) =>
        string.Join(", ", probes.Select(p => $"{p.Name} {(p.Version != null ? "✓" : "✗")}"));
}
