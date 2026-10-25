using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Sms.Shared.Kernel.Time;

namespace Sms.Shared.Kernel.Auth;

public sealed class JwtTokenService(JwtOptions options, IClock clock) : IJwtTokenService
{
    public string IssueAccess(Guid userId, Guid? tenantId, IEnumerable<string> roles, bool isPlatform, int? accessMinutesOverride = null)
    {
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("is_platform", isPlatform ? "1" : "0"),
        };
        if (tenantId is { } tid) claims.Add(new Claim("tenant_id", tid.ToString()));
        claims.AddRange(roles.Select(r => new Claim("role", r)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = clock.UtcNow;
        var token = new JwtSecurityToken(
            issuer: options.Issuer, audience: options.Audience, claims: claims,
            notBefore: now, expires: now.AddMinutes(accessMinutesOverride ?? options.AccessTokenMinutes), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string NewRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
