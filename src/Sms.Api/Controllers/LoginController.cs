using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Sms.Application.DTOs.Auth;
using Sms.Application.Services.Auth;

namespace Sms.Api.Controllers;

// The strict per-IP "auth" limiter is applied ONLY to the anonymous, brute-forceable
// credential endpoints below. Authenticated lifecycle calls (/me, /refresh, logout,
// set-password, photo) are intentionally left on the global per-user limiter so that
// normal app startup + token refresh (and many users behind one school/NAT IP) are not
// throttled by the small login bucket.
[Route("v1/auth")]
public sealed class LoginController(IAuthService auth) : ApiControllerBase
{
    // Which front-end made the call ("crm" / "sms-admin" web panels, or absent for the mobile
    // app). Drives per-client session length in AuthService (web = short, 1h idle; app = long).
    private string? Client => Request.Headers["X-Client"].ToString() is { Length: > 0 } c ? c : null;

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct) =>
        FromResult(await auth.LoginAsync(req, Client, ct));

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest req, CancellationToken ct) =>
        FromResult(await auth.RefreshAsync(req, Client, ct));

    [HttpPost("otp/request")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> RequestOtp([FromBody] OtpRequest req, CancellationToken ct) =>
        FromResult(await auth.RequestOtpAsync(req, ct));

    [HttpPost("otp/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> VerifyOtp([FromBody] OtpVerifyRequest req, CancellationToken ct) =>
        FromResult(await auth.VerifyOtpAsync(req, Client, ct));

    [HttpPost("password/forgot")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req, CancellationToken ct) =>
        FromResult(await auth.ForgotPasswordAsync(req, ct));

    [HttpPost("password/reset")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req, CancellationToken ct) =>
        FromResult(await auth.ResetPasswordAsync(req, ct));

    [HttpPost("set-password")]
    [Authorize]
    public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest req, CancellationToken ct) =>
        FromResult(await auth.SetPasswordAsync(req, ct));

    [HttpPatch("~/v1/me/photo")]
    [Authorize]
    public async Task<IActionResult> UpdatePhoto([FromBody] UpdatePhotoRequest req, CancellationToken ct) =>
        FromResult(await auth.UpdatePhotoAsync(req, ct));

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct) => FromResult(await auth.GetMeAsync(User, ct));

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest req, CancellationToken ct) =>
        FromResult(await auth.LogoutAsync(req, ct));
}
