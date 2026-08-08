using System.Reflection;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the missing-web-search gate helpers in the interleaved plan loop
/// (Controllers/AgentController.Planning.cs). TaskHintsWebNeed is a deliberately
/// BROAD regex gate — it only opens the LLM verification, never rejects on its
/// own — so it must catch web-flavored tasks (including noisy "search for" /
/// "look up" phrasing that may be repo-internal) without snagging plain coding
/// tasks. IsWebStep recognizes the _web_search/_web_fetch markers so the guard
/// stands down once a web step is planned.
/// </summary>
public class WebNeedGuardTests
{
    private static readonly MethodInfo HintsMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("TaskHintsWebNeed", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("TaskHintsWebNeed static method not found.");

    private static readonly MethodInfo WebStepMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("IsWebStep", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("IsWebStep static method not found.");

    private static bool Hints(string? p) => (bool)(HintsMethod.Invoke(null, new object?[] { p }) ?? false);
    private static bool IsWeb(string? f) => (bool)(WebStepMethod.Invoke(null, new object?[] { f }) ?? false);

    [Theory]
    [InlineData("Perform 3 independent web searches and write the results to a file")]
    [InlineData("Fetch the current Bitcoin halving date from the internet")]
    [InlineData("look up the latest API docs for the Stripe library")]
    [InlineData("search for the up to date version numbers online")]
    [InlineData("Find out what today's exchange rates are")]
    [InlineData("Search the web for three facts and save them to internet_facts.txt")]
    public void TaskHintsWebNeed_DetectsWebFlavoredTasks(string prompt)
    {
        Assert.True(Hints(prompt));
    }

    [Theory]
    [InlineData("Refactor the login component and add tests")]
    [InlineData("Fix the bug in the file picker")]
    [InlineData("Add a property to the DTO")]
    [InlineData("Search the repo for the add function and explain it")]
    [InlineData("Look at the existing tests and make them pass")]
    public void TaskHintsWebNeed_IgnoresPlainCodingTasks(string prompt)
    {
        Assert.False(Hints(prompt));
    }

    // The whole point of the LLM verification: noisy phrasing like "search for"
    // (which often means searching the REPO) must still OPEN the gate — the regex
    // hints broad, the LLM classifier is the real decider. These lock that contract.
    [Theory]
    [InlineData("search for the add function in the repo and explain it")]
    [InlineData("look up the config value in our codebase")]
    [InlineData("find out where the retry logic lives in this project")]
    public void TaskHintsWebNeed_OpensGateForNoisyRepoInternalPhrasing(string prompt)
    {
        Assert.True(Hints(prompt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TaskHintsWebNeed_BlankPromptsNeverHint(string? prompt)
    {
        Assert.False(Hints(prompt));
    }

    [Theory]
    [InlineData("_web_search", true)]
    [InlineData("_web_fetch", true)]
    [InlineData("_create_file", false)]
    [InlineData("_command", false)]
    [InlineData("_create_directory", false)]
    [InlineData(null, false)]
    public void IsWebStep_RecognizesWebMarkers(string? file, bool expected)
    {
        Assert.Equal(expected, IsWeb(file));
    }

    // ── WebNotNeededFeedback (rejection feedback for web steps on non-web tasks) ──

    private static string Constant(string name)
    {
        var field = typeof(Weaver.Controllers.AgentController)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(name + " constant not found.");
        return (string)(field.GetRawConstantValue() ?? "");
    }

    [Fact]
    public void WebNotNeededFeedback_ExistsAndIsDistinctFromWebNeedFeedback()
    {
        var notNeeded = Constant("WebNotNeededFeedback");
        var needed = Constant("WebNeedFeedback");
        Assert.False(string.IsNullOrWhiteSpace(notNeeded));
        Assert.NotEqual(needed, notNeeded);
    }

    [Fact]
    public void WebNotNeededFeedback_SteersAwayFromWebTools()
    {
        var fb = Constant("WebNotNeededFeedback");
        // The feedback must tell the model to drop the web step and work from repo context.
        Assert.Contains("does NOT need CURRENT EXTERNAL information", fb);
        Assert.Contains("_web_search/_web_fetch step is unnecessary", fb);
        Assert.Contains("DISCOVERY CONTEXT", fb);
    }

    // ── BuildFallbackWebQuery (query used by the auto-injected _web_search step) ──

    private static readonly MethodInfo FallbackQueryMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("BuildFallbackWebQuery", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildFallbackWebQuery static method not found.");

    private static string FallbackQuery(string? p) => (string)(FallbackQueryMethod.Invoke(null, new object?[] { p }) ?? "");

    [Fact]
    public void BuildFallbackWebQuery_FlattensPromptAndCollapsesWhitespace()
    {
        var q = FallbackQuery("Perform\n   three independent\r\n web searches\t today");
        Assert.Equal("Perform three independent web searches today", q);
    }

    [Fact]
    public void BuildFallbackWebQuery_TruncatesVeryLongPrompts()
    {
        var longPrompt = string.Concat(Enumerable.Repeat("search the web for current prices of gold silver copper ", 8)); // > 160 chars
        var q = FallbackQuery(longPrompt);
        Assert.True(q.Length <= 160, $"query should be truncated to <=160 chars, was {q.Length}");
        Assert.StartsWith("search the web for current prices", q);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildFallbackWebQuery_BlankPromptReturnsGenericQuery(string? prompt)
    {
        Assert.Equal("latest information", FallbackQuery(prompt));
    }
}
