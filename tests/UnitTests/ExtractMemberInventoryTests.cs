using System.Reflection;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the ExtractMemberInventory helper — the corrective-feedback feed for the
/// first-step hallucinated-removal guard. When the planner tries to delete a symbol
/// that isn't in the file, the guard rejects the step and names the file's real
/// members so the model can re-ground. These tests pin the extraction: real methods
/// are found, control-flow keywords are never reported, and empty input yields "".
/// </summary>
public class ExtractMemberInventoryTests
{
    private static string ExtractMemberInventory(string? content)
    {
        var method = typeof(AgentController).GetMethod(
            "ExtractMemberInventory", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { content })!;
    }

    private static (AgentUtilities.PreEditVerdict verdict, string reason) InvokePreEditValidation(string fileContent, PlanStep step)
    {
        var method = typeof(AgentController).GetMethod(
            "PreEditValidation", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (ValueTuple<AgentUtilities.PreEditVerdict, string>)(method!.Invoke(null, new object?[] { fileContent, step })!);
    }

    [Fact]
    public void Lists_RealTsComponentMembers_ExcludingControlFlowKeywords()
    {
        var content =
            "import { Component } from '@angular/core';\n" +
            "export class UserEventsComponent {\n" +
            "  private getEventIcon(eventType: string): string { return '📁'; }\n" +
            "  getEventDescription(eventType: string): string { return 'File Uploads'; }\n" +
            "  ngOnInit(): void { this.loadEvents(); }\n" +
            "  async loadEvents() {\n" +
            "    for (const e of this.eventTypes) {\n" +
            "      if (e) { continue; }\n" +
            "      this.eventTypeDescriptions[e] = this.getEventDescription(e);\n" +
            "    }\n" +
            "  }\n" +
            "}\n";
        var inv = ExtractMemberInventory(content);
        // Every real member shows up…
        foreach (var name in new[] { "getEventIcon", "getEventDescription", "ngOnInit", "loadEvents" })
            Assert.Contains(name, inv);
        // …and control-flow words never do.
        foreach (var word in new[] { "for", "if", "return", "continue", "async" })
            Assert.DoesNotContain(word, inv);
    }

    [Fact]
    public void Lists_CSharpMembers_AcrossVisibilityModifiers()
    {
        var content =
            "public class EmailService {\n" +
            "  public async Task SendAsync(string to) { }\n" +
            "  private bool ValidateAddress(string to) { return true; }\n" +
            "  protected void Log(string msg) { }\n" +
            "  internal int Count() { return 0; }\n" +
            "  public EmailService() { }\n" +
            "}\n";
        var inv = ExtractMemberInventory(content);
        Assert.Contains("SendAsync", inv);
        Assert.Contains("ValidateAddress", inv);
        Assert.Contains("Log", inv);
        Assert.Contains("Count", inv);
        Assert.DoesNotContain("public", inv);
        Assert.DoesNotContain("bool", inv);
        Assert.DoesNotContain("return", inv);
    }

    [Fact]
    public void EmptyOrNull_Content_ReturnsEmpty()
    {
        Assert.Equal("", ExtractMemberInventory(""));
        Assert.Equal("", ExtractMemberInventory(null));
        Assert.Equal("", ExtractMemberInventory("   \n\n  "));
    }

    [Fact]
    public void HtmlWithoutCallableMembers_ReturnsEmpty()
    {
        Assert.Equal("", ExtractMemberInventory("<div class=\"wrap\"><p>hello</p></div>"));
    }

    [Fact]
    public void CapsAtTwentyFour_Entries()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 40; i++)
            sb.AppendLine($"  method{i}() {{ }}");
        var inv = ExtractMemberInventory(sb.ToString());
        Assert.False(string.IsNullOrWhiteSpace(inv));
        var memberCount = inv.Split(", ").Length;
        Assert.True(memberCount <= 24, $"expected <=24 members, got {memberCount}");
        // First names survive, overflowing ones are dropped.
        Assert.Contains("method0", inv);
        Assert.DoesNotContain("method30", inv);
    }

    [Fact]
    public void PreEditValidation_RemovalOfAbsentTarget_YieldsTheGuardMatchedReason()
    {
        // Locks the guard's reason-string coupling: the hallucinated-removal guard matches
        // on the exact "already absent from file" reason, so if anyone rewords it in
        // PreEditValidation this test fails and the guard's Contains() check must be
        // revisited. Reproduces the real-world scenario — a plan step trying to delete a
        // method (getEventData) that does not exist in the file.
        var content =
            "export class UserEventsComponent {\n" +
            "  getEventIcon(eventType: string): string { return '📁'; }\n" +
            "  getEventDescription(eventType: string): string { return 'File Uploads'; }\n" +
            "}\n";
        var step = new PlanStep
        {
            File = "src/user-events.component.ts",
            Change = "Remove broken getEventData method definition",
            OldString =
                "private getEventData(eventType: string): { icon: string; description: string } {\n" +
                " const icon = this.getEventIcon(eventType);\n" +
                " return { icon, description };\n" +
                "}\n",
            NewString = ""
        };
        var (verdict, reason) = InvokePreEditValidation(content, step);
        Assert.Equal(AgentUtilities.PreEditVerdict.AlreadyDone, verdict);
        Assert.Contains("already absent from file", reason);
    }

    [Fact]
    public void RealisticComponent_WouldHaveNamedTheHelpersTheModelInvented()
    {
        // The scenario that triggered the guard: the model claimed getEventData was a
        // 'broken method calling non-existent helpers' and tried to delete it — but the
        // file has no getEventData at all. The inventory must surface the ACTUAL helpers
        // (getEventIcon, getEventDescription) so the corrective feedback re-grounds.
        var content =
            "import { Component } from '@angular/core';\n" +
            "@Component({ selector: 'app-user-events', templateUrl: './user-events.component.html' })\n" +
            "export class UserEventsComponent {\n" +
            "  eventTypes: string[] = ['file_upload', 'comment'];\n" +
            "  private getEventIcon(eventType: string): string {\n" +
            "    switch (eventType.toLowerCase()) {\n" +
            "      case 'file_upload': return '📁';\n" +
            "      default: return '📌';\n" +
            "    }\n" +
            "  }\n" +
            "  getEventDescription(eventType: string): string {\n" +
            "    return 'File Uploads';\n" +
            "  }\n" +
            "  ngOnInit(): void { }\n" +
            "}\n";
        var inv = ExtractMemberInventory(content);
        Assert.Contains("getEventIcon", inv);
        Assert.Contains("getEventDescription", inv);
        Assert.DoesNotContain("getEventData", inv); // the hallucinated symbol is absent
    }
}
