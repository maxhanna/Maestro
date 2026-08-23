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

    /// <summary>Fetches fresh changelog data from GitHub releases.</summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            var content = await _changelog.FetchChangelogAsync();
            return Ok(new { content, lastSynced = _changelog.LastFetchTime?.ToString("o") });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = "Failed to fetch from GitHub", detail = ex.Message });
        }
    }
}
