using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Weaver.Controllers;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public class BenchmarkControllerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weaver-benchmark-api-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PrepareEvaluatePersistReload_EndToEnd()
    {
        Directory.CreateDirectory(_root);
        var controller = new BenchmarkController(new FakeEnvironment(_root));
        var prepare = Assert.IsType<OkObjectResult>(await controller.Prepare(1,
            request: null, CancellationToken.None));
        using var preparedJson = JsonDocument.Parse(JsonSerializer.Serialize(prepare.Value));
        var runRoot = preparedJson.RootElement.GetProperty("benchmarkProjectRoot").GetString()!;
        var runId = preparedJson.RootElement.GetProperty("runId").GetString()!;
        Directory.CreateDirectory(Path.Combine(runRoot, "benchmark_test_1"));
        await File.WriteAllTextAsync(Path.Combine(runRoot, "benchmark_test_1", "test.md"),
            "Hello world\nThe capital of France is Paris");

        var evaluated = Assert.IsType<OkObjectResult>(await controller.Evaluate(new BenchmarkEvaluationRequest
        {
            Level = 1,
            RunId = runId,
            ModelUsed = "integration-model",
            DurationMs = 20,
            ActualStrategies = ["whole-file-create"]
        }, CancellationToken.None));
        var score = Assert.IsType<BenchmarkScore>(evaluated.Value);

        Assert.Equal(100, score.ScorePercent);
        Assert.Contains("whole-file-create", score.ActualStrategies);
        var scores = Assert.IsType<OkObjectResult>(controller.GetScores());
        Assert.Single(Assert.IsType<List<BenchmarkScore>>(scores.Value));
    }

    [Fact]
    public async Task SaveScore_RecomputesTheScoreFromThePreparedRun()
    {
        Directory.CreateDirectory(_root);
        var controller = new BenchmarkController(new FakeEnvironment(_root));
        var prepare = Assert.IsType<OkObjectResult>(await controller.Prepare(1, null, CancellationToken.None));
        using var preparedJson = JsonDocument.Parse(JsonSerializer.Serialize(prepare.Value));
        var runRoot = preparedJson.RootElement.GetProperty("benchmarkProjectRoot").GetString()!;
        var runId = preparedJson.RootElement.GetProperty("runId").GetString()!;
        Directory.CreateDirectory(Path.Combine(runRoot, "benchmark_test_1"));
        await File.WriteAllTextAsync(Path.Combine(runRoot, "benchmark_test_1", "test.md"),
            "Hello world\nThe capital of France is Paris");

        var result = Assert.IsType<OkObjectResult>(await controller.SaveScore(new BenchmarkScoreSubmission
        {
            Level = 1,
            RunId = runId,
            ModelUsed = "integrity-model",
            ActualStrategies = ["reported-but-not-trusted"]
        }, CancellationToken.None));
        var score = Assert.IsType<BenchmarkScore>(result.Value);

        Assert.Equal(100, score.ScorePercent);
        Assert.Equal("integrity-model", score.ModelUsed);
        Assert.Equal(100, score.CorrectnessPercent);
        Assert.Equal(100, score.EfficiencyPercent);
        Assert.Single(Assert.IsType<List<BenchmarkScore>>(((OkObjectResult)controller.GetScores()).Value));

        var duplicate = await controller.SaveScore(new BenchmarkScoreSubmission
        {
            Level = 1,
            RunId = runId
        }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(duplicate);

        Assert.IsType<OkObjectResult>(controller.DeleteScore(score.Id));
        var replay = await controller.SaveScore(new BenchmarkScoreSubmission
        {
            Level = 1,
            RunId = runId
        }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(replay);
    }

    [Fact]
    public async Task SaveScore_RejectsMissingOrMismatchedRun()
    {
        Directory.CreateDirectory(_root);
        var controller = new BenchmarkController(new FakeEnvironment(_root));

        var missing = await controller.SaveScore(new BenchmarkScoreSubmission { Level = 1 }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(missing);

        var prepare = Assert.IsType<OkObjectResult>(await controller.Prepare(1, null, CancellationToken.None));
        using var preparedJson = JsonDocument.Parse(JsonSerializer.Serialize(prepare.Value));
        var runId = preparedJson.RootElement.GetProperty("runId").GetString()!;
        var mismatched = await controller.SaveScore(new BenchmarkScoreSubmission { Level = 2, RunId = runId }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(mismatched);
    }

    [Fact]
    public async Task Evaluate_RejectsUnknownRunId()
    {
        Directory.CreateDirectory(_root);
        var controller = new BenchmarkController(new FakeEnvironment(_root));

        var result = await controller.Evaluate(new BenchmarkEvaluationRequest
        {
            Level = 1,
            RunId = "missing-run"
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Prepare_RejectsClientSuppliedRootOutsideServerSandbox()
    {
        Directory.CreateDirectory(_root);
        var controller = new BenchmarkController(new FakeEnvironment(_root));

        var result = await controller.Prepare(1,
            new BenchmarkPrepareRequest { BenchmarkProjectRoot = Path.Combine(_root, "outside") }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(Directory.Exists(Path.Combine(_root, "outside")));
    }

    [Fact]
    public async Task Prepare_RejectsUnknownLevel()
    {
        Directory.CreateDirectory(_root);
        var controller = new BenchmarkController(new FakeEnvironment(_root));
        var result = await controller.Prepare(999,
            new BenchmarkPrepareRequest { BenchmarkProjectRoot = Path.Combine(_root, "sandbox") }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Weaver.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
