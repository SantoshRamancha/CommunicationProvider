namespace Kinichit.Communication;

/// <summary>Sends email through the provider chosen in configuration (<c>Email:Provider</c>).</summary>
public interface IEmailSender
{
    /// <summary>Sends the message. Throws <see cref="CommunicationException"/> when the provider refuses it.</summary>
    Task<SendResult> SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Sends SMS through the provider chosen in configuration (<c>Sms:Provider</c>).</summary>
public interface ISmsSender
{
    /// <summary>Sends the message. Throws <see cref="CommunicationException"/> when the provider refuses it.</summary>
    Task<SendResult> SendAsync(SmsMessage message, CancellationToken ct = default);
}

/// <summary>Which providers are active and whether they have what they need (for admin screens and health checks).</summary>
public interface ICommunicationStatus
{
    ChannelStatus Email { get; }
    ChannelStatus Sms { get; }
}

public sealed record ChannelStatus(string Channel, string Provider, bool DeliversToRecipients, string Detail);

public sealed record EmailAddress(string Address, string? Name = null)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed class EmailMessage
{
    public EmailMessage(string to, string subject) : this([new EmailAddress(to)], subject) { }

    public EmailMessage(IEnumerable<EmailAddress> to, string subject)
    {
        To = [.. to];
        Subject = subject;
    }

    public List<EmailAddress> To { get; }
    public List<EmailAddress> Cc { get; init; } = [];
    public List<EmailAddress> Bcc { get; init; } = [];
    public string Subject { get; }
    /// <summary>HTML body. At least one of <see cref="HtmlBody"/> and <see cref="TextBody"/> is required.</summary>
    public string? HtmlBody { get; init; }
    public string? TextBody { get; init; }
    /// <summary>Overrides <c>Email:FromAddress</c> / <c>FromName</c> for this message.</summary>
    public EmailAddress? From { get; init; }
    public EmailAddress? ReplyTo { get; init; }
    public List<EmailAttachment> Attachments { get; init; } = [];

    internal void Validate()
    {
        if (To.Count == 0) throw new ArgumentException("An email needs at least one recipient.");
        if (string.IsNullOrWhiteSpace(Subject)) throw new ArgumentException("An email needs a subject.");
        if (string.IsNullOrWhiteSpace(HtmlBody) && string.IsNullOrWhiteSpace(TextBody)) throw new ArgumentException("An email needs a body.");
    }
}

public sealed class SmsMessage(string to, string body)
{
    /// <summary>Mobile number in any common format; normalised with <c>Sms:DefaultCountryCode</c> (e.g. 9876543210 → +919876543210).</summary>
    public string To { get; } = to;
    /// <summary>Text for providers that send free text (Twilio, Log).</summary>
    public string Body { get; } = body;
    /// <summary>
    /// Logical template name (e.g. "otp"). Template-based providers such as MSG91 (India DLT) map it to their
    /// registered template id through <c>Sms:Msg91:Templates</c>.
    /// </summary>
    public string? Template { get; init; }
    /// <summary>Values for the template's variables (e.g. otp = 123456).</summary>
    public Dictionary<string, string> Variables { get; init; } = [];
    /// <summary>The message holds a secret (OTP, password): never logged, even by the Log provider in production.</summary>
    public bool Sensitive { get; init; }
}

/// <summary>Outcome of a send. <see cref="MessageId"/> is the provider's id when it returns one.</summary>
public sealed record SendResult(string Provider, string? MessageId = null);

/// <summary>A provider refused or failed to send. <see cref="Exception.Message"/> is safe to show to users; details are logged.</summary>
public sealed class CommunicationException(string message, Exception? inner = null) : Exception(message, inner);
