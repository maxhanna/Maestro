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

    [HttpDelete("scores")]
    public IActionResult ClearAllScores()
    {
        var count = _benchmark.ClearAllScores();
        return Ok(new { message = "Cleared " + count + " score(s)", cleared = count });
    }
}
