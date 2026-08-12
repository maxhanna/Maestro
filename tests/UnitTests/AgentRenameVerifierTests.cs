using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Unit tests for <c>AgentRenameVerifier</c> — the deterministic post-execution completeness
/// check for rename-all tasks. Two halves, mirroring the guard's contract:
///   • PARSE precision: the all-occurrence signal must be required (every/all/each + occurrence
///     phrase, 'everywhere/throughout', or the direct 'rename all X to Y' form) — a plain single
///     rename, an aggregation task, or vacuous 'rename X to X' wording must NEVER fire the check.
///   • SCAN recall: when a request IS detected, every edited file is scanned for word-boundary
///     occurrences of the old name — a file that still contains it yields a CONFIRMED issue with
///     the remaining count, and a clean file (or a file where only a longer name containing the
///     token survives) passes.
/// </summary>
public class AgentRenameVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weaver_rename_verify_" + Guid.NewGuid().ToString("N"));

    public AgentRenameVerifierTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Write(string relPath, string content)
    {
        var p = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    // ── Parse precision: only genuine rename-ALL requests fire ──────────────────────────

    [Theory]
    [InlineData("Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS in the worker config", "MAX_RETRIES", "MAX_ATTEMPTS")]
    [InlineData("replace all instances of foo with bar", "foo", "bar")]
    [InlineData("Replace every usage of 'userName' by 'displayName'", "userName", "displayName")]
    [InlineData("rename each mention of retryCount to attemptCount", "retryCount", "attemptCount")]
    [InlineData("Rename MAX_RETRIES to MAX_ATTEMPTS everywhere", "MAX_RETRIES", "MAX_ATTEMPTS")]
    [InlineData("replace retryLimit with maxAttempts throughout", "retryLimit", "maxAttempts")]
    [InlineData("rename all MAX_RETRIES to MAX_ATTEMPTS", "MAX_RETRIES", "MAX_ATTEMPTS")]
    [InlineData("replace all FOO with BAR", "FOO", "BAR")]
    [InlineData("RENAME EVERY OCCURRENCE OF X TO Y", "X", "Y")]
    public void Parse_Accepts_GenuineRenameAllRequests(string prompt, string expectedOld, string expectedNew)
    {
        Assert.True(AgentRenameVerifier.TryParseRenameAllRequest(prompt, out var oldName, out var newName),
            $"expected '{prompt}' to be a rename-all request");
        Assert.Equal(expectedOld, oldName);
        Assert.Equal(expectedNew, newName);
    }

    [Theory]
    [InlineData("Group the 6 benchmarks by name in the benchmark data file")]
    [InlineData("Add max-height and overflow auto to the flight schedule container")]
    [InlineData("rename the property retryCount to attemptCount")] // single symbol, no all-occurrence signal
    [InlineData("In the CardComponent template, add an Open button that calls vm.openCard()")]
    [InlineData("fix the bug where the label overflows")]
    [InlineData("Add a comment to the MetricsService constructor.")]
    [InlineData("rename X to X everywhere")] // vacuous — old == new
    [InlineData("replace all the buttons with a link")] // 'the' between 'all' and the name — not a direct symbol
    [InlineData("")]
    [InlineData(null)]
    public void Parse_Rejects_NonRenameAllRequests(string? prompt)
    {
        Assert.False(AgentRenameVerifier.TryParseRenameAllRequest(prompt, out _, out _),
            $"expected '{prompt}' to NOT be a rename-all request");
    }

    // ── Scan recall: edited files still containing the old name are flagged ─────────────

    [Fact]
    public void Check_FileStillContainsOldName_YieldsConfirmedIssueWithCount()
    {
        Write("src/app/config.ts", "const a = MAX_RETRIES;\nconst b = MAX_RETRIES;\nconst c = MAX_ATTEMPTS;\n");
        var issues = AgentRenameVerifier.CheckRenameAllCompleteness(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS", _root, new[] { "src/app/config.ts" });
        var issue = Assert.Single(issues);
        Assert.Contains("RENAME-ALL INCOMPLETE", issue);
        Assert.Contains("'MAX_RETRIES'", issue);
        Assert.Contains("'MAX_ATTEMPTS'", issue);
        Assert.Contains("still occurs 2 times", issue);
        Assert.Contains("src/app/config.ts", issue);
    }

    [Fact]
    public void Check_SingleRemainingOccurrence_UsesSingularTime()
    {
        Write("src/app/config.ts", "const a = MAX_RETRIES;\n");
        var issues = AgentRenameVerifier.CheckRenameAllCompleteness(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS", _root, new[] { "src/app/config.ts" });
        var issue = Assert.Single(issues);
        Assert.Contains("still occurs 1 time", issue);
    }

    [Fact]
    public void Check_OldNameGone_NoIssues()
    {
        Write("src/app/config.ts", "const a = MAX_ATTEMPTS;\nconst b = MAX_ATTEMPTS;\n");
        Assert.Empty(AgentRenameVerifier.CheckRenameAllCompleteness(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS", _root, new[] { "src/app/config.ts" }));
    }

    [Fact]
    public void Check_WordBoundary_LongerNamesContainingTokenAreNotOccurrences()
    {
        // MAX_RETRIES_COUNT contains MAX_RETRIES as a prefix but is a DIFFERENT symbol — only
        // the standalone token counts, so exactly 1 occurrence remains.
        Write("src/app/config.ts", "const a = MAX_RETRIES;\nconst b = MAX_RETRIES_COUNT;\n");
        var issues = AgentRenameVerifier.CheckRenameAllCompleteness(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS", _root, new[] { "src/app/config.ts" });
        var issue = Assert.Single(issues);
        Assert.Contains("still occurs 1 time", issue);
    }

    [Fact]
    public void Check_MultipleEditedFiles_FlagsOnlyTheDirtyOne()
    {
        Write("src/app/a.ts", "const x = OLD_NAME;\nconst y = OLD_NAME;\n");
        Write("src/app/b.ts", "const z = NEW_NAME;\n");
        var issues = AgentRenameVerifier.CheckRenameAllCompleteness(
            "rename every occurrence of OLD_NAME to NEW_NAME", _root, new[] { "src/app/a.ts", "src/app/b.ts" });
        var issue = Assert.Single(issues);
        Assert.Contains("src/app/a.ts", issue);
        Assert.DoesNotContain("src/app/b.ts", issue);
    }

    [Fact]
    public void Check_NonRenamePrompt_NeverFiresEvenWhenFileContainsToken()
    {
        Write("src/app/config.ts", "const a = MAX_RETRIES;\n");
        Assert.Empty(AgentRenameVerifier.CheckRenameAllCompleteness(
            "Group the benchmarks by name", _root, new[] { "src/app/config.ts" }));
    }

    [Fact]
    public void Check_MissingFile_Skipped()
    {
        Assert.Empty(AgentRenameVerifier.CheckRenameAllCompleteness(
            "Rename every occurrence of MAX_RETRIES to MAX_ATTEMPTS", _root, new[] { "src/app/absent.ts" }));
    }
}
