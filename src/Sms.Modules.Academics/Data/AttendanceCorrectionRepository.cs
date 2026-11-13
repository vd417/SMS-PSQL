using System.Text.Json;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Academics.Data;

public sealed record AttendanceBackupMark(Guid StudentId, string Status, string? StudentName);

public sealed record AttendancePeriodBackup(
    IReadOnlyList<AttendanceBackupMark> Records,
    string? ActorName,
    DateTime At);

public sealed class AttendanceCorrectionRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<IReadOnlyDictionary<Guid, string>> StudentNamesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        var rows = await QueryInlineAsync<NameRow>(
            "SELECT \"Id\", \"Name\" FROM \"dbo\".\"Students\" WHERE \"Id\" = ANY(@ids)",
            new { ids = ids.ToArray() }, ct);
        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .ToDictionary(r => r.Id, r => r.Name!.Trim());
    }

    public async Task<string> ActorNameAsync(Guid? userId, CancellationToken ct = default)
    {
        if (userId is null) return "CRM user";
        var rows = await QueryInlineAsync<NameText>(@"
SELECT COALESCE(NULLIF(btrim(""Name""), ''), NULLIF(btrim(""Email""), ''), 'CRM user') AS ""Value""
FROM ""dbo"".""Users"" WHERE ""Id"" = @userId LIMIT 1",
            new { userId }, ct);
        return rows.FirstOrDefault()?.Value ?? "CRM user";
    }

    public async Task<IReadOnlyList<string>> OwnerEmailsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<NameText>(@"
SELECT DISTINCT btrim(u.""Email"") AS ""Value""
FROM ""dbo"".""Users"" u
INNER JOIN ""dbo"".""UserRoles"" ur ON ur.""UserId"" = u.""Id""
WHERE u.""TenantId"" = @tenantId
  AND ur.""Role"" = 'school.owner'
  AND NULLIF(btrim(u.""Email""), '') IS NOT NULL",
            new { tenantId }, ct);
        return rows
            .Select(r => r.Value?.Trim() ?? "")
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// Stores the marks from before this change and returns how many changes this school
    /// has made in the current Asia/Kolkata calendar month, including this one.
    public async Task<int> RecordAsync(
        Guid tenantId,
        Guid classId,
        DateTime date,
        int period,
        string subject,
        Guid? actorId,
        string actorName,
        string? actorRole,
        IReadOnlyList<AttendanceBackupMark> backup,
        string detail,
        CancellationToken ct = default)
    {
        await ExecuteInlineAsync(@"
INSERT INTO ""dbo"".""AttendanceCorrections""
    (""TenantId"", ""Kind"", ""ClassId"", ""Date"", ""Period"", ""Subject"", ""ActorId"", ""ActorName"", ""ActorRole"", ""BackupJson"", ""Detail"")
VALUES
    (@tenantId, 'period', @classId, @date, @period, @subject, @actorId, @actorName, @actorRole, @backupJson, @detail)",
            new
            {
                tenantId,
                classId,
                date = date.Date,
                period,
                subject,
                actorId,
                actorName,
                actorRole,
                backupJson = JsonSerializer.Serialize(backup, Json),
                detail,
            }, ct);

        var counts = await QueryInlineAsync<CountRow>(@"
SELECT COUNT(*)::int AS ""Count""
FROM ""dbo"".""AttendanceCorrections""
WHERE ""TenantId"" = @tenantId
  AND ""At"" >= (date_trunc('month', timezone('Asia/Kolkata', now())) AT TIME ZONE 'Asia/Kolkata')
  AND ""At"" < ((date_trunc('month', timezone('Asia/Kolkata', now())) + interval '1 month') AT TIME ZONE 'Asia/Kolkata')",
            new { tenantId }, ct);
        return counts.FirstOrDefault()?.Count ?? 0;
    }

    public async Task<AttendancePeriodBackup?> LatestPeriodBackupAsync(
        Guid classId, DateTime date, int period, string subject, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<BackupRow>(@"
SELECT ""BackupJson"", ""ActorName"", ""At""
FROM ""dbo"".""AttendanceCorrections""
WHERE ""Kind"" = 'period' AND ""ClassId"" = @classId AND ""Date"" = @date AND ""Period"" = @period AND ""Subject"" = @subject
ORDER BY ""At"" DESC
LIMIT 1",
            new { classId, date = date.Date, period, subject }, ct);
        var row = rows.FirstOrDefault();
        if (row is null || string.IsNullOrWhiteSpace(row.BackupJson)) return null;
        var records = JsonSerializer.Deserialize<List<AttendanceBackupMark>>(row.BackupJson, Json) ?? [];
        return new AttendancePeriodBackup(records, row.ActorName, row.At);
    }

    private sealed record NameRow(Guid Id, string? Name);
    private sealed record NameText(string? Value);
    private sealed record CountRow(int Count);
    private sealed record BackupRow(string BackupJson, string? ActorName, DateTime At);
}
