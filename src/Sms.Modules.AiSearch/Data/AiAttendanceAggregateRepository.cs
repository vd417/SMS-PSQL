using Sms.Shared.Kernel.Data;

namespace Sms.Modules.AiSearch.Data;

public sealed record AttendanceAggregate(int Total, int Present, int Absent, decimal Pct);

/// <summary>
/// School-/class-wide daily attendance aggregate for AI search intents. A dedicated, self-contained
/// query reusing the exact live-attendance-pct join pattern already proven in
/// <c>StudentRepository.ColsWithLivePct</c> (present+late over marked periods).
/// </summary>
/// <remarks>
/// Column/literal names confirmed against the actual schema before writing this SQL:
/// <c>dbo.PeriodAttendanceRecords</c> has no <c>MarkedAt</c> column — the timestamp column is the
/// bracketed reserved word <c>[Date]</c> (see <c>M0128_PeriodAttendance_Tables.cs</c>, an <c>AsDate()</c>
/// column), and <c>dbo.Students.Status</c> uses the literal <c>'active'</c>
/// (see <c>M0011_Sis_Students.cs</c>, <c>WithDefaultValue("active")</c>, also used verbatim in
/// <c>ReportingRepository.cs</c> and <c>AcademicsRepositories.cs</c>).
/// </remarks>
public sealed class AiAttendanceAggregateRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string AggregateSelect = @"
SELECT
    CAST(COUNT(*) AS int) AS ""Total"",
    CAST(COALESCE(SUM(CASE WHEN att.""Positive"" > 0 THEN 1 ELSE 0 END), 0) AS int) AS ""Present"",
    CAST(COALESCE(SUM(CASE WHEN att.""Marked"" > 0 AND att.""Positive"" = 0 THEN 1 ELSE 0 END), 0) AS int) AS ""Absent"",
    CAST(CASE WHEN SUM(att.""Marked"") > 0
              THEN ROUND(100.0 * SUM(att.""Positive"") / NULLIF(SUM(att.""Marked""), 0), 2)
              ELSE 0 END AS decimal(5,2)) AS ""Pct""
FROM ""dbo"".""Students"" s
LEFT JOIN (
    SELECT par.""StudentId"", COUNT(*) AS ""Marked"",
           SUM(CASE WHEN par.""Status"" IN ('present', 'late') THEN 1 ELSE 0 END) AS ""Positive""
    FROM ""dbo"".""PeriodAttendanceRecords"" par
    WHERE par.""TenantId"" = @tenantId AND par.""Date"" = @date
    GROUP BY par.""StudentId""
) att ON att.""StudentId"" = s.""Id""
WHERE s.""TenantId"" = @tenantId AND s.""Status"" = 'active'";

    public async Task<AttendanceAggregate> SchoolWideAsync(Guid tenantId, DateOnly date, CancellationToken ct = default)
    {
        // Dapper (this repo's version) has no built-in DateOnly parameter support — same conversion
        // PeriodAttendanceQueryRepository.DateRangeParameters already uses for its date-range queries.
        var rows = await QueryInlineAsync<AttendanceAggregate>(
            AggregateSelect, new { tenantId, date = date.ToDateTime(TimeOnly.MinValue) }, ct);
        return rows.FirstOrDefault() ?? new AttendanceAggregate(0, 0, 0, 0);
    }

    public async Task<AttendanceAggregate> ForClassAsync(
        Guid tenantId, string className, string? section, DateOnly date, CancellationToken ct = default)
    {
        var sql = AggregateSelect + " AND s.\"ClassLabel\" = @className" +
                  (section is null ? "" : " AND s.\"Section\" = @section");
        var rows = await QueryInlineAsync<AttendanceAggregate>(
            sql, new { tenantId, date = date.ToDateTime(TimeOnly.MinValue), className, section }, ct);
        return rows.FirstOrDefault() ?? new AttendanceAggregate(0, 0, 0, 0);
    }

    /// <summary>
    /// The literal <c>Students.ClassLabel</c> values actually in use for this tenant (e.g.
    /// <c>"8-A"</c>, generated as Grade + '-' + Section). Callers (see
    /// <c>ClassAttendanceHandler</c>) resolve a free-text class filter like <c>"8A"</c> against this
    /// candidate set — using <c>StudentClassScope.LabelsMatch</c>, which this project cannot
    /// reference directly (Sms.Application depends on this module, not the reverse) — before calling
    /// <see cref="ForClassAsync"/> with the real stored label, so the exact-match SQL above keeps
    /// working unchanged once the caller has resolved the mismatch.
    /// </summary>
    public async Task<IReadOnlyList<string>> DistinctClassLabelsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<string>(
            """
            SELECT DISTINCT "ClassLabel" FROM "dbo"."Students"
            WHERE "TenantId" = @tenantId AND "Status" = 'active' AND "ClassLabel" IS NOT NULL
            """,
            new { tenantId }, ct);
        return rows.ToList();
    }
}
