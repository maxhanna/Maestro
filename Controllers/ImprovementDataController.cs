using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Weaver.Services;
namespace Weaver.Controllers;
[ApiController]
[Route("api/improvementdata")]
public class ImprovementDataController : ControllerBase
{
        private readonly DatabaseService _db;
        public ImprovementDataController(DatabaseService db)
        {
            _db = db;
        }
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string project)
        {
            var json = _db.GetImprovementData(project ?? "");
            if (string.IsNullOrWhiteSpace(json))
                return Ok(new { features = Array.Empty<object>() });
            try
            {
                return new ContentResult { Content = json, ContentType = "application/json", StatusCode = 200 };
            }
            catch
            {
                return Ok(new { features = Array.Empty<object>() });
            }
        }
        [HttpPut]
        public async Task<IActionResult> Put([FromBody] JsonElement data)
        {
            var project = "";
            if (data.TryGetProperty("project", out var projEl))
                project = projEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(project))
                return BadRequest(new { error = "project is required" });
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            _db.SetImprovementData(project, json);
            return Ok();
        }
    }
