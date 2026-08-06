using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Weaver.Services;

/// <summary>
/// Per-endpoint stream reliability tracker powering the endpoint-picker health badge.
/// Moved out of AgentController (previously AgentController.Llm.cs + the persistence
/// hooks wired in the controller constructor) so the Recovered/StreamError counters,
/// the shared 24h idle-trim window, and the SQLite persistence round-trip are testable
/// directly instead of via reflection into the controller.
/// </summary>
public static class EndpointHealthService
{
    /// <summary>Per-endpoint stream reliability counters, keyed by normalized base URL.
    /// Feeds the health badge in the endpoint picker so flaky endpoints (frequent
    /// mid-stream drops) are visible at a glance.</summary>
    public sealed class EndpointStreamHealth
    {
        public long Calls;
        public long StreamErrors;
        public long Recovered;       // recovery retries that landed (finish-this / hint retry succeeded)
        public long RecoveryFailed;  // recovery retries that still failed
        public DateTime LastStreamErrorUtc;
        public DateTime LastSuccessUtc;
    }

    /// <summary>Config key under which the tracker blob is persisted (weaver_config table).</summary>
    public const string DbKey = "endpoint_stream_health";

    // Debounce between SQLite writes from the hot recording path (10s), and the window
    // beyond which an idle endpoint's counters are considered stale and dropped everywhere
    // (persist, hydrate, and the HTTP endpoint share this so what's served == what's saved).
    private const long PersistDebounceTicks = TimeSpan.TicksPerSecond * 10;
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);

    /// <summary>The live tracker, keyed by normalized endpoint base URL (case-insensitive).</summary>
    public static readonly ConcurrentDictionary<string, EndpointStreamHealth> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    // Persistence hooks — registered once by the AgentController constructor so the tracker
    // can round-trip through the injected DatabaseService. When unregistered (e.g. in unit
    // tests before fakes are wired) the tracker stays purely in-memory.
    private static Func<string?>? _loader;
    private static Action<string>? _saver;
    private static bool _hydrated;
    private static long _lastPersistTicks;

    /// <summary>Normalizes an endpoint URL to the dictionary key (trim trailing slash).</summary>
    public static string NormalizeKey(string? baseUrl)
        => string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl.Trim().TrimEnd('/');

    /// <summary>True when an endpoint's last recorded activity is older than the 24h window.</summary>
    public static bool IsStale(EndpointStreamHealth h, DateTime now)
    {
        var last = h.LastSuccessUtc > h.LastStreamErrorUtc ? h.LastSuccessUtc : h.LastStreamErrorUtc;
        return last != default && (now - last) > StaleAge;
    }

    /// <summary>
    /// Registers the SQLite-backed load/save hooks. No-op if already set. The check-then-act
    /// is technically racy under concurrent controller construction, but benign: every
    /// AgentController captures the same DI singleton DatabaseService, so whichever instance
    /// wins the race installs functionally identical hooks.
    /// </summary>
    public static void RegisterPersistence(Func<string?>? loader, Action<string>? saver)
    {
        if (_loader == null && _saver == null)
        {
            _loader = loader;
            _saver = saver;
            _hydrated = false;
        }
    }

    /// <summary>
    /// Restores the tracker from disk on process start. Entries whose last activity is
    /// older than 24h are dropped so removed endpoints don't resurrect forever — the same
    /// window the persist path and the HTTP endpoint enforce.
    /// </summary>
    public static void HydrateFromDisk()
    {
        if (_hydrated || _loader == null) return;
        _hydrated = true;
        try
        {
            var json = _loader();
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            var now = DateTime.UtcNow;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var key = el.TryGetProperty("baseUrl", out var urlEl) ? urlEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(key)) continue;
                var h = Entries.GetOrAdd(key, _ => new EndpointStreamHealth());
                if (el.TryGetProperty("calls", out var cEl) && cEl.TryGetInt64(out var calls)) h.Calls = calls;
                if (el.TryGetProperty("streamErrors", out var eEl) && eEl.TryGetInt64(out var errors)) h.StreamErrors = errors;
                if (el.TryGetProperty("recovered", out var recEl) && recEl.TryGetInt64(out var rec)) h.Recovered = rec;
                if (el.TryGetProperty("recoveryFailed", out var rfEl) && rfEl.TryGetInt64(out var rf)) h.RecoveryFailed = rf;
                // RoundtripKind keeps the UTC kind of the persisted "o" strings, so the
                // 24h-trim comparison against DateTime.UtcNow isn't skewed by the local
                // timezone offset (plain TryParse would convert Z timestamps to Local).
                if (el.TryGetProperty("lastStreamErrorUtc", out var lseEl) &&
                    DateTime.TryParse(lseEl.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var lse)) h.LastStreamErrorUtc = lse;
                if (el.TryGetProperty("lastSuccessUtc", out var lsuEl) &&
                    DateTime.TryParse(lsuEl.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var lsu)) h.LastSuccessUtc = lsu;
                if (IsStale(h, now))
                    Entries.TryRemove(key, out _);
            }
        }
        catch { /* corrupt or unreadable persisted blob — start fresh */ }
    }

    /// <summary>
    /// Serializes the tracker to the registered saver. Prunes entries whose last activity
    /// is older than 24h so removed endpoints don't linger on disk forever. Synchronous so
    /// it can be called from the HTTP endpoint; the recording path uses the debounced wrapper.
    /// </summary>
    public static void Persist()
    {
        var saver = _saver;
        if (saver == null) return;
        try
        {
            var now = DateTime.UtcNow;
            var items = new List<object>();
            // NOTE: blob field names (…Utc) differ from the HTTP response (…At) on purpose —
            // this is the internal persistence format; the endpoint builds its own contract.
            foreach (var kv in Entries)
            {
                if (IsStale(kv.Value, now)) continue; // trim stale entries
                items.Add(new
                {
                    baseUrl = kv.Key,
                    calls = kv.Value.Calls,
                    streamErrors = kv.Value.StreamErrors,
                    recovered = kv.Value.Recovered,
                    recoveryFailed = kv.Value.RecoveryFailed,
                    lastStreamErrorUtc = kv.Value.LastStreamErrorUtc == default
                        ? null : kv.Value.LastStreamErrorUtc.ToString("o"),
                    lastSuccessUtc = kv.Value.LastSuccessUtc == default
                        ? null : kv.Value.LastSuccessUtc.ToString("o")
                });
            }
            saver(JsonSerializer.Serialize(items));
        }
        catch { /* persistence is best-effort */ }
    }

    /// <summary>
    /// Debounced persist for the hot recording path — at most one SQLite write every 10s,
    /// executed synchronously. The debounce keeps the write frequency trivial (one small
    /// JSON blob per 10s per process), and a synchronous write removes any async race where
    /// a stale background flush could overwrite newer counters.
    /// </summary>
    private static void MaybePersist()
    {
        if (_saver == null) return;
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastPersistTicks);
        if (now - last < PersistDebounceTicks) return;
        if (Interlocked.CompareExchange(ref _lastPersistTicks, now, last) != last) return;
        Persist();
    }

    /// <summary>
    /// Records one completed LLM attempt for the endpoint health tracker. A call counts as
    /// a stream error when it failed with a transport/stream/truncation problem (the same
    /// predicate that triggers recovery), so the badge reflects reliability, not LLM quality.
    /// </summary>
    public static void RecordCall(string? baseUrl, string? partial, string? error)
    {
        HydrateFromDisk();
        var key = NormalizeKey(baseUrl);
        if (key.Length == 0) return;
        var h = Entries.GetOrAdd(key, _ => new EndpointStreamHealth());
        Interlocked.Increment(ref h.Calls);
        if (string.IsNullOrWhiteSpace(error))
        {
            h.LastSuccessUtc = DateTime.UtcNow;
        }
        else if (TransientFailureDetector.IsTransientTransportFailure(error) || TransientFailureDetector.IsRecoverableStreamFailure(partial, error))
        {
            // A recovered call counts as a stream error on the FIRST attempt plus a success
            // on the retry — intentional: the badge measures connection drops, not task
            // outcomes, so a drop that was healed still shows up as a reliability blip.
            Interlocked.Increment(ref h.StreamErrors);
            h.LastStreamErrorUtc = DateTime.UtcNow;
        }
        MaybePersist();
    }

    /// <summary>
    /// Records the OUTCOME of a recovery retry (finish-this continuation, hint retry, or
    /// non-streaming transport retry) for the endpoint health tracker. Returns the updated
    /// recovered/failed counts so the caller can surface them in the agent log as a metric.
    /// </summary>
    public static (long Recovered, long RecoveryFailed) RecordRecoveryOutcome(string? baseUrl, bool recovered)
    {
        var key = NormalizeKey(baseUrl);
        if (key.Length == 0) return (0, 0);
        var h = Entries.GetOrAdd(key, _ => new EndpointStreamHealth());
        if (recovered) Interlocked.Increment(ref h.Recovered);
        else Interlocked.Increment(ref h.RecoveryFailed);
        MaybePersist();
        return (h.Recovered, h.RecoveryFailed);
    }

    /// <summary>
    /// Drops entries whose last activity is older than the 24h window. Called by the HTTP
    /// endpoint so the served snapshot matches what was just persisted (same staleness rule).
    /// </summary>
    public static void PruneStale()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in Entries.ToList())
        {
            if (IsStale(kv.Value, now))
                Entries.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>Clears the tracker, persistence hooks, and hydration state — test isolation.</summary>
    public static void Reset()
    {
        Entries.Clear();
        _loader = null;
        _saver = null;
        _hydrated = false;
        _lastPersistTicks = 0;
    }

    /// <summary>
    /// Forces the next HydrateFromDisk call to re-read the persisted blob (simulating an app
    /// restart while the persistence hooks stay registered) — test helper.
    /// </summary>
    public static void ResetHydration()
    {
        _hydrated = false;
    }
}
