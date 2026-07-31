using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Weaver.Controllers;
using Weaver.IntegrationTests.Fakes;
using Weaver.Services;
using Xunit;

namespace Weaver.IntegrationTests;

/// <summary>
/// Pins the JSON contract between the frontend and AgentController for benchmark-ladder
/// cards. agent.js writes <c>card.benchmark = { presetLevel: N }</c>; the backend reads
/// it back out of board.json to decide whether a resumed card still gets the benchmark
/// sandbox. This replaced an older, differently-named <c>card._benchmark</c> flag, and a
/// mismatch between the two sides fails silently — the card simply stops being treated
/// as a ladder run — so the shape is asserted directly rather than inferred.
/// </summary>
public class BoardDataBenchmarkContractTests : IDisposable
{
    readonly string _scratchDir;
    readonly TerminalService _terminal;
    readonly ConfigFileService _configFile;
    readonly string _boardPath;

    public BoardDataBenchmarkContractTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "weaver-board-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
        _boardPath = Path.Combine(_scratchDir, "board.json");

        var env = new TestWebHostEnvironment { ContentRootPath = _scratchDir };
        _configFile = new ConfigFileService(env);
        _terminal = new TerminalService(_configFile);
    }

    public void Dispose()
    {
        _terminal.Dispose();
        try { Directory.Delete(_scratchDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    AgentController BuildController()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Model"] = "test-model"
        }).Build();
        var env = new TestWebHostEnvironment { ContentRootPath = _scratchDir };
        var boardData = new BoardDataService(_boardPath, NullLogger<BoardDataService>.Instance);

        return new AgentController(
            new FakeHttpClientFactory(new FakeLlmHandler(Array.Empty<string>())),
            config, env, _terminal,
            new FileHintsManager(_scratchDir), _configFile,
            new EmailService(_configFile), boardData,
            new PushNotificationService(_scratchDir));
    }

    void WriteBoard(string todoCardsJson)
    {
        var json = $$"""{"todo":[{{todoCardsJson}}],"doing":[],"done":[],"selfImproving":[]}""";
        // Parse before writing. LoadPlanFromBoardDataAsync swallows malformed JSON and
        // returns (null, null, false), so a broken fixture would satisfy every negative
        // assertion in this class without exercising a single line of the logic. Fail
        // here instead, where the message points at the fixture.
        using (JsonDocument.Parse(json)) { }
        File.WriteAllText(_boardPath, json);
    }

    /// <summary>A card carrying a plan, exactly as agent.js persists one mid-run.
    /// Written with explicit escapes rather than a raw literal: the leading quote of
    /// "_plan" is load-bearing, and a raw-string delimiter silently eats it, which
    /// produces malformed cards that fail closed and make negative assertions pass for
    /// the wrong reason.</summary>
    const string PlanJson =
        "\"_plan\":{\"summary\":\"s\",\"items\":[{\"index\":0,\"file\":\"a.txt\",\"change\":\"c\",\"done\":false}]}";

    [Fact]
    public async Task LadderCard_WithPresetLevel_IsDetectedAsLadderPreset()
    {
        WriteBoard($$"""{"id":"bm1","benchmark":{"presetLevel":3},{{PlanJson}}}""");

        var (plan, _, isLadderPreset) = await BuildController().LoadPlanFromBoardDataAsync("bm1");

        Assert.True(isLadderPreset);
        Assert.NotNull(plan);
    }

    [Fact]
    public async Task LadderCard_PresetLevelZero_IsStillALadderPreset()
    {
        // "Benchmark 0" is a real rung on the ladder. Anything that treats the level as
        // truthy rather than present/absent would silently demote it to a normal card.
        WriteBoard($$"""{"id":"bm0","benchmark":{"presetLevel":0},{{PlanJson}}}""");

        var (_, _, isLadderPreset) = await BuildController().LoadPlanFromBoardDataAsync("bm0");

        Assert.True(isLadderPreset);
    }

    [Fact]
    public async Task AuthoredTestCard_WithManifestButNoPresetLevel_IsNotALadderPreset()
    {
        // A hand-authored isTest card may carry a manifest (expectedSteps/allowedPaths)
        // without being a ladder run — it must stay on the board and use the real project
        // root rather than the benchmark sandbox.
        WriteBoard($$"""{"id":"auth1","benchmark":{"expectedSteps":2},{{PlanJson}}}""");

        var (_, _, isLadderPreset) = await BuildController().LoadPlanFromBoardDataAsync("auth1");

        Assert.False(isLadderPreset);
    }

    [Fact]
    public async Task OrdinaryCard_WithNoBenchmarkManifest_IsNotALadderPreset()
    {
        WriteBoard($$"""{"id":"plain",{{PlanJson}}}""");

        var (plan, _, isLadderPreset) = await BuildController().LoadPlanFromBoardDataAsync("plain");

        Assert.False(isLadderPreset);
        Assert.NotNull(plan);
    }

    [Fact]
    public async Task LegacyUnderscoreBenchmarkFlag_IsNoLongerHonoured()
    {
        // Cards saved by the pre-unification frontend used card._benchmark. Those are
        // ephemeral ladder cards that were deleted after scoring, so no real board should
        // still contain one; this asserts the old flag is genuinely dead rather than
        // quietly still driving behaviour.
        WriteBoard($$"""{"id":"old1","_benchmark":true,{{PlanJson}}}""");

        var (_, _, isLadderPreset) = await BuildController().LoadPlanFromBoardDataAsync("old1");

        Assert.False(isLadderPreset);
    }

    [Fact]
    public async Task UnknownCardId_ReturnsNothingRatherThanThrowing()
    {
        WriteBoard($$"""{"id":"bm1","benchmark":{"presetLevel":1},{{PlanJson}}}""");

        var (plan, completed, isLadderPreset) = await BuildController().LoadPlanFromBoardDataAsync("no-such-card");

        Assert.Null(plan);
        Assert.Null(completed);
        Assert.False(isLadderPreset);
    }

    [Fact]
    public async Task LadderCard_WithoutAPlanYet_LosesItsLadderFlag()
    {
        // Documents a real limitation rather than asserting desired behaviour: the lookup
        // skips any card that has no _plan, so a ladder card that has not planned yet
        // reports isLadderPreset=false. Harmless today because ExecuteStream derives the
        // flag from the request payload first and only consults board data when resuming
        // an existing plan — but it means board data alone is not a reliable source.
        WriteBoard("""{"id":"bmNoPlan","benchmark":{"presetLevel":2}}""");

        var (plan, _, isLadderPreset) = await BuildController().LoadPlanFromBoardDataAsync("bmNoPlan");

        Assert.Null(plan);
        Assert.False(isLadderPreset);
    }
}
