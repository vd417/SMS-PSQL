using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Academics.Data;

public sealed class LibraryRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<IReadOnlyList<LibraryBookResponse>> ListAsync(DateTime today, CancellationToken ct = default) =>
        QueryInlineAsync<LibraryBookResponse>(
            @"SELECT ""Id"", ""TenantId"", ""Title"", ""Author"", ""Subject"", ""IssuedTo"", ""DueDate"",
                     CASE WHEN ""Status"" = 'issued' AND ""DueDate"" IS NOT NULL AND ""DueDate"" < @today
                          THEN 'overdue' ELSE ""Status"" END AS ""Status""
              FROM ""dbo"".""LibraryBooks"" ORDER BY ""Title""", new { today = today.Date }, ct);

    /// Catalogue = total books, Members = distinct active borrowers, Issued = currently out,
    /// FinesDue = overdue days × per-day rate summed across still-issued overdue books.
    public async Task<LibrarySummaryResponse> SummaryAsync(DateTime today, decimal finePerDay, CancellationToken ct = default) =>
        (await QueryInlineAsync<LibrarySummaryResponse>(
            @"SELECT
                CAST((SELECT COUNT(*) FROM ""dbo"".""LibraryBooks"") AS int) AS ""Catalogue"",
                CAST((SELECT COUNT(DISTINCT ""IssuedTo"") FROM ""dbo"".""LibraryBooks""
                   WHERE ""IssuedTo"" IS NOT NULL AND trim(""IssuedTo"") <> '') AS int) AS ""Members"",
                CAST((SELECT COUNT(*) FROM ""dbo"".""LibraryBooks"" WHERE ""Status"" = 'issued') AS int) AS ""Issued"",
                CAST(COALESCE((SELECT SUM(@today::date - ""DueDate"")
                   FROM ""dbo"".""LibraryBooks""
                   WHERE ""Status"" = 'issued' AND ""DueDate"" IS NOT NULL AND ""DueDate"" < @today), 0)
                   AS decimal(18,2)) * @finePerDay AS ""FinesDue""",
            new { today = today.Date, finePerDay }, ct)).First();

    public Task<LibraryBookResponse?> CreateAsync(Guid tenantId, CreateLibraryBookRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<LibraryBookResponse>("dbo.LibraryBook_Create", new
        {
            TenantId = tenantId, r.Title, r.Author, r.Subject, r.IssuedTo, DueDate = r.DueDate, Status = r.Status ?? "available"
        }, ct);
}
