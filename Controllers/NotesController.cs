using Microsoft.AspNetCore.Mvc;
using Weaver.Services;

namespace Weaver.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NotesController : ControllerBase
{
    private readonly DatabaseService _db;

    public NotesController(DatabaseService db)
    {
        _db = db;
    }

    [HttpGet]
    public IActionResult Get([FromQuery] string? project)
    {
        var key = project ?? "";
        var content = _db.GetValue("notes:" + key);
        return Ok(new { project = key, content = content ?? "" });
    }

    [HttpPost]
    public IActionResult Save([FromBody] NotesDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Project))
            return BadRequest("project is required");
        _db.SetValue("notes:" + dto.Project, dto.Content ?? "");
        return Ok(new { project = dto.Project, saved = true });
    }
}

public class NotesDto
{
    public string? Project { get; set; }
    public string? Content { get; set; }
}
