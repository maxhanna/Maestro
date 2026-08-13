using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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
    /// True when a URL looks like a paginated list endpoint (has a ?limit= / &amp;limit= or
    /// offset param) — the signal that the scraper should loop over pages instead of a single
    /// fetch. A plain article/endpoint URL (no pagination params) stays a single fetch.
    /// </summary>
    public static bool ShouldPaginate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Regex.IsMatch(url, @"[?&](?:limit|offset)=\d+", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Escapes the demanded metadata line ("FETCHED_AT: yyyy-MM-dd\n") into a correct
    /// double-quoted string literal for the generated Python/Node script. The OLD inline
    /// embedding put a REAL newline inside the literal ("FETCHED_AT: 2026-08-13
    /// " + data) — a guaranteed SyntaxError in the very script that was supposed to be
    /// known-good. PowerShell double-quoted strings allow real newlines, so it embeds raw.
    /// </summary>
    private static string StringLiteralFor(string? text) =>
        "\"" + (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";

    /// <summary>
    /// Generates a KNOWN-GOOD scraper script for the given toolchain: fixed template, correct
    /// syntax, correct quoting, timeout, a User-Agent, and the demanded metadata line (e.g.
    /// "FETCHED_AT: yyyy-MM-dd") prepended to the output. Script takes (url, outputPath) as
    /// argv/parameters. When <paramref name="paginate"/> is set (a ?limit=/offset URL like the
    /// full-Pokedex benchmark), the template loops over pages — following the API's own
    /// "next" cursor when present, else advancing offset by the page size — merging the
    /// results into one JSON output ({count, results} when the API reports a count), capped at
    /// 50 pages so a broken cursor can't loop forever. Non-paginated responses degrade to a
    /// single fetch that emits the payload verbatim. This is what the LLM's freehand scraper
    /// never was — tested shape.
    /// </summary>
    public string GenerateScript(Toolchain toolchain, string url, string outputPath, string? metadataLine, bool paginate = false)
    {
        var meta = string.IsNullOrWhiteSpace(metadataLine) ? "" : metadataLine.TrimEnd('\n') + "\n";
        return paginate
            ? GeneratePaginatedScript(toolchain, meta)
            : GenerateSingleScript(toolchain, meta);
    }

    private string GenerateSingleScript(Toolchain toolchain, string meta)
    {
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
                       "    f.write(" + (string.IsNullOrEmpty(meta) ? "data" : StringLiteralFor(meta) + " + data") + ")\n" +
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
                       "    f.write(" + (string.IsNullOrEmpty(meta) ? "data" : StringLiteralFor(meta) + " + data") + ")\n" +
                       "print(\"WROTE\", out, len(data))\n";
            case Toolchain.NodeFetch:
                return "const url = process.argv[2];\n" +
                       "const out = process.argv[3];\n" +
                       "const fs = require(\"fs\");\n" +
                       "(async () => {\n" +
                       "  const res = await fetch(url, { headers: { \"User-Agent\": \"Weaver-scraper/1.0\" } });\n" +
                       "  if (!res.ok) throw new Error(\"HTTP \" + res.status);\n" +
                       "  let text = await res.text();\n" +
                       (string.IsNullOrEmpty(meta) ? "" : "  text = " + StringLiteralFor(meta) + " + text;\n") +
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

    private string GeneratePaginatedScript(Toolchain toolchain, string meta)
    {
        switch (toolchain)
        {
            case Toolchain.PythonRequests:
            case Toolchain.PythonUrllib:
                return GeneratePaginatedPythonScript(meta);
            case Toolchain.NodeFetch:
                return GeneratePaginatedNodeScript(meta);
            case Toolchain.PowerShell:
                return GeneratePaginatedPowerShellScript(meta);
            default:
                throw new ArgumentOutOfRangeException(nameof(toolchain));
        }
    }

    private string GeneratePaginatedPythonScript(string meta)
    {
        // One body for both python toolchains: uses requests when importable, else stdlib
        // urllib. Follows the API's "next" cursor when present, else advances ?offset= by the
        // page size when the URL has one; stops on a short page, on count exhaustion, or after
        // 50 pages. Non-paginated payloads (no results/next/offset) emit verbatim.
        var lit = StringLiteralFor(meta);
        return "import sys, json, re, time\n" +
               "try:\n" +
               "    import requests as _r\n" +
               "except ImportError:\n" +
               "    _r = None\n" +
               "    import urllib.request as _u\n" +
               "\n" +
               "url = sys.argv[1]\n" +
               "out = sys.argv[2]\n" +
               "limit = 100\n" +
               "_m = re.search(r\"[?&]limit=(\\d+)\", url)\n" +
               "if _m:\n" +
               "    limit = int(_m.group(1))\n" +
               "count = None\n" +
               "all_items = []\n" +
               "page = 0\n" +
               "\n" +
               "def _fetch(u):\n" +
               "    if _r is not None:\n" +
               "        _resp = _r.get(u, timeout=30, headers={\"User-Agent\": \"Weaver-scraper/1.0\"})\n" +
               "        _resp.raise_for_status()\n" +
               "        return _resp.json()\n" +
               "    _req = _u.Request(u, headers={\"User-Agent\": \"Weaver-scraper/1.0\"})\n" +
               "    with _u.urlopen(_req, timeout=30) as _resp:\n" +
               "        return json.loads(_resp.read().decode(\"utf-8\", \"replace\"))\n" +
               "\n" +
               "def _advance(cur, data, n):\n" +
               "    nxt = data.get(\"next\") if isinstance(data, dict) else None\n" +
               "    if nxt:\n" +
               "        return None if nxt == cur else nxt\n" +
               "    if not re.search(r\"[?&]offset=\", cur):\n" +
               "        return None\n" +
               "    off = int(re.search(r\"[?&]offset=(\\d+)\", cur).group(1))\n" +
               "    if count is not None and off + n >= count:\n" +
               "        return None\n" +
               "    if n < limit:\n" +
               "        return None\n" +
               "    base = re.sub(r\"[?&]offset=\\d+\", \"\", cur)\n" +
               "    sep = \"&\" if \"?\" in base else \"?\"\n" +
               "    return base + sep + \"offset=\" + str(off + n)\n" +
               "\n" +
               "cur = url\n" +
               "while cur and page < 50:\n" +
               "    data = _fetch(cur)\n" +
               "    if isinstance(data, dict) and (\"results\" in data or \"next\" in data or re.search(r\"[?&]offset=\", cur)):\n" +
               "        if count is None and isinstance(data.get(\"count\"), int):\n" +
               "            count = data[\"count\"]\n" +
               "        items = data.get(\"results\") if isinstance(data.get(\"results\"), list) else []\n" +
               "        all_items.extend(items)\n" +
               "        cur = _advance(cur, data, len(items))\n" +
               "    else:\n" +
               "        all_items = data\n" +
               "        cur = None\n" +
               "    page += 1\n" +
               "    time.sleep(0.05)\n" +
               "\n" +
               "if isinstance(all_items, list) and count is not None:\n" +
               "    payload = {\"count\": count, \"results\": all_items}\n" +
               "elif isinstance(all_items, list):\n" +
               "    payload = all_items\n" +
               "else:\n" +
               "    payload = all_items\n" +
               "text = json.dumps(payload, indent=2) if not isinstance(payload, str) else payload\n" +
               "with open(out, \"w\", encoding=\"utf-8\", newline=\"\") as f:\n" +
               "    f.write(" + (string.IsNullOrEmpty(meta) ? "text" : lit + " + text") + ")\n" +
               "print(\"WROTE\", out, len(all_items) if isinstance(all_items, list) else 1)\n";
    }

    private string GeneratePaginatedNodeScript(string meta)
    {
        var lit = StringLiteralFor(meta);
        return "const url = process.argv[2];\n" +
               "const out = process.argv[3];\n" +
               "const fs = require(\"fs\");\n" +
               "(async () => {\n" +
               "  const limit = parseInt((url.match(/[?&]limit=(\\d+)/) || [])[1] || \"100\", 10);\n" +
               "  const hasOffset = /[?&]offset=/.test(url);\n" +
               "  let cur = url;\n" +
               "  let page = 0;\n" +
               "  let count = null;\n" +
               "  const all = [];\n" +
               "  const fetchJson = async (u) => {\n" +
               "    const res = await fetch(u, { headers: { \"User-Agent\": \"Weaver-scraper/1.0\" } });\n" +
               "    if (!res.ok) throw new Error(\"HTTP \" + res.status + \" at \" + u);\n" +
               "    return res.json();\n" +
               "  };\n" +
               "  while (cur && page < 50) {\n" +
               "    const data = await fetchJson(cur);\n" +
               "    let items = [];\n" +
               "    let next = null;\n" +
               "    if (data && typeof data === \"object\" && !Array.isArray(data) && (data.results || data.next || hasOffset)) {\n" +
               "      if (typeof data.count === \"number\" && count === null) count = data.count;\n" +
               "      items = data.results || [];\n" +
               "      if (data.next && data.next !== cur) next = data.next;\n" +
               "      else if (hasOffset && items.length >= limit) {\n" +
               "        const m = cur.match(/[?&]offset=(\\d+)/);\n" +
               "        const off = m ? parseInt(m[1], 10) : 0;\n" +
               "        if (count !== null && off + items.length >= count) next = null;\n" +
               "        else {\n" +
               "          const base = cur.replace(/[?&]offset=\\d+/, \"\");\n" +
               "          const sep = base.includes(\"?\") ? \"&\" : \"?\";\n" +
               "          next = base + sep + \"offset=\" + (off + items.length);\n" +
               "        }\n" +
               "      }\n" +
               "      if (items.length) all.push(...items);\n" +
               "    } else if (Array.isArray(data)) {\n" +
               "      all.push(...data);\n" +
               "    } else {\n" +
               "      const text = typeof data === \"string\" ? data : JSON.stringify(data, null, 2);\n" +
               "      fs.writeFileSync(out, " + (string.IsNullOrEmpty(meta) ? "text" : lit + " + text") + ", \"utf8\");\n" +
               "      console.log(\"WROTE\", out, 1);\n" +
               "      return;\n" +
               "    }\n" +
               "    cur = next;\n" +
               "    page++;\n" +
               "  }\n" +
               "  const payload = count !== null ? { count, results: all } : all;\n" +
               "  fs.writeFileSync(out, " + (string.IsNullOrEmpty(meta) ? "JSON.stringify(payload, null, 2)" : lit + " + JSON.stringify(payload, null, 2)") + ", \"utf8\");\n" +
               "  console.log(\"WROTE\", out, all.length);\n" +
               "})().catch((e) => { console.error(e.message); process.exit(1); });\n";
    }

    private string GeneratePaginatedPowerShellScript(string meta)
    {
        // PowerShell double-quoted strings allow real newlines, so the metadata line embeds
        // raw (matching the single-fetch template).
        var metaCmd = string.IsNullOrEmpty(meta) ? "" : $"Set-Content -Path $Out -Value \"{meta}\" -Encoding UTF8 -NoNewline\n";
        return "param([string]$Url = $args[0], [string]$Out = $args[1])\n" +
               "$limit = 100\n" +
               "if ($Url -match '[?&]limit=(\\d+)') { $limit = [int]$Matches[1] }\n" +
               "$hasOffset = $Url -match '[?&]offset='\n" +
               "$cur = $Url\n" +
               "$all = @()\n" +
               "$count = $null\n" +
               "$page = 0\n" +
               "while ($cur -and $page -lt 50) {\n" +
               "  $r = Invoke-WebRequest -Uri $cur -Headers @{ \"User-Agent\" = \"Weaver-scraper/1.0\" } -TimeoutSec 30 -UseBasicParsing\n" +
               "  $data = $r.Content | ConvertFrom-Json\n" +
               "  $paginated = ($null -ne $data.results) -or ($null -ne $data.next) -or $hasOffset\n" +
               "  if ($paginated) {\n" +
               "    if ($null -eq $count -and $null -ne $data.count) { $count = $data.count }\n" +
               "    if ($data.results) { $all += $data.results }\n" +
               "    $next = $null\n" +
               "    if ($data.next -and $data.next -ne $cur) { $next = $data.next }\n" +
               "    elseif ($hasOffset) {\n" +
               "      $m = [regex]::Match($cur, '[?&]offset=(\\d+)')\n" +
               "      $off = if ($m.Success) { [int]$m.Groups[1].Value } else { 0 }\n" +
               "      $n = @($data.results).Count\n" +
               "      if (($null -eq $count -or ($off + $n -lt $count)) -and $n -ge $limit) {\n" +
               "        $base = [regex]::Replace($cur, '[?&]offset=\\d+', '')\n" +
               "        $sep = if ($base -match '\\?') { '&' } else { '?' }\n" +
               "        $next = \"$base$sep\" + \"offset=\" + ($off + $n)\n" +
               "      }\n" +
               "    }\n" +
               "    $cur = $next\n" +
               "  } else {\n" +
               "    $all = $data\n" +
               "    $cur = $null\n" +
               "  }\n" +
               "  $page++\n" +
               "  Start-Sleep -Milliseconds 50\n" +
               "}\n" +
               "if ($all -is [array] -and $null -ne $count) {\n" +
               "  $payload = @{ count = $count; results = @($all) }\n" +
               "} elseif ($all -is [array]) {\n" +
               "  $payload = @($all)\n" +
               "} else {\n" +
               "  $payload = $all\n" +
               "}\n" +
               "$json = if ($payload -is [string]) { $payload } else { $payload | ConvertTo-Json -Depth 10 }\n" +
               metaCmd +
               "Add-Content -Path $Out -Value $json -Encoding UTF8\n" +
               "Write-Output \"WROTE $Out $($all.Count)\"\n";
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
        // A ?limit=/offset= URL (e.g. the full-Pokedex benchmark) gets the paginating template
        // that loops over pages; a plain URL keeps the single-fetch template.
        var script = GenerateScript(toolchain.Value, url, target, metadataLine, ShouldPaginate(url));
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
