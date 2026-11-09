namespace Sms.Application.DTOs.Auth;

public sealed record LoginRequest(
    string? Email, string? Password, string? StudentId, string? Phone, string? Role, Guid? TenantId);

public sealed record TokenResponse(string AccessToken, string RefreshToken);
public sealed record RefreshRequest(string RefreshToken);
public sealed record OtpRequest(string Identifier);
public sealed record OtpVerifyRequest(string Identifier, string Code, string? Role = null, Guid? OrganizationId = null);
/// Returned only when a parent OTP matches more than one active school. No access token yet.
public sealed record ParentOrganizationChoice(Guid Id, string Name);
public sealed record ParentOtpSelectionResponse(
    bool SelectionRequired,
    IReadOnlyList<ParentOrganizationChoice> Organizations);
public sealed record SetPasswordRequest(string Password);
public sealed record UpdatePhotoRequest(string? PhotoUrl);
public sealed record ForgotPasswordRequest(string Identifier, string? Role = null);
public sealed record ResetPasswordRequest(string Identifier, string? Code, string? Password);
