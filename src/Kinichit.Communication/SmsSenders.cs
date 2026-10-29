using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kinichit.Communication;

/// <summary>Development: writes the SMS to the log. Sensitive messages (OTP) are shown only in the Development environment.</summary>
internal sealed class LogSmsSender(IOptions<SmsOptions> options, ILogger<LogSmsSender> logger, IHostEnvironment? env = null) : ISmsSender
{
    public Task<SendResult> SendAsync(SmsMessage message, CancellationToken ct = default)
    {
        var to = PhoneNumbers.ToE164(message.To, options.Value.DefaultCountryCode);
        var showBody = !message.Sensitive || env is null || env.IsDevelopment();
        logger.LogInformation("SMS (not sent, Log provider) to {To}: {Body}", to, showBody ? message.Body : "[hidden: sensitive]");
        return Task.FromResult(new SendResult(SmsProviders.Log));
    }
}

/// <summary>Twilio Messages REST API (free text; use a Messaging Service for India DLT sender IDs).</summary>
internal sealed class TwilioSmsSender(HttpClient http, IOptions<SmsOptions> options, ILogger<TwilioSmsSender> logger) : ISmsSender
{
    public async Task<SendResult> SendAsync(SmsMessage message, CancellationToken ct = default)
    {
        var o = options.Value.Twilio;
        var to = PhoneNumbers.ToE164(message.To, options.Value.DefaultCountryCode);
        var form = new Dictionary<string, string> { ["To"] = to, ["Body"] = message.Body };
        if (!string.IsNullOrEmpty(o.MessagingServiceSid)) form["MessagingServiceSid"] = o.MessagingServiceSid;
        else form["From"] = o.From ?? throw new CommunicationException("SMS is not configured (Sms:Twilio needs MessagingServiceSid or From).");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.twilio.com/2010-04-01/Accounts/{o.AccountSid}/Messages.json")
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{o.AccountSid}:{o.AuthToken}")));
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            // Twilio's error body explains DLT / sender problems. The message text is never logged (it may hold an OTP).
            logger.LogError("Twilio SMS to {To} failed: {Status} {Body}", PhoneNumbers.Mask(to), (int)res.StatusCode, text);
            throw new CommunicationException("Could not send the SMS right now. Please try again in a minute.");
        }
        return new SendResult(SmsProviders.Twilio, Json.TryString(text, "sid"));
    }
}

/// <summary>
/// MSG91 Flow API (India): sends a DLT-registered template. The message's <see cref="SmsMessage.Template"/> is mapped to a
/// template id through <c>Sms:Msg91:Templates</c> (else <c>DefaultTemplateId</c>); <see cref="SmsMessage.Variables"/> fill it.
/// </summary>
internal sealed class Msg91SmsSender(HttpClient http, IOptions<SmsOptions> options, ILogger<Msg91SmsSender> logger) : ISmsSender
{
    public async Task<SendResult> SendAsync(SmsMessage message, CancellationToken ct = default)
    {
        var o = options.Value.Msg91;
        var templateId = (message.Template is { } name && o.Templates.TryGetValue(name, out var id) ? id : null) ?? o.DefaultTemplateId;
        if (string.IsNullOrWhiteSpace(templateId))
            throw new CommunicationException($"SMS is not configured (no MSG91 template for '{message.Template ?? "default"}').");

        var to = PhoneNumbers.ToE164(message.To, options.Value.DefaultCountryCode).TrimStart('+');
        var recipient = new Dictionary<string, string>(message.Variables, StringComparer.OrdinalIgnoreCase) { ["mobiles"] = to };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://control.msg91.com/api/v5/flow/")
        {
            Content = JsonContent.Create(new { template_id = templateId, short_url = "0", recipients = new[] { recipient } }),
        };
        req.Headers.Add("authkey", o.AuthKey);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        // MSG91 answers 200 with {"type":"error",...} for many failures.
        if (!res.IsSuccessStatusCode || string.Equals(Json.TryString(text, "type"), "error", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("MSG91 SMS to {To} failed: {Status} {Body}", PhoneNumbers.Mask(to), (int)res.StatusCode, text);
            throw new CommunicationException("Could not send the SMS right now. Please try again in a minute.");
        }
        return new SendResult(SmsProviders.Msg91, Json.TryString(text, "message"));
    }
}

internal static class Json
{
    public static string? TryString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out var v)
                ? v.ToString() : null;
        }
        catch (JsonException) { return null; }
    }
}
