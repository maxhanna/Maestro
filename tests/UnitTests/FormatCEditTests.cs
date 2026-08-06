using System.Text;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Regression tests for the FORMAT C (insertAfter) edit flow on .ts/.js files.
/// Covers the CRLF normalization bug: the AST resolver returns LF-normalized
/// method blocks while the raw file is CRLF, and the text-fallback prefix used
/// to end with a lone '\r' that made the apply-time IndexOf fail with
/// "oldString not found verbatim" on every retry.
/// </summary>
public class FormatCEditTests : IDisposable
{
    private readonly string _dir;

    public FormatCEditTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "weaver_formatc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WriteFile(string ext, bool crlf)
    {
        var lines = new[]
        {
            "import { Component } from '@angular/core';",
            "",
            "@Component({",
            "  selector: 'app-recipe',",
            "  template: `<div></div>`",
            "})",
            "export class RecipeComponent {",
            "  expandedRecipeIds: string[] = [];",
            "",
            "  constructor() {",
            "    this.toggleRecipeDetails(this.expandedRecipeIds[0]);",
            "  }",
            "",
            "  toggleRecipeDetails(recipeId: string): void {",
            "    if (this.expandedRecipeIds.includes(recipeId)) {",
            "      this.expandedRecipeIds = this.expandedRecipeIds.filter(id => id !== recipeId);",
            "    } else {",
            "      this.expandedRecipeIds.push(recipeId);",
            "    }",
            "  }",
            "}"
        };
        var nl = crlf ? "\r\n" : "\n";
        var content = string.Join(nl, lines) + nl;
        var path = Path.Combine(_dir, "recipe.component" + ext);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return content;
    }

    /// <summary>The AST resolver returns the block LF-normalized (AstCodeEditorService.cs:380).</summary>
    [Theory]
    [InlineData(".ts", false)]
    [InlineData(".ts", true)]
    [InlineData(".js", false)]
    [InlineData(".js", true)]
    public void FindFunctionSource_ReturnsLfNormalizedMethodBlock(string ext, bool crlf)
    {
        var sourceText = WriteFile(ext, crlf);
        var (block, startLine, error) = AstCodeEditorService.FindFunctionSource(
            sourceText, "toggleRecipeDetails", ext);

        Assert.NotNull(block);
        Assert.Null(error);
        Assert.False(block.Contains('\r'), "AST block must be LF-normalized");
        Assert.StartsWith("toggleRecipeDetails(", block.TrimStart());
        Assert.True(startLine > 0);
        Assert.Contains("expandedRecipeIds.push(recipeId);", block);
    }

    /// <summary>
    /// The CRLF bug at AgentController.cs (fixed by searching the LF-normalized
    /// text): IndexOf against the raw CRLF source fails even though the block is
    /// present — the fix normalizes the search text first.
    /// </summary>
    [Theory]
    [InlineData(".ts", true)]
    [InlineData(".js", true)]
    public void InsertAfterResolve_CrlfSource_RawIndexOfFails_NormalizedMatches(string ext, bool crlf)
    {
        var sourceText = WriteFile(ext, crlf);
        var (block, _, _) = AstCodeEditorService.FindFunctionSource(sourceText, "toggleRecipeDetails", ext)!;

        Assert.Equal(-1, sourceText.IndexOf(block, StringComparison.Ordinal));

        var searchText = sourceText.Contains("\r\n") ? sourceText.Replace("\r\n", "\n") : sourceText;
        Assert.True(searchText.IndexOf(block, StringComparison.Ordinal) >= 0,
            "LF-normalized search text must find the AST block (the applied fix)");
    }

    /// <summary>
    /// The text fallback (first occurrence of the target name) builds
    /// prefix = sourceText[..lineEnd] where lineEnd sits before the '\n', so the
    /// prefix ends with a lone '\r' on CRLF files. NormalizeLineEndings only
    /// converts "\r\n" pairs, so the raw prefix never matches the normalized
    /// file. The fix trims the trailing '\r'.
    /// </summary>
    [Theory]
    [InlineData(".ts", true)]
    [InlineData(".js", true)]
    public void TextFallbackPrefix_TrailingCr_BreaksNaiveApply_TrimsToMatch(string ext, bool crlf)
    {
        var sourceText = WriteFile(ext, crlf);
        var idx2 = sourceText.IndexOf("toggleRecipeDetails", StringComparison.Ordinal);
        Assert.True(idx2 >= 0);

        var lineEnd = sourceText.IndexOf('\n', idx2);
        Assert.True(lineEnd > 0 && sourceText[lineEnd - 1] == '\r',
            "fallback line end must land between CR and LF on CRLF files");

        var prefix = sourceText[..lineEnd];
        Assert.EndsWith("\r", prefix);

        var normFile = AgentTextUtilities.NormalizeLineEndings(sourceText);
        var normOldRaw = AgentTextUtilities.NormalizeLineEndings(prefix);
        Assert.Equal(-1, normFile.IndexOf(normOldRaw, StringComparison.Ordinal));

        var normOldFixed = normOldRaw.TrimEnd('\r');
        Assert.Equal(0, normFile.IndexOf(normOldFixed, StringComparison.Ordinal));
    }

    /// <summary>
    /// TryReplaceSafe (the FORMAT C safe matcher) must accept the buggy
    /// '\r'-terminated prefix after the TrimEnd('\r') fix, while still
    /// rejecting genuinely non-matching anchors.
    /// </summary>
    [Theory]
    [InlineData(".ts", true)]
    [InlineData(".js", true)]
    public void TryReplaceSafe_FormatCPrefix_CrlfFile(string ext, bool crlf)
    {
        var sourceText = WriteFile(ext, crlf);
        var idx2 = sourceText.IndexOf("toggleRecipeDetails", StringComparison.Ordinal);
        var lineEnd = sourceText.IndexOf('\n', idx2);
        var prefixWithCr = sourceText[..lineEnd];
        var prefix = prefixWithCr[..^1];

        // The fix: TryReplaceSafe trims the trailing '\r' from the old string
        // (formerly AgentUtilities.cs) so the fallback prefix replaces successfully
        // on CRLF files instead of failing with "oldString not found verbatim".
        var (rFixed, newContent, error, _) = AgentEditHeuristics.TryReplaceSafe(
            sourceText, prefixWithCr, prefixWithCr + "\n\n// new method", 0, null);
        Assert.True(rFixed, $"CR-terminated prefix must be accepted after the fix ({ext}): {error}");
        Assert.Contains("// new method", newContent);

        // A genuinely wrong anchor must still be rejected (no false positives).
        var (rWrong, _, errorWrong, _) = AgentEditHeuristics.TryReplaceSafe(
            sourceText, "function doesNotExistAnywhere(", "// nope", 0, null);
        Assert.False(rWrong, "non-matching anchor must be rejected");
        Assert.NotNull(errorWrong);

        var (replaced, newContent2, error2, _) = AgentEditHeuristics.TryReplaceSafe(
            sourceText, prefix, prefix + "\n\n// new method", 0, null);
        Assert.True(replaced, $"fixed prefix must replace on {ext} (crlf={crlf}): {error2}");
        Assert.Contains("// new method", newContent2);
    }

    /// <summary>
    /// End-to-end simulation of the fixed FORMAT C resolve + apply on ts/js
    /// with both LF and CRLF line endings: the resolved oldString must always
    /// be found by the apply step (normFile.IndexOf(normOld) >= 0).
    /// </summary>
    [Theory]
    [InlineData(".ts", false)]
    [InlineData(".ts", true)]
    [InlineData(".js", false)]
    [InlineData(".js", true)]
    public void FormatC_EndToEnd_ResolveAndApply_Succeeds(string ext, bool crlf)
    {
        var sourceText = WriteFile(ext, crlf);
        var normFile = AgentTextUtilities.NormalizeLineEndings(sourceText);

        // -- resolve (AST path, fixed): block + normalized search text --
        var (block, _, _) = AstCodeEditorService.FindFunctionSource(sourceText, "toggleRecipeDetails", ext);
        string oldStr;
        if (block != null)
        {
            var searchText = sourceText.Contains("\r\n") ? sourceText.Replace("\r\n", "\n") : sourceText;
            var astIdx = searchText.IndexOf(block, StringComparison.Ordinal);
            Assert.True(astIdx >= 0);
            oldStr = searchText[..(astIdx + block.Length)];
        }
        else
        {
            // -- resolve (text fallback, fixed): prefix without trailing '\r' --
            var idx2 = sourceText.IndexOf("toggleRecipeDetails", StringComparison.Ordinal);
            var lineEnd = sourceText.IndexOf('\n', idx2);
            if (lineEnd > 0 && sourceText[lineEnd - 1] == '\r') lineEnd--;
            oldStr = sourceText[..lineEnd];
        }

        // -- apply --
        var normOld = AgentTextUtilities.NormalizeLineEndings(oldStr).TrimEnd('\r');
        var applyIdx = normFile.IndexOf(normOld, StringComparison.Ordinal);
        Assert.True(applyIdx >= 0,
            $"FORMAT C oldString must be found in {ext} (crlf={crlf}) — got {applyIdx}");

        var newCode = "\n\n  openPopupMenu(): void {\n    this.isPopupPanelOpen = true;\n  }";
        var (replaced, _, error, _) = AgentEditHeuristics.TryReplaceSafe(
            sourceText, oldStr, oldStr + newCode, 0, "Add implementation of openPopupMenu()");
        Assert.True(replaced, $"TryReplaceSafe must replace on {ext} (crlf={crlf}): {error}");
    }
}
