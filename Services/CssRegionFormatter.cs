namespace Weaver.Services;

/// <summary>
/// Region-window CSS formatting: locates an applied edit inside a file, widens it to a
/// ±4-line window, formats ONLY that window (prettier + LlmCssCleaner.Clean), and splices
/// it back. Extracted from AgentController.Formatting.cs so the window logic is testable
/// without reflecting into the controller.
/// </summary>
public static class CssRegionFormatter
{
    /// <summary>
    /// Formats just the region around an accepted edit when the file is a CSS family
    /// (.css/.scss/.less) and the new content is present in the file. Returns the original
    /// content unchanged when the extension is not CSS, the region can't be located, or the
    /// formatted window is identical to the original window.
    /// </summary>
    public static async Task<string> FormatAcceptedEditRegionAsync(
        string filePath, string content, string? oldString, string? newString, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(newString))
            return content;
        var normalizedContent = AgentTextUtilities.NormalizeLineEndings(content);
        var normalizedNew = AgentTextUtilities.NormalizeLineEndings(newString).Trim('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalizedNew))
            return content;
        var regionIndex = normalizedContent.IndexOf(normalizedNew, StringComparison.Ordinal);
        if (regionIndex < 0)
            return content;
        var contentLines = normalizedContent.Split('\n').ToList();
        var regionLineStart = normalizedContent[..regionIndex].Count(c => c == '\n') + 1;
        var regionLineEnd = regionLineStart + normalizedNew.Split('\n').Length - 1;
        var windowStart = Math.Max(1, regionLineStart - 4);
        var windowEnd = Math.Min(contentLines.Count, regionLineEnd + 4);
        var fExt = Path.GetExtension(filePath).ToLowerInvariant();
        if (fExt != ".css" && fExt != ".scss" && fExt != ".less")
            return content;
        var windowLines = contentLines.Skip(windowStart - 1).Take(windowEnd - windowStart + 1).ToList();
        var windowText = string.Join("\n", windowLines);
        var formattedWindow = await CodeFormatterService.FormatAsync(filePath, windowText, ct);
        formattedWindow = LlmCssCleaner.Clean(formattedWindow);
        if (string.Equals(formattedWindow, windowText, StringComparison.Ordinal))
            return content;
        var formattedWindowLines = formattedWindow.Split('\n').ToList();
        var replaceStart = windowStart - 1;
        var replaceCount = windowLines.Count;
        contentLines.RemoveRange(replaceStart, replaceCount);
        contentLines.InsertRange(replaceStart, formattedWindowLines);
        return string.Join("\n", contentLines);
    }
}
