using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Sms.Application.Services.Reporting;

public static class SchoolReportWorkbook
{
    private static readonly string[] TotalHeaders =
    [
        "Annual Fee", "Paid Fee", "Pending Fee", "Total Fee", "Discount", "Late Fee",
        "Paid Amount", "Pending Amount", "Total Payable", "Total Paid", "Outstanding Amount",
        "Transport Fee", "Maximum Marks", "Obtained Marks", "Working Hours", "Weekly Periods",
        "Students Assigned",
    ];

    public static byte[] Build(
        string schoolName, string title, string fromDate, string toDate, string? fromTime, string? toTime,
        string filters, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var range = $"{fromDate} to {toDate}";
        if (!string.IsNullOrWhiteSpace(fromTime) || !string.IsNullOrWhiteSpace(toTime))
            range += $" · {fromTime ?? "00:00"}–{toTime ?? "23:59"}";

        var sheet = new StringBuilder();
        sheet.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sheet.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        WriteRow(sheet, 1, [schoolName], 1);
        WriteRow(sheet, 2, [title], 1);
        WriteRow(sheet, 3, [$"Date range: {range}"]);
        WriteRow(sheet, 4, [$"Filters: {filters}"]);
        WriteRow(sheet, 5, [$"Rows: {rows.Count.ToString(CultureInfo.InvariantCulture)}"]);
        WriteRow(sheet, 6, columns, 2);
        var r = 7;
        foreach (var row in rows)
        {
            WriteRow(sheet, r++, row);
        }
        var totals = TotalRow(columns, rows);
        if (totals is not null) WriteRow(sheet, r, totals, 1);
        sheet.Append("</sheetData></worksheet>");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Put(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            Put(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Put(zip, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Report" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Put(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            Put(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts count="2">
                    <font><sz val="11"/><name val="Calibri"/></font>
                    <font><b/><sz val="11"/><name val="Calibri"/></font>
                  </fonts>
                  <fills count="3">
                    <fill><patternFill patternType="none"/></fill>
                    <fill><patternFill patternType="gray125"/></fill>
                    <fill><patternFill patternType="solid"><fgColor rgb="FF1F4E79"/></patternFill></fill>
                  </fills>
                  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="3">
                    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                    <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
                    <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/>
                  </cellXfs>
                </styleSheet>
                """);
            Put(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return ms.ToArray();
    }

    private static IReadOnlyList<string>? TotalRow(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var indexes = new List<int>();
        for (var i = 0; i < columns.Count; i++)
            if (TotalHeaders.Any(h => string.Equals(h, columns[i], StringComparison.OrdinalIgnoreCase)))
                indexes.Add(i);
        if (indexes.Count == 0 || rows.Count == 0) return null;
        var cells = new string[columns.Count];
        cells[0] = "Total";
        foreach (var i in indexes)
        {
            decimal sum = 0;
            foreach (var row in rows)
                if (i < row.Count && decimal.TryParse(row[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                    sum += n;
            cells[i] = sum.ToString("0.##", CultureInfo.InvariantCulture);
        }
        return cells;
    }

    private static void WriteRow(StringBuilder sheet, int index, IReadOnlyList<string> cells, int style = 0)
    {
        sheet.Append("<row r=\"").Append(index).Append("\">");
        for (var i = 0; i < cells.Count; i++)
        {
            var styleAttr = style > 0 ? $" s=\"{style}\"" : "";
            sheet.Append("<c r=\"").Append(ColumnName(i)).Append(index).Append('"').Append(styleAttr)
                .Append(" t=\"inlineStr\"><is><t>")
                .Append(Escape(cells[i]))
                .Append("</t></is></c>");
        }
        sheet.Append("</row>");
    }

    private static string ColumnName(int index)
    {
        var name = "";
        var n = index + 1;
        while (n > 0)
        {
            n--;
            name = (char)('A' + (n % 26)) + name;
            n /= 26;
        }
        return name;
    }

    private static string Escape(string? value) =>
        (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static void Put(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text.Trim());
    }
}
