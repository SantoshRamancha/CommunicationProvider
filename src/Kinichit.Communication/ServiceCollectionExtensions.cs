using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kinichit.Communication;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEmailSender"/>, <see cref="ISmsSender"/> and <see cref="ICommunicationStatus"/> from the
    /// <c>Email</c> and <c>Sms</c> configuration sections (names can be changed). A provider that is named but missing
    /// its settings fails here, at startup, rather than on the first message.
    /// </summary>
    public static IServiceCollection AddCommunication(this IServiceCollection services, IConfiguration configuration,
        string emailSection = "Email", string smsSection = "Sms")
    {
        var email = configuration.GetSection(emailSection).Get<EmailOptions>() ?? new EmailOptions();
        var sms = configuration.GetSection(smsSection).Get<SmsOptions>() ?? new SmsOptions();
        services.Configure<EmailOptions>(configuration.GetSection(emailSection));
        services.Configure<SmsOptions>(configuration.GetSection(smsSection));

        var emailStatus = Check(email);
        switch (Normalize(email.Provider, EmailProviders.Log, EmailProviders.Pickup, EmailProviders.Smtp, EmailProviders.SendGrid, "Email"))
        {
            case EmailProviders.Pickup: services.AddSingleton<IEmailSender, PickupEmailSender>(); break;
            case EmailProviders.Smtp: services.AddSingleton<IEmailSender, SmtpEmailSender>(); break;
            case EmailProviders.SendGrid: services.AddHttpClient<IEmailSender, SendGridEmailSender>(); break;
            default: services.AddSingleton<IEmailSender, LogEmailSender>(); break;
        }

        var smsStatus = Check(sms);
        switch (Normalize(sms.Provider, SmsProviders.Log, SmsProviders.Twilio, SmsProviders.Msg91, null, "Sms"))
        {
            case SmsProviders.Twilio: services.AddHttpClient<ISmsSender, TwilioSmsSender>(); break;
            case SmsProviders.Msg91: services.AddHttpClient<ISmsSender, Msg91SmsSender>(); break;
            default: services.AddSingleton<ISmsSender, LogSmsSender>(); break;
        }

        services.AddSingleton<ICommunicationStatus>(new CommunicationStatus(emailStatus, smsStatus));
        return services;
    }

    /// <summary>
    /// Registers <see cref="IEmailQueue"/> and <see cref="EmailQueueProcessor"/> from the <c>EmailQueue</c> section. Call it after
    /// <see cref="AddCommunication"/>: the processor sends through that <see cref="IEmailSender"/>. The SqlServer store (default) uses
    /// <c>EmailQueue:ConnectionString</c>, or else <c>ConnectionStrings:DefaultConnection</c>. To keep the queue somewhere else,
    /// register your own <see cref="IEmailQueueStore"/> first.
    /// </summary>
    public static IServiceCollection AddEmailQueue(this IServiceCollection services, IConfiguration configuration, string section = "EmailQueue")
    {
        if (!services.Any(d => d.ServiceType == typeof(IEmailSender)))
            throw new InvalidOperationException("Call AddCommunication before AddEmailQueue: the queue sends through its IEmailSender.");

        var o = configuration.GetSection(section).Get<EmailQueueOptions>() ?? new EmailQueueOptions();
        services.Configure<EmailQueueOptions>(configuration.GetSection(section));
        services.TryAddSingleton(TimeProvider.System);

        if (!services.Any(d => d.ServiceType == typeof(IEmailQueueStore)))
        {
            var store = string.IsNullOrWhiteSpace(o.Store) ? EmailQueueStores.SqlServer : o.Store.Trim();
            if (string.Equals(store, EmailQueueStores.SqlServer, StringComparison.OrdinalIgnoreCase))
            {
                var connectionString = string.IsNullOrWhiteSpace(o.ConnectionString) ? configuration.GetConnectionString(o.ConnectionStringName) : o.ConnectionString;
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException($"The email queue needs {section}:ConnectionString or ConnectionStrings:{o.ConnectionStringName}.");
                services.AddSingleton<IEmailQueueStore>(new SqlServerEmailQueueStore(connectionString, o.Schema, o.PrepareSchema, o.MaxAttempts));
            }
            else if (string.Equals(store, EmailQueueStores.InMemory, StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<IEmailQueueStore>(new InMemoryEmailQueueStore(o.MaxAttempts));
            }
            else
            {
                throw new InvalidOperationException(
                    $"{section}:Store '{o.Store}' is not supported. Use one of: {EmailQueueStores.SqlServer}, {EmailQueueStores.InMemory}.");
            }
        }

        services.TryAddSingleton<IEmailQueue, EmailQueue>();
        services.TryAddTransient<EmailQueueProcessor>();
        return services;
    }

    private static string Normalize(string? provider, string a, string b, string c, string? d, string section)
    {
        var p = string.IsNullOrWhiteSpace(provider) ? a : provider.Trim();
        foreach (var known in new[] { a, b, c, d })
            if (known is not null && string.Equals(p, known, StringComparison.OrdinalIgnoreCase)) return known;
        throw new InvalidOperationException(
            $"{section}:Provider '{provider}' is not supported. Use one of: {string.Join(", ", new[] { a, b, c, d }.Where(x => x is not null))}.");
    }

    internal static ChannelStatus Check(EmailOptions o)
    {
        var provider = Normalize(o.Provider, EmailProviders.Log, EmailProviders.Pickup, EmailProviders.Smtp, EmailProviders.SendGrid, "Email");
        static void Need(bool ok, string what) { if (!ok) throw new InvalidOperationException($"Email is set to send but {what} is missing."); }
        switch (provider)
        {
            case EmailProviders.Smtp:
                Need(!string.IsNullOrWhiteSpace(o.Smtp.Host), "Email:Smtp:Host");
                Need(!string.IsNullOrWhiteSpace(o.FromAddress) && o.FromAddress.Contains('@'), "Email:FromAddress");
                return new ChannelStatus("Email", provider, true, $"SMTP {o.Smtp.Host}:{o.Smtp.Port}, from {o.FromAddress}");
            case EmailProviders.SendGrid:
                Need(!string.IsNullOrWhiteSpace(o.SendGrid.ApiKey), "Email:SendGrid:ApiKey");
                Need(!string.IsNullOrWhiteSpace(o.FromAddress) && o.FromAddress.Contains('@'), "Email:FromAddress");
                return new ChannelStatus("Email", provider, true, $"SendGrid, from {o.FromAddress}");
            case EmailProviders.Pickup:
                return new ChannelStatus("Email", provider, false, $"Saved as .eml files in {Path.GetFullPath(o.PickupDirectory)} (not delivered)");
            default:
                return new ChannelStatus("Email", provider, false, "Written to the server log (not delivered)");
        }
    }

    internal static ChannelStatus Check(SmsOptions o)
    {
        var provider = Normalize(o.Provider, SmsProviders.Log, SmsProviders.Twilio, SmsProviders.Msg91, null, "Sms");
        static void Need(bool ok, string what) { if (!ok) throw new InvalidOperationException($"SMS is set to send but {what} is missing."); }
        switch (provider)
        {
            case SmsProviders.Twilio:
                Need(!string.IsNullOrWhiteSpace(o.Twilio.AccountSid) && !string.IsNullOrWhiteSpace(o.Twilio.AuthToken), "Sms:Twilio:AccountSid / AuthToken");
                Need(!string.IsNullOrWhiteSpace(o.Twilio.MessagingServiceSid) || !string.IsNullOrWhiteSpace(o.Twilio.From), "Sms:Twilio:MessagingServiceSid or From");
                return new ChannelStatus("Sms", provider, true, "Twilio");
            case SmsProviders.Msg91:
                Need(!string.IsNullOrWhiteSpace(o.Msg91.AuthKey), "Sms:Msg91:AuthKey");
                Need(!string.IsNullOrWhiteSpace(o.Msg91.DefaultTemplateId) || o.Msg91.Templates.Count > 0, "Sms:Msg91:DefaultTemplateId or Templates");
                return new ChannelStatus("Sms", provider, true, $"MSG91, {o.Msg91.Templates.Count} template(s)");
            default:
                return new ChannelStatus("Sms", provider, false, "Written to the server log (not delivered)");
        }
    }
}

internal sealed record CommunicationStatus(ChannelStatus Email, ChannelStatus Sms) : ICommunicationStatus;
