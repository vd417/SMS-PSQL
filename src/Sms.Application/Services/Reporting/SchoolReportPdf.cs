using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Sms.Application.Services.Reporting;

public static class SchoolReportPdf
{
    static SchoolReportPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Build(
        string schoolName, string title, string fromDate, string toDate, string filters,
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var shown = rows.Take(800).ToList();
        var wide = columns.Count > 10;
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(wide ? PageSizes.A3.Landscape() : PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken3));
                page.Header().Column(col =>
                {
                    col.Item().Text(string.IsNullOrWhiteSpace(schoolName) ? "School" : schoolName).Bold().FontSize(16).FontColor(Colors.BlueGrey.Darken4);
                    col.Item().Text(title).Bold().FontSize(12);
                    col.Item().Text($"Date range: {fromDate} to {toDate}").FontSize(9);
                    col.Item().Text($"Filters: {filters}").FontSize(9);
                    col.Item().PaddingBottom(8).Text($"Rows: {rows.Count}" + (rows.Count > shown.Count ? $" · PDF shows the first {shown.Count}" : "")).FontSize(9);
                });
                page.Content().Table(table =>
                {
                    table.ColumnsDefinition(def =>
                    {
                        foreach (var _ in columns) def.RelativeColumn();
                    });
                    table.Header(header =>
                    {
                        foreach (var name in columns)
                            header.Cell().Background("#1F4E79").Padding(3).Text(name).FontColor(Colors.White).Bold().FontSize(7);
                    });
                    foreach (var row in shown)
                    {
                        for (var i = 0; i < columns.Count; i++)
                        {
                            var text = i < row.Count ? row[i] : "";
                            if (text.Length > 80) text = text[..77] + "...";
                            table.Cell().BorderBottom(0.4f).BorderColor(Colors.Grey.Lighten2).Padding(2).Text(text).FontSize(7);
                        }
                    }
                });
            });
        }).GeneratePdf();
    }
}
