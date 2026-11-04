using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Api.Services;

public sealed class FirebaseOptions
{
    public string ProjectId { get; set; } = "";
    public string ServiceAccountPath { get; set; } = "";
}

public sealed class FcmAccessTokenProvider(
    IHttpClientFactory http,
    IOptions<FirebaseOptions> options,
    ILogger<FcmAccessTokenProvider> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset accessExpiry = DateTimeOffset.MinValue;
    private ServiceAccountFile? account;

    public async Task<string?> GetAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.ProjectId) || string.IsNullOrWhiteSpace(opts.ServiceAccountPath))
            return null;
        if (!File.Exists(opts.ServiceAccountPath))
        {
            logger.LogWarning("Firebase service account file is missing");
            return null;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (accessToken is not null && accessExpiry > DateTimeOffset.UtcNow.AddMinutes(1))
                return accessToken;

            account ??= JsonSerializer.Deserialize<ServiceAccountFile>(await File.ReadAllTextAsync(opts.ServiceAccountPath, ct));
            if (account?.client_email is null || account.private_key is null)
                return null;

            var jwt = Sign(account);
            var client = http.CreateClient("fcm");
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = jwt,
            });
            using var res = await client.PostAsync("https://oauth2.googleapis.com/token", body, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Firebase access token request failed: {Status}", (int)res.StatusCode);
                return null;
            }

            var parsed = JsonSerializer.Deserialize<TokenResponse>(json);
            if (string.IsNullOrWhiteSpace(parsed?.access_token))
                return null;
            accessToken = parsed.access_token;
            accessExpiry = DateTimeOffset.UtcNow.AddSeconds(parsed.expires_in is > 0 and var secs ? secs : 3000);
            return accessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Sign(ServiceAccountFile account)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = account.client_email,
            scope = "https://www.googleapis.com/auth/firebase.messaging",
            aud = "https://oauth2.googleapis.com/token",
            iat = now,
            exp = now + 3600,
        }));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + payload);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.private_key);
        var sig = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Base64Url(sig);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class ServiceAccountFile
    {
        public string? client_email { get; set; }
        public string? private_key { get; set; }
    }

    private sealed class TokenResponse
    {
        public string? access_token { get; set; }
        public int expires_in { get; set; }
    }
}

/// Loads device tokens for the notice's school, then sends each one through FCM.
public sealed class FcmPushFanout(
    IServiceScopeFactory scopes,
    FcmAccessTokenProvider tokens,
    IHttpClientFactory http,
    IOptions<FirebaseOptions> options,
    ILogger<FcmPushFanout> logger) : IPushNotificationFanout
{
    public void Enqueue(Guid tenantId, Guid? userId, string title, string? body)
    {
        _ = Task.Run(() => DeliverAsync(tenantId, userId, title, body));
    }

    private async Task DeliverAsync(Guid tenantId, Guid? userId, string title, string? body)
    {
        try
        {
            var projectId = options.Value.ProjectId;
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            List<string> deviceTokens;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                tenant.Set(tenantId, null, false);
                var repo = scope.ServiceProvider.GetRequiredService<PushDeviceRepository>();
                deviceTokens = (await repo.ListAsync(userId)).Select(r => r.Token).ToList();
            }
            if (deviceTokens.Count == 0)
                return;

            var access = await tokens.GetAsync(CancellationToken.None);
            if (access is null)
                return;

            var client = http.CreateClient("fcm");
            foreach (var device in deviceTokens)
            {
                var stale = await SendAsync(client, projectId, access, device, title, body);
                if (!stale) continue;
                await using var scope = scopes.CreateAsyncScope();
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                tenant.Set(tenantId, null, false);
                var repo = scope.ServiceProvider.GetRequiredService<PushDeviceRepository>();
                await repo.DeleteAsync(device);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Push fan-out failed for tenant {Tenant}", tenantId);
        }
    }

    private async Task<bool> SendAsync(
        HttpClient client, string projectId, string access, string device, string title, string? body)
    {
        var payload = JsonSerializer.Serialize(new
        {
            message = new
            {
                token = device,
                notification = new { title, body = body ?? "" },
                android = new { priority = "HIGH" },
                apns = new { payload = new { aps = new { sound = "default" } } },
                webpush = new { notification = new { title, body = body ?? "" } },
            }
        });
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req);
        if (res.IsSuccessStatusCode)
            return false;
        var text = await res.Content.ReadAsStringAsync();
        logger.LogWarning("FCM send failed: {Status}", (int)res.StatusCode);
        return (int)res.StatusCode is 404 || text.Contains("UNREGISTERED", StringComparison.OrdinalIgnoreCase);
    }
}
