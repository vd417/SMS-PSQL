using Sms.Shared.Kernel.Auth;

namespace Sms.Application.Interfaces.DAO;

public sealed record RosterStudentRecord(
    Guid Id,
    Guid TenantId,
    string AdmissionNo,
    string Name,
    string? Email,
    string? GuardianPhone,
    string Status,
    string? GuardianEmail = null);

public interface IAuthDao
{
    Task<UserRecord?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<UserRecord?> GetByPhoneAsync(string phone, CancellationToken ct = default);
    Task<UserRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<UserRecord>> ListByEmailAsync(string email, CancellationToken ct = default);
    Task<IReadOnlyList<UserRecord>> ListByPhoneAsync(string phone, CancellationToken ct = default);
    Task<IReadOnlyList<UserRecord>> ListByAdmissionIdAsync(string admissionId, CancellationToken ct = default);
    /// <summary>SIS roster email (Students.Email) for an admission number — source of truth for student OTP.</summary>
    Task<string?> GetRosterEmailByAdmissionIdAsync(string admissionId, CancellationToken ct = default);
    /// <summary>SIS roster row for an admission number (trim + case-insensitive).</summary>
    Task<RosterStudentRecord?> GetRosterByAdmissionIdAsync(string admissionId, CancellationToken ct = default);
    /// <summary>SIS roster row by Students.Email (trim + case-insensitive).</summary>
    Task<RosterStudentRecord?> GetRosterByEmailAsync(string email, CancellationToken ct = default);
    /// <summary>SIS roster row by Students.GuardianEmail (trim + case-insensitive).</summary>
    Task<RosterStudentRecord?> GetRosterByGuardianEmailAsync(string email, CancellationToken ct = default);
    /// <summary>Fetch the student login for an admission ID, creating it from the SIS roster when missing.</summary>
    Task<UserRecord?> EnsureStudentLoginAsync(string admissionId, CancellationToken ct = default);
    /// <summary>Fetch or create the parent login for an admission ID from Students.GuardianEmail / GuardianPhone.</summary>
    Task<UserRecord?> EnsureParentLoginAsync(string admissionId, CancellationToken ct = default);
    /// <summary>Fetch the staff login for an email, creating it from dbo.Staff.Email when missing (no invite needed).</summary>
    Task<UserRecord?> EnsureStaffLoginAsync(string email, CancellationToken ct = default);
    /// <summary>Fetch the teacher login for an email, creating it from dbo.Teachers.Email when missing (no invite needed; invite is reserved for CRM roles).</summary>
    Task<UserRecord?> EnsureTeacherLoginAsync(string email, CancellationToken ct = default);
    Task<UserRecord?> GetByEmailAndTenantAsync(string email, Guid tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken ct = default);
    Task SetPasswordAsync(Guid userId, string passwordHash, CancellationToken ct = default);
    Task SetPhotoAsync(Guid userId, string? photoUrl, CancellationToken ct = default);
    Task SetPhoneAsync(Guid userId, string? phone, CancellationToken ct = default);
    Task SetEmailAsync(Guid userId, string? email, CancellationToken ct = default);
    Task OtpInsertAsync(string identifier, string channel, string codeHash, DateTime expiresAt, CancellationToken ct = default);
    Task<string?> OtpActiveHashAsync(string identifier, CancellationToken ct = default);
    /// True if this call actually consumed the OTP row (false = already consumed/expired by a
    /// concurrent request, which the caller must treat as a failed verification, not a success).
    Task<bool> OtpConsumeAsync(string identifier, string codeHash, CancellationToken ct = default);
    Task OtpConsumeAllAsync(string identifier, CancellationToken ct = default);
}
