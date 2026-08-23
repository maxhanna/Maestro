namespace Weaver.Services;

/// <summary>
/// Manages a plain-text changelog file in the Weaver data folder.
/// The file is created on first run (empty header) and appended to
/// every time the agent completes a task that involved file edits.
/// </summary>
public class ChangelogService
{
    private readonly string _changelogPath;
    private static readonly object _lock = new();

    public ChangelogService(string dataDir)
    {
        _changelogPath = Path.Combine(dataDir, "changelog.txt");
        EnsureCreated();
    }

    /// <summary>Full path to the changelog file.</summary>
    public string FilePath => _changelogPath;

    /// <summary>Returns the full changelog text, or an empty header if the file does not exist yet.</summary>
    public string Read()
    {
        lock (_lock)
        {
            if (!File.Exists(_changelogPath)) return GetHeader();
            try { return File.ReadAllText(_changelogPath); }
            catch { return GetHeader(); }
        }
    }

    /// <summary>
    /// Appends a new changelog entry. Each call creates a timestamped block
    /// with the task summary, files edited, and an optional step breakdown.
    /// </summary>
    public void AppendEntry(string taskSummary, List<string>? filesEdited = null, string? thinking = null)
    {
        if (string.IsNullOrWhiteSpace(taskSummary) && (filesEdited == null || filesEdited.Count == 0))
            return; // nothing meaningful to record

        lock (_lock)
        {
            EnsureCreated();
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"## {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                if (!string.IsNullOrWhiteSpace(taskSummary))
                    sb.AppendLine($"Task: {taskSummary.Trim()}");

                if (filesEdited != null && filesEdited.Count > 0)
                {
                    sb.AppendLine("Files changed:");
                    foreach (var f in filesEdited)
                        sb.AppendLine($"  - {f}");
                }

                if (!string.IsNullOrWhiteSpace(thinking))
                {
                    var trimmedThinking = thinking.Trim();
                    if (trimmedThinking.Length > 500)
                        trimmedThinking = trimmedThinking.Substring(0, 500) + "...";
                    sb.AppendLine($"Notes: {trimmedThinking}");
                }

                File.AppendAllText(_changelogPath, sb.ToString());
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Overwrite the entire changelog (used for editing from the UI).</summary>
    public void Overwrite(string content)
    {
        lock (_lock)
        {
            try { File.WriteAllText(_changelogPath, content); }
            catch { /* best-effort */ }
        }
    }

    private void EnsureCreated()
    {
        if (File.Exists(_changelogPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(_changelogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_changelogPath, GetHeader());
        }
        catch { /* best-effort */ }
    }

    private static string GetHeader() =>
        "# Weaver Changelog\n" +
        "# Automatically updated by the agent after each task with file edits.\n";
}
