using System.Text.RegularExpressions;
using Weaver;

namespace Weaver.Services;

/// <summary>
/// Deterministic verification + repair for a benchmark's live canvas/animation state globals
/// (<c>window.legCount</c>). The browser test reads the REAL value off the rendered page and
/// reports it as a finding (<c>window.legCount = 4 (live canvas/animation state)</c>); this class
/// closes the loop the weak model keeps fumbling:
/// <list type="bullet">
/// <item><see cref="CheckLiveStateMismatch"/> compares that live value against the value the task
/// REQUIRES ("window.legCount must equal 6") and returns a CONFIRMED verifier issue when they
/// differ — so a browser test that PASSED its heading check but read the wrong canvas state still
/// keeps the run incomplete.</item>
/// <item><see cref="TryBuildStateRepairStep"/> turns that issue into a fully-resolved
/// (oldString/newString) edit that rewrites <c>window.&lt;name&gt; = &lt;actual&gt;</c> to the required
/// value (and any hardcoded draw-loop bound like <c>for(...;i&lt;4;...)</c>) — no LLM round-trip, so
/// the edit lands even when the model cannot produce one.</item>
/// </list>
/// Everything is a pure function of the task text, the browser-test output, and the on-disk file
/// content — the same determinism discipline as <see cref="AgentOsOutputVerifier"/>.
/// </summary>
public static class AgentStateProbeVerifier
{
    private const string ValuePattern = @"-?\d+(?:\.\d+)?|true|false|null";

    // The live-probe finding BrowserAutomationService.AppendLiveStateProbesAsync emits:
    //   "window.legCount = 4 (live canvas/animation state)"
    private static readonly Regex LiveProbeRegex = new(
        @"\bwindow\.([A-Za-z_$][\w$]*)\s*=\s*(" + ValuePattern + @")\s*\(live canvas/animation state\)",
        RegexOptions.Compiled);

    // The CONFIRMED issue text this class generates — parsed back by TryParseMismatch so the
    // repair step can be synthesized from the issue string alone (no re-parsing of the output).
    private static readonly Regex MismatchIssueRegex = new(
        @"\bwindow\.([A-Za-z_$][\w$]*)\s+is\s+(" + ValuePattern + @")\s+on\s+the\s+rendered\s+page\s+but\s+the\s+task\s+requires\s+(" + ValuePattern + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Compares the most recent live state probe against the value the task requires. Returns a
    /// CONFIRMED issue when the prompt demands <c>window.&lt;name&gt;</c> equal a value but the
    /// browser read a different one (benchmark 23: "window.legCount must equal 6" but the page
    /// reports 4). Returns null when there is no expectation, no probe, or the values match.
    /// </summary>
    public static string? CheckLiveStateMismatch(string? prompt, string? browserTestOutput)
    {
        var expectation = TestIntentClassifier.ExtractWindowStateExpectation(prompt);
        if (expectation == null) return null;
        if (string.IsNullOrWhiteSpace(browserTestOutput)) return null;

        // Last match wins: a repair-pass re-run appends a fresh probe, and only the final read counts.
        string? live = null;
        foreach (Match m in LiveProbeRegex.Matches(browserTestOutput))
        {
            if (string.Equals(m.Groups[1].Value, expectation.Name, StringComparison.Ordinal))
                live = m.Groups[2].Value;
        }
        if (live == null) return null;
        if (string.Equals(live, expectation.ExpectedValue, StringComparison.Ordinal)) return null;

        return $"window.{expectation.Name} is {live} on the rendered page but the task requires {expectation.ExpectedValue} — " +
               $"update the code so window.{expectation.Name} = {expectation.ExpectedValue} (and any hardcoded " +
               $"leg/loop count uses window.{expectation.Name}) so the canvas shows the required state.";
    }

    /// <summary>Parses a <see cref="CheckLiveStateMismatch"/> issue back into its parts.</summary>
    internal static (string Name, string Actual, string Expected)? TryParseMismatch(string? issue)
    {
        if (string.IsNullOrWhiteSpace(issue)) return null;
        var m = MismatchIssueRegex.Match(issue);
        if (!m.Success) return null;
        return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
    }

    /// <summary>
    /// Synthesizes a fully-resolved edit for the live-state mismatch — a concrete
    /// oldString/newString that rewrites <c>window.&lt;name&gt; = &lt;actual&gt;</c> to the required value,
    /// plus (when the drawing loop hardcodes the old count) a second edit that makes the loop
    /// bound follow <c>window.&lt;name&gt;</c>. Returns null when the file does not contain a matching
    /// assignment, so the caller falls back to the normal LLM repair.
    /// </summary>
    internal static List<EditPair>? BuildStateRepairEdits(string fileContent, string name, string actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(fileContent) || string.IsNullOrWhiteSpace(name))
            return null;

        var edits = new List<EditPair>();

        // 1) The assignment: window.legCount = 4 (flexible whitespace) → the required value,
        //    preserving everything before the literal so "=4" stays "=6" and "= 4" stays "= 6".
        var assignmentRx = new Regex(
            @"\bwindow\." + Regex.Escape(name) + @"\s*=\s*(" + ValuePattern + @")",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        foreach (Match m in assignmentRx.Matches(fileContent))
        {
            if (!string.Equals(m.Groups[1].Value, actual, StringComparison.Ordinal)) continue;
            var valueStart = m.Groups[1].Index - m.Index;
            edits.Add(new EditPair
            {
                OldString = m.Value,
                NewString = m.Value[..valueStart] + expected,
                LineNumber = LineOf(fileContent, m.Index)
            });
        }

        // 2) A draw loop that hardcodes the old count: "for(let i=0;i<4;++i)" → "i<window.legCount".
        //    Only the comparison token is rewritten; the variable and spacing are preserved.
        var loopRx = new Regex(
            @"\bfor\s*\([^)]*\b(?<cmp>(?<var>[A-Za-z_$]\w*)\s*<\s*(?<n>\d+))\s*(?=[;)])",
            RegexOptions.Compiled);
        foreach (Match m in loopRx.Matches(fileContent))
        {
            if (!string.Equals(m.Groups["n"].Value, actual, StringComparison.Ordinal)) continue;
            var cmp = m.Groups["cmp"].Value;
            edits.Add(new EditPair
            {
                OldString = cmp,
                NewString = cmp.Replace(m.Groups["n"].Value, "window." + name),
                LineNumber = LineOf(fileContent, m.Groups["cmp"].Index)
            });
        }

        return edits.Count == 0 ? null : edits;
    }

    /// <summary>
    /// Builds a deterministic <see cref="PlanStep"/> for the given live-state mismatch issue,
    /// targeting the first file (from the run's edited/created/read paths, then a bounded source
    /// scan) that contains a matching <c>window.&lt;name&gt; = &lt;actual&gt;</c> assignment. Returns null
    /// when the issue is not a mismatch or no file matches — the repair loop then falls back to
    /// the LLM replanner as usual.
    /// </summary>
    public static PlanStep? TryBuildStateRepairStep(string projectRoot, string? issue, IEnumerable<object> allResults)
    {
        var mismatch = TryParseMismatch(issue);
        if (mismatch == null) return null;
        var (name, actual, expected) = mismatch.Value;

        var candidates = new List<string>();
        foreach (var r in allResults.OfType<Dictionary<string, object?>>())
        {
            if (r.GetValueOrDefault("type")?.ToString() is not ("edit" or "create" or "read")) continue;
            var p = r.GetValueOrDefault("path")?.ToString();
            if (!string.IsNullOrWhiteSpace(p) && !candidates.Contains(p, StringComparer.OrdinalIgnoreCase))
                candidates.Add(p);
        }
        foreach (var rel in EnumerateSourceFiles(projectRoot))
            if (!candidates.Contains(rel, StringComparer.OrdinalIgnoreCase))
                candidates.Add(rel);

        foreach (var rel in candidates)
        {
            var full = ResolveUnderRoot(projectRoot, rel);
            if (full == null || !System.IO.File.Exists(full)) continue;
            string content;
            try { content = System.IO.File.ReadAllText(full); }
            catch { continue; }

            var edits = BuildStateRepairEdits(content, name, actual, expected);
            if (edits is not { Count: > 0 }) continue;

            return new PlanStep
            {
                File = rel,
                Change = $"Set window.{name} from {actual} to {expected} — the live browser state probe read {actual} but the task requires {expected}",
                OldString = edits[0].OldString,
                NewString = $"(deterministic batch: {edits.Count} edits, applied {edits.Count}/{edits.Count} occurrences)",
                LineNumber = edits[0].LineNumber,
                Edits = edits
            };
        }
        return null;
    }

    private static int LineOf(string content, int index)
        => index < 0 ? 0 : content.AsSpan(0, Math.Min(index, content.Length)).Count('\n') + 1;

    private static string? ResolveUnderRoot(string projectRoot, string rel)
    {
        try
        {
            var full = System.IO.Path.IsPathRooted(rel)
                ? rel
                : System.IO.Path.Combine(projectRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return System.IO.Path.GetFullPath(full);
        }
        catch { return null; }
    }

    private static readonly string[] SourceExtensions =
    {
        ".html", ".htm", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".vue", ".svelte", ".py", ".cs", ".css", ".json"
    };

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", ".vs", ".vscode", "data", "undo"
    };

    /// <summary>Bounded, best-effort scan for source files under the project root (fallback when
    /// the run's own results don't name the file). Yields project-root-RELATIVE paths (the same
    /// shape the apply pipeline expects in <see cref="PlanStep.File"/>). Capped so a large repo
    /// never causes a long stall.</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !System.IO.Directory.Exists(projectRoot))
            yield break;
        var root = System.IO.Path.GetFullPath(projectRoot);
        var budget = new ScanBudget();
        foreach (var file in EnumerateFilesBounded(root, 0, budget))
        {
            if (budget.Count > 2000) yield break;
            var ext = System.IO.Path.GetExtension(file)?.ToLowerInvariant();
            if (ext != null && SourceExtensions.Contains(ext))
                yield return System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
        }
    }

    private sealed class ScanBudget { public int Count; }

    private static IEnumerable<string> EnumerateFilesBounded(string dir, int depth, ScanBudget budget)
    {
        if (depth > 6 || budget.Count > 2000) yield break;
        string[] files, dirs;
        try
        {
            files = System.IO.Directory.GetFiles(dir);
            dirs = System.IO.Directory.GetDirectories(dir);
        }
        catch { yield break; }
        foreach (var f in files)
        {
            if (++budget.Count > 2000) yield break;
            yield return f;
        }
        foreach (var d in dirs)
        {
            if (SkipDirectories.Contains(System.IO.Path.GetFileName(d))) continue;
            foreach (var f in EnumerateFilesBounded(d, depth + 1, budget))
            {
                if (budget.Count > 2000) yield break;
                yield return f;
            }
        }
    }
}
