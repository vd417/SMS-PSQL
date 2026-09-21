using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Academics.Data;

public sealed class AssignmentRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private sealed record Row(
        Guid Id, Guid TenantId, string Title, Guid? ClassId, string? ClassName, string? Subject,
        DateTime? DueDate, int SubmissionsCount, int TotalStudents, string RawStatus, string? Description, string? ImageUri,
        int? Period);

    public async Task<IReadOnlyList<AssignmentResponse>> ListAsync(DateTime today, CancellationToken ct = default)
    {
        var d = today.Date;
        var rows = await QueryInlineAsync<Row>(@"
SELECT a.""Id"", a.""TenantId"", a.""Title"", a.""ClassId"", a.""ClassName"", a.""Subject"", a.""DueDate"",
  CAST((SELECT COUNT(*) FROM ""dbo"".""Homework"" h WHERE h.""AssignmentId"" = a.""Id"" AND h.""Status"" IN ('done','submitted')) AS int) AS ""SubmissionsCount"",
  CAST(COALESCE((SELECT COUNT(*) FROM ""dbo"".""Students"" s JOIN ""dbo"".""Classes"" c
          ON c.""Id"" = a.""ClassId"" AND s.""Grade"" = c.""Grade"" AND s.""Section"" = c.""Section""), 0) AS int) AS ""TotalStudents"",
  a.""Status"" AS ""RawStatus"", a.""Description"", a.""ImageUri"", a.""Period""
FROM ""dbo"".""Assignments"" a ORDER BY a.""DueDate""", null, ct);

        return rows.Select(r => new AssignmentResponse(
            r.Id, r.TenantId, r.Title, r.ClassId, r.ClassName, r.Subject, r.DueDate,
            r.SubmissionsCount, r.TotalStudents, DeriveStatus(r.RawStatus, r.DueDate, d), r.Description, r.ImageUri, r.Period)).ToList();
    }

    public Task<AssignmentResponse?> CreateAsync(Guid tenantId, CreateAssignmentRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<AssignmentResponse>("dbo.Assignment_Create", new
        {
            TenantId = tenantId, r.Title, r.ClassId, r.ClassName, r.Subject, r.DueDate, r.Description, r.ImageUri, r.Period
        }, ct);

    public Task<AssignmentResponse?> UpdateAsync(Guid id, CreateAssignmentRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<AssignmentResponse>("dbo.Assignment_Update", new
        {
            Id = id, r.Title, r.ClassId, r.ClassName, r.Subject, r.DueDate, r.Description, r.ImageUri, r.Period
        }, ct);

    // RawStatus 'closed' wins; else overdue if past due; else due_soon within 3 days; else active.
    private static string DeriveStatus(string raw, DateTime? due, DateTime today)
    {
        if (string.Equals(raw, "closed", StringComparison.OrdinalIgnoreCase)) return "closed";
        if (due is not { } d) return "active";
        if (d.Date < today) return "overdue";
        if (d.Date <= today.AddDays(3)) return "due_soon";
        return "active";
    }
}
