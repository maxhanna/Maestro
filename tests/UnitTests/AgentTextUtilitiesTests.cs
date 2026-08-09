using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for <c>AgentTextUtilities.BuildVerifierFileView</c> — the windowed view of large
/// files fed to the post-execution verifier. Regression: a 36k-char stylesheet whose edit
/// lands at char ~29k was head-truncated to the first 12k chars, so the verifier honestly
/// reported the freshly added rule as "not found" and spawned a redundant repair step. The
/// windowed view guarantees the edited region (anchored by each applied edit's newString)
/// is always visible, alongside a bounded head and tail.
/// </summary>
public class AgentTextUtilitiesTests
{
    private const string Line = "// filler line filler line filler line\n"; // 42 chars

    private static string Fill(int lines) => string.Concat(Enumerable.Repeat(Line, lines));

    [Fact]
    public void BuildVerifierFileView_SmallFile_ReturnedWhole()
    {
        const string content = "a\nb\nc";
        Assert.Equal(content, AgentTextUtilities.BuildVerifierFileView(content, null, 12000));
    }

    [Fact]
    public void BuildVerifierFileView_LargeFileWithAnchor_ShowsEditedRegionAndHeadAndTail()
    {
        // head: 150 lines (6300 chars — beyond the 3000-char head budget, so the head
        // truncation marker appears); mid1: filler before the edit; anchor: the new CSS
        // rule (~char 9k of a ~16k file); mid2: filler after the edit, far from both the
        // anchor window and the tail; tail: last ~3.6k chars (beyond the 2000-char tail
        // budget, so the tail marker appears).
        const string anchor = ".kanban-card .attachments .attachment-item {\n white-space: nowrap !important;\n overflow: hidden !important;\n text-overflow: ellipsis !important;\n}";
        var content = Fill(150) + Fill(300) + anchor + "\n" +
            string.Concat(Enumerable.Repeat("/* gap2 filler */\n", 100)) +
            "/* GAP2_SENTINEL_OMITTED */\n" +
            string.Concat(Enumerable.Repeat("/* gap2 filler */\n", 200)) +
            string.Concat(Enumerable.Repeat("/* tail filler line */\n", 200));
        Assert.True(content.Length > 12000);

        var view = AgentTextUtilities.BuildVerifierFileView(content, new[] { anchor }, 12000);

        // The edited region MUST be visible — this is the regression this helper fixes.
        Assert.Contains(".kanban-card .attachments .attachment-item", view);
        Assert.Contains("text-overflow: ellipsis !important", view);
        Assert.Contains("EDITED REGION", view);
        // Head and tail markers present; the view is bounded.
        Assert.Contains("head of file shown", view);
        Assert.Contains("TAIL", view);
        Assert.Contains("TRUNCATED", view);
        Assert.True(view.Length < content.Length);
        // The middle beyond the anchor's ±400 window (and before the tail) is omitted.
        Assert.DoesNotContain("GAP2_SENTINEL_OMITTED", view);
    }

    [Fact]
    public void BuildVerifierFileView_VerbatimMiss_FallsBackToLongestLine()
    {
        // The edit was reformatted after apply (e.g. a CSS dedupe re-serialized the body),
        // so the verbatim anchor no longer matches — but its selector line still does.
        var content = Fill(700) +
            ".kanban-card .attachments .attachment-item {\n  white-space: nowrap !important;\n}\n" +
            Fill(700);
        var verbatimAnchor = ".kanban-card .attachments .attachment-item {\n white-space: nowrap !important;\n overflow: hidden !important;\n}";
        Assert.True(content.Length > 12000);

        var view = AgentTextUtilities.BuildVerifierFileView(content, new[] { verbatimAnchor }, 12000);

        Assert.Contains(".kanban-card .attachments .attachment-item", view);
        Assert.Contains("EDITED REGION", view);
    }

    [Fact]
    public void BuildVerifierFileView_MultipleAnchors_AllRegionsVisible()
    {
        const string anchorA = ".settings-edit-field {\n display: flex;\n}";
        const string anchorB = ".command-item {\n cursor: pointer;\n}";
        var content = Fill(400) + anchorA + "\n" + Fill(500) + anchorB + "\n" + Fill(400);
        Assert.True(content.Length > 12000);

        var view = AgentTextUtilities.BuildVerifierFileView(content, new[] { anchorA, anchorB }, 12000);

        Assert.Contains(".settings-edit-field", view);
        Assert.Contains(".command-item", view);
        Assert.Equal(2, CountOccurrences(view, "EDITED REGION"));
    }

    [Fact]
    public void BuildVerifierFileView_NoAnchors_HeadAndTailOnlyBounded()
    {
        var content = Fill(400) +
            string.Concat(Enumerable.Repeat("/* MIDDLE_SENTINEL line */\n", 800)) +
            Fill(400);
        Assert.True(content.Length > 12000);

        var view = AgentTextUtilities.BuildVerifierFileView(content, null, 12000);

        Assert.Contains("head of file shown", view);
        Assert.Contains("TAIL", view);
        Assert.DoesNotContain("EDITED REGION", view);
        Assert.DoesNotContain("MIDDLE_SENTINEL", view);
        Assert.True(view.Length < content.Length);
        Assert.True(view.Length <= 12000 + 500); // bounded (markers may push slightly past)
    }

    [Fact]
    public void BuildVerifierFileView_AnchorAtVeryStart_StillBounded()
    {
        const string anchor = ".top-rule {\n color: blue;\n}";
        var content = anchor + "\n" + Fill(1500);
        Assert.True(content.Length > 12000);

        var view = AgentTextUtilities.BuildVerifierFileView(content, new[] { anchor }, 12000);

        Assert.Contains(".top-rule", view);
        Assert.True(view.Length < content.Length);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
