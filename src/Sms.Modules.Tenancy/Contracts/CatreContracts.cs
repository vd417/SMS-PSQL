namespace Sms.Modules.Tenancy.Contracts;

// ---- Client (Tenant) ----
public sealed record ClientLimits(int? Students, int? Staff, int? StorageGb);

public sealed record ClientResponse(
    Guid Id, string Name, string Slug, string? Country, string Status,
    Guid? PlanId, string? PlanName, string? Tier, decimal Mrr,
    // StudentsCount is the BILLED count — the committed/onboarding floor the invoice is charged on
    // (max of live enrolment and the seeded count). EnrolledStudents is the ACTUAL live SIS
    // enrolment shown on the school surface. See SelectLive in ClientRepository.
    int StudentsCount, int StaffCount, decimal StorageGb, ClientLimits Limits,
    DateTime Created, string? Csm, int HealthScore,
    string? ContactName, string? ContactEmail, string? ContactPhone, string? Address,
    string? LogoUrl = null, string? ImageUrl = null,
    string? IdProofType = null, string? IdProofUrl = null,
    int EnrolledStudents = 0, string? BillingCycle = null,
    string? SeatIncreaseMode = null, int? SeatAlertThreshold = null,
    // Education board: "CBSE" | "ICSE" | "State Board" | a custom board name. Null when unset.
    string? Board = null);

/// <summary>Raise a school's billed seat limit (CATRE team action). Only increases are allowed.</summary>
public sealed record IncreaseSeatsRequest(int SeatLimit, string? Reason = null);

/// <summary>CATRE updates a school's seat policy: auto/manual increase mode and the near-limit alert gap.</summary>
public sealed record SeatSettingsRequest(string? SeatIncreaseMode = null, int? SeatAlertThreshold = null);

public sealed record CreateClientRequest(
    string Name, string Slug, string? Country, string? AdminName, string? AdminEmail,
    string? AdminPhone, Guid PlanId, int TrialDays, string? Csm, string? Address = null,
    string? LogoUrl = null, string? ImageUrl = null,
    string? IdProofType = null, string? IdProofUrl = null,
    // Initial student count entered at onboarding. Seeds Tenants.StudentsCount so the first invoice
    // bills on it until live SIS enrolment takes over. Carried for both plan types.
    int? StudentsCount = null,
    // Billing cycle chosen at onboarding: "month" | "quarter" | "year". Defaults to monthly.
    string? BillingCycle = null,
    // Education board: "CBSE" | "ICSE" | "State Board" | a custom board name. Null when unset.
    string? Board = null);

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
    bool SetGeofence = false,
    // Education board: "CBSE" | "ICSE" | "State Board" | a custom board name. Null leaves it unchanged.
    string? Board = null);

public sealed record SetStatusRequest(string? Status, string? Reason);

/// <summary>Hard-delete empty school. Body must confirm with the literal string DELETE.</summary>
public sealed record DeleteClientRequest(string? Confirm);
public sealed record ChangePlanRequest(Guid PlanId);

// Flat DB row (Dapper maps columns by name). Deliberately a parameterless record with settable
// properties rather than a positional one: it is materialised from TWO different column sets — the
// stored procs (Client_Create/SetStatus/ChangePlan/UpdateProfile) and SelectLive, which additionally
// returns EnrolledStudents. Dapper's constructor path demands a single ctor matching every column,
// so a positional record can't serve both; property mapping fills whatever columns each source
// returns and leaves the rest at their defaults. Composed into ClientResponse.
public sealed record ClientRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Country { get; set; }
    public string Status { get; set; } = "";
    public Guid? PlanId { get; set; }
    public string? PlanName { get; set; }
    public string? Tier { get; set; }
    public decimal Mrr { get; set; }
    /// <summary>Billed floor = GREATEST(live enrolment, seeded onboarding count). Billing reads this.</summary>
    public int StudentsCount { get; set; }
    public int StaffCount { get; set; }
    public decimal StorageGb { get; set; }
    public int? LimitsStudents { get; set; }
    public int? LimitsStaff { get; set; }
    public int? LimitsStorageGb { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Csm { get; set; }
    public int HealthScore { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Address { get; set; }
    public string? LogoUrl { get; set; }
    public string? ImageUrl { get; set; }
    public string? IdProofType { get; set; }
    public string? IdProofUrl { get; set; }
    /// <summary>Live active SIS enrolment (display). Only SelectLive returns it; 0 for stored-proc rows.</summary>
    public int EnrolledStudents { get; set; }
    /// <summary>Billing cycle ("month" | "quarter" | "year"). Only SelectLive returns it.</summary>
    public string? BillingCycle { get; set; }
    /// <summary>Seat increase mode ("auto" | "manual"). Only SelectLive returns it.</summary>
    public string? SeatIncreaseMode { get; set; }
    /// <summary>Per-school near-limit alert gap override (null = use plan/default). Only SelectLive returns it.</summary>
    public int? SeatAlertThreshold { get; set; }
    /// <summary>Education board: "CBSE" | "ICSE" | "State Board" | a custom name. Null when unset.</summary>
    public string? Board { get; set; }
}

// ---- Plan ----
public sealed record PlanLimits(int Students, int Staff, int StorageGb);
public sealed record PlanOffer(string Label, int Pct);

public sealed record PlanResponse(
    Guid Id, string Name, string Tier, string Pricing, decimal Price, decimal? PerStudent,
    int? MinStudents, string Period, IReadOnlyList<string> Features, PlanLimits Limits,
    string Visibility, string Audience, string? Band, PlanOffer? Offer, string? Color, string? Description,
    int? IncludedStudents = null, string? AllowedCycles = null, int? SeatAlertThreshold = null);

public sealed record PlanUpsertRequest(
    Guid? Id, string Name, string? Tier, string Pricing, decimal Price, decimal? PerStudent, int? MinStudents,
    string Period, IReadOnlyList<string>? Features, PlanLimits Limits, string Visibility, string Audience,
    string? Band, PlanOffer? Offer, string? Color, string? Description,
    int? IncludedStudents = null, string? AllowedCycles = null, int? SeatAlertThreshold = null);

// Publish/unpublish a plan: visibility is "published" or "draft".
public sealed record PublishPlanRequest(string Visibility);

public sealed record PlanRow(
    Guid Id, string Name, string Tier, string Pricing, decimal Price, decimal? PerStudent, int? MinStudents,
    string Period, string? FeaturesCsv, int LimitsStudents, int LimitsStaff, int LimitsStorageGb,
    string Visibility, string Audience, string? Band, string? OfferLabel, int? OfferPct, string? Color, string? Description,
    int? IncludedStudents = null, string? AllowedCycles = null, int? SeatAlertThreshold = null);

public static class CatreMappers
{
    public static ClientResponse ToResponse(this ClientRow r) => new(
        r.Id, r.Name, r.Slug, r.Country, r.Status, r.PlanId, r.PlanName, r.Tier, r.Mrr,
        r.StudentsCount, r.StaffCount, r.StorageGb,
        new ClientLimits(r.LimitsStudents, r.LimitsStaff, r.LimitsStorageGb),
        r.CreatedAt, r.Csm, r.HealthScore,
        r.ContactName, r.ContactEmail, r.ContactPhone, r.Address, r.LogoUrl, r.ImageUrl,
        r.IdProofType, r.IdProofUrl, r.EnrolledStudents, r.BillingCycle,
        r.SeatIncreaseMode, r.SeatAlertThreshold, r.Board);

    public static PlanResponse ToResponse(this PlanRow r) => new(
        r.Id, r.Name, r.Tier, r.Pricing, r.Price, r.PerStudent, r.MinStudents, r.Period,
        string.IsNullOrEmpty(r.FeaturesCsv) ? [] : r.FeaturesCsv.Split(','),
        new PlanLimits(r.LimitsStudents, r.LimitsStaff, r.LimitsStorageGb),
        r.Visibility, r.Audience, r.Band,
        r.OfferLabel is null ? null : new PlanOffer(r.OfferLabel, r.OfferPct ?? 0),
        r.Color, r.Description, r.IncludedStudents, r.AllowedCycles, r.SeatAlertThreshold);

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

    /// <summary>Number of months covered by one billing cycle. Unknown/null -> monthly.</summary>
    public static int MonthsInCycle(string? cycle) => cycle?.Trim().ToLowerInvariant() switch
    {
        "quarter" => 3,
        "year" => 12,
        _ => 1,
    };

    /// <summary>
    /// Amount billed for one full cycle = monthly amount × months in the cycle.
    /// Per-student plans scale with the seat count; flat plans use the fixed price.
    /// MRR stays the monthly figure (ComputeMonthlyAmount); only the invoice uses this.
    /// </summary>
    public static decimal ComputeCycleAmount(PlanRow plan, int studentsCount, string? cycle, int seats = 0)
        => ComputeMonthlyAmount(plan, studentsCount, seats) * MonthsInCycle(cycle);
}
