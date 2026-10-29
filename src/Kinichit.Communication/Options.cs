namespace Kinichit.Communication;

/// <summary>Configuration section <c>Email</c>.</summary>
public sealed class EmailOptions
{
    /// <summary>Log (default: writes to the log), Pickup (writes .eml files to a folder), Smtp or SendGrid.</summary>
    public string Provider { get; set; } = EmailProviders.Log;
    public string FromAddress { get; set; } = "no-reply@localhost";
    public string? FromName { get; set; }
    /// <summary>Folder for the Pickup provider (each message is a .eml file you can open in Outlook).</summary>
    public string PickupDirectory { get; set; } = "App_Data/mail";
    public SmtpOptions Smtp { get; set; } = new();
    public SendGridOptions SendGrid { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    /// <summary>587 with STARTTLS (Office 365, Gmail, most hosts). Port 465 (implicit TLS) is not supported by System.Net.Mail.</summary>
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class SendGridOptions
{
    public string ApiKey { get; set; } = "";
}

/// <summary>Configuration section <c>Sms</c>.</summary>
public sealed class SmsOptions
{
    /// <summary>Log (default), Twilio or Msg91.</summary>
    public string Provider { get; set; } = SmsProviders.Log;
    /// <summary>Added to numbers written without one (10-digit Indian mobiles by default).</summary>
    public string DefaultCountryCode { get; set; } = "91";
    public TwilioOptions Twilio { get; set; } = new();
    public Msg91Options Msg91 { get; set; } = new();
}

public sealed class TwilioOptions
{
    public string AccountSid { get; set; } = "";
    public string AuthToken { get; set; } = "";
    /// <summary>Messaging Service SID (MG…), preferred for India DLT sender IDs. Otherwise set <see cref="From"/>.</summary>
    public string? MessagingServiceSid { get; set; }
    /// <summary>Sender number or alphanumeric sender ID, used when no Messaging Service is set.</summary>
    public string? From { get; set; }
}

public sealed class Msg91Options
{
    public string AuthKey { get; set; } = "";
    /// <summary>DLT template id used when a message names no template (or an unmapped one).</summary>
    public string? DefaultTemplateId { get; set; }
    /// <summary>Logical template name → MSG91 template id, e.g. { "otp": "65f…" }.</summary>
    public Dictionary<string, string> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class EmailProviders
{
    public const string Log = "Log";
    public const string Pickup = "Pickup";
    public const string Smtp = "Smtp";
    public const string SendGrid = "SendGrid";
}

public static class SmsProviders
{
    public const string Log = "Log";
    public const string Twilio = "Twilio";
    public const string Msg91 = "Msg91";
}
