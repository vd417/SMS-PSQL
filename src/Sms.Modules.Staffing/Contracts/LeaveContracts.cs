namespace Sms.Modules.Staffing.Contracts;

// Dapper materializes via a constructor matching the EXACT column count of whatever query
// ran — it won't mix a matched-prefix constructor with leftover property setters. Priority
// is now a real, always-present column (default 'medium'), so it's baked into the primary
// 14-param constructor - GetAsync/ListMineAsync/Leave_Create consistently return 14 columns.
// RequesterName/DecidedByName are JOIN-derived (ListByStatusAsync + Leave_Decide, 16 columns),
// so they stay trailing init-only properties with extra constructors, same reasoning as Task 6.
public sealed record LeaveResponse(
    Guid Id, Guid TenantId, Guid? RequesterId, Guid? ChildId, string Type, DateTime? FromDate, DateTime? ToDate,
    string? Reason, string? Substitute, string Status, DateTime? AppliedOn, string? DecidedNote, string Priority,
    string? AttachmentUrls)
{
    public string? RequesterName { get; init; }
    public string? DecidedByName { get; init; }
    public string? RequesterRole { get; init; }
    public string? StudentName { get; init; }
    public string? AdmissionNo { get; init; }
    public string? StudentClass { get; init; }
    public string? StudentSection { get; init; }
    public int? StudentRoll { get; init; }

    public LeaveResponse(
        Guid Id, Guid TenantId, Guid? RequesterId, Guid? ChildId, string Type, DateTime? FromDate, DateTime? ToDate,
        string? Reason, string? Substitute, string Status, DateTime? AppliedOn, string? DecidedNote, string Priority,
        string? AttachmentUrls, string? RequesterName)
        : this(Id, TenantId, RequesterId, ChildId, Type, FromDate, ToDate, Reason, Substitute, Status, AppliedOn, DecidedNote, Priority, AttachmentUrls) =>
        this.RequesterName = RequesterName;

    public LeaveResponse(
        Guid Id, Guid TenantId, Guid? RequesterId, Guid? ChildId, string Type, DateTime? FromDate, DateTime? ToDate,
        string? Reason, string? Substitute, string Status, DateTime? AppliedOn, string? DecidedNote, string Priority,
        string? AttachmentUrls, string? RequesterName, string? DecidedByName)
        : this(Id, TenantId, RequesterId, ChildId, Type, FromDate, ToDate, Reason, Substitute, Status, AppliedOn, DecidedNote, Priority, AttachmentUrls, RequesterName) =>
        this.DecidedByName = DecidedByName;

    // ListByStatusAsync (17 columns: 14 base + RequesterName + DecidedByName + RequesterRole) —
    // Dapper needs an exact-arity constructor match, same reasoning as the two above.
    public LeaveResponse(
        Guid Id, Guid TenantId, Guid? RequesterId, Guid? ChildId, string Type, DateTime? FromDate, DateTime? ToDate,
        string? Reason, string? Substitute, string Status, DateTime? AppliedOn, string? DecidedNote, string Priority,
        string? AttachmentUrls, string? RequesterName, string? DecidedByName, string? RequesterRole)
        : this(Id, TenantId, RequesterId, ChildId, Type, FromDate, ToDate, Reason, Substitute, Status, AppliedOn, DecidedNote, Priority, AttachmentUrls, RequesterName, DecidedByName) =>
        this.RequesterRole = RequesterRole;

    // ListByStatusAsync with the Students join (22 columns: 17 above + 5 student fields).
    // Dapper needs an exact-arity constructor match, same reasoning as the constructors above.
    public LeaveResponse(
        Guid Id, Guid TenantId, Guid? RequesterId, Guid? ChildId, string Type, DateTime? FromDate, DateTime? ToDate,
        string? Reason, string? Substitute, string Status, DateTime? AppliedOn, string? DecidedNote, string Priority,
        string? AttachmentUrls, string? RequesterName, string? DecidedByName, string? RequesterRole,
        string? StudentName, string? AdmissionNo, string? StudentClass, string? StudentSection, int? StudentRoll)
        : this(Id, TenantId, RequesterId, ChildId, Type, FromDate, ToDate, Reason, Substitute, Status, AppliedOn, DecidedNote, Priority, AttachmentUrls, RequesterName, DecidedByName, RequesterRole)
    {
        this.StudentName = StudentName;
        this.AdmissionNo = AdmissionNo;
        this.StudentClass = StudentClass;
        this.StudentSection = StudentSection;
        this.StudentRoll = StudentRoll;
    }
}

public sealed record LeaveBalanceResponse(string Type, int Total, int Used);

public sealed record CreateLeaveRequest(
    string Type, DateTime? FromDate, DateTime? ToDate, string? Reason, string? Substitute, Guid? ChildId,
    string? Priority = null, IReadOnlyList<string>? AttachmentUrls = null);

public sealed record DecideLeaveRequest(string Status, string? DecidedNote);
