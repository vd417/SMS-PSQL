namespace Sms.Modules.Sis.Contracts;

public sealed record StudentResponse(
    Guid Id, Guid TenantId, string AdmissionNo, string Name, string? Gender, string? Grade, string? Section,
    string? ClassLabel, int Roll, string? GuardianName, string? GuardianPhone, string? GuardianEmail,
    decimal? AttendancePct,
    string FeeStatus, decimal FeeDue, string Status, string? House, int AvatarHue,
    DateTime? Dob, string? Email, string? Address, string? PhotoUrl);

public sealed record CreateStudentRequest(
    string? AdmissionNo, string Name, string? Gender, string? Grade, string? Section, int Roll,
    string? GuardianName, string? GuardianPhone, string? GuardianEmail, string? House, int AvatarHue,
    DateTime? Dob, string? Email, string? Address,
    // Guardian relationship to the student ("Mother" | "Father" | "Guardian"); flows to the parent
    // login's ParentStudentLinks."Relationship" so the teacher inbox can show which parent messaged.
    string? GuardianRelation = null);

/// <summary>SetPhoto distinguishes "leave the photo untouched" (SetPhoto=false, the
/// common case for name/grade/roll edits) from "set/clear it" (SetPhoto=true; PhotoUrl
/// null clears it) — a bare nullable PhotoUrl can't express "clear" vs "not provided".</summary>
public sealed record UpdateStudentRequest(
    string? Name, string? Grade, string? Section, int? Roll, string? GuardianName, string? GuardianPhone,
    string? GuardianEmail,
    string? House, string? FeeStatus, decimal? FeeDue, string? Status,
    string? PhotoUrl = null, bool SetPhoto = false,
    string? Gender = null, DateTime? Dob = null, string? Email = null, string? Address = null,
    int? AvatarHue = null, string? GuardianRelation = null);
