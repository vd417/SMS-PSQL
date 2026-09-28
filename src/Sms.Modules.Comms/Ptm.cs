using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Comms;

/// Wire shape the student/parent app already consumes (PTMMeetingDTO): date is YYYY-MM-DD,
/// time is HH:MM, child is the student id. StudentName and TeacherId are additive fields for
/// the staff (teacher/admin) apps; existing keys are unchanged so the parent app is unaffected.
public sealed record PtmMeetingResponse(
    Guid Id, string Date, string Time, string Teacher, string? Subject, Guid Child, string Mode, string Status,
    string StudentName, Guid? TeacherId);

public sealed record CreatePtmRequest(
    Guid? StudentId, Guid? TeacherId, string? Subject, string? Date, string? Time, string? Mode);

/// The parent path only reads Status; the staff path ignores Status (and instead resets it to
/// "pending" itself whenever Date or Time changes).
public sealed record UpdatePtmRequest(string? Subject, string? Date, string? Time, string? Mode, string? Status);

public sealed class PtmRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Select =
        "SELECT m.\"Id\", to_char(m.\"MeetingDate\", 'YYYY-MM-DD') AS \"Date\", " +
        "to_char(m.\"MeetingTime\", 'HH24:MI') AS \"Time\", COALESCE(t.\"Name\", '') AS \"Teacher\", " +
        "m.\"Subject\", m.\"StudentId\" AS \"Child\", m.\"Mode\", m.\"Status\", " +
        "COALESCE(s.\"Name\", '') AS \"StudentName\", m.\"TeacherId\" " +
        "FROM \"dbo\".\"PtmMeetings\" m " +
        "LEFT JOIN \"dbo\".\"Teachers\" t ON t.\"Id\" = m.\"TeacherId\" AND t.\"TenantId\" = m.\"TenantId\" " +
        "LEFT JOIN \"dbo\".\"Students\" s ON s.\"Id\" = m.\"StudentId\" AND s.\"TenantId\" = m.\"TenantId\" ";

    public Task<IReadOnlyList<PtmMeetingResponse>> ListForStudentsAsync(
        Guid tenantId, Guid[] studentIds, CancellationToken ct = default) =>
        QueryInlineAsync<PtmMeetingResponse>(
            Select + "WHERE m.\"TenantId\" = @tenantId AND m.\"StudentId\" = ANY(@studentIds) " +
            "ORDER BY m.\"MeetingDate\", m.\"MeetingTime\"",
            new { tenantId, studentIds }, ct);

    /// Staff listing: teachers pass their own teacherId; managers may pass null (every meeting)
    /// or filter by teacherId/studentId. status/from/to narrow further for either caller.
    /// from/to are passed as DateTime (Dapper/Npgsql has no built-in DateOnly value handler).
    public Task<IReadOnlyList<PtmMeetingResponse>> ListForStaffAsync(
        Guid tenantId, Guid? teacherId, Guid? studentId, string? status, DateOnly? from, DateOnly? to,
        CancellationToken ct = default) =>
        QueryInlineAsync<PtmMeetingResponse>(
            Select + "WHERE m.\"TenantId\" = @tenantId " +
            "AND (@teacherId::uuid IS NULL OR m.\"TeacherId\" = @teacherId) " +
            "AND (@studentId::uuid IS NULL OR m.\"StudentId\" = @studentId) " +
            "AND (@status::text IS NULL OR m.\"Status\" = @status) " +
            "AND (@from::date IS NULL OR m.\"MeetingDate\" >= @from) " +
            "AND (@to::date IS NULL OR m.\"MeetingDate\" <= @to) " +
            "ORDER BY m.\"MeetingDate\", m.\"MeetingTime\"",
            new
            {
                tenantId, teacherId, studentId, status,
                from = from?.ToDateTime(TimeOnly.MinValue),
                to = to?.ToDateTime(TimeOnly.MinValue),
            }, ct);

    public async Task<PtmMeetingResponse?> GetAsync(Guid id, Guid tenantId, CancellationToken ct = default) =>
        (await QueryInlineAsync<PtmMeetingResponse>(
            Select + "WHERE m.\"Id\" = @id AND m.\"TenantId\" = @tenantId", new { id, tenantId }, ct))
        .FirstOrDefault();

    public Task<int> SetStatusAsync(Guid id, Guid tenantId, string status, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            "UPDATE \"dbo\".\"PtmMeetings\" SET \"Status\" = @status WHERE \"Id\" = @id AND \"TenantId\" = @tenantId",
            new { id, tenantId, status }, ct);

    public async Task<bool> StudentExistsAsync(Guid tenantId, Guid studentId, CancellationToken ct = default) =>
        (await QueryInlineAsync<Guid>(
            "SELECT \"Id\" FROM \"dbo\".\"Students\" WHERE \"Id\" = @studentId AND \"TenantId\" = @tenantId",
            new { tenantId, studentId }, ct))
        .Any();

    public async Task<bool> TeacherExistsAsync(Guid tenantId, Guid teacherId, CancellationToken ct = default) =>
        (await QueryInlineAsync<Guid>(
            "SELECT \"Id\" FROM \"dbo\".\"Teachers\" WHERE \"Id\" = @teacherId AND \"TenantId\" = @tenantId",
            new { tenantId, teacherId }, ct))
        .Any();

    /// date/time are passed as DateTime/TimeSpan (Dapper/Npgsql has no built-in DateOnly/TimeOnly
    /// value handler); the target columns are still "date" and "time" respectively.
    public async Task<Guid> CreateAsync(
        Guid tenantId, Guid studentId, Guid teacherId, string? subject, DateOnly date, TimeOnly time, string mode,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await ExecuteInlineAsync(
            "INSERT INTO \"dbo\".\"PtmMeetings\" " +
            "(\"Id\", \"TenantId\", \"StudentId\", \"TeacherId\", \"Subject\", \"MeetingDate\", \"MeetingTime\", " +
            "\"Mode\", \"Status\") VALUES " +
            "(@id, @tenantId, @studentId, @teacherId, @subject, @date, @time, @mode, 'pending')",
            new
            {
                id, tenantId, studentId, teacherId, subject,
                date = date.ToDateTime(TimeOnly.MinValue),
                time = time.ToTimeSpan(),
                mode,
            }, ct);
        return id;
    }

    public Task<int> UpdateAsync(
        Guid id, Guid tenantId, string? subject, DateOnly? date, TimeOnly? time, string? mode, bool resetStatus,
        CancellationToken ct = default) =>
        ExecuteInlineAsync(
            "UPDATE \"dbo\".\"PtmMeetings\" SET " +
            "\"Subject\" = COALESCE(@subject, \"Subject\"), " +
            "\"MeetingDate\" = COALESCE(@date::date, \"MeetingDate\"), " +
            "\"MeetingTime\" = COALESCE(@time::time, \"MeetingTime\"), " +
            "\"Mode\" = COALESCE(@mode, \"Mode\"), " +
            "\"Status\" = CASE WHEN @resetStatus THEN 'pending' ELSE \"Status\" END " +
            "WHERE \"Id\" = @id AND \"TenantId\" = @tenantId",
            new
            {
                id, tenantId, subject,
                date = date?.ToDateTime(TimeOnly.MinValue),
                time = time?.ToTimeSpan(),
                mode, resetStatus,
            }, ct);

    public Task<int> DeleteAsync(Guid id, Guid tenantId, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            "DELETE FROM \"dbo\".\"PtmMeetings\" WHERE \"Id\" = @id AND \"TenantId\" = @tenantId",
            new { id, tenantId }, ct);
}
