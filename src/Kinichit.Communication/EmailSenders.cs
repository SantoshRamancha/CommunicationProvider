using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kinichit.Communication;

/// <summary>Development: writes the email to the log instead of sending it.</summary>
internal sealed class LogEmailSender(IOptions<EmailOptions> options, ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task<SendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        message.Validate();
        var text = message.TextBody ?? message.HtmlBody ?? "";
        logger.LogInformation("Email (not sent, Log provider) from {From} to {To}: {Subject}\n{Body}",
            message.From ?? MailBuilder.DefaultFrom(options.Value), string.Join(", ", message.To), message.Subject,
            text.Length > 2000 ? text[..2000] + "…" : text);
        return Task.FromResult(new SendResult(EmailProviders.Log));
    }
}

/// <summary>Testing: writes each email as a .eml file into <c>Email:PickupDirectory</c> (open it in Outlook or a browser).</summary>
internal sealed class PickupEmailSender(IOptions<EmailOptions> options, ILogger<PickupEmailSender> logger) : IEmailSender
{
    public async Task<SendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        message.Validate();
        var dir = Path.GetFullPath(options.Value.PickupDirectory);
        Directory.CreateDirectory(dir);
        using var mail = MailBuilder.Build(message, options.Value);
        using var client = new SmtpClient { DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory, PickupDirectoryLocation = dir };
        await client.SendMailAsync(mail, ct);
        logger.LogInformation("Email to {To} saved in {Dir}: {Subject}", string.Join(", ", message.To), dir, message.Subject);
        return new SendResult(EmailProviders.Pickup);
    }
}

/// <summary>Any SMTP server (Office 365, Gmail/Workspace, Zoho, your host's mail server) with STARTTLS.</summary>
internal sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task<SendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        message.Validate();
        var o = options.Value.Smtp;
        using var mail = MailBuilder.Build(message, options.Value);
        using var client = new SmtpClient(o.Host, o.Port)
        {
            EnableSsl = o.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = o.TimeoutSeconds * 1000,
        };
        if (!string.IsNullOrEmpty(o.Username))
        {
            client.UseDefaultCredentials = false;
            client.Credentials = new NetworkCredential(o.Username, o.Password);
        }
        try
        {
            await client.SendMailAsync(mail, ct);
        }
        catch (SmtpException ex)
        {
            logger.LogError(ex, "SMTP send to {To} via {Host}:{Port} failed ({Status})", string.Join(", ", message.To), o.Host, o.Port, ex.StatusCode);
            throw new CommunicationException("Could not send the email right now. Please try again later.", ex);
        }
        return new SendResult(EmailProviders.Smtp);
    }
}

/// <summary>SendGrid Web API v3 (no SMTP needed; works where port 587 is blocked).</summary>
internal sealed class SendGridEmailSender(HttpClient http, IOptions<EmailOptions> options, ILogger<SendGridEmailSender> logger) : IEmailSender
{
    public async Task<SendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        message.Validate();
        var o = options.Value;
        var from = message.From ?? MailBuilder.DefaultFrom(o);
        static object Addr(EmailAddress a) => string.IsNullOrWhiteSpace(a.Name) ? new { email = a.Address } : new { email = a.Address, name = a.Name };

        var personalization = new Dictionary<string, object> { ["to"] = message.To.Select(Addr).ToList() };
        if (message.Cc.Count > 0) personalization["cc"] = message.Cc.Select(Addr).ToList();
        if (message.Bcc.Count > 0) personalization["bcc"] = message.Bcc.Select(Addr).ToList();
        var content = new List<object>();
        if (!string.IsNullOrWhiteSpace(message.TextBody)) content.Add(new { type = "text/plain", value = message.TextBody });
        if (!string.IsNullOrWhiteSpace(message.HtmlBody)) content.Add(new { type = "text/html", value = message.HtmlBody });
        var body = new Dictionary<string, object>
        {
            ["personalizations"] = new[] { personalization },
            ["from"] = Addr(from),
            ["subject"] = message.Subject,
            ["content"] = content,
        };
        if (message.ReplyTo is { } rt) body["reply_to"] = Addr(rt);
        if (message.Attachments.Count > 0)
            body["attachments"] = message.Attachments.Select(a => new
            {
                content = Convert.ToBase64String(a.Content), filename = a.FileName, type = a.ContentType, disposition = "attachment",
            }).ToList();

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", o.SendGrid.ApiKey);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogError("SendGrid send to {To} failed: {Status} {Body}", string.Join(", ", message.To), (int)res.StatusCode,
                await res.Content.ReadAsStringAsync(ct));
            throw new CommunicationException("Could not send the email right now. Please try again later.");
        }
        return new SendResult(EmailProviders.SendGrid, res.Headers.TryGetValues("X-Message-Id", out var ids) ? ids.FirstOrDefault() : null);
    }
}

internal static class MailBuilder
{
    public static EmailAddress DefaultFrom(EmailOptions o) => new(o.FromAddress, o.FromName);

    public static MailMessage Build(EmailMessage m, EmailOptions o)
    {
        var from = m.From ?? DefaultFrom(o);
        var mail = new MailMessage { From = new MailAddress(from.Address, from.Name), Subject = m.Subject };
        foreach (var a in m.To) mail.To.Add(new MailAddress(a.Address, a.Name));
        foreach (var a in m.Cc) mail.CC.Add(new MailAddress(a.Address, a.Name));
        foreach (var a in m.Bcc) mail.Bcc.Add(new MailAddress(a.Address, a.Name));
        if (m.ReplyTo is { } rt) mail.ReplyToList.Add(new MailAddress(rt.Address, rt.Name));

        // Both parts when given: mail clients show HTML, fall back to text.
        if (!string.IsNullOrWhiteSpace(m.HtmlBody) && !string.IsNullOrWhiteSpace(m.TextBody))
        {
            mail.Body = m.TextBody;
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(m.HtmlBody, null, MediaTypeNames.Text.Html));
        }
        else
        {
            mail.IsBodyHtml = !string.IsNullOrWhiteSpace(m.HtmlBody);
            mail.Body = m.HtmlBody ?? m.TextBody;
        }
        foreach (var att in m.Attachments)
            mail.Attachments.Add(new Attachment(new MemoryStream(att.Content), att.FileName, att.ContentType));
        return mail;
    }
}
