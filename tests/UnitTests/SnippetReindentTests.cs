using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Regression tests for the oldString/newString apply-path re-indentation
/// (AgentUtilities.ReindentReplacementSnippet).
///
/// BUG (fixed): the apply path used content sniffing (IsHtmlLikeContent) to pick
/// between the HTML tag-depth indenter and the brace-depth indenter. TypeScript
/// generics like `Promise&lt;void&gt;` contain '&lt;void&gt;' which matched the HTML
/// regex, so a .ts edit got routed through AutoIndentHtml — flattening every line
/// of the snippet to the base indent and destroying all nesting. The fix gates
/// the HTML indenter on the file EXTENSION (HtmlDomEditor.IsHtmlDomFile) instead.
/// </summary>
public class SnippetReindentTests
{
    /// <summary>The exact globe.component.ts scenario from the bug report.</summary>
    private static readonly string[] GlobeFile = new[]
    {
        "import { Component, OnInit } from '@angular/core';",
        "",
        "@Component({",
        "  selector: 'app-globe',",
        "  template: `<div></div>`",
        "})",
        "export class GlobeComponent implements OnInit {",
        "  usersWithLocations: any[] = [];",
        "",
        "  async ngOnInit(): Promise<void> {",
        "    await this.loadUsersWithLocations();",
        "    this.loadStories();",
        "    this.loadNewsPins();",
        "    this.loadFlights();",
        "    this.loadAllFlights();",
        "    this.filterCoordinates();",
        "    if (this.userId) {",
        "      const userWithLocation = this.usersWithLocations.find(u => u.user.id === this.userId);",
        "      console.log(\"Rotating to user location:\", userWithLocation);",
        "      if (userWithLocation && userWithLocation.city && userWithLocation.country) {",
        "        const coords = this.lookupCityCoords(userWithLocation.city, userWithLocation.country);",
        "        if (coords) {",
        "          console.log(\"Found user coords:\", coords);",
        "          this.rotateToLocation(coords[0], coords[1]);",
        "        }",
        "      }",
        "    }",
        "  }",
        "}"
    };

    /// <summary>
    /// The core regression: a .ts file whose method signature contains a generic
    /// (`Promise&lt;void&gt;`). The old content sniffing misdetected it as HTML and
    /// flattened the whole snippet to the base indent. Now the brace-depth
    /// indenter must run instead, preserving correct 2/4/6/8 nesting.
    /// </summary>
    [Fact]
    public void TsSnippet_WithPromiseVoidGeneric_IsNotFlattened_KeepsNesting()
    {
        // The plan-supplied newString (LLM output) — note it is already correctly
        // indented relative to the old block; the helper must preserve nesting.
        var newLines = new[]
        {
            "  async ngOnInit(): Promise<void> {",
            "    await this.loadUsersWithLocations();",
            "    this.loadStories();",
            "    this.loadNewsPins();",
            "    this.loadFlights();",
            "    this.loadAllFlights();",
            "    this.filterCoordinates();",
            "    if (this.userId) {",
            "      this.centerCurrentLocation();",
            "    }",
            "  }",
            "",
            "  private centerCurrentLocation() {",
            "    const userWithLocation = this.usersWithLocations.find(u => u.user.id === this.userId);",
            "    console.log(\"Rotating to user location:\", userWithLocation);",
            "    if (userWithLocation && userWithLocation.city && userWithLocation.country) {",
            "      const coords = this.lookupCityCoords(userWithLocation.city, userWithLocation.country);",
            "      if (coords) {",
            "        console.log(\"Found user coords:\", coords);",
            "        this.rotateToLocation(coords[0], coords[1]);",
            "      }",
            "    }",
            "  }"
        };

        // The old block being replaced: the original ngOnInit (matched from file).
        var oldStart = Array.IndexOf(GlobeFile, "  async ngOnInit(): Promise<void> {");
        var oldEnd = Array.IndexOf(GlobeFile, "  }") + 1; // closing brace of ngOnInit
        var oldLines = GlobeFile.Skip(oldStart).Take(oldEnd - oldStart).ToList();

        var result = AgentUtilities.ReindentReplacementSnippet(
            newLines.ToList(), oldLines,
            GlobeFile.ToList(), oldStart,
            isHtmlDomFile: false);

        Assert.Equal(newLines, result.ToArray());
        // The tell-tale generic survives untouched and is not what triggers flattening.
        Assert.Contains(result, l => l.Contains("Promise<void>"));
        // Nesting is preserved: method at 2, body at 4, nested if at 6, deepest at 8.
        Assert.Equal("    await this.loadUsersWithLocations();", result[1]);
        Assert.Equal("      this.centerCurrentLocation();", result[8]);
        Assert.Equal("      const coords = this.lookupCityCoords(userWithLocation.city, userWithLocation.country);", result[16]);
    }

    /// <summary>
    /// The apply path must format ONLY the inserted snippet: the surrounding file
    /// lines (before and after the matched block) must be byte-identical after
    /// the RemoveRange + InsertRange that the apply step performs.
    /// </summary>
    [Fact]
    public void ApplyPath_FormatsOnlyInsertedSnippet_SurroundingLinesByteIdentical()
    {
        var newLines = new[]
        {
            "  async ngOnInit(): Promise<void> {",
            "    await this.loadUsersWithLocations();",
            "    this.loadStories();",
            "    this.loadNewsPins();",
            "    this.loadFlights();",
            "    this.loadAllFlights();",
            "    this.filterCoordinates();",
            "    if (this.userId) {",
            "      this.centerCurrentLocation();",
            "    }",
            "  }",
            "",
            "  private centerCurrentLocation() {",
            "    const userWithLocation = this.usersWithLocations.find(u => u.user.id === this.userId);",
            "    console.log(\"Rotating to user location:\", userWithLocation);",
            "    if (userWithLocation && userWithLocation.city && userWithLocation.country) {",
            "      const coords = this.lookupCityCoords(userWithLocation.city, userWithLocation.country);",
            "      if (coords) {",
            "        console.log(\"Found user coords:\", coords);",
            "        this.rotateToLocation(coords[0], coords[1]);",
            "      }",
            "    }",
            "  }"
        };

        var oldStart = Array.IndexOf(GlobeFile, "  async ngOnInit(): Promise<void> {");
        var oldEnd = Array.IndexOf(GlobeFile, "  }") + 1;
        var oldLines = GlobeFile.Skip(oldStart).Take(oldEnd - oldStart).ToList();

        var fileLines = GlobeFile.ToList();
        var reindented = AgentUtilities.ReindentReplacementSnippet(
            newLines.ToList(), oldLines, fileLines, oldStart, isHtmlDomFile: false);

        var prefix = fileLines.Take(oldStart).ToList();
        var suffix = fileLines.Skip(oldEnd).ToList();
        fileLines.RemoveRange(oldStart, oldEnd - oldStart);
        fileLines.InsertRange(oldStart, reindented);

        // Prefix and suffix byte-identical — nothing outside the snippet changed.
        Assert.Equal(GlobeFile.Take(oldStart), fileLines.Take(oldStart));
        Assert.Equal(GlobeFile.Skip(oldEnd), fileLines.Skip(oldStart + reindented.Count));
        // The full file now contains the new method exactly once.
        Assert.Contains("private centerCurrentLocation()", string.Join("\n", fileLines));
        // The old ngOnInit body no longer contains the inline centering logic —
        // it was extracted into the new private method.
        var newNgOnInit = string.Join("\n", fileLines.Skip(oldStart).Take(11));
        Assert.Contains("this.centerCurrentLocation();", newNgOnInit);
        Assert.DoesNotContain("usersWithLocations.find", newNgOnInit);
        Assert.DoesNotContain("rotateToLocation", newNgOnInit);
        // The moved logic now lives inside centerCurrentLocation().
        var newMethod = string.Join("\n", fileLines.Skip(oldStart));
        Assert.Contains("rotateToLocation(coords[0], coords[1]);", newMethod);
    }

    /// <summary>
    /// HTML files still route through the tag-depth indenter (extension gate is
    /// permissive for real HTML), so an under-indented HTML snippet gets proper
    /// 2-space tag nesting.
    /// </summary>
    [Fact]
    public void HtmlFile_StillRoutedThroughTagDepthIndenter()
    {
        var fileLines = new[]
        {
            "<div class=\"panel\">",
            "  <h3>Title</h3>",
            "  <div class=\"content\">",
            "    <button>Go</button>",
            "  </div>",
            "</div>"
        };
        // A flat (LLM-style) insert: no indentation at all.
        var newLines = new[]
        {
            "<div class=\"card\">",
            "<h4>New section</h4>",
            "<span>Text</span>",
            "</div>"
        };

        var result = AgentUtilities.ReindentReplacementSnippet(
            newLines.ToList(), new List<string> { "<div class=\"content\">" },
            fileLines.ToList(), 2, isHtmlDomFile: true);

        var joined = string.Join("\n", result);
        Assert.Contains("<div class=\"card\">", joined);
        Assert.Contains("  <h4>New section</h4>", joined);
        Assert.Contains("  <span>Text</span>", joined);
        Assert.Contains("</div>", joined);
    }

    /// <summary>
    /// An already-correctly-indented snippet passes through untouched (the helper
    /// is a no-op when nothing needs fixing) — it must never over-format.
    /// </summary>
    [Fact]
    public void AlreadyIndentedSnippet_PassesThroughUnchanged()
    {
        var newLines = new[]
        {
            "  newMethod(): void {",
            "    if (this.flag) {",
            "      this.doSomething();",
            "    }",
            "  }"
        };
        var fileLines = new[]
        {
            "class Foo {",
            "  existingMethod(): void {",
            "    this.bar();",
            "  }",
            "}"
        };
        var result = AgentUtilities.ReindentReplacementSnippet(
            newLines.ToList(),
            new List<string> { "  existingMethod(): void {", "    this.bar();", "  }" },
            fileLines.ToList(), 1, isHtmlDomFile: false);

        Assert.Equal(newLines, result.ToArray());
    }
}
