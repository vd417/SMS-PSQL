using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sms.Shared.Kernel.Push;

/// Server-side Expo push client (https://docs.expo.dev/push-notifications/sending-notifications/).
/// Batches tokens (<=MaxBatchSize per request, Expo's limit is 100) and NEVER throws for provider
/// failures — a dead token or Expo outage must not break the in-app notification flow.
public sealed class ExpoPushSender(
    IHttpClientFactory httpClientFactory, IOptions<ExpoPushOptions> options, ILogger<ExpoPushSender> log)
    : IExpoPushSender
{
    public async Task SendAsync(IReadOnlyList<string> expoPushTokens, string title, string body,
        IReadOnlyDictionary<string, object?>? data = null, CancellationToken ct = default)
    {
        if (expoPushTokens.Count == 0) return;
        var opts = options.Value;
        var batchSize = opts.MaxBatchSize > 0 ? opts.MaxBatchSize : 100;

        for (var i = 0; i < expoPushTokens.Count; i += batchSize)
        {
            var batch = expoPushTokens.Skip(i).Take(batchSize).ToList();
            await SendBatchAsync(batch, title, body, data, opts, ct);
        }
    }

    async Task SendBatchAsync(IReadOnlyList<string> tokens, string title, string body,
        IReadOnlyDictionary<string, object?>? data, ExpoPushOptions opts, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("expo");
            var payload = new { to = tokens, title, body, data, sound = "default" };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{opts.BaseUrl}/--/api/v2/push/send");
            req.Headers.Add("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(opts.AccessToken))
                req.Headers.Add("Authorization", $"Bearer {opts.AccessToken}");
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            // Per-call timeout via a linked CTS rather than mutating a shared named client's Timeout.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(opts.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var res = await client.SendAsync(req, linkedCts.Token);
            if (!res.IsSuccessStatusCode)
            {
                var respBody = await res.Content.ReadAsStringAsync(ct);
                log.LogWarning("Expo push failed: {Status} {Body}", (int)res.StatusCode, respBody);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "Expo push call threw");
        }
    }
}
