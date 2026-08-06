using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for <see cref="DatabaseService"/> file→DB migration cleanup: the legacy
/// weaverconfig.json must be imported once and then deleted (config lives in the
/// weaver_config table only), a leftover file must be cleaned up even when the DB
/// was migrated by an older app version, a file whose data was never imported must
/// NOT be deleted, and the app's local version is persisted in the DB.
/// </summary>
public class DatabaseServiceTests : IDisposable
{
    private readonly string _root;

    public DatabaseServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "weaver_db_cleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string DbPath => Path.Combine(_root, "weaver.db");
    private string ConfigPath => Path.Combine(_root, "weaverconfig.json");

    private void WriteLegacyConfig(string json = "{\"showTerminal\": true, \"meetingVolume\": 70}")
        => System.IO.File.WriteAllText(ConfigPath, json);

    // ── Legacy config cleanup ──────────────────────────────────────────────

    [Fact]
    public void FreshDb_ImportsLegacyConfig_AndDeletesFile()
    {
        WriteLegacyConfig();

        var db = new DatabaseService(DbPath, _root, ConfigPath);

        Assert.False(System.IO.File.Exists(ConfigPath), "legacy config file must be deleted after import");
        var stored = db.GetValue("weaver_config", "config");
        Assert.NotNull(stored);
        Assert.Contains("meetingVolume", stored!);
    }

    [Fact]
    public void ExistingDb_WithLeftoverFile_CleansUpOnStartup()
    {
        // First start migrates the file and deletes it.
        WriteLegacyConfig();
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        Assert.False(System.IO.File.Exists(ConfigPath));

        // The file reappears (e.g. restored from a backup) — a second start must
        // delete it because the DB already holds the config.
        WriteLegacyConfig("{\"showNotes\": true}");
        var db2 = new DatabaseService(DbPath, _root, ConfigPath);

        Assert.False(System.IO.File.Exists(ConfigPath), "leftover config file must be cleaned even when DB already migrated");
        Assert.Contains("meetingVolume", db2.GetValue("weaver_config", "config")!);
    }

    [Fact]
    public void DbWithoutConfig_KeepsUnimportedFile()
    {
        // A DB that has *some* data (so migration won't re-run) but no config row:
        // the file must be left alone — deleting it would lose the user's settings.
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        db.SetLocalVersion("1");

        WriteLegacyConfig();

        var db2 = new DatabaseService(DbPath, _root, ConfigPath);

        Assert.True(System.IO.File.Exists(ConfigPath), "unimported config file must not be deleted");
        Assert.Null(db2.GetValue("weaver_config", "config"));
    }

    // ── DB-backed local version ────────────────────────────────────────────

    [Fact]
    public void LocalVersion_RoundTripsThroughDatabase()
    {
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        Assert.Null(db.GetLocalVersion());

        db.SetLocalVersion("12");
        Assert.Equal("12", db.GetLocalVersion());

        db.SetLocalVersion("13");
        Assert.Equal("13", db.GetLocalVersion());
    }
}
