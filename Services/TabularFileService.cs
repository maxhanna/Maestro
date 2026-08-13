using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Weaver.Services;

// ═══════════════════════════════════════════════════════════════════════════════
//  TABULAR FILE SERVICE — structural CSV / TSV / XLSX editing
// ═══════════════════════════════════════════════════════════════════════════════
//
// The agent historically treated .csv/.xlsx files as opaque text and applied blind
// oldString/newString replacements. That corrupts RFC-4180 CSV (commas, quotes, and
// embedded newlines inside a single field) and is outright impossible for .xlsx — a
// ZIP container of XML that a text read-modify-write destroys.
//
// This service gives tabular files a real editing model:
//   • a CsvTable (preamble lines + header row + data rows) is parsed/serialized with
//     proper quoting, so a cell value like `"Smith, John"` survives a round-trip;
//   • .xlsx is decoded from (and encoded back to) OOXML, so an Excel workbook can be
//     edited in place;
//   • a small deterministic operation vocabulary (add/remove/rename column, add/delete
//     row, set a cell, replace a value) is applied STRUCTURALLY — never via text munging.
//
// Everything here is a pure function of (file content, change description): no LLM, no
// network. Unknown operations decline (return false) so the caller falls back to the
// normal pipeline instead of guessing.

public static class TabularFileService
{
    /// <summary>A parsed tabular document: optional preamble (metadata) lines, an optional
    /// header row, and the data rows. Cell values are always the UNQUOTED logical values.</summary>
    public sealed class CsvTable
    {
        public List<string> Preamble { get; } = new();
        public List<string>? Header { get; set; }
        public List<List<string>> Rows { get; } = new();
    }

    // ── File-type detection ──────────────────────────────────────────────────

    public static bool IsTabularFile(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return false;
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        return ext is ".csv" or ".tsv" or ".xlsx" or ".xls";
    }

    public static bool IsDelimitedText(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return false;
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        return ext is ".csv" or ".tsv";
    }

    public static bool IsSpreadsheetBinary(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return false;
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        return ext is ".xlsx" or ".xls";
    }

    public static char DelimiterFor(string relPath)
        => Path.GetExtension(relPath).ToLowerInvariant() == ".tsv" ? '\t' : ',';

    // ── CSV / TSV codec ──────────────────────────────────────────────────────

    /// <summary>Parses delimiter-separated text (RFC-4180: quoted fields, doubled quotes,
    /// embedded delimiters and newlines). Leading lines that look like metadata
    /// (`KEY: value`) are preserved as a preamble, the next record is the header, and the
    /// rest are data rows.</summary>
    public static CsvTable ParseDelimited(string text, char delimiter)
    {
        var table = new CsvTable();
        var records = ParseRecords(text, delimiter);
        if (records.Count == 0) return table;

        var i = 0;
        // Preamble: leading single-field records shaped like `KEY: value` (FETCHED_AT: ...).
        while (i < records.Count && IsMetadataLine(records[i]))
        {
            table.Preamble.Add(records[i][0]);
            i++;
        }

        if (i < records.Count)
        {
            table.Header = records[i];
            i++;
        }
        while (i < records.Count)
        {
            table.Rows.Add(records[i]);
            i++;
        }
        return table;
    }

    public static CsvTable ParseCsv(string text) => ParseDelimited(text, ',');
    public static CsvTable ParseTsv(string text) => ParseDelimited(text, '\t');

    /// <summary>Serializes a table back to delimiter-separated text. Fields are quoted only
    /// when necessary (delimiter, quote, newline, or leading/trailing whitespace). Preamble
    /// lines are emitted verbatim first.</summary>
    public static string SerializeDelimited(CsvTable table, char delimiter)
    {
        var sb = new StringBuilder();
        foreach (var line in table.Preamble)
            sb.Append(line).Append('\n');
        if (table.Header != null)
            sb.Append(SerializeRecord(table.Header, delimiter)).Append('\n');
        foreach (var row in table.Rows)
            sb.Append(SerializeRecord(row, delimiter)).Append('\n');
        return sb.ToString();
    }

    public static string SerializeCsv(CsvTable table) => SerializeDelimited(table, ',');
    public static string SerializeTsv(CsvTable table) => SerializeDelimited(table, '\t');

    private static List<List<string>> ParseRecords(string text, char delimiter)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        var n = text.Length;

        // Strip a leading UTF-8 BOM so the first header cell isn't "\uFEFFid".
        if (n > 0 && text[0] == '\uFEFF') i = 1;

        void EndField() { fields.Add(field.ToString()); field.Clear(); }
        void EndRecord() { EndField(); records.Add(fields); fields = new List<string>(); }

        while (i < n)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                i++;
                continue;
            }
            if (c == delimiter)
            {
                EndField();
                i++;
                continue;
            }
            if (c == '\r')
            {
                // CRLF or bare CR both end the record.
                EndRecord();
                if (i + 1 < n && text[i + 1] == '\n') i += 2; else i++;
                continue;
            }
            if (c == '\n')
            {
                EndRecord();
                i++;
                continue;
            }
            field.Append(c);
            i++;
        }
        // A trailing field/record without a final newline (or a trailing comma) still counts.
        if (field.Length > 0 || fields.Count > 0) EndRecord();
        return records;
    }

    private static string SerializeRecord(List<string> fields, char delimiter)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(delimiter);
            var f = fields[i] ?? "";
            var needsQuote = f.IndexOf(delimiter) >= 0 || f.IndexOf('"') >= 0 ||
                             f.IndexOf('\n') >= 0 || f.IndexOf('\r') >= 0 ||
                             f.Length != f.Trim().Length;
            if (needsQuote)
                sb.Append('"').Append(f.Replace("\"", "\"\"")).Append('"');
            else
                sb.Append(f);
        }
        return sb.ToString();
    }

    private static readonly Regex MetadataLineRegex = new(
        @"^[A-Za-z_][A-Za-z0-9_]*\s*:\s*.*$", RegexOptions.Compiled);

    private static bool IsMetadataLine(List<string> fields)
        => fields.Count == 1 && MetadataLineRegex.IsMatch(fields[0]);

    // ── XLSX codec ───────────────────────────────────────────────────────────

    private static readonly XNamespace SsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace DocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Decodes the first worksheet of an .xlsx workbook into a table (first row is
    /// the header). Shared strings, inline strings, and numeric cells are all supported.</summary>
    public static CsvTable ParseXlsx(byte[] bytes)
    {
        var table = new CsvTable();
        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        var sharedStrings = ReadSharedStrings(zip);
        var sheetPath = ResolveFirstWorksheetPath(zip);
        if (sheetPath == null) return table;

        var entry = zip.GetEntry(sheetPath);
        if (entry == null) return table;
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        var grid = new Dictionary<(int row, int col), string>();
        var maxRow = 0;
        var maxCol = 0;
        foreach (var rowEl in doc.Descendants(SsMain + "row"))
        {
            var rowIdx = (int?)rowEl.Attribute("r") ?? (maxRow + 1);
            var colCursor = 0;
            foreach (var cEl in rowEl.Elements(SsMain + "c"))
            {
                colCursor++;
                var colIdx = ColumnIndexFromReference((string?)cEl.Attribute("r")) ?? colCursor;
                var type = (string?)cEl.Attribute("t");
                var value = ReadCellValue(cEl, type, sharedStrings);
                if (value == null) continue;
                grid[(rowIdx, colIdx)] = value;
                if (rowIdx > maxRow) maxRow = rowIdx;
                if (colIdx > maxCol) maxCol = colIdx;
            }
        }
        if (maxRow == 0) return table;

        for (var r = 1; r <= maxRow; r++)
        {
            var row = new List<string>(maxCol);
            for (var c = 1; c <= maxCol; c++)
                row.Add(grid.TryGetValue((r, c), out var v) ? v : "");
            if (r == 1) table.Header = row;
            else table.Rows.Add(row);
        }
        return table;
    }

    /// <summary>Encodes a table into a minimal, valid .xlsx workbook (one "Sheet1" worksheet,
    /// shared strings for text, native numbers where a cell is numeric).</summary>
    public static byte[] SerializeXlsx(CsvTable table)
    {
        var header = table.Header ?? new List<string>();
        var allRows = new List<List<string>> { header };
        allRows.AddRange(table.Rows);

        var sharedStrings = new List<string>();
        var indexByText = new Dictionary<string, int>(StringComparer.Ordinal);
        int SsIndex(string s)
        {
            if (!indexByText.TryGetValue(s, out var idx))
            {
                idx = sharedStrings.Count;
                sharedStrings.Add(s);
                indexByText[s] = idx;
            }
            return idx;
        }

        // Determine numeric-vs-string per cell so numbers stay numbers in Excel.
        var isNumeric = new List<List<bool>>();
        foreach (var row in allRows)
        {
            var flags = new List<bool>(row.Count);
            foreach (var cell in row) flags.Add(IsNumeric(cell));
            isNumeric.Add(flags);
        }

        var sheetXml = BuildWorksheetXml(allRows, isNumeric, SsIndex);
        var sstXml = BuildSharedStringsXml(sharedStrings);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", ContentTypesXml);
            WriteEntry(zip, "_rels/.rels", RootRelsXml);
            WriteEntry(zip, "xl/workbook.xml", WorkbookXml);
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
            WriteEntry(zip, "xl/worksheets/sheet1.xml", sheetXml);
            WriteEntry(zip, "xl/sharedStrings.xml", sstXml);
            WriteEntry(zip, "xl/styles.xml", StylesXml);
        }
        return ms.ToArray();
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return result;
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var si in doc.Descendants(SsMain + "si"))
        {
            // Concatenate every <t> run within <si> (rich-text runs split a string).
            result.Add(string.Concat(si.Descendants(SsMain + "t").Select(t => t.Value)));
        }
        return result;
    }

    private static string? ResolveFirstWorksheetPath(ZipArchive zip)
    {
        // Default to the conventional sheet1 path; refine via workbook + rels when present.
        string? first = null;
        var workbookEntry = zip.GetEntry("xl/workbook.xml");
        if (workbookEntry != null)
        {
            try
            {
                using var stream = workbookEntry.Open();
                var wb = XDocument.Load(stream);
                var sheet = wb.Descendants(SsMain + "sheet").FirstOrDefault();
                if (sheet != null)
                {
                    var rid = (string?)sheet.Attribute(DocRel + "id");
                    var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
                    if (rid != null && relsEntry != null)
                    {
                        using var relsStream = relsEntry.Open();
                        var rels = XDocument.Load(relsStream);
                        var rel = rels.Descendants(PkgRel + "Relationship")
                            .FirstOrDefault(r => (string?)r.Attribute("Id") == rid);
                        var target = (string?)rel?.Attribute("Target");
                        if (!string.IsNullOrWhiteSpace(target))
                        {
                            target = target.TrimStart('/');
                            if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                                target = "xl/" + target;
                            first = target;
                        }
                    }
                }
            }
            catch { }
        }
        if (first == null && zip.GetEntry("xl/worksheets/sheet1.xml") != null)
            first = "xl/worksheets/sheet1.xml";
        return first;
    }

    private static string? ReadCellValue(XElement cEl, string? type, List<string> sharedStrings)
    {
        if (type == "inlineStr")
            return string.Concat(cEl.Descendants(SsMain + "t").Select(t => t.Value));
        var v = (string?)cEl.Element(SsMain + "v");
        if (type == "s")
        {
            if (v == null) return "";
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) &&
                   idx >= 0 && idx < sharedStrings.Count
                ? sharedStrings[idx]
                : "";
        }
        // t="str" (formula string), t="b" (boolean), or a bare numeric cell — the <v> text is the value.
        return v;
    }

    private static int? ColumnIndexFromReference(string? cellRef)
    {
        if (string.IsNullOrWhiteSpace(cellRef)) return null;
        var letters = new string(cellRef.TakeWhile(char.IsLetter).ToArray());
        if (letters.Length == 0) return null;
        var col = 0;
        foreach (var ch in letters.ToUpperInvariant())
            col = col * 26 + (ch - 'A' + 1);
        return col;
    }

    private static string ColumnLetter(int index)
    {
        var sb = new StringBuilder();
        while (index > 0)
        {
            index--;
            sb.Insert(0, (char)('A' + index % 26));
            index /= 26;
        }
        return sb.ToString();
    }

    private static bool IsNumeric(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return false;
        return long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
               double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static string BuildWorksheetXml(List<List<string>> rows, List<List<bool>> isNumeric,
        Func<string, int> ssIndex)
    {
        var ns = SsMain;
        var sheetData = new XElement(ns + "sheetData");
        for (var r = 0; r < rows.Count; r++)
        {
            var rowEl = new XElement(ns + "row", new XAttribute("r", r + 1));
            var row = rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                var value = row[c] ?? "";
                var cEl = new XElement(ns + "c", new XAttribute("r", ColumnLetter(c + 1) + (r + 1)));
                if (isNumeric[r][c])
                {
                    cEl.Add(new XElement(ns + "v", value));
                }
                else
                {
                    cEl.Add(new XAttribute("t", "s"));
                    cEl.Add(new XElement(ns + "v", ssIndex(value)));
                }
                rowEl.Add(cEl);
            }
            sheetData.Add(rowEl);
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(ns + "worksheet", sheetData)).ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildSharedStringsXml(List<string> strings)
    {
        var ns = SsMain;
        var sst = new XElement(ns + "sst",
            new XAttribute("count", strings.Count),
            new XAttribute("uniqueCount", strings.Count));
        foreach (var s in strings)
            sst.Add(new XElement(ns + "si", new XElement(ns + "t", s)));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), sst).ToString(SaveOptions.DisableFormatting);
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string ContentTypesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private const string RootRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorkbookXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private const string WorkbookRelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>" +
        "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
        "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
        "<borders count=\"1\"><border/></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
        "</styleSheet>";

    // ── Structured edit operations ───────────────────────────────────────────

    /// <summary>Applies a recognized structured operation to <paramref name="table"/> in
    /// place, returning true with a human-readable <paramref name="reason"/>. Returns false
    /// when the change description doesn't map to a known tabular operation (the caller must
    /// decline rather than guess).</summary>
    public static bool TryApplyEdit(CsvTable table, string changeDescription, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(changeDescription)) return false;
        var desc = changeDescription.Trim();
        // Preserve the last meaningful decline reason (e.g. "column 'x' not found") so the
        // caller can steer, even though each op resets its own out-reason on the way past.
        var declinedReason = "";

        if (TryRemoveColumn(table, desc, out reason)) return true;
        if (reason.Length > 0) declinedReason = reason;
        if (TryRenameColumn(table, desc, out reason)) return true;
        if (reason.Length > 0) declinedReason = reason;
        if (TryAddColumn(table, desc, out reason)) return true;
        if (reason.Length > 0) declinedReason = reason;
        if (TryDeleteRows(table, desc, out reason)) return true;
        if (reason.Length > 0) declinedReason = reason;
        if (TrySetCell(table, desc, out reason)) return true;
        if (reason.Length > 0) declinedReason = reason;
        if (TryAddRow(table, desc, out reason)) return true;
        if (reason.Length > 0) declinedReason = reason;
        if (TryRenameValues(table, desc, out reason)) return true;
        if (reason.Length > 0) declinedReason = reason;

        reason = declinedReason;
        return false;
    }

    private static int ColumnIndex(CsvTable table, string name)
    {
        if (table.Header == null) return -1;
        for (var i = 0; i < table.Header.Count; i++)
            if (string.Equals(table.Header[i].Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string UnquoteToken(string token)
    {
        var t = token.Trim();
        if (t.Length >= 2 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\'')))
            return t[1..^1];
        return t;
    }

    // "add (a|an|the) column (called|named) X" / "add a X column" / "add column X with value V"
    private static readonly Regex AddColumnRegex = new(
        @"\badd\b[^,;]*?\bcolumn\b\s+(?:called\s+|named\s+)?['""]?(?<name>[A-Za-z_][A-Za-z0-9_\- ]*?)['""]?(?:\s+with\s+(?:value|default)\s+['""]?(?<value>[^""',;]+)['""]?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AddColumnAdjRegex = new(
        @"\badd\b\s+(?:a|an|the)\s+['""]?(?<name>[A-Za-z_][A-Za-z0-9_\- ]*?)['""]?\s+column\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryAddColumn(CsvTable table, string desc, out string reason)
    {
        reason = "";
        string? name = null, defaultValue = null;
        var m = AddColumnRegex.Match(desc);
        if (m.Success)
        {
            name = m.Groups["name"].Value.Trim();
            if (m.Groups["value"].Success) defaultValue = m.Groups["value"].Value.Trim();
        }
        else
        {
            var m2 = AddColumnAdjRegex.Match(desc);
            if (m2.Success) name = m2.Groups["name"].Value.Trim();
        }
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = UnquoteToken(name);

        table.Header ??= new List<string>();
        table.Header.Add(name);
        foreach (var row in table.Rows)
            row.Add(defaultValue ?? "");
        reason = $"added column '{name}'";
        return true;
    }

    // "remove|delete|drop (the) column X" / "remove the X column"
    private static readonly Regex RemoveColumnRegex = new(
        @"\b(?:remove|delete|drop)\b\s+(?:the\s+)?column\s+(?:called\s+|named\s+)?['""]?(?<name>[A-Za-z_][A-Za-z0-9_\- ]*?)['""]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RemoveColumnAdjRegex = new(
        @"\b(?:remove|delete|drop)\b\s+the\s+['""]?(?<name>[A-Za-z_][A-Za-z0-9_\- ]*?)['""]?\s+column\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryRemoveColumn(CsvTable table, string desc, out string reason)
    {
        reason = "";
        string? name = null;
        var m = RemoveColumnRegex.Match(desc);
        if (m.Success) name = m.Groups["name"].Value.Trim();
        else
        {
            var m2 = RemoveColumnAdjRegex.Match(desc);
            if (m2.Success) name = m2.Groups["name"].Value.Trim();
        }
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = UnquoteToken(name);

        var idx = ColumnIndex(table, name);
        if (idx < 0) { reason = $"column '{name}' not found"; return false; }
        table.Header!.RemoveAt(idx);
        foreach (var row in table.Rows)
            if (idx < row.Count) row.RemoveAt(idx);
        reason = $"removed column '{name}'";
        return true;
    }

    // "rename|change (the) column A to|into B"
    private static readonly Regex RenameColumnRegex = new(
        @"\b(?:rename|change)\b\s+(?:the\s+)?column\s+['""]?(?<from>[A-Za-z_][A-Za-z0-9_\- ]*?)['""]?\s+(?:to|into|as)\s+['""]?(?<to>[A-Za-z_][A-Za-z0-9_\- ]*?)['""]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryRenameColumn(CsvTable table, string desc, out string reason)
    {
        reason = "";
        var m = RenameColumnRegex.Match(desc);
        if (!m.Success) return false;
        var from = UnquoteToken(m.Groups["from"].Value.Trim());
        var to = UnquoteToken(m.Groups["to"].Value.Trim());
        var idx = ColumnIndex(table, from);
        if (idx < 0) { reason = $"column '{from}' not found"; return false; }
        table.Header![idx] = to;
        reason = $"renamed column '{from}' to '{to}'";
        return true;
    }

    // "add|append|insert (a|an|the|new) row ..." with key=value / key: value pairs
    private static readonly Regex AddRowVerbRegex = new(
        @"\b(?:add|append|insert)\b\s+(?:a|an|the|new)?\s*row\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PairRegex = new(
        @"(?<key>[A-Za-z_][A-Za-z0-9_\- ]*?)\s*[:=]\s*(?<value>""[^""]*""|'[^']*'|[^,;]+?)(?=\s*(?:,|;|$))",
        RegexOptions.Compiled);

    private static bool TryAddRow(CsvTable table, string desc, out string reason)
    {
        reason = "";
        var m = AddRowVerbRegex.Match(desc);
        if (!m.Success) return false;
        var tail = desc[(m.Index + m.Length)..].Trim();
        tail = Regex.Replace(tail, @"^(?:with|having|containing|that\s+has)\b\s*", "", RegexOptions.IgnoreCase);
        tail = tail.Trim().TrimStart(':', ',', ';', '-', ' ');
        var pairs = new List<(string key, string value)>();
        foreach (Match p in PairRegex.Matches(tail))
        {
            var key = p.Groups["key"].Value.Trim();
            var value = UnquoteToken(p.Groups["value"].Value.Trim());
            if (key.Length == 0) continue;
            pairs.Add((key, value));
        }
        if (pairs.Count == 0) return false;

        var row = new List<string>();
        var header = table.Header ?? new List<string>();
        // Ensure a header exists so row columns have names.
        if (table.Header == null)
        {
            if (table.Rows.Count > 0)
            {
                table.Header = table.Rows[0];
                table.Rows.RemoveAt(0);
            }
            else
            {
                table.Header = header;
            }
        }
        for (var i = 0; i < table.Header!.Count; i++)
        {
            var cell = "";
            foreach (var (key, value) in pairs)
                if (string.Equals(key, table.Header[i].Trim(), StringComparison.OrdinalIgnoreCase)) { cell = value; break; }
            row.Add(cell);
        }
        // Columns named in the pairs but absent from the header extend it (and existing rows).
        foreach (var (key, value) in pairs)
        {
            if (ColumnIndex(table, key) >= 0) continue;
            table.Header.Add(key);
            foreach (var existing in table.Rows)
                existing.Add("");
            row.Add(value);
        }
        table.Rows.Add(row);
        reason = $"added a row ({pairs.Count} field(s))";
        return true;
    }

    // "delete|remove (the|all) row(s) where COL (is|=|==|equals|contains|matches) VALUE"
    private static readonly Regex DeleteRowsRegex = new(
        @"\b(?:delete|remove)\b\s+(?:the\s+|all\s+)?rows?\s+(?:where|when|if|that)\s+['""]?(?<col>[A-Za-z_][A-Za-z0-9_\-]*)['""]?\s*(?:(?<op>is|=|==|equals|contains|matches)\s*)?['""]?(?<value>.+?)['""]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryDeleteRows(CsvTable table, string desc, out string reason)
    {
        reason = "";
        var m = DeleteRowsRegex.Match(desc);
        if (!m.Success) return false;
        var col = m.Groups["col"].Value.Trim();
        var value = UnquoteToken(m.Groups["value"].Value.Trim());
        var idx = ColumnIndex(table, col);
        if (idx < 0) { reason = $"column '{col}' not found"; return false; }
        var op = (m.Groups["op"].Success ? m.Groups["op"].Value : "").Trim();
        var contains = op.Equals("contains", StringComparison.OrdinalIgnoreCase) ||
                       op.Equals("matches", StringComparison.OrdinalIgnoreCase);
        var removed = table.Rows.RemoveAll(row =>
            idx < row.Count && (contains
                ? row[idx].IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(row[idx].Trim(), value, StringComparison.OrdinalIgnoreCase)));
        var opWord = contains ? "contains" : "is";
        reason = $"removed {removed} row(s) where {col} {opWord} '{value}'";
        return removed > 0;
    }

    // "set|update|change COL to|as VALUE where KEYCOL (is|=) KEY"
    private static readonly Regex SetCellRegex = new(
        @"\b(?:set|update|change)\b\s+(?:the\s+)?['""]?(?<col>[A-Za-z_][A-Za-z0-9_\-]*)['""]?\s+(?:to|as|=)\s*(?<value>.+?)\s+(?:where|when|if|for)\s+['""]?(?<keycol>[A-Za-z_][A-Za-z0-9_\-]*)['""]?\s*(?:(?:is|=|==|equals)\s*)?['""]?(?<key>.+?)['""]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TrySetCell(CsvTable table, string desc, out string reason)
    {
        reason = "";
        var m = SetCellRegex.Match(desc);
        if (!m.Success) return false;
        var col = m.Groups["col"].Value.Trim();
        var value = UnquoteToken(m.Groups["value"].Value.Trim());
        var keyCol = m.Groups["keycol"].Value.Trim();
        var key = UnquoteToken(m.Groups["key"].Value.Trim());

        var colIdx = ColumnIndex(table, col);
        var keyIdx = ColumnIndex(table, keyCol);
        if (colIdx < 0 || keyIdx < 0)
        {
            var missing = colIdx < 0 ? col : keyCol;
            reason = $"column '{missing}' not found";
            return false;
        }
        var updated = 0;
        foreach (var row in table.Rows)
        {
            if (keyIdx < row.Count && string.Equals(row[keyIdx].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                while (row.Count <= colIdx) row.Add("");
                row[colIdx] = value;
                updated++;
            }
        }
        reason = $"set {col} to '{value}' in {updated} row(s) where {keyCol} is '{key}'";
        return updated > 0;
    }

    // "change|rename|replace (all|every) A to|with B in|within (the) column C"
    private static readonly Regex RenameValuesRegex = new(
        @"\b(?:change|rename|replace)\b\s+(?:all\s+|every\s+)?['""]?(?<from>[A-Za-z0-9_\- .]+?)['""]?\s+(?:to|with)\s+['""]?(?<to>[A-Za-z0-9_\- .]+?)['""]?\s+(?:in|within)\s+(?:the\s+)?column\s+['""]?(?<col>[A-Za-z_][A-Za-z0-9_\- ]*?)['""]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryRenameValues(CsvTable table, string desc, out string reason)
    {
        reason = "";
        var m = RenameValuesRegex.Match(desc);
        if (!m.Success) return false;
        var from = UnquoteToken(m.Groups["from"].Value.Trim());
        var to = UnquoteToken(m.Groups["to"].Value.Trim());
        var col = m.Groups["col"].Value.Trim();
        var idx = ColumnIndex(table, col);
        if (idx < 0) { reason = $"column '{col}' not found"; return false; }
        var changed = 0;
        foreach (var row in table.Rows)
        {
            if (idx < row.Count && string.Equals(row[idx].Trim(), from, StringComparison.OrdinalIgnoreCase))
            {
                row[idx] = to;
                changed++;
            }
        }
        reason = $"replaced '{from}' with '{to}' in {changed} cell(s) of column '{col}'";
        return changed > 0;
    }

    // ── Whole-file convenience entry points ──────────────────────────────────

    /// <summary>Parses delimiter text, applies a recognized operation, and re-serializes.
    /// Returns false when the operation is unrecognized or produces no change.</summary>
    public static bool TryEditDelimited(string text, char delimiter, string changeDescription,
        out string? newText, out string reason)
    {
        newText = null;
        reason = "";
        var table = ParseDelimited(text, delimiter);
        if (!TryApplyEdit(table, changeDescription, out reason)) return false;
        newText = SerializeDelimited(table, delimiter);
        return true;
    }

    /// <summary>Decodes an .xlsx workbook, applies a recognized operation, and re-encodes.
    /// Returns false when the operation is unrecognized or produces no change.</summary>
    public static bool TryEditXlsx(byte[] bytes, string changeDescription,
        out byte[]? newBytes, out string reason)
    {
        newBytes = null;
        reason = "";
        CsvTable table;
        try { table = ParseXlsx(bytes); }
        catch { return false; }
        if (!TryApplyEdit(table, changeDescription, out reason)) return false;
        newBytes = SerializeXlsx(table);
        return true;
    }
}
