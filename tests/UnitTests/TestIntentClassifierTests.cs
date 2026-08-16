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
        Assert.False(TestIntentClassifier.DemandsLiveBrowserTest(prompt));
    }

    // ── Explicit browser-test demand (benchmark 23) ──────────────────────────

    // The benchmark-23 failure: the prompt EXPLICITLY names the live browser test as a
    // requirement, but the LLM visual classifier read the dominant build/implement language
    // and answered needsVisual=false, so the browser test was never injected. A prompt that
    // NAMES the browser test must be recognized deterministically so the classifier's false
    // cannot veto the injection.
    [Theory]
    [InlineData("Use the live browser test to spin up the game and visually confirm the spider has 4 legs")]
    [InlineData("The benchmark passes only if the leg count confirmed by the browser test goes from 4 to 6")]
    [InlineData("check it for visual bugs with the live browser test suite")]
    [InlineData("reload the server, run the browser test again, and confirm 6 legs")]
    [InlineData("use _browser_test to verify the page")]
    [InlineData("run the live web test against the homepage")]
    public void DemandsLiveBrowserTest_ExplicitPhrasing_ReturnsTrue(string prompt)
    {
        Assert.True(TestIntentClassifier.DemandsLiveBrowserTest(prompt));
    }

    [Theory]
    [InlineData("style the button's visual appearance")]
    [InlineData("check my game for visual bugs")]
    [InlineData("verify visually")]
    [InlineData("run the tests")]
    [InlineData("screenshot the homepage")]
    [InlineData("write a unit test for the service")]
    [InlineData("build a small web game and then check it for visual bugs")]
    public void DemandsLiveBrowserTest_NonExplicitPhrasing_ReturnsFalse(string prompt)
    {
        Assert.False(TestIntentClassifier.DemandsLiveBrowserTest(prompt));
    }

    // The gate-bypass contract: a build task that ALSO explicitly demands the browser test
    // fires BOTH the inclusive visual hint AND the explicit demand (so the gates inject
    // deterministically, no classifier consulted), while a pure styling hint fires the hint
    // but NOT the explicit demand (so the classifier still runs and its false is honored).
    [Fact]
    public void ExplicitBrowserTestDemand_FiresHintAndDemand()
    {
        const string prompt = "Create a folder called 'benchmark_test_23' at the project root. Inside it, build a " +
                              "small web app that draws an animated spider on a canvas, then use the live browser test to " +
                              "visually confirm the spider has 4 legs, then add 2 more legs so it has 6. The benchmark passes " +
                              "only if the leg count confirmed by the browser test goes from 4 to 6.";
        Assert.True(TestIntentClassifier.HasVisualInspectionHint(prompt));
        Assert.True(TestIntentClassifier.HasEditIntent(prompt));
        Assert.True(TestIntentClassifier.DemandsLiveBrowserTest(prompt));
    }

    // The benchmark-23 regression: an UNAVAILABLE classifier (empty/malformed reply,
    // transport error, LLM down) must FAIL OPEN — the deterministic hint has already
    // fired, so the gate injects the live browser test instead of silently skipping it
    // (the observed halt: step-5 planner response cut off, then the visual classifier
    // also failed closed, and the run ended with _browser_test never executed).
    [Fact]
    public void ParseVisualVerdict_MalformedIsUnavailable_NotConfidentNo()
    {
        Assert.Null(TestIntentClassifier.ParseVisualVerdict("not json at all").NeedsVisual);
        Assert.Null(TestIntentClassifier.ParseVisualVerdict(null).NeedsVisual);
        Assert.Null(TestIntentClassifier.ParseVisualVerdict("").NeedsVisual);
        Assert.Null(TestIntentClassifier.ParseVisualVerdict("{\"target\": \"missing needsVisual\"}").NeedsVisual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void ShouldInjectVisualBrowserTest_TrueOrUnavailable_ReturnsTrue(bool? classifierNeedsVisual)
    {
        // Confident "yes" AND unavailable (null) both inject: the deterministic hint
        // already fired, so a flaky classifier must not drop the required browser test.
        Assert.True(TestIntentClassifier.ShouldInjectVisualBrowserTest(classifierNeedsVisual));
    }

    [Fact]
    public void ShouldInjectVisualBrowserTest_ConfidentNo_ReturnsFalse()
    {
        // Only a CONFIDENT "needsVisual:false" produced by a working classifier vetoes.
        Assert.False(TestIntentClassifier.ShouldInjectVisualBrowserTest(false));
    }

    // ── window.<name> state-global expectation extraction ───────────────────

    [Theory]
    [InlineData("window.legCount must equal 6", "legCount", "6")]
    [InlineData("the page must expose window.legCount = 6", "legCount", "6")]
    [InlineData("window.legCount === 6", "legCount", "6")]
    [InlineData("window.legCount == 6", "legCount", "6")]
    [InlineData("window.legCount should be 12", "legCount", "12")]
    [InlineData("window.score needs to equal 0", "score", "0")]
    public void ExtractWindowStateExpectation_ExplicitValue_ReturnsIt(string prompt, string name, string value)
    {
        var e = TestIntentClassifier.ExtractWindowStateExpectation(prompt);
        Assert.NotNull(e);
        Assert.Equal(name, e!.Name);
        Assert.Equal(value, e.ExpectedValue);
    }

    [Fact]
    public void ExtractWindowStateExpectation_LastMatchWins_FinalRequiredValue()
    {
        // A multi-phase prompt that walks the value up must resolve to the FINAL requirement.
        const string prompt = "Start with window.legCount = 4 legs, then window.legCount must equal 6.";
        var e = TestIntentClassifier.ExtractWindowStateExpectation(prompt);
        Assert.NotNull(e);
        Assert.Equal("legCount", e!.Name);
        Assert.Equal("6", e.ExpectedValue);
    }

    [Fact]
    public void ExtractWindowStateExpectation_BareLegCountNoValue_ReturnsNull()
    {
        // "EXACTLY 4 legs" with no window.name adjacency must not produce a false expectation.
        Assert.Null(TestIntentClassifier.ExtractWindowStateExpectation(
            "Start by drawing EXACTLY 4 legs. The page MUST expose window.legCount."));
        Assert.Null(TestIntentClassifier.ExtractWindowStateExpectation("fix the button"));
        Assert.Null(TestIntentClassifier.ExtractWindowStateExpectation(null));
    }
}