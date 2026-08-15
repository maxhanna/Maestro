using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks TestIntentClassifier — the gate that decides whether a prompt is STRICTLY a
/// live web-app test task ("test the kanban board", "verify the calendar page loads")
/// and routes it to the deterministic live-test pipeline (spin up the project's server,
/// open it in a browser, verify the named feature). The classifier must be conservative:
/// edit intent, web-research intent, and "run the tests" (unit-test) intent must never
/// hijack the live-test pipeline. Everything is word-boundary + phrase matching — no
/// model intelligence, so any model routes identically.
/// </summary>
public class TestIntentClassifierTests
{
    // ── UI test intents ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("test the kanban board", "kanban board")]
    [InlineData("Test the Kanban board please", "kanban board")]
    [InlineData("verify the calendar page loads", "calendar page")]
    [InlineData("verify that the settings page opens", "settings page")]
    [InlineData("check that the login form works", "login form")]
    [InlineData("check if the save button exists", "save button")]
    [InlineData("make sure the kanban board renders", "kanban board")]
    [InlineData("ensure that the notes page displays", "notes page")]
    [InlineData("does the todo list work?", "todo list")]
    [InlineData("is the agent panel working", "agent panel")]
    [InlineData("test the ui of the ide page", "ide page")]
    [InlineData("smoke test the calendar", "calendar")]
    [InlineData("validate the file tree loads", "file tree")]
    [InlineData("test the feature where cards can be dragged", "where cards can be dragged")]
    public void Classify_UiTestPhrasing_ReturnsUi(string prompt, string expectedTarget)
    {
        var result = TestIntentClassifier.Classify(prompt);
        Assert.Equal(TestIntentClassifier.Kind.Ui, result.Intent);
        Assert.Equal(expectedTarget, result.Target);
    }

    // ── API test intents ────────────────────────────────────────────────────

    [Theory]
    [InlineData("test the api/agent/llm-reachable endpoint")]
    [InlineData("verify that /api/benchmark/plans returns 200")]
    [InlineData("check if the api endpoint /api/config works")]
    [InlineData("test the rest api at /api/notes")]
    public void Classify_ApiTestPhrasing_ReturnsApi(string prompt)
    {
        var result = TestIntentClassifier.Classify(prompt);
        Assert.Equal(TestIntentClassifier.Kind.Api, result.Intent);
        Assert.False(string.IsNullOrWhiteSpace(result.Target));
        Assert.Contains("api", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    // ── Non-test intents (must NOT route to the live-test pipeline) ─────────

    [Theory]
    [InlineData("fix the kanban board")]
    [InlineData("add a button to the calendar page")]
    [InlineData("implement the login form")]
    [InlineData("change the settings page styling")]
    [InlineData("write a python script that tests the api")]
    [InlineData("create a unit test for the service")]
    [InlineData("run the tests")]
    [InlineData("run dotnet test")]
    [InlineData("run the unit tests")]
    [InlineData("add tests to the project")]
    [InlineData("search the web for the latest news")]
    [InlineData("look up the calendar api docs")]
    [InlineData("fetch a recent ai article and write it to a file on my desktop")]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("test the api of the stock market and write results to desktop")]
    public void Classify_NonTestIntent_ReturnsNone(string prompt)
    {
        Assert.Equal(TestIntentClassifier.Kind.None, TestIntentClassifier.Classify(prompt).Intent);
    }

    [Fact]
    public void Classify_EditPlusTestMixed_ReturnsNone()
    {
        // Not STRICTLY a test: the prompt demands an edit too.
        Assert.Equal(TestIntentClassifier.Kind.None,
            TestIntentClassifier.Classify("fix the broken button and verify it works").Intent);
    }

    [Fact]
    public void Classify_WebHint_ReturnsNone()
    {
        // "verify the latest …" needs CURRENT EXTERNAL data → web pipeline, not live test.
        Assert.Equal(TestIntentClassifier.Kind.None,
            TestIntentClassifier.Classify("verify the latest weather for montreal").Intent);
    }

    // ── Determinism / normalization ─────────────────────────────────────────

    [Fact]
    public void Classify_NormalizesCaseAndWhitespace()
    {
        var a = TestIntentClassifier.Classify("  TEST   the   KANBAN   board  ");
        var b = TestIntentClassifier.Classify("test the kanban board");
        Assert.Equal(a.Intent, b.Intent);
        Assert.Equal(a.Target, b.Target);
        Assert.Equal(TestIntentClassifier.Kind.Ui, a.Intent);
    }

    [Fact]
    public void Classify_NullAndBlank_ReturnsNone()
    {
        Assert.Equal(TestIntentClassifier.Kind.None, TestIntentClassifier.Classify(null).Intent);
        Assert.Equal(TestIntentClassifier.Kind.None, TestIntentClassifier.Classify("   ").Intent);
    }

    [Fact]
    public void Classify_IsTestIntentHelper()
    {
        Assert.True(TestIntentClassifier.IsTestIntent("test the kanban board"));
        Assert.False(TestIntentClassifier.IsTestIntent("fix the kanban board"));
    }

    [Fact]
    public void Classify_UnitTestToolingNeverRoutes()
    {
        foreach (var p in new[] { "run pytest", "execute jest", "cargo test", "go test ./..." })
            Assert.Equal(TestIntentClassifier.Kind.None, TestIntentClassifier.Classify(p).Intent);
    }

    // ── Visual-inspection hints (gate the LLM visual classifier) ────────────

    [Theory]
    [InlineData("check my game for visual bugs")]
    [InlineData("verify visually")]
    [InlineData("screenshot the homepage")]
    [InlineData("does the nav bar look right")]
    [InlineData("what does the settings page look like")]
    [InlineData("check the page for visual bugs")]
    [InlineData("how does the landing page look")]
    [InlineData("the button looks broken on the game")]
    public void HasVisualInspectionHint_VisualPhrasing_ReturnsTrue(string prompt)
    {
        Assert.True(TestIntentClassifier.HasVisualInspectionHint(prompt));
    }

    [Theory]
    [InlineData("fix the kanban board")]
    [InlineData("add a button to the calendar page")]
    [InlineData("look up the calendar api docs")]
    [InlineData("run the tests")]
    [InlineData("search the web for the latest news")]
    [InlineData("write a python script that tests the api")]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData(null)]
    public void HasVisualInspectionHint_NonVisual_ReturnsFalse(string? prompt)
    {
        Assert.False(TestIntentClassifier.HasVisualInspectionHint(prompt));
    }

    // The canonical case: a visual-bug prompt has NO strict test verb, so the
    // deterministic classifier stays conservative (None) while the visual hint fires —
    // that gap is exactly what the LLM-based classifier closes.
    [Fact]
    public void Classify_VisualBugPhrasing_NoneButHintsVisual()
    {
        Assert.Equal(TestIntentClassifier.Kind.None,
            TestIntentClassifier.Classify("check my game for visual bugs").Intent);
        Assert.True(TestIntentClassifier.HasVisualInspectionHint("check my game for visual bugs"));
    }

    // "verify visually" contains a strict verb, so it routes deterministically (free).
    [Fact]
    public void Classify_VerifyVisually_ReturnsUi()
    {
        Assert.Equal(TestIntentClassifier.Kind.Ui, TestIntentClassifier.Classify("verify visually").Intent);
    }

    // ── LLM visual verdict parsing ───────────────────────────────────────────

    [Fact]
    public void ParseVisualVerdict_TrueWithTarget()
    {
        var (needs, target) = TestIntentClassifier.ParseVisualVerdict(
            "{\"needsVisual\": true, \"target\": \"the game\"}");
        Assert.True(needs);
        Assert.Equal("the game", target);
    }

    [Fact]
    public void ParseVisualVerdict_False()
    {
        var (needs, target) = TestIntentClassifier.ParseVisualVerdict(
            "{\"needsVisual\": false, \"target\": \"\"}");
        Assert.False(needs);
        Assert.Equal("", target);
    }

    [Fact]
    public void ParseVisualVerdict_ProseWrappedJson()
    {
        var (needs, target) = TestIntentClassifier.ParseVisualVerdict(
            "Here is my answer: {\"needsVisual\": true, \"target\": \"the nav bar\"} done.");
        Assert.True(needs);
        Assert.Equal("the nav bar", target);
    }

    // ── Edit-intent veto (build tasks must plan the build, then test) ──────────

    [Theory]
    [InlineData("build a small web game and check it for visual bugs")]
    [InlineData("Create a folder called benchmark_test_22 at the project root")]
    [InlineData("write a python server that serves index.html")]
    [InlineData("fix the button and verify it renders")]
    [InlineData("add a column to the csv")]
    public void HasEditIntent_BuildOrEditPhrasing_ReturnsTrue(string prompt)
    {
        Assert.True(TestIntentClassifier.HasEditIntent(prompt));
    }

    [Theory]
    [InlineData("check my game for visual bugs")]
    [InlineData("verify visually")]
    [InlineData("does the nav bar look right")]
    [InlineData("screenshot the homepage")]
    [InlineData("test the kanban board")]
    public void HasEditIntent_PureInspection_ReturnsFalse(string prompt)
    {
        Assert.False(TestIntentClassifier.HasEditIntent(prompt));
    }

    // The benchmark 22 regression: a build task that ALSO mentions visual bugs must NOT
    // short-circuit to the live web test — it must plan the build FIRST and test SECOND.
    [Fact]
    public void BuildPromptWithVisualHint_DoesNotShortCircuit()
    {
        const string prompt = "Create a folder called 'benchmark_test_22' at the project root. " +
                              "Inside it, build a small web game and then check it for visual bugs.";
        Assert.Equal(TestIntentClassifier.Kind.None, TestIntentClassifier.Classify(prompt).Intent);
        Assert.True(TestIntentClassifier.HasVisualInspectionHint(prompt));
        Assert.True(TestIntentClassifier.HasEditIntent(prompt));
    }

    [Fact]
    public void ParseVisualVerdict_MalformedFailsClosed()
    {
        Assert.False(TestIntentClassifier.ParseVisualVerdict("not json at all").NeedsVisual);
        Assert.False(TestIntentClassifier.ParseVisualVerdict(null).NeedsVisual);
        Assert.False(TestIntentClassifier.ParseVisualVerdict("").NeedsVisual);
        Assert.False(TestIntentClassifier.ParseVisualVerdict("{\"target\": \"missing needsVisual\"}").NeedsVisual);
    }
}