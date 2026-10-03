using Sms.Application.Services.Realtime;
using Sms.Modules.Comms;
using Sms.Modules.Sis.Data;

namespace Sms.Application.Services.Comms;

/// <summary>
/// In-app notice when a parent-teacher meeting is scheduled. Writes a user-scoped row to the
/// tenant <c>Notifications</c> feed for each member of the student's family (parent logins and the
/// student's own login, exactly as <see cref="StudentRepository.ListParentUserIdsAsync"/> resolves
/// them) and broadcasts the live <c>notification</c> event so the bell refreshes — the same pattern
/// as <see cref="Academics.AcademicsCommsNotifier"/>'s absence notice. Creating the meeting used to
/// write no notification, so the family saw the meeting in the PTM list but was never alerted.
/// </summary>
public sealed class PtmCommsNotifier(
    CommsRepository comms,
    StudentRepository students,
    ILiveBroadcaster live)
{
    public async Task NotifyMeetingScheduledAsync(
        Guid tenantId, PtmMeetingResponse meeting, CancellationToken ct = default)
    {
        var student = await students.GetAsync(meeting.Child, ct);
        var recipients = await students.ListParentUserIdsAsync(
            meeting.Child, student?.AdmissionNo ?? "", ct);
        if (recipients.Count == 0) return;

        var name = string.IsNullOrWhiteSpace(meeting.StudentName) ? "your child" : meeting.StudentName.Trim();
        var subject = string.IsNullOrWhiteSpace(meeting.Subject) ? null : meeting.Subject!.Trim();
        var body = subject is null
            ? $"{name} with {meeting.Teacher} · {meeting.Date} {meeting.Time} · {meeting.Mode}"
            : $"{name} · {subject} with {meeting.Teacher} · {meeting.Date} {meeting.Time} · {meeting.Mode}";

        foreach (var userId in recipients)
        {
            await comms.CreateNotificationAsync(tenantId, new CreateNotificationRequest(
                Icon: "calendar",
                Tone: "brand",
                Title: "Parent-teacher meeting scheduled",
                Body: body,
                UserId: userId), ct);
        }

        await live.PublishAsync(tenantId, LiveEventTypes.Notification, ct: ct);
    }
}
