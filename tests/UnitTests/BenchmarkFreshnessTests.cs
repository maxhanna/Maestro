using System.Reflection;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the benchmark fetch-freshness check: a data/web-search file must carry a
/// run-time capture date (FETCHED_AT: YYYY-MM-DD) that matches the file's own write
/// date and is recent relative to evaluation — so a run that reuses a cached/stale
/// file or hardcodes an old date is flagged, while a genuinely fresh fetch passes.
/// </summary>
public class BenchmarkFreshnessTests
{
    private static string RunCheck(string content, int maxDaysOld = 2)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "weaver_fresh_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            var file = Path.Combine(tmp, "output.txt");
            File.WriteAllText(file, content);
            File.SetLastWriteTime(file, DateTime.Now);

            var check = Check.FreshTimestamp("fresh", "output.txt", maxDaysOld);
            var service = new BenchmarkService(SubstituteDb());
            var task = (Task<BenchmarkCheckResult>)typeof(BenchmarkService)
                .GetMethod("EvaluateCheckAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, new object[] { check, tmp, CancellationToken.None })!;
            var result = task.GetAwaiter().GetResult();
            return result.Passed ? "PASS" : "FAIL: " + result.Message;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static DatabaseService SubstituteDb()
    {
        // Point the DB at throwaway temp paths so construction never touches the real DB.
        var basePath = Path.Combine(Path.GetTempPath(), "weaver_fresh_db_" + Guid.NewGuid().ToString("N"));
        return new DatabaseService(basePath + ".db", basePath + "_data", basePath + "_cfg.json");
    }

    [Fact]
    public void FreshMarker_Today_Passes()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var content = $"FETCHED_AT: {today}\nid,type\n1,grass";
        Assert.StartsWith("PASS", RunCheck(content));
    }

    [Fact]
    public void FreshMarker_YesterdayWithinTolerance_Passes()
    {
        // A run that crossed midnight captures the previous day — still within tolerance.
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var content = $"Fetched at {yesterday}\n1969";
        Assert.StartsWith("PASS", RunCheck(content));
    }

    [Fact]
    public void NoDate_Fails()
    {
        Assert.StartsWith("FAIL", RunCheck("id,type\n1,grass\npikachu"));
    }

    [Fact]
    public void HardcodedOldDate_Fails()
    {
        var content = "FETCHED_AT: 2020-01-01\nid,type\n1,grass";
        Assert.StartsWith("FAIL", RunCheck(content));
    }

    [Fact]
    public void StaleCachedContent_Fails()
    {
        // The realistic cache-reuse case: an untouched old file — old embedded date AND
        // old write time. The write-date match passes but the freshness window catches it.
        var old = DateTime.Today.AddDays(-10).ToString("yyyy-MM-dd");
        var content = $"FETCHED_AT: {old}\nid,type\n1,grass";
        var tmp = Path.Combine(Path.GetTempPath(), "weaver_fresh_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            var file = Path.Combine(tmp, "output.txt");
            File.WriteAllText(file, content);
            File.SetLastWriteTime(file, DateTime.Today.AddDays(-10));
            var check = Check.FreshTimestamp("fresh", "output.txt");
            var service = new BenchmarkService(SubstituteDb());
            var task = (Task<BenchmarkCheckResult>)typeof(BenchmarkService)
                .GetMethod("EvaluateCheckAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, new object[] { check, tmp, CancellationToken.None })!;
            var result = task.GetAwaiter().GetResult();
            Assert.False(result.Passed);
            // The embedded date matches the (old) write date, so the failure must come
            // from the freshness window — the message names the stale date.
            Assert.Contains("stale", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    [Fact]
    public void FutureDate_Fails()
    {
        var future = DateTime.Today.AddDays(5).ToString("yyyy-MM-dd");
        Assert.StartsWith("FAIL", RunCheck($"FETCHED_AT: {future}\n1,grass"));
    }

    [Fact]
    public void FallbackIsoDate_IsUsedWhenNoMarker()
    {
        // No explicit marker — the first YYYY-MM-DD date is used as the capture date.
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        Assert.StartsWith("PASS", RunCheck($"captured {today}\nbulbasaur"));
    }

    [Fact]
    public void JsonFetchedAtMarker_IsDetected()
    {
        // JSON format: "fetched_at": "2026-08-07" — quote-wrapped key/value pairs.
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var content = $"{{\"fetched_at\": \"{today}\", \"cities\": [{{\"name\": \"Paris\", \"temperature\": 21.5}}]}}";
        Assert.StartsWith("PASS", RunCheck(content));
    }

    [Fact]
    public void JsonFetchedAtCamelCase_IsDetected()
    {
        // CamelCase variant (no underscore) must also resolve.
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var content = $"{{\"fetchedAt\": \"{today}\", \"name\": \"Tokyo\"}}";
        Assert.StartsWith("PASS", RunCheck(content));
    }

    [Fact]
    public void MarkerPreferredOverOtherDates()
    {
        // An answer that itself contains a date (e.g. a halving date) must not confuse
        // the check when an explicit FETCHED_AT marker is present.
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var content = $"FETCHED_AT: {today}\nnext halving: 2028-04-24\nMariana Trench\n1969";
        Assert.StartsWith("PASS", RunCheck(content));
    }

    [Fact]
    public void StaleMarker_WithNewerOtherDate_Fails()
    {
        // Old explicit marker with a newer-looking date elsewhere: the marker is the
        // run-time capture, so the check still flags the stale one.
        var old = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd");
        Assert.StartsWith("FAIL", RunCheck($"FETCHED_AT: {old}\nsome event: {DateTime.Today:yyyy-MM-dd}"));
    }

    [Fact]
    public void DataFetchLevels_IncludeFreshnessCheck()
    {
        foreach (var level in new[] { 16, 17, 18, 19 })
        {
            var plan = BenchmarkService.GetBenchmarkPlans().First(p => p.Level == level);
            Assert.Contains(plan.AcceptanceChecks,
                c => c.Type == BenchmarkCheckType.FileFreshTimestamp);
            Assert.Contains("FETCHED_AT", plan.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Level19_WeatherBenchmark_HasCityAndFieldChecks()
    {
        var plan = BenchmarkService.GetBenchmarkPlans().First(p => p.Level == 19);
        var values = plan.AcceptanceChecks.Select(c => c.Value).Where(v => v != null).Select(v => v!).ToList();
        foreach (var expected in new[] { "paris", "tokyo", "sydney", "temperature", "windspeed", "weathercode" })
            Assert.Contains(expected, values, StringComparer.OrdinalIgnoreCase);
    }
}
