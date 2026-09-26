using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Comms;

/// Wire shape the student/parent app already consumes (PTMMeetingDTO): date is YYYY-MM-DD,
/// time is HH:MM, child is the student id.
public sealed record PtmMeetingResponse(
    Guid Id, string Date, string Time, string Teacher, string? Subject, Guid Child, string Mode, string Status);
public sealed record SetPtmStatusRequest(string? Status);

public sealed class PtmRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Select =
        "SELECT m.\"Id\", to_char(m.\"MeetingDate\", 'YYYY-MM-DD') AS \"Date\", " +
        "to_char(m.\"MeetingTime\", 'HH24:MI') AS \"Time\", COALESCE(t.\"Name\", '') AS \"Teacher\", " +
        "m.\"Subject\", m.\"StudentId\" AS \"Child\", m.\"Mode\", m.\"Status\" " +
        "FROM \"dbo\".\"PtmMeetings\" m " +
        "LEFT JOIN \"dbo\".\"Teachers\" t ON t.\"Id\" = m.\"TeacherId\" AND t.\"TenantId\" = m.\"TenantId\" ";

    public Task<IReadOnlyList<PtmMeetingResponse>> ListForStudentsAsync(
        Guid tenantId, Guid[] studentIds, CancellationToken ct = default) =>
        QueryInlineAsync<PtmMeetingResponse>(
            Select + "WHERE m.\"TenantId\" = @tenantId AND m.\"StudentId\" = ANY(@studentIds) " +
            "ORDER BY m.\"MeetingDate\", m.\"MeetingTime\"",
            new { tenantId, studentIds }, ct);

    public async Task<PtmMeetingResponse?> GetAsync(Guid id, Guid tenantId, CancellationToken ct = default) =>
        (await QueryInlineAsync<PtmMeetingResponse>(
            Select + "WHERE m.\"Id\" = @id AND m.\"TenantId\" = @tenantId", new { id, tenantId }, ct))
        .FirstOrDefault();

    public Task<int> SetStatusAsync(Guid id, Guid tenantId, string status, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            "UPDATE \"dbo\".\"PtmMeetings\" SET \"Status\" = @status WHERE \"Id\" = @id AND \"TenantId\" = @tenantId",
            new { id, tenantId, status }, ct);
}
