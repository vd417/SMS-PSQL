namespace Sms.Modules.Staffing.Contracts;

// ---- Teacher ----
public sealed record TeacherResponse(
    Guid Id, Guid TenantId, string Name, string? Gender, string? Department, string? Designation,
    IReadOnlyList<string> Subjects, string? ClassTeacher, string? Phone, string? Email, int Exp, decimal Rating,
    decimal? AttendancePct, decimal Result, int Load, string Status, int AvatarHue, bool Top,
    string? EmployeeCode = null, string? PhotoUrl = null, Guid? UserId = null);

public sealed record TeacherRow(
    Guid Id, Guid TenantId, string Name, string? Gender, string? Department, string? Designation,
    string? SubjectsCsv, string? ClassTeacher, string? Phone, string? Email, int Exp, decimal Rating,
    decimal? AttendancePct, decimal Result, int Load, string Status, int AvatarHue, bool Top,
    string? EmployeeCode = null, string? PhotoUrl = null, Guid? UserId = null);

public sealed record CreateTeacherRequest(
    string Name, string? Gender, string? Department, string? Designation, IReadOnlyList<string>? Subjects,
    string? ClassTeacher, string? Phone, string? Email, int Exp, decimal Rating, decimal Result, int Load,
    int AvatarHue, bool Top, string? EmployeeCode = null);

/// <summary>SetPhoto distinguishes "leave the photo untouched" (SetPhoto=false, the
/// common case for other field edits) from "set/clear it" (SetPhoto=true; PhotoUrl
/// null clears it) — see Students.PhotoUrl for the identical pattern. Written through
/// to the linked Users row (Users.PhotoUrl is the single source of truth read by
/// GET /auth/me); fails with `no_linked_user` if this teacher has no UserId yet
/// (not yet invited/accepted).</summary>
public sealed record UpdateTeacherRequest(
    string? Name, string? Department, string? Designation, IReadOnlyList<string>? Subjects,
    string? ClassTeacher, string? Phone, string? Email, string? Status,
    string? PhotoUrl = null, bool SetPhoto = false,
    string? Gender = null, int? Exp = null, string? EmployeeCode = null);

// ---- Staff ----
public sealed record StaffResponse(
    Guid Id, Guid TenantId, string Name, string? Gender, string? Role, string? Category, string? Department,
    string? Phone, string? Shift, string? Route, decimal? AttendancePct, string Status, int AvatarHue,
    string? EmployeeCode = null, string? Email = null, string? PhotoUrl = null, Guid? UserId = null)
{
    /// Dapper maps `Staff_Create` from a SELECT that stops at PhotoUrl. Optional `UserId` on the
    /// primary constructor still produces a 17-parameter ctor, which Dapper will not match.
    public StaffResponse(
        Guid Id, Guid TenantId, string Name, string? Gender, string? Role, string? Category, string? Department,
        string? Phone, string? Shift, string? Route, decimal? AttendancePct, string Status, int AvatarHue,
        string? EmployeeCode, string? Email, string? PhotoUrl)
        : this(Id, TenantId, Name, Gender, Role, Category, Department, Phone, Shift, Route, AttendancePct,
            Status, AvatarHue, EmployeeCode, Email, PhotoUrl, null)
    {
    }
}

public sealed record CreateStaffRequest(
    string Name, string? Gender, string? Role, string? Category, string? Department, string? Phone,
    string? Shift, string? Route, int AvatarHue, string? EmployeeCode = null, string? Email = null);

/// <summary>SetPhoto/PhotoUrl: same pattern as UpdateTeacherRequest, above. Email is
/// written through to the linked Users row by StaffingService — see UpdateTeacherRequest.</summary>
public sealed record UpdateStaffRequest(
    string? Name, string? Role, string? Category, string? Department, string? Phone, string? Shift,
    string? Route, string? Status, string? PhotoUrl = null, bool SetPhoto = false, string? Email = null,
    string? Gender = null, string? EmployeeCode = null);

public static class StaffingMappers
{
    public static TeacherResponse ToResponse(this TeacherRow r) => new(
        r.Id, r.TenantId, r.Name, r.Gender, r.Department, r.Designation,
        string.IsNullOrEmpty(r.SubjectsCsv) ? [] : r.SubjectsCsv.Split(','),
        r.ClassTeacher, r.Phone, r.Email, r.Exp, r.Rating, r.AttendancePct, r.Result, r.Load,
        r.Status, r.AvatarHue, r.Top, r.EmployeeCode, r.PhotoUrl, r.UserId);
}
