using System.Reflection;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks benchmark 4's platform-agnostic shape: the description must not pin a language or
/// filename (no 'server.py', no 'python'), the acceptance checks must scan the whole folder
/// rather than a hardcoded path, and the new DirectoryContains check type must pass/fail
/// deterministically across file layouts.
/// </summary>
public class BenchmarkAgnosticPlanTests
{
    private static BenchmarkPlanDefinition Level4 => BenchmarkService.GetBenchmarkPlans().First(p => p.Level == 4);

    private static string RunCheck(BenchmarkAcceptanceCheck check, string root)
    {
        var service = new BenchmarkService(SubstituteDb());
        var task = (Task<BenchmarkCheckResult>)typeof(BenchmarkService)
            .GetMethod("EvaluateCheckAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object[] { check, root, CancellationToken.None })!;
        var result = task.GetAwaiter().GetResult();
        return result.Passed ? "PASS" : "FAIL: " + result.Message;
    }

    private static DatabaseService SubstituteDb()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "weaver_agnostic_db_" + Guid.NewGuid().ToString("N"));
        return new DatabaseService(basePath + ".db", basePath + "_data", basePath + "_cfg.json");
    }

    private static string MakeTempDir()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "weaver_agnostic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    // ── Benchmark 4 plan shape ─────────────────────────────────────────────

    [Fact]
    public void Level4_Description_DoesNotPinLanguageOrFilename()
    {
        var desc = Level4.Description;
        Assert.Contains("9969", desc);
        Assert.Contains("/api/hello", desc);
        Assert.DoesNotContain("server.py", desc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("python", desc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("node", desc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Level4_Checks_ScanFolderInsteadOfHardcodedServerFile()
    {
        var checks = Level4.AcceptanceChecks;
        Assert.DoesNotContain(checks, c => c.Path != null && c.Path.Contains("server.py", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.DirectoryContains && c.Value == "9969");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.DirectoryContains && c.Value == "/api/hello");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileExists && c.Path == "benchmark_test_4/index.html");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.DirectoryExists && c.Path == "benchmark_test_4");
    }

    // ── DirectoryContains check type ───────────────────────────────────────

    [Fact]
    public void DirectoryContains_FoundInAnyFileUnderDir_Passes()
    {
        var tmp = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tmp, "index.html"), "<html>hi</html>");
            File.WriteAllText(Path.Combine(tmp, "server.py"), "PORT = 9969\ndef hello(): return {'message': 'Hello'}\n");
            var check = Check.AnyFileContains("port", ".", "9969");
            Assert.StartsWith("PASS", RunCheck(check, tmp));
        }
        finally { TryDelete(tmp); }
    }

    [Fact]
    public void DirectoryContains_FoundInNestedSubdirectory_Passes()
    {
        var tmp = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "src"));
            File.WriteAllText(Path.Combine(tmp, "src", "app.js"), "const PORT = 9969;");
            var check = Check.AnyFileContains("port", ".", "9969");
            Assert.StartsWith("PASS", RunCheck(check, tmp));
        }
        finally { TryDelete(tmp); }
    }

    [Fact]
    public void DirectoryContains_NotFoundInAnyFile_Fails()
    {
        var tmp = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tmp, "server.py"), "PORT = 8080");
            File.WriteAllText(Path.Combine(tmp, "index.html"), "<html>no port here</html>");
            var check = Check.AnyFileContains("port", ".", "9969");
            Assert.StartsWith("FAIL", RunCheck(check, tmp));
        }
        finally { TryDelete(tmp); }
    }

    [Fact]
    public void DirectoryContains_MissingDirectory_Fails()
    {
        var tmp = MakeTempDir();
        try
        {
            var check = Check.AnyFileContains("port", ".", "9969");
            Assert.StartsWith("FAIL", RunCheck(check, Path.Combine(tmp, "does_not_exist")));
        }
        finally { TryDelete(tmp); }
    }

    [Fact]
    public void DirectoryContains_SkipsBinaryFiles_StillFindsTextSibling()
    {
        var tmp = MakeTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(tmp, "logo.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02 });
            File.WriteAllText(Path.Combine(tmp, "server.py"), "PORT = 9969");
            var check = Check.AnyFileContains("port", ".", "9969");
            Assert.StartsWith("PASS", RunCheck(check, tmp));
        }
        finally { TryDelete(tmp); }
    }

    [Fact]
    public void DirectoryContains_OnlyBinaryFiles_Fails()
    {
        var tmp = MakeTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(tmp, "logo.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02 });
            var check = Check.AnyFileContains("port", ".", "9969");
            Assert.StartsWith("FAIL", RunCheck(check, tmp));
        }
        finally { TryDelete(tmp); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}
