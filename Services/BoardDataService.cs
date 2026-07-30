using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Weaver.Services;

public class BoardDataService
{
    private readonly DatabaseService _db;
    private readonly ILogger<BoardDataService> _logger;

    public BoardDataService(DatabaseService db, ILogger<BoardDataService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SaveRawAsync(string json, int maxRetries = 15, int baseDelayMs = 500)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _db.SetBoardData(json);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SaveRawAsync attempt {Attempt}/{MaxRetries} failed.", attempt, maxRetries);
                if (attempt >= maxRetries)
                {
                    _logger.LogCritical("SaveRawAsync failed after {MaxRetries} attempts.", maxRetries);
                    throw;
                }
                var delay = baseDelayMs * (1 << (attempt - 1));
                await Task.Delay(delay);
            }
        }
    }

    public async Task<string?> LoadRawAsync(int maxRetries = 5, int baseDelayMs = 200)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return _db.GetBoardData();
            }
            catch when (attempt < maxRetries)
            {
                var delay = baseDelayMs * (1 << (attempt - 1));
                _logger.LogWarning("LoadRawAsync attempt {Attempt}/{MaxRetries} failed, retrying in {Delay}ms",
                    attempt, maxRetries, delay);
                await Task.Delay(delay);
            }
        }
        return _db.GetBoardData();
    }
}