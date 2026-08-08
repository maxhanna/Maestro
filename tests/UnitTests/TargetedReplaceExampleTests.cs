using System.Reflection;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the canonical TARGETED-REPLACE worked example rendered into the edit-resolver
/// prompt (Prompts.cs — BuildTargetedReplaceWorkedExample). The example shows the SAME
/// small edit in both the oldString/newString format (code files) and the FORMAT D format
/// (HTML files), side by side. The C# escaping is the fragile part — a future edit that
/// mangles a quote or the embedded \n would silently corrupt the JSON shape the model is
/// being taught, so the exact rendered contract is asserted here.
/// </summary>
public class TargetedReplaceExampleTests
{
    private static readonly MethodInfo Method = typeof(AgentController).GetMethod(
        "BuildTargetedReplaceWorkedExample", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string Render() => (string)Method.Invoke(null, null)!;

    [Fact]
    public void Example_ContainsBothFormatLabels()
    {
        var example = Render();
        Assert.Contains("FORMAT 1 — oldString/newString", example);
        Assert.Contains("FORMAT 2 — FORMAT D", example);
        Assert.Contains("WORKED EXAMPLE", example);
    }

    [Fact]
    public void Example_TeachesSingleLineAnchorForCodeFiles()
    {
        var example = Render();
        // oldString must be the one line verbatim; newString swaps the token and adds lines.
        Assert.Contains("oldString: \"<div *ngFor=\\\"let b of benchmarks\\\" class=\\\"benchmark-item\\\">\"", example);
        Assert.Contains("groupedBenchmarks | keyvalue", example);
    }

    [Fact]
    public void Example_TeachesSameAnchorAsTargetNameForHtml()
    {
        var example = Render();
        Assert.Contains("\"targetType\": \"html\"", example);
        Assert.Contains("\"replace\": true", example);
        Assert.Contains("let group of groupedBenchmarks", example);
    }

    [Fact]
    public void Example_StatesBothAnchorOnTheSameLine()
    {
        var example = Render();
        Assert.Contains("anchor on the SAME single unique line", example);
        Assert.Contains("neither re-emits the enclosing section", example);
    }

    [Fact]
    public void Example_Escaping_KeepsQuotesAndNewlinesIntact()
    {
        // The exact JSON-shape escape contract: HTML attribute quotes are escaped \" inside
        // the JSON strings, and the embedded newline in newString is a literal backslash-n.
        // NOTE: the raw file line ('<div *ngFor="let b of benchmarks" class=...>') is shown
        // UNESCAPED once as the "file contains this line" intro — only the FORMAT 1/2 JSON
        // representations must use the escaped \" form, which is asserted here.
        var example = Render();
        // Debug dump if the assertion below fails.
        try
        {
            Assert.Contains("<h3>{{ group.key }}</h3>\\n<div", example);
        }
        catch
        {
            Console.WriteLine("=== RENDERED EXAMPLE ===\n" + example.Replace("\n", "\\n\n"));
            throw;
        }
        // The escaped attribute-quote form appears in the FORMAT 1 oldString/newString lines
        // and the FORMAT 2 targetName/newCode lines.
        Assert.Contains("oldString: \"<div *ngFor=\\\"let b of benchmarks\\\"", example);
        Assert.Contains("targetName\": \"<div *ngFor=\\\"let b of benchmarks\\\"", example);
    }
}
