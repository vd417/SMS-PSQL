using Sms.Application.Common;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Comms;

public interface IPushDeviceService
{
    Task<ApiResult> RegisterAsync(string? token, string? platform, string? app, CancellationToken ct = default);
}

public sealed class PushDeviceService(PushDeviceRepository repo, ITenantContext tenant) : IPushDeviceService
{
    private static readonly HashSet<string> Platforms = new(StringComparer.OrdinalIgnoreCase) { "android", "ios", "web" };
    private static readonly HashSet<string> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        "student", "teacher", "staff", "crm", "superadmin"
    };

    public async Task<ApiResult> RegisterAsync(string? token, string? platform, string? app, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);

        var trimmed = (token ?? "").Trim();
        var plat = (platform ?? "").Trim().ToLowerInvariant();
        var which = (app ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length is < 20 or > 4096 || !Platforms.Contains(plat) || !Apps.Contains(which))
            return ApiResult.Fail(new Error("invalid_request", "token, platform and app are required"), 422);

        await repo.UpsertAsync(tid, uid, trimmed, plat, which, ct);
        return ApiResult.NoContent();
    }
}
