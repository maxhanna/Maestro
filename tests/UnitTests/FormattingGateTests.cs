using Xunit;
using Weaver;

namespace Weaver.UnitTests;

public class FormattingGateTests
{
    [Fact]
    public async Task CheckAsync_MachineCannotCheckARequiredExtension_IsUnmeasuredNotClean()
    {
        // The honesty guarantee of the settings split: a card that requires .py checks
        // must not pass on a machine that only knows how to check .js. Reporting null
        // keeps it out of perfectPass instead of crediting an unperformed check.
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.js"), "const a = 1;\n");

            var result = await FormattingGate.CheckAsync(dir, new[] { "a.js" },
                new BenchmarkFormatting { Mode = "formatter", Extensions = new List<string> { "py" } },
                new Dictionary<string, string> { ["js"] = "cmd.exe /c exit 0" });

            Assert.Null(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CheckAsync_MachineCoversEveryRequiredExtension_RunsTheCheck()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.js"), "const a = 1;\n");

            var result = await FormattingGate.CheckAsync(dir, new[] { "a.js" },
                new BenchmarkFormatting { Mode = "formatter", Extensions = new List<string> { "js" } },
                new Dictionary<string, string> { ["js"] = "cmd.exe /c exit 0" });

            Assert.True(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CheckAsync_NoMachineCommandsConfigured_IsUnmeasured()
    {
        var result = await FormattingGate.CheckAsync(Path.GetTempPath(), new[] { "a.cs" },
            new BenchmarkFormatting { Mode = "formatter" }, null);

        Assert.Null(result);
    }

    [Fact]
    public void Tokenize_SplitsOnWhitespace()
    {
        var tokens = FormattingGate.Tokenize("dotnet format --verify-no-changes --include {file}");
        Assert.Equal(new[] { "dotnet", "format", "--verify-no-changes", "--include", "{file}" }, tokens);
    }

    [Fact]
    public void Tokenize_RespectsQuotedSegments()
    {
        var tokens = FormattingGate.Tokenize("prettier --config \"my config.json\" {file}");
        Assert.Equal(new[] { "prettier", "--config", "my config.json", "{file}" }, tokens);
    }

    [Theory]
    [InlineData("a & calc.exe")]
    [InlineData("evil; rm -rf ~")]
    [InlineData("$(echo pwned)")]
    [InlineData("`echo pwned`")]
    [InlineData("a|b>c<d^e")]
    public void FilePlaceholderSubstitution_PreservesShellMetacharactersAsOneAtomicArgument(string maliciousLookingPath)
    {
        // {file} must survive substitution as a single ArgumentList element regardless of
        // embedded shell metacharacters. This is the actual fix: file paths come from
        // agent-authored file names (and cards may be shared via the BugHosted
        // leaderboard), so the substituted value must never be handed to a shell for
        // re-parsing. Passing it as one argv element (rather than interpolating into a
        // "cmd.exe /c ..." string) means characters like & | ; ` $() are inert.
        var tokens = FormattingGate.Tokenize("dotnet format --include {file}");
        var fileTokenIndex = tokens.IndexOf("{file}");
        Assert.True(fileTokenIndex >= 0);

        var substituted = tokens.Select(t => t == "{file}" ? maliciousLookingPath : t).ToList();

        Assert.Equal(tokens.Count, substituted.Count); // no extra tokens were produced
        Assert.Equal(maliciousLookingPath, substituted[fileTokenIndex]); // preserved verbatim, unsplit
    }

    [Fact]
    public async Task CheckAsync_ModeNone_ReturnsNull()
    {
        var result = await FormattingGate.CheckAsync(Path.GetTempPath(), new[] { "a.cs" },
            new BenchmarkFormatting { Mode = "none" }, null);
        Assert.Null(result);
    }

    [Fact]
    public async Task CheckAsync_NoConfiguredExtension_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.py"), "print(1)");
            var result = await FormattingGate.CheckAsync(dir, new[] { "a.py" },
                new BenchmarkFormatting { Mode = "formatter" },
                new Dictionary<string, string> { ["cs"] = "cmd.exe /c exit 0" });
            Assert.Null(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CheckAsync_ConfiguredCommandExitsZero_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.cs"), "class A {}");
            var result = await FormattingGate.CheckAsync(dir, new[] { "a.cs" },
                new BenchmarkFormatting { Mode = "formatter" },
                new Dictionary<string, string> { ["cs"] = "cmd.exe /c exit 0" });
            Assert.True(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CheckAsync_ConfiguredCommandExitsNonZero_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.cs"), "class A {}");
            var result = await FormattingGate.CheckAsync(dir, new[] { "a.cs" },
                new BenchmarkFormatting { Mode = "formatter" },
                new Dictionary<string, string> { ["cs"] = "cmd.exe /c exit 1" });
            Assert.False(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    // Every card in the repo (docs example, both Phase 5 seed cards) configures
    // `dotnet format --verify-no-changes --include {file}` as its formatting oracle.
    // dotnet format's --include filters against paths RELATIVE to the project it's
    // formatting — substituting an absolute path there silently matches zero files,
    // so --verify-no-changes trivially "passes" even a badly formatted file. These
    // tests exercise the real command (not a synthetic cmd.exe stand-in) to catch
    // that regression directly, matching the empirical repro used to find the bug.
    [Fact]
    public async Task CheckAsync_DotnetFormatOnBadlyFormattedFile_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "FmtTest.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir, "Bad.cs"),
                "namespace FmtTest;\npublic class Bad\n{\n        public int Add(int a,int b){\n    return a+b;\n        }\n}\n");

            var result = await FormattingGate.CheckAsync(dir, new[] { "Bad.cs" },
                new BenchmarkFormatting { Mode = "formatter" },
                new Dictionary<string, string> { ["cs"] = "dotnet format --verify-no-changes --include {file}" });

            Assert.False(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CheckAsync_DotnetFormatOnWellFormattedFile_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "FmtTest.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir, "Good.cs"),
                "namespace FmtTest;\n\npublic class Good\n{\n    public int Add(int a, int b)\n    {\n        return a + b;\n    }\n}\n");

            var result = await FormattingGate.CheckAsync(dir, new[] { "Good.cs" },
                new BenchmarkFormatting { Mode = "formatter" },
                new Dictionary<string, string> { ["cs"] = "dotnet format --verify-no-changes --include {file}" });

            Assert.True(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CheckAsync_DotnetFormatOnNestedFile_UsesRelativePathAndDetectsBadFormatting()
    {
        // Regression coverage for the specific failure mode found: an absolute {file}
        // substitution matches nothing even when the file is in a subdirectory (the
        // shape both Phase 5 seed cards actually create files in — tests/**, todocli/**).
        var dir = Path.Combine(Path.GetTempPath(), "weaver-fmt-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "FmtTest.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            Directory.CreateDirectory(Path.Combine(dir, "tests"));
            File.WriteAllText(Path.Combine(dir, "tests", "Bad.cs"),
                "namespace FmtTest;\npublic class Bad\n{\n        public int Add(int a,int b){\n    return a+b;\n        }\n}\n");

            var result = await FormattingGate.CheckAsync(dir, new[] { "tests/Bad.cs" },
                new BenchmarkFormatting { Mode = "formatter" },
                new Dictionary<string, string> { ["cs"] = "dotnet format --verify-no-changes --include {file}" });

            Assert.False(result);
        }
        finally { Directory.Delete(dir, true); }
    }
}
