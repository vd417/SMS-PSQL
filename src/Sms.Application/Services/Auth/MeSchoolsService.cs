using Npgsql;
using Sms.Application.Common;
using Sms.Application.DTOs.Auth;
using Sms.Application.Interfaces.DAO;
using Sms.Modules.Finance;
using Sms.Modules.Tenancy.Contracts;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Auth;

public interface IMeSchoolsService
{
    Task<IReadOnlyList<ClientResponse>> ListAsync(CancellationToken ct = default);
    Task<ApiResult<ClientResponse>> CreateAsync(CreateMySchoolRequest req, CancellationToken ct = default);
    Task<ApiResult<ClientResponse>> UpdateAsync(Guid tenantId, UpdateSchoolProfileRequest req, CancellationToken ct = default);
    Task<ApiResult> DeleteAsync(Guid tenantId, DeleteClientRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<PlanResponse>> ListPublishedPlansAsync(CancellationToken ct = default);
    Task<ApiResult<TokenResponse>> SwitchTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<FeeSummaryResponse> FeeSummaryAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);
}

public sealed record FeeSchoolSummary(
    Guid TenantId, string Name, decimal Collected, decimal Outstanding, int PaymentCount, int InvoiceCount);

public sealed record FeeSummaryTotals(decimal Collected, decimal Outstanding, int PaymentCount, int InvoiceCount);

public sealed record FeeSummaryPeriod(DateOnly From, DateOnly To);

public sealed record FeeSummaryResponse(
    FeeSummaryPeriod Period,
    IReadOnlyList<FeeSchoolSummary> Schools,
    FeeSummaryTotals Totals);

public sealed record CreateMySchoolRequest(
    string Name, string Slug, string? Country, Guid PlanId,
    string? AdminName, string? AdminPhone, string? Address, int TrialDays = 14,
    string? LogoUrl = null, string? ImageUrl = null);

/// <summary>
/// School-owner portfolio: list/create schools for the signed-in founding owner
/// (same email across tenant user rows) without platform privileges.
/// </summary>
public sealed class MeSchoolsService(
    ITenantContext tenant,
    IAuthDao auth,
    IProfileDao profiles,
    IUserProvisioningDao provisioning,
    ClientRepository clients,
    PlanRepository plans,
    OnboardingRepository onboarding,
    FeeInvoiceRepository feeInvoices,
    IJwtTokenService jwt,
    IRefreshTokenStore tokens) : IMeSchoolsService
{
    public async Task<IReadOnlyList<ClientResponse>> ListAsync(CancellationToken ct = default)
    {
        if (tenant.IsPlatform)
            return (await clients.ListAsync(null, null, null, ct)).Select(r => r.ToResponse()).ToList();

        if (tenant.UserId is not { } uid)
            return [];

        var me = await auth.GetByIdAsync(uid, ct);
        if (me?.Email is null)
            return [];

        // Elevate for cross-tenant reads (Tenants is not RLS-scoped the same way under platform).
        tenant.Set(null, uid, isPlatform: true);
        try
        {
            var peers = await auth.ListByEmailAsync(me.Email, ct);
            var ids = peers
                .Where(u => u.TenantId is not null && !u.IsPlatform)
                .Select(u => u.TenantId!.Value)
                .Distinct()
                .ToList();
            if (ids.Count == 0 && me.TenantId is { } only)
                ids.Add(only);
            if (ids.Count == 0) return [];
            var rows = await clients.GetManyAsync(ids, ct);
            return rows.Select(r => r.ToResponse()).ToList();
        }
        finally
        {
            tenant.Set(me.TenantId, uid, isPlatform: false);
        }
    }

    public async Task<ApiResult<ClientResponse>> CreateAsync(CreateMySchoolRequest req, CancellationToken ct = default)
    {
        if (tenant.IsPlatform)
            return ApiResult<ClientResponse>.Fail(new Error("forbidden", "Platform operators must use POST /clients."), 403);
        if (tenant.UserId is not { } uid)
            return ApiResult<ClientResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);

        var roles = await auth.GetRolesAsync(uid, ct);
        if (!roles.Contains(Policies.SchoolOwner) && !roles.Contains(Policies.SchoolAdmin))
            return ApiResult<ClientResponse>.Fail(new Error("forbidden", "Only a school owner can add schools."), 403);

        var me = await auth.GetByIdAsync(uid, ct);
        if (me?.Email is null)
            return ApiResult<ClientResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Slug))
            return ApiResult<ClientResponse>.Fail(new Error("invalid_request", "name and slug are required"), 422);

        tenant.Set(null, uid, isPlatform: true);
        try
        {
            var slug = await clients.AllocateUniqueSlugAsync(req.Slug, ct);
            var create = new CreateClientRequest(
                req.Name.Trim(), slug, req.Country,
                req.AdminName ?? me.Email.Split('@')[0], me.Email, req.AdminPhone ?? me.Phone,
                req.PlanId, req.TrialDays <= 0 ? 14 : req.TrialDays, null, req.Address, req.LogoUrl, req.ImageUrl);

            ClientRow? row;
            try
            {
                row = await clients.CreateAsync(create, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return ApiResult<ClientResponse>.Fail(
                    new Error("slug_taken", "A school with this name/slug already exists. Try a different school name."),
                    409);
            }

            if (row is null)
                return ApiResult<ClientResponse>.Fail(new Error("internal_error", "could not create school"), 500);

            var newUserId = await provisioning.CreateUserAsync(
                row.Id, me.Email, req.AdminPhone ?? me.Phone, false, [Policies.SchoolOwner], ct);
            if (me.PasswordHash is not null)
                await auth.SetPasswordAsync(newUserId, me.PasswordHash, ct);

            await onboarding.CreateAsync(new CreateOnboardingRequest(
                row.Name, row.Slug, row.Csm, row.Mrr, "trial",
                create.AdminName, create.AdminEmail, create.AdminPhone, create.Address, row.Id), ct);

            return ApiResult<ClientResponse>.Ok(row.ToResponse(), 201);
        }
        finally
        {
            tenant.Set(me.TenantId, uid, isPlatform: false);
        }
    }

    public async Task<ApiResult<ClientResponse>> UpdateAsync(
        Guid tenantId, UpdateSchoolProfileRequest req, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } uid)
            return ApiResult<ClientResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);

        var me = await auth.GetByIdAsync(uid, ct);
        if (me is null)
            return ApiResult<ClientResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);

        var roles = await auth.GetRolesAsync(uid, ct);
        var canEdit = tenant.IsPlatform
            || roles.Contains(Policies.SchoolOwner)
            || roles.Contains(Policies.SchoolAdmin);

        if (!canEdit)
            return ApiResult<ClientResponse>.Fail(new Error("forbidden", "Only school owners or admins can edit school profile."), 403);

        var savedTid = tenant.TenantId;
        var wasPlatform = tenant.IsPlatform;
        tenant.Set(null, uid, isPlatform: true);
        try
        {
            if (!wasPlatform)
            {
                if (me.Email is null)
                    return ApiResult<ClientResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);
                var owned = await auth.GetByEmailAndTenantAsync(me.Email, tenantId, ct);
                if (owned is null)
                    return ApiResult<ClientResponse>.Fail(new Error("forbidden", "You do not own that school."), 403);
            }

            if (string.IsNullOrWhiteSpace(req.Name)
                && string.IsNullOrWhiteSpace(req.Slug)
                && string.IsNullOrWhiteSpace(req.Country)
                && string.IsNullOrWhiteSpace(req.Address)
                && string.IsNullOrWhiteSpace(req.ContactName)
                && string.IsNullOrWhiteSpace(req.ContactEmail)
                && string.IsNullOrWhiteSpace(req.ContactPhone)
                && !req.SetLogo
                && !req.SetImage
                && !req.SetGeofence)
            {
                return ApiResult<ClientResponse>.Fail(new Error("invalid_request", "No profile fields to update."), 422);
            }

            string? slug = null;
            if (!string.IsNullOrWhiteSpace(req.Slug))
            {
                slug = NormalizeSchoolSlug(req.Slug);
                if (slug is null)
                    return ApiResult<ClientResponse>.Fail(
                        new Error("invalid_request", "school id must be 3–40 characters: letters, numbers, hyphens"), 422);
                if (await clients.SlugTakenByOtherAsync(tenantId, slug, ct))
                    return ApiResult<ClientResponse>.Fail(
                        new Error("slug_taken", "That school id is already in use. Choose another."), 409);
            }

            var patch = req with
            {
                Name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim(),
                Slug = slug,
                Country = string.IsNullOrWhiteSpace(req.Country) ? null : req.Country.Trim(),
                Address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim(),
                ContactName = string.IsNullOrWhiteSpace(req.ContactName) ? null : req.ContactName.Trim(),
                ContactEmail = string.IsNullOrWhiteSpace(req.ContactEmail) ? null : req.ContactEmail.Trim(),
                ContactPhone = string.IsNullOrWhiteSpace(req.ContactPhone) ? null : req.ContactPhone.Trim(),
            };

            try
            {
                var row = await clients.UpdateProfileAsync(tenantId, patch, ct);
                if (row is null)
                    return ApiResult<ClientResponse>.Fail(new Error("not_found", "school not found"), 404);

                return ApiResult<ClientResponse>.Ok(row.ToResponse());
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return ApiResult<ClientResponse>.Fail(
                    new Error("slug_taken", "That school id is already in use. Choose another."), 409);
            }
        }
        finally
        {
            tenant.Set(savedTid, uid, wasPlatform);
        }
    }

    public async Task<ApiResult> DeleteAsync(Guid tenantId, DeleteClientRequest req, CancellationToken ct = default)
    {
        if (tenant.IsPlatform)
            return ApiResult.Fail(new Error("forbidden", "Platform operators must use DELETE /clients/{id}."), 403);
        if (tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("unauthorized", "unauthorized"), 401);
        if (!string.Equals(req.Confirm?.Trim(), "DELETE", StringComparison.Ordinal))
            return ApiResult.Fail(new Error("invalid_request", "confirm must be DELETE"), 422);

        var me = await auth.GetByIdAsync(uid, ct);
        if (me?.Email is null)
            return ApiResult.Fail(new Error("unauthorized", "unauthorized"), 401);

        tenant.Set(null, uid, isPlatform: true);
        try
        {
            var owned = await auth.GetByEmailAndTenantAsync(me.Email, tenantId, ct);
            if (owned is null)
                return ApiResult.Fail(new Error("forbidden", "You do not own that school."), 403);

            var result = await clients.DeleteEmptyAsync(tenantId, ct);
            if (result is null)
                return ApiResult.Fail(new Error("internal_error", "delete failed"), 500);

            if (!result.Ok)
            {
                if (string.Equals(result.Code, "not_found", StringComparison.OrdinalIgnoreCase))
                    return ApiResult.Fail(new Error("not_found", "school not found"), 404);
                if (string.Equals(result.Code, "has_people", StringComparison.OrdinalIgnoreCase))
                    return ApiResult.Fail(new Error("conflict",
                        $"Cannot delete: school has {result.Students} student(s) and {result.Teachers + result.Staff} staff/teacher(s). Remove them first."), 409);
                return ApiResult.Fail(new Error("conflict", "cannot delete school"), 409);
            }

            return ApiResult.NoContent();
        }
        finally
        {
            tenant.Set(me.TenantId, uid, isPlatform: false);
        }
    }

    public async Task<IReadOnlyList<PlanResponse>> ListPublishedPlansAsync(CancellationToken ct = default)
    {
        var uid = tenant.UserId;
        var wasPlatform = tenant.IsPlatform;
        var tid = tenant.TenantId;
        tenant.Set(null, uid, isPlatform: true);
        try
        {
            // Catre has used both "published" and "public" for live plans; exclude drafts only.
            var rows = await plans.ListAsync(null, null, ct);
            return rows
                .Where(r => IsLivePlanVisibility(r.Visibility))
                .Select(r => r.ToResponse())
                .ToList();
        }
        finally
        {
            tenant.Set(tid, uid, wasPlatform);
        }
    }

    private static bool IsLivePlanVisibility(string? visibility)
    {
        if (string.IsNullOrWhiteSpace(visibility)) return false;
        var v = visibility.Trim();
        return v.Equals("published", StringComparison.OrdinalIgnoreCase)
            || v.Equals("public", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ApiResult<TokenResponse>> SwitchTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenant.IsPlatform)
            return ApiResult<TokenResponse>.Fail(new Error("forbidden", "Platform sessions cannot switch school tenants."), 403);
        if (tenant.UserId is not { } uid)
            return ApiResult<TokenResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);

        var me = await auth.GetByIdAsync(uid, ct);
        if (me?.Email is null)
            return ApiResult<TokenResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);

        tenant.Set(null, uid, isPlatform: true);
        try
        {
            var target = await auth.GetByEmailAndTenantAsync(me.Email, tenantId, ct);
            if (target is null)
                return ApiResult<TokenResponse>.Fail(new Error("forbidden", "You do not own that school."), 403);

            await UserPhotoSync.EnsureTargetHasPhotoAsync(auth, me, target, ct);
            await UserContactSync.EnsureTargetHasContactAsync(auth, profiles, me, target, ct);

            var roles = await auth.GetRolesAsync(target.Id, ct);
            var access = jwt.IssueAccess(target.Id, target.TenantId, roles, target.IsPlatform);
            var refresh = jwt.NewRefreshToken();
            await tokens.SaveAsync(target.Id, Sha256(refresh), DateTime.UtcNow.AddDays(30), ct);
            return ApiResult<TokenResponse>.Ok(new TokenResponse(access, refresh));
        }
        finally
        {
            tenant.Set(me.TenantId, uid, isPlatform: false);
        }
    }

    public async Task<FeeSummaryResponse> FeeSummaryAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Rolling trailing window, not calendar month-to-date — MTD collapses to a single
        // empty day right after month rollover and hides real recent collections.
        var periodFrom = from ?? today.AddDays(-30);
        var periodTo = to ?? today;
        if (periodTo < periodFrom)
            (periodFrom, periodTo) = (periodTo, periodFrom);

        var empty = new FeeSummaryResponse(
            new FeeSummaryPeriod(periodFrom, periodTo),
            [],
            new FeeSummaryTotals(0, 0, 0, 0));

        List<Guid> ids;
        if (tenant.IsPlatform)
        {
            ids = (await clients.ListAsync(null, null, null, ct)).Select(r => r.Id).ToList();
        }
        else
        {
            if (tenant.UserId is not { } uid)
                return empty;
            var me = await auth.GetByIdAsync(uid, ct);
            if (me?.Email is null)
                return empty;

            var prevTenant = me.TenantId;
            tenant.Set(null, uid, isPlatform: true);
            try
            {
                var peers = await auth.ListByEmailAsync(me.Email, ct);
                ids = peers
                    .Where(u => u.TenantId is not null && !u.IsPlatform)
                    .Select(u => u.TenantId!.Value)
                    .Distinct()
                    .ToList();
                if (ids.Count == 0 && me.TenantId is { } only)
                    ids.Add(only);
                if (ids.Count == 0)
                    return empty;

                var rows = await feeInvoices.SummarizeByTenantsAsync(ids, periodFrom, periodTo, ct);
                return ToFeeSummary(periodFrom, periodTo, rows);
            }
            finally
            {
                tenant.Set(prevTenant, uid, isPlatform: false);
            }
        }

        if (ids.Count == 0)
            return empty;
        var platformRows = await feeInvoices.SummarizeByTenantsAsync(ids, periodFrom, periodTo, ct);
        return ToFeeSummary(periodFrom, periodTo, platformRows);
    }

    private static FeeSummaryResponse ToFeeSummary(
        DateOnly from, DateOnly to, IReadOnlyList<FeeTenantSummaryRow> rows)
    {
        var schools = rows.Select(r => new FeeSchoolSummary(
            r.TenantId, r.Name, r.Collected, r.Outstanding, r.PaymentCount, r.InvoiceCount)).ToList();
        var totals = new FeeSummaryTotals(
            schools.Sum(s => s.Collected),
            schools.Sum(s => s.Outstanding),
            schools.Sum(s => s.PaymentCount),
            schools.Sum(s => s.InvoiceCount));
        return new FeeSummaryResponse(new FeeSummaryPeriod(from, to), schools, totals);
    }

    private static string Sha256(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Lowercase slug; null when invalid.</summary>
    private static string? NormalizeSchoolSlug(string raw)
    {
        var slug = raw.Trim().ToLowerInvariant();
        if (slug.Length is < 3 or > 40) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(slug, @"^[a-z0-9]+(?:-[a-z0-9]+)*$")) return null;
        return slug;
    }
}
