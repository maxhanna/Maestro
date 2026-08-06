using System.Text.RegularExpressions;
using AngleSharp.Css.Parser;

namespace Weaver.Services;

public static class LlmCssCleaner
{
    private static readonly Regex SplitHexRx = new(@"(?<=[:\s])#([0-9a-fA-F]{1,2})\s+([0-9a-fA-F]{1,2})\s+([0-9a-fA-F]{1,2})(?:\s+([0-9a-fA-F]{1,2}))?", RegexOptions.Compiled);
    private static readonly Regex UnitRx = new(@"(\d+(?:\.\d+)?(?:px|rem|em|%|vh|vw|ms|s|deg|fr))(?=\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Squished zeros (0000 -> 0 0 0 0). The prefix must NOT be part of a hex color
    // (#f00, #ff0000) or a plain number (z-index: 1000) — exclude '#' and word chars
    // so only zero-runs directly after whitespace/colon/paren get split.
    private static readonly Regex ZeroRx = new(@"(^|[^.\d#\w])0+(?=\d)", RegexOptions.Compiled);
    private static readonly Regex CalcRx = new(@"calc\(([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CalcOpRx = new(@"\s*([+\-*/])\s*", RegexOptions.Compiled);
    private static readonly Regex DblSpaceRx = new(@"\s+", RegexOptions.Compiled);
    // Fix squished keyword-number (all0.2s -> all 0.2s). The word is restricted to known
    // CSS timing/keyword tokens so type selectors (`h4`) AND arbitrary identifiers like
    // @keyframes spin1s / animation-name: pulse1s are never split — a bare digit or
    // duration-suffixed name is a selector/identifier, not a squished value.
    private static readonly Regex WordNumberRx = new(@"(?<=[\s(:])((?:all|linear|ease(?:-in|-out|-in-out)?|infinite|normal|alternate(?:-reverse)?|forwards|backwards|both|none|paused|running|step-(?:start|end)))(\d+(?:\.\d+)?(?:ms|s))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MissingColonRx = new(@"^(\s*[a-z-]+)\s+(?=\d|#|var\(--)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    // Only insert the space when the value starts with a non-letter/non-colon/non-space
    // token (digit, #, -, quote, var(, ...). A selector line like `a:hover {` or
    // `a::before {` must NEVER be rewritten to `a: hover` — the char after the colon
    // there is a letter or a colon, so it is skipped. A line that already has a space
    // after the colon (`margin: 8px`) is skipped too — no double space.
    private static readonly Regex MissingSpaceAfterColonRx = new(@"^(\s*[a-z-]+):(?=[^a-zA-Z:\s])", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    // Fix a trailing comma on a declaration (width:40px, -> width: 40px;) — but ONLY
    // when the value starts with a non-letter/non-colon token (digit, #, -, quote, ...),
    // so selector-list lines like `a:hover,` / `a::before,` / `.guess-header,` are never
    // touched. The value is captured so it is preserved, not dropped.
    private static readonly Regex TrailingCommaRx = new(@"^(\s*[a-z-]+:\s*(?=[^a-zA-Z:])[^;{]+),\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex SmashedBraceRx = new(@"\}(?=[^\s}])", RegexOptions.Compiled);

    public static string Clean(string cssContent)
    {
        if (string.IsNullOrEmpty(cssContent)) return cssContent;

        string clean = cssContent;

        // 0. Fix split hex colors (#ab cd ef -> #abcdef)
        clean = SplitHexRx.Replace(clean, match =>
        {
            string hex = "#" + match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value;
            if (match.Groups[4].Success)
                hex += match.Groups[4].Value;
            return hex;
        });

        // 1. Fix squished units (12px24px -> 12px 24px)
        clean = UnitRx.Replace(clean, "$1 ");

        // 2. Fix squished zeros (0000 -> 0 0 0 0)
        clean = ZeroRx.Replace(clean, "${1}0 ");

        // 3. Fix missing spaces inside calc()
        clean = CalcRx.Replace(clean, match => {
            string inner = match.Groups[1].Value;
            string spacedInner = CalcOpRx.Replace(inner, " $1 ");
            return $"calc({DblSpaceRx.Replace(spacedInner, " ")})";
        });

        // 4. Fix missing colons
        clean = MissingColonRx.Replace(clean, "$1: ");

        // 4b. Fix missing space after colon (width:40px -> width: 40px)
        clean = MissingSpaceAfterColonRx.Replace(clean, "$1: ");

        // 4c. Fix squished keyword-number (all0.2s -> all 0.2s)
        clean = WordNumberRx.Replace(clean, "$1 $2");

        // 5. Fix illegal trailing commas (width:40px, -> width:40px;)
        clean = TrailingCommaRx.Replace(clean, "$1;");

        // 6. Fix smashed closing curly braces
        clean = SmashedBraceRx.Replace(clean, "}\n");

        return clean;
    }

    public static string FixCssStructure(string css)
    {
        if (string.IsNullOrEmpty(css)) return css;

        int openBraces = 0, closeBraces = 0;
        foreach (var c in css)
        {
            if (c == '{') openBraces++;
            else if (c == '}') closeBraces++;
        }
        if (openBraces == closeBraces) return css;

        try
        {
            var parser = new CssParser(new CssParserOptions
            {
                IsIncludingUnknownDeclarations = true,
                IsIncludingUnknownRules = true
            });
            var sheet = parser.ParseStyleSheet(css);

            using var writer = new StringWriter();
            sheet.ToCss(writer, new AngleSharp.Css.PrettyStyleFormatter
            {
                Indentation = "  ",
                NewLine = "\n"
            });
            var fixedCss = writer.ToString();
            return !string.IsNullOrWhiteSpace(fixedCss) ? fixedCss : css;
        }
        catch
        {
            return css;
        }
    }
}
