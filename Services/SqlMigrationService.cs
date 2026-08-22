using System.Text;
using System.Text.RegularExpressions;

namespace Weaver.Services;

/// <summary>
/// Writes schema-change documentation (CREATE TABLE and ALTER TABLE statements) into a single
/// <c>migrations/schema_changes.md</c> markdown file. When an agent edit introduces a NEW SQL
/// table or a NEW COLUMN, the DDL is extracted from the code and appended to the markdown file so
/// the user can read (and apply) the changes MANUALLY — instead of the agent inlining DDL inside
/// the method body. Each change becomes a markdown section with a fenced <c>sql</c> block; the
/// file is append-only and de-duplicated (a table or column already documented is not re-added).
/// The SQL guard treats tables/columns covered by the file as existing.
/// </summary>
public static class SqlMigrationService
{
    public const string MigrationsFolder = "migrations";
    public const string SchemaChangesFile = "schema_changes.md";

    private const string HeaderLine = "# Schema Changes";

    private static readonly Regex CreateTableHeaderRegex = new(
        @"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?`?(\w+)`?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlterTableHeaderRegex = new(
        @"\bALTER\s+TABLE\s+`?(\w+)`?\s+ADD\s+(?:COLUMN\s+)?`?(\w+)`?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Repo-relative path of the schema-changes markdown file.</summary>
    public static string SchemaChangesRelPath => $"{MigrationsFolder}/{SchemaChangesFile}";

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
    /// Extract complete ALTER TABLE ... ADD [COLUMN] ... statements (table name, column name,
    /// and full SQL through the trailing ';') from arbitrary text. A statement runs from the
    /// <c>ALTER TABLE</c> keyword to the next ';' — the shape generated for new-column changes.
    /// </summary>
    public static List<(string Table, string Column, string Sql)> ExtractAlterTableStatements(string text)
    {
        var result = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match m in AlterTableHeaderRegex.Matches(text))
        {
            var table = m.Groups[1].Value;
            var column = m.Groups[2].Value;
            var start = m.Index;
            var end = text.IndexOf(';', start);
            if (end < 0) end = text.Length - 1;
            else end++; // include the trailing ';'
            var sql = text[start..end].Trim();
            result.Add((table, column, sql));
        }
        return result;
    }

    /// <summary>
    /// Append a CREATE TABLE section to <c>migrations/schema_changes.md</c>. Skips writing when
    /// the table is already documented. Returns the repo-relative path written, or null.
    /// </summary>
    public static string? WriteMigration(string projectRoot, string tableName, string createTableSql)
    {
        if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(createTableSql)) return null;
        if (TableHasMigration(projectRoot, tableName)) return null;
        AppendSection(projectRoot, $"Table `{tableName}`", createTableSql);
        return SchemaChangesRelPath;
    }

    /// <summary>
    /// Append an ALTER TABLE ADD COLUMN section to <c>migrations/schema_changes.md</c>. Skips
    /// writing when that column is already documented for the table. Returns the path or null.
    /// </summary>
    public static string? WriteAlterMigration(string projectRoot, string tableName, string columnName, string alterSql)
    {
        if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(columnName) ||
            string.IsNullOrWhiteSpace(alterSql)) return null;
        if (ColumnHasMigration(projectRoot, tableName, columnName)) return null;
        AppendSection(projectRoot, $"Alter `{tableName}` — add column `{columnName}`", alterSql);
        return SchemaChangesRelPath;
    }

    /// <summary>True when the schema-changes file already documents a CREATE TABLE for the table.</summary>
    public static bool TableHasMigration(string projectRoot, string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return false;
        var content = ReadSchemaChanges(projectRoot);
        if (string.IsNullOrWhiteSpace(content)) return false;
        foreach (Match m in CreateTableHeaderRegex.Matches(content))
            if (string.Equals(m.Groups[1].Value, tableName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>True when the schema-changes file already documents an ALTER TABLE ADD for the column.</summary>
    public static bool ColumnHasMigration(string projectRoot, string tableName, string columnName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(columnName)) return false;
        var content = ReadSchemaChanges(projectRoot);
        if (string.IsNullOrWhiteSpace(content)) return false;
        foreach (var (t, c, _) in ExtractAlterTableStatements(content))
            if (string.Equals(t, tableName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>All table names documented by a CREATE TABLE section in the schema-changes file.</summary>
    public static List<string> FindMigratedTables(string projectRoot)
    {
        var tables = new List<string>();
        var content = ReadSchemaChanges(projectRoot);
        if (string.IsNullOrWhiteSpace(content)) return tables;
        foreach (Match m in CreateTableHeaderRegex.Matches(content))
            tables.Add(m.Groups[1].Value);
        return tables;
    }

    /// <summary>
    /// Remove the given CREATE TABLE statements from code (returns the cleaned text). The
    /// DDL typically lives inside a C# verbatim string; removing the SQL text leaves the
    /// surrounding string fragments, which stays valid C#.
    /// </summary>
    public static string StripCreateTableStatements(string code, IReadOnlyCollection<string> statements)
        => StripStatements(code, statements);

    /// <summary>Remove the given ALTER TABLE statements from code (returns the cleaned text).</summary>
    public static string StripAlterTableStatements(string code, IReadOnlyCollection<string> statements)
        => StripStatements(code, statements);

    private static string StripStatements(string code, IReadOnlyCollection<string> statements)
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

    private static string? ReadSchemaChanges(string projectRoot)
    {
        var full = Path.Combine(projectRoot, SchemaChangesRelPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    private static void AppendSection(string projectRoot, string heading, string sql)
    {
        var folder = Path.Combine(projectRoot, MigrationsFolder);
        Directory.CreateDirectory(folder);
        var full = Path.Combine(folder, SchemaChangesFile);

        var body = new StringBuilder();
        var existing = File.Exists(full) ? File.ReadAllText(full).TrimEnd() : null;
        if (string.IsNullOrWhiteSpace(existing))
        {
            body.AppendLine(HeaderLine);
            body.AppendLine();
        }
        else
        {
            body.Append(existing);
            body.AppendLine();
            body.AppendLine();
        }
        body.AppendLine($"## {heading}");
        body.AppendLine();
        body.AppendLine("```sql");
        body.AppendLine(NormalizeSql(sql));
        body.AppendLine("```");
        body.AppendLine();
        File.WriteAllText(full, body.ToString());
    }

    private static string NormalizeSql(string sql)
    {
        var s = sql.Trim();
        if (s.Length > 0 && !s.EndsWith(';')) s += ";";
        return s;
    }
}
