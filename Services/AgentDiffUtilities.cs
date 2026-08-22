using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Weaver.Services;

using static Weaver.Services.AgentTokenMetrics;
using static Weaver.Services.AgentEditHeuristics;
using static Weaver.Services.AgentPlanParsing;
using static Weaver.Services.AgentMethodInventory;
using static Weaver.Services.AgentProjectUtilities;
using static Weaver.Services.AgentDiscovery;
using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentCodeFormatting;
using static Weaver.Services.AgentSkeleton;
using static Weaver.Services.AgentDiffUtilities;
using static Weaver.Services.AgentJsonUtilities;

/// <summary>Part of the split of the former AgentUtilities monolith.</summary>
public static class AgentDiffUtilities
{
    public static string BuildDiffPreview(string? oldStr, string? newStr)
    {
        if (string.IsNullOrEmpty(oldStr) && string.IsNullOrEmpty(newStr)) return string.Empty;
        var oldLines = (oldStr ?? string.Empty).Split('\n');
        var newLines = (newStr ?? string.Empty).Split('\n');
        var sb = new StringBuilder();
        for (int i = 0, j = 0; i < oldLines.Length || j < newLines.Length;)
        {
            if (i < oldLines.Length && j < newLines.Length && oldLines[i] == newLines[j])
            {
                sb.Append("  ").AppendLine(oldLines[i]);
                i++; j++;
            }
            else
            {
                if (i < oldLines.Length) { sb.Append("- ").AppendLine(oldLines[i]); i++; }
                if (j < newLines.Length) { sb.Append("+ ").AppendLine(newLines[j]); j++; }
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Truthful line diff of REAL file content (before → after), LCS-aligned so unchanged
    /// lines pair up instead of showing as removed+re-added. The files-changed preview and
    /// +/− counts previously came from the LLM's oldString/newString, which can carry
    /// drifted indentation/blank lines — a single inserted line (the live
    /// navigation.component.ts moviesTodoCount insert) then rendered as 2−/3+ because every
    /// line's indentation differed. Returns the aligned old/new line arrays (null on the
    /// empty side of a pure add/remove), a focused +/- preview, the +/− counts, and the
    /// 0-based OLD line index where the shown context window starts.
    /// </summary>
    public static (int Added, int Removed, object[] OldLines, object[] NewLines, string Preview, int OldStartLine) BuildAlignedDiff(
        string beforeContent, string afterContent, int maxContextLines = 3)
    {
        var beforeLines = (beforeContent ?? "").Replace("\r\n", "\n").Split('\n');
        var afterLines = (afterContent ?? "").Replace("\r\n", "\n").Split('\n');
        var afterToBefore = LcsAlign(beforeLines, afterLines);

        // Canonical LCS row sequence: paired (old, new) rows for aligned lines, old-only
        // rows for pure removals, new-only rows for pure additions.
        var rows = new List<(string? Old, string? New, int OldIdx, bool Changed)>();
        var i = 0;
        for (var j = 0; j < afterLines.Length; j++)
        {
            var bi = afterToBefore[j];
            if (bi < 0)
            {
                rows.Add((null, afterLines[j], i, true)); // pure addition
                continue;
            }
            while (i < bi) { rows.Add((beforeLines[i], null, i, true)); i++; } // pure removals
            rows.Add((beforeLines[bi], afterLines[j], bi,
                !string.Equals(beforeLines[bi], afterLines[j], StringComparison.Ordinal)));
            i = bi + 1;
        }
        while (i < beforeLines.Length) { rows.Add((beforeLines[i], null, i, true)); i++; }

        var added = 0;
        var removed = 0;
        var first = -1;
        var last = -1;
        for (var r = 0; r < rows.Count; r++)
        {
            if (rows[r].Old == null) added++;
            if (rows[r].New == null) removed++;
            if (rows[r].Changed) { if (first < 0) first = r; last = r; }
        }
        if (first < 0)
            return (0, 0, Array.Empty<object>(), Array.Empty<object>(), "", 0);

        // Window around the changed region (a few unchanged context lines on each side).
        var start = Math.Max(0, first - maxContextLines);
        var end = Math.Min(rows.Count - 1, last + maxContextLines);
        var oldLines = new object[end - start + 1];
        var newLines = new object[end - start + 1];
        var sb = new StringBuilder();
        for (var r = start; r <= end; r++)
        {
            oldLines[r - start] = rows[r].Old!;
            newLines[r - start] = rows[r].New!;
            if (rows[r].Old == null) sb.Append("+ ").AppendLine(rows[r].New);
            else if (rows[r].New == null) sb.Append("- ").AppendLine(rows[r].Old);
            else if (rows[r].Changed) { sb.Append("- ").AppendLine(rows[r].Old); sb.Append("+ ").AppendLine(rows[r].New); }
            else sb.Append("  ").AppendLine(rows[r].Old);
        }
        return (added, removed, oldLines, newLines, sb.ToString().TrimEnd(), rows[start].OldIdx);
    }

    public static string BuildUnifiedDiff(string oldStr, string newStr, string filePath)
    {
        var oldLines = (oldStr ?? "").Replace("\r\n", "\n").Split('\n');
        var newLines = (newStr ?? "").Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        sb.Append("--- a/").AppendLine(filePath);
        sb.Append("+++ b/").AppendLine(filePath);
        int maxLen = Math.Max(oldLines.Length, newLines.Length);
        int hunkStart = -1;
        var hunkOld = new List<string>();
        var hunkNew = new List<string>();
        for (int idx = 0; idx < maxLen; idx++)
        {
            bool same = idx < oldLines.Length && idx < newLines.Length && oldLines[idx] == newLines[idx];
            if (same)
            {
                if (hunkStart != -1)
                {
                    FlushHunkSimple(sb, hunkStart, hunkOld, hunkNew, filePath);
                    hunkStart = -1; hunkOld.Clear(); hunkNew.Clear();
                }
                continue;
            }
            if (hunkStart == -1) hunkStart = idx;
            if (idx < oldLines.Length) hunkOld.Add(oldLines[idx]);
            if (idx < newLines.Length) hunkNew.Add(newLines[idx]);
        }
        if (hunkStart != -1 || hunkOld.Count > 0 || hunkNew.Count > 0)
            FlushHunkSimple(sb, hunkStart == -1 ? 0 : hunkStart, hunkOld, hunkNew, filePath);
        return sb.ToString();
    }

    internal static void FlushHunkSimple(StringBuilder sb, int hunkStart,
        List<string> hunkOld, List<string> hunkNew, string filePath)
    {
        int oldCount = hunkOld.Count;
        int newCount = hunkNew.Count;
        sb.Append("@@ -").Append(hunkStart + 1).Append(',').Append(oldCount)
          .Append(" +").Append(hunkStart + 1).Append(',').Append(newCount).AppendLine(" @@");
        foreach (var line in hunkOld) sb.Append('-').AppendLine(line);
        foreach (var line in hunkNew) sb.Append('+').AppendLine(line);
    }

    public static int ComputeLevenshteinDistance(string a, string b)
    {
        var m = a.Length; var n = b.Length;
        if (m == 0) return n; if (n == 0) return m;
        var d = new int[n + 1];
        for (var i = 0; i <= n; i++) d[i] = i;
        for (var i = 1; i <= m; i++)
        {
            var prev = d[0]; d[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var temp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1),
                    prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = temp;
            }
        }
        return d[n];
    }

    public static string ReconstructFromVerbatimDiff(string verbatimBlock, string llmNewStr)
    {
        if (string.IsNullOrEmpty(verbatimBlock) || string.IsNullOrEmpty(llmNewStr))
            return llmNewStr ?? "";
        var verbatimLines = verbatimBlock.Split('\n');
        var newLines = llmNewStr.Split('\n');
        var newToVerbatim = LcsAlign(verbatimLines, newLines);
        var result = new List<string>(newLines.Length);
        for (var j = 0; j < newLines.Length; j++)
        {
            if (newToVerbatim[j] >= 0)
                result.Add(verbatimLines[newToVerbatim[j]]);
            else
                result.Add(newLines[j]);
        }
        var reconstructed = string.Join("\n", result);
        if (reconstructed.Split('\n').Length < newLines.Length)
            return llmNewStr;
        return reconstructed;
    }

    internal static int[] LcsAlign(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                if (LinesMatchPrefixTolerant(a[i - 1], b[j - 1]))
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
            }
        }
        var newToVerbatim = new int[m];
        for (var j = 0; j < m; j++) newToVerbatim[j] = -1;
        var ii = n;
        var jj = m;
        while (ii > 0 && jj > 0)
        {
            if (LinesMatchPrefixTolerant(a[ii - 1], b[jj - 1]))
            {
                newToVerbatim[jj - 1] = ii - 1;
                ii--; jj--;
            }
            else if (dp[ii - 1, jj] >= dp[ii, jj - 1])
                ii--;
            else
                jj--;
        }
        return newToVerbatim;
    }

    internal static bool LinesMatchPrefixTolerant(string x, string y)
    {
        var xt = x.Trim();
        var yt = y.Trim();
        if (xt.Length == 0 || yt.Length == 0)
            return xt.Length == 0 && yt.Length == 0;
        return xt == yt
            || (xt.Length >= yt.Length && xt.StartsWith(yt, StringComparison.Ordinal))
            || (yt.Length >= xt.Length && yt.StartsWith(xt, StringComparison.Ordinal));
    }

    public static double ComputeLineSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        var aNorm = a.Trim().ToLowerInvariant();
        var bNorm = b.Trim().ToLowerInvariant();
        var maxLen = Math.Max(aNorm.Length, bNorm.Length);
        if (maxLen == 0) return 1.0;
        if (maxLen <= 80)
            return 1.0 - (double)ComputeLevenshteinDistance(aNorm, bNorm) / maxLen;
        var common = 0; var minLen = Math.Min(aNorm.Length, bNorm.Length);
        for (var i = 0; i < minLen; i++) { if (aNorm[i] == bNorm[i]) common++; else break; }
        return (double)common / maxLen;
    }

    public static string? DescribeLineDiff(string llm, string file)
    {
        if (string.Equals(llm, file, StringComparison.Ordinal)) return null;
        var diffs = new List<string>();
        var llmNoCommaSpace = Regex.Replace(llm, @",\s*", ",");
        var fileNoCommaSpace = Regex.Replace(file, @",\s*", ",");
        if (llmNoCommaSpace == fileNoCommaSpace && llm != file)
            diffs.Add("the file has spaces after commas that you omitted — e.g. 'rgba(255,255,255)' should be 'rgba(255, 255, 255)'");
        var llmNoColonSpace = Regex.Replace(llm, @":\s*", ":");
        var fileNoColonSpace = Regex.Replace(file, @":\s*", ":");
        if (llmNoColonSpace == fileNoColonSpace && llmNoCommaSpace != fileNoCommaSpace)
            diffs.Add("the file has spaces after colons that you omitted — e.g. 'padding:16px' should be 'padding: 16px'");
        var llmNoEqSpace = Regex.Replace(llm, @"\s*=\s*", "=");
        var fileNoEqSpace = Regex.Replace(file, @"\s*=\s*", "=");
        if (llmNoEqSpace == fileNoEqSpace && llmNoCommaSpace != fileNoCommaSpace && llmNoColonSpace != fileNoColonSpace)
            diffs.Add("the file has spaces around '=' that you omitted — e.g. 'x=0' should be 'x = 0'");
        var llmNoParenSpace = Regex.Replace(llm, @"\(\s+", "(").Replace(")", " )").Replace(") )", "))");
        var fileNoParenSpace = Regex.Replace(file, @"\(\s+", "(").Replace(")", " )").Replace(") )", "))");
        if (llmNoParenSpace == fileNoParenSpace && llm != file
            && llmNoCommaSpace == fileNoCommaSpace && llmNoColonSpace == fileNoColonSpace)
            diffs.Add("the file has different whitespace inside parens");
        if (diffs.Count == 0)
        {
            var minLen = Math.Min(llm.Length, file.Length);
            var firstDiff = -1;
            for (var i = 0; i < minLen; i++)
            {
                if (llm[i] != file[i]) { firstDiff = i; break; }
            }
            if (firstDiff >= 0)
            {
                var ctx = Math.Max(0, firstDiff - 8);
                var llmCtx = llm.Substring(ctx, Math.Min(20, llm.Length - ctx));
                var fileCtx = file.Substring(ctx, Math.Min(20, file.Length - ctx));
                diffs.Add($"first difference at position {firstDiff}: you wrote '{llmCtx}' but file has '{fileCtx}'");
            }
            else
            {
                diffs.Add($"length differs: you wrote {llm.Length} chars, file has {file.Length} chars");
            }
        }
        return string.Join("; ", diffs);
    }
}
