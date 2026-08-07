using System.Reflection;
using System.Text.Json;
using Xunit;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for <c>AgentController.ParseVerifyIssues</c> — the classifier that splits the
/// post-execution verifier's 'issues' array into CONFIRMED (actionable, drives repair
/// steps) and SPECULATIVE (hypothetical risks, logged but never repaired) buckets.
/// Invoked via reflection because the method is private static, mirroring the pattern
/// used by FormatDPayloadCorpusTests.InvokeHasConcreteEdit. The triage rules live in
/// Services/VerifierIssueTriage.cs and are called directly.
/// </summary>
public class PostExecuteVerifyTests
{
    private static (List<string> confirmed, List<string> speculative) InvokeParseVerifyIssues(string issuesJson)
    {
        var method = typeof(AgentController).GetMethod(
            "ParseVerifyIssues", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ParseVerifyIssues not found");
        using var doc = JsonDocument.Parse(issuesJson);
        var result = (ValueTuple<List<string>, List<string>>)(method.Invoke(null, new object?[] { doc.RootElement })!);
        return (result.Item1, result.Item2);
    }

    // ── Classification ──────────────────────────────────────────────────────

    [Fact]
    public void ParseVerifyIssues_TypedItems_SplitsConfirmedAndSpeculative()
    {
        const string json = """
        {
          "issues": [
            { "type": "CONFIRMED", "text": "The centerCurrentLocation method is missing from GlobeComponent" },
            { "type": "SPECULATIVE", "text": "globeComponent might not be initialized during the initial render cycle" }
          ]
        }
        """;
        var (confirmed, speculative) = InvokeParseVerifyIssues(json);
        Assert.Single(confirmed);
        Assert.Single(speculative);
        Assert.Contains("centerCurrentLocation method is missing", confirmed[0]);
        Assert.Contains("might not be initialized", speculative[0]);
    }

    [Fact]
    public void ParseVerifyIssues_MixedTypes_PreservesOrderAndSeparates()
    {
        const string json = """
        {
          "issues": [
            { "type": "CONFIRMED", "text": "first" },
            { "type": "SPECULATIVE", "text": "second" },
            { "type": "CONFIRMED", "text": "third" },
            { "type": "SPECULATIVE", "text": "fourth" }
          ]
        }
        """;
        var (confirmed, speculative) = InvokeParseVerifyIssues(json);
        Assert.Equal(new[] { "first", "third" }, confirmed);
        Assert.Equal(new[] { "second", "fourth" }, speculative);
    }

    [Fact]
    public void ParseVerifyIssues_TypeCaseInsensitive_SpeculativeLowercaseIsStillSpeculative()
    {
        const string json = """
        { "issues": [ { "type": "speculative", "text": "could cause a runtime error" } ] }
        """;
        var (confirmed, speculative) = InvokeParseVerifyIssues(json);
        Assert.Empty(confirmed);
        Assert.Single(speculative);
    }

    // ── Backward compatibility ──────────────────────────────────────────────

    [Fact]
    public void ParseVerifyIssues_PlainStringItems_DefaultToConfirmed()
    {
        // Legacy verifier output used plain strings — those must remain actionable.
        const string json = """{ "issues": ["old broken reference", "missing import"] }""";
        var (confirmed, speculative) = InvokeParseVerifyIssues(json);
        Assert.Equal(2, confirmed.Count);
        Assert.Empty(speculative);
    }

    [Fact]
    public void ParseVerifyIssues_MissingType_DefaultsToConfirmed()
    {
        const string json = """{ "issues": [ { "text": "no type given" } ] }""";
        var (confirmed, speculative) = InvokeParseVerifyIssues(json);
        Assert.Single(confirmed);
        Assert.Empty(speculative);
    }

    // ── Robustness ──────────────────────────────────────────────────────────

    [Fact]
    public void ParseVerifyIssues_MixedObjectAndString_HandlesBoth()
    {
        const string json = """
        { "issues": [ { "type": "CONFIRMED", "text": "object issue" }, "legacy string" ] }
        """;
        var (confirmed, speculative) = InvokeParseVerifyIssues(json);
        Assert.Equal(2, confirmed.Count);
        Assert.Empty(speculative);
    }

    [Fact]
    public void ParseVerifyIssues_EmptyOrMissingIssues_ReturnsEmptyLists()
    {
        var (a, b) = InvokeParseVerifyIssues("""{ "issues": [] }""");
        Assert.Empty(a);
        Assert.Empty(b);

        var (c, d) = InvokeParseVerifyIssues("""{ "complete": true, "reason": "done" }""");
        Assert.Empty(c);
        Assert.Empty(d);

        var (e, f) = InvokeParseVerifyIssues("""{}""");
        Assert.Empty(e);
        Assert.Empty(f);
    }

    [Fact]
    public void ParseVerifyIssues_EmptyTextItem_Skipped()
    {
        const string json = """
        { "issues": [ { "type": "CONFIRMED", "text": "  " }, { "type": "SPECULATIVE", "text": "" } ] }
        """;
        var (confirmed, speculative) = InvokeParseVerifyIssues(json);
        Assert.Empty(confirmed);
        Assert.Empty(speculative);
    }

    [Fact]
    public void ParseVerifyIssues_NonArrayValue_ReturnsEmptyLists()
    {
        var (a, b) = InvokeParseVerifyIssues("""{ "issues": "not an array" }""");
        Assert.Empty(a);
        Assert.Empty(b);
    }

    // ── Triage: phantom claims ──────────────────────────────────────────────

    private static (bool keep, string reason) InvokeTriageVerifierIssue(
        string issue, Dictionary<string, string> filesByPath)
        => VerifierIssueTriage.TriageVerifierIssue(issue, filesByPath);

    [Fact]
    public void Triage_PhantomClaim_SymbolPresent_Drops()
    {
        // Mirrors the sig-int failure: verifier claimed centerCurrentLocation was missing
        // while the method physically exists in the file.
        var files = new Dictionary<string, string>
        {
            ["globe/globe.component.ts"] =
                "export class GlobeComponent { centerCurrentLocation() { this.rotate(); } }"
        };
        var (keep, reason) = InvokeTriageVerifierIssue(
            "'centerCurrentLocation()' not found at expected location in GlobeComponent", files);
        Assert.False(keep);
        Assert.Contains("phantom", reason);
        Assert.Contains("centerCurrentLocation", reason);
    }

    [Fact]
    public void Triage_PhantomClaim_SymbolGenuinelyMissing_Keeps()
    {
        // centerCurrentLocation is genuinely absent from the file. The bare class name
        // GlobeComponent appearing in the file must NOT excuse the phantom check — only
        // high-confidence symbols (backticked/qualified/method-call) count as evidence.
        var files = new Dictionary<string, string>
        {
            ["globe/globe.component.ts"] = "export class GlobeComponent { rotate() {} }"
        };
        var (keep, _) = InvokeTriageVerifierIssue(
            "The method centerCurrentLocation is missing from GlobeComponent", files);
        Assert.True(keep);
    }

    [Fact]
    public void Triage_PhantomClaim_BareTokenOnly_DoesNotDrop_WhenSymbolAbsent()
    {
        // Even though 'GlobeComponent' (a bare token) is present, the issue names no
        // high-confidence symbol that exists — the claim stays actionable.
        var files = new Dictionary<string, string>
        {
            ["globe/globe.component.ts"] = "export class GlobeComponent { rotate() {} }"
        };
        var (keep, _) = InvokeTriageVerifierIssue(
            "The method centerCurrentLocation is missing from GlobeComponent", files);
        Assert.True(keep);
    }

    [Fact]
    public void Triage_PhantomClaim_BacktickedSymbol_Present_Drops()
    {
        var files = new Dictionary<string, string>
        {
            ["app.ts"] = "function saveCards() {}"
        };
        var (keep, reason) = InvokeTriageVerifierIssue(
            "`saveCards` is not defined anywhere in the file", files);
        Assert.False(keep);
        Assert.Contains("phantom", reason);
    }

    // ── Triage: 'X should be Y' already-resolved renames ────────────────────

    [Fact]
    public void Triage_ShouldBeRename_OldSymbolGone_NewSymbolPresent_Drops()
    {
        // Mirrors the benchmark case: the verifier re-claims 'do_get should be do_GET' AFTER the
        // fix landed — do_get is gone from the file and do_GET exists, so the issue was re-issued
        // from stale/historical text and must not burn a repair pass re-fixing it.
        var files = new Dictionary<string, string>
        {
            ["benchmark_test_4/server.py"] =
                "class MyHandler(http.server.SimpleHTTPRequestHandler):\n" +
                "    def do_GET(self):\n" +
                "        return http.server.SimpleHTTPRequestHandler.do_GET(self)\n"
        };
        var (keep, reason) = InvokeTriageVerifierIssue(
            "'do_get(self)' should be 'do_GET(self)' according to HTTP request handler conventions.", files);
        Assert.False(keep);
        Assert.Contains("already resolved", reason);
        Assert.Contains("do_GET", reason);
    }

    [Fact]
    public void Triage_ShouldBeRename_WrongSymbolStillPresent_Keeps()
    {
        // do_get is STILL in the file (the rename has not happened) — the issue is genuine and
        // must stay actionable so the repair loop actually fixes it.
        var files = new Dictionary<string, string>
        {
            ["benchmark_test_4/server.py"] =
                "class MyHandler(http.server.SimpleHTTPRequestHandler):\n" +
                "    def do_GET(self):\n" +
                "        return http.server.SimpleHTTPRequestHandler.do_get(self)\n"
        };
        var (keep, _) = InvokeTriageVerifierIssue(
            "'do_get(self)' should be 'do_GET(self)' according to HTTP request handler conventions.", files);
        Assert.True(keep);
    }

    [Fact]
    public void Triage_ShouldBeRename_NeitherSymbolPresent_FallsThroughAndKeeps()
    {
        // Neither the claimed-wrong nor the corrected symbol exists — not an 'already resolved'
        // rename (no evidence the fix landed). No other rule fires, so it stays actionable.
        var files = new Dictionary<string, string>
        {
            ["app.ts"] = "function loadCards() {}\n"
        };
        var (keep, _) = InvokeTriageVerifierIssue(
            "'getEventIcon' should be 'getEventData' in user-events.component.ts", files);
        Assert.True(keep);
    }

    // ── Triage: event-gated reachability ────────────────────────────────────

    [Fact]
    public void Triage_ViewChildConcern_SymbolOnlyInClickHandler_Drops()
    {
        // The user's exact case: a ViewChild referenced only from a (click) handler is
        // inherently safe at click time — no repair should be forced.
        var files = new Dictionary<string, string>
        {
            ["sig-int/sig-int.component.html"] =
                "<button (click)=\"globeComponent.centerCurrentLocation(); closeMenuPanel()\">Center</button>"
        };
        var (keep, reason) = InvokeTriageVerifierIssue(
            "globeComponent might not be initialized during the initial render cycle", files);
        Assert.False(keep);
        Assert.Contains("event-gated", reason);
    }

    [Fact]
    public void Triage_ViewChildConcern_SymbolAlsoUsedInNonEventContext_Keeps()
    {
        // globeComponent is ALSO referenced outside any event handler (e.g. in the template
        // body directly), so the event-gate check must NOT excuse the initialization concern.
        // Non-hedged wording keeps this in the event-gate path (speculative-wording is covered
        // by the other tests).
        var files = new Dictionary<string, string>
        {
            ["sig-int/sig-int.component.html"] =
                "<p>{{globeComponent.status}}</p>\n<button (click)=\"globeComponent.go()\">Go</button>"
        };
        var (keep, _) = InvokeTriageVerifierIssue(
            "globeComponent is not initialized during the initial render cycle", files);
        Assert.True(keep);
    }

    [Fact]
    public void Triage_NoHtmlFiles_TimingConcernFallsThroughToWordingCheck()
    {
        var files = new Dictionary<string, string>
        {
            ["globe/globe.component.ts"] = "export class GlobeComponent {}"
        };
        var (keep, reason) = InvokeTriageVerifierIssue(
            "globeComponent might not be initialized during the initial render cycle", files);
        // No HTML file → no event-gate evidence → hedge wording drops it as speculative.
        Assert.False(keep);
        Assert.Contains("speculative", reason);
    }

    // ── Triage: speculative wording ─────────────────────────────────────────

    [Theory]
    [InlineData("The view child could be null at runtime")]
    [InlineData("This change may cause a regression")]
    [InlineData("possibly breaks the existing flow")]
    public void Triage_SpeculativeWording_Drops(string issue)
    {
        var (keep, reason) = InvokeTriageVerifierIssue(issue, new Dictionary<string, string>());
        Assert.False(keep);
        Assert.Contains("speculative", reason);
    }

    [Theory]
    [InlineData("The saveCards function is missing from the controller")]
    [InlineData("centerCurrentLocation is undefined in GlobeComponent")]
    [InlineData("There is a syntax error in the file")]
    public void Triage_ConcreteDefect_Keeps(string issue)
    {
        var (keep, _) = InvokeTriageVerifierIssue(issue, new Dictionary<string, string>());
        Assert.True(keep);
    }

    [Fact]
    public void Triage_EmptyIssue_Drops()
    {
        var (keep, reason) = InvokeTriageVerifierIssue("   ", new Dictionary<string, string>());
        Assert.False(keep);
        Assert.Contains("empty", reason);
    }

    // ── Partial-edit consistency gate ───────────────────────────────────────

    private static (bool isPartial, string reason) InvokeDetectPartialEdit(PlanStep? step)
    {
        var method = typeof(AgentController).GetMethod(
            "DetectPartialEdit", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DetectPartialEdit not found");
        var result = (ValueTuple<bool, string>)(method.Invoke(null, new object?[] { step })!);
        return (result.Item1, result.Item2);
    }

    [Fact]
    public void DetectPartialEdit_UnusedImport_WhileClaimingLifecycleImpl_Flags()
    {
        // The exact sig-int failure: description claims a full AfterViewInit lifecycle
        // implementation + existence check, but the concrete edit only adds an unused import.
        var step = new PlanStep
        {
            Change = "Initialize globeComponent reference safely using AfterViewInit lifecycle hook and check for existence before calling centerCurrentLocation()",
            OldString = "import { Component, OnInit, ViewChild } from '@angular/core';",
            NewString = "import { Component, AfterViewInit, OnInit, ViewChild } from '@angular/core';"
        };
        var (isPartial, reason) = InvokeDetectPartialEdit(step);
        Assert.True(isPartial);
        Assert.Contains("centerCurrentLocation", reason);
    }

    [Fact]
    public void DetectPartialEdit_FullImplementation_NotFlagged()
    {
        // The edit genuinely implements the described lifecycle hook — must pass.
        var step = new PlanStep
        {
            Change = "Initialize globeComponent reference safely using AfterViewInit lifecycle hook and check for existence before calling centerCurrentLocation()",
            OldString = "import { Component, OnInit, ViewChild } from '@angular/core';",
            NewString = "import { Component, AfterViewInit, OnInit, ViewChild } from '@angular/core';\n" +
                        "ngAfterViewInit() { if (this.globeComponent) { this.globeComponent.centerCurrentLocation(); } }"
        };
        var (isPartial, _) = InvokeDetectPartialEdit(step);
        Assert.False(isPartial);
    }

    [Fact]
    public void DetectPartialEdit_MethodCallClaimed_ButEditTouchesOtherSymbol_Flags()
    {
        var step = new PlanStep
        {
            Change = "Add a method that calls saveCards() when the board changes",
            OldString = "refreshBoard();",
            NewString = "refreshBoard(); notifyListeners();"
        };
        var (isPartial, reason) = InvokeDetectPartialEdit(step);
        Assert.True(isPartial);
        Assert.Contains("saveCards", reason);
    }

    [Fact]
    public void DetectPartialEdit_NoAnchorsAndTrivialEdit_NotFlagged()
    {
        // A change description with no concrete symbol anchors and a genuinely tiny edit
        // (import-adding task) must NOT be flagged by the structural rule.
        var step = new PlanStep
        {
            Change = "Add the missing import statement",
            OldString = "import { Component } from '@angular/core';",
            NewString = "import { Component, AfterViewInit } from '@angular/core';"
        };
        var (isPartial, _) = InvokeDetectPartialEdit(step);
        Assert.False(isPartial);
    }

    [Fact]
    public void DetectPartialEdit_DeletionOrEmptyPayload_NotFlagged()
    {
        // Deletion steps (empty newString) and resolution-driven steps (no payload) are out of scope.
        var del = new PlanStep { Change = "Remove the split button", OldString = "<button>Split</button>", NewString = "" };
        var (a, _) = InvokeDetectPartialEdit(del);
        Assert.False(a);

        var resolution = new PlanStep { Change = "Update the card rendering", TargetType = "html", TargetName = "<div class=card>" };
        var (b, _) = InvokeDetectPartialEdit(resolution);
        Assert.False(b);
    }

    [Fact]
    public void DetectPartialEdit_NullOrBlankChange_NotFlagged()
    {
        var (a, _) = InvokeDetectPartialEdit(null);
        Assert.False(a);
        var (b, _) = InvokeDetectPartialEdit(new PlanStep { Change = "  ", NewString = "x" });
        Assert.False(b);
    }

    [Fact]
    public void DetectPartialEdit_LegitTinyEdit_NotFlagged()
    {
        // 'initialize' alone is NOT a strong Rule B trigger — a genuinely small but complete
        // edit must never be rejected (reviewer: no false positives on legit small edits).
        var step = new PlanStep
        {
            Change = "Initialize the counter to zero",
            OldString = "private int _counter;",
            NewString = "private int _counter; // starts at zero"
        };
        var (isPartial, _) = InvokeDetectPartialEdit(step);
        Assert.False(isPartial);
    }

    [Fact]
    public void DetectPartialEdit_MethodBodyRefactor_WithoutSymbolInNewString_NotFlagged()
    {
        // Rule A compares against OLD+NEW touched text: a refactor that changes a method body
        // without re-uttering the method name (the name lives in the untouched old context)
        // must NOT be flagged as a partial edit.
        var step = new PlanStep
        {
            Change = "Refactor the render() method to use map",
            OldString = "render() { for (var i = 0; i < items.length; i++) { draw(items[i]); } }",
            NewString = "render() { items.map(function (x) { return draw(x); }); }"
        };
        var (isPartial, _) = InvokeDetectPartialEdit(step);
        Assert.False(isPartial);
    }

    [Fact]
    public void DetectPartialEdit_OneOfThreeClaimedMissing_NotStrictMajority()
    {
        // With 3 claimed symbols, exactly 1 missing is NOT a strict majority (2 needed) —
        // reviewer flagged integer-division bug (3/2 == 1) that would have flagged this.
        // refreshBoard is legitimately absent from the payload but the edit is still valid
        // (it lives in the untouched old context), so this must NOT be flagged.
        var step = new PlanStep
        {
            Change = "Call saveCards(), loadCards(), and refreshBoard()",
            OldString = "saveCards();",
            NewString = "saveCards(); loadCards();"
        };
        var (isPartial, _) = InvokeDetectPartialEdit(step);
        Assert.False(isPartial);
    }

    // ── Triage: hallucinated reference (symbol absent from ALL files) ───────

    [Fact]
    public void Triage_HallucinatedReference_SymbolInNoFile_DropsSpeculative()
    {
        // The verifier references `saveCards` but the symbol does not exist in ANY provided
        // file — the concern names code that isn't in the workspace, so it's unverifiable and
        // must be dropped as speculative (no repair step generated).
        var files = new Dictionary<string, string>
        {
            ["app.ts"] = "function loadCards() {}\nfunction renderBoard() {}"
        };
        var (keep, reason) = InvokeTriageVerifierIssue(
            "`saveCards` is never called anywhere in the project", files);
        Assert.False(keep);
        Assert.Contains("saveCards", reason);
        Assert.Contains("not present", reason);
    }

    [Fact]
    public void Triage_HallucinatedReference_BacktickedAbsentSymbol_DropsSpeculative()
    {
        // A backticked REFERENCE (no absence-claim wording) to a symbol absent from all files
        // is unverifiable — dropped as speculative.
        var files = new Dictionary<string, string>
        {
            ["globe/globe.component.ts"] = "export class GlobeComponent { rotate() {} }"
        };
        var (keep, reason) = InvokeTriageVerifierIssue(
            "`centerCurrentLocation` is never called anywhere in the project", files);
        Assert.False(keep);
        Assert.Contains("centerCurrentLocation", reason);
    }

    [Fact]
    public void Triage_HallucinatedReference_EmptyFilesMap_DoesNotDrop()
    {
        // With no files loaded the rule cannot prove absence — it must not nuke everything.
        var (keep, _) = InvokeTriageVerifierIssue(
            "`saveCards` is never called anywhere in the project", new Dictionary<string, string>());
        Assert.True(keep);
    }

    [Fact]
    public void Triage_ReferencePresentInFile_NotDroppedAsHallucination()
    {
        // The referenced symbol DOES exist in a file, so it is not a hallucination; whether it
        // survives triage is decided by the other rules (phantom/hedge/event-gate).
        var files = new Dictionary<string, string>
        {
            ["app.ts"] = "function saveCards() {}"
        };
        var (keep, _) = InvokeTriageVerifierIssue(
            "`saveCards` is never called anywhere in the project", files);
        Assert.True(keep);
    }

    // ── Repair-loop skip-phantom path ───────────────────────────────────────

    private static (bool isPhantom, string? phantom, List<string> remaining) InvokeTrySkipPhantomIssue(
        List<object> allSteps, List<string>? issues)
        => VerifierIssueTriage.TrySkipPhantomIssue(allSteps, issues);

    private static List<object> StepsWithLast(string? status, string? reason)
    {
        return new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "edit", ["path"] = "a.ts", ["status"] = "done" },
            new Dictionary<string, object?> { ["type"] = "edit", ["path"] = "b.ts", ["status"] = status, ["reason"] = reason }
        };
    }

    [Fact]
    public void SkipPhantom_AlreadyDoneStep_DropsFirstIssue_AndSkipsReVerify()
    {
        // Mirrors the existing skip-phantom logic: fix step resolves to 'already done' → the
        // driving issue was a phantom → drop it, keep the rest, no re-verify (caller continues).
        var (isPhantom, phantom, remaining) = InvokeTrySkipPhantomIssue(
            StepsWithLast("skipped", "already done"),
            new List<string> { "issue one", "issue two" });
        Assert.True(isPhantom);
        Assert.Equal("issue one", phantom);
        Assert.Single(remaining);
        Assert.Equal("issue two", remaining[0]);
    }

    [Fact]
    public void SkipPhantom_AlreadyDoneStep_WithSingleIssue_LeavesNone()
    {
        var (isPhantom, phantom, remaining) = InvokeTrySkipPhantomIssue(
            StepsWithLast("skipped", "already done"),
            new List<string> { "only issue" });
        Assert.True(isPhantom);
        Assert.Equal("only issue", phantom);
        Assert.Empty(remaining);
    }

    [Fact]
    public void SkipPhantom_StepNotSkipped_DoesNotDrop()
    {
        var (isPhantom, _, remaining) = InvokeTrySkipPhantomIssue(
            StepsWithLast("done", null), new List<string> { "issue" });
        Assert.False(isPhantom);
        Assert.Single(remaining);
    }

    [Fact]
    public void SkipPhantom_SkippedForOtherReason_DoesNotDrop()
    {
        var (isPhantom, _, remaining) = InvokeTrySkipPhantomIssue(
            StepsWithLast("skipped", "no match"), new List<string> { "issue" });
        Assert.False(isPhantom);
        Assert.Single(remaining);
    }

    [Fact]
    public void SkipPhantom_NoIssues_DoesNotDrop()
    {
        var (isPhantom, _, remaining) = InvokeTrySkipPhantomIssue(StepsWithLast("skipped", "already done"), null);
        Assert.False(isPhantom);
        Assert.Empty(remaining);
    }

    [Fact]
    public void SkipPhantom_EmptySteps_DoesNotDrop()
    {
        var (isPhantom, _, remaining) = InvokeTrySkipPhantomIssue(new List<object>(), new List<string> { "issue" });
        Assert.False(isPhantom);
        Assert.Single(remaining);
    }

    // ── Triage: concrete claim suppresses hedge ─────────────────────────────

    [Fact]
    public void Triage_ConcernWording_WithConcreteClaim_Keeps()
    {
        // 'concern' is a hedge word, but 'is missing' is a concrete claim — the claim suppresses
        // the hedge so the issue stays actionable. Uses a BARE token (no backticks) so the
        // hallucinated-reference rule (high-confidence absent symbols) is not triggered; a
        // bare mention of a genuinely-absent symbol is weaker evidence than a backticked one.
        var files = new Dictionary<string, string>
        {
            ["app.ts"] = "function loadCards() {}"
        };
        var (keep, _) = InvokeTriageVerifierIssue(
            "The concern is that saveCards is missing from the controller", files);
        Assert.True(keep);
    }

    [Fact]
    public void Triage_ConcreteAbsenceClaim_BacktickedAbsentSymbol_StaysActionable()
    {
        // Even a backticked high-confidence symbol stays actionable when the issue carries an
        // explicit ABSENCE CLAIM ('is missing') — reporting a genuinely-missing symbol is the
        // verifier's core job, and the hallucinated-reference rule must NOT swallow it.
        var files = new Dictionary<string, string>
        {
            ["app.ts"] = "function loadCards() {}"
        };
        var (keep, _) = InvokeTriageVerifierIssue(
            "The concern is that `saveCards` is missing from the controller", files);
        Assert.True(keep);
    }

    // ── Triage: event-gate counting ─────────────────────────────────────────

    [Fact]
    public void Triage_MultipleRefsInOneClickHandler_AllEventGated_Drops()
    {
        // One handler referencing the symbol twice must still count both occurrences
        // as event-bound, so the concern is dropped.
        var files = new Dictionary<string, string>
        {
            ["sig-int/sig-int.component.html"] =
                "<button (click)=\"globeComponent.centerCurrentLocation(); globeComponent.refresh()\">Center</button>"
        };
        var (keep, reason) = InvokeTriageVerifierIssue(
            "globeComponent is not initialized during the initial render cycle", files);
        Assert.False(keep);
        Assert.Contains("event-gated", reason);
    }

    [Fact]
    public void Triage_EventGate_SubstringSymbol_NotMistaken()
    {
        // Symbol 'go' (backticked in the issue) appears exactly once inside a click handler,
        // while 'google' sits in a NON-event link. Word boundaries must ensure 'go' does not
        // match inside 'google' — otherwise total>eventBound and the safe drop never fires.
        // Button label is kept symbol-free so the only reference is the event-bound one.
        var files = new Dictionary<string, string>
        {
            ["sig-int/sig-int.component.html"] =
                "<button (click)=\"go()\">Run</button>\n<a href=\"https://google.com\">x</a>"
        };
        var (keep, reason) = InvokeTriageVerifierIssue(
            "`go` is not initialized during the initial render cycle", files);
        Assert.False(keep);
        Assert.Contains("event-gated", reason);
    }
}
