using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks AgentProjectUtilities.IsAngularTemplate — the gate that decides whether the
/// "Angular template uses Math.min(...) — move this logic to the component's .ts file"
/// verification guard applies. The live navigation.component.ts movie-count run was
/// blocked by a false positive: the .ts component contains {{ }} interpolation markers
/// in template literals AND legitimate Math.min calls in its logic, so the old content-
/// only heuristic classified it as an Angular template and rejected the edit. With a
/// known file path, ONLY an actual .html template file (navigation.component.html, the
/// root index.html, …) is a template; a .ts file is never one.
/// </summary>
public class AngularTemplateGuardTests
{
    private const string HtmlTemplateContent =
        "<div *ngIf=\"items.length\">" +
        "<button (click)=\"go()\">{{ label }}</button></div>";

    private const string TsComponentContent =
        "export class NavigationComponent {\n" +
        "  private shortenCount(n: number): string { return Math.min(n, 99).toString(); }\n" +
        "  nav = { content: `{{ count }} files` };\n" +
        "}\n";

    [Fact]
    public void HtmlFile_IsAngularTemplate()
    {
        Assert.True(AgentProjectUtilities.IsAngularTemplate(HtmlTemplateContent, "src/app/navigation/navigation.component.html"));
        Assert.True(AgentProjectUtilities.IsAngularTemplate(HtmlTemplateContent, "wwwroot/index.html"));
        Assert.True(AgentProjectUtilities.IsAngularTemplate(HtmlTemplateContent, "src/app/foo.component.html"));
    }

    [Fact]
    public void TsComponentFile_IsNeverAngularTemplate_EvenWithMarkers()
    {
        // The regression from the live run: a .ts component whose content contains {{ }}
        // markers (template literals) and Math.min (logic) must NOT be classified as an
        // Angular template — otherwise the banned-math guard blocks legal .ts edits.
        Assert.False(AgentProjectUtilities.IsAngularTemplate(TsComponentContent, "src/app/navigation/navigation.component.ts"));
        Assert.False(AgentProjectUtilities.IsAngularTemplate(TsComponentContent, "navigation.component.ts"));
    }

    [Fact]
    public void PathWins_OverMisleadingContent_ForNonHtmlFiles()
    {
        // Even content that STRONGLY looks like a template (ng directives, click bindings,
        // interpolation) is not a template when the path says it isn't an .html file.
        Assert.False(AgentProjectUtilities.IsAngularTemplate(HtmlTemplateContent, "src/app/foo.component.ts"));
        Assert.False(AgentProjectUtilities.IsAngularTemplate(HtmlTemplateContent, "src/app/foo.ts"));
        Assert.False(AgentProjectUtilities.IsAngularTemplate(HtmlTemplateContent, "src/app/foo.js"));
    }

    [Fact]
    public void UnknownPath_FallsBackToContentHeuristic()
    {
        // No path known (legacy callers) — the content heuristic still applies.
        Assert.True(AgentProjectUtilities.IsAngularTemplate(HtmlTemplateContent));
        Assert.True(AgentProjectUtilities.IsAngularTemplate("{{ a }} + {{ b }} + {{ c }}"));
        Assert.False(AgentProjectUtilities.IsAngularTemplate("const x = Math.min(1, 2);"));
        Assert.False(AgentProjectUtilities.IsAngularTemplate(""));
        Assert.False(AgentProjectUtilities.IsAngularTemplate(null!));
    }

    [Fact]
    public void ComponentHtmlFile_IsTemplate()
    {
        // Per the explicit rule: files that contain component.html in the name are templates.
        Assert.True(AgentProjectUtilities.IsAngularTemplate(HtmlTemplateContent, "src/app/navigation/navigation.component.html"));
        Assert.True(AgentProjectUtilities.IsAngularTemplate(HtmlTemplateContent, "navigation.component.html"));
    }
}
