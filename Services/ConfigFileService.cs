using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Weaver.Services;

public class ProjectDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public string BuildCommands { get; set; } = "";
}

public class EmailAccountConfig
{
    public string? imapServer { get; set; }
    public int imapPort { get; set; } = 993;
    public bool useSsl { get; set; } = true;
    public string? username { get; set; }
    public string? password { get; set; }
    public string? label { get; set; }
}

public class LlamaEndpoint
{
    public string id { get; set; } = "";
    public string name { get; set; } = "";
    public string url { get; set; } = "http://localhost:8080";
    public string model { get; set; } = "";
}

public class SavedThemeConfig
{
    public string name { get; set; } = "";
    public Dictionary<string, string> colors { get; set; } = new();
}

public class FontSizesConfig
{
    public int log { get; set; } = 18;
    public int llm { get; set; } = 14;
    public int plan { get; set; } = 14;
    public int metaplan { get; set; } = 12;
}

public class FrontendConfig
{
    public List<ProjectDto> projects { get; set; } = new();
    public string defaultProject { get; set; } = "";
    public bool showTerminal { get; set; } = true;
    public bool showAI { get; set; } = true;
    public bool showIDE { get; set; } = true;
    public bool showKanban { get; set; } = true;
    public bool showCalendar { get; set; } = false;
    public bool showNotes { get; set; } = false;
    public bool showMeeting { get; set; } = false;
    public bool meetingMuted { get; set; } = false;
    public int meetingVolume { get; set; } = 70;
    public bool prByDefault { get; set; } = false;
    public string buildCommands { get; set; } = "dotnet clean & dotnet build";
    public string llamaUrl { get; set; } = "http://localhost:8080";
    public string llamaModel { get; set; } = "lfm2.5-it-1.2b-FLM";
    public List<LlamaEndpoint> llamaEndpoints { get; set; } = new();
    public string terminalApprovalMode { get; set; } = "approveAll";
    public List<string> approvedTerminalRoots { get; set; } = new();
    public List<string> disallowedTerminalRoots { get; set; } = new();
    public int maxFileContextChars { get; set; } = 24000;
    public int maxFullFileTokens { get; set; } = 4096;
    public int maxContextChars { get; set; } = 22000;
    public int fileBodyTruncationChars { get; set; } = 8000;
    public int buildOutputTailChars { get; set; } = 8000;
    public int defaultMaxTokens { get; set; } = 2048;
    public List<EmailAccountConfig> emailAccounts { get; set; } = new();
    public string? emailImapServer { get; set; }
    public int emailImapPort { get; set; } = 993;
    public bool emailUseSsl { get; set; } = true;
    public string? emailUsername { get; set; }
    public string? emailPassword { get; set; }
    public string? bughostedUrl { get; set; }
    public string? bughostedUsername { get; set; }
    public string? bughostedPassword { get; set; }
    public bool bughostedHeartbeatEnabled { get; set; } = false;
    public Dictionary<string, string>? themeColors { get; set; }
    public List<SavedThemeConfig> savedThemes { get; set; } = new();
    public List<string> enabledTools { get; set; } = new();
    public bool includeProjectSkeleton { get; set; } = true;
    public bool includeEditKnowledge { get; set; } = false;
    public bool extendThinking { get; set; } = true;
    public int thinkingMaxTokens { get; set; } = 4096;
    public bool compactThinkingContext { get; set; } = true;
    public bool summarizeDiffContext { get; set; } = true;
    public int diffContextSummaryChars { get; set; } = 6000;
    public int llmTimeoutMinutes { get; set; } = 0;
    public bool useVSCodeInsteadOfIDE { get; set; } = false;
    public string ideTheme { get; set; } = "weaver-dark";
    public bool ideMinimapVisible { get; set; } = true;
    public FontSizesConfig fontSizes { get; set; } = new();
}

public class ConfigFileService
{
    private readonly DatabaseService _db;
    private const string EncryptedPrefix = "DPAPI_B64:";
    private const string ConfigKey = "config";

    public ConfigFileService(DatabaseService db)
    {
        _db = db;
    }

    // Compatibility constructor for isolated tests and legacy callers. Production
    // wiring uses the shared DatabaseService registered by Program.cs.
    public ConfigFileService(IWebHostEnvironment env)
        : this(CreateCompatibilityDatabase())
    {
    }

    private static DatabaseService CreateCompatibilityDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver-config-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new DatabaseService(
            Path.Combine(root, "weaver.db"), root, Path.Combine(root, "weaverconfig.json"));
    }

    private static string? EncryptPassword(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (plaintext.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) return plaintext;
        if (!OperatingSystem.IsWindows())
            return plaintext;

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return EncryptedPrefix + Convert.ToBase64String(encryptedBytes);
        }
        catch
        {
            return plaintext;
        }
    }

    private static string? DecryptPassword(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return encrypted;
        if (!encrypted.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) return encrypted;
        if (!OperatingSystem.IsWindows())
            return encrypted;

        try
        {
            var b64 = encrypted[EncryptedPrefix.Length..];
            var encryptedBytes = Convert.FromBase64String(b64);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return encrypted;
        }
    }

    private static void EncryptAccountPasswords(FrontendConfig cfg)
    {
        foreach (var acct in cfg.emailAccounts)
            acct.password = EncryptPassword(acct.password);
        cfg.emailPassword = EncryptPassword(cfg.emailPassword);
        cfg.bughostedUsername = EncryptPassword(cfg.bughostedUsername);
        cfg.bughostedPassword = EncryptPassword(cfg.bughostedPassword);
    }

    private static void DecryptAccountPasswords(FrontendConfig cfg)
    {
        foreach (var acct in cfg.emailAccounts)
            acct.password = DecryptPassword(acct.password);
        cfg.emailPassword = DecryptPassword(cfg.emailPassword);
        cfg.bughostedUsername = DecryptPassword(cfg.bughostedUsername);
        cfg.bughostedPassword = DecryptPassword(cfg.bughostedPassword);
    }

    public async Task EnsureConfigAsync()
    {
        var existing = _db.GetValue("weaver_config", ConfigKey);
        if (existing != null) return;
        await WriteConfigAsync(new FrontendConfig());
    }

    public async Task<FrontendConfig> LoadConfigAsync()
    {
        await EnsureConfigAsync();
        FrontendConfig cfg;
        try
        {
            var text = _db.GetValue("weaver_config", ConfigKey);
            cfg = string.IsNullOrWhiteSpace(text)
                ? new FrontendConfig()
                : JsonSerializer.Deserialize<FrontendConfig>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new FrontendConfig();

            if (cfg.emailAccounts.Count == 0 &&
                !string.IsNullOrWhiteSpace(cfg.emailUsername))
            {
                cfg.emailAccounts.Add(new EmailAccountConfig
                {
                    imapServer = cfg.emailImapServer,
                    imapPort = cfg.emailImapPort,
                    useSsl = cfg.emailUseSsl,
                    username = cfg.emailUsername,
                    password = cfg.emailPassword,
                    label = cfg.emailUsername.Contains('@') ? cfg.emailUsername[..cfg.emailUsername.IndexOf('@')] : cfg.emailUsername
                });
            }
        }
        catch
        {
            cfg = new FrontendConfig();
        }
        DecryptAccountPasswords(cfg);
        return cfg;
    }

    public async Task WriteConfigAsync(FrontendConfig cfg)
    {
        EncryptAccountPasswords(cfg);
        if (cfg.emailAccounts.Count > 0)
        {
            var first = cfg.emailAccounts[0];
            cfg.emailImapServer = first.imapServer;
            cfg.emailImapPort = first.imapPort;
            cfg.emailUseSsl = first.useSsl;
            cfg.emailUsername = first.username;
            cfg.emailPassword = first.password;
        }
        else
        {
            cfg.emailImapServer = null;
            cfg.emailUsername = null;
            cfg.emailPassword = null;
        }

        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        DecryptAccountPasswords(cfg);

        _db.SetValue("weaver_config", ConfigKey, json);
    }
}
