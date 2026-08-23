using Microsoft.AspNetCore.Mvc;
using Weaver.Services;

namespace Weaver.Controllers;

[ApiController]
[Route("api/changelog")]
public class ChangelogController : ControllerBase
{
    private readonly ChangelogService _changelog;

    public ChangelogController(ChangelogService changelog)
    {
        _changelog = changelog;
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new { content = _changelog.Read() });
    }

    [HttpPost]
    public IActionResult Save([FromBody] ChangelogSaveRequest req)
    {
        if (req == null || string.IsNullOrEmpty(req.Content))
            return BadRequest("Content is required");
        _changelog.Overwrite(req.Content);
        return Ok(new { ok = true });
    }
}

public class ChangelogSaveRequest
{
    public string Content { get; set; } = "";
}
