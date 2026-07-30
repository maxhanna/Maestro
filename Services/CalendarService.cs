namespace Weaver.Services;

public class CalendarService
{
    private readonly DatabaseService _db;

    public CalendarService(DatabaseService db)
    {
        _db = db;
    }

    public async Task<string?> LoadRawAsync()
    {
        return _db.GetCalendarData();
    }

    public async Task SaveRawAsync(string json)
    {
        if (json == null) json = "[]";
        _db.SetCalendarData(json);
    }
}
