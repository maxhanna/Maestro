using Microsoft.AspNetCore.Mvc;
using Weaver.Services;

namespace Weaver.Controllers;

[ApiController]
[Route("api/benchmark")]
public class BenchmarkController : ControllerBase
{
    private readonly BenchmarkService _benchmark;
    private readonly IWebHostEnvironment _env;
    private readonly ConfigFileService _configFile;

    public BenchmarkController(IWebHostEnvironment env, DatabaseService db, ConfigFileService configFile)
    {
        _env = env;
        _benchmark = new BenchmarkService(db);
        _configFile = configFile;
    }

    /// <summary>
    /// Ensures a "Weaver Benchmarks" project exists whose root is the benchmark project
    /// root (custom system-info root, else the desktop benchmark_sandbox). Benchmark cards
    /// are created under this project so they land in a dedicated kanban instead of whatever
    /// project happens to be selected. Idempotent: the existing entry is reused when the
    /// path already matches, re-pointed when the name exists with a stale root, or created.
    /// </summary>
    [HttpPost("ensure-project")]
    public async Task<IActionResult> EnsureProject()
    {
        var custom = _benchmark.LoadCustomSystemInfo();
        var root = BenchmarkService.ResolveBenchmarkRoot(custom?.BenchmarkProjectRoot);
        var cfg = await _configFile.LoadConfigAsync();
        cfg.projects ??= new List<ProjectDto>();
        var (proj, created, updated) = BenchmarkService.ResolveBenchmarkProjectEntry(cfg.projects, root);
        if (created || updated)
            await _configFile.WriteConfigAsync(cfg);
        // Always return the NORMALIZED path so the board's filePath === selectedProject
        // filter stays exactly consistent even when an existing entry was stored with a
        // trailing separator or differing case.
        return Ok(new { path = BenchmarkService.NormalizeProjectPath(proj.Path), name = proj.Name, created, updated });
    }

    [HttpGet("scores")]
    public IActionResult GetScores()
    {
        var scores = _benchmark.LoadScores();
        return Ok(scores.OrderByDescending(s => s.Timestamp).ToList());
    }

    [HttpGet("info")]
    public IActionResult GetSystemInfo()
    {
        var info = BenchmarkService.DetectSystemInfo();
        return Ok(info);
    }

    [HttpGet("plans")]
    public IActionResult GetPlans()
    {
        var plans = BenchmarkService.GetBenchmarkPlans();
        return Ok(plans);
    }

    [HttpPost("save-score")]
    public IActionResult SaveScore([FromBody] BenchmarkScore score)
    {
        if (score == null)
            return BadRequest("Invalid score data");
        score.Timestamp = DateTime.UtcNow;
        var overrides = _benchmark.LoadCustomSystemInfo();
        score.SystemInfo = _benchmark.ResolveSystemInfo(overrides);
        _benchmark.SaveScore(score);
        return Ok(new { message = "Score saved", id = score.Id });
    }

    /// <summary>
    /// Runs a benchmark's acceptance checks end-to-end (filesystem + live web test) against
    /// the resolved benchmark root, computes the REAL score (correctness from the checks +
    /// edit success + step efficiency), persists it, and returns the full score — including
    /// the per-check <see cref="BenchmarkScore.Checks"/> list. This is the verify-then-score
    /// path: a saved score now reflects whether the benchmark's acceptance criteria actually
    /// passed, not merely whether the agent's edit operations reported success. The root is
    /// resolved exactly as <c>ExecuteBenchmarkVerifyStep</c> does (custom system-info root,
    /// else the desktop benchmark_sandbox) so checks inspect the same workspace the agent
    /// wrote to.
    /// </summary>
    [HttpPost("verify-and-score")]
    public async Task<IActionResult> VerifyAndScore([FromBody] VerifyScoreRequest? req, CancellationToken ct)
    {
        if (req == null)
            return BadRequest("Invalid verify-and-score request");
        var custom = _benchmark.LoadCustomSystemInfo();
        var root = BenchmarkService.ResolveBenchmarkRoot(custom?.BenchmarkProjectRoot);
        try
        {
            var score = await _benchmark.EvaluateAsync(
                req.Level, root, req.SuccessfulEdits, req.FailedEdits, req.StepCount,
                req.DurationMs, req.ModelUsed ?? "", req.Edits, req.ErrorReason, ct);
            return Ok(score);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("system-info")]
    public IActionResult GetSystemInfoConfig()
    {
        var custom = _benchmark.LoadCustomSystemInfo();
        var detected = BenchmarkService.DetectSystemInfo();
        var defaultRoot = AgentProjectUtilities.GetBenchmarkSandboxPath();
        return Ok(new { detected, custom, defaultBenchmarkRoot = defaultRoot });
    }

    [HttpPost("system-info")]
    public IActionResult SaveSystemInfoConfig([FromBody] CustomSystemInfo info)
    {
        if (info == null)
            return BadRequest("Invalid system info data");
        _benchmark.SaveCustomSystemInfo(info);
        return Ok(new { message = "System info saved" });
    }

    [HttpDelete("scores/{id}")]
    public IActionResult DeleteScore(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest("Missing score id");
        var deleted = _benchmark.DeleteScore(id);
        if (!deleted)
            return NotFound(new { message = "Score not found" });
        return Ok(new { message = "Score deleted" });
    }

    /// <summary>Records that a local score was successfully uploaded to BugHosted, so
    /// subsequent "Send all" runs skip it. Idempotent; returns 404 for unknown ids.</summary>
    [HttpPost("scores/{id}/mark-sent")]
    public IActionResult MarkScoreSent(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest("Missing score id");
        var marked = _benchmark.MarkScoreSent(id);
        if (!marked)
            return NotFound(new { message = "Score not found" });
        return Ok(new { message = "Score marked as sent" });
    }

    [HttpDelete("scores")]
    public IActionResult ClearAllScores()
    {
        var count = _benchmark.ClearAllScores();
        return Ok(new { message = "Cleared " + count + " score(s)", cleared = count });
    }
}

/// <summary>Body for <c>POST /api/benchmark/verify-and-score</c>: the edit/step metrics the
/// client captured while the agent ran the benchmark card. The server adds the missing
/// half — the acceptance-check results — and returns the computed, persisted score.</summary>
public class VerifyScoreRequest
{
    public int Level { get; set; }
    public int SuccessfulEdits { get; set; }
    public int FailedEdits { get; set; }
    public int StepCount { get; set; }
    public double DurationMs { get; set; }
    public string? ModelUsed { get; set; }
    public List<BenchmarkEditRecord>? Edits { get; set; }
    public string? ErrorReason { get; set; }
}
