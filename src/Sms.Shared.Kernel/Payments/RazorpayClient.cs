using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Sms.Shared.Kernel.Payments;

public interface IRazorpayClient
{
    Task<RazorpayOrderCreated> CreateOrderAsync(
        string keyId, string keySecret, long amountPaise, string currency, string receipt, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> ProbeAsync(string keyId, string keySecret, CancellationToken ct = default);
    bool VerifyPaymentSignature(string keySecret, string orderId, string paymentId, string signature);
    bool VerifyWebhookSignature(string webhookSecret, string body, string signatureHeader);
}

/// Stateless Razorpay HTTP/HMAC client — credentials are passed per-call so the same client
/// serves both the global platform account (Catre billing, via RazorpayGateway) and any number
/// of per-tenant accounts (fee payments, via FeeOnlinePaymentService), with zero duplicated logic.
public sealed class RazorpayClient(IHttpClientFactory httpClientFactory, ILogger<RazorpayClient> log) : IRazorpayClient
{
    public async Task<RazorpayOrderCreated> CreateOrderAsync(
        string keyId, string keySecret, long amountPaise, string currency, string receipt, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("razorpay");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{keyId}:{keySecret}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.razorpay.com/v1/orders");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        var payload = JsonSerializer.Serialize(new
        {
            amount = amountPaise,
            currency,
            receipt = receipt.Length > 40 ? receipt[..40] : receipt,
            payment_capture = 1,
        });
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Razorpay order create failed: {Status} {Body}", (int)res.StatusCode, body);
            throw new InvalidOperationException("Could not create Razorpay order.");
        }

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Razorpay order missing id.");
        var amount = doc.RootElement.GetProperty("amount").GetInt64();
        var curr = doc.RootElement.GetProperty("currency").GetString() ?? currency;
        return new RazorpayOrderCreated(id, amount, curr);
    }

    public async Task<(bool Ok, string? Error)> ProbeAsync(string keyId, string keySecret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(keySecret))
            return (false, "Save the Razorpay key ID and secret first.");
        var client = httpClientFactory.CreateClient("razorpay");
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{keyId}:{keySecret}"));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.razorpay.com/v1/orders?count=1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        using var res = await client.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return (true, null);
        log.LogWarning("Razorpay credential check failed: {Status}", (int)res.StatusCode);
        return (false, res.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? "Razorpay rejected this key ID and secret."
            : "Could not reach Razorpay with this school's keys.");
    }

    bool IRazorpayClient.VerifyPaymentSignature(string keySecret, string orderId, string paymentId, string signature) =>
        VerifyPaymentSignature(keySecret, orderId, paymentId, signature);

    bool IRazorpayClient.VerifyWebhookSignature(string webhookSecret, string body, string signatureHeader) =>
        VerifyWebhookSignature(webhookSecret, body, signatureHeader);

    public static bool VerifyPaymentSignature(string keySecret, string orderId, string paymentId, string signature)
    {
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(paymentId) || string.IsNullOrWhiteSpace(signature))
            return false;
        return HmacEquals($"{orderId}|{paymentId}", signature, keySecret);
    }

    public static bool VerifyWebhookSignature(string webhookSecret, string body, string signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(webhookSecret) || string.IsNullOrWhiteSpace(signatureHeader))
            return false;
        return HmacEquals(body, signatureHeader, webhookSecret);
    }

    private static bool HmacEquals(string payload, string signatureHex, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(key, data);
        var expected = Convert.ToHexString(hash).ToLowerInvariant();
        var actual = signatureHex.Trim().ToLowerInvariant();
        if (expected.Length != actual.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));
    }
}
