using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Academics.Data;

public sealed class HomeworkRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols =
        "\"Id\", \"TenantId\", \"StudentId\", \"AssignmentId\", \"Title\", \"SubjectId\", \"DueDate\", \"DueTime\", \"Status\", \"Priority\", \"Grade\"";

    public Task<HomeworkResponse?> CreateAsync(Guid tenantId, CreateHomeworkRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<HomeworkResponse>("dbo.Homework_Create", new
        {
            TenantId = tenantId, r.StudentId, r.AssignmentId, r.Title, r.SubjectId, r.DueDate, r.DueTime, r.Priority
        }, ct);

    public Task<HomeworkResponse?> SetStatusAsync(Guid id, string status, CancellationToken ct = default) =>
        QuerySingleProcAsync<HomeworkResponse>("dbo.Homework_SetStatus", new { Id = id, Status = status }, ct);

    public async Task<HomeworkResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<HomeworkResponse>($"SELECT {Cols} FROM \"dbo\".\"Homework\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public Task<IReadOnlyList<HomeworkResponse>> ListAsync(Guid? studentId, string? status, CancellationToken ct = default) =>
        QueryInlineAsync<HomeworkResponse>(
            $"SELECT {Cols} FROM \"dbo\".\"Homework\" WHERE (@studentId IS NULL OR \"StudentId\" = @studentId) " +
            "AND (@status IS NULL OR \"Status\" = @status) ORDER BY \"DueDate\"",
            new { studentId, status }, ct);
}
