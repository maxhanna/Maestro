using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the deterministic placeholder-stub detector's false-positive behavior. A real
/// multi-step run ("add a public getItems() method" as the SECOND step) had its plan edit
/// wrongly rejected: the newString was
///   "  constructor() { }\n  getItems() { return this.items.slice(); }"
/// and the detector flagged it as a stub because the carried-over `constructor() { }` line
/// matched the empty-method-body pattern. The whole edit was then reverted and escalated to
/// the LLM resolver (burning calls) — the false positive killed a perfectly good direct apply.
///
/// Two fixes are locked in here:
///   1. Lines carried over UNCHANGED from the oldString (preExisting) are pre-existing code,
///      never LLM-authored stubs — they are excluded from the line-based stub analysis.
///   2. Empty constructors/destructors are idiomatic, not stubs (field-initializer classes,
///      DI shells) — only empty NEW methods get flagged.
/// </summary>
public class StubDetectionRegressionTests
{
    private const string CtorLine = "  constructor() { }";
    private const string CtorWithGetItems =
        "  constructor() { }\n  getItems() { return this.items.slice(); }";

    // ── The exact false-positive shapes ─────────────────────────────────────

    [Fact]
    public void CarriedOverCtor_PlusRealOneLiner_IsNotAStub()
    {
        // preExisting = the oldString the edit replaced — the ctor line is carried over verbatim.
        Assert.False(AgentEditHeuristics.LooksLikePlaceholderStub(CtorWithGetItems, preExisting: CtorLine));
    }

    [Fact]
    public void EmptyCtor_WithoutPreExisting_IsStillNotAStub()
    {
        // Even standalone (no oldString context), an empty constructor is idiomatic, not a stub.
        Assert.False(AgentEditHeuristics.LooksLikePlaceholderStub("  constructor() { }"));
    }

    [Fact]
    public void CarriedOverEmptyMethod_IsNotAStub_WhenInOldString()
    {
        // An empty helper that already existed in the file must not doom the new edit.
        var newStr = "  helper() { }\n  getItems() { return this.items.slice(); }";
        Assert.False(AgentEditHeuristics.LooksLikePlaceholderStub(newStr, preExisting: "  helper() { }"));
    }

    [Fact]
    public void RealOneLinerMethod_NoCtor_IsNotAStub()
    {
        Assert.False(AgentEditHeuristics.LooksLikePlaceholderStub(
            "  getItems() { return this.items.slice(); }"));
    }

    // ── The detector must still catch genuine stubs ─────────────────────────

    [Fact]
    public void NewEmptyMethod_IsStillAStub()
    {
        Assert.True(AgentEditHeuristics.LooksLikePlaceholderStub(
            "  getItems() { }", preExisting: CtorLine));
    }

    [Fact]
    public void NotImplementedException_IsStillAStub()
    {
        Assert.True(AgentEditHeuristics.LooksLikePlaceholderStub(
            "  getItems() { throw new NotImplementedException(); }"));
    }

    [Fact]
    public void ConsoleLogOnlyBody_IsStillAStub()
    {
        Assert.True(AgentEditHeuristics.LooksLikePlaceholderStub(
            "  getItems() { console.log('stub'); }"));
    }

    [Fact]
    public void PlaceholderComment_IsStillAStub()
    {
        Assert.True(AgentEditHeuristics.LooksLikePlaceholderStub(
            "  getItems() { // TODO: implement\n  }"));
    }

    // ── Carried-over lines must not mask a REAL new stub ────────────────────

    [Fact]
    public void CarriedOverCtor_WithNewEmptyMethod_IsStillAStub()
    {
        // The ctor is excluded, but the genuinely NEW empty method must still be caught.
        var newStr = "  constructor() { }\n  getItems() { }";
        Assert.True(AgentEditHeuristics.LooksLikePlaceholderStub(newStr, preExisting: CtorLine));
    }
}
