using System.Data.Common;
using Dapper;
using FluentAssertions;
using Npgsql;
using Sms.Modules.Staffing.Data;
using Sms.Shared.Kernel.Data;
using Xunit;

namespace Sms.Tests.Integration.Staffing;

[Collection("sql")]
public class ClassTeacherStudentIdsTests(PostgresFixture fx)
{
    private sealed class PlainFactory(string cs, Guid tenantId) : IDbConnectionFactory
    {
        public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
        {
            var c = new NpgsqlConnection(cs);
            await c.OpenAsync(ct);
            await c.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            return c;
        }
    }

    [Fact]
    public async Task Returns_only_active_students_of_the_teachers_own_class()
    {
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var matching = Guid.NewGuid();
        var other = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Teachers\" (\"Id\", \"TenantId\", \"Name\", \"UserId\") VALUES (@teacherId, @tenantId, 'Rajesh Teacher', @teacherUserId)",
                new { teacherId, tenantId, teacherUserId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Classes\" (\"TenantId\", \"Name\", \"Grade\", \"Section\", \"ClassTeacherId\") VALUES (@tenantId, 'Grade 5 - A', 'Grade 5', 'A', @teacherId)",
                new { tenantId, teacherId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Grade\", \"Section\", \"ClassLabel\", \"Roll\", \"Status\") " +
                "VALUES (@matching, @tenantId, 'ADM-1', 'Match', 'Grade 5', 'A', 'Grade 5 - A', 1, 'active'), " +
                "(@other, @tenantId, 'ADM-2', 'Other', 'Grade 6', 'B', 'Grade 6 - B', 2, 'active')",
                new { matching, other, tenantId });
        }

        var repo = new LeaveRepository(new PlainFactory(fx.ConnectionString, tenantId));
        var ids = await repo.StudentIdsForClassTeacherAsync(teacherUserId, tenantId, default);
        ids.Should().ContainSingle().Which.Should().Be(matching);
    }

    [Fact]
    public async Task Does_not_leak_students_from_another_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var teacherUserA = Guid.NewGuid();
        var teacherUserB = Guid.NewGuid();
        var studentA = Guid.NewGuid();
        var studentB = Guid.NewGuid();

        foreach (var (tenantId, userId, studentId, adm) in new[] { (tenantA, teacherUserA, studentA, "ADM-A"), (tenantB, teacherUserB, studentB, "ADM-B") })
        {
            await using var conn = new NpgsqlConnection(fx.ConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            var teacherId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Teachers\" (\"Id\", \"TenantId\", \"Name\", \"UserId\") VALUES (@teacherId, @tenantId, 'T', @userId)",
                new { teacherId, tenantId, userId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Classes\" (\"TenantId\", \"Name\", \"Grade\", \"Section\", \"ClassTeacherId\") VALUES (@tenantId, 'Grade 5 - A', 'Grade 5', 'A', @teacherId)",
                new { tenantId, teacherId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Grade\", \"Section\", \"ClassLabel\", \"Roll\", \"Status\") " +
                "VALUES (@studentId, @tenantId, @adm, 'S', 'Grade 5', 'A', 'Grade 5 - A', 1, 'active')",
                new { studentId, tenantId, adm });
        }

        var repo = new LeaveRepository(new PlainFactory(fx.ConnectionString, tenantA));
        var ids = await repo.StudentIdsForClassTeacherAsync(teacherUserA, tenantA, default);
        ids.Should().ContainSingle().Which.Should().Be(studentA);
        ids.Should().NotContain(studentB);
    }
}
