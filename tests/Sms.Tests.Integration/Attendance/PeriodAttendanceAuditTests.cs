using System.Data;
using Dapper;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Sms.Tests.Integration.Attendance;

[Collection("sql")]
public sealed class PeriodAttendanceAuditTests(PostgresFixture fx)
{
    [Fact]
    public async Task Insert_then_status_change_appends_audit_rows_and_stamps_updated_by()
    {
        var tenantId = Guid.NewGuid();
        var classId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var markerId = Guid.NewGuid();
        var date = new DateTime(2026, 8, 12);

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await using var connection = new NpgsqlConnection(fx.ConnectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, tenantId);

        await connection.ExecuteAsync(
            """
            INSERT dbo.Users (Id, TenantId, Name) VALUES (@markerId, @tenantId, N'Ravi Sharma');
            INSERT dbo.Classes (Id, TenantId, Name, Grade, Section, StudentCount) VALUES (@classId, @tenantId, N'IX-A', N'IX', N'A', 1);
            INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, Status) VALUES (@studentId, @tenantId, N'AUD-1', N'Student One', N'IX', N'A', N'active');
            """,
            new { tenantId, classId, studentId, markerId });

        var recordId = await BulkUpsertAsync(connection, tenantId, classId, date, markerId, "present");
        await SetTenantAsync(connection, tenantId);
        var afterInsert = await ReadAuditAsync(connection, recordId);
        afterInsert.Should().ContainSingle();
        afterInsert[0].FromStatus.Should().BeNull();
        afterInsert[0].ToStatus.Should().Be("present");
        afterInsert[0].ActorId.Should().Be(markerId);
        afterInsert[0].ActorName.Should().Be("Ravi Sharma");

        // Re-saving the same status must not append a second audit row (idempotent re-save).
        await BulkUpsertAsync(connection, tenantId, classId, date, markerId, "present");
        await SetTenantAsync(connection, tenantId);
        (await ReadAuditAsync(connection, recordId)).Should().ContainSingle();

        // A real status change appends a second audit row with the prior status captured.
        await BulkUpsertAsync(connection, tenantId, classId, date, markerId, "absent");
        await SetTenantAsync(connection, tenantId);
        var afterChange = await ReadAuditAsync(connection, recordId);
        afterChange.Should().HaveCount(2);
        var latest = afterChange.OrderByDescending(r => r.At).First();
        latest.FromStatus.Should().Be("present");
        latest.ToStatus.Should().Be("absent");

        var record = await connection.QuerySingleAsync<(Guid? UpdatedBy, string? UpdatedByRole)>(
            "SELECT UpdatedBy, UpdatedByRole FROM dbo.PeriodAttendanceRecords WHERE Id = @recordId",
            new { recordId });
        record.UpdatedBy.Should().Be(markerId);
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId) =>
        await connection.ExecuteAsync(
            "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId",
            new { tenantId });

    private static async Task<Guid> BulkUpsertAsync(
        NpgsqlConnection connection, Guid tenantId, Guid classId, DateTime date, Guid markerId, string status)
    {
        await SetTenantAsync(connection, tenantId);
        var table = new DataTable();
        table.Columns.Add("StudentId", typeof(Guid));
        table.Columns.Add("Status", typeof(string));
        var studentId = await connection.QuerySingleAsync<Guid>(
            "SELECT TOP 1 Id FROM dbo.Students WHERE TenantId = @tenantId", new { tenantId });
        table.Rows.Add(studentId, status);

        var p = new DynamicParameters();
        p.Add("@TenantId", tenantId);
        p.Add("@ClassId", classId);
        p.Add("@Date", date);
        p.Add("@Period", 1);
        p.Add("@Subject", "Math");
        p.Add("@MarkedBy", markerId);
        p.Add("@MarkedByRole", "teacher");
        p.Add("@Rows", table.AsTableValuedParameter("dbo.PeriodAttendanceTvp"));
        await connection.ExecuteAsync(
            "dbo.PeriodAttendance_BulkUpsert",
            p,
            commandType: CommandType.StoredProcedure);

        return await connection.QuerySingleAsync<Guid>(
            "SELECT Id FROM dbo.PeriodAttendanceRecords WHERE TenantId = @tenantId AND ClassId = @classId AND [Date] = @date AND Period = 1 AND Subject = N'Math'",
            new { tenantId, classId, date });
    }

    private static async Task<List<AuditRow>> ReadAuditAsync(NpgsqlConnection connection, Guid recordId) =>
        (await connection.QueryAsync<AuditRow>(
            "SELECT FromStatus, ToStatus, ActorId, ActorName, At FROM dbo.PeriodAttendanceAudit WHERE RecordId = @recordId",
            new { recordId })).AsList();

    private sealed record AuditRow(string? FromStatus, string ToStatus, Guid? ActorId, string? ActorName, DateTime At);
}
