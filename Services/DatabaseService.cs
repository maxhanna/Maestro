using Microsoft.Data.Sqlite;

namespace Weaver.Services;

/// <summary>
/// Central SQLite database service for Weaver.
/// Replaces all file-based JSON storage with a single database file
/// that lives in %LOCALAPPDATA%/Weaver/ so it survives app updates.
///
/// Tables:
///   weaver_config     — key/value store for FrontendConfig (weaverconfig.json)
///   board_data        — single-row Kanban board JSON blob
///   calendar_data     — single-row calendar events JSON blob
///   file_hints        — single-row file hints JSON blob
///   vapid_keys        — single-row VAPID key pair JSON blob
///   benchmark_scores  — per-score rows (id, timestamp, data)
///   system_info       — single-row custom system info JSON blob
///   improvement_data  — per-project improvement data JSON blob
///   edit_knowledge    — per-project edit knowledge JSON blob
/// </summary>
public class DatabaseService
{
    private readonly string _dbPath;
    private readonly string _weaverDataDir; // old data/ dir for migration
    private readonly string _configPath;    // old weaverconfig.json for migration

    public DatabaseService(string dbPath, string weaverDataDir, string configPath)
    {
        _dbPath = dbPath;
        _weaverDataDir = weaverDataDir;
        _configPath = configPath;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        InitializeDatabase();
        MigrateFromFilesIfNeeded();
        MigrateLegacyVersionFile();
        CleanupLegacyFiles();
    }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        // Enable WAL mode for better concurrent read/write performance
        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();
        return conn;
    }

    // ─── Initialization ─────────────────────────────────────────────────────

    private void InitializeDatabase()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS weaver_config (
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS board_data (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS calendar_data (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS file_hints (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS vapid_keys (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS benchmark_scores (
                id TEXT PRIMARY KEY NOT NULL,
                timestamp TEXT NOT NULL,
                data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS system_info (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS improvement_data (
                project TEXT PRIMARY KEY NOT NULL,
                data TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS edit_knowledge (
                project_name TEXT PRIMARY KEY NOT NULL,
                data TEXT NOT NULL
            );

            -- All tables use PRIMARY KEY columns for lookups (SQLite auto-indexes PKs).
            -- - weaver_config.key, edit_knowledge.project_name, improvement_data.project
            --   are already indexed by virtue of being PRIMARY KEYs.
            -- - Single-row tables (board_data, calendar_data, file_hints, vapid_keys,
            --   system_info) don't benefit from additional indexes.
        ";
        cmd.ExecuteNonQuery();
    }

    // ─── Migration from files ───────────────────────────────────────────────

    private void MigrateFromFilesIfNeeded()
    {
        // Check if migration already happened (weaver_config has data)
        if (HasExistingData()) return;

        MigrateWeaverConfig();
        MigrateBoardData();
        MigrateCalendarData();
        MigrateFileHints();
        MigrateVapidKeys();
        MigrateBenchmarkScores();
        MigrateSystemInfo();
        MigrateImprovementData();
        MigrateEditKnowledge();
    }

    private bool HasExistingData()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM weaver_config;";
        var count = (long)(cmd.ExecuteScalar() ?? 0);
        return count > 0;
    }

    private void MigrateWeaverConfig()
    {
        if (!File.Exists(_configPath)) return;
        try
        {
            var json = File.ReadAllText(_configPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            SetValue("weaver_config", "config", json);
            // The config now lives in the database — the legacy JSON file must not linger.
            File.Delete(_configPath);
        }
        catch { /* migration is best-effort */ }
    }

    /// <summary>
    /// Migrates the legacy .weaver-version file — which the app used to read
    /// before versions moved into the database — into the local_version key,
    /// then deletes the file. Runs on every startup: a database migrated by an
    /// earlier app version gets its real version back if the file still exists,
    /// and any leftover file is swept once the DB holds a version. When nothing
    /// is available it seeds "0", exactly like the old file-based code wrote a
    /// "0" file for a missing one — so the Discord panel always shows a
    /// DB-backed value instead of a transient fallback.
    /// </summary>
    private void MigrateLegacyVersionFile()
    {
        try
        {
            // The version file lived in several places over the app's history:
            // next to the data dir (dev runs), and in %LOCALAPPDATA%\Weaver
            // (installed builds), under either .weaver-version or .weaver-version.txt.
            // Headless Linux hosts have no %LOCALAPPDATA% (GetFolderPath returns "") — skip
            // that legacy root so an empty string never becomes a cwd-relative probe.
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roots = new[]
            {
                _weaverDataDir,
                Path.GetDirectoryName(_weaverDataDir) ?? "",
                string.IsNullOrWhiteSpace(localAppData) ? "" : Path.Combine(localAppData, "Weaver"),
            };
            var names = new[] { ".weaver-version", ".weaver-version.txt" };

            var hasVersion = !string.IsNullOrWhiteSpace(GetLocalVersion());
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                foreach (var name in names)
                {
                    var versionFile = Path.Combine(root, name);
                    if (!File.Exists(versionFile)) continue;
                    // Import whenever the DB holds no real version — including the
                    // seeded "0" placeholder, which only means "unknown" and must
                    // not swallow a real version that still exists on disk.
                    if (!hasVersion || GetLocalVersion() == "0")
                    {
                        var content = File.ReadAllText(versionFile).Trim();
                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            SetLocalVersion(content);
                            hasVersion = true;
                        }
                    }
                    // The version now lives in the database — the legacy file must not linger.
                    File.Delete(versionFile);
                }
            }

            if (!hasVersion)
                SetLocalVersion("0");
        }
        catch { /* best-effort migration */ }
    }

    /// <summary>
    /// Removes every legacy JSON file once its data has been migrated into the
    /// database. Runs on every startup so files disappear even for databases
    /// that were migrated by an earlier version of the app (which left them
    /// behind). Each file is only deleted when the DB actually holds that data,
    /// so a not-yet-migrated file is never discarded.
    /// </summary>
    private void CleanupLegacyFiles()
    {
        CleanupLegacyFile(_configPath, () => GetValue("weaver_config", "config") != null);
        CleanupLegacyFile(Path.Combine(_weaverDataDir, "board.json"), () => GetBoardData() != null);
        CleanupLegacyFile(Path.Combine(_weaverDataDir, ".calendardata"), () => GetCalendarData() != null);
        CleanupLegacyFile(Path.Combine(_weaverDataDir, "filehints.json"), () => GetFileHints() != null);
        CleanupLegacyFile(Path.Combine(_weaverDataDir, "vapid-keys.json"), () => GetVapidKeys() != null);
        CleanupLegacyFile(Path.Combine(_weaverDataDir, "benchmark_scores.json"), () => GetValue("benchmark_scores_json") != null);
        CleanupLegacyFile(Path.Combine(_weaverDataDir, "system_info.json"), () => GetSystemInfo() != null);

        if (!Directory.Exists(_weaverDataDir)) return;
        foreach (var file in Directory.GetFiles(_weaverDataDir, ".project_*_edit_knowledge.json"))
        {
            var projectName = ExtractEditKnowledgeProjectName(file);
            CleanupLegacyFile(file, () => GetEditKnowledge(projectName) != null);
        }
    }

    private void CleanupLegacyFile(string path, Func<bool> hasData)
    {
        try
        {
            if (!File.Exists(path)) return;
            // Delete once the DB holds the data, or when the file holds nothing
            // at all — an empty/whitespace-only file has no data worth keeping,
            // so it must not linger either.
            if (hasData() || string.IsNullOrWhiteSpace(File.ReadAllText(path)))
                File.Delete(path);
        }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Extracts the project name from a legacy edit-knowledge file name of the
    /// form ".project_{name}_edit_knowledge.json".
    /// </summary>
    private static string ExtractEditKnowledgeProjectName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (name.StartsWith(".project_"))
            name = name[".project_".Length..];
        if (name.EndsWith("_edit_knowledge"))
            name = name[..^"_edit_knowledge".Length];
        return name;
    }

    private void MigrateBoardData()
    {
        var boardPath = Path.Combine(_weaverDataDir, "board.json");
        if (!File.Exists(boardPath)) return;
        try
        {
            var json = File.ReadAllText(boardPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            SetBoardData(json);
            // The board now lives in the database — the legacy JSON file must not linger.
            File.Delete(boardPath);
        }
        catch { }
    }

    private void MigrateCalendarData()
    {
        var calPath = Path.Combine(_weaverDataDir, ".calendardata");
        if (!File.Exists(calPath)) return;
        try
        {
            var json = File.ReadAllText(calPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            SetCalendarData(json);
            // The calendar now lives in the database — the legacy file must not linger.
            File.Delete(calPath);
        }
        catch { }
    }

    private void MigrateFileHints()
    {
        var hintsPath = Path.Combine(_weaverDataDir, "filehints.json");
        if (!File.Exists(hintsPath)) return;
        try
        {
            var json = File.ReadAllText(hintsPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            SetFileHints(json);
            // The hints now live in the database — the legacy JSON file must not linger.
            File.Delete(hintsPath);
        }
        catch { }
    }

    private void MigrateVapidKeys()
    {
        var vapidPath = Path.Combine(_weaverDataDir, "vapid-keys.json");
        if (!File.Exists(vapidPath)) return;
        try
        {
            var json = File.ReadAllText(vapidPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            SetVapidKeys(json);
            // The keys now live in the database — the legacy JSON file must not linger.
            File.Delete(vapidPath);
        }
        catch { }
    }

    private void MigrateBenchmarkScores()
    {
        var benchPath = Path.Combine(_weaverDataDir, "benchmark_scores.json");
        if (!File.Exists(benchPath)) return;
        try
        {
            var json = File.ReadAllText(benchPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            using var conn = CreateConnection();
            // The file contains a JSON array — we store the whole array as a single blob
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO weaver_config (key, value)
                VALUES ('benchmark_scores_json', @data);
            ";
            cmd.Parameters.AddWithValue("@data", json);
            cmd.ExecuteNonQuery();
            // The scores now live in the database — the legacy JSON file must not linger.
            File.Delete(benchPath);
        }
        catch { }
    }

    private void MigrateSystemInfo()
    {
        var sysInfoPath = Path.Combine(_weaverDataDir, "system_info.json");
        if (!File.Exists(sysInfoPath)) return;
        try
        {
            var json = File.ReadAllText(sysInfoPath);
            if (string.IsNullOrWhiteSpace(json)) return;
            SetSystemInfo(json);
            // The info now lives in the database — the legacy JSON file must not linger.
            File.Delete(sysInfoPath);
        }
        catch { }
    }

    private void MigrateImprovementData()
    {
        if (!Directory.Exists(_weaverDataDir)) return;
        // Improvement data lives in project dirs, not weaver data dir — skip
    }

    private void MigrateEditKnowledge()
    {
        if (!Directory.Exists(_weaverDataDir)) return;
        try
        {
            var files = Directory.GetFiles(_weaverDataDir, ".project_*_edit_knowledge.json");
            foreach (var file in files)
            {
                try
                {
                    var projectName = ExtractEditKnowledgeProjectName(file);
                    var json = File.ReadAllText(file);
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    SetEditKnowledge(projectName, json);
                    // The knowledge now lives in the database — the legacy file must not linger.
                    File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    // ─── Local version (update tracking) ────────────────────────────────────

    public string? GetLocalVersion()
    {
        return GetValue("weaver_config", "local_version");
    }

    public void SetLocalVersion(string version)
    {
        SetValue("weaver_config", "local_version", version);
    }

    // ─── Generic key/value access (weaver_config) ───────────────────────────

    public string? GetValue(string key)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM weaver_config WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        var result = cmd.ExecuteScalar();
        return result?.ToString();
    }

    public string? GetValue(string table, string key)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT value FROM {QuoteIdentifier(table)} WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        var result = cmd.ExecuteScalar();
        return result?.ToString();
    }

    public void SetValue(string key, string value)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO weaver_config (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = @value;
        ";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    // Tables: store a single table name directly for simpler access
    public void SetValue(string table, string key, string value)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO {QuoteIdentifier(table)} (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = @value;
        ";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    // ─── Board data (single row) ────────────────────────────────────────────

    public string? GetBoardData()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM board_data WHERE id = 1;";
        return cmd.ExecuteScalar()?.ToString();
    }

    public void SetBoardData(string json)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO board_data (id, data) VALUES (1, @data)
            ON CONFLICT(id) DO UPDATE SET data = @data;
        ";
        cmd.Parameters.AddWithValue("@data", json);
        cmd.ExecuteNonQuery();
    }

    // ─── Calendar data (single row) ─────────────────────────────────────────

    public string? GetCalendarData()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM calendar_data WHERE id = 1;";
        return cmd.ExecuteScalar()?.ToString();
    }

    public void SetCalendarData(string json)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO calendar_data (id, data) VALUES (1, @data)
            ON CONFLICT(id) DO UPDATE SET data = @data;
        ";
        cmd.Parameters.AddWithValue("@data", json);
        cmd.ExecuteNonQuery();
    }

    // ─── File hints (single row) ────────────────────────────────────────────

    public string? GetFileHints()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM file_hints WHERE id = 1;";
        return cmd.ExecuteScalar()?.ToString();
    }

    public void SetFileHints(string json)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO file_hints (id, data) VALUES (1, @data)
            ON CONFLICT(id) DO UPDATE SET data = @data;
        ";
        cmd.Parameters.AddWithValue("@data", json);
        cmd.ExecuteNonQuery();
    }

    // ─── VAPID keys (single row) ────────────────────────────────────────────

    public string? GetVapidKeys()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM vapid_keys WHERE id = 1;";
        return cmd.ExecuteScalar()?.ToString();
    }

    public void SetVapidKeys(string json)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO vapid_keys (id, data) VALUES (1, @data)
            ON CONFLICT(id) DO UPDATE SET data = @data;
        ";
        cmd.Parameters.AddWithValue("@data", json);
        cmd.ExecuteNonQuery();
    }

    // ─── Benchmark scores ───────────────────────────────────────────────────

    public void SetAllBenchmarkScores(string json)
    {
        SetValue("weaver_config", "benchmark_scores_json", json);
    }

    // ─── System info (single row) ───────────────────────────────────────────

    public string? GetSystemInfo()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM system_info WHERE id = 1;";
        return cmd.ExecuteScalar()?.ToString();
    }

    public void SetSystemInfo(string json)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO system_info (id, data) VALUES (1, @data)
            ON CONFLICT(id) DO UPDATE SET data = @data;
        ";
        cmd.Parameters.AddWithValue("@data", json);
        cmd.ExecuteNonQuery();
    }

    // ─── Improvement data (per project) ─────────────────────────────────────

    public string? GetImprovementData(string project)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM improvement_data WHERE project = @project;";
        cmd.Parameters.AddWithValue("@project", project);
        return cmd.ExecuteScalar()?.ToString();
    }

    public void SetImprovementData(string project, string json)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO improvement_data (project, data) VALUES (@project, @data)
            ON CONFLICT(project) DO UPDATE SET data = @data;
        ";
        cmd.Parameters.AddWithValue("@project", project);
        cmd.Parameters.AddWithValue("@data", json);
        cmd.ExecuteNonQuery();
    }

    // ─── Edit knowledge (per project) ───────────────────────────────────────

    public string? GetEditKnowledge(string projectName)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM edit_knowledge WHERE project_name = @name;";
        cmd.Parameters.AddWithValue("@name", projectName);
        return cmd.ExecuteScalar()?.ToString();
    }

    public void SetEditKnowledge(string projectName, string json)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO edit_knowledge (project_name, data) VALUES (@name, @data)
            ON CONFLICT(project_name) DO UPDATE SET data = @data;
        ";
        cmd.Parameters.AddWithValue("@name", projectName);
        cmd.Parameters.AddWithValue("@data", json);
        cmd.ExecuteNonQuery();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string QuoteIdentifier(string identifier)
    {
        // Sanitize: only allow letter/digit/underscore
        if (identifier.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new ArgumentException($"Invalid SQL identifier: {identifier}");
        return $"\"{identifier}\"";
    }
}
