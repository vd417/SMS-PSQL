using Sms.Modules.Tenancy.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tenancy.Data;

public sealed class ClientRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    /// <summary>
    /// Live SIS counts — denormalized Tenants.StudentsCount/StaffCount stay out of date
    /// unless procs refresh them; portfolio/Catre lists must read live.
    /// </summary>
    private const string SelectLive =
        """
        SELECT t."Id", t."Name", t."Slug", t."Country", t."Status", t."PlanId", t."PlanName", t."Tier", t."Mrr",
        CAST((SELECT count(*) FROM "dbo"."Students" s WHERE s."TenantId" = t."Id" AND s."Status" = 'active') AS int) AS "StudentsCount",
        CAST((
          (SELECT count(*) FROM "dbo"."Teachers" te WHERE te."TenantId" = t."Id" AND te."Status" = 'active') +
          (SELECT count(*) FROM "dbo"."Staff" st WHERE st."TenantId" = t."Id" AND st."Status" = 'active')
        ) AS int) AS "StaffCount",
        t."StorageGb", t."LimitsStudents", t."LimitsStaff", t."LimitsStorageGb", t."CreatedAt", t."Csm", t."HealthScore",
        t."ContactName", t."ContactEmail", t."ContactPhone", t."Address", t."LogoUrl", t."ImageUrl"
        FROM "dbo"."Tenants" t
        """;

    public async Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
    {
        var n = (await QueryInlineAsync<int>(
            """SELECT CASE WHEN EXISTS (SELECT 1 FROM "dbo"."Tenants" WHERE "Slug" = @slug) THEN 1 ELSE 0 END""",
            new { slug }, ct)).FirstOrDefault();
        return n == 1;
    }

    /// <summary>True when another tenant already owns this slug.</summary>
    public async Task<bool> SlugTakenByOtherAsync(Guid id, string slug, CancellationToken ct = default)
    {
        var n = (await QueryInlineAsync<int>(
            """SELECT CASE WHEN EXISTS (SELECT 1 FROM "dbo"."Tenants" WHERE "Slug" = @slug AND "Id" <> @id) THEN 1 ELSE 0 END""",
            new { slug, id }, ct)).FirstOrDefault();
        return n == 1;
    }

    /// <summary>Pick an unused slug; appends -NNNN or a short guid suffix on collision.</summary>
    public async Task<string> AllocateUniqueSlugAsync(string desired, CancellationToken ct = default)
    {
        var baseSlug = string.IsNullOrWhiteSpace(desired) ? "school" : desired.Trim().ToLowerInvariant();
        if (baseSlug.Length > 40) baseSlug = baseSlug[..40];
        baseSlug = baseSlug.Trim('-');
        if (baseSlug.Length == 0) baseSlug = "school";

        var candidate = baseSlug;
        for (var i = 0; i < 24; i++)
        {
            if (!await SlugExistsAsync(candidate, ct)) return candidate;
            candidate = $"{baseSlug}-{Random.Shared.Next(1000, 9999)}";
            if (candidate.Length > 48) candidate = candidate[..48];
        }
        return $"{baseSlug}-{Guid.NewGuid():N}"[..48];
    }

    public Task<ClientRow?> CreateAsync(CreateClientRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<ClientRow>("dbo.Client_Create", new
        {
            r.Name, r.Slug, r.Country,
            ContactName = r.AdminName, ContactEmail = r.AdminEmail, ContactPhone = r.AdminPhone,
            r.Address, r.PlanId, r.Csm, r.LogoUrl, r.ImageUrl
        }, ct);

    public Task<ClientRow?> UpdateProfileAsync(Guid id, UpdateSchoolProfileRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<ClientRow>("dbo.Client_UpdateProfile", new
        {
            Id = id,
            r.Name,
            r.Slug,
            r.Country,
            r.Address,
            r.ContactName,
            r.ContactEmail,
            r.ContactPhone,
            r.LogoUrl,
            r.ImageUrl,
            r.SetLogo,
            r.SetImage,
            r.Lat,
            r.Lng,
            r.GeofenceRadiusMeters,
            r.SetGeofence,
        }, ct);

    public Task<ClientRow?> SetStatusAsync(Guid id, string status, CancellationToken ct = default) =>
        QuerySingleProcAsync<ClientRow>("dbo.Client_SetStatus", new { Id = id, Status = status }, ct);

    public Task<ClientRow?> ChangePlanAsync(Guid id, Guid planId, CancellationToken ct = default) =>
        QuerySingleProcAsync<ClientRow>("dbo.Client_ChangePlan", new { Id = id, PlanId = planId }, ct);

    public async Task<ClientRow?> SetMrrAsync(Guid id, decimal mrr, CancellationToken ct = default)
    {
        await ExecuteInlineAsync("""UPDATE "dbo"."Tenants" SET "Mrr" = @mrr WHERE "Id" = @id""",
            new { id, mrr }, ct);
        return (await QueryInlineAsync<ClientRow>($"{SelectLive} WHERE t.\"Id\" = @id", new { id }, ct))
            .FirstOrDefault();
    }

    public async Task<ClientRow?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<ClientRow>($"{SelectLive} WHERE t.\"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public Task<IReadOnlyList<ClientRow>> ListAsync(
        string? status, string? tier, string? q, CancellationToken ct = default) =>
        QueryInlineAsync<ClientRow>(
            $"{SelectLive} WHERE t.\"PlanId\" IS NOT NULL " +
            "AND (@status IS NULL OR t.\"Status\" = @status) " +
            "AND (@tier IS NULL OR t.\"Tier\" = @tier) " +
            "AND (@q IS NULL OR t.\"Name\" LIKE '%' || @q || '%' OR t.\"Slug\" LIKE '%' || @q || '%') " +
            "ORDER BY t.\"Mrr\" DESC",
            new { status, tier, q }, ct);

    public Task<IReadOnlyList<ClientRow>> GetManyAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        ids.Count == 0
            ? Task.FromResult<IReadOnlyList<ClientRow>>([])
            : QueryInlineAsync<ClientRow>(
                $"{SelectLive} WHERE t.\"Id\" = ANY(@ids) ORDER BY t.\"Mrr\" DESC",
                new { ids }, ct);

    public sealed record DeleteResult(bool Ok, string Code, int Students, int Teachers, int Staff);

    /// <summary>Deletes an empty school (no students/teachers/staff). Returns outcome row from dbo.Client_Delete.</summary>
    public async Task<DeleteResult?> DeleteEmptyAsync(Guid id, CancellationToken ct = default) =>
        (await QuerySingleProcAsync<DeleteResult>("dbo.Client_Delete", new { Id = id }, ct));
}
