using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Deterministic tests for <see cref="SqlMigrationService"/> — the migration-file writer
/// that keeps CREATE TABLE statements out of endpoint code. Covers statement extraction
/// (plain SQL, C# verbatim strings, nested parens, string literals), migration file
/// writing with the instructional header, table-coverage detection, and the strip step
/// the apply loop uses to remove inline DDL from the applied edit.
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
        // The real-world shape: DDL inside a C# verbatim string literal.
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
        // The statement must stop at the CREATE TABLE's own ';' — the INSERT that follows
        // belongs to the method body, not the extracted migration DDL.
        Assert.Contains("id INTEGER PRIMARY KEY", result[0].Sql);
        Assert.EndsWith(");", result[0].Sql);
        Assert.DoesNotContain("INSERT INTO benchmark_scores", result[0].Sql);
    }

    [Fact]
    public void Extract_NestedParensAndStringLiterals_ClosesAtCorrectBrace()
    {
        // VARCHAR(255) + a single-quoted literal containing parens must not break matching.
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

    // ── WriteMigration / TableHasMigration / FindMigratedTables ─────────────

    [Fact]
    public void WriteMigration_CreatesTimestampedFileWithHeader()
    {
        var sql = "CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY);";
        var ts = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

        var rel = SqlMigrationService.WriteMigration(_root, "users", sql, ts);

        Assert.NotNull(rel);
        Assert.StartsWith("migrations/20260803-120000_create_users.sql", rel);
        var full = Path.Combine(_root, rel!);
        Assert.True(File.Exists(full));
        var content = File.ReadAllText(full);
        Assert.Contains("apply this to your database manually, then delete this file", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE IF NOT EXISTS users", content);
    }

    [Fact]
    public void WriteMigration_AlreadyCovered_ReturnsNullAndDoesNotDuplicate()
    {
        SqlMigrationService.WriteMigration(_root, "users", "CREATE TABLE IF NOT EXISTS users (id INT);");
        var before = Directory.GetFiles(Path.Combine(_root, "migrations")).Length;

        var rel = SqlMigrationService.WriteMigration(_root, "users", "CREATE TABLE IF NOT EXISTS users (id INT, extra INT);");

        Assert.Null(rel);
        Assert.Equal(before, Directory.GetFiles(Path.Combine(_root, "migrations")).Length);
    }

    [Fact]
    public void WriteMigration_EmptyInput_ReturnsNull()
    {
        Assert.Null(SqlMigrationService.WriteMigration(_root, "", "CREATE TABLE x (id INT);"));
        Assert.Null(SqlMigrationService.WriteMigration(_root, "x", ""));
    }

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
    public void FindMigratedTables_ReturnsCoveredTables()
    {
        SqlMigrationService.WriteMigration(_root, "users", "CREATE TABLE IF NOT EXISTS users (id INT);");
        SqlMigrationService.WriteMigration(_root, "sessions", "CREATE TABLE IF NOT EXISTS sessions (id INT);");

        var tables = SqlMigrationService.FindMigratedTables(_root).ToList();

        Assert.Contains("users", tables);
        Assert.Contains("sessions", tables);
    }

    // ── StripCreateTableStatements ──────────────────────────────────────────

    [Fact]
    public void Strip_RemovesStatementAndTrailingNewline_KeepsRest()
    {
        var code = "    cmd.CommandText = @\"\n        CREATE TABLE IF NOT EXISTS x (id INT);\n        INSERT INTO x VALUES (1);\";";
        var statements = SqlMigrationService.ExtractCreateTableStatements(code);

        var stripped = SqlMigrationService.StripCreateTableStatements(code, statements.Select(s => s.Sql).ToList());

        Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS x", stripped);
        Assert.Contains("INSERT INTO x VALUES (1);", stripped);
        Assert.Contains("cmd.CommandText = @\"", stripped);
    }

    [Fact]
    public void Strip_NoStatements_ReturnsUnchanged()
    {
        const string code = "INSERT INTO x VALUES (1);";

        Assert.Equal(code, SqlMigrationService.StripCreateTableStatements(code, Array.Empty<string>()));
    }
}
