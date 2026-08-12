using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Deterministic post-execution verification for RENAME-ALL tasks ("rename every occurrence of
/// X to Y", "replace all instances of X with Y", "rename X to Y everywhere"). This is the
/// completeness guard for the "confident partial rename" failure class: the LLM renames ONE of N
/// occurrences (a plausible, well-formed edit) and declares the task done, silently corrupting
/// the data. No LLM is consulted anywhere in this class — everything is a pure function of the
/// task text and the CURRENT file contents, so the findings are always CONFIRMED.
/// </summary>
/// <remarks>
/// <para>
/// The check fires only for ALL-OCCURRENCE requests: a rename verb plus an every/all/each
/// occurrence phrase, the "everywhere/throughout" form, or the direct "rename all X to Y" form.
/// A plain "rename the property X to Y" (a single symbol) is deliberately NOT treated as a
/// rename-all — the edit may legitimately touch only the declaration.
/// </para>
/// <para>
/// When a request is detected, every file the run reports as edited is scanned for
/// word-boundary occurrences of the old name; each file that still contains it yields a
/// CONFIRMED issue naming the file and the remaining count, which flows through the repair loop
/// so the replanner replaces the rest. A clean scan is reported as a positive ground-truth pass
/// (every occurrence of the old name is gone from the edited files).
/// </para>
/// </remarks>
public static class AgentRenameVerifier
{
    // 1) Phrase form: "rename every occurrence of X to Y" / "replace all instances of X with Y".
    private static readonly Regex PhraseForm = new(
        @"\b(?:rename|replace)\s+(?:every|each|all)\s+(?:occurrence|instance|usage|mention|appearance)s?\s+of\s+['""]?([A-Za-z_]\w*)['""]?\s+(?:to|with|by)\s+['""]?([A-Za-z_]\w*)['""]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 2) Direct form with a scope word: "rename X to Y everywhere / throughout".
    private static readonly Regex ScopeForm = new(
        @"\b(?:rename|replace)\s+['""]?([A-Za-z_]\w*)['""]?\s+(?:to|with|by)\s+['""]?([A-Za-z_]\w*)['""]?\s+(?:everywhere|throughout)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 3) Direct form with "all": "rename all X to Y" / "replace all X with Y".
    private static readonly Regex AllForm = new(
        @"\b(?:rename|replace)\s+all\s+['""]?([A-Za-z_]\w*)['""]?\s+(?:to|with|by)\s+['""]?([A-Za-z_]\w*)['""]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Deterministically detects a rename-ALL request in the task text and extracts the old and
    /// new names. Returns false for non-rename tasks and for vacuous "rename X to X" wording, so
    /// the completeness check can never fire on an edit that isn't an all-occurrence rename.
    /// </summary>
    public static bool TryParseRenameAllRequest(string? prompt, out string oldName, out string newName)
    {
        oldName = "";
        newName = "";
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        foreach (var rx in new[] { PhraseForm, ScopeForm, AllForm })
        {
            var m = rx.Match(prompt);
            if (!m.Success) continue;
            var oldN = m.Groups[1].Value;
            var newN = m.Groups[2].Value;
            if (string.Equals(oldN, newN, StringComparison.Ordinal)) continue; // "rename X to X" is vacuous
            oldName = oldN;
            newName = newN;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks every file the run reports as edited for word-boundary occurrences of the old name
    /// from a rename-all request. Returns one CONFIRMED issue per file that still contains it
    /// (with the remaining count), so verification fails until the repair loop replaces the rest.
    /// Files that no longer contain the old name are silently passed.
    /// </summary>
    public static List<string> CheckRenameAllCompleteness(
        string? prompt, string projectRoot, IReadOnlyList<string> modifiedPaths)
    {
        var issues = new List<string>();
        if (!TryParseRenameAllRequest(prompt, out var oldName, out var newName)) return issues;
        var nameRegex = new Regex(@"\b" + Regex.Escape(oldName) + @"\b", RegexOptions.Compiled);
        foreach (var relPath in modifiedPaths)
        {
            var absPath = Path.Combine(projectRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absPath)) continue;
            string content;
            try { content = File.ReadAllText(absPath); }
            catch (IOException) { continue; }
            var count = nameRegex.Matches(content).Count;
            if (count > 0)
            {
                issues.Add(
                    $"RENAME-ALL INCOMPLETE — the task asked to rename every occurrence of '{oldName}' to '{newName}', " +
                    $"but '{oldName}' still occurs {count} time{(count == 1 ? "" : "s")} in {relPath}. " +
                    $"Replace every remaining occurrence of '{oldName}' with '{newName}'.");
            }
        }
        return issues;
    }
}
