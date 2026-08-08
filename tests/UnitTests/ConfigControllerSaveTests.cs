using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Weaver.Controllers;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for the POST api/config/save credential handling: the preserve-if-omitted merge
/// (empty values keep the existing BugHosted credentials) versus the explicit-clear flag
/// (clearBughostedCredentials — empty values are authoritative and wipe them). The flag is
/// how the front end lets a user DELETE saved credentials, since the merge alone makes it
/// impossible to clear them.
/// </summary>
public class ConfigControllerSaveTests
{
    private static (ConfigController controller, ConfigFileService configFile, string dir) BuildHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-cfg-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var db = new DatabaseService(Path.Combine(dir, "weaver.db"), dir, Path.Combine(dir, "weaverconfig.json"));
        var configFile = new ConfigFileService(db);
        var controller = (ConfigController)RuntimeHelpers.GetUninitializedObject(typeof(ConfigController));
        typeof(ConfigController).GetField("_configFile", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(controller, configFile);
        return (controller, configFile, dir);
    }

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task SeedCredentials(ConfigFileService configFile, string user, string pass)
    {
        var cfg = await configFile.LoadConfigAsync();
        cfg.bughostedUsername = user;
        cfg.bughostedPassword = pass;
        await configFile.WriteConfigAsync(cfg);
    }

    private static async Task<FrontendConfig> Reload(ConfigFileService configFile)
        => await configFile.LoadConfigAsync();

    [Fact]
    public async Task ClearFlag_EmptyCredentials_WipesThem()
    {
        var (controller, configFile, dir) = BuildHarness();
        try
        {
            await SeedCredentials(configFile, "olduser", "oldpass");

            var result = await controller.Save(Body(
                """{"bughostedUsername":"","bughostedPassword":"","clearBughostedCredentials":true}"""));

            Assert.IsType<OkObjectResult>(result);
            var reloaded = await Reload(configFile);
            Assert.Equal("", reloaded.bughostedUsername);
            Assert.Equal("", reloaded.bughostedPassword);
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public async Task EmptyWithoutFlag_PreservesExistingCredentials()
    {
        // Regression guard: the original merge semantics — a client that omits/empties the
        // fields must not wipe stored credentials (that is exactly what made clearing hard).
        var (controller, configFile, dir) = BuildHarness();
        try
        {
            await SeedCredentials(configFile, "olduser", "oldpass");

            var result = await controller.Save(Body(
                """{"bughostedUsername":"","bughostedPassword":""}"""));

            Assert.IsType<OkObjectResult>(result);
            var reloaded = await Reload(configFile);
            Assert.Equal("olduser", reloaded.bughostedUsername);
            Assert.Equal("oldpass", reloaded.bughostedPassword);
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public async Task NewCredentials_OverwriteWithoutFlag()
    {
        var (controller, configFile, dir) = BuildHarness();
        try
        {
            await SeedCredentials(configFile, "olduser", "oldpass");

            var result = await controller.Save(Body(
                """{"bughostedUsername":"newuser","bughostedPassword":"newpass"}"""));

            Assert.IsType<OkObjectResult>(result);
            var reloaded = await Reload(configFile);
            Assert.Equal("newuser", reloaded.bughostedUsername);
            Assert.Equal("newpass", reloaded.bughostedPassword);
        }
        finally { TryCleanup(dir); }
    }

    private static void TryCleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
