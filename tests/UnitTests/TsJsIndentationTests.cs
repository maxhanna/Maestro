using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Verifies that .ts/.js files get correctly re-indented after an agent edit.
/// Exercises the SAME chain the apply path uses for oldString/newString edits:
/// AutoFixOperatorSpacing → ReindentReplacementSnippet (brace-depth indenter) →
/// RemoveRange/InsertRange. Covers flat LLM output, strings/template literals
/// containing braces (which must NOT shift brace depth), line comments, CRLF
/// files, and method insertion.
/// </summary>
public class TsJsIndentationTests
{
    /// <summary>
    /// Simulates the apply path exactly: normalize line endings, run
    /// AutoFixOperatorSpacing (as the .ts/.js apply path does), re-indent the
    /// snippet to the matched block's base indent, and splice it into the file.
    /// Returns the final file content.
    /// </summary>
    private static string ApplyTsJsEdit(
        string[] fileLines, int matchIdx, string[] oldBlock, string[] newBlock, string ext = ".ts")
    {
        var fileLinesArr = fileLines.ToList();
        var oldLinesArr = oldBlock.ToList();
        // AutoFixOperatorSpacing is applied to newStr for .ts/.tsx/.js/.jsx files
        // before the block matcher runs in the real apply path.
        var normNew = ext is ".ts" or ".tsx" or ".js" or ".jsx"
            ? AgentCodeFormatting.AutoFixOperatorSpacing(string.Join("\n", newBlock))
            : string.Join("\n", newBlock);
        var newLinesArr = normNew.Split('\n').ToList();
        var reindented = AgentEditHeuristics.ReindentReplacementSnippet(
            newLinesArr, oldLinesArr, fileLinesArr, matchIdx, isHtmlDomFile: false);
        fileLinesArr.RemoveRange(matchIdx, oldLinesArr.Count);
        fileLinesArr.InsertRange(matchIdx, reindented);
        return string.Join("\n", fileLinesArr);
    }

    /// <summary>
    /// Flat LLM output (every line at column 0) for a method inside a class body
    /// must be re-indented to the file's 2-space nesting — the core guarantee.
    /// </summary>
    [Theory]
    [InlineData(".ts")]
    [InlineData(".js")]
    public void FlatLlmOutput_MethodInClass_ReindentedToFileNesting(string ext)
    {
        var file = new[]
        {
            "export class Foo {",
            "  existingMethod(): void {",
            "    this.bar();",
            "  }",
            "}"
        };
        // LLM emits the new method with ZERO indentation.
        var newBlock = new[]
        {
            "newMethod(): void {",
            "if (this.flag) {",
            "this.doSomething();",
            "}",
            "}"
        };
        var result = ApplyTsJsEdit(file, 1,
            new[] { "  existingMethod(): void {", "    this.bar();", "  }" },
            newBlock, ext);

        var lines = result.Split('\n');
        Assert.Equal("  newMethod(): void {", lines[1]);
        Assert.Equal("    if (this.flag) {", lines[2]);
        Assert.Equal("      this.doSomething();", lines[3]);
        Assert.Equal("    }", lines[4]);
        Assert.Equal("  }", lines[5]);
        Assert.Equal("}", lines[6]);
    }

    /// <summary>
    /// Direct FORMAT C/D replacements use a raw string splice instead of the normal
    /// line-based oldString/newString matcher. The replacement must still be rebuilt
    /// from braces before that splice, otherwise a flat payload flattens the method.
    /// </summary>
    [Fact]
    public void DirectReplacement_FlatViewEventChain_ReindentsBeforeSplice()
    {
        var file = new[]
        {
            "export class UserEvents {",
            "  viewEvent(e: UserEvent) {",
            "    if (e.referenceId == null) return;",
            "  }",
            "}"
        };
        var oldBlock = new[]
        {
            "  viewEvent(e: UserEvent) {",
            "    if (e.referenceId == null) return;",
            "  }"
        };
        var flatReplacement = string.Join("\n", new[]
        {
            "viewEvent(e: UserEvent) {",
            "if (e.referenceId == null) return;",
            "if (e.eventType.includes('digcraft')) {",
            "this.parentRef?.createComponent('DigCraft');",
            "}",
            "if (e.eventType.toLowerCase().includes('grandtheft')) {",
            "this.parentRef?.createComponent('GrandTheft');",
            "}",
            "else if (e.eventType.includes('save_note')) {",
            "this.parentRef?.createComponent('Notepad', { 'noteId': e.referenceId });",
            "}",
            "}"
        });

        var reindented = AgentCodeFormatting.AutoIndentFromFile(
            flatReplacement, "  ", oldBlock, 0);
        var lines = reindented.Split('\n');

        Assert.Equal("  viewEvent(e: UserEvent) {", lines[0]);
        Assert.Equal("    if (e.referenceId == null) return;", lines[1]);
        Assert.Equal("    if (e.eventType.includes('digcraft')) {", lines[2]);
        Assert.Equal("      this.parentRef?.createComponent('DigCraft');", lines[3]);
        Assert.Equal("    }", lines[4]);
        Assert.Equal("    if (e.eventType.toLowerCase().includes('grandtheft')) {", lines[5]);
        Assert.Equal("      this.parentRef?.createComponent('GrandTheft');", lines[6]);
        Assert.Equal("    }", lines[7]);
        Assert.Equal("      this.parentRef?.createComponent('Notepad', { 'noteId': e.referenceId });", lines[9]);
        Assert.Equal("  }", lines[11]);
    }

    /// <summary>
    /// A method inserted AFTER an existing method (insertAfter semantics): the new
    /// method must land at the same 2-space class-member level, not nested inside
    /// the previous method.
    /// </summary>
    [Fact]
    public void InsertAfter_MethodLandsAtClassMemberLevel()
    {
        var file = new[]
        {
            "export class Foo {",
            "  first(): void {",
            "    this.a();",
            "  }",
            "}"
        };
        var newBlock = new[]
        {
            "second(): void {",
            "const x = 1;",
            "}"
        };
        var result = ApplyTsJsEdit(file, 3, new[] { "  }" }, newBlock);

        var lines = result.Split('\n');
        Assert.Equal("  second(): void {", lines[3]);
        Assert.Equal("    const x = 1;", lines[4]);
        Assert.Equal("  }", lines[5]);
    }

    /// <summary>
    /// Braces inside string literals must NOT shift brace depth: `const s = "}";`
    /// is a statement at body level, so the next line stays at 4-space body level,
    /// not de-indented.
    /// </summary>
    [Fact]
    public void StringContainingBrace_DoesNotAffectDepth()
    {
        var file = new[]
        {
            "export class Foo {",
            "  render(): string {",
            "    const before = 'ok';",
            "    return before;",
            "  }",
            "}"
        };
        var newBlock = new[]
        {
            "render(): string {",
            "const s = \"}\";",
            "const t = '{';",
            "return s + t;",
            "}"
        };
        var result = ApplyTsJsEdit(file, 1,
            new[] { "  render(): string {", "    const before = 'ok';", "    return before;", "  }" },
            newBlock);

        var lines = result.Split('\n');
        Assert.Equal("  render(): string {", lines[1]);
        Assert.Equal("    const s = \"}\";", lines[2]);
        Assert.Equal("    const t = '{';", lines[3]);
        Assert.Equal("    return s + t;", lines[4]);
        Assert.Equal("  }", lines[5]);
    }

    /// <summary>
    /// Template literals containing braces (common in Angular/JS: HTML templates,
    /// object interpolation) must be preserved verbatim and must not alter brace
    /// depth for the lines around them.
    /// </summary>
    [Fact]
    public void TemplateLiteralWithBraces_PreservedAndDepthUnaffected()
    {
        var file = new[]
        {
            "export class Foo {",
            "  buildHtml(): string {",
            "    return '';",
            "  }",
            "}"
        };
        var newBlock = new[]
        {
            "buildHtml(): string {",
            "const html = `<div class=\"x\">",
            "${this.name} {",
            "</div>`;",
            "return html;",
            "}"
        };
        var result = ApplyTsJsEdit(file, 1,
            new[] { "  buildHtml(): string {", "    return '';", "  }" },
            newBlock);

        var lines = result.Split('\n');
        // Template literal content lines keep the base-indent prefix from the
        // relative pass (the LLM emitted them flat) but are otherwise untouched.
        Assert.Equal("    const html = `<div class=\"x\">", lines[2]);
        Assert.Contains("${this.name} {", lines[3]);
        Assert.Contains("</div>`;", lines[4]);
        // The braces INSIDE the template (${this.name} and the literal {) did NOT
        // bump depth: return is at 4, close at 2.
        Assert.Equal("    return html;", lines[5]);
        Assert.Equal("  }", lines[6]);
    }

    /// <summary>
    /// Braces inside line comments must not affect depth either.
    /// </summary>
    [Fact]
    public void LineCommentWithBrace_DoesNotAffectDepth()
    {
        var file = new[]
        {
            "export class Foo {",
            "  doIt(): void {",
            "    this.a();",
            "  }",
            "}"
        };
        var newBlock = new[]
        {
            "doIt(): void {",
            "// closing } of previous block",
            "this.a();",
            "}"
        };
        var result = ApplyTsJsEdit(file, 1,
            new[] { "  doIt(): void {", "    this.a();", "  }" },
            newBlock);

        var lines = result.Split('\n');
        Assert.Equal("    // closing } of previous block", lines[2]);
        Assert.Equal("    this.a();", lines[3]);
        Assert.Equal("  }", lines[4]);
    }

    /// <summary>
    /// CRLF files: the apply path normalizes line endings before matching; the
    /// re-indented result must use LF consistently (the write path normalizes on
    /// disk), and nesting must be correct.
    /// </summary>
    [Fact]
    public void CrlfFile_ReindentedCorrectly()
    {
        var file = new[]
        {
            "export class Foo {",
            "  existing(): void {",
            "    this.bar();",
            "  }",
            "}"
        };
        // Simulate a CRLF file read as raw text.
        var crlfContent = string.Join("\r\n", file);
        var fileLines = crlfContent.Replace("\r\n", "\n").Split('\n');
        var newBlock = new[]
        {
            "newMethod(): void {",
            "const x = 1;",
            "}"
        };
        var result = ApplyTsJsEdit(fileLines, 1,
            new[] { "  existing(): void {", "    this.bar();", "  }" },
            newBlock);

        Assert.DoesNotContain("\r", result);
        var lines = result.Split('\n');
        Assert.Equal("  newMethod(): void {", lines[1]);
        Assert.Equal("    const x = 1;", lines[2]);
        Assert.Equal("  }", lines[3]);
    }

    /// <summary>
    /// Already-correctly-indented LLM output passes through unchanged (the helper
    /// must never over-format or introduce drift).
    /// </summary>
    [Fact]
    public void AlreadyIndented_PassesThroughUnchanged()
    {
        var file = new[]
        {
            "export class Foo {",
            "  existing(): void {",
            "    this.bar();",
            "  }",
            "}"
        };
        var newBlock = new[]
        {
            "  newMethod(): void {",
            "    if (this.flag) {",
            "      this.doSomething();",
            "    }",
            "  }"
        };
        var result = ApplyTsJsEdit(file, 1,
            new[] { "  existing(): void {", "    this.bar();", "  }" },
            newBlock);

        var lines = result.Split('\n');
        Assert.Equal(new[] { "  newMethod(): void {", "    if (this.flag) {", "      this.doSomething();", "    }", "  }" },
            lines.Skip(1).Take(5));
    }

    /// <summary>
    /// A generic signature (`Promise&lt;void&gt;`) in a .js file too must not be
    /// misdetected as HTML and flattened.
    /// </summary>
    [Fact]
    public void JsGenericLikeSignature_NotFlattened()
    {
        var file = new[]
        {
            "class Api {",
            "  fetchData() {",
            "    return Promise.resolve(null);",
            "  }",
            "}"
        };
        var newBlock = new[]
        {
            "async load(): Promise<void> {",
            "const x = await this.fetchData();",
            "console.log(x);",
            "}"
        };
        var result = ApplyTsJsEdit(file, 1,
            new[] { "  fetchData() {", "    return Promise.resolve(null);", "  }" },
            newBlock, ".js");

        var lines = result.Split('\n');
        Assert.Equal("  async load(): Promise<void> {", lines[1]);
        Assert.Equal("    const x = await this.fetchData();", lines[2]);
        Assert.Equal("    console.log(x);", lines[3]);
        Assert.Equal("  }", lines[4]);
    }

    /// <summary>
    /// Braces inside a BLOCK COMMENT that spans multiple lines must not be
    /// counted as code on any of the continuation lines.
    /// </summary>
    [Fact]
    public void BlockCommentSpanningLines_BracesNotCounted()
    {
        var file = new[]
        {
            "export class Foo {",
            "  doIt(): void {",
            "    this.a();",
            "  }",
            "}"
        };
        var newBlock = new[]
        {
            "doIt(): void {",
            "/*",
            "comment with { and } on continuation lines",
            "*/",
            "this.a();",
            "}"
        };
        var result = ApplyTsJsEdit(file, 1,
            new[] { "  doIt(): void {", "    this.a();", "  }" },
            newBlock);

        var lines = result.Split('\n');
        Assert.Equal("    /*", lines[2]);
        Assert.Equal("    comment with { and } on continuation lines", lines[3]);
        Assert.Equal("    */", lines[4]);
        // The braces inside the comment did NOT bump depth.
        Assert.Equal("    this.a();", lines[5]);
        Assert.Equal("  }", lines[6]);
    }

    /// <summary>
    /// A template literal that opens and closes on the SAME line
    /// (`const x = \`a${b}c\`;`) — the ${b} braces must not shift depth.
    /// </summary>
    [Fact]
    public void SameLineTemplateOpenClose_BracesNotCounted()
    {
        var file = new[]
        {
            "export class Foo {",
            "  build(): string {",
            "    return '';",
            "  }",
            "}"
        };
        var newBlock = new[]
        {
            "build(): string {",
            "const x = `a${b}c`;",
            "return x;",
            "}"
        };
        var result = ApplyTsJsEdit(file, 1,
            new[] { "  build(): string {", "    return '';", "  }" },
            newBlock);

        var lines = result.Split('\n');
        Assert.Equal("    const x = `a${b}c`;", lines[2]);
        Assert.Equal("    return x;", lines[3]);
        Assert.Equal("  }", lines[4]);
    }

    /// <summary>
    /// An escaped backtick inside a template literal (`` \` ``) must not be
    /// treated as the closing backtick of the template.
    /// </summary>
    [Fact]
    public void EscapedBacktickInsideTemplate_NotTreatedAsClosing()
    {
        var file = new[]
        {
            "export class Foo {",
            "  render(): string {",
            "    return '';",
            "  }",
            "}"
        };
        var newBlock = new[]
        {
            "render(): string {",
            "const msg = `escaped \\` backtick here`;",
            "return msg;",
            "}"
        };
        var result = ApplyTsJsEdit(file, 1,
            new[] { "  render(): string {", "    return '';", "  }" },
            newBlock);

        var lines = result.Split('\n');
        Assert.Equal("    const msg = `escaped \\` backtick here`;", lines[2]);
        Assert.Equal("    return msg;", lines[3]);
        Assert.Equal("  }", lines[4]);
    }

    /// <summary>
    /// Deeply nested control flow (method → if → for → arrow) must re-indent at
    /// every level from flat input.
    /// </summary>
    [Fact]
    public void DeeplyNestedControlFlow_ReindentedAtEveryLevel()
    {
        var file = new[]
        {
            "export class Foo {",
            "  process(): void {",
            "    this.ready();",
            "  }",
            "}"
        };
        var newBlock = new[]
        {
            "process(): void {",
            "if (this.items) {",
            "for (const item of this.items) {",
            "items.forEach(i => {",
            "this.handle(i);",
            "});",
            "}",
            "}",
            "}"
        };
        var result = ApplyTsJsEdit(file, 1,
            new[] { "  process(): void {", "    this.ready();", "  }" },
            newBlock);

        var lines = result.Split('\n');
        Assert.Equal("  process(): void {", lines[1]);
        Assert.Equal("    if (this.items) {", lines[2]);
        Assert.Equal("      for (const item of this.items) {", lines[3]);
        Assert.Equal("        items.forEach(i => {", lines[4]);
        Assert.Equal("          this.handle(i);", lines[5]);
        Assert.Equal("        });", lines[6]);
        Assert.Equal("      }", lines[7]);
        Assert.Equal("    }", lines[8]);
        Assert.Equal("  }", lines[9]);
    }
}
