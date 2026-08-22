using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks benchmark 24 (SQL Schema: CREATE TABLE + ALTER TABLE documented in a .md file) to the
/// new consolidated <c>migrations/schema_changes.md</c> behavior: the acceptance checks assert
/// the markdown file exists and documents both statements, and that the backend C# file has NO
/// inline DDL. Runs the checks hermetically (filesystem only, no LLM/browser) against a
/// correct fixture (all pass) and an inline-DDL fixture (the right checks fail).
/// </summary>
public class BenchmarkSchemaMigrationTests : IDisposable
{
    private readonly string _root;

    public BenchmarkSchemaMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "weaver_bench24_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static DatabaseService SubstituteDb()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "weaver_bench24_db_" + Guid.NewGuid().ToString("N"));
        return new DatabaseService(basePath + ".db", basePath + "_data", basePath + "_cfg.json");
    }

    [Fact]
    public void Benchmark24_Definition_DocumentsMarkdownAndNoInlineDdl()
    {
        var plan = BenchmarkService.GetBenchmarkPlans().Single(p => p.Level == 24);

        Assert.Contains("migrations/schema_changes.md", plan.Description);
        Assert.Contains("ALTER TABLE", plan.Description);

        var checks = plan.AcceptanceChecks;
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileExists && c.Path == "migrations/schema_changes.md");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileContains && c.Path == "migrations/schema_changes.md" &&
                                     c.Value == "CREATE TABLE" && c.IgnoreCase);
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileContains && c.Path == "migrations/schema_changes.md" &&
                                     c.Value == "ALTER TABLE" && c.IgnoreCase);
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileNotContains && c.Path == "benchmark_test_24/BenchmarkRepository.cs" &&
                                     c.Value == "CREATE TABLE");
        Assert.Contains(checks, c => c.Type == BenchmarkCheckType.FileNotContains && c.Path == "benchmark_test_24/BenchmarkRepository.cs" &&
                                     c.Value == "ALTER TABLE");
    }

    [Fact]
    public async Task Benchmark24_CorrectFixture_AllChecksPass()
    {
        Directory.CreateDirectory(Path.Combine(_root, "benchmark_test_24"));
        File.WriteAllText(Path.Combine(_root, "benchmark_test_24", "BenchmarkRepository.cs"),
            "public class BenchmarkRepository {\n" +
            "  public void SaveScore(string player, int score) {\n" +
            "    // INSERT INTO benchmark_metrics (player, score) VALUES (@player, @score);\n" +
            "  }\n" +
            "}\n");
        Directory.CreateDirectory(Path.Combine(_root, "migrations"));
        File.WriteAllText(Path.Combine(_root, "migrations", "schema_changes.md"),
            "# Schema Changes\n\n" +
            "## Table `benchmark_metrics`\n\n```sql\nCREATE TABLE IF NOT EXISTS benchmark_metrics (id INT);\n```\n\n" +
            "## Alter `benchmark_scores` — add column `metric_type`\n\n```sql\nALTER TABLE benchmark_scores ADD COLUMN metric_type TEXT;\n```\n");

        var results = await new BenchmarkService(SubstituteDb()).EvaluateChecksAsync(24, _root);

        Assert.All(results, r => Assert.True(r.Passed, $"{r.Name} should pass: {r.Message}"));
    }

    [Fact]
    public async Task Benchmark24_InlineDdlAndNoMarkdown_FailsTheRightChecks()
    {
        Directory.CreateDirectory(Path.Combine(_root, "benchmark_test_24"));
        File.WriteAllText(Path.Combine(_root, "benchmark_test_24", "BenchmarkRepository.cs"),
            "public class BenchmarkRepository {\n" +
            "  public void SaveScore(string player, int score) {\n" +
            "    cmd.CommandText = @\"CREATE TABLE benchmark_metrics (id INT);\";\n" +
            "    cmd.CommandText = @\"ALTER TABLE benchmark_scores ADD COLUMN metric_type TEXT;\";\n" +
            "  }\n" +
            "}\n");

        var results = await new BenchmarkService(SubstituteDb()).EvaluateChecksAsync(24, _root);

        Assert.False(results.Single(r => r.Name == "Schema changes markdown exists").Passed);
        Assert.False(results.Single(r => r.Name == "No inline create table").Passed);
        Assert.False(results.Single(r => r.Name == "No inline alter table").Passed);
    }
}
