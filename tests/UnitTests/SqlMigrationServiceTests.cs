using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Deterministic tests for <see cref="SqlMigrationService"/> — the schema-changes writer that
/// keeps CREATE TABLE and ALTER TABLE statements out of endpoint code by appending them to a
/// single <c>migrations/schema_changes.md</c> markdown file. Covers statement extraction (plain
/// SQL, C# verbatim strings, nested parens, string literals, ALTER TABLE ADD COLUMN), the
/// append-only markdown sections, table/column coverage detection, and the strip step the apply
/// loop uses to remove inline DDL from the applied edit.
/// </summary>
public class SqlMigrationServiceTests : IDisposable
{
    private readonly string _root;

    public SqlMigrationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "weaver_sqlmigration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── ExtractCreateTableStatements ────────────────────────────────────────

    [Fact]
    public void Extract_PlainSql_ReturnsTableAndStatement()
    {
        var sql = "CREATE TABLE IF NOT EXISTS benchmark_scores (\n  id INTEGER PRIMARY KEY NOT NULL,\n  score REAL NOT NULL\n);";

        var result = SqlMigrationService.ExtractCreateTableStatements(sql);

        Assert.Single(result);
        Assert.Equal("benchmark_scores", result[0].Table);
        Assert.StartsWith("CREATE TABLE IF NOT EXISTS benchmark_scores", result[0].Sql);
        Assert.EndsWith(");", result[0].Sql);
    }

    [Fact]
    public void Extract_InsideCSharpVerbatimString_ReturnsStatement()
    {
        var code = """
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS benchmark_scores (
                        id INTEGER PRIMARY KEY NOT NULL,
                        score REAL NOT NULL
                    );
                    INSERT INTO benchmark_scores (id, score) VALUES (1, 9.5);";
                cmd.ExecuteNonQuery();
            }
            """;

        var result = SqlMigrationService.ExtractCreateTableStatements(code);

        Assert.Single(result);
        Assert.Equal("benchmark_scores", result[0].Table);
        Assert.Contains("id INTEGER PRIMARY KEY", result[0].Sql);
        Assert.EndsWith(");", result[0].Sql);
        Assert.DoesNotContain("INSERT INTO benchmark_scores", result[0].Sql);
    }

    [Fact]
    public void Extract_NestedParensAndStringLiterals_ClosesAtCorrectBrace()
    {
        var sql = "CREATE TABLE users (\n  name VARCHAR(255) DEFAULT 'a(b)c',\n  role VARCHAR(16) DEFAULT 'x)'\n);";

        var result = SqlMigrationService.ExtractCreateTableStatements(sql);

        Assert.Single(result);
        Assert.Equal("users", result[0].Table);
        Assert.Contains("DEFAULT 'a(b)c'", result[0].Sql);
        Assert.EndsWith(");", result[0].Sql);
    }

    [Fact]
    public void Extract_MultipleTables_ReturnsAllInOrder()
    {
        var sql = "CREATE TABLE IF NOT EXISTS a (id INT);\nCREATE TABLE IF NOT EXISTS b (id INT);";

        var result = SqlMigrationService.ExtractCreateTableStatements(sql);

        Assert.Equal(2, result.Count);
        Assert.Equal("a", result[0].Table);
        Assert.Equal("b", result[1].Table);
    }

    [Fact]
    public void Extract_NoTable_ReturnsEmpty()
    {
        Assert.Empty(SqlMigrationService.ExtractCreateTableStatements("INSERT INTO x VALUES (1);"));
        Assert.Empty(SqlMigrationService.ExtractCreateTableStatements(""));
        Assert.Empty(SqlMigrationService.ExtractCreateTableStatements("   "));
    }

    // ── ExtractAlterTableStatements ─────────────────────────────────────────

    [Fact]
    public void ExtractAlter_PlainSql_ReturnsTableColumnAndStatement()
    {
        var sql = "ALTER TABLE benchmark_scores ADD COLUMN metric_type TEXT;";

        var result = SqlMigrationService.ExtractAlterTableStatements(sql);

        Assert.Single(result);
        Assert.Equal("benchmark_scores", result[0].Table);
        Assert.Equal("metric_type", result[0].Column);
        Assert.Equal("ALTER TABLE benchmark_scores ADD COLUMN metric_type TEXT;", result[0].Sql);
    }

    [Fact]
    public void ExtractAlter_WithoutColumnKeywordAndCaseInsensitive_ReturnsStatement()
    {
        var sql = "alter table users add email varchar(255);";

        var result = SqlMigrationService.ExtractAlterTableStatements(sql);

        Assert.Single(result);
        Assert.Equal("users", result[0].Table);
        Assert.Equal("email", result[0].Column);
        Assert.EndsWith(";", result[0].Sql);
    }

    [Fact]
    public void ExtractAlter_Multiple_ReturnsAllInOrder()
    {
        var sql = "ALTER TABLE a ADD COLUMN x INT;\nALTER TABLE b ADD COLUMN y TEXT;";

        var result = SqlMigrationService.ExtractAlterTableStatements(sql);

        Assert.Equal(2, result.Count);
        Assert.Equal(("a", "x"), (result[0].Table, result[0].Column));
        Assert.Equal(("b", "y"), (result[1].Table, result[1].Column));
    }

    [Fact]
    public void ExtractAlter_NoAlter_ReturnsEmpty()
    {
        Assert.Empty(SqlMigrationService.ExtractAlterTableStatements("CREATE TABLE x (id INT);"));
        Assert.Empty(SqlMigrationService.ExtractAlterTableStatements("SELECT * FROM users;"));
        Assert.Empty(SqlMigrationService.ExtractAlterTableStatements(""));
    }

    // ── WriteMigration (CREATE TABLE → schema_changes.md) ──────────────────

    [Fact]
    public void WriteMigration_WritesSchemaChangesMarkdownWithCreateSection()
    {
        var sql = "CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY);";

        var rel = SqlMigrationService.WriteMigration(_root, "users", sql);

        Assert.Equal("migrations/schema_changes.md", rel);
        var full = Path.Combine(_root, rel!);
        Assert.True(File.Exists(full));
        var content = File.ReadAllText(full);
        Assert.Contains("# Schema Changes", content);
        Assert.Contains("## Table `users`", content);
        Assert.Contains("```sql", content);
        Assert.Contains("CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY);", content);
    }

    [Fact]
    public void WriteMigration_MissingTrailingSemicolon_NormalizedOnDisk()
    {
        var rel = SqlMigrationService.WriteMigration(_root, "users", "CREATE TABLE IF NOT EXISTS users (id INT)");

        var content = File.ReadAllText(Path.Combine(_root, rel!));
        Assert.Contains("CREATE TABLE IF NOT EXISTS users (id INT);", content);
    }

    [Fact]
    public void WriteMigration_AlreadyCovered_ReturnsNullAndDoesNotDuplicate()
    {
        SqlMigrationService.WriteMigration(_root, "users", "CREATE TABLE IF NOT EXISTS users (id INT);");
        var before = File.ReadAllText(Path.Combine(_root, "migrations/schema_changes.md"));

        var rel = SqlMigrationService.WriteMigration(_root, "users", "CREATE TABLE IF NOT EXISTS users (id INT, extra INT);");

        Assert.Null(rel);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "migrations/schema_changes.md")));
    }

    [Fact]
    public void WriteMigration_EmptyInput_ReturnsNull()
    {
        Assert.Null(SqlMigrationService.WriteMigration(_root, "", "CREATE TABLE x (id INT);"));
        Assert.Null(SqlMigrationService.WriteMigration(_root, "x", ""));
    }

    // ── WriteAlterMigration (ALTER TABLE → schema_changes.md) ──────────────

    [Fact]
    public void WriteAlterMigration_WritesAlterSection()
    {
        var rel = SqlMigrationService.WriteAlterMigration(
            _root, "users", "email", "ALTER TABLE users ADD COLUMN email TEXT;");

        Assert.Equal("migrations/schema_changes.md", rel);
        var content = File.ReadAllText(Path.Combine(_root, rel!));
        Assert.Contains("## Alter `users` — add column `email`", content);
        Assert.Contains("ALTER TABLE users ADD COLUMN email TEXT;", content);
    }

    [Fact]
    public void WriteAlterMigration_AlreadyCovered_ReturnsNull()
    {
        SqlMigrationService.WriteAlterMigration(_root, "users", "email", "ALTER TABLE users ADD COLUMN email TEXT;");

        Assert.Null(SqlMigrationService.WriteAlterMigration(
            _root, "users", "email", "ALTER TABLE users ADD COLUMN email VARCHAR(255);"));
        // A different column on the same table still writes.
        Assert.NotNull(SqlMigrationService.WriteAlterMigration(
            _root, "users", "phone", "ALTER TABLE users ADD COLUMN phone TEXT;"));
    }

    [Fact]
    public void WriteAlterMigration_EmptyInput_ReturnsNull()
    {
        Assert.Null(SqlMigrationService.WriteAlterMigration(_root, "", "c", "ALTER TABLE t ADD c INT;"));
        Assert.Null(SqlMigrationService.WriteAlterMigration(_root, "t", "", "ALTER TABLE t ADD c INT;"));
        Assert.Null(SqlMigrationService.WriteAlterMigration(_root, "t", "c", ""));
    }

    [Fact]
    public void WriteCreateAndAlter_AppendsBothSectionsToSameFile()
    {
        SqlMigrationService.WriteMigration(_root, "benchmark_metrics", "CREATE TABLE IF NOT EXISTS benchmark_metrics (id INT);");
        SqlMigrationService.WriteAlterMigration(_root, "benchmark_scores", "metric_type", "ALTER TABLE benchmark_scores ADD COLUMN metric_type TEXT;");

        var content = File.ReadAllText(Path.Combine(_root, "migrations/schema_changes.md"));
        Assert.Contains("## Table `benchmark_metrics`", content);
        Assert.Contains("## Alter `benchmark_scores` — add column `metric_type`", content);
        // Header appears once (no duplicate on the second append).
        var headerCount = content.Split("# Schema Changes").Length - 1;
        Assert.Equal(1, headerCount);
    }

    // ── Coverage detection ──────────────────────────────────────────────────

    [Fact]
    public void TableHasMigration_CaseInsensitive_DetectsCoverage()
    {
        Assert.False(SqlMigrationService.TableHasMigration(_root, "users"));
        SqlMigrationService.WriteMigration(_root, "users", "CREATE TABLE IF NOT EXISTS users (id INT);");

        Assert.True(SqlMigrationService.TableHasMigration(_root, "users"));
        Assert.True(SqlMigrationService.TableHasMigration(_root, "USERS"));
        Assert.False(SqlMigrationService.TableHasMigration(_root, "sessions"));
    }

    [Fact]
    public void ColumnHasMigration_DetectsCoveragePerColumn()
    {
        Assert.False(SqlMigrationService.ColumnHasMigration(_root, "users", "email"));
        SqlMigrationService.WriteAlterMigration(_root, "users", "email", "ALTER TABLE users ADD COLUMN email TEXT;");

        Assert.True(SqlMigrationService.ColumnHasMigration(_root, "users", "email"));
        Assert.True(SqlMigrationService.ColumnHasMigration(_root, "USERS", "EMAIL"));
        Assert.False(SqlMigrationService.ColumnHasMigration(_root, "users", "phone"));
        Assert.False(SqlMigrationService.ColumnHasMigration(_root, "sessions", "email"));
    }

    [Fact]
    public void FindMigratedTables_ReturnsCoveredTables()
    {
        SqlMigrationService.WriteMigration(_root, "users", "CREATE TABLE IF NOT EXISTS users (id INT);");
        SqlMigrationService.WriteMigration(_root, "sessions", "CREATE TABLE IF NOT EXISTS sessions (id INT);");

        var tables = SqlMigrationService.FindMigratedTables(_root).ToList();

        Assert.Contains("users", tables);
        Assert.Contains("sessions", tables);
    }

    // ── Strip statements (inline DDL removal) ───────────────────────────────

    [Fact]
    public void Strip_RemovesCreateStatementAndTrailingNewline_KeepsRest()
    {
        var code = "    cmd.CommandText = @\"\n        CREATE TABLE IF NOT EXISTS x (id INT);\n        INSERT INTO x VALUES (1);\";";
        var statements = SqlMigrationService.ExtractCreateTableStatements(code);

        var stripped = SqlMigrationService.StripCreateTableStatements(code, statements.Select(s => s.Sql).ToList());

        Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS x", stripped);
        Assert.Contains("INSERT INTO x VALUES (1);", stripped);
        Assert.Contains("cmd.CommandText = @\"", stripped);
    }

    [Fact]
    public void Strip_RemovesAlterStatement_KeepsRest()
    {
        var code = "cmd.CommandText = @\"ALTER TABLE x ADD COLUMN y INT; SELECT * FROM x;\";";
        var statements = SqlMigrationService.ExtractAlterTableStatements(code);

        var stripped = SqlMigrationService.StripAlterTableStatements(code, statements.Select(s => s.Sql).ToList());

        Assert.DoesNotContain("ALTER TABLE x", stripped);
        Assert.Contains("SELECT * FROM x;", stripped);
    }

    [Fact]
    public void Strip_NoStatements_ReturnsUnchanged()
    {
        const string code = "INSERT INTO x VALUES (1);";

        Assert.Equal(code, SqlMigrationService.StripCreateTableStatements(code, Array.Empty<string>()));
        Assert.Equal(code, SqlMigrationService.StripAlterTableStatements(code, Array.Empty<string>()));
    }
}
