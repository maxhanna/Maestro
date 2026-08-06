using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Weaver.Services;
namespace Weaver.Controllers;
[ApiController]
[Route("api/[controller]")]
public class FileHintsController : ControllerBase
{
    private readonly DatabaseService _db;

    public FileHintsController(DatabaseService db)
    {
        _db = db;
    }

    [HttpGet]
    public IActionResult GetFileHints()
    {
        var raw = _db.GetFileHints();
        if (string.IsNullOrWhiteSpace(raw))
        {
            var defaultContent = "{\"Projects\": {}}";
            _db.SetFileHints(defaultContent);
            raw = defaultContent;
        }
        try
        {
            var parsed = JsonDocument.Parse(raw);
            return Ok(parsed.RootElement.Clone());
        }
        catch
        {
            return Ok(raw);
        }
    }

    [HttpPut]
    public IActionResult UpdateFileHints([FromBody] object content)
    {
        try
        {
            var json = JsonSerializer.Serialize(content);
            JsonDocument.Parse(json); // validate
            _db.SetFileHints(json);
            return Ok("File hints updated successfully.");
        }
        catch (JsonException)
        {
            return BadRequest("Invalid JSON format.");
        }
        catch (Exception)
        {
            return StatusCode(500, "An error occurred while updating file hints.");
        }
    }
}
