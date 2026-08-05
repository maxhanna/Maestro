using System.Reflection;
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
}
