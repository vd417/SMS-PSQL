namespace Sms.Modules.Tenancy.Contracts;

// ---- Client (Tenant) ----
public sealed record ClientLimits(int? Students, int? Staff, int? StorageGb);

public sealed record ClientResponse(
    Guid Id, string Name, string Slug, string? Country, string Status,
    Guid? PlanId, string? PlanName, string? Tier, decimal Mrr,
    int StudentsCount, int StaffCount, decimal StorageGb, ClientLimits Limits,
    DateTime Created, string? Csm, int HealthScore,
    string? ContactName, string? ContactEmail, string? ContactPhone, string? Address,
    string? LogoUrl = null, string? ImageUrl = null,
    string? IdProofType = null, string? IdProofUrl = null);

public sealed record CreateClientRequest(
    string Name, string Slug, string? Country, string? AdminName, string? AdminEmail,
    string? AdminPhone, Guid PlanId, int TrialDays, string? Csm, string? Address = null,
    string? LogoUrl = null, string? ImageUrl = null,
    string? IdProofType = null, string? IdProofUrl = null);

/// <summary>Partial update of school branding / contact. Null fields are left unchanged; Logo/Image use Set* flags.</summary>
public sealed record UpdateSchoolProfileRequest(
    string? Name = null,
    string? Slug = null,
    string? Country = null,
    string? Address = null,
    string? ContactName = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? LogoUrl = null,
    string? ImageUrl = null,
    bool SetLogo = false,
    bool SetImage = false,
    double? Lat = null,
    double? Lng = null,
    int? GeofenceRadiusMeters = null,
    bool SetGeofence = false);

public sealed record SetStatusRequest(string? Status, string? Reason);

/// <summary>Hard-delete empty school. Body must confirm with the literal string DELETE.</summary>
public sealed record DeleteClientRequest(string? Confirm);
public sealed record ChangePlanRequest(Guid PlanId);

// Flat DB row (Dapper maps columns by name); composed into ClientResponse.
public sealed record ClientRow(
    Guid Id, string Name, string Slug, string? Country, string Status, Guid? PlanId, string? PlanName,
    string? Tier, decimal Mrr, int StudentsCount, int StaffCount, decimal StorageGb,
    int? LimitsStudents, int? LimitsStaff, int? LimitsStorageGb, DateTime CreatedAt, string? Csm, int HealthScore,
    string? ContactName, string? ContactEmail, string? ContactPhone, string? Address,
    string? LogoUrl = null, string? ImageUrl = null,
    string? IdProofType = null, string? IdProofUrl = null);

// ---- Plan ----
public sealed record PlanLimits(int Students, int Staff, int StorageGb);
public sealed record PlanOffer(string Label, int Pct);

public sealed record PlanResponse(
    Guid Id, string Name, string Tier, string Pricing, decimal Price, decimal? PerStudent,
    int? MinStudents, string Period, IReadOnlyList<string> Features, PlanLimits Limits,
    string Visibility, string Audience, string? Band, PlanOffer? Offer, string? Color, string? Description);

public sealed record PlanUpsertRequest(
    Guid? Id, string Name, string? Tier, string Pricing, decimal Price, decimal? PerStudent, int? MinStudents,
    string Period, IReadOnlyList<string>? Features, PlanLimits Limits, string Visibility, string Audience,
    string? Band, PlanOffer? Offer, string? Color, string? Description);

// Publish/unpublish a plan: visibility is "published" or "draft".
public sealed record PublishPlanRequest(string Visibility);

public sealed record PlanRow(
    Guid Id, string Name, string Tier, string Pricing, decimal Price, decimal? PerStudent, int? MinStudents,
    string Period, string? FeaturesCsv, int LimitsStudents, int LimitsStaff, int LimitsStorageGb,
    string Visibility, string Audience, string? Band, string? OfferLabel, int? OfferPct, string? Color, string? Description);

public static class CatreMappers
{
    public static ClientResponse ToResponse(this ClientRow r) => new(
        r.Id, r.Name, r.Slug, r.Country, r.Status, r.PlanId, r.PlanName, r.Tier, r.Mrr,
        r.StudentsCount, r.StaffCount, r.StorageGb,
        new ClientLimits(r.LimitsStudents, r.LimitsStaff, r.LimitsStorageGb),
        r.CreatedAt, r.Csm, r.HealthScore,
        r.ContactName, r.ContactEmail, r.ContactPhone, r.Address, r.LogoUrl, r.ImageUrl,
        r.IdProofType, r.IdProofUrl);

    public static PlanResponse ToResponse(this PlanRow r) => new(
        r.Id, r.Name, r.Tier, r.Pricing, r.Price, r.PerStudent, r.MinStudents, r.Period,
        string.IsNullOrEmpty(r.FeaturesCsv) ? [] : r.FeaturesCsv.Split(','),
        new PlanLimits(r.LimitsStudents, r.LimitsStaff, r.LimitsStorageGb),
        r.Visibility, r.Audience, r.Band,
        r.OfferLabel is null ? null : new PlanOffer(r.OfferLabel, r.OfferPct ?? 0),
        r.Color, r.Description);

    /// <summary>
    /// Monthly bill for a tenant on a plan. Per-student plans: rate × max(students, seats, min_students, 1).
    /// Flat plans: plan.Price.
    /// </summary>
    public static decimal ComputeMonthlyAmount(PlanRow plan, int studentsCount, int seats = 0)
    {
        if (string.Equals(plan.Pricing, "per_student", StringComparison.OrdinalIgnoreCase))
        {
            var rate = plan.PerStudent ?? 0m;
            var min = plan.MinStudents ?? 0;
            var billable = Math.Max(Math.Max(Math.Max(studentsCount, seats), min), 1);
            return rate * billable;
        }
        return plan.Price;
    }

    public static int BillableSeats(PlanRow plan, int studentsCount, int fallbackSeats = 0)
    {
        if (string.Equals(plan.Pricing, "per_student", StringComparison.OrdinalIgnoreCase))
        {
            var min = plan.MinStudents ?? 0;
            return Math.Max(Math.Max(Math.Max(studentsCount, fallbackSeats), min), 1);
        }
        return Math.Max(Math.Max(studentsCount, fallbackSeats), 1);
    }
}
