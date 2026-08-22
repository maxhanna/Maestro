using System.Linq;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks AgentDiffUtilities.BuildAlignedDiff — the truthful before→after line diff behind
/// the "files changed" summary. The live navigation.component.ts run exposed the bug it
/// fixes: diffPreview and the +/− counts were computed from the LLM's oldString/newString,
/// which carried drifted indentation, so a SINGLE inserted moviesTodoCount line rendered as
/// 2 lines removed / 3 added (every line looked changed). BuildAlignedDiff LCS-aligns the
/// REAL file content, pairing unchanged lines so a pure insert is exactly +1.
/// </summary>
public class AgentDiffUtilitiesTests
{
    private const string Before =
        "export class NavigationComponent {\n" +
        "    tradeNotifsCount = 0;\n" +
        "\n" +
        "    musicTodoCount: number | null = null;\n" +
        "\n" +
        "    arrayActivePlayers: number | null = null;\n" +
        "\n" +
        "    ngOnInit() { }\n" +
        "}";

    private static readonly string AfterInsert =
        "export class NavigationComponent {\n" +
        "    tradeNotifsCount = 0;\n" +
        "\n" +
        "    musicTodoCount: number | null = null;\n" +
        "    moviesTodoCount: number | null = null;\n" +
        "\n" +
        "    arrayActivePlayers: number | null = null;\n" +
        "\n" +
        "    ngOnInit() { }\n" +
        "}";

    [Fact]
    public void PureSingleLineInsert_IsExactlyOneAddedLine()
    {
        // The live regression: one line inserted, nothing removed.
        var (added, removed, oldLines, newLines, preview, _) =
            AgentDiffUtilities.BuildAlignedDiff(Before, AfterInsert);

        Assert.Equal(1, added);
        Assert.Equal(0, removed);
        Assert.Contains("+     moviesTodoCount: number | null = null;", preview);
        // Unchanged lines must NOT appear as removals or re-additions.
        Assert.DoesNotContain("-     musicTodoCount", preview);
        Assert.DoesNotContain("-     arrayActivePlayers", preview);
        Assert.DoesNotContain("+     musicTodoCount", preview);

        // Aligned arrays: equal length, the insert row has null on the old side.
        Assert.Equal(oldLines.Length, newLines.Length);
        var insertIdx = -1;
        for (var i = 0; i < newLines.Length; i++)
        {
            if (Equals(newLines[i], "    moviesTodoCount: number | null = null;"))
            {
                insertIdx = i;
                break;
            }
        }
        Assert.True(insertIdx >= 0, "aligned newLines must contain the inserted line");
        Assert.Null(oldLines[insertIdx]);
    }

    [Fact]
    public void PureRemoval_IsExactlyOneRemovedLine()
    {
        // The reverse edit: drop the inserted line again.
        var (added, removed, _, _, preview, _) =
            AgentDiffUtilities.BuildAlignedDiff(AfterInsert, Before);

        Assert.Equal(0, added);
        Assert.Equal(1, removed);
        Assert.Contains("-     moviesTodoCount: number | null = null;", preview);
        Assert.DoesNotContain("+     moviesTodoCount", preview);
    }

    [Fact]
    public void SingleLineModification_OnlyThatLineShowsAsChanged()
    {
        // One line edited: LCS treats it as delete+insert of THAT line (the two texts don't
        // align), so the +/− counts are 1/1 — and critically the UNCHANGED lines around it
        // must not appear as removed+re-added (the drift bug's signature).
        var after = Before.Replace("    tradeNotifsCount = 0;", "    tradeNotifsCount = 1;");
        var (added, removed, oldLines, newLines, preview, _) =
            AgentDiffUtilities.BuildAlignedDiff(Before, after);

        Assert.Equal(1, added);
        Assert.Equal(1, removed);
        Assert.Contains("-     tradeNotifsCount = 0;", preview);
        Assert.Contains("+     tradeNotifsCount = 1;", preview);
        Assert.DoesNotContain("-     musicTodoCount", preview);
        Assert.DoesNotContain("-     arrayActivePlayers", preview);
        Assert.DoesNotContain("+     musicTodoCount", preview);

        var oldIdx = -1;
        var newIdx = -1;
        for (var i = 0; i < oldLines.Length; i++)
        {
            if (Equals(oldLines[i], "    tradeNotifsCount = 0;")) oldIdx = i;
            if (Equals(newLines[i], "    tradeNotifsCount = 1;")) newIdx = i;
        }
        Assert.True(oldIdx >= 0, "removed line present on the old side");
        Assert.True(newIdx >= 0, "added line present on the new side");
        Assert.Null(newLines[oldIdx]);
        Assert.Null(oldLines[newIdx]);
    }

    [Fact]
    public void NoChange_ReturnsEmptyDiff()
    {
        var (added, removed, oldLines, newLines, preview, _) =
            AgentDiffUtilities.BuildAlignedDiff(Before, Before);
        Assert.Equal(0, added);
        Assert.Equal(0, removed);
        Assert.Empty(oldLines);
        Assert.Empty(newLines);
        Assert.Equal("", preview);
    }

    [Fact]
    public void InsertAtStartAndEnd_CountsBothAdds()
    {
        var after = "// header\n" + AfterInsert + "\n// footer";
        var (added, removed, _, _, preview, _) =
            AgentDiffUtilities.BuildAlignedDiff(AfterInsert, after);
        Assert.Equal(2, added);
        Assert.Equal(0, removed);
        Assert.Contains("+ // header", preview);
        Assert.Contains("+ // footer", preview);
    }

    [Fact]
    public void OldStartLine_PointsAtWindowStart()
    {
        // maxContextLines=0 → the window is exactly the changed row; OldStartLine is the
        // 0-based OLD line index at the insertion point (line 4 = the blank before
        // arrayActivePlayers in the before content).
        var (_, _, _, _, _, oldStartLine) =
            AgentDiffUtilities.BuildAlignedDiff(Before, AfterInsert, maxContextLines: 0);
        Assert.Equal(4, oldStartLine);

        // A change at the very top has OldStartLine 0.
        var afterTop = "// top\n" + Before;
        var (_, _, _, _, _, oldStartTop) =
            AgentDiffUtilities.BuildAlignedDiff(Before, afterTop, maxContextLines: 0);
        Assert.Equal(0, oldStartTop);
    }
}
