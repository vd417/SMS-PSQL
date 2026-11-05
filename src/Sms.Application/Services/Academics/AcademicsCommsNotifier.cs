using Sms.Application.Services.Attendance;
using Sms.Application.Services.Comms;
using Sms.Application.Services.Realtime;
using Sms.Modules.Academics.Contracts;
using Sms.Modules.Comms;
using Sms.Modules.Sis.Data;

namespace Sms.Application.Services.Academics;

/// <summary>
/// In-app notices for academics events (homework, class tests, timetable publish).
/// Uses the tenant-scoped <c>Notifications</c> feed — student, parent, teacher, and school CRM apps
/// all read <c>GET /v1/notifications</c>. Exam-term publish from admin is handled separately.
/// </summary>
public sealed class AcademicsCommsNotifier(
    CommsRepository comms,
    IAnnouncementService announcements,
    StudentRepository students,
    ILiveBroadcaster live)
{
    public async Task NotifyHomeworkAssignedAsync(
        Guid tenantId,
        string title,
        string? className,
        DateTime? dueDate,
        CancellationToken ct = default)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return;

        var cls = string.IsNullOrWhiteSpace(className) ? "Class" : className.Trim();
        var due = dueDate is { } d ? d.ToString("yyyy-MM-dd") : null;
        var body = due is null ? cls : $"{cls} · due {due}";

        var notice = await comms.CreateNotificationAsync(tenantId, new CreateNotificationRequest(
            Icon: "book",
            Tone: "brand",
            Title: $"Homework: {trimmed}",
            Body: body), ct);
        await live.PublishAsync(tenantId, LiveEventTypes.Homework, ct: ct);
        if (notice is not null)
            await NotificationEvents.PublishAsync(live, tenantId, notice, null, ct);
    }

    public async Task NotifyClassTestScheduledAsync(
        Guid tenantId,
        string? title,
        string? subject,
        DateTime? date,
        Guid? classId = null,
        CancellationToken ct = default)
    {
        var name = (title ?? "").Trim();
        if (name.Length == 0) name = "Class test";

        var subj = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        var when = date is { } d ? d.ToString("yyyy-MM-dd") : null;
        var body = subj is null
            ? when ?? "Scheduled"
            : when is null ? subj : $"{subj} · {when}";

        var rosterEmails = new List<string>();
        if (classId is { } cid)
        {
            string? cursor = null;
            do
            {
                var (rows, next) = await students.ListByClassPagedAsync(cid, 200, cursor, ct);
                foreach (var s in rows)
                {
                    AddEmail(rosterEmails, s.GuardianEmail);
                    AddEmail(rosterEmails, s.Email);
                }
                cursor = next;
            } while (cursor is not null);
        }

        var audience = classId is null ? "parents" : "parent";
        await announcements.CreateAsync(new CreateAnnouncementRequest(
            $"Class test: {name}",
            body,
            "class_test",
            audience,
            rosterEmails.Count > 0 ? rosterEmails : null,
            null,
            ["email", "app"],
            null,
            when,
            "Class test"), null, "teacher", ct);

        await live.PublishAsync(tenantId, LiveEventTypes.Exams, ct: ct);
    }

    public async Task NotifyTimetablePublishedAsync(
        Guid tenantId,
        int classCount,
        int slotCount,
        CancellationToken ct = default)
    {
        if (classCount <= 0 || slotCount <= 0) return;

        var clsLabel = classCount == 1 ? "1 class" : $"{classCount} classes";
        var slotLabel = slotCount == 1 ? "1 period" : $"{slotCount} periods";
        var notice = await comms.CreateNotificationAsync(tenantId, new CreateNotificationRequest(
            Icon: "calendar",
            Tone: "brand",
            Title: "Timetable updated",
            Body: $"{clsLabel} · {slotLabel} with bell times — open Schedule to refresh."), ct);
        await live.PublishAsync(tenantId, LiveEventTypes.Timetable, ct: ct);
        if (notice is not null)
            await NotificationEvents.PublishAsync(live, tenantId, notice, null, ct);
    }

    public async Task NotifyAbsenceMarksAsync(
        Guid tenantId,
        DateTime date,
        IReadOnlyList<AttendanceUpsertRow> records,
        string? subject = null,
        int? period = null,
        CancellationToken ct = default)
    {
        var absentIds = AttendanceParentNotice.AbsentStudentIds(
            (records ?? []).Select(r => (r.StudentId, (string?)r.Status)));
        if (absentIds.Count == 0) return;

        foreach (var studentId in absentIds)
        {
            var student = await students.GetAsync(studentId, ct);
            var name = (student?.Name ?? "").Trim();
            if (name.Length == 0) name = "Your child";
            var parents = await students.ListParentUserIdsAsync(studentId, student?.AdmissionNo ?? "", ct);
            foreach (var parentId in parents)
            {
                var notice = await comms.CreateNotificationAsync(tenantId, new CreateNotificationRequest(
                    Icon: "alert",
                    Tone: "warn",
                    Title: AttendanceParentNotice.Title(name),
                    Body: AttendanceParentNotice.Body(date, subject, period),
                    UserId: parentId), ct);
                if (notice is not null)
                    await NotificationEvents.PublishAsync(live, tenantId, notice, parentId, ct);
            }
        }
    }

    private static void AddEmail(List<string> list, string? email)
    {
        var v = (email ?? "").Trim();
        if (v.Contains('@') && v.Length > 3 && !list.Contains(v, StringComparer.OrdinalIgnoreCase))
            list.Add(v);
    }
}
