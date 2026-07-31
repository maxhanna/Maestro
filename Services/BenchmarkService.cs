using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Weaver;

namespace Weaver.Services;

public class BenchmarkService
{
    private readonly string _scoresPath;
    private readonly string _systemInfoPath;
    private readonly string _testResultsPath;

    public BenchmarkService(string weaverDataDir)
    {
        _scoresPath = Path.Combine(weaverDataDir, "benchmark_scores.json");
        _systemInfoPath = Path.Combine(weaverDataDir, "system_info.json");
        // Separate from the legacy benchmark_scores.json (points/percent/status only) —
        // this stores the full TestRunResult (gates, machine detail, edit + progress
        // scores) shared by both hand-authored test cards and the benchmark ladder.
        _testResultsPath = Path.Combine(weaverDataDir, "test_results.json");
    }

    public static SystemInfo DetectSystemInfo()
    {
        var info = new SystemInfo
        {
            Os = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            MachineName = Environment.MachineName,
            ProcessorCount = Environment.ProcessorCount,
            Is64Bit = Environment.Is64BitOperatingSystem,
            UserName = Environment.UserName,
            OsVersion = Environment.OSVersion.ToString()
        };

        PopulateWindowsHardwareInfo(info);

        return info;
    }

    private static void PopulateWindowsHardwareInfo(SystemInfo info)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            var cpus = new List<string>();
            foreach (var o in searcher.Get())
            {
                using var obj = o;
                var name = obj["Name"]?.ToString() ?? "";
                var cores = obj["NumberOfCores"]?.ToString() ?? "";
                var threads = obj["NumberOfLogicalProcessors"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(name))
                    cpus.Add($"{name} ({cores} cores, {threads} threads)");
            }
            info.Cpu = cpus.Count > 0 ? string.Join("; ", cpus) : null;
        }
        catch { /* WMI not available */ }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            foreach (var o in searcher.Get())
            {
                using var obj = o;
                var ram = obj["TotalPhysicalMemory"]?.ToString();
                if (long.TryParse(ram, out var bytes))
                    info.RamBytes = bytes;
                break;
            }
        }
        catch { /* WMI not available */ }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            var gpus = new List<string>();
            foreach (var o in searcher.Get())
            {
                using var obj = o;
                var name = obj["Name"]?.ToString() ?? "";
                var ram = obj["AdapterRAM"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var entry = name;
                    if (long.TryParse(ram, out var ramBytes) && ramBytes > 0)
                        entry += $" ({ramBytes / 1024 / 1024} MB)";
                    gpus.Add(entry);
                }
            }
            info.Gpu = gpus.Count > 0 ? string.Join("; ", gpus) : null;
        }
        catch { /* WMI not available */ }

    }

    public List<BenchmarkScore> LoadScores()
    {
        try
        {
            if (!System.IO.File.Exists(_scoresPath))
                return new List<BenchmarkScore>();
            var raw = System.IO.File.ReadAllText(_scoresPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(raw))
                return new List<BenchmarkScore>();
            return JsonSerializer.Deserialize<List<BenchmarkScore>>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<BenchmarkScore>();
        }
        catch
        {
            return new List<BenchmarkScore>();
        }
    }

    public CustomSystemInfo? LoadCustomSystemInfo()
    {
        try
        {
            if (!System.IO.File.Exists(_systemInfoPath))
                return null;
            var raw = System.IO.File.ReadAllText(_systemInfoPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            return JsonSerializer.Deserialize<CustomSystemInfo>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    public void SaveCustomSystemInfo(CustomSystemInfo info)
    {
        var dir = Path.GetDirectoryName(_systemInfoPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        System.IO.File.WriteAllText(_systemInfoPath, json, Encoding.UTF8);
    }

    public SystemInfo ResolveSystemInfo(CustomSystemInfo? overrides)
    {
        var detected = DetectSystemInfo();
        if (overrides == null)
            return detected;
        if (!string.IsNullOrWhiteSpace(overrides.Os))
            detected.Os = overrides.Os;
        if (!string.IsNullOrWhiteSpace(overrides.Cpu))
            detected.Cpu = overrides.Cpu;
        if (overrides.RamGb.HasValue)
            detected.RamBytes = (long)(overrides.RamGb.Value * 1024 * 1024 * 1024);
        if (!string.IsNullOrWhiteSpace(overrides.Gpu))
            detected.Gpu = overrides.Gpu;
        return detected;
    }

    public void SaveScore(BenchmarkScore score)
    {
        var scores = LoadScores();
        scores.Add(score);
        WriteScores(scores);
    }

    public bool DeleteScore(string id)
    {
        var scores = LoadScores();
        var removed = scores.RemoveAll(s => s.Id == id);
        if (removed == 0)
            return false;
        WriteScores(scores);
        return true;
    }

    private void WriteScores(List<BenchmarkScore> scores)
    {
        var dir = Path.GetDirectoryName(_scoresPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(scores, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        System.IO.File.WriteAllText(_scoresPath, json, Encoding.UTF8);
    }

    public List<TestRunResult> LoadTestResults()
    {
        try
        {
            if (!System.IO.File.Exists(_testResultsPath))
                return new List<TestRunResult>();
            var raw = System.IO.File.ReadAllText(_testResultsPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(raw))
                return new List<TestRunResult>();
            return JsonSerializer.Deserialize<List<TestRunResult>>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<TestRunResult>();
        }
        catch
        {
            return new List<TestRunResult>();
        }
    }

    public void SaveTestResult(TestRunResult result)
    {
        var results = LoadTestResults();
        results.Add(result);
        WriteTestResults(results);
    }

    public bool DeleteTestResult(string id)
    {
        var results = LoadTestResults();
        var removed = results.RemoveAll(r => r.Id == id);
        if (removed == 0)
            return false;
        WriteTestResults(results);
        return true;
    }

    private void WriteTestResults(List<TestRunResult> results)
    {
        var dir = Path.GetDirectoryName(_testResultsPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        System.IO.File.WriteAllText(_testResultsPath, json, Encoding.UTF8);
    }

    /// <summary>
    /// Formatter commands this machine can run out of the box. Paths are absolute
    /// because FormattingGate executes with WorkingDirectory set to the benchmark
    /// sandbox, not the Weaver install — a repo-relative path would resolve against the
    /// wrong directory and the gate would fail every file it was handed.
    ///
    /// prettier is vendored under .formatter/ and version-pinned; it is invoked through
    /// node rather than npm's .bin shim, which on Windows is an extensionless shell
    /// script that Process.Start cannot execute with UseShellExecute=false. ruff is
    /// invoked as a Python module for the same reason — its console script is not
    /// guaranteed to be on PATH.
    /// </summary>
    public static Dictionary<string, string> DefaultFormatterCommands(string contentRoot)
    {
        var prettier = Path.Combine(contentRoot, ".formatter", "node_modules", "prettier", "bin", "prettier.cjs");
        var prettierCmd = $"node \"{prettier}\" --check {{file}}";
        var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["py"] = "python -m ruff format --check {file}",
            ["cs"] = "dotnet format --verify-no-changes --include {file}"
        };
        if (System.IO.File.Exists(prettier))
            foreach (var ext in new[] { "js", "mjs", "cjs", "ts", "json", "css", "scss", "html", "md", "yml", "yaml" })
                commands[ext] = prettierCmd;
        return commands;
    }

    /// <summary>Machine overrides win per-extension; anything unset falls back to the defaults.</summary>
    public static Dictionary<string, string> ResolveFormatterCommands(CustomSystemInfo? overrides, string contentRoot)
    {
        var resolved = DefaultFormatterCommands(contentRoot);
        if (overrides?.FormatterCommands == null) return resolved;
        foreach (var (ext, command) in overrides.FormatterCommands)
            resolved[ext.TrimStart('.')] = command;
        return resolved;
    }

    public static BenchmarkPlanDefinition GetPlanForDifficulty(int level)
    {
        var plans = GetBenchmarkPlans();
        return level < plans.Count ? plans[level] : plans[^1];
    }

    /// <summary>Builds the AllowedPaths + Formatting portion of a level's manifest.
    /// ExpectedSteps is deliberately left null — pinning it requires observing several
    /// real runs of the level (a separate calibration story), and pinning a number that
    /// has not been observed to be reliably achievable would make exactStepCount fail a
    /// clean run for the wrong reason.</summary>
    static BenchmarkManifest LadderManifest(string folder, params string[] extensions) => new()
    {
        AllowedPaths = new List<string> { $"{folder}/**" },
        Formatting = new BenchmarkFormatting { Mode = "formatter", Extensions = extensions.ToList() },
        Runs = 3
    };

    public static List<BenchmarkPlanDefinition> GetBenchmarkPlans()
    {
        return new List<BenchmarkPlanDefinition>
        {
            new() {
                Level = 0, Name = "Benchmark 0",
                Description = "Create a folder called 'benchmark_0' at the project root. Create a file called 'notes.md' inside it and write 'Benchmark 0 complete'.",
                Benchmark = LadderManifest("benchmark_0", "md")
            },
            new() {
                Level = 1, Name = "Benchmark 1",
                Description = "Create a folder called 'benchmark_test_1' at the project root. Create a file called 'test.md' inside it and write 'Hello world'. Then append 'The capital of France is Paris'.",
                Benchmark = LadderManifest("benchmark_test_1", "md")
            },
            new() {
                Level = 2, Name = "Benchmark 2",
                Description = "Create a folder called 'benchmark_test_2' at the project root. Create a Python script 'hello.py' inside it that prints 'Hello, World!', then modify it to ask for the user's name and greet them. Also create a JavaScript file 'hello.js' that logs 'Hello from JS' to the console.",
                Benchmark = LadderManifest("benchmark_test_2", "py", "js")
            },
            new() {
                Level = 3, Name = "Benchmark 3",
                Description = "Create a folder called 'benchmark_test_3'. Build an HTML page 'page.html' with a heading 'Benchmark Page', a lorem ipsum paragraph, and a centered styled div. Create 'style.css' with styles including a red body background. Add a button that changes paragraph text via inline script.",
                Benchmark = LadderManifest("benchmark_test_3", "html", "css")
            },
            new() {
                Level = 4, Name = "Benchmark 4",
                Description = "Create a folder called 'benchmark_test_4'. Create 'server.py' that runs an HTTP server on port 9999 serving 'index.html' at / and a /api/hello JSON endpoint. Create index.html with basic content.",
                Benchmark = LadderManifest("benchmark_test_4", "py", "html")
            },
            new() {
                Level = 5, Name = "Benchmark 5",
                Description = "Create a folder called 'benchmark_test_5'. Create 'datastructures.py' with a Stack class (push, pop, peek, is_empty) and a Queue class (enqueue, dequeue, peek, is_empty).",
                Benchmark = LadderManifest("benchmark_test_5", "py")
            },
            new() {
                Level = 6, Name = "Benchmark 6",
                Description = "Create a folder called 'benchmark_test_6'. Inside this folder, create a file named 'readme.md', write initial content describing the benchmark purpose, then add instructions for running automated tests in multiple languages including Python, JavaScript, and C#.",
                Benchmark = LadderManifest("benchmark_test_6", "md")
            },
            new() {
                Level = 7, Name = "Benchmark 7",
                Description = "Create a folder called 'benchmark_test_7' at the project root. Create a complex HTML page with embedded CSS and JS that includes interactive elements like buttons and forms. Implement responsive design using media queries and ensure cross-browser compatibility testing.",
                Benchmark = LadderManifest("benchmark_test_7", "html")
            }
        };
    }
}

public class CustomSystemInfo
{
    public string? Os { get; set; }
    public string? Cpu { get; set; }
    public double? RamGb { get; set; }
    public string? Gpu { get; set; }
    public string? BenchmarkProjectRoot { get; set; }
    public string? Model { get; set; }

    /// <summary>
    /// File extension (no dot) to check-mode formatter command, used by the
    /// formattingClean gate. "{file}" is substituted with the edited file's path.
    /// Machine-local on purpose: absolute tool paths and pinned versions belong to the
    /// machine, not to a card that other machines will run. Null falls back to
    /// <see cref="BenchmarkService.DefaultFormatterCommands"/>.
    /// </summary>
    public Dictionary<string, string>? FormatterCommands { get; set; }
}

public class BenchmarkPlanDefinition
{
    public int Level { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Gate expectations for this level — allowedPaths and formatting are set
    /// for every level; ExpectedSteps stays null until calibrated (separate story), so
    /// PerfectPass cannot be true for a ladder run yet.</summary>
    public BenchmarkManifest? Benchmark { get; set; }
}

public class BenchmarkScore
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int Level { get; set; }
    public int SuccessfulEdits { get; set; }
    public int FailedEdits { get; set; }
    public int Points { get; set; }
    public double ScorePercent { get; set; }
    public string Status { get; set; } = ""; // "completed", "partial", "failed"
    public SystemInfo? SystemInfo { get; set; }
    public string ModelUsed { get; set; } = "";
    public List<string> FailedSteps { get; set; } = new();
    public string? ErrorReason { get; set; }
    public double DurationMs { get; set; }
}

public class SystemInfo
{
    public string Os { get; set; } = "";
    public string OsArchitecture { get; set; } = "";
    public string ProcessArchitecture { get; set; } = "";
    public string Framework { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int ProcessorCount { get; set; }
    public bool Is64Bit { get; set; }
    public string? Cpu { get; set; }
    public long? RamBytes { get; set; }
    public string? Gpu { get; set; }
    public string UserName { get; set; } = "";
    public string OsVersion { get; set; } = "";
}
