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

    // ── TaskExplicitlyCommandsWeb (decisive explicit web-search commands) ──

    private static readonly MethodInfo ExplicitWebMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("TaskExplicitlyCommandsWeb", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("TaskExplicitlyCommandsWeb static method not found.");

    private static bool ExplicitWeb(string? p) => (bool)(ExplicitWebMethod.Invoke(null, new object?[] { p }) ?? false);

    // A direct web-search command is AUTHORITATIVE — the classifier may not veto it.
    // These are the exact phrasings that must bypass the LLM verification entirely.
    [Theory]
    [InlineData("Search the web for an interesting and relevant AI article and write the data into a text file on my desktop")]
    [InlineData("search the internet for the latest npm versions and save them to a file")]
    [InlineData("look up the current exchange rates online")]
    [InlineData("fetch the current weather from the web")]
    [InlineData("browse the web for job postings and summarize them")]
    [InlineData("find an interesting AI article online and write it to a file")]
    [InlineData("google for the latest AI news online")]
    [InlineData("do a web search for recent advances in machine learning")]
    public void TaskExplicitlyCommandsWeb_DetectsDirectWebSearchCommands(string prompt)
    {
        Assert.True(ExplicitWeb(prompt));
    }

    // Noisy phrasing that usually means searching the REPO must stay with the LLM
    // classifier — only the classifier decides those, never the deterministic regex.
    [Theory]
    [InlineData("search for the add function in the repo and explain it")]
    [InlineData("look up the config value in our codebase")]
    [InlineData("find out where the retry logic lives in this project")]
    [InlineData("Refactor the login component and add tests")]
    [InlineData("Use the Google Maps API to geocode addresses")]
    public void TaskExplicitlyCommandsWeb_LeavesRepoInternalPhrasingToTheClassifier(string prompt)
    {
        Assert.False(ExplicitWeb(prompt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TaskExplicitlyCommandsWeb_BlankPromptsNeverFire(string? prompt)
    {
        Assert.False(ExplicitWeb(prompt));
    }

    // ── TryParseWebNeedVerdict (classifier JSON parsing + reason capture) ──

    private static readonly MethodInfo ParseVerdictMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("TryParseWebNeedVerdict", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryParseWebNeedVerdict static method not found.");

    private static (bool Ok, bool NeedsWeb, string? Query, string Reason) ParseVerdict(string json)
    {
        var args = new object?[] { json, null, null, null };
        var ok = (bool)(ParseVerdictMethod.Invoke(null, args) ?? false);
        return (ok, (bool)(args[1] ?? false), (string?)args[2], (string)(args[3] ?? ""));
    }

    [Fact]
    public void TryParseWebNeedVerdict_ParsesWebTaskWithQuery()
    {
        var (ok, needsWeb, query, reason) = ParseVerdict(
            "{\"needsWeb\": true, \"reason\": \"task asks for live data\", \"query\": \"latest npm version of lodash\"}");
        Assert.True(ok);
        Assert.True(needsWeb);
        Assert.Equal("latest npm version of lodash", query);
        Assert.Equal("task asks for live data", reason);
    }

    // The exact failure mode from the "Search the web for an AI article" run: the
    // classifier vetoes with a reason. The reason must come back so the agent panel
    // can surface it instead of showing only the generic rejection line.
    [Fact]
    public void TryParseWebNeedVerdict_ParsesNoWebVetoWithReason()
    {
        var (ok, needsWeb, query, reason) = ParseVerdict(
            "{\"needsWeb\": false, \"reason\": \"the article content can be written from the model's own knowledge\", \"query\": \"\"}");
        Assert.True(ok);
        Assert.False(needsWeb);
        Assert.Null(query);
        Assert.Contains("own knowledge", reason);
    }

    [Fact]
    public void TryParseWebNeedVerdict_MissingReasonDefaultsEmpty()
    {
        var (ok, _, _, reason) = ParseVerdict("{\"needsWeb\": false}");
        Assert.True(ok);
        Assert.Equal("", reason);
    }

    [Fact]
    public void TryParseWebNeedVerdict_MalformedJsonFails()
    {
        var (ok, _, _, _) = ParseVerdict("{\"needsWeb\": tru");
        Assert.False(ok);
    }

    [Fact]
    public void TryParseWebNeedVerdict_NonObjectRootFails()
    {
        var (ok, _, _, _) = ParseVerdict("[1,2,3]");
        Assert.False(ok);
    }

    [Fact]
    public void TryParseWebNeedVerdict_BlankOrEmptyJsonFails()
    {
        Assert.False(ParseVerdict("").Ok);
        Assert.False(ParseVerdict("   ").Ok);
    }

    // ── ShouldRejectFetchCommand (scoped fetch-in-command guard) ──
    // The guard fires ONLY on tasks that hint at needing web data, so a bare legit
    // `curl https://…/health` on a non-web task stays allowed as a real command.

    private static readonly MethodInfo RejectFetchMethod = typeof(Weaver.Controllers.AgentController)
        .GetMethod("ShouldRejectFetchCommand", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("ShouldRejectFetchCommand static method not found.");

    private static bool RejectFetch(string? prompt, string? command) =>
        (bool)(RejectFetchMethod.Invoke(null, new object?[] { prompt, command }) ?? false);

    [Fact]
    public void ShouldRejectFetchCommand_RejectsOnWebHintingTask()
    {
        const string prompt = "Search the web for an interesting and relevant AI article and write the data into a text file on my desktop";
        const string command = "Invoke-RestMethod https://api.current.ai/articles | Select-Object title, summary, url | ConvertTo-Csv -NoTypeInformation | Set-Content \"C:\\Users\\Saint\\Desktop\\ai_article_data.txt\"";
        Assert.True(RejectFetch(prompt, command));
    }

    [Fact]
    public void ShouldRejectFetchCommand_AllowsCurlHealthCheckOnNonWebTask()
    {
        Assert.False(RejectFetch("Check the uptime of the local service and report the response code", "curl https://my-server/health"));
    }

    [Fact]
    public void ShouldRejectFetchCommand_AllowsFetchCommandOnNonWebTask()
    {
        // The api.current.ai-style command on a task with NO web hint is a legit terminal
        // command (the relaxation: the guard only polices web-needing tasks).
        Assert.False(RejectFetch("Run the build and fix the failing tests", "Invoke-RestMethod https://api.current.ai/articles | Out-File out.json"));
    }

    [Fact]
    public void ShouldRejectFetchCommand_AllowsLegitUrlCommandsOnWebTask()
    {
        // Detector alone says no (git clone is not a content fetch) — hint doesn't matter.
        Assert.False(RejectFetch("search the web for the latest release and clone it", "git clone https://github.com/foo/bar.git"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldRejectFetchCommand_BlankPromptsNeverReject(string? prompt)
    {
        Assert.False(RejectFetch(prompt, "curl https://example.com/data.json"));
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
