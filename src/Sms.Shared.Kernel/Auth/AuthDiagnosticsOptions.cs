namespace Sms.Shared.Kernel.Auth;

/// Development-only auth diagnostics. When LogOtpCodes is true (Development only), OTP and
/// password-setup codes are also written to the log so local sign-in works without live SMTP.
/// This mirrors EmailOtpSender's dev logging for the invite / first-password path, which builds a
/// custom welcome email directly (bypassing EmailOtpSender) and therefore had no dev-log fallback.
/// Never enable outside Development: it writes a live credential to the logs.
public sealed record AuthDiagnosticsOptions(bool LogOtpCodes);
