using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kinichit.Communication.Tests;

public class PhoneAndTemplateTests
{
    [Theory]
    [InlineData("9876543210", "+919876543210")]
    [InlineData("098765 43210", "+919876543210")]
    [InlineData("+91 98765-43210", "+919876543210")]
    [InlineData("0091 9876543210", "+919876543210")]
    [InlineData("+14155550123", "+14155550123")]
    public void Normalises_to_E164(string input, string expected) => Assert.Equal(expected, PhoneNumbers.ToE164(input));

    [Fact]
    public void Rejects_non_numbers() => Assert.Throws<ArgumentException>(() => PhoneNumbers.ToE164("12ab"));

    [Fact]
    public void Masks_all_but_last_four() => Assert.Equal("********3210", PhoneNumbers.Mask("+919876543210"));

    [Fact]
    public void Renders_templates_and_keeps_unknown_placeholders()
    {
        var values = new Dictionary<string, string?> { ["name"] = "Anil <Kumar>", ["amount"] = "₹28,000" };
        Assert.Equal("Dear Anil <Kumar>, ₹28,000 due. {{missing}}", MessageTemplate.Render("Dear {{name}}, {{ amount }} due. {{missing}}", values));
        Assert.Equal("Dear Anil &lt;Kumar&gt;", MessageTemplate.Render("Dear {{name}}", values, htmlEncode: true));
    }
}

public class RegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddCommunication(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_to_log_providers_that_deliver_nothing()
    {
        using var sp = Build([]);
        var status = sp.GetRequiredService<ICommunicationStatus>();
        Assert.Equal("Log", status.Email.Provider);
        Assert.Equal("Log", status.Sms.Provider);
        Assert.False(status.Email.DeliversToRecipients);
        Assert.IsType<LogEmailSender>(sp.GetRequiredService<IEmailSender>());
        Assert.IsType<LogSmsSender>(sp.GetRequiredService<ISmsSender>());
    }

    [Fact]
    public void Picks_configured_providers_case_insensitively()
    {
        using var sp = Build(new()
        {
            ["Email:Provider"] = "smtp", ["Email:FromAddress"] = "noreply@kinichit.com", ["Email:Smtp:Host"] = "smtp.office365.com",
            ["Sms:Provider"] = "msg91", ["Sms:Msg91:AuthKey"] = "k", ["Sms:Msg91:Templates:otp"] = "tpl-otp",
        });
        Assert.IsType<SmtpEmailSender>(sp.GetRequiredService<IEmailSender>());
        Assert.IsType<Msg91SmsSender>(sp.GetRequiredService<ISmsSender>());
        var status = sp.GetRequiredService<ICommunicationStatus>();
        Assert.True(status.Email.DeliversToRecipients);
        Assert.Contains("smtp.office365.com:587", status.Email.Detail);
    }

    [Theory]
    [InlineData("Email:Provider", "Smtp")]       // no host
    [InlineData("Email:Provider", "SendGrid")]   // no API key
    [InlineData("Sms:Provider", "Twilio")]       // no credentials
    [InlineData("Sms:Provider", "Msg91")]        // no auth key
    [InlineData("Sms:Provider", "Carrier pigeon")]
    public void Misconfiguration_fails_at_startup(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new() { [key] = value }));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }
}

public class EmailSenderTests
{
    private static IOptions<EmailOptions> Opts(Action<EmailOptions>? edit = null)
    {
        var o = new EmailOptions { FromAddress = "noreply@kinichit.com", FromName = "Lease Manager" };
        edit?.Invoke(o);
        return Options.Create(o);
    }

    [Fact]
    public async Task Pickup_writes_an_eml_file_with_html_text_and_attachment()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kc-mail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sender = new PickupEmailSender(Opts(o => o.PickupDirectory = dir), NullLogger<PickupEmailSender>.Instance);
            await sender.SendAsync(new EmailMessage("anil@example.com", "Invoice INV/2026-27/00007")
            {
                TextBody = "Your invoice is attached.", HtmlBody = "<p>Your invoice is <b>attached</b>.</p>",
                Attachments = [new EmailAttachment("invoice.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4"))],
            });
            var file = Assert.Single(Directory.GetFiles(dir, "*.eml"));
            var eml = await File.ReadAllTextAsync(file);
            Assert.Contains("To: anil@example.com", eml);
            Assert.Contains("Subject: Invoice INV/2026-27/00007", eml);
            Assert.Contains("text/html", eml);
            Assert.Contains("invoice.pdf", eml);
            Assert.Contains("noreply@kinichit.com", eml);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Messages_without_a_body_are_rejected() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LogEmailSender(Opts(), NullLogger<LogEmailSender>.Instance).SendAsync(new EmailMessage("a@b.com", "Hi")));

    [Fact]
    public async Task SendGrid_posts_the_v3_payload()
    {
        var handler = new FakeHandler(HttpStatusCode.Accepted, "", ("X-Message-Id", "sg-123"));
        var sender = new SendGridEmailSender(new HttpClient(handler), Opts(o => o.SendGrid.ApiKey = "SG.key"), NullLogger<SendGridEmailSender>.Instance);
        var result = await sender.SendAsync(new EmailMessage("anil@example.com", "Welcome") { HtmlBody = "<p>Hi</p>" });

        Assert.Equal("sg-123", result.MessageId);
        Assert.Equal("https://api.sendgrid.com/v3/mail/send", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer SG.key", handler.Request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("noreply@kinichit.com", json.RootElement.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal("anil@example.com", json.RootElement.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("text/html", json.RootElement.GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task SendGrid_failure_becomes_a_friendly_error()
    {
        var sender = new SendGridEmailSender(new HttpClient(new FakeHandler(HttpStatusCode.Unauthorized, "{\"errors\":[]}")),
            Opts(o => o.SendGrid.ApiKey = "bad"), NullLogger<SendGridEmailSender>.Instance);
        var ex = await Assert.ThrowsAsync<CommunicationException>(() => sender.SendAsync(new EmailMessage("a@b.com", "x") { TextBody = "y" }));
        Assert.Contains("Could not send the email", ex.Message);
    }
}

public class SmsSenderTests
{
    private static IOptions<SmsOptions> Opts(Action<SmsOptions> edit)
    {
        var o = new SmsOptions();
        edit(o);
        return Options.Create(o);
    }

    [Fact]
    public async Task Twilio_posts_form_with_E164_number_and_basic_auth()
    {
        var handler = new FakeHandler(HttpStatusCode.Created, "{\"sid\":\"SM123\"}");
        var sender = new TwilioSmsSender(new HttpClient(handler),
            Opts(o => { o.Twilio.AccountSid = "AC1"; o.Twilio.AuthToken = "tok"; o.Twilio.MessagingServiceSid = "MG1"; }),
            NullLogger<TwilioSmsSender>.Instance);
        var result = await sender.SendAsync(new SmsMessage("9876543210", "123456 is your code"));

        Assert.Equal("SM123", result.MessageId);
        Assert.Equal("https://api.twilio.com/2010-04-01/Accounts/AC1/Messages.json", handler.Request!.RequestUri!.ToString());
        Assert.Contains("To=%2B919876543210", handler.Body);
        Assert.Contains("MessagingServiceSid=MG1", handler.Body);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("AC1:tok")), handler.Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Twilio_failure_never_echoes_the_message()
    {
        var logger = new CapturingLogger<TwilioSmsSender>();
        var sender = new TwilioSmsSender(new HttpClient(new FakeHandler(HttpStatusCode.BadRequest, "{\"code\":21211}")),
            Opts(o => { o.Twilio.AccountSid = "AC1"; o.Twilio.AuthToken = "t"; o.Twilio.From = "LEASEM"; }), logger);
        await Assert.ThrowsAsync<CommunicationException>(() => sender.SendAsync(new SmsMessage("9876543210", "SECRET-654321") { Sensitive = true }));
        Assert.DoesNotContain(logger.Lines, l => l.Contains("SECRET-654321"));
        Assert.Contains(logger.Lines, l => l.Contains("3210") && !l.Contains("9876543210"));
    }

    [Fact]
    public async Task Msg91_sends_the_mapped_template_with_variables()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{\"type\":\"success\",\"message\":\"req-1\"}");
        var sender = new Msg91SmsSender(new HttpClient(handler),
            Opts(o => { o.Msg91.AuthKey = "auth"; o.Msg91.Templates["otp"] = "tpl-otp"; }), NullLogger<Msg91SmsSender>.Instance);
        var result = await sender.SendAsync(new SmsMessage("98765 43210", "ignored by MSG91") { Template = "otp", Variables = { ["otp"] = "123456" } });

        Assert.Equal("req-1", result.MessageId);
        Assert.Equal("auth", handler.Request!.Headers.GetValues("authkey").Single());
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("tpl-otp", json.RootElement.GetProperty("template_id").GetString());
        var r = json.RootElement.GetProperty("recipients")[0];
        Assert.Equal("919876543210", r.GetProperty("mobiles").GetString());
        Assert.Equal("123456", r.GetProperty("otp").GetString());
    }

    [Fact]
    public async Task Msg91_error_body_with_200_is_still_an_error()
    {
        var sender = new Msg91SmsSender(new HttpClient(new FakeHandler(HttpStatusCode.OK, "{\"type\":\"error\",\"message\":\"Invalid template\"}")),
            Opts(o => { o.Msg91.AuthKey = "a"; o.Msg91.DefaultTemplateId = "t"; }), NullLogger<Msg91SmsSender>.Instance);
        await Assert.ThrowsAsync<CommunicationException>(() => sender.SendAsync(new SmsMessage("9876543210", "x")));
    }

    [Fact]
    public async Task Msg91_without_a_template_explains_what_is_missing()
    {
        var sender = new Msg91SmsSender(new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}")),
            Opts(o => o.Msg91.AuthKey = "a"), NullLogger<Msg91SmsSender>.Instance);
        var ex = await Assert.ThrowsAsync<CommunicationException>(() => sender.SendAsync(new SmsMessage("9876543210", "x") { Template = "receipt" }));
        Assert.Contains("receipt", ex.Message);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public async Task Log_provider_hides_sensitive_text_outside_development(string environment, bool shown)
    {
        var logger = new CapturingLogger<LogSmsSender>();
        var sender = new LogSmsSender(Opts(_ => { }), logger, new Env(environment));
        await sender.SendAsync(new SmsMessage("9876543210", "Code 112233") { Sensitive = true });
        Assert.Equal(shown, logger.Lines.Any(l => l.Contains("112233")));
    }
}

internal sealed class FakeHandler(HttpStatusCode status, string body, params (string Name, string Value)[] headers) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Request = request;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var res = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) res.Headers.TryAddWithoutValidation(name, value);
        return res;
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception));
}

internal sealed class Env(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;
    public string ApplicationName { get; set; } = "tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
