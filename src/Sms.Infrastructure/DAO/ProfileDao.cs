using Sms.Application.Interfaces.DAO;
using Sms.Shared.Kernel.Data;

namespace Sms.Infrastructure.DAO;

public sealed class ProfileDao(IDbConnectionFactory factory) : BaseRepository(factory), IProfileDao
{
    private const string TeacherSelect =
        """
        SELECT t."Designation", t."ClassTeacher", t."Phone", t."Email", t."EmployeeCode", t."CreatedAt" AS "JoinedAt",
            (SELECT c."Name" FROM "dbo"."Classes" c WHERE c."ClassTeacherId" = t."Id" LIMIT 1) AS "HomeroomClassName",
            CAST(NULL AS varchar(80)) AS "DutyPost"
        FROM "dbo"."Teachers" t
        WHERE t."UserId" = @userId
           OR (
                @tenantId IS NOT NULL AND t."TenantId" = @tenantId AND (
                    (@email IS NOT NULL AND lower(trim(t."Email")) = lower(trim(@email)))
                    OR (@name IS NOT NULL AND lower(trim(t."Name")) = lower(trim(@name)))
                )
              )
        ORDER BY CASE WHEN t."UserId" = @userId THEN 0
                      WHEN @email IS NOT NULL AND lower(trim(t."Email")) = lower(trim(@email)) THEN 1
                      ELSE 2 END, t."CreatedAt"
        LIMIT 1
        """;

    private const string StaffSelect =
        """
        SELECT s."Role" AS "Designation",
            CAST(NULL AS varchar(40)) AS "ClassTeacher",
            s."Phone", s."Email", s."EmployeeCode",
            s."CreatedAt" AS "JoinedAt",
            CAST(NULL AS varchar(200)) AS "HomeroomClassName",
            COALESCE(NULLIF(trim(s."Route"), ''), NULLIF(trim(s."Department"), ''), '') AS "DutyPost"
        FROM "dbo"."Staff" s
        WHERE s."UserId" = @userId
           OR (
                @tenantId IS NOT NULL AND s."TenantId" = @tenantId AND (
                    (@email IS NOT NULL AND lower(trim(s."Email")) = lower(trim(@email)))
                    OR (@name IS NOT NULL AND lower(trim(s."Name")) = lower(trim(@name)))
                )
              )
        ORDER BY CASE WHEN s."UserId" = @userId THEN 0
                      WHEN @email IS NOT NULL AND lower(trim(s."Email")) = lower(trim(@email)) THEN 1
                      ELSE 2 END, s."CreatedAt"
        LIMIT 1
        """;

    public Task<LinkedPersonProfile?> GetLinkedTeacherAsync(
        Guid userId, Guid? tenantId, string? email, string? name, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(tenantId, email, name, userId, TeacherSelect, ct);

    public Task<LinkedPersonProfile?> GetLinkedStaffAsync(
        Guid userId, Guid? tenantId, string? email, string? name, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync(tenantId, email, name, userId, StaffSelect, ct);

    public async Task<string?> GetSharedPhoneFromRosterAsync(string? email, string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(name)) return null;
        var rows = await QueryInlineAsync<string>(
            """
            SELECT "Phone" FROM (
                SELECT t."Phone", t."CreatedAt"
                FROM "dbo"."Teachers" t
                WHERE t."Phone" IS NOT NULL AND trim(t."Phone") <> ''
                  AND (
                    (@email IS NOT NULL AND t."Email" IS NOT NULL
                     AND lower(trim(t."Email")) = lower(trim(@email)))
                    OR (@name IS NOT NULL AND lower(trim(t."Name")) = lower(trim(@name)))
                  )
                UNION ALL
                SELECT s."Phone", s."CreatedAt"
                FROM "dbo"."Staff" s
                WHERE s."Phone" IS NOT NULL AND trim(s."Phone") <> ''
                  AND (
                    (@email IS NOT NULL AND s."Email" IS NOT NULL
                     AND lower(trim(s."Email")) = lower(trim(@email)))
                    OR (@name IS NOT NULL AND lower(trim(s."Name")) = lower(trim(@name)))
                  )
            ) AS contacts
            ORDER BY "CreatedAt" DESC
            LIMIT 1
            """,
            new { email, name }, ct);
        return rows.FirstOrDefault()?.Trim();
    }

    private async Task<LinkedPersonProfile?> QuerySingleOrDefaultAsync(
        Guid? tenantId, string? email, string? name, Guid userId, string sql, CancellationToken ct)
    {
        var rows = await QueryInlineAsync<LinkedPersonProfile>(
            sql, new { userId, tenantId, email, name }, ct);
        return rows.FirstOrDefault();
    }
}
