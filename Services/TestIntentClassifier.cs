using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// The ONE "is this a live web-app test task?" classifier. A prompt whose intent is
/// STRICTLY to test some feature/functionality of the project's web server (as opposed
/// to editing it, searching the web, or writing scripts) is routed to the deterministic
/// live-test pipeline: spin up the project's own server, open it in a browser, navigate
/// to the section the prompt names, and report what is actually there.
///
/// The classifier is deliberately CONSERVATIVE and fully deterministic — it must work
/// with a very basic model, so nothing here requires intelligence: the same prompt
/// always classifies the same way, edit/web/script intent vetoes the test verdict, and
/// only clearly-test-shaped phrasing triggers.
///
/// Three outcomes:
/// <list type="bullet">
/// <item><see cref="Kind.Ui"/> — a UI test ("test the kanban board", "verify the calendar
/// page loads", "does the settings form save?"). The pipeline spins up the server and
/// drives a real browser to the section.</item>
/// <item><see cref="Kind.Api"/> — a test of a specific endpoint/route ("test the
/// /api/agent/llm-reachable endpoint", "check that POST api/foo returns 200"). The
/// pipeline calls the endpoint over HTTP and verifies the response.</item>
/// <item><see cref="Kind.None"/> — not a strict test task (edits, web research, scripts,
/// plain "run the tests" requests that mean unit tests, etc.). Normal planning continues.</item>
/// </list>
/// </summary>
public static class TestIntentClassifier
{
    public enum Kind { None, Ui, Api }

    /// <summary>The classification verdict for one prompt.</summary>
    public sealed record TestIntent(Kind Intent, string Target);

    // Test-shaping verbs. Word-boundary matched so "verify" inside "verifying" still
    // hits, but a bare noun "test" alone ("run the tests") never does — the verb must
    // clearly demand testing/checking an app feature.
    private static readonly string[] TestVerbs =
    {
        "test the", "test that", "test if", "test whether", "test out", "test it",
        "verify", "verify that", "make sure", "ensure that", "check that", "check if",
        "check whether", "check out", "does ", "does the", "does it", "can you test",
        "validate", "reproduce", "smoke test", "quality check", "qa the",
        "load test", "see if", "see whether", "try to", "works?", "working?",
        "is it working", "does it work", "does this work", "test the feature",
        "test the functionality", "test the page", "test the ui", "test the app",
        "test the website", "test the site", "test the web",
        "is the", "is a", "is an", "are the"
    };

    // Edit/script verbs that VETO the test verdict: the prompt wants to CHANGE the app,
    // not just test it. "fix the test", "add tests", "write a test script" etc. must
    // never route to the live-test pipeline.
    private static readonly string[] EditVetoVerbs =
    {
        "fix", "add", "create", "implement", "change", "update", "remove", "delete",
        "refactor", "rewrite", "write", "improve", "enhance", "style", "format",
        "migrate", "rename", "move", "build a", "build the", "generate", "insert",
        "edit", "modify", "repair", "patch", "extend", "replace"
    };

    /// <summary>
    /// Classifies a prompt. Returns <see cref="Kind.None"/> when the prompt is not a
    /// strict live-test task. The extracted <see cref="TestIntent.Target"/> is the
    /// feature/section the prompt names ("kanban board", "calendar page",
    /// "/api/agent/llm-reachable"), stripped of the test phrasing — used by discovery
    /// to find the section in the running app.
    /// </summary>
    public static TestIntent Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TestIntent(Kind.None, "");
        var t = Normalize(text);

        // Veto order: edits and web research first. A test-shaped phrase on top of an
        // edit ("fix the button and verify it") is NOT a strict test task.
        foreach (var veto in EditVetoVerbs)
            if (t.Contains(" " + veto + " ", StringComparison.Ordinal) ||
                t.StartsWith(veto + " ", StringComparison.Ordinal))
                return new TestIntent(Kind.None, "");

        // "run the tests" / "run the unit tests" / "run dotnet test" — the unit-test
        // harness, NOT the live app. Never treat as a web-server test.
        if (Regex.IsMatch(t, @"\b(run|execute)\b.{0,30}\btests?\b") ||
            t.Contains("dotnet test") || t.Contains("unit tests") || t.Contains("unit-test") ||
            t.Contains("test suite") || t.Contains("pytest") || t.Contains("jest") ||
            t.Contains("mocha") || t.Contains("xunit") || t.Contains("nunit") ||
            t.Contains("go test") || t.Contains("cargo test"))
            return new TestIntent(Kind.None, "");

        // Web-research veto: "verify the latest news …" needs CURRENT EXTERNAL data and
        // routes to the web tools, not the local app.
        if (WebNeedClassifier.IsWebNeed(text))
            return new TestIntent(Kind.None, "");

        var testVerbIndex = FindTestVerbIndex(t);
        if (testVerbIndex < 0) return new TestIntent(Kind.None, "");
        var matchedVerb = TestVerbs
            .Where(v => t.IndexOf(v, StringComparison.Ordinal) == testVerbIndex)
            .OrderByDescending(v => v.Length)
            .First();

        var rest = t[(testVerbIndex + matchedVerb.Length)..].Trim();
        // API tests: the target is a route/endpoint — strip the trailing test phrasing.
        var apiMatch = Regex.Match(rest, @"(?:\b(?:the|an)\s+)?(\/?api[\/\w\.\-]*|\/\w[\w\-\.\/]*endpoint\b|endpoint\s+\/?[\w\-\.\/]+)");
        if (Regex.IsMatch(rest, @"\b(api|endpoint|route|url|rest|http)\b"))
        {
            if (apiMatch.Success)
                return new TestIntent(Kind.Api, apiMatch.Groups[1].Value.Trim());
            return new TestIntent(Kind.Api, rest);
        }

        // UI tests: strip trailing test phrasing ("loads", "works", "renders", "opens",
        // "is there", "exists", "is working", "working", "has a", "shows", "displays").
        // "can be" is deliberately NOT stripped — "test the feature where cards can be
        // dragged" must keep the whole capability as the target.
        var target = Regex.Replace(rest,
            @"\b(loads?|renders?|works?|opens?|displays?|shows?|exists?|is there|is working|working|has a|has the|has|does it|can you|properly|correctly|fine|ok|okay|without errors?|as expected)\b.*$", "");
        target = Regex.Replace(target, @"\b(the|a|an|that|it|its|of|to|is|are|should|would|please|me)\b", " ");
        target = Regex.Replace(target, @"\s{2,}", " ").Trim();
        target = target.TrimEnd('?', '!', ':', ',', ';', '.').Trim();
        if (target.Length < 2) return new TestIntent(Kind.None, "");
        return new TestIntent(Kind.Ui, target);
    }

    /// <summary>True when the prompt is a strict live web-app test task (Ui or Api).</summary>
    public static bool IsTestIntent(string? text) => Classify(text).Intent != Kind.None;

    // ── Visual-inspection detection ─────────────────────────────────────────
    //
    // Prompts like "check my game for visual bugs", "verify visually", "does the nav bar
    // look right", or "screenshot the homepage" want the agent to SEE a rendered page —
    // but they don't always contain one of the strict test verbs above, so the
    // deterministic classifier returns None. This hint is the cheap gate for the LLM
    // classifier: when it fires, the LLM decides whether the request genuinely needs
    // visual inspection (and names the target). The hint is deliberately INCLUSIVE — a
    // false-positive hint just costs one tiny LLM call that answers "no", while a
    // false-negative hint would silently skip the visual pipeline entirely.
    private static readonly Regex VisualHintRegex = new(
        @"\b(visual|visually|screenshot|screenshots|pixel|pixels|appearance)\b|" +
        @"\blooks?\s+(right|wrong|ok|okay|good|bad|broken|correct|proper|fine|like)\b|" +
        @"\bwhat\s+does.{0,40}\blook\s+like\b|\bhow\s+does.{0,40}\blook\b",
        RegexOptions.Compiled);

    /// <summary>
    /// True when a prompt hints at needing to SEE a rendered page — visual bugs, layout,
    /// styling, screenshots, "does it look right" — even though it may not match a strict
    /// test verb. Used to gate the LLM-based visual-inspection classifier (so the LLM is
    /// only consulted when there is a real visual signal, never on every prompt).
    /// </summary>
    public static bool HasVisualInspectionHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return VisualHintRegex.IsMatch(Normalize(text));
    }

    /// <summary>
    /// True when the prompt asks to BUILD/EDIT/CHANGE something ("build a game", "create a
    /// folder", "fix the button", "write a server") — the deterministic counterpart of the
    /// visual-inspection gate. A build task must go through normal planning (build FIRST,
    /// test SECOND), so the visual short-circuit must NOT fire on it: otherwise
    /// "build a game and check it for visual bugs" would try to run the (not-yet-built)
    /// server before anything exists.
    /// </summary>
    public static bool HasEditIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = Normalize(text);
        foreach (var veto in EditVetoVerbs)
            if (t.Contains(" " + veto + " ", StringComparison.Ordinal) ||
                t.StartsWith(veto + " ", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Parses the LLM visual-inspection verdict JSON. Returns (needsVisual, target) with a
    /// clean (false, "") on malformed input, empty responses, or missing properties — the
    /// classifier must fail CLOSED (no visual inspection) when the model rambles or the
    /// JSON is broken.
    /// </summary>
    public static (bool NeedsVisual, string Target) ParseVisualVerdict(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (false, "");
        try
        {
            var cleaned = AgentJsonUtilities.ExtractFirstJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var needs = doc.RootElement.TryGetProperty("needsVisual", out var v) &&
                        v.ValueKind == JsonValueKind.True;
            var target = "";
            if (doc.RootElement.TryGetProperty("target", out var t) &&
                t.ValueKind == JsonValueKind.String)
                target = t.GetString() ?? "";
            return (needs, target.Trim());
        }
        catch { return (false, ""); }
    }

    /// <summary>Lowercases, collapses whitespace, and strips punctuation so phrase
    /// matching is stable across prompt formatting.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c is '?' or '!' or ',' or ';' or ':' or '.' or '"' or '\'')
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
            pendingSpace = false;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Index of the first test-verb phrase in the normalized text, or -1.</summary>
    private static int FindTestVerbIndex(string t)
    {
        var best = -1;
        foreach (var verb in TestVerbs)
        {
            var idx = t.IndexOf(verb, StringComparison.Ordinal);
            if (idx < 0) continue;
            // Prefer the earliest (and longest) match so "check that the page loads"
            // beats a stray later "does it".
            if (best < 0 || idx < best)
                best = idx;
        }
        return best;
    }
}