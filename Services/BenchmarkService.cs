using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver.Services;

public class BenchmarkService
{
    private readonly DatabaseService _db;

    public BenchmarkService(DatabaseService db)
    {
        _db = db;
    }

    /// <summary>
    /// Resolves the benchmark project root exactly the way ExecuteStreamCore does: the
    /// user-configured custom root when set (from the benchmark system-info panel), else
    /// the desktop benchmark_sandbox. The kanban project created for benchmark cards must
    /// use this same root so the board view, the cards' filePath, and the directory the
    /// agent actually works in all agree.
    /// </summary>
    public static string ResolveBenchmarkRoot(string? customRoot)
    {
        var root = !string.IsNullOrWhiteSpace(customRoot)
            ? Path.GetFullPath(customRoot)
            : AgentProjectUtilities.GetBenchmarkSandboxPath();
        return NormalizeProjectPath(root);
    }

    /// <summary>Normalized (full, trailing-separator-stripped) form of a project path.</summary>
    public static string NormalizeProjectPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Ensures the "Weaver Benchmarks" project entry exists in the config list and points
    /// at the given benchmark root. Priority: an existing project whose path matches the
    /// root is reused (idempotent); otherwise an existing "Weaver Benchmarks" entry is
    /// re-pointed at the root (updated); otherwise a fresh entry is created. Pure so it
    /// can be unit-tested without touching the config store.
    /// </summary>
    public static (ProjectDto project, bool created, bool updated) ResolveBenchmarkProjectEntry(
        List<ProjectDto> projects, string root)
    {
        var norm = NormalizeProjectPath(root);
        var existing = projects.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.Path) &&
            string.Equals(NormalizeProjectPath(p.Path), norm, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return (existing, false, false);

        var named = projects.FirstOrDefault(p =>
            string.Equals(p.Name, "Weaver Benchmarks", StringComparison.OrdinalIgnoreCase));
        if (named != null)
        {
            named.Path = norm;
            return (named, false, true);
        }

        var created = new ProjectDto
        {
            Name = "Weaver Benchmarks",
            Path = norm,
            Description = "Auto-created for benchmark runs — benchmark cards land here."
        };
        projects.Add(created);
        return (created, true, false);
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
        if (!OperatingSystem.IsWindows()) return;
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
        catch { }
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
        catch { }
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
        catch { }
    }

    public List<BenchmarkScore> LoadScores()
    {
        try
        {
            var json = _db.GetValue("weaver_config", "benchmark_scores_json");
            if (string.IsNullOrWhiteSpace(json))
                return new List<BenchmarkScore>();
            return JsonSerializer.Deserialize<List<BenchmarkScore>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<BenchmarkScore>();
        }
        catch { return new List<BenchmarkScore>(); }
    }

    public CustomSystemInfo? LoadCustomSystemInfo()
    {
        try
        {
            var json = _db.GetSystemInfo();
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<CustomSystemInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch { return null; }
    }

    public void SaveCustomSystemInfo(CustomSystemInfo info)
    {
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
        _db.SetSystemInfo(json);
    }

    public SystemInfo ResolveSystemInfo(CustomSystemInfo? overrides)
    {
        var detected = DetectSystemInfo();
        if (overrides == null) return detected;
        if (!string.IsNullOrWhiteSpace(overrides.Os)) detected.Os = overrides.Os;
        if (!string.IsNullOrWhiteSpace(overrides.Cpu)) detected.Cpu = overrides.Cpu;
        if (overrides.RamGb.HasValue) detected.RamBytes = (long)(overrides.RamGb.Value * 1024 * 1024 * 1024);
        if (!string.IsNullOrWhiteSpace(overrides.Gpu)) detected.Gpu = overrides.Gpu;
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
        if (removed == 0) return false;
        WriteScores(scores);
        return true;
    }

    public int ClearAllScores()
    {
        var count = LoadScores().Count;
        WriteScores(new List<BenchmarkScore>());
        return count;
    }

    private void WriteScores(List<BenchmarkScore> scores)
    {
        var json = JsonSerializer.Serialize(scores, new JsonSerializerOptions { WriteIndented = true });
        _db.SetValue("weaver_config", "benchmark_scores_json", json);
    }

    public static BenchmarkPlanDefinition GetPlanForLevel(int level)
    {
        var plans = GetBenchmarkPlans();
        var match = plans.FirstOrDefault(p => p.Level == level);
        return match ?? plans[^1];
    }

    public static List<BenchmarkPlanDefinition> GetBenchmarkPlans()
    {
        return new List<BenchmarkPlanDefinition>
        {
            new()
            {
                Level = 0, Name = "Benchmark 0", Description = "Create a folder called 'benchmark_0' at the project root.",
                AcceptanceChecks = [Check.Dir("Benchmark directory exists", "benchmark_0")]
            },
            new()
            {
                Level = 1, Name = "Benchmark 1", Description = "Create a folder called 'benchmark_test_1' at the project root. Create a file called 'test.md' inside it and write 'Hello world'. Then append 'The capital of France is Paris'.",
                AcceptanceChecks =
                [
                    Check.Dir("Benchmark directory exists", "benchmark_test_1"),
                    Check.File("Markdown file exists", "benchmark_test_1/test.md"),
                    Check.Contains("Contains greeting", "benchmark_test_1/test.md", "Hello world"),
                    Check.Contains("Contains Paris fact", "benchmark_test_1/test.md", "The capital of France is Paris"),
                    Check.Exact("Exact markdown content", "benchmark_test_1/test.md", "Hello world\nThe capital of France is Paris", 2)
                ]
            },
            new()
            {
                Level = 2, Name = "Benchmark 2", Description = "Create a folder called 'benchmark_test_2' at the project root. Create a Python script 'hello.py' inside it that prints 'Hello, World!', then modify it to ask for the user's name and greet them. Also create a JavaScript file 'hello.js' that logs 'Hello from JS' to the console.",
                AcceptanceChecks =
                [
                    Check.File("Python script exists", "benchmark_test_2/hello.py"),
                    Check.Contains("Python asks for input", "benchmark_test_2/hello.py", "input("),
                    Check.File("JavaScript file exists", "benchmark_test_2/hello.js"),
                    Check.Contains("JavaScript greeting exists", "benchmark_test_2/hello.js", "Hello from JS")
                ]
            },
            new()
            {
                Level = 3, Name = "Benchmark 3", Description = "Create a folder called 'benchmark_test_3'. Create 'page.html' with a basic HTML skeleton, a heading that says 'Benchmark Page', a paragraph of lorem ipsum, and link styles.css. Add a centered styled div and a button that changes the paragraph text when clicked (inline script). Create 'styles.css' with a red body background and styles for the page elements.",
                AcceptanceChecks =
                [
                    Check.File("HTML page exists", "benchmark_test_3/page.html"),
                    Check.Contains("Heading exists", "benchmark_test_3/page.html", "Benchmark Page"),
                    Check.Contains("Stylesheet linked", "benchmark_test_3/page.html", "styles.css"),
                    Check.Contains("Interactive button exists", "benchmark_test_3/page.html", "button"),
                    Check.File("Stylesheet exists", "benchmark_test_3/styles.css"),
                    Check.Contains("Red background exists", "benchmark_test_3/styles.css", "red")
                ]
            },
            new()
            {
                Level = 4, Name = "Benchmark 4", Description = "Create a folder called 'benchmark_test_4'. Create 'server.py' that runs a simple HTTP server on port 9999 serving 'index.html' at / with basic content, plus a /api/hello endpoint that returns JSON {\"message\": \"Hello\"}.",
                AcceptanceChecks =
                [
                    Check.File("Server script exists", "benchmark_test_4/server.py"),
                    Check.Contains("Server uses port 9999", "benchmark_test_4/server.py", "9999"),
                    Check.Contains("Hello endpoint exists", "benchmark_test_4/server.py", "/api/hello"),
                    Check.File("Index page exists", "benchmark_test_4/index.html")
                ]
            },
            new()
            {
                Level = 5, Name = "Benchmark 5", Description = "Create a folder called 'benchmark_test_5'. Create 'datastructures.py' with a Stack class (push, pop, peek, is_empty) and a Queue class (enqueue, dequeue, peek, is_empty).",
                AcceptanceChecks =
                [
                    Check.File("Data structures module exists", "benchmark_test_5/datastructures.py"),
                    Check.Contains("Stack implemented", "benchmark_test_5/datastructures.py", "class Stack"),
                    Check.Contains("Queue implemented", "benchmark_test_5/datastructures.py", "class Queue")
                ]
            },
            new()
            {
                Level = 6, Name = "Benchmark 6", Description = "Create a folder called 'benchmark_test_6' at the project root. Build a multi-language test suite for a simple Calculator that adds, subtracts, multiplies, and divides two numbers. Create exactly 5 files:\n\n1. readme.md — Documentation with these exact sections:\n   - An h1 heading with exact text \"Benchmark 6 — Cross-Language Test Suite\"\n   - A paragraph describing the purpose: testing a Calculator across Python, JavaScript, and C#\n   - An h2 \"Test Files\" with a table listing all 4 files (calculator.py, test_calculator.js, TestCalculator.cs, readme.md) and a one-line description of each\n   - An h2 \"Test Matrix\" with a table showing at least 4 operations (add, subtract, multiply, divide) and which tests cover them\n   - An h2 \"Running the Tests\" with the exact commands: \"python -m unittest discover\" for Python, \"node --test\" for JavaScript, and \"dotnet test\" for C#\n\n2. calculator.py — A Python module with a Calculator class with 4 methods: add(a, b), subtract(a, b), multiply(a, b), divide(a, b). divide must raise ZeroDivisionError when b is 0.\n\n3. test_calculator.py — Python unittest with a TestCalculator class inheriting unittest.TestCase with at least 4 test methods (test_add, test_subtract, test_multiply, test_divide, test_divide_by_zero). Use assertTrue/assertEqual/assertRaises.\n\n4. test_calculator.js — JavaScript using node:test (import { test } from 'node:test'; import assert from 'node:assert/strict';). Export a Calculator object with add, subtract, multiply, divide methods. Write at least 4 test() calls testing add, subtract, multiply, and divide (including divide-by-zero throwing).\n\n5. TestCalculator.cs — C# xUnit with a Calculator class (4 methods) and a TestCalculator class with at least 4 [Fact] methods (Add_ReturnsSum, Subtract_ReturnsDifference, Multiply_ReturnsProduct, Divide_ReturnsQuotient, Divide_ByZero_ThrowsDivideByZeroException). Divide must throw DivideByZeroException.",
                AcceptanceChecks =
                [
                    Check.File("readme exists", "benchmark_test_6/readme.md"),
                    Check.Contains("readme h1", "benchmark_test_6/readme.md", "Benchmark 6"),
                    Check.Contains("readme test files table", "benchmark_test_6/readme.md", "Test Files"),
                    Check.Contains("readme test matrix", "benchmark_test_6/readme.md", "Test Matrix"),
                    Check.Contains("readme running tests", "benchmark_test_6/readme.md", "Running the Tests"),
                    Check.Contains("readme python command", "benchmark_test_6/readme.md", "python -m unittest discover"),
                    Check.Contains("readme js command", "benchmark_test_6/readme.md", "node --test"),
                    Check.Contains("readme csharp command", "benchmark_test_6/readme.md", "dotnet test"),
                    Check.File("Python calculator module", "benchmark_test_6/calculator.py"),
                    Check.Contains("Python add", "benchmark_test_6/calculator.py", "def add"),
                    Check.Contains("Python subtract", "benchmark_test_6/calculator.py", "def subtract"),
                    Check.Contains("Python multiply", "benchmark_test_6/calculator.py", "def multiply"),
                    Check.Contains("Python divide", "benchmark_test_6/calculator.py", "def divide"),
                    Check.File("Python tests", "benchmark_test_6/test_calculator.py"),
                    Check.Contains("Python TestCase", "benchmark_test_6/test_calculator.py", "TestCase"),
                    Check.Contains("Python test_add", "benchmark_test_6/test_calculator.py", "test_add"),
                    Check.Contains("Python test_divide_by_zero", "benchmark_test_6/test_calculator.py", "test_divide_by_zero"),
                    Check.File("JavaScript tests", "benchmark_test_6/test_calculator.js"),
                    Check.Contains("JS node:test import", "benchmark_test_6/test_calculator.js", "node:test"),
                    Check.Contains("JS assert import", "benchmark_test_6/test_calculator.js", "node:assert/strict"),
                    Check.Contains("JS test calls", "benchmark_test_6/test_calculator.js", "test("),
                    Check.Contains("JS add test", "benchmark_test_6/test_calculator.js", "add"),
                    Check.File("C# tests", "benchmark_test_6/TestCalculator.cs"),
                    Check.Contains("C# xUnit Fact", "benchmark_test_6/TestCalculator.cs", "[Fact]"),
                    Check.Contains("C# Calculator class", "benchmark_test_6/TestCalculator.cs", "class Calculator"),
                    Check.Contains("C# add method", "benchmark_test_6/TestCalculator.cs", "Add"),
                    Check.Contains("C# divide throws", "benchmark_test_6/TestCalculator.cs", "DivideByZeroException")
                ]
            },
            new()
            {
                Level = 7, Name = "Benchmark 7", Description = "Create a folder called 'benchmark_test_7' at the project root. Inside it create a single file 'index.html' that contains a complete, self-contained responsive web page with all CSS and JavaScript embedded in the same file (no external .css or .js files). The page must satisfy every requirement below exactly:\n\n1. DOCUMENT STRUCTURE: Valid HTML5 with <!DOCTYPE html>, <html lang=\"en\">, <head> with <meta charset=\"UTF-8\">, a <meta name=\"viewport\"> tag, a <title> containing the text \"Benchmark 7\", and a <body>.\n\n2. HEADER / NAV: A <header> at the top containing an <h1> with the exact text \"Benchmark 7\" and a <nav> with at least 3 anchor links (\"Home\", \"Features\", \"Contact\").\n\n3. HERO SECTION: A section with a heading <h2> containing the text \"Welcome\" and a <button> with the exact text \"Get Started\" that, when clicked, toggles the visibility of the features section below it (hidden by default via display:none, shown on click).\n\n4. FEATURES GRID: A section with exactly 3 feature cards. Each card is a <div> containing an <h3> and a <p>. The 3 headings must be exactly \"Speed\", \"Reliability\", and \"Scale\". The cards must be laid out in a CSS Grid or Flexbox row.\n\n5. CONTACT FORM: A <form> containing: a text <input> with placeholder \"Your name\", an <email> <input> with placeholder \"Your email\", a <textarea> with placeholder \"Your message\", and a <button type=\"submit\"> with text \"Send\". On submit the form must prevent the default submission (show an alert or update a confirmation <div> with id=\"form-status\" instead).\n\n6. COUNTER: A <button> with exact text \"Click me\" and a <span> with id=\"counter\" initialized to \"0\". Each click on the button increments the counter by 1 and updates the span text.\n\n7. FOOTER: A <footer> containing the exact text \"2024 Benchmark 7\".\n\n8. EMBEDDED CSS: All styles in a single <style> block in <head>. Must include: a CSS custom property (--accent) defined on :root, the grid/flex layout for feature cards, a dark background on the header and footer, and a media query @media (max-width: 600px) that stacks the feature cards in a single column (grid-template-columns: 1fr or flex-direction: column).\n\n9. EMBEDDED JAVASCRIPT: All logic in a single <script> block. Must wire up: the Get Started toggle, the form submit handler, and the counter button. Use addEventListener, not inline onclick attributes.",
                AcceptanceChecks =
                [
                    Check.File("HTML page exists", "benchmark_test_7/index.html"),
                    Check.Contains("DOCTYPE html", "benchmark_test_7/index.html", "<!DOCTYPE html>"),
                    Check.Contains("Viewport meta tag", "benchmark_test_7/index.html", "viewport"),
                    Check.Contains("Title is Benchmark 7", "benchmark_test_7/index.html", "<title>Benchmark 7</title>"),
                    Check.Contains("Header with h1", "benchmark_test_7/index.html", "<header>"),
                    Check.Contains("H1 says Benchmark 7", "benchmark_test_7/index.html", "Benchmark 7"),
                    Check.Contains("Nav with 3 links", "benchmark_test_7/index.html", "<nav>"),
                    Check.Contains("Hero welcome heading", "benchmark_test_7/index.html", "Welcome"),
                    Check.Contains("Get Started button", "benchmark_test_7/index.html", "Get Started"),
                    Check.Contains("Features section", "benchmark_test_7/index.html", "Features"),
                    Check.Occurs("Exactly 3 feature cards", "benchmark_test_7/index.html", "feature", 3, 3),
                    Check.Contains("Speed feature", "benchmark_test_7/index.html", "Speed"),
                    Check.Contains("Reliability feature", "benchmark_test_7/index.html", "Reliability"),
                    Check.Contains("Scale feature", "benchmark_test_7/index.html", "Scale"),
                    Check.Contains("Contact form", "benchmark_test_7/index.html", "<form>"),
                    Check.Contains("Name input", "benchmark_test_7/index.html", "Your name"),
                    Check.Contains("Email input", "benchmark_test_7/index.html", "Your email"),
                    Check.Contains("Textarea", "benchmark_test_7/index.html", "<textarea>"),
                    Check.Contains("Send button", "benchmark_test_7/index.html", "Send"),
                    Check.Contains("Form status element", "benchmark_test_7/index.html", "form-status"),
                    Check.Contains("Counter button", "benchmark_test_7/index.html", "Click me"),
                    Check.Contains("Counter span", "benchmark_test_7/index.html", "counter"),
                    Check.Contains("Footer text", "benchmark_test_7/index.html", "2024 Benchmark 7"),
                    Check.Contains("Embedded style block", "benchmark_test_7/index.html", "<style>"),
                    Check.Contains("CSS custom property", "benchmark_test_7/index.html", "--accent"),
                    Check.Contains("Media query", "benchmark_test_7/index.html", "@media (max-width: 600px)"),
                    Check.Contains("Responsive single column", "benchmark_test_7/index.html", "1fr"),
                    Check.Contains("Embedded script block", "benchmark_test_7/index.html", "<script>"),
                    Check.Contains("addEventListener used", "benchmark_test_7/index.html", "addEventListener")
                ]
            },
            new()
            {
                Level = 8, Name = "Edit Strategy 1: Targeted replacement", Description = "Create a folder called 'edit_strategy_8' with a file 'AppSettings.cs' containing a C# class AppSettings with properties: Environment (string, default \"Development\"), RetryCount (int, default 3), EnableTelemetry (bool, default true), with a comment \"// PRESERVE: environment fallback\" above Environment. Then change RetryCount from 3 to 5 without changing any other setting or comment.",
                AcceptanceChecks =
                [
                    Check.File("Settings file exists", "edit_strategy_8/AppSettings.cs"),
                    Check.Contains("Retry count updated", "edit_strategy_8/AppSettings.cs", "RetryCount", 3),
                    Check.NotContains("Old retry value removed", "edit_strategy_8/AppSettings.cs", "= 3;", 1),
                    Check.Contains("Fallback comment preserved", "edit_strategy_8/AppSettings.cs", "PRESERVE", 2, "preservation"),
                    Check.Contains("Telemetry preserved", "edit_strategy_8/AppSettings.cs", "EnableTelemetry", 2, "preservation")
                ]
            },
            new()
            {
                Level = 9, Name = "Edit Strategy 2: Method insertion", Description = "Create a folder called 'edit_strategy_9' with 'PriceService.cs' containing a C# class PriceService with a method ApplyTax(decimal price) returning price * 1.2m, and ApplyDiscount(decimal price, decimal discount) returning price - discount. Then add a public decimal ClampToZero(decimal price) method after ApplyTax that returns 0 when price is negative and otherwise returns price. Preserve both existing methods.",
                AcceptanceChecks =
                [
                    Check.File("Service file exists", "edit_strategy_9/PriceService.cs"),
                    Check.Contains("New method signature exists", "edit_strategy_9/PriceService.cs", "ClampToZero", 3),
                    Check.Contains("Negative values are clamped", "edit_strategy_9/PriceService.cs", "price < 0", 2),
                    Check.Occurs("Method inserted once", "edit_strategy_9/PriceService.cs", "ClampToZero", 1, 2),
                    Check.Contains("Tax method preserved", "edit_strategy_9/PriceService.cs", "price * 1.2m", 2, "preservation")
                ]
            },
            new()
            {
                Level = 10, Name = "Edit Strategy 3: Property update without duplication", Description = "Create a folder called 'edit_strategy_10' with 'CacheOptions.cs' containing a C# class CacheOptions with properties MaxEntries (int, default 100) and Expiration (TimeSpan, default TimeSpan.FromMinutes(10)). Then update MaxEntries to 250. Modify the existing property; do not add another MaxEntries property.",
                AcceptanceChecks =
                [
                    Check.File("Options file exists", "edit_strategy_10/CacheOptions.cs"),
                    Check.Contains("Property value updated", "edit_strategy_10/CacheOptions.cs", "250", 3),
                    Check.Occurs("Property is not duplicated", "edit_strategy_10/CacheOptions.cs", "MaxEntries", 1, 3, "preservation"),
                    Check.Contains("Expiration preserved", "edit_strategy_10/CacheOptions.cs", "FromMinutes(10)", 2, "preservation")
                ]
            },
            new()
            {
                Level = 11, Name = "Edit Strategy 4: Section-aware edit", Description = "Create a folder called 'edit_strategy_11' with 'settings.html' containing two sections: section id=\"general\" with h2 'Settings' and a button 'Save', and section id=\"users\" with h2 'Settings' and a button 'Save'. Change only the Save button inside the users section to say 'Save Users'. Leave the general section unchanged.",
                AcceptanceChecks =
                [
                    Check.File("Settings file exists", "edit_strategy_11/settings.html"),
                    Check.Contains("Users button updated", "edit_strategy_11/settings.html", "Save Users", 3),
                    Check.Occurs("Only one users label exists", "edit_strategy_11/settings.html", "Save Users", 1, 2),
                    Check.Occurs("General Save remains once", "edit_strategy_11/settings.html", "<button>Save</button>", 1, 3, "preservation"),
                    Check.Contains("General section preserved", "edit_strategy_11/settings.html", "id=\"general\"", 2, "preservation")
                ]
            },
            new()
            {
                Level = 12, Name = "Edit Strategy 5: Signature propagation", Description = "Create a folder called 'edit_strategy_12' with 'Greeter.cs' (C# class Greeter with string Greet(string name) => $\"Hello {name}\") and 'Program.cs' (creates Greeter, calls Greet(\"Ada\"), writes \"Done\" with comment \"// PRESERVE: completion marker\"). Change Greet to accept a second string parameter punctuation and append it after the name. Update the call in Program.cs to pass \"!\". Preserve the completion marker and Done output.",
                AcceptanceChecks =
                [
                    Check.File("Greeter exists", "edit_strategy_12/Greeter.cs"),
                    Check.Contains("Signature accepts punctuation", "edit_strategy_12/Greeter.cs", "string punctuation", 3),
                    Check.Contains("Implementation uses punctuation", "edit_strategy_12/Greeter.cs", "{punctuation}", 2),
                    Check.File("Program exists", "edit_strategy_12/Program.cs"),
                    Check.Contains("Caller passes punctuation", "edit_strategy_12/Program.cs", "Greet", 3),
                    Check.Contains("Completion marker preserved", "edit_strategy_12/Program.cs", "PRESERVE", 2, "preservation"),
                    Check.Contains("Done output preserved", "edit_strategy_12/Program.cs", "Done", 2, "preservation")
                ]
            },
            new()
            {
                Level = 13, Name = "Cross-language CSS", Description = "Create a folder called 'edit_strategy_13' with 'site.css' containing a body rule with color: #222, background: white, and font-family: sans-serif. Then change only the body background from white to #f5f5f5.",
                AcceptanceChecks =
                [
                    Check.File("Stylesheet exists", "edit_strategy_13/site.css"),
                    Check.Contains("Background updated", "edit_strategy_13/site.css", "#f5f5f5", 3),
                    Check.Contains("Color preserved", "edit_strategy_13/site.css", "color: #222", 2, "preservation"),
                    Check.Contains("Font preserved", "edit_strategy_13/site.css", "font-family: sans-serif", 2, "preservation")
                ]
            },
            new()
            {
                Level = 14, Name = "Cross-language TypeScript", Description = "Create a folder called 'edit_strategy_14' with 'user-service.ts' containing TypeScript: export class UserService { getName(id: number): string { return `user-${id}`; } }. Then add an isValidId(id: number): boolean method that returns true only for positive IDs.",
                AcceptanceChecks =
                [
                    Check.File("Service file exists", "edit_strategy_14/user-service.ts"),
                    Check.Contains("Method added", "edit_strategy_14/user-service.ts", "isValidId", 3),
                    Check.Contains("Positive check added", "edit_strategy_14/user-service.ts", "id > 0", 2),
                    Check.Contains("Existing method preserved", "edit_strategy_14/user-service.ts", "user-${id}", 2, "preservation")
                ]
            },
            new()
            {
                Level = 15, Name = "Cross-language Python", Description = "Create a folder called 'edit_strategy_15' with 'formatter.py' containing: def format_name(first, last): full = f\"{first} {last}\"; return full followed by def unchanged(): return \"keep\". Then update format_name to strip whitespace from first and last before formatting. Preserve unchanged().",
                AcceptanceChecks =
                [
                    Check.File("Formatter exists", "edit_strategy_15/formatter.py"),
                    Check.Contains("First stripped", "edit_strategy_15/formatter.py", ".strip()", 2),
                    Check.Contains("Unchanged function preserved", "edit_strategy_15/formatter.py", "return \"keep\"", 2, "preservation")
                ]
            },
            new()
            {
                Level = 16, Name = "Data Fetch 1: Pokemon CSV", Description = "Create a folder called 'benchmark_test_16' at the project root. Inside it, create a file called 'pokemon_data.csv'. Fetch real Pokemon data (id numbers, stats, and types) from a live source — the public PokeAPI (https://pokeapi.co) is recommended — and write the data into pokemon_data.csv. The CSV must contain real Pokemon data: each row should include the Pokemon's id number, its base stats (for example hp, attack, defense), and its type(s). Fetch as many Pokemon as possible — the goal is to cover the full Pokedex (~1025 species), not just the first few. Do not invent or fabricate the data.\n\nFRESHNESS REQUIREMENT: Begin the file with a metadata line in this exact format — FETCHED_AT: YYYY-MM-DD — where YYYY-MM-DD is the current date at the moment you actually perform the fetch. Never use a hardcoded, guessed, or copied date, and never reuse a date from cached or previously fetched data. This timestamp proves the data was freshly fetched during this run.",
                AcceptanceChecks =
                [
                    Check.Dir("Benchmark directory exists", "benchmark_test_16"),
                    Check.File("Pokemon CSV exists", "benchmark_test_16/pokemon_data.csv"),
                    Check.FreshTimestamp("Fetch timestamp is fresh and run-time", "benchmark_test_16/pokemon_data.csv"),
                    Check.Contains("CSV has id column", "benchmark_test_16/pokemon_data.csv", "id"),
                    Check.Contains("CSV has type column", "benchmark_test_16/pokemon_data.csv", "type"),
                    Check.Contains("CSV has stats column (hp)", "benchmark_test_16/pokemon_data.csv", "hp"),
                    Check.Contains("CSV has stats column (attack)", "benchmark_test_16/pokemon_data.csv", "attack"),
                    Check.Contains("First dex pokemon present", "benchmark_test_16/pokemon_data.csv", "bulbasaur"),
                    Check.Contains("Iconic pokemon present", "benchmark_test_16/pokemon_data.csv", "pikachu"),
                    Check.Contains("Mid dex pokemon present", "benchmark_test_16/pokemon_data.csv", "mewtwo"),
                    Check.Contains("Late dex pokemon present", "benchmark_test_16/pokemon_data.csv", "lucario")
                ]
            },
            new()
            {
                Level = 17, Name = "Web Search 1: Sequential Facts", Description = "Create a folder called 'benchmark_test_17' at the project root. Inside it, create a text file called 'internet_facts.txt'. Then perform SEVERAL separate web searches, one after another (do them sequentially — each search is independent and must be run as its own fetch, not combined into a single query), to find the answers to these unrelated questions:\n\n1. When is the next Bitcoin halving expected to occur?\n2. What is the deepest point in the ocean called?\n3. In what year did the first crewed Moon landing happen?\n\nUse real web search results for every question — do not guess or fabricate any answer. After collecting all three answers, write each one on its own line into benchmark_test_17/internet_facts.txt (three separate lines, one per question).\n\nFRESHNESS REQUIREMENT: Begin the file with a metadata line in this exact format — FETCHED_AT: YYYY-MM-DD — where YYYY-MM-DD is the current date at the moment you actually perform the searches. Never use a hardcoded, guessed, or copied date, and never reuse a date from cached or previously fetched data. This timestamp proves the answers were freshly searched during this run.",
                AcceptanceChecks =
                [
                    Check.Dir("Benchmark directory exists", "benchmark_test_17"),
                    Check.File("Facts file exists", "benchmark_test_17/internet_facts.txt"),
                    Check.FreshTimestamp("Fetch timestamp is fresh and run-time", "benchmark_test_17/internet_facts.txt"),
                    Check.ContainsIc("Bitcoin halving answer present", "benchmark_test_17/internet_facts.txt", "halving"),
                    Check.ContainsIc("Bitcoin mentioned", "benchmark_test_17/internet_facts.txt", "bitcoin"),
                    Check.ContainsIc("Deepest point answer present", "benchmark_test_17/internet_facts.txt", "mariana trench"),
                    Check.Contains("Moon landing year present", "benchmark_test_17/internet_facts.txt", "1969")
                ]
            },
            new()
            {
                Level = 18, Name = "Web Search 2: Cross-Check Consistency", Description = "Create a folder called 'benchmark_test_18' at the project root. Inside it, create a text file called 'consistency_facts.txt'. Then perform SEVERAL separate web searches, one after another (do them sequentially — each search is independent and must be run as its own fetch, not combined into a single query), to answer these related questions:\n\n1. In what year was the first iPhone released?\n2. What is iOS? (the operating system Apple makes for its phones)\n3. Which operating system did the first iPhone run at launch?\n4. Is the operating system that shipped on the first iPhone the same thing that is today called iOS?\n\nUse real web search results for every question — do not guess or fabricate any answer. After collecting the answers, cross-check them against each other: the facts must be consistent (for example, if the first iPhone shipped in a specific year, the OS it ran at launch must line up with that same year). Then write the reconciled findings into benchmark_test_18/consistency_facts.txt: one line per question with its answer, plus a final line that states the consistency verdict (e.g. \"VERDICT: consistent — the first iPhone (2007) ran iPhone OS 1.0, which was later renamed iOS\").\n\nFRESHNESS REQUIREMENT: Begin the file with a metadata line in this exact format — FETCHED_AT: YYYY-MM-DD — where YYYY-MM-DD is the current date at the moment you actually perform the searches. Never use a hardcoded, guessed, or copied date, and never reuse a date from cached or previously fetched data. This timestamp proves the answers were freshly searched during this run.",
                AcceptanceChecks =
                [
                    Check.Dir("Benchmark directory exists", "benchmark_test_18"),
                    Check.File("Facts file exists", "benchmark_test_18/consistency_facts.txt"),
                    Check.FreshTimestamp("Fetch timestamp is fresh and run-time", "benchmark_test_18/consistency_facts.txt"),
                    Check.ContainsIc("First iPhone year present", "benchmark_test_18/consistency_facts.txt", "2007"),
                    Check.ContainsIc("iPhone mentioned", "benchmark_test_18/consistency_facts.txt", "iphone"),
                    Check.ContainsIc("iOS mentioned", "benchmark_test_18/consistency_facts.txt", "ios"),
                    Check.ContainsIc("Launch OS identified", "benchmark_test_18/consistency_facts.txt", "iphone os"),
                    Check.ContainsIc("Consistency verdict present", "benchmark_test_18/consistency_facts.txt", "consistent"),
                    Check.ContainsIc("Verdict explains the link", "benchmark_test_18/consistency_facts.txt", "renamed")
                ]
            },
            new()
            {
                Level = 19, Name = "Data Fetch 2: REST Weather API", Description = "Create a folder called 'benchmark_test_19' at the project root. Inside it, create a file called 'weather.json'. Call a public REST API to fetch CURRENT weather for at least 3 real, well-known cities (for example Paris, Tokyo, and Sydney), parse the JSON responses, and write the results into weather.json as valid JSON (one object per city). The public Open-Meteo API (https://api.open-meteo.com) is recommended — it needs no API key: call https://api.open-meteo.com/v1/forecast?latitude=<lat>&longitude=<lon>&current_weather=true for each city. Each city object in weather.json must contain: the real city name, its current temperature, windspeed, weathercode, and the time of the measurement. Use REAL city names and REAL API data — do not invent or fabricate any value, and do not copy cached data from another project or previous run.\n\nFRESHNESS REQUIREMENT: Include a top-level field named 'fetched_at' in weather.json with the current date in YYYY-MM-DD format at the moment you actually perform the fetch (for example \"fetched_at\": \"2026-08-07\"). Never use a hardcoded, guessed, or copied date, and never reuse a date from cached or previously fetched data. This timestamp proves the data was freshly fetched during this run.",
                AcceptanceChecks =
                [
                    Check.Dir("Benchmark directory exists", "benchmark_test_19"),
                    Check.File("Weather JSON exists", "benchmark_test_19/weather.json"),
                    Check.FreshTimestamp("Fetch timestamp is fresh and run-time", "benchmark_test_19/weather.json"),
                    Check.ContainsIc("Paris present", "benchmark_test_19/weather.json", "paris"),
                    Check.ContainsIc("Tokyo present", "benchmark_test_19/weather.json", "tokyo"),
                    Check.ContainsIc("Sydney present", "benchmark_test_19/weather.json", "sydney"),
                    Check.ContainsIc("Temperature field present", "benchmark_test_19/weather.json", "temperature"),
                    Check.ContainsIc("Windspeed field present", "benchmark_test_19/weather.json", "windspeed"),
                    Check.ContainsIc("Weathercode field present", "benchmark_test_19/weather.json", "weathercode")
                ]
            }
        };
    }

    public async Task<BenchmarkScore> EvaluateAsync(
        int level, string projectRoot, int successfulEdits, int failedEdits,
        int stepCount, double durationMs, string modelUsed,
        List<BenchmarkEditRecord>? edits = null, string? errorReason = null,
        CancellationToken ct = default)
    {
        var plan = GetBenchmarkPlans().FirstOrDefault(p => p.Level == level)
            ?? throw new ArgumentOutOfRangeException(nameof(level), $"Unknown benchmark level {level}.");

        var results = new List<BenchmarkCheckResult>();
        foreach (var check in plan.AcceptanceChecks)
            results.Add(await EvaluateCheckAsync(check, projectRoot, ct));

        var totalWeight = results.Sum(r => r.Weight);
        var earnedWeight = results.Where(r => r.Passed).Sum(r => r.Weight);
        var correctness = totalWeight == 0 ? 0d : Math.Round(earnedWeight / totalWeight * 100, 1);

        var totalEdits = successfulEdits + failedEdits;
        var editSuccessRate = totalEdits == 0 ? 0d : Math.Round((double)successfulEdits / totalEdits * 100, 1);
        var stepEfficiency = ComputeStepEfficiency(stepCount, level);

        var scorePercent = Math.Round(correctness * 0.5 + editSuccessRate * 0.3 + stepEfficiency * 0.2, 1);
        if (correctness < 50) scorePercent = Math.Min(scorePercent, correctness + 10);

        var allPassed = results.Count > 0 && results.All(r => r.Passed);
        var anyPassed = results.Any(r => r.Passed);
        var status = allPassed ? "completed" : anyPassed ? "partial" : "failed";

        var score = new BenchmarkScore
        {
            Level = level,
            SuccessfulEdits = successfulEdits,
            FailedEdits = failedEdits,
            StepsUsed = stepCount,
            Points = successfulEdits + (totalEdits > 0 && failedEdits == 0 ? successfulEdits : 0),
            ScorePercent = scorePercent,
            CorrectnessPercent = correctness,
            StepEfficiencyPercent = stepEfficiency,
            EditSuccessPercent = editSuccessRate,
            Status = status,
            ModelUsed = modelUsed ?? "",
            DurationMs = durationMs,
            SystemInfo = ResolveSystemInfo(LoadCustomSystemInfo()),
            Edits = edits ?? new List<BenchmarkEditRecord>(),
            FailedSteps = results.Where(r => !r.Passed).Select(r => r.Name).ToList(),
            ErrorReason = errorReason
        };

        SaveScore(score);
        return score;
    }

    private static double ComputeStepEfficiency(int stepCount, int level)
    {
        var target = level <= 2 ? 2 : level <= 4 ? 4 : 6;
        if (stepCount <= 0) return 0;
        if (stepCount <= target) return 100;
        var ratio = (double)stepCount / target;
        return Math.Max(0, Math.Round(100 - (ratio - 1) * 50, 1));
    }

    private async Task<BenchmarkCheckResult> EvaluateCheckAsync(
        BenchmarkAcceptanceCheck check, string root, CancellationToken ct)
    {
        var result = new BenchmarkCheckResult { Name = check.Name, Type = check.Type, Weight = check.Weight, Category = check.Category };
        try
        {
            var path = Path.GetFullPath(Path.Combine(root, check.Path ?? ""));
            switch (check.Type)
            {
                case BenchmarkCheckType.DirectoryExists:
                    result.Passed = Directory.Exists(path);
                    result.Message = result.Passed ? "Directory exists." : $"Missing directory: {check.Path}";
                    break;
                case BenchmarkCheckType.FileExists:
                    result.Passed = File.Exists(path);
                    result.Message = result.Passed ? "File exists." : $"Missing file: {check.Path}";
                    break;
                case BenchmarkCheckType.FileContains:
                case BenchmarkCheckType.FileNotContains:
                case BenchmarkCheckType.FileOccurrenceCount:
                case BenchmarkCheckType.FileEquals:
                    if (!File.Exists(path)) { result.Message = $"Missing file: {check.Path}"; break; }
                    var content = await File.ReadAllTextAsync(path, ct);
                    if (check.Type == BenchmarkCheckType.FileOccurrenceCount)
                    {
                        var comparison = check.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        var value = check.Value ?? "";
                        var count = 0;
                        for (var index = 0; value.Length > 0 && (index = content.IndexOf(value, index, comparison)) >= 0; index += value.Length) count++;
                        result.Passed = count == check.ExpectedCount;
                        result.Message = result.Passed ? $"Found exactly {count} occurrence(s)." : $"Expected {check.ExpectedCount} occurrence(s) in {check.Path}, found {count}.";
                        break;
                    }
                    if (check.Type == BenchmarkCheckType.FileEquals)
                    {
                        static string Normalize(string value) => value.Replace("\r\n", "\n").TrimEnd('\n');
                        result.Passed = string.Equals(Normalize(content), Normalize(check.Value ?? ""), check.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                        result.Message = result.Passed ? "Exact content assertion passed." : $"Exact content assertion failed for {check.Path}.";
                        break;
                    }
                    var contains = content.Contains(check.Value ?? "", check.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    result.Passed = check.Type == BenchmarkCheckType.FileContains ? contains : !contains;
                    result.Message = result.Passed ? "Content assertion passed." : $"Content assertion failed for {check.Path}.";
                    break;
                case BenchmarkCheckType.FileFreshTimestamp:
                    if (!File.Exists(path)) { result.Message = $"Missing file: {check.Path}"; break; }
                    var freshText = await File.ReadAllTextAsync(path, ct);
                    var embedded = ExtractRunDate(freshText);
                    if (embedded == null)
                    {
                        result.Message = $"No run-time date found in {check.Path} — expected a \"FETCHED_AT: YYYY-MM-DD\" line captured at run time.";
                        break;
                    }
                    var writeDate = File.GetLastWriteTime(path).Date;
                    var todayDate = DateTime.Today;
                    var matchesWrite = Math.Abs((embedded.Value - writeDate).TotalDays) <= 1;
                    var isFresh = embedded.Value <= todayDate && (todayDate - embedded.Value).Days <= Math.Max(1, check.MaxDaysOld);
                    result.Passed = matchesWrite && isFresh;
                    result.Message = result.Passed
                        ? $"Run-time date {embedded.Value:yyyy-MM-dd} matches the file write date and is within {check.MaxDaysOld} day(s) of today."
                        : $"Stale or mismatched run-time date: file says {embedded.Value:yyyy-MM-dd}, file was written {writeDate:yyyy-MM-dd}, today is {todayDate:yyyy-MM-dd}.";
                    break;
                default:
                    result.Message = $"Unsupported check type: {check.Type}.";
                    break;
            }
        }
        catch (Exception ex) { result.Message = ex.Message; }
        return result;
    }

    public static BenchmarkRegressionComparison Compare(BenchmarkScore current, BenchmarkScore baseline)
    {
        return new BenchmarkRegressionComparison
        {
            BaselineScoreId = baseline.Id,
            ScoreDelta = Math.Round(current.ScorePercent - baseline.ScorePercent, 1),
            DurationDeltaMs = Math.Round(current.DurationMs - baseline.DurationMs, 1)
        };
    }

    /// <summary>
    /// Pulls a run-time capture date out of a fetched file. Prefers an explicit marker line
    /// ("FETCHED_AT: 2026-08-07", "Fetched: ...", "Fetched at ...", or a JSON
    /// "fetched_at"/"fetchedAt" field — all case-insensitive); falls back to the first
    /// ISO-ish YYYY-MM-DD date found. That fallback is deliberate: for a weather/API fetch
    /// the measurement "time" field (e.g. "2026-08-07T14:00") doubles as freshness evidence,
    /// so a cached file reusing old times still gets flagged even if the marker is absent.
    /// Returns null when nothing looks like a date, so a file without any run-time timestamp
    /// fails the freshness check.
    /// </summary>
    private static DateTime? ExtractRunDate(string content)
    {
        // Optional quote slots around the separator make the marker work both as a plain
        // leading line ("FETCHED_AT: 2026-08-07") and inside JSON ("fetched_at": "2026-08-07");
        // the lookbehind keeps it from matching mid-word prose like "refetched at …".
        var marker = Regex.Match(content,
            @"(?<![A-Za-z])(?:FETCHED_AT|fetched\s*(?:at|on|:))\s*[""']?\s*[:=]?\s*[""']?\s*(20\d{2})[-/.](\d{1,2})[-/.](\d{1,2})",
            RegexOptions.IgnoreCase);
        if (marker.Success && TryBuildDate(marker, out var marked)) return marked;
        // Negative lookahead (not \b) so ISO timestamps like "2026-08-07T14:32" still yield the date.
        var any = Regex.Match(content, @"\b(20\d{2})[-/.](\d{1,2})[-/.](\d{1,2})(?![0-9])");
        if (any.Success && TryBuildDate(any, out var fallback)) return fallback;
        return null;
    }

    private static bool TryBuildDate(Match m, out DateTime date)
    {
        date = default;
        if (!int.TryParse(m.Groups[1].Value, out var year) || year < 2000 || year > 2100) return false;
        if (!int.TryParse(m.Groups[2].Value, out var month) || month < 1 || month > 12) return false;
        if (!int.TryParse(m.Groups[3].Value, out var day) || day < 1 || day > 31) return false;
        try { date = new DateTime(year, month, day); return true; }
        catch { return false; }
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
    public List<BenchmarkAcceptanceCheck> AcceptanceChecks { get; set; } = new();
}

public enum BenchmarkCheckType { DirectoryExists, FileExists, FileContains, FileNotContains, FileOccurrenceCount, FileEquals, FileFreshTimestamp }

public static class Check
{
    public static BenchmarkAcceptanceCheck Dir(string name, string path, double weight = 1) =>
        new() { Name = name, Type = BenchmarkCheckType.DirectoryExists, Path = path, Weight = weight };
    public static BenchmarkAcceptanceCheck File(string name, string path, double weight = 1) =>
        new() { Name = name, Type = BenchmarkCheckType.FileExists, Path = path, Weight = weight };
    public static BenchmarkAcceptanceCheck Contains(string name, string path, string value, double weight = 1) =>
        new() { Name = name, Type = BenchmarkCheckType.FileContains, Path = path, Value = value, Weight = weight };
    public static BenchmarkAcceptanceCheck Contains(string name, string path, string value, double weight, string category) =>
        new() { Name = name, Type = BenchmarkCheckType.FileContains, Path = path, Value = value, Weight = weight, Category = category };
    public static BenchmarkAcceptanceCheck ContainsIc(string name, string path, string value, double weight = 1) =>
        new() { Name = name, Type = BenchmarkCheckType.FileContains, Path = path, Value = value, Weight = weight, IgnoreCase = true };
    public static BenchmarkAcceptanceCheck Exact(string name, string path, string value, double weight = 1) =>
        new() { Name = name, Type = BenchmarkCheckType.FileEquals, Path = path, Value = value, Weight = weight };
    public static BenchmarkAcceptanceCheck NotContains(string name, string path, string value, double weight = 1) =>
        new() { Name = name, Type = BenchmarkCheckType.FileNotContains, Path = path, Value = value, Weight = weight };
    public static BenchmarkAcceptanceCheck Occurs(string name, string path, string value, int count, double weight = 1) =>
        new() { Name = name, Type = BenchmarkCheckType.FileOccurrenceCount, Path = path, Value = value, ExpectedCount = count, Weight = weight };
    public static BenchmarkAcceptanceCheck Occurs(string name, string path, string value, int count, double weight, string category) =>
        new() { Name = name, Type = BenchmarkCheckType.FileOccurrenceCount, Path = path, Value = value, ExpectedCount = count, Weight = weight, Category = category };
    /// <summary>
    /// Verifies the file contains a run-time capture date (e.g. a "FETCHED_AT: YYYY-MM-DD"
    /// line) that (a) matches the file's own last-write date and (b) is recent relative to
    /// evaluation — so reusing a cached/stale file or hardcoding an old date is flagged.
    /// </summary>
    public static BenchmarkAcceptanceCheck FreshTimestamp(string name, string path, int maxDaysOld = 2, double weight = 2) =>
        new() { Name = name, Type = BenchmarkCheckType.FileFreshTimestamp, Path = path, Weight = weight, MaxDaysOld = maxDaysOld };
}

public class BenchmarkAcceptanceCheck
{
    public string Name { get; set; } = "";
    public BenchmarkCheckType Type { get; set; }
    public string? Path { get; set; }
    public string? Value { get; set; }
    public bool IgnoreCase { get; set; }
    public double Weight { get; set; } = 1;
    public int ExpectedCount { get; set; }
    public int MaxDaysOld { get; set; } = 2;
    public string Category { get; set; } = "correctness";
}

public class BenchmarkScore
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int Level { get; set; }
    public int SuccessfulEdits { get; set; }
    public int FailedEdits { get; set; }
    public int StepsUsed { get; set; }
    public int Points { get; set; }
    public double ScorePercent { get; set; }
    public double CorrectnessPercent { get; set; }
    public double? StepEfficiencyPercent { get; set; }
    public double? EditSuccessPercent { get; set; }
    public string Status { get; set; } = "";
    public SystemInfo? SystemInfo { get; set; }
    public string ModelUsed { get; set; } = "";
    public string? ErrorReason { get; set; }
    public double DurationMs { get; set; }
    public List<BenchmarkEditRecord> Edits { get; set; } = new();
    public List<BenchmarkCheckResult> Checks { get; set; } = new();
    public List<string> FailedSteps { get; set; } = new();
}

public class BenchmarkEditRecord
{
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public string EditAction { get; set; } = "";
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public string ToPath { get; set; } = "";
    public string? Error { get; set; }
}

public class BenchmarkCheckResult
{
    public string Name { get; set; } = "";
    public BenchmarkCheckType Type { get; set; }
    public bool Passed { get; set; }
    public double Weight { get; set; }
    public string Message { get; set; } = "";
    public string Category { get; set; } = "correctness";
}

public sealed record CommandCheckOutcome(int ExitCode, bool TimedOut, double DurationMs,
    string StandardOutput, string StandardError, string Message)
{
    public static CommandCheckOutcome Failed(string message) => new(-1, false, 0, "", "", message);
}

public class BenchmarkRegressionComparison
{
    public string BaselineScoreId { get; set; } = "";
    public double ScoreDelta { get; set; }
    public double DurationDeltaMs { get; set; }
    public bool HasRegression => ScoreDelta < 0;
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
