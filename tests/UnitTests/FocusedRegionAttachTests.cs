using System.Text;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks in AgentDiscovery.AttachFocusedRegions — the plumbing that attaches
/// focused-region metadata (focused / focusIds / focusedOutput) to discovery
/// "read" step results before they are SSE-emitted, so the UI can render the
/// region with a collapse/expand affordance instead of the whole file.
/// </summary>
public class FocusedRegionAttachTests
{
    private const string LargeOutput =
        "line one\nline two\npublic void DoWork()\n{\n    var needle = 1;\n}\nline four\n";

    private static (string snippet, string? focusIds) Reader(string output, List<string> tokens, string path)
    {
        // Mirrors the real FocusLargeFileRead contract: null focusIds => full read stays.
        if (!output.Contains("needle", StringComparison.OrdinalIgnoreCase)) return (output, null);
        return ("NEEDLE_REGION", "needle");
    }

    private static AgentStep ReadStep() => new() { Type = "read", Path = "src/File.cs" };

    [Fact]
    public void ReadResult_WithIdentifierMatch_GetsFocusedFields()
    {
        var results = new Dictionary<string, object?>[]
        {
            new()
            {
                ["index"] = 0,
                ["type"] = "read",
                ["path"] = "src/File.cs",
                ["output"] = LargeOutput,
                ["status"] = "done"
            }
        };

        AgentDiscovery.AttachFocusedRegions(results, new[] { ReadStep() }, Reader, new List<string> { "needle" });

        Assert.Equal(true, results[0]["focused"]);
        Assert.Equal("needle", results[0]["focusIds"]);
        Assert.Equal("NEEDLE_REGION", results[0]["focusedOutput"]);
        // The full output stays intact for the "expand to full file" affordance.
        Assert.Equal(LargeOutput, results[0]["output"]);
    }

    [Fact]
    public void ReadResult_WithoutMatch_KeepsFullReadAndNoFocusedFields()
    {
        var results = new Dictionary<string, object?>[]
        {
            new()
            {
                ["index"] = 0,
                ["type"] = "read",
                ["path"] = "src/Other.cs",
                ["output"] = "no identifier here",
                ["status"] = "done"
            }
        };

        AgentDiscovery.AttachFocusedRegions(results, new[] { ReadStep() }, Reader, new List<string> { "needle" });

        Assert.False(results[0].ContainsKey("focused"));
        Assert.False(results[0].ContainsKey("focusIds"));
        Assert.False(results[0].ContainsKey("focusedOutput"));
    }

    [Fact]
    public void NonReadStep_NeverGetsFocusedFields()
    {
        var results = new Dictionary<string, object?>[]
        {
            new() { ["type"] = "list", ["path"] = ".", ["output"] = "[dir]  src", ["status"] = "done" }
        };

        AgentDiscovery.AttachFocusedRegions(results, new[] { new AgentStep { Type = "list" } }, Reader, new List<string> { "needle" });

        Assert.False(results[0].ContainsKey("focused"));
        Assert.False(results[0].ContainsKey("focusedOutput"));
    }

    [Fact]
    public void NullReader_IsNoOp()
    {
        var results = new Dictionary<string, object?>[]
        {
            new() { ["type"] = "read", ["output"] = LargeOutput, ["status"] = "done" }
        };

        AgentDiscovery.AttachFocusedRegions(results, new[] { ReadStep() }, null, null);

        Assert.False(results[0].ContainsKey("focused"));
        Assert.False(results[0].ContainsKey("focusedOutput"));
    }

    [Fact]
    public void FailedRead_WithNoOutput_GetsNoFocusedFields()
    {
        var results = new Dictionary<string, object?>[]
        {
            new() { ["type"] = "read", ["path"] = "src/Gone.cs", ["status"] = "error", ["error"] = "File not found" }
        };

        AgentDiscovery.AttachFocusedRegions(results, new[] { ReadStep() }, Reader, new List<string> { "needle" });

        Assert.False(results[0].ContainsKey("focused"));
        Assert.False(results[0].ContainsKey("focusedOutput"));
    }
}

/// <summary>
/// Locks in AgentDiscovery.FocusLargeFileRead — the shared focused-read rule used by the
/// bootstrap auto-read, the _discover tool, AND the _explore pipeline (a symbol-targeted
/// explore of a large file returns just the enclosing method instead of the whole file).
/// </summary>
public class FocusLargeFileReadTests
{
    private static string BuildLargeCsFileWithMethod(string methodBody)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 400; i++) sb.AppendLine($"    // filler line {i} padding the file well past the threshold");
        sb.AppendLine("public class Calculator");
        sb.AppendLine("{");
        sb.AppendLine("    public int Add(int a, int b)");
        sb.AppendLine("    {");
        sb.AppendLine(methodBody);
        sb.AppendLine("        return 0;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        for (var i = 0; i < 400; i++) sb.AppendLine($"    // trailing filler {i}");
        return sb.ToString();
    }

    [Fact]
    public void LargeFile_WithIdentifierMatchInside_ReturnsFocusedRegion()
    {
        var output = BuildLargeCsFileWithMethod("        var calcResult = a + b;");
        Assert.True(output.Length >= AgentDiscovery.LargeFileFocusThresholdChars);

        var (snippet, focusIds) = AgentDiscovery.FocusLargeFileRead(
            output, new List<string> { "calcResult" }, "src/Calculator.cs");

        Assert.NotNull(focusIds);
        Assert.Contains("calcResult", focusIds);
        Assert.Contains("public int Add(int a, int b)", snippet); // enclosing method
        Assert.NotEqual(output, snippet);
        Assert.True(snippet.Length < output.Length);
    }

    [Fact]
    public void SmallFile_AlwaysStaysWhole()
    {
        var output = "public class Small\n{\n    public void DoWork() { var needle = 1; }\n}\n";

        var (snippet, focusIds) = AgentDiscovery.FocusLargeFileRead(
            output, new List<string> { "needle" }, "src/Small.cs");

        Assert.Null(focusIds);
        Assert.Equal(output, snippet);
    }

    [Fact]
    public void LargeFile_WithNoContentMatch_StaysWhole()
    {
        var output = new string('x', AgentDiscovery.LargeFileFocusThresholdChars + 500);

        var (snippet, focusIds) = AgentDiscovery.FocusLargeFileRead(
            output, new List<string> { "unmatchedToken" }, "src/Big.cs");

        Assert.Null(focusIds);
        Assert.Equal(output, snippet);
    }

    [Fact]
    public void LargeFile_WithNoTokens_StaysWhole()
    {
        var output = new string('y', AgentDiscovery.LargeFileFocusThresholdChars + 500);

        var (snippet, focusIds) = AgentDiscovery.FocusLargeFileRead(
            output, new List<string>(), "src/Big.cs");

        Assert.Null(focusIds);
        Assert.Equal(output, snippet);
    }

    [Fact]
    public void ExplicitLoweredThreshold_FocusesMidSizedFile()
    {
        // ~14k chars: below the default 20k threshold (stays whole), but above a
        // pressure-lowered threshold (gets focused) — this is the hot-context case.
        var output = new StringBuilder();
        for (var i = 0; i < 250; i++) output.AppendLine($"    // filler line {i} padding the mid-sized file");
        output.AppendLine("public class Mid");
        output.AppendLine("{");
        output.AppendLine("    public void Work()");
        output.AppendLine("    {");
        output.AppendLine("        var midResult = 1;");
        output.AppendLine("    }");
        output.AppendLine("}");
        for (var i = 0; i < 250; i++) output.AppendLine($"    // trailing filler {i}");
        var content = output.ToString();
        var tokens = new List<string> { "midResult" };
        var lowered = AgentDiscovery.LargeFileFocusThresholdChars / 2; // 10k
        Assert.InRange(content.Length, lowered + 1, AgentDiscovery.LargeFileFocusThresholdChars - 1);

        var (wholeSnippet, wholeIds) = AgentDiscovery.FocusLargeFileRead(content, tokens, "src/Mid.cs");
        Assert.Null(wholeIds);
        Assert.Equal(content, wholeSnippet);

        var (regionSnippet, regionIds) = AgentDiscovery.FocusLargeFileRead(content, tokens, "src/Mid.cs", lowered);
        Assert.NotNull(regionIds);
        Assert.Contains("midResult", regionIds);
        Assert.Contains("public void Work()", regionSnippet);
        Assert.True(regionSnippet.Length < content.Length);
    }
}

/// <summary>
/// Locks in AgentDiscovery.FocusThresholdForPressure — the auto-tune curve that shrinks
/// the focus threshold when the discovery context runs hot, so large files contribute
/// focused regions instead of being dropped at the budget edge. Pure function: no shared
/// mutable state, safe across concurrent agent runs.
/// </summary>
public class FocusThresholdForPressureTests
{
    [Fact]
    public void ZeroPressure_KeepsDefaultThreshold()
    {
        Assert.Equal(AgentDiscovery.LargeFileFocusThresholdChars,
            AgentDiscovery.FocusThresholdForPressure(0));
        Assert.Equal(AgentDiscovery.LargeFileFocusThresholdChars,
            AgentDiscovery.FocusThresholdForPressure(-1));
    }

    [Fact]
    public void ModeratePressure_ScalesDownLinearly()
    {
        // 0.5 pressure → 20_000 * (1 - 0.4) = 12_000.
        Assert.Equal(12_000, AgentDiscovery.FocusThresholdForPressure(0.5));
    }

    [Fact]
    public void FullPressure_ClampsToFloor()
    {
        Assert.Equal(AgentDiscovery.LargeFileFocusThresholdFloor,
            AgentDiscovery.FocusThresholdForPressure(1.0));
        Assert.Equal(AgentDiscovery.LargeFileFocusThresholdFloor,
            AgentDiscovery.FocusThresholdForPressure(2.0)); // clamped, never below floor
    }

    [Fact]
    public void CurveIsMonotonicallyNonIncreasing()
    {
        var prev = AgentDiscovery.FocusThresholdForPressure(0);
        for (var p = 0.0; p <= 1.01; p += 0.05)
        {
            var next = AgentDiscovery.FocusThresholdForPressure(p);
            Assert.True(next <= prev, $"threshold rose at pressure {p}");
            prev = next;
        }
    }
}

/// <summary>
/// Locks in AgentDiscovery.TryRefocusHotFile — the bootstrap auto-read's hot-context
/// decision: when a file won't fit the aggregate budget, shrink the focus threshold so it
/// contributes its key regions instead of being dropped whole.
/// </summary>
public class TryRefocusHotFileTests
{
    private static string BuildMidSizedFile()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 250; i++) sb.AppendLine($"    // filler line {i} padding the mid-sized file");
        sb.AppendLine("public class Mid");
        sb.AppendLine("{");
        sb.AppendLine("    public void Work()");
        sb.AppendLine("    {");
        sb.AppendLine("        var calcResult = 1;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        for (var i = 0; i < 250; i++) sb.AppendLine($"    // trailing filler {i}");
        return sb.ToString();
    }

    private static readonly string MidFile = BuildMidSizedFile();
    private static readonly List<string> Tokens = new() { "calcResult" };

    [Fact]
    public void OverBudget_MidFileWithMatch_RefocusesWithLoweredThreshold()
    {
        // Pressure 0.5 -> candidate threshold 12k; file is ~16k (below default 20k, above
        // 12k) and matches an identifier, so it can be re-focused instead of dropped.
        Assert.InRange(MidFile.Length, 12_001, 19_999);

        var ok = AgentDiscovery.TryRefocusHotFile(MidFile, Tokens, "src/Mid.cs",
            alreadyFocused: false, currentSnippetLength: MidFile.Length,
            usedChars: 50_000, totalBudget: 100_000, effectiveThreshold: 20_000,
            out var snippet, out var ids, out var threshold);

        Assert.True(ok);
        Assert.NotNull(ids);
        Assert.Contains("calcResult", ids);
        Assert.Contains("public void Work()", snippet);
        Assert.True(snippet.Length < MidFile.Length);
        Assert.Equal(12_000, threshold);
    }

    [Fact]
    public void AlreadyFocused_ReturnsFalse()
    {
        var ok = AgentDiscovery.TryRefocusHotFile(MidFile, Tokens, "src/Mid.cs",
            alreadyFocused: true, currentSnippetLength: 200,
            usedChars: 50_000, totalBudget: 100_000, effectiveThreshold: 20_000,
            out var snippet, out var ids, out var threshold);

        Assert.False(ok);
    }

    [Fact]
    public void FileBelowCandidateThreshold_ReturnsFalse()
    {
        var ok = AgentDiscovery.TryRefocusHotFile("tiny file", Tokens, "src/Tiny.cs",
            alreadyFocused: false, currentSnippetLength: 10,
            usedChars: 50_000, totalBudget: 100_000, effectiveThreshold: 20_000,
            out var snippet, out var ids, out var threshold);

        Assert.False(ok);
    }

    [Fact]
    public void RefocusedRegionStillOverBudget_ReturnsFalse()
    {
        // Used chars pinned with only 10 chars of budget left — even the small method
        // region can't fit, so the file must be dropped.
        var ok = AgentDiscovery.TryRefocusHotFile(MidFile, Tokens, "src/Mid.cs",
            alreadyFocused: false, currentSnippetLength: MidFile.Length,
            usedChars: 99_990, totalBudget: 100_000, effectiveThreshold: 20_000,
            out var snippet, out var ids, out var threshold);

        Assert.False(ok);
    }

    [Fact]
    public void CandidateEqualsEffectiveThreshold_StillRefocuses()
    {
        // Rounding tie: candidate (12k) == already-lowered threshold — the previously
        // tuned threshold still applies to the next file instead of dropping it.
        var ok = AgentDiscovery.TryRefocusHotFile(MidFile, Tokens, "src/Mid.cs",
            alreadyFocused: false, currentSnippetLength: MidFile.Length,
            usedChars: 50_000, totalBudget: 100_000, effectiveThreshold: 12_000,
            out var snippet, out var ids, out var threshold);

        Assert.True(ok);
        Assert.Equal(12_000, threshold);
    }
}
