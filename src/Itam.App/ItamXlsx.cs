using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Itam.App;

/// <summary>A small Excel (.xlsx) writer for the report exports: one sheet, a bold tinted heading row that stays in view,
/// filters on every column, numbers as numbers and dates as dd/MM/yyyy. No external package.</summary>
public static class ItamXlsx
{
    public static byte[] Build(string sheet, string[] headers, IEnumerable<object?[]> rows)
    {
        var data = rows.ToList();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string path, string xml) { using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)); w.Write(xml); }
            Add("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Add("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            var name = SecurityElement.Escape(new string(sheet.Where(ch => !"[]:*?/\\".Contains(ch)).Take(31).ToArray()));
            Add("xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{name}" sheetId="1" r:id="rId1"/></sheets><definedNames><definedName name="_xlnm._FilterDatabase" localSheetId="0" hidden="1">'{name}'!$A$1:${Col(headers.Length - 1)}${data.Count + 1}</definedName></definedNames></workbook>""");
            Add("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Add("xl/styles.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><color rgb="FF0F2F6B"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEEF3FC"/></patternFill></fill></fills><borders count="2"><border/><border><bottom style="thin"><color rgb="FFB8C7E6"/></bottom></border></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="4"><xf/><xf fontId="1" fillId="2" borderId="1" applyFont="1" applyFill="1" applyBorder="1"/><xf numFmtId="3" applyNumberFormat="1"/><xf numFmtId="4" applyNumberFormat="1"/></cellXfs></styleSheet>""");
            var sb = new StringBuilder();
            sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><cols>""");
            for (var i = 0; i < headers.Length; i++)
            {
                var width = Math.Min(60, Math.Max(headers[i].Length, data.Select(r => i < r.Length ? Text(r[i]).Length : 0).DefaultIfEmpty(0).Max()) + 3);
                sb.Append($"""<col min="{i + 1}" max="{i + 1}" width="{width}" customWidth="1"/>""");
            }
            sb.Append("</cols><sheetData>");
            void Row(int n, IReadOnlyList<object?> cells, bool head)
            {
                sb.Append($"""<row r="{n}">""");
                for (var i = 0; i < cells.Count; i++)
                {
                    var at = Col(i) + n; var v = cells[i];
                    if (!head && v is int or long or decimal or double)
                        sb.Append($"""<c r="{at}" s="{(v is decimal or double ? 3 : 2)}"><v>{Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}</v></c>""");
                    else if (Text(v) is { Length: > 0 } t)
                        sb.Append($"""<c r="{at}" t="inlineStr"{(head ? " s=\"1\"" : "")}><is><t xml:space="preserve">{SecurityElement.Escape(Clean(t))}</t></is></c>""");
                }
                sb.Append("</row>");
            }
            Row(1, headers, true);
            for (var i = 0; i < data.Count; i++) Row(i + 2, data[i], false);
            sb.Append($"""</sheetData><autoFilter ref="A1:{Col(headers.Length - 1)}{data.Count + 1}"/></worksheet>""");
            Add("xl/worksheets/sheet1.xml", sb.ToString());
        }
        return ms.ToArray();
    }

    static string Col(int i) { var s = ""; i++; while (i > 0) { var m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; } return s; }
    static string Text(object? v) => v switch
    {
        null => "", DateOnly d => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
        DateTime t => t.AddHours(3).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), bool b => b ? "Yes" : "No",
        decimal m => m.ToString("0.##", CultureInfo.InvariantCulture), _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };
    // Characters Excel refuses in XML are dropped. Every text is an inline string, so it is never read as a formula.
    static string Clean(string t) => new(t.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ').ToArray());
}
