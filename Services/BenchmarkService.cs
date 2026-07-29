using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Weaver.Services;

public class BenchmarkService
{
    private readonly string _scoresPath;
    private readonly string _systemInfoPath;

    public BenchmarkService(string weaverDataDir)
    {
        _scoresPath = Path.Combine(weaverDataDir, "benchmark_scores.json");
        _systemInfoPath = Path.Combine(weaverDataDir, "system_info.json");
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

    public static BenchmarkPlanDefinition GetPlanForDifficulty(int level)
    {
        var plans = GetBenchmarkPlans();
        return level < plans.Count ? plans[level] : plans[^1];
    }

    public static List<BenchmarkPlanDefinition> GetBenchmarkPlans()
    {
        return new List<BenchmarkPlanDefinition>
        {
            new() { Level = 0, Name = "Benchmark 0", Description = "Create a folder called 'benchmark_0' at the project root." },
            new() { Level = 1, Name = "Benchmark 1", Description = "Create a folder called 'benchmark_test_1' at the project root. Create a file called 'test.md' inside it and write 'Hello world'. Then append 'The capital of France is Paris'." },
            new() { Level = 2, Name = "Benchmark 2", Description = "Create a folder called 'benchmark_test_2' at the project root. Create a Python script 'hello.py' inside it that prints 'Hello, World!', then modify it to ask for the user's name and greet them. Also create a JavaScript file 'hello.js' that logs 'Hello from JS' to the console." },
            new() { Level = 3, Name = "Benchmark 3", Description = "Create a folder called 'benchmark_test_3'. Build an HTML page 'page.html' with a heading 'Benchmark Page', a lorem ipsum paragraph, and a centered styled div. Create 'style.css' with styles including a red body background. Add a button that changes paragraph text via inline script." },
            new() { Level = 4, Name = "Benchmark 4", Description = "Create a folder called 'benchmark_test_4'.Create 'server.py' that runs an HTTP server on port 9999 serving 'index.html' at / and a /api/hello JSON endpoint. Create index.html with basic content. Start the server and verify it responds." },
            new() { Level = 5, Name = "Benchmark 5", Description = "Create a folder called 'benchmark_test_5. Create 'datastructures.py' with a Stack class (push, pop, peek, is_empty) and a Queue class (enqueue, dequeue, peek, is_empty). Write unit tests using Python's unittest module and run them to verify they pass." },
            new() { Level = 6, Name = "Benchmark 6", Description = "Create a folder called 'benchmark_test_6'. Inside this folder, create a file named 'readme.md', write initial content describing the benchmark purpose, then add instructions for running automated tests in multiple languages including Python, JavaScript, and C#." },
            new() { Level = 7, Name = "Benchmark 7", Description = "Create a folder called 'benchmark_test_7' at the project root. Create a complex HTML page with embedded CSS and JS that includes interactive elements like buttons and forms. Implement responsive design using media queries and ensure cross-browser compatibility testing." }
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
}

public class BenchmarkPlanDefinition
{
    public int Level { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
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
