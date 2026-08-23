using System.Reflection;
using System.Text.Json;
using Weaver.Controllers;
using Xunit;

namespace Weaver.UnitTests;

public class DuplicateStepAdjudicationTests
{
    [Fact]
    public void ReplacementPayload_ProducesExecutablePlanStep()
    {
        using var document = JsonDocument.Parse("""
            {
              "file": "maxhanna.client/src/app/user-events/user-events.component.ts",
              "change": "Add downloaded_painting handler to viewEvent",
              "targetSymbol": "viewEvent",
              "oldString": "else if (e.eventType === 'save_note') {\n  this.parentRef?.createComponent('Notes', { 'noteId': e.referenceId });\n}",
              "newString": "else if (e.eventType === 'save_note') {\n  this.parentRef?.createComponent('Notes', { 'noteId': e.referenceId });\n}\nelse if (e.eventType === 'downloaded_painting') {\n  this.parentRef?.createComponent('Paint', { 'paintingId': e.referenceId });\n}"
            }
            """);

        var step = Parse(document.RootElement);

        Assert.NotNull(step);
        Assert.Equal("maxhanna.client/src/app/user-events/user-events.component.ts", step!.File);
        Assert.Equal("Add downloaded_painting handler to viewEvent", step.Change);
        Assert.Equal("viewEvent", step.TargetSymbol);
        Assert.Contains("downloaded_painting", step.NewString);
        Assert.Contains("Paint", step.NewString);
    }

    [Fact]
    public void ReplacementPayload_WithoutFileOrChange_FailsClosed()
    {
        using var document = JsonDocument.Parse("""
            {
              "file": "user-events.component.ts",
              "oldString": "old",
              "newString": "new"
            }
            """);

        Assert.Null(Parse(document.RootElement));
    }

    private static PlanStep? Parse(JsonElement step)
    {
        var method = typeof(AgentController).GetMethod(
            "ParseAdjudicatedStep", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, new object[] { step }) as PlanStep;
    }
}
