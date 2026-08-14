using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks WebPageProbeService — the deterministic "read the live page" layer: AngleSharp
/// parsing of fetched HTML into PageSnapshots, keyword section discovery, and
/// presence/absence verification. All tests feed fixture HTML directly (no network),
/// and the expectations encode the exact DOM-extraction rules.
/// </summary>
public class WebPageProbeServiceTests
{
    private const string KanbanHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>Task Board</title></head>
        <body>
          <nav>
            <a href="/">Home</a>
            <a href="/kanban">Kanban Board</a>
            <a href="/calendar">Calendar</a>
          </nav>
          <h1>Task Board</h1>
          <h2>Kanban Board</h2>
          <p>Drag cards between columns to manage your work.</p>
          <button>Save changes</button>
          <input type="text" name="cardTitle" />
        </body>
        </html>
        """;

    private static PageSnapshot Snapshot(string html) => WebPageProbeService.SnapshotFromHtml(html);

    // ── snapshot extraction ──────────────────────────────────────────────────

    [Fact]
    public void SnapshotFromHtml_ExtractsTitleHeadingsLinksButtonsInputsText()
    {
        var s = Snapshot(KanbanHtml);
        Assert.Equal("Task Board", s.Title);
        Assert.Contains("Task Board", s.Headings);
        Assert.Contains("Kanban Board", s.Headings);
        Assert.Contains(new PageLink("Kanban Board", "/kanban"), s.Links);
        Assert.Contains(new PageLink("Home", "/"), s.Links);
        Assert.Contains("Save changes", s.Buttons);
        Assert.Contains("text \"cardTitle\"", s.Inputs);
        Assert.Contains("Drag cards between columns", s.BodyText);
    }

    [Fact]
    public void SnapshotFromHtml_NormalizesWhitespace()
    {
        var s = Snapshot("<html><head><title>  A  B </title></head><body>  hello\n world </body></html>");
        Assert.Equal("A B", s.Title);
        Assert.Equal("hello world", s.BodyText);
    }

    [Fact]
    public void SnapshotFromHtml_LinkWithoutText_UsesHref()
    {
        var s = Snapshot("<a href='/plain'></a>");
        var link = Assert.Single(s.Links);
        Assert.Equal("/plain", link.Text);
        Assert.Equal("/plain", link.Href);
    }

    [Fact]
    public void SnapshotFromHtml_SkipsHiddenAndPasswordInputs()
    {
        var s = Snapshot("<input type='hidden' name='h'><input type='password' name='p'><input name='ok'>");
        var input = Assert.Single(s.Inputs);
        Assert.Contains("ok", input);
    }

    // ── section discovery ────────────────────────────────────────────────────

    [Fact]
    public void FindTargetSection_HeadingMatch_FindsKanban()
    {
        var s = Snapshot(KanbanHtml);
        var match = WebPageProbeService.FindTargetSection(s, "kanban board");
        Assert.NotNull(match);
        Assert.Equal("Kanban Board", match!.Label);
        Assert.Equal("heading", match.Kind);
    }

    [Fact]
    public void FindTargetSection_LinkMatch_FindsCalendar()
    {
        var s = Snapshot(KanbanHtml);
        var match = WebPageProbeService.FindTargetSection(s, "calendar page");
        Assert.NotNull(match);
        Assert.Equal("Calendar", match!.Label);
        Assert.Equal("/calendar", match.Url);
        Assert.Equal("link", match.Kind);
    }

    [Fact]
    public void FindTargetSection_ButtonMatch_FindsButton()
    {
        var s = Snapshot(KanbanHtml);
        var match = WebPageProbeService.FindTargetSection(s, "save changes");
        Assert.NotNull(match);
        Assert.Equal("Save changes", match!.Label);
        Assert.Equal("button", match.Kind);
    }

    [Fact]
    public void FindTargetSection_NoMatch_ReturnsNull()
    {
        var s = Snapshot(KanbanHtml);
        Assert.Null(WebPageProbeService.FindTargetSection(s, "weather widget"));
    }

    [Fact]
    public void FindTargetSection_Deterministic_SameResult()
    {
        var s = Snapshot(KanbanHtml);
        var a = WebPageProbeService.FindTargetSection(s, "kanban board");
        var b = WebPageProbeService.FindTargetSection(s, "kanban board");
        Assert.Equal(a, b);
    }

    // ── mentions ────────────────────────────────────────────────────────────

    [Fact]
    public void PageMentions_MatchesHeadingLinkButtonBody()
    {
        var s = Snapshot(KanbanHtml);
        Assert.True(WebPageProbeService.PageMentions(s, "Task Board"));
        Assert.True(WebPageProbeService.PageMentions(s, "Kanban Board"));
        Assert.True(WebPageProbeService.PageMentions(s, "Save changes"));
        Assert.True(WebPageProbeService.PageMentions(s, "Drag cards"));
        Assert.False(WebPageProbeService.PageMentions(s, "banana split"));
    }

    // ── verification ────────────────────────────────────────────────────────

    [Fact]
    public void Verify_PresentTarget_Passes()
    {
        var s = Snapshot(KanbanHtml);
        var findings = WebPageProbeService.Verify(s, "kanban board");
        Assert.Contains(findings, f => f.Kind == "pass" && f.Message.Contains("matching", StringComparison.OrdinalIgnoreCase) ||
                                       f.Kind == "pass" && f.Message.Contains("present"));
        Assert.DoesNotContain(findings, f => f.Kind == "fail");
    }

    [Fact]
    public void Verify_AbsentTarget_Fails()
    {
        var s = Snapshot(KanbanHtml);
        var findings = WebPageProbeService.Verify(s, "quantum flux capacitor");
        Assert.Contains(findings, f => f.Kind == "fail" && f.Message.Contains("quantum flux capacitor"));
    }

    [Fact]
    public void Verify_ErrorPageText_Fails()
    {
        var s = Snapshot("<html><body><h1>404 Not Found</h1><p>Page not found</p></body></html>");
        var findings = WebPageProbeService.Verify(s, "something");
        Assert.Contains(findings, f => f.Kind == "fail" && f.Message.Contains("error"));
    }

    [Fact]
    public void Verify_EmptyBody_Fails()
    {
        var s = Snapshot("<html><head><title>Empty</title></head><body></body></html>");
        var findings = WebPageProbeService.Verify(s, "");
        Assert.Contains(findings, f => f.Kind == "fail" && f.Message.Contains("no visible text"));
    }

    [Fact]
    public void Verify_EmptyTitle_Warns()
    {
        var s = Snapshot("<html><body><h1>Hi</h1><p>text</p></body></html>");
        var findings = WebPageProbeService.Verify(s, "");
        Assert.Contains(findings, f => f.Kind == "warning" && f.Message.Contains("title"));
    }

    // ── keywords ────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractKeywords_StripsStopWordsAndKeepsPhrase()
    {
        var keywords = WebPageProbeService.ExtractKeywords("the kanban board", null);
        Assert.Contains("the kanban board", keywords); // whole normalized phrase first
        Assert.Contains("kanban", keywords);
        Assert.Contains("board", keywords);
        Assert.DoesNotContain("the", keywords);
    }
}