using Sms.Application.Common;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Devices;

public sealed record RegisterDeviceRequest(string? ExpoPushToken, string? Platform);

public interface IDeviceService
{
    Task<ApiResult> RegisterAsync(RegisterDeviceRequest req, CancellationToken ct = default);
}

/// Idempotent upsert of the current app user's Expo push token. User/tenant come from the
/// authenticated context, never from the request body.
public sealed class DeviceService(DeviceTokenRepository devices, ITenantContext tenant) : IDeviceService
{
    private static readonly HashSet<string> Platforms = ["ios", "android"];

    public async Task<ApiResult> RegisterAsync(RegisterDeviceRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("forbidden", "no tenant/user context"), 403);

        var token = req.ExpoPushToken?.Trim() ?? "";
        if (token.Length == 0)
            return ApiResult.Fail(new Error("invalid_token", "expo_push_token is required"), 422);
        var platform = req.Platform?.Trim().ToLowerInvariant() ?? "";
        if (!Platforms.Contains(platform))
            return ApiResult.Fail(new Error("invalid_platform", "platform must be ios or android"), 422);

        await devices.UpsertAsync(tid, uid, token, platform, ct);
        return ApiResult.Ok();
    }
}
