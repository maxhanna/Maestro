using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks <see cref="TabularFileService"/> — the structural CSV/TSV/XLSX editing layer that
/// lets the agent modify tabular data without corrupting RFC-4180 quoting or the .xlsx ZIP
/// container. Covers: CSV parse/serialize round-trips (quoting, BOM, CRLF, embedded newlines,
/// preamble lines), TSV, XLSX read/write (shared strings, inline strings, numeric cells,
/// empty cells), every structured operation, and the decline paths.
/// </summary>
public class TabularFileServiceTests
{
    // ── File-type detection ──────────────────────────────────────────────────

    [Theory]
    [InlineData("data.csv", true)]
    [InlineData("data.tsv", true)]
    [InlineData("report.xlsx", true)]
    [InlineData("report.xls", true)]
    [InlineData("DATA.CSV", true)]
    [InlineData("notes.md", false)]
    [InlineData("app.ts", false)]
    [InlineData("", false)]
    public void IsTabularFile_ClassifiesByExtension(string path, bool expected)
        => Assert.Equal(expected, TabularFileService.IsTabularFile(path));

    [Theory]
    [InlineData("data.csv", true)]
    [InlineData("data.tsv", true)]
    [InlineData("report.xlsx", false)]
    public void IsDelimitedText_OnlyTextTabular(string path, bool expected)
        => Assert.Equal(expected, TabularFileService.IsDelimitedText(path));

    [Theory]
    [InlineData("report.xlsx", true)]
    [InlineData("report.xls", true)]
    [InlineData("data.csv", false)]
    public void IsSpreadsheetBinary_OnlyExcelContainers(string path, bool expected)
        => Assert.Equal(expected, TabularFileService.IsSpreadsheetBinary(path));

    [Fact]
    public void DelimiterFor_TsvIsTab_CsvIsComma()
    {
        Assert.Equal('\t', TabularFileService.DelimiterFor("x.tsv"));
        Assert.Equal(',', TabularFileService.DelimiterFor("x.csv"));
        Assert.Equal(',', TabularFileService.DelimiterFor("x.xlsx")); // non-text default
    }

    // ── CSV parse ────────────────────────────────────────────────────────────

    [Fact]
    public void ParseCsv_HeaderAndRows()
    {
        var t = TabularFileService.ParseCsv("id,name\n1,bulbasaur\n25,pikachu\n");
        Assert.Empty(t.Preamble);
        Assert.Equal(new[] { "id", "name" }, t.Header);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(new[] { "1", "bulbasaur" }, t.Rows[0]);
        Assert.Equal(new[] { "25", "pikachu" }, t.Rows[1]);
    }

    [Fact]
    public void ParseCsv_QuotedFieldWithCommaAndQuote()
    {
        var t = TabularFileService.ParseCsv("name,note\n\"Smith, John\",\"said \"\"hi\"\"\"\n");
        Assert.Single(t.Rows);
        Assert.Equal("Smith, John", t.Rows[0][0]);
        Assert.Equal("said \"hi\"", t.Rows[0][1]);
    }

    [Fact]
    public void ParseCsv_QuotedFieldWithEmbeddedNewline()
    {
        var t = TabularFileService.ParseCsv("a,b\n\"line1\nline2\",2\n");
        Assert.Single(t.Rows);
        Assert.Equal("line1\nline2", t.Rows[0][0]);
    }

    [Fact]
    public void ParseCsv_CrlfLineEndings_Normalized()
    {
        var t = TabularFileService.ParseCsv("id,name\r\n1,bulbasaur\r\n2,ivysaur\r\n");
        Assert.Equal(new[] { "id", "name" }, t.Header);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("ivysaur", t.Rows[1][1]);
    }

    [Fact]
    public void ParseCsv_StripsLeadingBom()
    {
        var t = TabularFileService.ParseCsv("\uFEFFid,name\n1,bulbasaur\n");
        Assert.Equal(new[] { "id", "name" }, t.Header);
    }

    [Fact]
    public void ParseCsv_PreservesMetadataPreamble()
    {
        var t = TabularFileService.ParseCsv("FETCHED_AT: 2026-08-13\nid,name\n1,bulbasaur\n");
        Assert.Equal(new[] { "FETCHED_AT: 2026-08-13" }, t.Preamble);
        Assert.Equal(new[] { "id", "name" }, t.Header);
        Assert.Single(t.Rows);
    }

    [Fact]
    public void ParseCsv_EmptyInput_EmptyTable()
    {
        var t = TabularFileService.ParseCsv("");
        Assert.Empty(t.Preamble);
        Assert.Null(t.Header);
        Assert.Empty(t.Rows);
    }

    [Fact]
    public void ParseCsv_TrailingEmptyField_Preserved()
    {
        var t = TabularFileService.ParseCsv("a,b,c\n1,2,\n");
        Assert.Equal(3, t.Header!.Count);
        Assert.Equal(new[] { "1", "2", "" }, t.Rows[0]);
    }

    // ── CSV serialize + round-trip ───────────────────────────────────────────

    [Fact]
    public void SerializeCsv_QuotesOnlyWhenNeeded()
    {
        var t = new TabularFileService.CsvTable();
        t.Header = new List<string> { "id", "note" };
        t.Rows.Add(new List<string> { "1", "plain" });
        t.Rows.Add(new List<string> { "2", "comma, here" });
        t.Rows.Add(new List<string> { "3", "quote \"here\"" });

        var csv = TabularFileService.SerializeCsv(t);
        Assert.Contains("1,plain\n", csv);
        Assert.Contains("2,\"comma, here\"\n", csv);
        Assert.Contains("3,\"quote \"\"here\"\"\"\n", csv);
    }

    [Fact]
    public void Csv_RoundTrip_PreservesLogicalValues()
    {
        const string input = "id,name,note\n1,bulbasaur,grass\n2,\"Smith, John\",\"said \"\"hi\"\"\"\n";
        var once = TabularFileService.ParseCsv(input);
        var twice = TabularFileService.ParseCsv(TabularFileService.SerializeCsv(once));
        AssertTablesEqual(once, twice);
    }

    [Fact]
    public void Csv_RoundTrip_PreservesPreamble()
    {
        const string input = "FETCHED_AT: 2026-08-13\nid,name\n1,bulbasaur\n";
        var once = TabularFileService.ParseCsv(input);
        var out2 = TabularFileService.SerializeCsv(once);
        Assert.StartsWith("FETCHED_AT: 2026-08-13\n", out2);
        var twice = TabularFileService.ParseCsv(out2);
        AssertTablesEqual(once, twice);
    }

    // ── TSV ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Tsv_RoundTrip_PreservesValuesWithCommas()
    {
        const string input = "id\tname\n1\t\"Smith, John\"\n";
        var t = TabularFileService.ParseTsv(input);
        Assert.Equal("Smith, John", t.Rows[0][1]);
        var out2 = TabularFileService.SerializeTsv(t);
        var t2 = TabularFileService.ParseTsv(out2);
        AssertTablesEqual(t, t2);
    }

    // ── XLSX codec ────────────────────────────────────────────────────────────

    [Fact]
    public void Xlsx_RoundTrip_PreservesStringsAndNumbers()
    {
        var t = new TabularFileService.CsvTable();
        t.Header = new List<string> { "id", "name", "weight" };
        t.Rows.Add(new List<string> { "1", "bulbasaur", "6.9" });
        t.Rows.Add(new List<string> { "25", "pikachu", "6.0" });

        var bytes = TabularFileService.SerializeXlsx(t);
        Assert.True(bytes.Length > 0);
        var parsed = TabularFileService.ParseXlsx(bytes);
        AssertTablesEqual(t, parsed);
    }

    [Fact]
    public void Xlsx_RoundTrip_PreservesSpecialCharacters()
    {
        var t = new TabularFileService.CsvTable();
        t.Header = new List<string> { "name", "note" };
        t.Rows.Add(new List<string> { "\"Smith, John\"", "said \"hi\" & <bye>" });
        var parsed = TabularFileService.ParseXlsx(TabularFileService.SerializeXlsx(t));
        AssertTablesEqual(t, parsed);
    }

    [Fact]
    public void Xlsx_RoundTrip_EmptyCellsPreserved()
    {
        var t = new TabularFileService.CsvTable();
        t.Header = new List<string> { "a", "b", "c" };
        t.Rows.Add(new List<string> { "1", "", "3" });
        var parsed = TabularFileService.ParseXlsx(TabularFileService.SerializeXlsx(t));
        Assert.Equal(new[] { "1", "", "3" }, parsed.Rows[0]);
    }

    [Fact]
    public void Xlsx_IsAValidZip_WithExpectedParts()
    {
        var t = new TabularFileService.CsvTable();
        t.Header = new List<string> { "id" };
        t.Rows.Add(new List<string> { "1" });
        var bytes = TabularFileService.SerializeXlsx(t);

        using var ms = new MemoryStream(bytes);
        using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
        Assert.NotNull(zip.GetEntry("xl/sharedStrings.xml"));
    }

    // ── Structured operations: add column ────────────────────────────────────

    [Fact]
    public void Edit_AddColumn_AppendsEmptyValues()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name\n1,bulbasaur\n", ',', "add a column 'type'", out var csv, out var reason);
        Assert.True(ok);
        Assert.Contains("type", reason);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal(new[] { "id", "name", "type" }, t.Header);
        Assert.Equal(new[] { "1", "bulbasaur", "" }, t.Rows[0]);
    }

    [Fact]
    public void Edit_AddColumn_WithDefaultValue()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name\n1,bulbasaur\n", ',', "add column type with value grass", out var csv, out _);
        Assert.True(ok);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal(new[] { "1", "bulbasaur", "grass" }, t.Rows[0]);
    }

    // ── Structured operations: remove column ─────────────────────────────────

    [Fact]
    public void Edit_RemoveColumn_RemovesHeaderAndCells()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name,url\n1,bulbasaur,https://x/1/\n", ',', "remove the column url", out var csv, out _);
        Assert.True(ok);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal(new[] { "id", "name" }, t.Header);
        Assert.Equal(new[] { "1", "bulbasaur" }, t.Rows[0]);
    }

    [Fact]
    public void Edit_RemoveColumn_MissingColumn_Declines()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name\n1,bulbasaur\n", ',', "remove the column url", out _, out var reason);
        Assert.False(ok);
        Assert.Contains("not found", reason);
    }

    // ── Structured operations: rename column ─────────────────────────────────

    [Fact]
    public void Edit_RenameColumn_ChangesHeaderOnly()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,hp\n1,45\n", ',', "rename column hp to health", out var csv, out _);
        Assert.True(ok);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal(new[] { "id", "health" }, t.Header);
        Assert.Equal(new[] { "1", "45" }, t.Rows[0]);
    }

    // ── Structured operations: add row ───────────────────────────────────────

    [Fact]
    public void Edit_AddRow_MapsFieldsToColumns()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name\n1,bulbasaur\n", ',', "add a row with id=25, name=pikachu", out var csv, out _);
        Assert.True(ok);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(new[] { "25", "pikachu" }, t.Rows[1]);
    }

    [Fact]
    public void Edit_AddRow_ExtendsHeaderForNewColumns()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name\n1,bulbasaur\n", ',', "add a row with id=25, type=electric", out var csv, out _);
        Assert.True(ok);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal(new[] { "id", "name", "type" }, t.Header);
        Assert.Equal(new[] { "25", "", "electric" }, t.Rows[1]);
    }

    // ── Structured operations: delete rows ───────────────────────────────────

    [Fact]
    public void Edit_DeleteRows_Equality_RemovesMatches()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name\n1,bulbasaur\n25,pikachu\n", ',', "delete the row where name is pikachu", out var csv, out _);
        Assert.True(ok);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Single(t.Rows);
        Assert.Equal("bulbasaur", t.Rows[0][1]);
    }

    [Fact]
    public void Edit_DeleteRows_Contains_RemovesMatches()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name\n1,bulbasaur\n2,ivysaur\n", ',', "remove rows where name contains saur", out var csv, out _);
        Assert.True(ok);
        Assert.Empty(TabularFileService.ParseCsv(csv!).Rows);
    }

    // ── Structured operations: set cell ──────────────────────────────────────

    [Fact]
    public void Edit_SetCell_UpdatesMatchingRows()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,hp\n1,45\n25,35\n", ',', "set hp to 60 where id is 25", out var csv, out _);
        Assert.True(ok);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal("45", t.Rows[0][1]);
        Assert.Equal("60", t.Rows[1][1]);
    }

    // ── Structured operations: replace value ─────────────────────────────────

    [Fact]
    public void Edit_RenameValues_ReplacesInColumn()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,type\n1,grass\n2,grass\n", ',', "change all grass to plant in column type", out var csv, out _);
        Assert.True(ok);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal("plant", t.Rows[0][1]);
        Assert.Equal("plant", t.Rows[1][1]);
    }

    // ── Structured operations: MASS edit (set-cell contains / where-in / fill) ──

    [Fact]
    public void Edit_SetCell_Contains_UpdatesMatchingRows()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name,type\n1,bulbasaur,\n25,pikachu,\n", ',',
            "set type to electric where name contains chu", out var csv, out var reason);
        Assert.True(ok);
        Assert.Contains("contains", reason);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal("", t.Rows[0][2]);        // bulbasaur untouched
        Assert.Equal("electric", t.Rows[1][2]); // pikachu matched via contains
    }

    [Fact]
    public void Edit_SetCell_WhereInList_UpdatesMatchingRows()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name,type\n1,bulbasaur,\n25,pikachu,\n448,lucario,\n", ',',
            "set type to legendary where name is in (bulbasaur, lucario)", out var csv, out var reason);
        Assert.True(ok);
        Assert.Contains("is in", reason);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal("legendary", t.Rows[0][2]);
        Assert.Equal("", t.Rows[1][2]);          // pikachu not in the list
        Assert.Equal("legendary", t.Rows[2][2]);
    }

    [Fact]
    public void Edit_FillColumn_ForAllRows_SetsEveryRow()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name,type\n1,bulbasaur,\n25,pikachu,\n", ',',
            "set the type column to normal for all rows", out var csv, out var reason);
        Assert.True(ok);
        Assert.Contains("filled column 'type'", reason);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal("normal", t.Rows[0][2]);
        Assert.Equal("normal", t.Rows[1][2]);
    }

    [Fact]
    public void Edit_FillColumn_SimpleForm_WithValue()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name,type\n1,bulbasaur,\n", ',', "fill the type column with normal", out var csv, out _);
        Assert.True(ok);
        var t = TabularFileService.ParseCsv(csv!);
        Assert.Equal("normal", t.Rows[0][2]);
    }

    // ── Benchmark-21 shape: the full fetch → add column → add rows → mass edit →
    // ── edit-rows sequence against a FETCHED_AT-preamble CSV, all structural ops ──

    [Fact]
    public void Edit_Benchmark21Sequence_AddColumnAddRowsMassEditAndEditRows()
    {
        var csv = "FETCHED_AT: 2026-08-13\nid,name,url\n1,bulbasaur,https://pokeapi.co/api/v2/pokemon/1/\n25,pikachu,https://pokeapi.co/api/v2/pokemon/25/\n";

        // STEP 2 — add a type column (empty for existing rows).
        Assert.True(TabularFileService.TryEditDelimited(csv, ',', "add a type column", out csv, out _));
        // STEP 3 — add the three custom rows with the placeholder type.
        Assert.True(TabularFileService.TryEditDelimited(csv, ',',
            "add a row with id=1026, name=weavmon, type=unknown", out csv, out _));
        Assert.True(TabularFileService.TryEditDelimited(csv, ',',
            "add a row with id=1027, name=kanbanite, type=unknown", out csv, out _));
        Assert.True(TabularFileService.TryEditDelimited(csv, ',',
            "add a row with id=1028, name=bugcatcher, type=unknown", out csv, out _));
        // STEP 4 — MASS EDIT: one op fills the ORIGINAL rows' empty type cells with 'normal',
        // leaving the three custom rows' 'unknown' placeholder untouched.
        Assert.True(TabularFileService.TryEditDelimited(csv, ',',
            "set type to 'normal' where type is empty", out csv, out var massReason));
        Assert.Contains("2 row(s)", massReason);
        Assert.Contains("(empty)", massReason);
        // STEP 5 — EDIT ROWS: each custom pokemon gets its real type (overwrites the placeholder).
        Assert.True(TabularFileService.TryEditDelimited(csv, ',',
            "set type to 'electric' where name is 'weavmon'", out csv, out _));
        Assert.True(TabularFileService.TryEditDelimited(csv, ',',
            "set type to 'ghost' where name is 'kanbanite'", out csv, out _));
        Assert.True(TabularFileService.TryEditDelimited(csv, ',',
            "set type to 'fairy' where name is 'bugcatcher'", out csv, out _));

        var t = TabularFileService.ParseCsv(csv);
        Assert.Equal(new[] { "FETCHED_AT: 2026-08-13" }, t.Preamble);
        Assert.Equal(new[] { "id", "name", "url", "type" }, t.Header);
        Assert.Equal(5, t.Rows.Count);
        // Original rows: their EMPTY type cells were mass-edited to 'normal'.
        Assert.Equal("normal", t.Rows[0][3]);
        Assert.Equal("bulbasaur", t.Rows[0][1]);
        Assert.Equal("normal", t.Rows[1][3]);
        // The three custom rows (indexes 2-4): placeholder overwritten by the per-row edits.
        Assert.Equal(new[] { "1026", "weavmon", "", "electric" }, t.Rows[2]);
        Assert.Equal(new[] { "1027", "kanbanite", "", "ghost" }, t.Rows[3]);
        Assert.Equal(new[] { "1028", "bugcatcher", "", "fairy" }, t.Rows[4]);
        Assert.DoesNotContain("unknown", csv); // the per-row edits replaced the placeholder
    }

    // ── Decline paths ─────────────────────────────────────────────────────────

    [Fact]
    public void Edit_UnrecognizedOperation_Declines()
    {
        var ok = TabularFileService.TryEditDelimited(
            "id,name\n1,bulbasaur\n", ',', "frobnicate the whole file", out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void Edit_BlankDescription_Declines()
    {
        var ok = TabularFileService.TryEditDelimited("id,name\n1,x\n", ',', "   ", out _, out _);
        Assert.False(ok);
    }

    // ── XLSX edit operation end-to-end ───────────────────────────────────────

    [Fact]
    public void EditXlsx_AddColumn_RoundTripsThroughTheContainer()
    {
        var t = new TabularFileService.CsvTable();
        t.Header = new List<string> { "id", "name" };
        t.Rows.Add(new List<string> { "1", "bulbasaur" });
        var bytes = TabularFileService.SerializeXlsx(t);

        var ok = TabularFileService.TryEditXlsx(bytes, "add a column 'type'", out var newBytes, out var reason);
        Assert.True(ok);
        Assert.Contains("type", reason);
        var parsed = TabularFileService.ParseXlsx(newBytes!);
        Assert.Equal(new[] { "id", "name", "type" }, parsed.Header);
        Assert.Equal(new[] { "1", "bulbasaur", "" }, parsed.Rows[0]);
    }

    [Fact]
    public void EditXlsx_UnrecognizedOperation_Declines()
    {
        var t = new TabularFileService.CsvTable();
        t.Header = new List<string> { "id" };
        t.Rows.Add(new List<string> { "1" });
        var bytes = TabularFileService.SerializeXlsx(t);

        var ok = TabularFileService.TryEditXlsx(bytes, "do something impossible", out _, out _);
        Assert.False(ok);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertTablesEqual(TabularFileService.CsvTable a, TabularFileService.CsvTable b)
    {
        Assert.Equal(a.Preamble, b.Preamble);
        Assert.Equal(a.Header ?? new List<string>(), b.Header ?? new List<string>());
        Assert.Equal(a.Rows.Count, b.Rows.Count);
        for (var i = 0; i < a.Rows.Count; i++)
            Assert.Equal(a.Rows[i], b.Rows[i]);
    }
}
