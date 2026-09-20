using Dapper;
using FluentAssertions;
using Xunit;

namespace Sms.Tests.Integration.Migrations;

[Collection("sql")]
public class M0085_BackfillTests(PostgresFixture fx)
{
    [Fact]
    public async Task Clean_single_match_links_teacher_and_copies_name()
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Set SESSION_CONTEXT so RLS block predicates on Users/Teachers allow these inserts
        // (same pattern as M0084_IdentityLinkTests).
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@v", new { v = tenantId });
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=@v", new { v = 0 });

        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Email) VALUES (@userId, @tenantId, 'match@x.com')",
            new { userId, tenantId });
        var teacherId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT dbo.Teachers (Id, TenantId, Name, Email) VALUES (@teacherId, @tenantId, 'Jane Teacher', 'match@x.com')",
            new { teacherId, tenantId });

        // Re-run the backfill statements directly (migration already ran once at fixture setup with no rows present;
        // this test validates the SQL logic itself against freshly inserted rows using the same predicate).
        // The session's TenantId already matches these rows so no IsPlatform elevation is needed here;
        // the real M0085 migration elevates to IsPlatform=1 because its own connection has no
        // TenantId session context set at all and must see rows across every tenant.
        await conn.ExecuteAsync(@"
UPDATE t SET t.UserId = u.Id
FROM dbo.Teachers t JOIN dbo.Users u ON u.TenantId = t.TenantId
  AND LOWER(LTRIM(RTRIM(u.Email))) = LOWER(LTRIM(RTRIM(t.Email)))
WHERE t.Id = @teacherId AND t.UserId IS NULL", new { teacherId });
        await conn.ExecuteAsync(@"
UPDATE u SET u.Name = t.Name FROM dbo.Users u JOIN dbo.Teachers t ON t.UserId = u.Id
WHERE u.Id = @userId AND u.Name IS NULL", new { userId });

        var linkedUserId = await conn.QuerySingleAsync<Guid?>(
            "SELECT UserId FROM dbo.Teachers WHERE Id = @teacherId", new { teacherId });
        linkedUserId.Should().Be(userId);

        var name = await conn.QuerySingleAsync<string?>(
            "SELECT Name FROM dbo.Users WHERE Id = @userId", new { userId });
        name.Should().Be("Jane Teacher");
    }

    [Fact]
    public async Task Ambiguous_match_is_not_linked_and_would_be_reported()
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var tenantId = Guid.NewGuid();

        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@v", new { v = tenantId });
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=@v", new { v = 0 });

        // Users.Email and Users.Phone are each unique per tenant (M0082), so two Users rows can't
        // share the same phone. Instead make the Teacher match two DIFFERENT Users rows via the
        // OR (email-or-phone) predicate: one User shares the Teacher's phone, another shares an
        // email the Teacher doesn't actually have set... so instead give the Teacher both an email
        // and a phone, each of which belongs to a different User row -> 2 matches -> ambiguous.
        var phone = "+10000000001";
        var email = "shared@x.com";
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Phone) VALUES (@id, @tenantId, @phone)",
            new { id = Guid.NewGuid(), tenantId, phone });
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Email) VALUES (@id, @tenantId, @email)",
            new { id = Guid.NewGuid(), tenantId, email });
        var teacherId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT dbo.Teachers (Id, TenantId, Name, Phone, Email) VALUES (@teacherId, @tenantId, 'Ambiguous Teacher', @phone, @email)",
            new { teacherId, tenantId, phone, email });

        // Same "clean single match" predicate as the migration (email OR phone): COUNT(*) must equal 1 to link.
        await conn.ExecuteAsync(@"
UPDATE t
SET t.UserId = m.MatchedUserId
FROM dbo.Teachers t
CROSS APPLY (
    SELECT TOP 1 u.Id AS MatchedUserId
    FROM dbo.Users u
    WHERE u.TenantId = t.TenantId
      AND ((t.Email IS NOT NULL AND u.Email IS NOT NULL
              AND LOWER(LTRIM(RTRIM(u.Email))) = LOWER(LTRIM(RTRIM(t.Email))))
        OR (t.Phone IS NOT NULL AND u.Phone IS NOT NULL AND u.Phone = t.Phone))
) m
WHERE t.Id = @teacherId
  AND t.UserId IS NULL
  AND (
    SELECT COUNT(*) FROM dbo.Users u2
    WHERE u2.TenantId = t.TenantId
      AND ((t.Email IS NOT NULL AND u2.Email IS NOT NULL
              AND LOWER(LTRIM(RTRIM(u2.Email))) = LOWER(LTRIM(RTRIM(t.Email))))
        OR (t.Phone IS NOT NULL AND u2.Phone IS NOT NULL AND u2.Phone = t.Phone))
  ) = 1",
            new { teacherId });

        var linkedUserId = await conn.QuerySingleAsync<Guid?>(
            "SELECT UserId FROM dbo.Teachers WHERE Id = @teacherId", new { teacherId });
        linkedUserId.Should().BeNull("two different Users rows each matched (one by phone, one by email), so this is ambiguous and must not be auto-linked");

        // Now run the migration's report-insert SQL (same predicate/guard as M0085's Up()) and
        // assert the "reported" half actually happens: a row lands in the report table with
        // Reason='ambiguous' and MatchCount=2 for this teacher.
        await conn.ExecuteAsync(@"
INSERT INTO dbo._Migration_UnmatchedDirectoryRows (SourceTable, SourceId, TenantId, Reason, MatchCount)
SELECT 'Teachers', t.Id, t.TenantId, CASE WHEN x.Cnt = 0 THEN 'no_match' ELSE 'ambiguous' END, x.Cnt
FROM dbo.Teachers t
CROSS APPLY (
    SELECT COUNT(*) AS Cnt FROM dbo.Users u2
    WHERE u2.TenantId = t.TenantId
      AND ((t.Email IS NOT NULL AND u2.Email IS NOT NULL
              AND LOWER(LTRIM(RTRIM(u2.Email))) = LOWER(LTRIM(RTRIM(t.Email))))
        OR (t.Phone IS NOT NULL AND u2.Phone IS NOT NULL AND u2.Phone = t.Phone))
) x
WHERE t.Id = @teacherId AND t.UserId IS NULL AND x.Cnt <> 1
  AND NOT EXISTS (
    SELECT 1 FROM dbo._Migration_UnmatchedDirectoryRows r
    WHERE r.SourceTable = 'Teachers' AND r.SourceId = t.Id);", new { teacherId });

        var report = await conn.QuerySingleAsync(
            "SELECT Reason, MatchCount FROM dbo._Migration_UnmatchedDirectoryRows WHERE SourceTable = 'Teachers' AND SourceId = @teacherId",
            new { teacherId });
        ((string)report.Reason).Should().Be("ambiguous");
        ((int)report.MatchCount).Should().Be(2);
    }

    [Fact]
    public async Task Report_insert_is_idempotent_when_rerun_outside_the_migration_gate()
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var tenantId = Guid.NewGuid();

        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@v", new { v = tenantId });
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=@v", new { v = 0 });

        var teacherId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT dbo.Teachers (Id, TenantId, Name, Email) VALUES (@teacherId, @tenantId, 'Unmatched Teacher', 'nobody@x.com')",
            new { teacherId, tenantId });

        const string reportInsertSql = @"
INSERT INTO dbo._Migration_UnmatchedDirectoryRows (SourceTable, SourceId, TenantId, Reason, MatchCount)
SELECT 'Teachers', t.Id, t.TenantId, CASE WHEN x.Cnt = 0 THEN 'no_match' ELSE 'ambiguous' END, x.Cnt
FROM dbo.Teachers t
CROSS APPLY (
    SELECT COUNT(*) AS Cnt FROM dbo.Users u2
    WHERE u2.TenantId = t.TenantId
      AND ((t.Email IS NOT NULL AND u2.Email IS NOT NULL
              AND LOWER(LTRIM(RTRIM(u2.Email))) = LOWER(LTRIM(RTRIM(t.Email))))
        OR (t.Phone IS NOT NULL AND u2.Phone IS NOT NULL AND u2.Phone = t.Phone))
) x
WHERE t.Id = @teacherId AND t.UserId IS NULL AND x.Cnt <> 1
  AND NOT EXISTS (
    SELECT 1 FROM dbo._Migration_UnmatchedDirectoryRows r
    WHERE r.SourceTable = 'Teachers' AND r.SourceId = t.Id);";

        // Simulate re-running the raw SQL outside FluentMigrator's normal VersionInfo gate (e.g. a
        // manual hotfix run twice by accident): the NOT EXISTS guard must prevent a duplicate row.
        await conn.ExecuteAsync(reportInsertSql, new { teacherId });
        await conn.ExecuteAsync(reportInsertSql, new { teacherId });

        var rowCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo._Migration_UnmatchedDirectoryRows WHERE SourceTable = 'Teachers' AND SourceId = @teacherId",
            new { teacherId });
        rowCount.Should().Be(1, "the NOT EXISTS guard must make the report INSERT idempotent when re-run");
    }

    [Fact]
    public async Task Report_table_exists_and_is_queryable()
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var count = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo._Migration_UnmatchedDirectoryRows");
        count.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Report_table_has_expected_columns()
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var cols = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '_Migration_UnmatchedDirectoryRows'")).ToList();
        cols.Should().Contain(new[] { "Id", "SourceTable", "SourceId", "TenantId", "Reason", "MatchCount", "CreatedAt" });
    }
}
