using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Sms.Shared.Kernel.Auth;

/// Delivers a Catre-generated login code through the approved WhatsApp template.
/// Fast2SMS does not verify the code. The hash stays in OtpCodes.
public sealed class Fast2SmsWhatsAppOtpSender : IPhoneOtpSender
{
    public const string DeliveryFailed = "Could not deliver the verification code.";

    private readonly HttpClient _http;
    private readonly WhatsAppOtpOptions _options;
    private readonly ILogger<Fast2SmsWhatsAppOtpSender>? _log;

    public Fast2SmsWhatsAppOtpSender(
        HttpClient http,
        WhatsAppOtpOptions options,
        ILogger<Fast2SmsWhatsAppOtpSender>? log = null)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    public async Task<string> SendAsync(string identifier, string channel, CancellationToken ct = default)
    {
        var mobile = IndianMobile.ToTenDigits(identifier);
        if (mobile is null)
            throw new InvalidOperationException(DeliveryFailed);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var path = QueryHelpers.AddQueryString("/dev/whatsapp", new Dictionary<string, string?>
        {
            ["authorization"] = _options.ApiKey,
            ["message_id"] = _options.MessageId,
            ["phone_number_id"] = _options.PhoneNumberId,
            ["numbers"] = mobile,
            ["variables_values"] = code,
        });

        using var response = await GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!Fast2SmsWhatsAppResult.IsAccepted((int)response.StatusCode, body))
        {
            _log?.LogWarning(
                "WhatsApp OTP rejected with HTTP {Status} for message {MessageId}",
                (int)response.StatusCode, _options.MessageId);
            throw new InvalidOperationException(DeliveryFailed);
        }

        return code;
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _http.GetAsync(path, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.LogWarning("WhatsApp OTP delivery timed out for message {MessageId}", _options.MessageId);
            throw new InvalidOperationException(DeliveryFailed);
        }
        catch (HttpRequestException)
        {
            _log?.LogWarning("WhatsApp OTP delivery failed for message {MessageId}", _options.MessageId);
            throw new InvalidOperationException(DeliveryFailed);
        }
    }
}

/// Production phone OTP when the Catre WhatsApp key is not configured.
public sealed class UnavailablePhoneOtpSender : IPhoneOtpSender
{
    public Task<string> SendAsync(string identifier, string channel, CancellationToken ct = default) =>
        throw new InvalidOperationException(Fast2SmsWhatsAppOtpSender.DeliveryFailed);
}

public static class IndianMobile
{
    /// Last 10 digits of an Indian mobile. Accepts 98…, 098…, 9198…, and +91 98….
    public static string? ToTenDigits(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 12 && digits.StartsWith("91", StringComparison.Ordinal))
            digits = digits[2..];
        else if (digits.Length == 11 && digits.StartsWith('0'))
            digits = digits[1..];
        if (digits.Length != 10 || digits[0] < '6')
            return null;
        return digits;
    }
}

public static class Fast2SmsWhatsAppResult
{
    /// HTTP 200 is not enough. The provider must set return or success to true.
    public static bool IsAccepted(int statusCode, string? body)
    {
        if (statusCode < (int)HttpStatusCode.OK || statusCode >= 300)
            return false;
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (Flag(root, "return") is { } ret) return ret;
            if (Flag(root, "success") is { } ok) return ok;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool? Flag(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => false,
        };
    }
}
