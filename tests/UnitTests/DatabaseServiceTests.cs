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

    private void WriteLegacyFile(string name, string json)
        => System.IO.File.WriteAllText(Path.Combine(_root, name), json);

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

    // ── Legacy file cleanup (board, calendar, hints, vapid, benchmark, system info) ──

    [Fact]
    public void FreshDb_ImportsAllLegacyFiles_AndDeletesThem()
    {
        WriteLegacyFile("board.json", "{\"cards\": []}");
        WriteLegacyFile(".calendardata", "[{\"id\": 1}]");
        WriteLegacyFile("filehints.json", "{\"hints\": []}");
        WriteLegacyFile("vapid-keys.json", "{\"PublicKey\":\"pk\",\"PrivateKey\":\"sk\"}");
        WriteLegacyFile("benchmark_scores.json", "[{\"score\": 1}]");
        WriteLegacyFile("system_info.json", "{\"os\":\"win\"}");

        var db = new DatabaseService(DbPath, _root, ConfigPath);

        foreach (var name in new[] { "board.json", ".calendardata", "filehints.json", "vapid-keys.json", "benchmark_scores.json", "system_info.json" })
            Assert.False(System.IO.File.Exists(Path.Combine(_root, name)), name + " must be deleted after import");
        Assert.NotNull(db.GetBoardData());
        Assert.NotNull(db.GetCalendarData());
        Assert.NotNull(db.GetFileHints());
        Assert.NotNull(db.GetVapidKeys());
        Assert.NotNull(db.GetSystemInfo());
        Assert.NotNull(db.GetValue("benchmark_scores_json"));
    }

    [Fact]
    public void FreshDb_ImportsEditKnowledgeFiles_AndDeletesThem()
    {
        WriteLegacyFile(".project_acme_edit_knowledge.json", "{\"entry\": 1}");
        WriteLegacyFile(".project_zeta_edit_knowledge.json", "{\"entry\": 2}");

        var db = new DatabaseService(DbPath, _root, ConfigPath);

        Assert.False(System.IO.File.Exists(Path.Combine(_root, ".project_acme_edit_knowledge.json")));
        Assert.False(System.IO.File.Exists(Path.Combine(_root, ".project_zeta_edit_knowledge.json")));
        Assert.NotNull(db.GetEditKnowledge("acme"));
        Assert.NotNull(db.GetEditKnowledge("zeta"));
    }

    [Fact]
    public void ExistingDb_WithLeftoverLegacyFiles_CleansUpOnStartup()
    {
        // First start migrates the files and deletes them.
        WriteLegacyFile("board.json", "{\"cards\": [{\"id\":\"a\"}]}");
        WriteLegacyFile("vapid-keys.json", "{\"PublicKey\":\"pk\",\"PrivateKey\":\"sk\"}");
        WriteLegacyFile(".project_acme_edit_knowledge.json", "{\"entry\": 1}");
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "board.json")));

        // The files reappear (e.g. restored from a backup) — a second start must
        // delete them because the DB already holds their data.
        WriteLegacyFile("board.json", "{\"cards\": [{\"id\":\"b\"}]}");
        WriteLegacyFile("vapid-keys.json", "{\"PublicKey\":\"pk\",\"PrivateKey\":\"sk\"}");
        WriteLegacyFile(".project_acme_edit_knowledge.json", "{\"entry\": 1}");
        var db2 = new DatabaseService(DbPath, _root, ConfigPath);

        Assert.False(System.IO.File.Exists(Path.Combine(_root, "board.json")), "leftover board.json must be cleaned when DB already migrated");
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "vapid-keys.json")), "leftover vapid-keys.json must be cleaned when DB already migrated");
        Assert.False(System.IO.File.Exists(Path.Combine(_root, ".project_acme_edit_knowledge.json")), "leftover edit-knowledge file must be cleaned when DB already migrated");
    }

    [Fact]
    public void DbWithoutMigratedData_KeepsUnimportedFiles()
    {
        // A DB that has *some* data (so migration won't re-run) but no rows for
        // these tables: the files must be left alone — deleting them would lose data.
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        db.SetLocalVersion("1");

        WriteLegacyFile("board.json", "{\"cards\": []}");
        WriteLegacyFile("vapid-keys.json", "{\"PublicKey\":\"pk\",\"PrivateKey\":\"sk\"}");
        WriteLegacyFile(".project_acme_edit_knowledge.json", "{\"entry\": 1}");

        var db2 = new DatabaseService(DbPath, _root, ConfigPath);

        Assert.True(System.IO.File.Exists(Path.Combine(_root, "board.json")), "unimported board.json must not be deleted");
        Assert.True(System.IO.File.Exists(Path.Combine(_root, "vapid-keys.json")), "unimported vapid-keys.json must not be deleted");
        Assert.True(System.IO.File.Exists(Path.Combine(_root, ".project_acme_edit_knowledge.json")), "unimported edit-knowledge file must not be deleted");
        Assert.Null(db2.GetBoardData());
    }

    [Fact]
    public void EmptyLegacyFiles_AreCleanedEvenWithoutMigration()
    {
        // Some data → migration won't re-run on the second start.
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        db.SetLocalVersion("1");

        // Zero-byte / whitespace-only legacy files hold no data worth keeping —
        // the startup cleanup must remove them even though nothing was imported.
        WriteLegacyFile("vapid-keys.json", "");
        WriteLegacyFile("board.json", "   ");

        var db2 = new DatabaseService(DbPath, _root, ConfigPath);

        Assert.False(System.IO.File.Exists(Path.Combine(_root, "vapid-keys.json")), "empty legacy file must not linger");
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "board.json")), "whitespace-only legacy file must not linger");
    }

    // ── DB-backed local version ────────────────────────────────────────────

    [Fact]
    public void FreshDb_WithLegacyVersionFile_ImportsItAndDeletesFile()
    {
        // _root is the data dir, so a version file next to it is one of the
        // historical locations the migration scans.
        var legacyFile = Path.Combine(_root, ".weaver-version.txt");
        System.IO.File.WriteAllText(legacyFile, "12");

        var db = new DatabaseService(DbPath, _root, ConfigPath);

        Assert.False(System.IO.File.Exists(legacyFile), "legacy version file must be deleted after import");
        Assert.Equal("12", db.GetLocalVersion());
    }

    [Fact]
    public void FreshDb_WithoutVersionSource_SeedsZero()
    {
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        Assert.Equal("0", db.GetLocalVersion());
    }

    [Fact]
    public void ReappearedVersionFile_OverridesSeedZero_AndIsDeleted()
    {
        // First start seeds "0" (no file present) and persists it in the DB.
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        Assert.Equal("0", db.GetLocalVersion());

        // A version file reappears (e.g. restored from a backup) — the seeded
        // "0" is only the "unknown" placeholder, so the real version must win
        // rather than being swept and lost.
        var legacyFile = Path.Combine(_root, ".weaver-version");
        System.IO.File.WriteAllText(legacyFile, "99");
        var db2 = new DatabaseService(DbPath, _root, ConfigPath);

        Assert.False(System.IO.File.Exists(legacyFile), "version file must be deleted after import");
        Assert.Equal("99", db2.GetLocalVersion());
    }

    [Fact]
    public void LocalVersion_RoundTripsThroughDatabase()
    {
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        Assert.Equal("0", db.GetLocalVersion()); // seeded on first startup

        db.SetLocalVersion("12");
        Assert.Equal("12", db.GetLocalVersion());

        db.SetLocalVersion("13");
        Assert.Equal("13", db.GetLocalVersion());
    }

    // ── Runtime probe cache (per project) ──────────────────────────────────

    [Fact]
    public void RuntimeProbe_MissingProject_ReturnsNull()
    {
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        Assert.Null(db.GetRuntimeProbe("no-such-project"));
    }

    [Fact]
    public void RuntimeProbe_RoundTripsPerProject_WithoutCrossContamination()
    {
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        db.SetRuntimeProbe("proj-a", "{\"probes\":[{\"name\":\"python\"}]}");
        db.SetRuntimeProbe("proj-b", "{\"probes\":[{\"name\":\"node\"}]}");

        Assert.Contains("python", db.GetRuntimeProbe("proj-a"));
        Assert.Contains("node", db.GetRuntimeProbe("proj-b"));
        Assert.DoesNotContain("node", db.GetRuntimeProbe("proj-a"));
    }

    [Fact]
    public void RuntimeProbe_OverwriteUpdatesInPlace()
    {
        var db = new DatabaseService(DbPath, _root, ConfigPath);
        db.SetRuntimeProbe("proj", "v1");
        db.SetRuntimeProbe("proj", "v2");
        Assert.Equal("v2", db.GetRuntimeProbe("proj"));
    }
}
