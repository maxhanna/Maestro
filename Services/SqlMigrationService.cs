using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Writes SQL migration files (CREATE TABLE statements) into the repo's migrations/
/// folder. When an agent edit introduces a new SQL table, the DDL is extracted from the
/// code and written to a timestamped .sql file so the user can apply it to their database
/// MANUALLY — instead of the agent inlining CREATE TABLE inside the method body. Once the
/// user has applied the migration they delete the .sql file; edits that reference a table
/// covered by a migration file are accepted by the SQL guard.
/// </summary>
public static class SqlMigrationService
{
    public const string MigrationsFolder = "migrations";

    private static readonly Regex CreateTableHeaderRegex = new(
        @"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?`?(\w+)`?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extract complete CREATE TABLE statements (table name + full SQL including the
    /// trailing ';') from arbitrary text — e.g. a C# method body where the DDL sits
    /// inside a verbatim string literal. Returns one entry per statement, in order.
    /// </summary>
    public static List<(string Table, string Sql)> ExtractCreateTableStatements(string text)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match m in CreateTableHeaderRegex.Matches(text))
        {
            var table = m.Groups[1].Value;
            var start = m.Index;
            var parenIdx = text.IndexOf('(', start + m.Length);
            if (parenIdx < 0) continue;
            var depth = 0;
            var inSingle = false;
            // Track ONLY single-quote SQL string literals. We deliberately ignore double
            // quotes: the DDL may live inside a C# verbatim string ("@...") or string
            // concatenation fragments, where quote toggling would corrupt paren matching.
            // Column defaults in generated DDL use single quotes, which we do track.
            for (var i = parenIdx; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\'') inSingle = !inSingle;
                else if (!inSingle)
                {
                    if (c == '(') depth++;
                    else if (c == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            var end = i + 1;
                            while (end < text.Length && text[end] != ';') end++;
                            if (end < text.Length) end++; // include the trailing ';'
                            var sql = text[start..end].Trim();
                            result.Add((table, sql));
                            break;
                        }
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Write a timestamped migration file (<c>migrations/&lt;ts&gt;_create_&lt;table&gt;.sql</c>)
    /// containing the CREATE TABLE statement with an instructional header. Skips writing when a
    /// migration file already covers the table. Returns the repo-relative path written, or null.
    /// </summary>
    public static string? WriteMigration(string projectRoot, string tableName, string createTableSql, DateTime? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(createTableSql)) return null;
        var folder = Path.Combine(projectRoot, MigrationsFolder);
        Directory.CreateDirectory(folder);
        if (TableHasMigration(projectRoot, tableName)) return null;
        var safeTable = Regex.Replace(tableName, @"[^\w]", "_");
        var ts = (timestamp ?? DateTime.UtcNow).ToString("yyyyMMdd-HHmmss");
        var rel = $"{MigrationsFolder}/{ts}_create_{safeTable}.sql";
        var full = Path.Combine(projectRoot, rel);
        var header =
            $"-- Migration for table `{tableName}` — apply this to your database manually, then delete this file.\n" +
            $"-- Generated: {ts} (UTC)\n\n";
        File.WriteAllText(full, header + createTableSql.TrimEnd() + "\n");
        return rel;
    }

    /// <summary>True when any <c>migrations/*.sql</c> file already contains a CREATE TABLE for the table.</summary>
    public static bool TableHasMigration(string projectRoot, string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return false;
        var folder = Path.Combine(projectRoot, MigrationsFolder);
        if (!Directory.Exists(folder)) return false;
        foreach (var file in Directory.GetFiles(folder, "*.sql"))
        {
            try
            {
                var content = File.ReadAllText(file);
                foreach (Match m in CreateTableHeaderRegex.Matches(content))
                    if (string.Equals(m.Groups[1].Value, tableName, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>All table names covered by existing <c>migrations/*.sql</c> files.</summary>
    public static List<string> FindMigratedTables(string projectRoot)
    {
        var tables = new List<string>();
        var folder = Path.Combine(projectRoot, MigrationsFolder);
        if (!Directory.Exists(folder)) return tables;
        foreach (var file in Directory.GetFiles(folder, "*.sql"))
        {
            try
            {
                var content = File.ReadAllText(file);
                foreach (Match m in CreateTableHeaderRegex.Matches(content))
                    tables.Add(m.Groups[1].Value);
            }
            catch { }
        }
        return tables;
    }

    /// <summary>
    /// Remove the given CREATE TABLE statements from code (returns the cleaned text). The
    /// DDL typically lives inside a C# verbatim string; removing the SQL text leaves the
    /// surrounding string fragments, which stays valid C#.
    /// </summary>
    public static string StripCreateTableStatements(string code, IReadOnlyCollection<string> statements)
    {
        if (statements.Count == 0) return code;
        var stripped = code;
        foreach (var sql in statements)
        {
            var idx = stripped.IndexOf(sql, StringComparison.Ordinal);
            if (idx < 0) continue;
            var end = idx + sql.Length;
            if (end < stripped.Length && stripped[end] == '\n') end++; // consume the trailing newline
            stripped = stripped[..idx] + stripped[end..];
        }
        return stripped;
    }
}
