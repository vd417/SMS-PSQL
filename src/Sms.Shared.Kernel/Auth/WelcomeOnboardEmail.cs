using System.Net;

namespace Sms.Shared.Kernel.Auth;

/// Onboarding welcome email sent when Catre creates a school/client. Unlike
/// <see cref="InviteWelcomeEmail"/>, this message carries NO one-time code — the owner
/// sets their password themselves from the panel's "Create a password" flow, where the
/// OTP lives. This keeps the welcome a clean, warm greeting with a clear next step.
public static class WelcomeOnboardEmail
{
    private const string Brand = "#4f46e5";

    /// <param name="loginUrl">The SchoolMate panel URL the owner opens to set their password.</param>
    /// <param name="roleLabel">Optional role shown as a badge (e.g. "Owner").</param>
    public static EmailMessage Build(string to, string schoolName, string loginUrl, string? roleLabel = null)
    {
        var school = string.IsNullOrWhiteSpace(schoolName) ? "your school" : schoolName.Trim();
        var roleBit = string.IsNullOrWhiteSpace(roleLabel) ? "" : $" as {roleLabel.Trim()}";
        var subject = $"Welcome to {school} on SchoolMate";
        var body =
            "Hello,\n\n" +
            $"You've been onboarded to {school} on SchoolMate{roleBit}. We're glad to have you.\n\n" +
            "To get started, set your password:\n" +
            "1. Open SchoolMate\n" +
            $"   {loginUrl}\n" +
            "2. Choose \"Create a password\"\n" +
            "3. Enter your email — we'll send you a one-time code\n" +
            "4. Create your password and sign in\n\n" +
            $"If you didn't expect this invite for {school}, you can ignore this email.\n\n" +
            $"— {school} via SchoolMate";

        return new EmailMessage(to, subject, body, HtmlBody: BuildHtml(school, loginUrl, roleLabel));
    }

    public static string SmsBody(string schoolName, string loginUrl, string? roleLabel = null)
    {
        var school = string.IsNullOrWhiteSpace(schoolName) ? "your school" : schoolName.Trim();
        var roleBit = string.IsNullOrWhiteSpace(roleLabel) ? "" : $" ({roleLabel.Trim()})";
        return $"Welcome to {school}{roleBit} on SchoolMate. Open {loginUrl} and choose \"Create a password\" to get started.";
    }

    /// Inline-styled HTML — inline CSS is required since most mail clients strip &lt;style&gt;
    /// blocks. School/role are user-controlled tenant data, so they're HTML-encoded.
    private static string BuildHtml(string school, string loginUrl, string? roleLabel)
    {
        var schoolSafe = WebEncode(school);
        var urlSafe = WebEncode(loginUrl);
        var roleBadge = string.IsNullOrWhiteSpace(roleLabel)
            ? ""
            : $"""<span style="display:inline-block;padding:3px 10px;border-radius:999px;background:#eef2ff;color:{Brand};font-size:12px;font-weight:600;">{WebEncode(roleLabel!.Trim())}</span>""";

        return $"""
            <div style="background:#f1f5f9;padding:32px 16px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Arial,sans-serif;">
              <div style="max-width:440px;margin:0 auto;background:#ffffff;border-radius:14px;overflow:hidden;border:1px solid #e2e8f0;">
                <div style="background:{Brand};padding:22px 28px;">
                  <span style="color:#ffffff;font-size:17px;font-weight:700;letter-spacing:.2px;">SchoolMate</span>
                </div>
                <div style="padding:28px;">
                  <h1 style="margin:0 0 6px;font-size:19px;color:#0f172a;">Welcome to {schoolSafe} 🎉</h1>
                  <p style="margin:0 0 20px;font-size:14px;color:#475569;">
                    You've been onboarded to <strong>{schoolSafe}</strong> on SchoolMate{(roleBadge == "" ? "." : " —")} {roleBadge}
                  </p>
                  <div style="text-align:center;margin:8px 0 16px;">
                    <a href="{urlSafe}" style="display:inline-block;background:{Brand};color:#ffffff;text-decoration:none;font-weight:600;font-size:15px;padding:13px 28px;border-radius:8px;">Open SchoolMate</a>
                  </div>
                  <p style="margin:0 0 8px;font-size:14px;color:#475569;">To get started, set your password:</p>
                  <ol style="margin:0 0 4px;padding-left:20px;font-size:14px;color:#475569;line-height:1.7;">
                    <li>Open SchoolMate with the button above</li>
                    <li>Choose <strong>"Create a password"</strong></li>
                    <li>Enter your email — we'll send a one-time code</li>
                    <li>Create your password and sign in</li>
                  </ol>
                  <p style="margin:20px 0 0;font-size:12px;color:#94a3b8;">
                    Didn't expect this invite for {schoolSafe}? You can safely ignore this email.
                  </p>
                </div>
              </div>
              <p style="text-align:center;font-size:11px;color:#94a3b8;margin-top:16px;">— {schoolSafe} via SchoolMate</p>
            </div>
            """;
    }

    private static string WebEncode(string s) => WebUtility.HtmlEncode(s);
}
