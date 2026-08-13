using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// End-to-end integration coverage for the TABULAR fast-path: the real
/// <c>ResolveAndApplyEdit</c> (private controller method, invoked via reflection exactly
/// like <c>DeterministicBatchIntegrationTests</c>) running a structural CSV/TSV/XLSX edit
/// ("add a type column") against a real file on disk. The step must:
///
/// 1. be claimed by <c>ApplyTabularEditAsync</c> BEFORE the text-replace pipeline — the
///    tabular check sits right after the create-file path in <c>ResolveAndApplyEdit</c>,
///    so a recognized operation (parse → operation → serialize) lands with ZERO LLM calls,
///    proven by a client factory that THROWS on <c>CreateClient</c>; every LLM path in the
///    controller routes through <c>_clientFactory</c>, so any accidental LLM attempt fails
///    the test loudly;
/// 2. preserve the CSV structure the text pipeline would destroy — the <c>FETCHED_AT:</c>
///    preamble line, the header row, and every data row (a blind string replace or
///    full-file rewrite would mangle quoting / drop the preamble);
/// 3. produce a done result carrying the <c>tabular</c> flag + the human-readable reason
///    ("added column 'type'") so the UI shows why the file changed;
/// 4. round-trip a real .xlsx workbook (a ZIP of XML) without corrupting it — the binary
///    spreadsheet path is handled exclusively inside the tabular fast-path.
///
/// The change description ("add a type column") is written for the structured operation
/// grammar (<c>AddColumnAdjRegex</c>: "add a X column"). Unrecognized operations on
/// .csv/.tsv decline and fall through to the normal pipeline (covered by
/// <c>TabularFileServiceTests</c>); binary spreadsheets never fall through (they would be
/// corrupted by a text read-modify-write).
/// </summary>
public class TabularEditPipelineIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly DatabaseService _db;
    private readonly ThrowingClientFactory _clientFactory = new();

    public TabularEditPipelineIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "weaver_tabular_edit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = new DatabaseService(
            Path.Combine(_root, "weaver.db"),
            _root,
            Path.Combine(_root, "weaverconfig.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        _clientFactory.Dispose();
    }

    /// <summary>
    /// A real "add a type column" step against a real .csv file that carries a
    /// <c>FETCHED_AT:</c> metadata preamble (the benchmark-16 file shape):
    /// ResolveAndApplyEdit claims it via the tabular fast-path, applies the column
    /// structurally, preserves the preamble + all rows, and reports a done result with the
    /// <c>tabular</c> flag — zero LLM calls.
    /// </summary>
    [Fact]
    public async Task TabularCsv_AddColumn_EndToEndThroughResolveAndApplyEdit_NoLlm()
    {
        const string relPath = "data.csv";
        var fullPath = Path.Combine(_root, relPath);
        var csv =
            "FETCHED_AT: 2026-08-13\n" +
            "id,name\n" +
            "1,bulbasaur\n" +
            "25,pikachu\n" +
            "448,lucario\n";
        await File.WriteAllTextAsync(fullPath, csv);

        var controller = BuildController();
        var step = new PlanStep
        {
            File = relPath,
            Change = "add a type column",
            LineNumber = 0,
            OldString = null,
            NewString = null,
        };
        var allResults = new List<object>();

        var nextIndex = await InvokeResolveAndApplyEditAsync(controller, step, allResults);

        // 1. The step completed (stepIndex 0 → next is 1) with zero LLM calls.
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        // 2. Exactly one done result, flagged as a tabular edit with the reason.
        var result = Assert.Single(allResults);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("done", dict["status"]);
        Assert.Equal(relPath, dict["path"]);
        Assert.True(Assert.IsType<bool>(dict["tabular"]));
        Assert.Contains("added column 'type'", Assert.IsType<string>(dict["reason"]));

        // 3. The header gained the column; the FETCHED_AT: preamble and every data row survived.
        var final = await File.ReadAllTextAsync(fullPath);
        Assert.StartsWith("FETCHED_AT: 2026-08-13", final);
        Assert.Contains("id,name,type", final);
        Assert.Contains("1,bulbasaur,", final);
        Assert.Contains("25,pikachu,", final);
        Assert.Contains("448,lucario,", final);
        // No preamble or row was dropped — the structure round-trips exactly.
        var parsed = TabularFileService.ParseCsv(final);
        Assert.Equal(new List<string> { "FETCHED_AT: 2026-08-13" }, parsed.Preamble);
        Assert.Equal(new List<string> { "id", "name", "type" }, parsed.Header);
        Assert.Equal(3, parsed.Rows.Count);
        Assert.Equal(new List<string> { "1", "bulbasaur", "" }, parsed.Rows[0]);
        Assert.Equal(new List<string> { "448", "lucario", "" }, parsed.Rows[2]);
    }

    /// <summary>
    /// A real "add a row" step against the same FETCHED_AT-preamble CSV, driving
    /// <c>TryAddRow</c>'s key=value pair grammar (including a pair that names a column
    /// absent from the header, which extends the header and backfills existing rows)
    /// through the real ResolveAndApplyEdit flow — the new row lands structurally with
    /// zero LLM calls.
    /// </summary>
    [Fact]
    public async Task TabularCsv_AddRow_EndToEndThroughResolveAndApplyEdit_NoLlm()
    {
        const string relPath = "data.csv";
        var fullPath = Path.Combine(_root, relPath);
        var csv =
            "FETCHED_AT: 2026-08-13\n" +
            "id,name\n" +
            "1,bulbasaur\n" +
            "25,pikachu\n" +
            "448,lucario\n";
        await File.WriteAllTextAsync(fullPath, csv);

        var controller = BuildController();
        var step = new PlanStep
        {
            File = relPath,
            Change = "add a row with id=26, name=raichu, type=electric",
            LineNumber = 0,
            OldString = null,
            NewString = null,
        };
        var allResults = new List<object>();

        var nextIndex = await InvokeResolveAndApplyEditAsync(controller, step, allResults);

        // 1. The step completed (stepIndex 0 → next is 1) with zero LLM calls.
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        // 2. Exactly one done result, flagged as a tabular edit with the row reason.
        var result = Assert.Single(allResults);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("done", dict["status"]);
        Assert.Equal(relPath, dict["path"]);
        Assert.True(Assert.IsType<bool>(dict["tabular"]));
        Assert.Contains("added a row (3 field(s))", Assert.IsType<string>(dict["reason"]));

        // 3. The new row landed: preamble + header intact, the 'type' pair extended the
        //    header (backfilling existing rows), and the key=value row is appended last.
        var final = await File.ReadAllTextAsync(fullPath);
        Assert.StartsWith("FETCHED_AT: 2026-08-13", final);
        Assert.Contains("26,raichu,electric", final);
        var parsed = TabularFileService.ParseCsv(final);
        Assert.Equal(new List<string> { "FETCHED_AT: 2026-08-13" }, parsed.Preamble);
        Assert.Equal(new List<string> { "id", "name", "type" }, parsed.Header);
        Assert.Equal(4, parsed.Rows.Count);
        // Existing rows were backfilled with an empty cell for the new column.
        Assert.Equal(new List<string> { "1", "bulbasaur", "" }, parsed.Rows[0]);
        Assert.Equal(new List<string> { "448", "lucario", "" }, parsed.Rows[2]);
        // The new key=value row sits at the end, keys mapped to their columns.
        Assert.Equal(new List<string> { "26", "raichu", "electric" }, parsed.Rows[3]);
    }

    /// <summary>
    /// The same structural operation against a real .xlsx workbook: the binary spreadsheet
    /// path decodes the ZIP, adds the column, re-encodes a valid workbook, and lands with
    /// zero LLM calls — proving a text read-modify-write can never corrupt the file.
    /// </summary>
    [Fact]
    public async Task TabularXlsx_AddColumn_EndToEndThroughResolveAndApplyEdit_NoLlm()
    {
        const string relPath = "data.xlsx";
        var fullPath = Path.Combine(_root, relPath);

        // Build a real workbook with the shared-strings + numeric-cell shapes.
        var table = new TabularFileService.CsvTable();
        table.Header = new List<string> { "id", "name" };
        table.Rows.Add(new List<string> { "1", "bulbasaur" });
        table.Rows.Add(new List<string> { "25", "pikachu" });
        await File.WriteAllBytesAsync(fullPath, TabularFileService.SerializeXlsx(table));

        var controller = BuildController();
        var step = new PlanStep
        {
            File = relPath,
            Change = "add a type column",
            LineNumber = 0,
            OldString = null,
            NewString = null,
        };
        var allResults = new List<object>();

        var nextIndex = await InvokeResolveAndApplyEditAsync(controller, step, allResults);

        // 1. The step completed with zero LLM calls.
        Assert.Equal(1, nextIndex);
        Assert.Equal(0, _clientFactory.CreateClientCalls);

        // 2. Done result flagged as tabular.
        var result = Assert.Single(allResults);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("done", dict["status"]);
        Assert.Equal(relPath, dict["path"]);
        Assert.True(Assert.IsType<bool>(dict["tabular"]));
        Assert.Contains("added column 'type'", Assert.IsType<string>(dict["reason"]));

        // 3. The workbook round-tripped: header now has id,name,type; both rows kept; ZIP intact.
        var bytes = await File.ReadAllBytesAsync(fullPath);
        var decoded = TabularFileService.ParseXlsx(bytes);
        Assert.NotNull(decoded.Header);
        Assert.Equal(new List<string> { "id", "name", "type" }, decoded.Header);
        Assert.Equal(2, decoded.Rows.Count);
        Assert.Equal("1", decoded.Rows[0][0]);
        Assert.Equal("bulbasaur", decoded.Rows[0][1]);
        Assert.Equal("25", decoded.Rows[1][0]);
        Assert.Equal("pikachu", decoded.Rows[1][1]);
        // The re-encoded bytes are a valid ZIP with the OOXML parts intact.
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes), System.IO.Compression.ZipArchiveMode.Read))
        {
            Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
            Assert.NotNull(zip.GetEntry("xl/sharedStrings.xml"));
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private AgentController BuildController()
    {
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        SetField(controller, "_clientFactory", _clientFactory);
        SetField(controller, "_configFile", new ConfigFileService(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        return controller;
    }

    private async Task<int> InvokeResolveAndApplyEditAsync(
        AgentController controller, PlanStep step, List<object> allResults)
    {
        var method = typeof(AgentController).GetMethod(
            "ResolveAndApplyEdit", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ResolveAndApplyEdit not found");
        var task = (Task<int>)method.Invoke(controller, new object?[]
        {
            step, _root, /*emitSse*/ false, CancellationToken.None, allResults,
            /*stepIndex*/ 0, /*prompt*/ null, /*plan*/ null, /*planItemIndex*/ -1,
            /*cardId*/ null, /*attachedFiles*/ null, /*replanDepth*/ 0,
            /*onActivity*/ null, /*skipLlmPreResolution*/ false
        })!;
        return await task;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {name} not found");
        field.SetValue(target, value);
    }

    /// <summary>
    /// An <see cref="IHttpClientFactory"/> that records and THROWS on every
    /// <c>CreateClient</c> call — the controller's only LLM paths (CallLlmRaw,
    /// CallLlmRawStreaming, CallLlmRawText) all go through this factory, so a nonzero
    /// call count means the tabular step secretly touched the LLM.
    /// </summary>
    private sealed class ThrowingClientFactory : IHttpClientFactory, IDisposable
    {
        public int CreateClientCalls;

        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref CreateClientCalls);
            throw new InvalidOperationException(
                $"LLM client requested ('{name}') during a tabular edit — the step must not call the LLM");
        }

        public HttpClient CreateClient() => CreateClient("default");

        public void Dispose() { }
    }
}
