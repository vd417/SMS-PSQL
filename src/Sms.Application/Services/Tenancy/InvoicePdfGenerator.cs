using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Sms.Modules.Tenancy.Contracts;

namespace Sms.Application.Services.Tenancy;

public sealed record InvoicePdfModel(
    Guid InvoiceId,
    string SchoolName,
    string? SchoolSlug,
    string? Country,
    string? OwnerName,
    string? OwnerEmail,
    string? OwnerPhone,
    string? Address,
    string? Csm,
    string ClientStatus,
    string? PlanName,
    string? PlanTier,
    string? PlanPricing,
    string? PlanPeriod,
    string? PlanBand,
    string? PlanDescription,
    decimal? PlanPrice,
    decimal? PerStudentRate,
    int? MinStudents,
    int BillableStudents,
    int StudentsCount,
    int StaffCount,
    decimal StorageGb,
    int? LimitStudents,
    int? LimitStaff,
    int? LimitStorageGb,
    IReadOnlyList<string> PlanFeatures,
    decimal Amount,
    string Status,
    DateTime Issued,
    DateTime Due,
    DateTime? PeriodStart,
    DateTime? PeriodEnd,
    int? Seats,
    string? LineDetail);

public interface IInvoicePdfGenerator
{
    byte[] Generate(InvoicePdfModel model);
}

public sealed class InvoicePdfGenerator : IInvoicePdfGenerator
{
    static InvoicePdfGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Generate(InvoicePdfModel model)
    {
        var enIn = CultureInfo.GetCultureInfo("en-IN");
        var amount = model.Amount.ToString("N2", enIn);
        var issued = model.Issued.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        var due = model.Due.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        var invNo = model.InvoiceId.ToString("N")[..8].ToUpperInvariant();
        var period = model.PeriodStart is { } ps && model.PeriodEnd is { } pe
            ? $"{ps:dd MMM yyyy} – {pe:dd MMM yyyy}"
            : "—";

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(9.5f).FontColor(Colors.Grey.Darken3));

                page.Header().Column(col =>
                {
                    col.Item().Text("CATRE TECHNOLOGY").Bold().FontSize(18).FontColor(Colors.Blue.Darken2);
                    col.Item().Text("School Management SaaS · Tax Invoice / Bill").FontSize(9).FontColor(Colors.Grey.Medium);
                    col.Item().PaddingTop(6).LineHorizontal(1.5f).LineColor(Colors.Blue.Darken2);
                });

                page.Content().PaddingVertical(14).Column(col =>
                {
                    col.Spacing(10);

                    // Invoice meta + bill-to
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("BILL TO").SemiBold().FontSize(8).FontColor(Colors.Grey.Medium);
                            c.Item().Text(model.SchoolName).Bold().FontSize(13);
                            if (!string.IsNullOrWhiteSpace(model.SchoolSlug))
                                c.Item().Text($"Slug: {model.SchoolSlug}");
                            if (!string.IsNullOrWhiteSpace(model.Country))
                                c.Item().Text(model.Country!);
                            if (!string.IsNullOrWhiteSpace(model.Address))
                                c.Item().Text(model.Address!);
                            c.Item().PaddingTop(6).Text("SCHOOL OWNER").SemiBold().FontSize(8).FontColor(Colors.Grey.Medium);
                            c.Item().Text(string.IsNullOrWhiteSpace(model.OwnerName) ? "—" : model.OwnerName!).Bold();
                            if (!string.IsNullOrWhiteSpace(model.OwnerEmail))
                                c.Item().Text(model.OwnerEmail!);
                            if (!string.IsNullOrWhiteSpace(model.OwnerPhone))
                                c.Item().Text(model.OwnerPhone!);
                            if (!string.IsNullOrWhiteSpace(model.Csm))
                                c.Item().Text($"CSM: {model.Csm}");
                        });
                        row.ConstantItem(200).Column(c =>
                        {
                            c.Item().AlignRight().Text($"Invoice #{invNo}").Bold().FontSize(12);
                            c.Item().AlignRight().Text($"Invoice status: {model.Status}");
                            c.Item().AlignRight().Text($"Client status: {model.ClientStatus}");
                            c.Item().AlignRight().Text($"Issued: {issued}");
                            c.Item().AlignRight().Text($"Due: {due}");
                            c.Item().AlignRight().Text($"Period: {period}");
                            if (model.Seats is int seats)
                                c.Item().AlignRight().Text($"Seats: {seats}");
                        });
                    });

                    // Plan details
                    col.Item().Background(Colors.Grey.Lighten4).Padding(10).Column(c =>
                    {
                        c.Item().Text("PLAN DETAILS").SemiBold().FontSize(8).FontColor(Colors.Grey.Medium);
                        c.Item().PaddingTop(2).Text(model.PlanName ?? "—").Bold().FontSize(12);
                        c.Item().Text(
                            $"Tier: {model.PlanTier ?? "—"}  ·  Pricing: {model.PlanPricing ?? "—"}  ·  Period: {model.PlanPeriod ?? "month"}");
                        if (!string.IsNullOrWhiteSpace(model.PlanBand))
                            c.Item().Text($"Band: {model.PlanBand}");
                        if (!string.IsNullOrWhiteSpace(model.PlanDescription))
                            c.Item().Text(model.PlanDescription!);
                        if (string.Equals(model.PlanPricing, "per_student", StringComparison.OrdinalIgnoreCase))
                        {
                            c.Item().PaddingTop(4).Text(
                                $"Per-student rate: ₹{(model.PerStudentRate ?? 0).ToString("N2", enIn)}  ·  " +
                                $"Min students: {model.MinStudents ?? 0}  ·  Billable students: {model.BillableStudents}");
                        }
                        else
                        {
                            c.Item().PaddingTop(4).Text(
                                $"Flat price: ₹{(model.PlanPrice ?? 0).ToString("N2", enIn)} / {model.PlanPeriod ?? "month"}");
                        }
                    });

                    // Usage / limits
                    col.Item().Text("USAGE & LIMITS").SemiBold().FontSize(8).FontColor(Colors.Grey.Medium);
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                        });
                        void Cell(string label, string value)
                        {
                            table.Cell().Border(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(6).Column(c =>
                            {
                                c.Item().Text(label).FontSize(8).FontColor(Colors.Grey.Medium);
                                c.Item().Text(value).Bold();
                            });
                        }
                        Cell("Students", $"{model.StudentsCount} / {(model.LimitStudents?.ToString() ?? "—")}");
                        Cell("Staff", $"{model.StaffCount} / {(model.LimitStaff?.ToString() ?? "—")}");
                        Cell("Storage", $"{model.StorageGb:0.##} GB / {(model.LimitStorageGb is int ls ? $"{ls} GB" : "—")}");
                    });

                    // Features
                    if (model.PlanFeatures.Count > 0)
                    {
                        col.Item().PaddingTop(4).Text("INCLUDED FEATURES").SemiBold().FontSize(8).FontColor(Colors.Grey.Medium);
                        col.Item().Text(string.Join("  ·  ", model.PlanFeatures));
                    }

                    // Line items
                    col.Item().PaddingTop(6).Text("CHARGES").SemiBold().FontSize(8).FontColor(Colors.Grey.Medium);
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(5);
                            columns.RelativeColumn(2);
                            columns.RelativeColumn(2);
                        });
                        table.Header(header =>
                        {
                            header.Cell().Background(Colors.Blue.Darken2).Padding(6)
                                .Text("Description").FontColor(Colors.White).SemiBold();
                            header.Cell().Background(Colors.Blue.Darken2).Padding(6)
                                .AlignRight().Text("Qty / rate").FontColor(Colors.White).SemiBold();
                            header.Cell().Background(Colors.Blue.Darken2).Padding(6)
                                .AlignRight().Text("Amount (₹)").FontColor(Colors.White).SemiBold();
                        });

                        var qtyRate = string.Equals(model.PlanPricing, "per_student", StringComparison.OrdinalIgnoreCase)
                            ? $"{model.BillableStudents} × ₹{(model.PerStudentRate ?? 0).ToString("N2", enIn)}"
                            : $"1 × ₹{(model.PlanPrice ?? model.Amount).ToString("N2", enIn)}";

                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(6)
                            .Text(model.LineDetail
                                  ?? $"Catre {model.PlanName ?? "plan"} subscription — {model.PlanPeriod ?? "monthly"}");
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(6)
                            .AlignRight().Text(qtyRate);
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(6)
                            .AlignRight().Text(amount);
                    });

                    col.Item().AlignRight().PaddingTop(6).Column(c =>
                    {
                        c.Item().Text($"Subtotal: ₹{amount}");
                        c.Item().Text($"Total due: ₹{amount}").Bold().FontSize(13);
                    });

                    col.Item().PaddingTop(16).Text("PAYMENT NOTES").SemiBold().FontSize(8).FontColor(Colors.Grey.Medium);
                    col.Item().Text(
                        "Please arrange payment before the due date. This invoice covers the Catre Technology platform subscription for the school named above. " +
                        "For billing queries, contact Catre Technology support.")
                        .FontSize(8.5f);
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Catre Technology · catre.tech").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.Span("  ·  Page ").FontSize(8).FontColor(Colors.Grey.Medium);
                    t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        }).GeneratePdf();
    }

    public static InvoicePdfModel From(
        InvoiceResponse invoice,
        ClientRow client,
        PlanRow? plan = null,
        SubscriptionResponse? subscription = null)
    {
        var seats = subscription?.Seats
            ?? (plan is null ? Math.Max(client.StudentsCount, 1)
                : CatreMappers.BillableSeats(plan, client.StudentsCount, fallbackSeats: 0));
        var billable = plan is null
            ? seats
            : CatreMappers.BillableSeats(plan, client.StudentsCount, seats);
        var features = string.IsNullOrWhiteSpace(plan?.FeaturesCsv)
            ? Array.Empty<string>()
            : plan!.FeaturesCsv!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? lineDetail = null;
        if (plan is not null && string.Equals(plan.Pricing, "per_student", StringComparison.OrdinalIgnoreCase))
        {
            lineDetail =
                $"Catre {plan.Name} subscription — {billable} billable students × ₹{(plan.PerStudent ?? 0).ToString("0.##", CultureInfo.InvariantCulture)}/student / {plan.Period}";
        }
        else if (plan is not null)
        {
            lineDetail = $"Catre {plan.Name} subscription — flat {plan.Period} plan";
        }

        return new InvoicePdfModel(
            invoice.Id,
            invoice.TenantName ?? client.Name,
            client.Slug,
            client.Country,
            client.ContactName,
            client.ContactEmail,
            client.ContactPhone,
            client.Address,
            client.Csm,
            client.Status,
            invoice.PlanName ?? client.PlanName ?? plan?.Name,
            client.Tier ?? plan?.Tier,
            plan?.Pricing,
            plan?.Period,
            plan?.Band,
            plan?.Description,
            plan?.Price,
            plan?.PerStudent,
            plan?.MinStudents,
            billable,
            client.StudentsCount,
            client.StaffCount,
            client.StorageGb,
            client.LimitsStudents ?? plan?.LimitsStudents,
            client.LimitsStaff ?? plan?.LimitsStaff,
            client.LimitsStorageGb ?? plan?.LimitsStorageGb,
            features,
            invoice.Amount,
            invoice.Status,
            invoice.Issued,
            invoice.Due,
            subscription?.CurrentPeriodStart ?? invoice.Issued,
            subscription?.CurrentPeriodEnd ?? invoice.Due,
            seats,
            lineDetail);
    }
}
