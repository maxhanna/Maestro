using System.Text.Json;
using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the per-endpoint stream-reliability tracker (EndpointHealthService) that powers
/// the endpoint-picker health badge. RecordCall must count transport/stream/truncation
/// failures as stream errors (the same predicates that drive recovery) while successful
/// calls and non-transport LLM failures (JSON parse, hallucination, empty) only move the
/// call/error counters appropriately. NormalizeKey normalizes URLs so trailing slashes
/// and case don't fragment the counters.
/// </summary>
public class EndpointHealthTests
{
    private static string? _persistBlob; // fake SQLite blob the fakes read/write

    private static void RegisterPersistenceFakes()
    {
        _persistBlob = null;
        EndpointHealthService.RegisterPersistence(
            () => _persistBlob,
            json => _persistBlob = json);
    }

    [Fact]
    public void Key_NormalizesTrailingSlashes()
    {
        Assert.Equal("http://localhost:8080", EndpointHealthService.NormalizeKey("http://localhost:8080/"));
        Assert.Equal("HTTP://GPU2:8080", EndpointHealthService.NormalizeKey("HTTP://GPU2:8080//"));
        Assert.Equal("", EndpointHealthService.NormalizeKey("   "));
        Assert.Equal("", EndpointHealthService.NormalizeKey(null));
    }

    [Fact]
    public void Key_CaseVariantCollapsesViaCaseInsensitiveDictionary()
    {
        EndpointHealthService.Reset();
        EndpointHealthService.RecordCall("HTTP://GPU2:8080", "partial", "The read operation failed.");
        EndpointHealthService.RecordCall("http://gpu2:8080/", "ok", null);
        // Same logical endpoint (case + trailing slash) must share one counter.
        Assert.Single(EndpointHealthService.Entries);
        var h = EndpointHealthService.Entries["http://gpu2:8080"];
        Assert.Equal(2L, h.Calls);
        Assert.Equal(1L, h.StreamErrors);
    }

    [Fact]
    public void Success_IncrementsCalls_NotStreamErrors()
    {
        EndpointHealthService.Reset();
        EndpointHealthService.RecordCall("http://a:1", "full response", null);
        var h = EndpointHealthService.Entries["http://a:1"];
        Assert.Equal(1L, h.Calls);
        Assert.Equal(0L, h.StreamErrors);
    }

    [Fact]
    public void StreamFailure_IncrementsCallsAndStreamErrors()
    {
        EndpointHealthService.Reset();
        EndpointHealthService.RecordCall("http://a:1", "partial data here", "The response ended prematurely.");
        var h = EndpointHealthService.Entries["http://a:1"];
        Assert.Equal(1L, h.Calls);
        Assert.Equal(1L, h.StreamErrors);
    }

    [Fact]
    public void NonTransportFailure_IncrementsCalls_NotStreamErrors()
    {
        EndpointHealthService.Reset();
        EndpointHealthService.RecordCall("http://a:1", "garbage", "JSON parse failed");
        var h = EndpointHealthService.Entries["http://a:1"];
        Assert.Equal(1L, h.Calls);
        Assert.Equal(0L, h.StreamErrors);
    }

    [Fact]
    public void EndpointsAreTrackedIndependently()
    {
        EndpointHealthService.Reset();
        EndpointHealthService.RecordCall("http://flaky:1", "partial", "The read operation failed.");
        EndpointHealthService.RecordCall("http://solid:2", "fine", null);
        EndpointHealthService.RecordCall("http://flaky:1/", "partial", "Connection reset by peer.");
        var flaky = EndpointHealthService.Entries["http://flaky:1"];
        var solid = EndpointHealthService.Entries["http://solid:2"];
        Assert.Equal(2L, flaky.Calls);
        Assert.Equal(2L, flaky.StreamErrors);
        Assert.Equal(1L, solid.Calls);
        Assert.Equal(0L, solid.StreamErrors);
    }

    [Fact]
    public void EmptyBaseUrl_IsIgnored()
    {
        EndpointHealthService.Reset();
        EndpointHealthService.RecordCall("", "partial", "The read operation failed.");
        Assert.Empty(EndpointHealthService.Entries);
    }

    // ── Persistence (SQLite round-trip) ────────────────────────────────────
    // The tracker persists through loader/saver hooks registered by the controller
    // constructor. These tests swap in in-memory fakes to lock the serialize →
    // hydrate contract and the 24h trim window.

    [Fact]
    public void Persist_WritesCountersToBlob()
    {
        EndpointHealthService.Reset();
        RegisterPersistenceFakes();
        try
        {
            EndpointHealthService.RecordCall("http://persist:1", "partial", "The read operation failed.");
            EndpointHealthService.RecordCall("http://persist:1", "full", null);
            EndpointHealthService.Persist();
            Assert.False(string.IsNullOrWhiteSpace(_persistBlob));
            using var doc = JsonDocument.Parse(_persistBlob!);
            var el = doc.RootElement.EnumerateArray().First(e => e.GetProperty("baseUrl").GetString() == "http://persist:1");
            Assert.Equal(2L, el.GetProperty("calls").GetInt64());
            Assert.Equal(1L, el.GetProperty("streamErrors").GetInt64());
            Assert.NotNull(el.GetProperty("lastSuccessUtc").GetString());
            Assert.NotNull(el.GetProperty("lastStreamErrorUtc").GetString());
        }
        finally { EndpointHealthService.Reset(); }
    }

    // Recovered/recoveryFailed counters (the recovery-effectiveness metrics surfaced in
    // the agent log and badge tooltip) must survive the same persist → hydrate round-trip.
    [Fact]
    public void RecoveryCounters_PersistAndHydrate()
    {
        EndpointHealthService.Reset();
        RegisterPersistenceFakes();
        try
        {
            EndpointHealthService.RecordCall("http://recover:1", "partial", "The read operation failed.");
            EndpointHealthService.RecordRecoveryOutcome("http://recover:1", recovered: true);
            EndpointHealthService.RecordRecoveryOutcome("http://recover:1", recovered: false);
            EndpointHealthService.RecordRecoveryOutcome("http://recover:1", recovered: true);
            EndpointHealthService.Persist();
            Assert.False(string.IsNullOrWhiteSpace(_persistBlob));
            using (var doc = JsonDocument.Parse(_persistBlob!))
            {
                var el = doc.RootElement.EnumerateArray().First(e => e.GetProperty("baseUrl").GetString() == "http://recover:1");
                Assert.Equal(2L, el.GetProperty("recovered").GetInt64());
                Assert.Equal(1L, el.GetProperty("recoveryFailed").GetInt64());
            }

            // Simulate app restart: wipe the in-memory dict + hydration flag, then load.
            EndpointHealthService.Entries.Clear();
            EndpointHealthService.ResetHydration();
            EndpointHealthService.HydrateFromDisk();

            Assert.Single(EndpointHealthService.Entries);
            var h = EndpointHealthService.Entries["http://recover:1"];
            Assert.Equal(2L, h.Recovered);
            Assert.Equal(1L, h.RecoveryFailed);
        }
        finally { EndpointHealthService.Reset(); }
    }

    [Fact]
    public void Hydrate_ToleratesBlobWithoutRecoveryFields()
    {
        // Old blobs written before the recovered/recoveryFailed fields existed must still
        // hydrate (fields default to 0) instead of throwing.
        EndpointHealthService.Reset();
        RegisterPersistenceFakes();
        try
        {
            _persistBlob = "[{\"baseUrl\":\"http://legacy:1\",\"calls\":5,\"streamErrors\":2," +
                           "\"lastStreamErrorUtc\":\"" + DateTime.UtcNow.AddMinutes(-10).ToString("o") +
                           "\",\"lastSuccessUtc\":\"" + DateTime.UtcNow.AddMinutes(-2).ToString("o") + "\"}]";
            EndpointHealthService.HydrateFromDisk(); // must not throw
            Assert.Single(EndpointHealthService.Entries);
            var h = EndpointHealthService.Entries["http://legacy:1"];
            Assert.Equal(0L, h.Recovered);
            Assert.Equal(0L, h.RecoveryFailed);
        }
        finally { EndpointHealthService.Reset(); }
    }

    [Fact]
    public void Hydrate_RestoresCountersAcrossSessions()
    {
        EndpointHealthService.Reset();
        RegisterPersistenceFakes();
        try
        {
            EndpointHealthService.RecordCall("http://roundtrip:1", "partial", "Connection reset by peer.");
            EndpointHealthService.RecordCall("http://roundtrip:1", "fine", null);
            EndpointHealthService.RecordCall("http://roundtrip:1", "ok", null);
            EndpointHealthService.Persist();
            var blob = _persistBlob; // capture what a fresh process would read

            // Simulate app restart: wipe the in-memory dict + hydration flag, then load.
            EndpointHealthService.Entries.Clear();
            EndpointHealthService.ResetHydration();
            EndpointHealthService.HydrateFromDisk();

            Assert.Single(EndpointHealthService.Entries);
            var h = EndpointHealthService.Entries["http://roundtrip:1"];
            Assert.Equal(3L, h.Calls);
            Assert.Equal(1L, h.StreamErrors);
            // Success timestamps survived the round-trip too.
            Assert.True((DateTime.UtcNow - h.LastSuccessUtc).TotalMinutes < 5);
            Assert.True(blob!.Contains("http://roundtrip:1"));
        }
        finally { EndpointHealthService.Reset(); }
    }

    [Fact]
    public void Persist_TrimsEntriesIdleOver24h()
    {
        EndpointHealthService.Reset();
        RegisterPersistenceFakes();
        try
        {
            EndpointHealthService.RecordCall("http://fresh:1", "ok", null);
            // Backdate the entry's last activity so it falls outside the 24h window.
            EndpointHealthService.Entries["http://fresh:1"].LastSuccessUtc = DateTime.UtcNow.AddHours(-30);
            EndpointHealthService.Persist();
            using var doc = JsonDocument.Parse(_persistBlob!);
            Assert.Empty(doc.RootElement.EnumerateArray());
        }
        finally { EndpointHealthService.Reset(); }
    }

    [Fact]
    public void Hydrate_SkipsStaleEntriesOver24h()
    {
        EndpointHealthService.Reset();
        RegisterPersistenceFakes();
        try
        {
            // A blob containing only a stale entry (last activity 2 days ago).
            _persistBlob = "[{\"baseUrl\":\"http://ancient:1\",\"calls\":9,\"streamErrors\":4," +
                           "\"lastStreamErrorUtc\":\"" + DateTime.UtcNow.AddDays(-2).ToString("o") +
                           "\",\"lastSuccessUtc\":\"" + DateTime.UtcNow.AddDays(-3).ToString("o") + "\"}]";
            EndpointHealthService.HydrateFromDisk();
            Assert.Empty(EndpointHealthService.Entries);
        }
        finally { EndpointHealthService.Reset(); }
    }

    [Fact]
    public void Hydrate_ToleratesCorruptBlob()
    {
        EndpointHealthService.Reset();
        RegisterPersistenceFakes();
        try
        {
            _persistBlob = "{ this is not valid json [";
            EndpointHealthService.HydrateFromDisk(); // must not throw
            Assert.Empty(EndpointHealthService.Entries);
        }
        finally { EndpointHealthService.Reset(); }
    }
}
