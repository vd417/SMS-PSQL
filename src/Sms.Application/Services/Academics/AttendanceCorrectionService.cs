using Sms.Application.Common;
using Sms.Modules.Academics.Contracts;
using Sms.Modules.Academics.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Results;
using Sms.Application.Services.Tenancy;

namespace Sms.Application.Services.Academics;

public sealed record AttendanceBackupMarkResponse(Guid StudentId, string Status, string? StudentName);

public sealed record AttendancePeriodBackupResponse(
    IReadOnlyList<AttendanceBackupMarkResponse> Records,
    string? ActorName,
    DateTime At);

/// Leadership may change attendance that is already saved. The previous marks are stored.
/// Two such saves in a month are quiet. From the third, the owner is emailed who changed what.
public sealed class AttendanceCorrectionService(
    AttendanceCorrectionRepository corrections,
    IEmailSender email,
    ISchoolMailIdentity schoolMail)
{
    public async Task<ApiResult?> GuardPeriodOverwriteAsync(
        Guid tenantId,
        Guid classId,
        string className,
        BulkPeriodAttendanceRequest req,
        bool leadership,
        Guid? actorId,
        string? actorRole,
        IReadOnlyList<PeriodAttendanceRecordResponse> existing,
        CancellationToken ct)
    {
        var previous = new Dictionary<Guid, string>();
        foreach (var row in existing)
        {
            if (!string.IsNullOrWhiteSpace(row.Status))
                previous[row.StudentId] = row.Status.Trim();
        }
        if (previous.Count == 0) return null;

        var changes = new List<(Guid StudentId, string From, string To)>();
        foreach (var row in req.Records)
        {
            if (!previous.TryGetValue(row.StudentId, out var from)) continue;
            var to = (row.Status ?? "").Trim();
            if (to.Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) continue;
            changes.Add((row.StudentId, from, to));
        }
        if (changes.Count == 0) return null;

        if (!leadership)
        {
            return ApiResult.Fail(
                new Error("attendance_locked", "Saved attendance can be changed by the owner, admin, or principal."),
                403);
        }

        var names = await corrections.StudentNamesAsync(previous.Keys.ToArray(), ct);
        var actorName = await corrections.ActorNameAsync(actorId, ct);
        var backup = previous
            .Select(pair => new AttendanceBackupMark(
                pair.Key,
                pair.Value,
                names.TryGetValue(pair.Key, out var name) ? name : null))
            .ToList();
        var detail = string.Join("\n", changes.Select(c =>
        {
            var name = names.TryGetValue(c.StudentId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : c.StudentId.ToString();
            return $"{name}: {c.From} → {c.To}";
        }));

        var count = await corrections.RecordAsync(
            tenantId, classId, req.Date, req.Period, (req.Subject ?? "").Trim(),
            actorId, actorName, actorRole, backup, detail, ct);

        if (AttendanceCorrectionPolicy.WarnsOwner(count))
        {
            try { await WarnOwnerAsync(tenantId, className, req, actorName, actorRole, count, detail, ct); }
            catch { /* the change is already backed up; the mail is a warning, not the save */ }
        }
        return null;
    }

    public async Task<ApiResult<AttendancePeriodBackupResponse>> LatestPeriodBackupAsync(
        Guid classId, DateTime date, int period, string subject, bool leadership, CancellationToken ct)
    {
        if (!leadership)
        {
            return ApiResult<AttendancePeriodBackupResponse>.Fail(
                new Error("forbidden", "Only the owner, admin, or principal can restore attendance."),
                403);
        }
        var backup = await corrections.LatestPeriodBackupAsync(classId, date, period, subject.Trim(), ct);
        if (backup is null)
        {
            return ApiResult<AttendancePeriodBackupResponse>.Fail(
                new Error("not_found", "No earlier copy of this period is stored yet."),
                404);
        }
        return ApiResult<AttendancePeriodBackupResponse>.Ok(new AttendancePeriodBackupResponse(
            backup.Records.Select(r => new AttendanceBackupMarkResponse(r.StudentId, r.Status, r.StudentName)).ToList(),
            backup.ActorName,
            backup.At));
    }

    private async Task WarnOwnerAsync(
        Guid tenantId,
        string className,
        BulkPeriodAttendanceRequest req,
        string actorName,
        string? actorRole,
        int count,
        string detail,
        CancellationToken ct)
    {
        var owners = await corrections.OwnerEmailsAsync(tenantId, ct);
        if (owners.Count == 0) return;
        SchoolMailRoute? from = null;
        try { from = await schoolMail.ForTenantAsync(tenantId, ct); }
        catch { from = null; }

        var when = req.Date.ToString("dd MMM yyyy");
        var month = req.Date.ToString("MMMM yyyy");
        var subjectLine = $"Attendance change {count} this month — {className}";
        var body =
            $"{actorName} ({actorRole ?? "CRM"}) changed attendance that was already saved.\n" +
            $"Class: {className}\n" +
            $"Date: {when}\n" +
            $"Period: P{req.Period} {(req.Subject ?? "").Trim()}\n" +
            $"This is change {count} in {month}. The first two changes in a month are stored only. " +
            "From the third change, this warning is sent to the owner.\n\n" +
            "What changed:\n" +
            detail +
            "\n\nThe marks from before this save are kept, and can be put back from Class Attendance.";

        foreach (var address in owners)
        {
            await email.SendAsync(new EmailMessage(address, subjectLine, body).WithRoute(from), ct);
        }
    }
}
