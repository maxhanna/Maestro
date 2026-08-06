using Microsoft.Extensions.Configuration;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the Editor:DisableLLMRetries toggle. When true, every LLM recovery retry is
/// skipped (non-streaming transport retry, streaming hint retry, prose retry, and the
/// finish-this continuation loop) so flaky endpoints fail fast instead of adding a
/// 300ms pause + full re-stream per call. The helper must default to false (retries
/// on) and tolerate a missing/malformed config value.
/// </summary>
public class LlmRetryDisableTests
{
    private static bool LlmRetriesDisabled(IConfiguration? cfg)
    {
        var method = typeof(AgentController).GetMethod(
            "LlmRetriesDisabled",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[] { cfg })!;
    }

    private static IConfiguration ConfigWith(string? value)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Editor:DisableLLMRetries"] = value
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void Null_Config_DefaultsToRetriesEnabled()
    {
        Assert.False(LlmRetriesDisabled(null));
    }

    [Fact]
    public void Missing_Key_DefaultsToRetriesEnabled()
    {
        var cfg = new ConfigurationBuilder().Build();
        Assert.False(LlmRetriesDisabled(cfg));
    }

    [Fact]
    public void Explicit_False_KeepsRetriesEnabled()
    {
        Assert.False(LlmRetriesDisabled(ConfigWith("false")));
    }

    [Fact]
    public void True_DisablesRetries()
    {
        Assert.True(LlmRetriesDisabled(ConfigWith("true")));
    }

    [Fact]
    public void CaseInsensitive_True_DisablesRetries()
    {
        Assert.True(LlmRetriesDisabled(ConfigWith("TRUE")));
    }

    [Fact]
    public void Garbage_Value_DefaultsToRetriesEnabled()
    {
        Assert.False(LlmRetriesDisabled(ConfigWith("definitely-not-a-bool")));
    }
}
