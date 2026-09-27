using System.Globalization;
using Sms.Modules.Tenancy.Contracts;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Auth;

namespace Sms.Application.Services.Tenancy;

/// The Catre billing emails a school's contact receives, queued on the shared email queue with the
/// invoice PDF attached. One home for both templates so every flow that activates or bills a school
/// (status activation, upgrade approval, "email invoice") sends exactly the same message.
public sealed class ClientBillingEmails(
    SubscriptionRepository subscriptions,
    IEmailQueue emailQueue,
    IInvoicePdfGenerator invoicePdf)
{
    /// Renders the invoice PDF the way every Catre surface shows it (download, email).
    public async Task<(byte[] Pdf, string FileName)> RenderInvoicePdfAsync(
        InvoiceResponse invoice, ClientRow client, PlanRow? plan, CancellationToken ct = default)
    {
        var subs = await subscriptions.ListAsync(client.Id, "active", ct);
        var pdf = invoicePdf.Generate(InvoicePdfGenerator.From(invoice, client, plan, subs.FirstOrDefault()));
        return (pdf, $"Catre-Invoice-{invoice.Id:N}.pdf");
    }

    /// "&lt;School&gt; is now an active Catre client" with the invoice attached. Sent when a school
    /// becomes active. Returns false (nothing queued) when the school has no contact email.
    public async Task<bool> QueueActivationInvoiceAsync(
        ClientRow client, InvoiceResponse invoice, PlanRow plan, int seats, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(client.ContactEmail))
            return false;

        var amountText = invoice.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var due = invoice.Due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var billableNote = string.Equals(plan.Pricing, "per_student", StringComparison.OrdinalIgnoreCase)
            ? $" ({seats} students × ₹{(plan.PerStudent ?? 0).ToString("0.##", CultureInfo.InvariantCulture)})"
            : "";
        var (pdf, fileName) = await RenderInvoicePdfAsync(invoice, client, plan, ct);
        var body =
            $"Hello{(string.IsNullOrWhiteSpace(client.ContactName) ? "" : " " + client.ContactName)},\n\n" +
            $"{client.Name} is now an active Catre client on the {client.PlanName ?? plan.Name} plan.\n\n" +
            $"Invoice amount: ₹{amountText}{billableNote}\n" +
            $"Due date: {due}\n" +
            $"Status: {invoice.Status}\n\n" +
            "Please find the full invoice PDF attached (plan, students, usage & charges).\n\n" +
            "— Catre Technology";
        emailQueue.Enqueue(new EmailMessage(
            client.ContactEmail.Trim(),
            $"Invoice for {client.Name} — ₹{amountText}",
            body,
            pdf,
            fileName,
            "application/pdf"));
        return true;
    }

    /// The Catre invoice with its current status (a paid invoice reads as the payment receipt).
    /// Returns false (nothing queued) when the school has no contact email.
    public async Task<bool> QueueInvoiceAsync(
        ClientRow client, InvoiceResponse invoice, PlanRow? plan, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(client.ContactEmail))
            return false;

        var (pdf, fileName) = await RenderInvoicePdfAsync(invoice, client, plan, ct);
        var amountText = invoice.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var due = invoice.Due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var owner = string.IsNullOrWhiteSpace(client.ContactName) ? "" : " " + client.ContactName;
        var body =
            $"Hello{owner},\n\n" +
            $"Please find attached the Catre Technology invoice for {client.Name}.\n\n" +
            $"School: {client.Name}\n" +
            $"Plan: {invoice.PlanName ?? client.PlanName}\n" +
            $"Students: {client.StudentsCount}\n" +
            $"Amount: ₹{amountText}\n" +
            $"Due: {due}\n" +
            $"Status: {invoice.Status}\n\n" +
            "Full plan, usage and billing details are in the PDF.\n\n" +
            "— Catre Technology";
        emailQueue.Enqueue(new EmailMessage(
            client.ContactEmail.Trim(),
            $"Catre Invoice — {client.Name} — ₹{amountText}",
            body,
            pdf,
            fileName,
            "application/pdf"));
        return true;
    }
}
