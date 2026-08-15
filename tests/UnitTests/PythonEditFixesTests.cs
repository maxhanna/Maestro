using Weaver.Controllers;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the fixes behind "the agent completely fails at editing Python files — good edits,
/// but FORMAT C/D fails at life" (benchmark_test_22/server.py): (1) FORMAT C insertAfter
/// realigned inserted methods with the generic min-indent logic, which counts a tab as ONE
/// character — a block whose lines mix tabs and spaces came out misaligned and the file died
/// with TabError; (2) the duplicate-property guard treated Python `else:`/`elif:`/`if:`
/// block headers as object keys and rejected good inserts ("newString contains 2 occurrences
/// of property 'else'"); (3) the LLM verifier's abandon-on-"syntax error" was neither
/// confirmed nor overridable — now a real `python -m py_compile` gate decides.
/// </summary>
public class PythonEditFixesTests
{
    // ── ReindentPythonBlock ──────────────────────────────────────────────────────────

    [Fact]
    public void TabAnchoredFile_MixedTabSpaceNewCode_ReindentsToTabsPreservingDepth()
    {
        // The anchor method lives at one tab inside the class body (tab-indented file). The
        // LLM output is mostly tabs with ONE stray 12-space line ("server.timeout = 1").
        const string anchorBaseIndent = "\t";
        var newCode = string.Join("\n",
            "\tdef passive_port_checking(server, port):",
            "\t\terror_occurred = False",
            "\t\ttry:",
            "            server.timeout = 1",
            "\t\t\tserver.handle_request()",
            "\t\texcept Exception as e:",
            "\t\t\tpass");
        var result = AgentCodeFormatting.ReindentPythonBlock(newCode, anchorBaseIndent);
        var lines = result.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("\tdef passive_port_checking(server, port):", lines[0]);
        Assert.Equal("\t\terror_occurred = False", lines[1]);
        Assert.Equal("\t\ttry:", lines[2]);
        Assert.Equal("\t\t\tserver.timeout = 1", lines[3]);
        Assert.Equal("\t\t\tserver.handle_request()", lines[4]);
        Assert.Equal("\t\texcept Exception as e:", lines[5]);
        Assert.Equal("\t\t\tpass", lines[6]);
        Assert.All(lines, l => Assert.DoesNotContain(' ', l.TakeWhile(c => c == ' ' || c == '\t')));
        Assert.All(lines, l => Assert.False(l.StartsWith("    ")));
    }

    [Fact]
    public void SpaceAnchoredFile_NewCodeKeepsRelativeDepth()
    {
        const string anchorBaseIndent = "    "; // 4-space file
        var newCode = string.Join("\n",
            "def helper():",
            "    return 42");
        var result = AgentCodeFormatting.ReindentPythonBlock(newCode, anchorBaseIndent);
        var lines = result.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("    def helper():", lines[0]);
        Assert.Equal("        return 42", lines[1]);
    }

    [Fact]
    public void TopLevelAnchor_NewCodeLandsAtColumnZero()
    {
        var result = AgentCodeFormatting.ReindentPythonBlock("def f():\n    pass", "");
        Assert.Equal("def f():\n    pass", result.Replace("\r\n", "\n"));
    }

    [Fact]
    public void DefLineFlushLeft_BodyDeeper_StillLandsAtAnchorWithBodyOneUnitDeeper()
    {
        // LLM emitted the def flush-left and the body with 8 spaces (an 8-space file) — the
        // body is ONE level deeper, so it must land at anchor + one tab.
        var result = AgentCodeFormatting.ReindentPythonBlock(
            "def f():\n        x = 1\n        return x", "\t");
        var lines = result.Replace("\r\n", "\n").Split('\n');
        Assert.Equal("\tdef f():", lines[0]);
        Assert.Equal("\t\tx = 1", lines[1]);
        Assert.Equal("\t\treturn x", lines[2]);
    }

    [Fact]
    public void BlankLinesStaysBlank_AndIdempotent()
    {
        var newCode = "def f():\n\n    pass";
        var once = AgentCodeFormatting.ReindentPythonBlock(newCode, "\t").Replace("\r\n", "\n");
        var twice = AgentCodeFormatting.ReindentPythonBlock(once, "\t").Replace("\r\n", "\n");
        Assert.Equal(once, twice);
        Assert.Contains("\tdef f():", once);
        Assert.Contains("\n\n", once);
        Assert.Contains("\t\tpass", once);
    }

    // ── DetectDuplicatePropertyAddition (Python guard misfire) ───────────────────────

    [Fact]
    public void PythonElse_NoLongerTripsDuplicatePropertyGuard()
    {
        // The exact failure: FORMAT C insertAfter oldStr = the anchor method (no else yet
        // in oldStr in the fallback path) and newStr contains else: twice — the guard used
        // to reject with "DUPLICATE PROPERTY ADDITION — property 'else'".
        var oldStr = "\tdef do_GET(self):\n\t\tpass";
        var newStr = string.Join("\n",
            "\tdef do_GET(self):",
            "\t\tpass",
            "",
            "\tdef passive_port_checking(server, port):",
            "\t\ttry:",
            "\t\t\tpass",
            "\t\telse:",
            "\t\t\tpass");
        Assert.Null(AgentEditHeuristics.DetectDuplicatePropertyAddition(oldStr, newStr, "benchmark_test_22/server.py"));
    }

    [Fact]
    public void PythonKeywordsAreNotObjectKeys()
    {
        // Even without relPath, `elif:`/`if:`/`with:` block headers must not count as keys.
        var oldStr = "def a():\n    if x:\n        pass";
        var newStr = oldStr + "\n\ndef b():\n    if y:\n        pass\n    elif z:\n        pass";
        Assert.Null(AgentEditHeuristics.DetectDuplicatePropertyAddition(oldStr, newStr));
    }

    [Fact]
    public void JsDuplicateKey_StillRejected()
    {
        var oldStr = "const a = {\n  name: \"a\",\n  value: 1\n};";
        var newStr = "const a = {\n  name: \"a\",\n  value: 1,\n  name: \"b\"\n};";
        Assert.NotNull(AgentEditHeuristics.DetectDuplicatePropertyAddition(oldStr, newStr));
    }

    // ── Deterministic Python syntax gate verdict ─────────────────────────────────────

    [Fact]
    public void Gate_EditedCompiles_Keeps()
    {
        Assert.Equal("keep", AgentController.PythonSyntaxGateVerdict(0, "", true, -1, "err"));
        Assert.Equal("keep", AgentController.PythonSyntaxGateVerdict(0, "", false, -1, "err"));
    }

    [Fact]
    public void Gate_NewCompileError_Abandons()
    {
        // Pre-edit compiled cleanly, edited broke → reject.
        Assert.Equal("abandon", AgentController.PythonSyntaxGateVerdict(-1, "IndentationError: unexpected indent", true, 0, ""));
        // Pre-edit missing (file create) → reject with the real error.
        Assert.Equal("abandon", AgentController.PythonSyntaxGateVerdict(-1, "TabError: inconsistent use of tabs", false, -1, ""));
    }

    [Fact]
    public void Gate_IdenticalPreExistingError_Neutral()
    {
        Assert.Equal("neutral",
            AgentController.PythonSyntaxGateVerdict(-1, "IndentationError: unindent does not match any outer indentation level",
                true, -1, "IndentationError: unindent does not match any outer indentation level"));
    }

    [Fact]
    public void Gate_InterpreterUnavailable_Neutral()
    {
        Assert.Equal("neutral", AgentController.PythonSyntaxGateVerdict(-2, "", true, -2, ""));
    }

    [Fact]
    public void NormalizePyCompileError_StripsTempPath_SoPreAndEditedCompareEqual()
    {
        var pre = AgentController.NormalizePyCompileError(
            "  File \"C:\\Temp\\weaver-pyverify\\pre_abc.py\", line 3\n    elif x:\n    ^\nIndentationError: unindent does not match any outer indentation level",
            "C:\\Temp\\weaver-pyverify\\pre_abc.py");
        var edited = AgentController.NormalizePyCompileError(
            "  File \"C:\\Temp\\weaver-pyverify\\edited_xyz.py\", line 3\n    elif x:\n    ^\nIndentationError: unindent does not match any outer indentation level",
            "C:\\Temp\\weaver-pyverify\\edited_xyz.py");
        Assert.Equal(pre, edited);
        Assert.Contains("<file>", pre);
    }

    [Fact]
    public void NormalizePyCompileError_DifferentErrors_DoNotCollapse()
    {
        var a = AgentController.NormalizePyCompileError("line 5\nNameError: x", "p.py");
        var b = AgentController.NormalizePyCompileError("line 9\nNameError: y", "p.py");
        Assert.NotEqual(a, b);
    }
}
