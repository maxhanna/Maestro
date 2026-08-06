using System.Reflection;
using System.Text.Json;
using Xunit;
using Weaver;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the per-endpoint stream-reliability tracker that powers the endpoint-picker health
/// badge. RecordEndpointCall must count transport/stream/truncation failures as stream errors
/// (the same predicates that drive recovery) while successful calls and non-transport LLM
/// failures (JSON parse, hallucination, empty) only move the call/error counters appropriately.
/// EndpointHealthKey normalizes URLs so trailing slashes and case don't fragment the counters.
/// </summary>
public class EndpointHealthTests
{
    private static string EndpointHealthKey(string? baseUrl)
    {
        var method = typeof(AgentController).GetMethod(
            "EndpointHealthKey", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { baseUrl })!;
    }

    private static void RecordEndpointCall(string? baseUrl, string? partial, string? error)
    {
        var method = typeof(AgentController).GetMethod(
            "RecordEndpointCall", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, new object?[] { baseUrl, partial, error });
    }

    private static FieldInfo HealthDictField()
    {
        var field = typeof(AgentController).GetField(
            "_endpointStreamHealth", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return field!;
    }

    private static System.Collections.IDictionary HealthDict()
        => (System.Collections.IDictionary)HealthDictField().GetValue(null)!;

    private static void ResetHealth()
    {
        var dict = HealthDict();
        dict.Clear();
    }

    // The value is the private nested EndpointStreamHealth class — read its fields via
    // reflection since it's not visible to the test assembly.
    private static long HealthCalls(object h)
    {
        var f = h.GetType().GetField("Calls", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(f);
        return (long)f!.GetValue(h)!;
    }

    private static long HealthStreamErrors(object h)
    {
        var f = h.GetType().GetField("StreamErrors", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(f);
        return (long)f!.GetValue(h)!;
    }

    [Fact]
    public void Key_NormalizesTrailingSlashes()
    {
        Assert.Equal("http://localhost:8080", EndpointHealthKey("http://localhost:8080/"));
        Assert.Equal("HTTP://GPU2:8080", EndpointHealthKey("HTTP://GPU2:8080//"));
        Assert.Equal("", EndpointHealthKey("   "));
        Assert.Equal("", EndpointHealthKey(null));
    }

    [Fact]
    public void Key_CaseVariantCollapsesViaCaseInsensitiveDictionary()
    {
        ResetHealth();
        RecordEndpointCall("HTTP://GPU2:8080", "partial", "The read operation failed.");
        RecordEndpointCall("http://gpu2:8080/", "ok", null);
        // Same logical endpoint (case + trailing slash) must share one counter.
        Assert.Equal(1, HealthDict().Count);
        var h = HealthDict()["http://gpu2:8080"];
        Assert.Equal(2L, HealthCalls(h));
        Assert.Equal(1L, HealthStreamErrors(h));
    }

    [Fact]
    public void Success_IncrementsCalls_NotStreamErrors()
    {
        ResetHealth();
        RecordEndpointCall("http://a:1", "full response", null);
        var h = HealthDict()["http://a:1"];
        Assert.Equal(1L, HealthCalls(h));
        Assert.Equal(0L, HealthStreamErrors(h));
    }

    [Fact]
    public void StreamFailure_IncrementsCallsAndStreamErrors()
    {
        ResetHealth();
        RecordEndpointCall("http://a:1", "partial data here", "The response ended prematurely.");
        var h = HealthDict()["http://a:1"];
        Assert.Equal(1L, HealthCalls(h));
        Assert.Equal(1L, HealthStreamErrors(h));
    }

    [Fact]
    public void NonTransportFailure_IncrementsCalls_NotStreamErrors()
    {
        ResetHealth();
        RecordEndpointCall("http://a:1", "garbage", "JSON parse failed");
        var h = HealthDict()["http://a:1"];
        Assert.Equal(1L, HealthCalls(h));
        Assert.Equal(0L, HealthStreamErrors(h));
    }

    [Fact]
    public void EndpointsAreTrackedIndependently()
    {
        ResetHealth();
        RecordEndpointCall("http://flaky:1", "partial", "The read operation failed.");
        RecordEndpointCall("http://solid:2", "fine", null);
        RecordEndpointCall("http://flaky:1/", "partial", "Connection reset by peer.");
        var flaky = HealthDict()["http://flaky:1"];
        var solid = HealthDict()["http://solid:2"];
        Assert.Equal(2L, HealthCalls(flaky));
        Assert.Equal(2L, HealthStreamErrors(flaky));
        Assert.Equal(1L, HealthCalls(solid));
        Assert.Equal(0L, HealthStreamErrors(solid));
    }

    [Fact]
    public void EmptyBaseUrl_IsIgnored()
    {
        ResetHealth();
        RecordEndpointCall("", "partial", "The read operation failed.");
        Assert.Empty(HealthDict());
    }

    // ── Persistence (SQLite round-trip) ────────────────────────────────────
    // The static tracker persists through loader/saver hooks registered by the
    // controller constructor. These tests swap in in-memory fakes to lock the
    // serialize → hydrate contract and the 24h trim window.

    private static string? _persistBlob; // fake SQLite blob the fakes read/write

    private static void RegisterPersistenceFakes()
    {
        _persistBlob = null;
        var register = typeof(AgentController).GetMethod(
            "RegisterEndpointHealthPersistence", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(register);
        register!.Invoke(null, new object?[]
        {
            (Func<string?>)(() => _persistBlob),
            (Action<string>)(json => _persistBlob = json)
        });
    }

    private static void InvokePersist()
    {
        var m = typeof(AgentController).GetMethod(
            "PersistEndpointHealth", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        m!.Invoke(null, null);
    }

    private static void InvokeHydrate()
    {
        var m = typeof(AgentController).GetMethod(
            "HydrateEndpointHealthFromDisk", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        m!.Invoke(null, null);
    }

    private static void ResetPersistenceState()
    {
        var loaderField = typeof(AgentController).GetField(
            "_endpointHealthLoader", BindingFlags.NonPublic | BindingFlags.Static);
        var saverField = typeof(AgentController).GetField(
            "_endpointHealthSaver", BindingFlags.NonPublic | BindingFlags.Static);
        var hydratedField = typeof(AgentController).GetField(
            "_endpointHealthHydrated", BindingFlags.NonPublic | BindingFlags.Static);
        var lastPersistField = typeof(AgentController).GetField(
            "_lastHealthPersistTicks", BindingFlags.NonPublic | BindingFlags.Static);
        loaderField!.SetValue(null, null);
        saverField!.SetValue(null, null);
        hydratedField!.SetValue(null, false);
        lastPersistField!.SetValue(null, 0L);
    }

    [Fact]
    public void Persist_WritesCountersToBlob()
    {
        ResetHealth();
        ResetPersistenceState();
        RegisterPersistenceFakes();
        try
        {
            RecordEndpointCall("http://persist:1", "partial", "The read operation failed.");
            RecordEndpointCall("http://persist:1", "full", null);
            InvokePersist();
            Assert.False(string.IsNullOrWhiteSpace(_persistBlob));
            using var doc = JsonDocument.Parse(_persistBlob!);
            var el = doc.RootElement.EnumerateArray().First(e => e.GetProperty("baseUrl").GetString() == "http://persist:1");
            Assert.Equal(2L, el.GetProperty("calls").GetInt64());
            Assert.Equal(1L, el.GetProperty("streamErrors").GetInt64());
            Assert.NotNull(el.GetProperty("lastSuccessUtc").GetString());
            Assert.NotNull(el.GetProperty("lastStreamErrorUtc").GetString());
        }
        finally { ResetPersistenceState(); ResetHealth(); }
    }

    // Recovered/recoveryFailed counters (the recovery-effectiveness metrics surfaced in
    // the agent log and badge tooltip) must survive the same persist → hydrate round-trip.
    private static void RecordRecoveryOutcome(string? baseUrl, bool recovered)
    {
        var key = EndpointHealthKey(baseUrl);
        if (key.Length == 0) return;
        var dict = HealthDict();
        if (!dict.Contains(key)) dict.Add(key, Activator.CreateInstance(HealthDictField().FieldType.GetGenericArguments()[1])!);
        var h = dict[key]!;
        var f = h.GetType().GetField(recovered ? "Recovered" : "RecoveryFailed", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(f);
        f!.SetValue(h, (long)f.GetValue(h)! + 1);
    }

    private static long HealthField(object h, string name)
    {
        var f = h.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(f);
        return (long)f!.GetValue(h)!;
    }

    [Fact]
    public void RecoveryCounters_PersistAndHydrate()
    {
        ResetHealth();
        ResetPersistenceState();
        RegisterPersistenceFakes();
        try
        {
            RecordEndpointCall("http://recover:1", "partial", "The read operation failed.");
            RecordRecoveryOutcome("http://recover:1", recovered: true);
            RecordRecoveryOutcome("http://recover:1", recovered: false);
            RecordRecoveryOutcome("http://recover:1", recovered: true);
            InvokePersist();
            Assert.False(string.IsNullOrWhiteSpace(_persistBlob));
            using (var doc = JsonDocument.Parse(_persistBlob!))
            {
                var el = doc.RootElement.EnumerateArray().First(e => e.GetProperty("baseUrl").GetString() == "http://recover:1");
                Assert.Equal(2L, el.GetProperty("recovered").GetInt64());
                Assert.Equal(1L, el.GetProperty("recoveryFailed").GetInt64());
            }

            // Simulate app restart: wipe the in-memory dict + hydration flag, then load.
            ResetHealth();
            var hydratedField = typeof(AgentController).GetField(
                "_endpointHealthHydrated", BindingFlags.NonPublic | BindingFlags.Static);
            hydratedField!.SetValue(null, false);
            InvokeHydrate();

            Assert.Equal(1, HealthDict().Count);
            var h = HealthDict()["http://recover:1"];
            Assert.Equal(2L, HealthField(h, "Recovered"));
            Assert.Equal(1L, HealthField(h, "RecoveryFailed"));
        }
        finally { ResetPersistenceState(); ResetHealth(); }
    }

    [Fact]
    public void Hydrate_ToleratesBlobWithoutRecoveryFields()
    {
        // Old blobs written before the recovered/recoveryFailed fields existed must still
        // hydrate (fields default to 0) instead of throwing.
        ResetHealth();
        ResetPersistenceState();
        RegisterPersistenceFakes();
        try
        {
            _persistBlob = "[{\"baseUrl\":\"http://legacy:1\",\"calls\":5,\"streamErrors\":2," +
                           "\"lastStreamErrorUtc\":\"" + DateTime.UtcNow.AddMinutes(-10).ToString("o") +
                           "\",\"lastSuccessUtc\":\"" + DateTime.UtcNow.AddMinutes(-2).ToString("o") + "\"}]";
            InvokeHydrate(); // must not throw
            Assert.Equal(1, HealthDict().Count);
            var h = HealthDict()["http://legacy:1"];
            Assert.Equal(0L, HealthField(h, "Recovered"));
            Assert.Equal(0L, HealthField(h, "RecoveryFailed"));
        }
        finally { ResetPersistenceState(); ResetHealth(); }
    }

    [Fact]
    public void Hydrate_RestoresCountersAcrossSessions()
    {
        ResetHealth();
        ResetPersistenceState();
        RegisterPersistenceFakes();
        try
        {
            RecordEndpointCall("http://roundtrip:1", "partial", "Connection reset by peer.");
            RecordEndpointCall("http://roundtrip:1", "fine", null);
            RecordEndpointCall("http://roundtrip:1", "ok", null);
            InvokePersist();
            var blob = _persistBlob; // capture what a fresh process would read

            // Simulate app restart: wipe the in-memory dict + hydration flag, then load.
            ResetHealth();
            var hydratedField = typeof(AgentController).GetField(
                "_endpointHealthHydrated", BindingFlags.NonPublic | BindingFlags.Static);
            hydratedField!.SetValue(null, false);
            InvokeHydrate();

            Assert.Equal(1, HealthDict().Count);
            var h = HealthDict()["http://roundtrip:1"];
            Assert.Equal(3L, HealthCalls(h));
            Assert.Equal(1L, HealthStreamErrors(h));
            // Success timestamps survived the round-trip too.
            var successField = h.GetType().GetField("LastSuccessUtc", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(successField);
            var ts = (DateTime)successField!.GetValue(h)!;
            Assert.True((DateTime.UtcNow - ts).TotalMinutes < 5);
            Assert.True(blob!.Contains("http://roundtrip:1"));
        }
        finally { ResetPersistenceState(); ResetHealth(); }
    }

    [Fact]
    public void Persist_TrimsEntriesIdleOver24h()
    {
        ResetHealth();
        ResetPersistenceState();
        RegisterPersistenceFakes();
        try
        {
            RecordEndpointCall("http://fresh:1", "ok", null);
            // Backdate the entry's last activity so it falls outside the 24h window.
            var h = HealthDict()["http://fresh:1"];
            var successField = h.GetType().GetField("LastSuccessUtc", BindingFlags.Instance | BindingFlags.Public);
            successField!.SetValue(h, DateTime.UtcNow.AddHours(-30));
            InvokePersist();
            using var doc = JsonDocument.Parse(_persistBlob!);
            Assert.Empty(doc.RootElement.EnumerateArray());
        }
        finally { ResetPersistenceState(); ResetHealth(); }
    }

    [Fact]
    public void Hydrate_SkipsStaleEntriesOver24h()
    {
        ResetHealth();
        ResetPersistenceState();
        RegisterPersistenceFakes();
        try
        {
            // A blob containing only a stale entry (last activity 2 days ago).
            _persistBlob = "[{\"baseUrl\":\"http://ancient:1\",\"calls\":9,\"streamErrors\":4," +
                           "\"lastStreamErrorUtc\":\"" + DateTime.UtcNow.AddDays(-2).ToString("o") +
                           "\",\"lastSuccessUtc\":\"" + DateTime.UtcNow.AddDays(-3).ToString("o") + "\"}]";
            InvokeHydrate();
            Assert.Empty(HealthDict());
        }
        finally { ResetPersistenceState(); ResetHealth(); }
    }

    [Fact]
    public void Hydrate_ToleratesCorruptBlob()
    {
        ResetHealth();
        ResetPersistenceState();
        RegisterPersistenceFakes();
        try
        {
            _persistBlob = "{ this is not valid json [";
            InvokeHydrate(); // must not throw
            Assert.Empty(HealthDict());
        }
        finally { ResetPersistenceState(); ResetHealth(); }
    }
}
