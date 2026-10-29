# CommunicationProvider (`Kinichit.Communication`)

This is a shared .NET 8 library for sending **email** and **SMS** from any of our applications. Each channel has one interface, and the provider (the service that actually sends) is chosen in configuration, the same way as StorageProvider.

| Channel | Interface | Providers |
|---|---|---|
| Email | `IEmailSender` | `Log` (default: writes to the log), `Pickup` (saves `.eml` files to a folder), `Smtp` (Office 365, Gmail/Workspace, Zoho, any host), `SendGrid` |
| SMS | `ISmsSender` | `Log` (default), `Twilio` (free text), `Msg91` (India DLT templates) |

What else it does:

- **Startup checks:** a provider that is chosen but missing its settings stops the app at startup, with a message naming the missing setting.
- **Mobile numbers:** written any common way, they are converted to `+91…` (the default country code is configurable). Logs show only the last four digits.
- **Secrets:** messages marked `Sensitive` (OTP, passwords) are never written to logs outside Development.
- **Status:** `ICommunicationStatus` tells an admin screen which providers are active and whether they really deliver.

## Use it in a project

1. **Once per developer PC**, add the package feed. Create a GitHub personal access token (classic) with only the `read:packages` scope, then run:
   ```bash
   dotnet nuget add source https://nuget.pkg.github.com/SantoshRamancha/index.json --name kinichit --username <your-github-user> --password <token> --store-password-in-clear-text
   ```
2. **In the app's repository**, copy `nuget.config.sample` to `nuget.config` next to the `.sln`, then install the package:
   ```bash
   dotnet add package Kinichit.Communication --version 1.0.0
   ```
3. **Register** it at startup:
   ```csharp
   builder.Services.AddCommunication(builder.Configuration);   // reads the "Email" and "Sms" sections
   ```
4. **Configure** it in `appsettings.json`. The defaults deliver nothing; they only log:
   ```json
   "Email": { "Provider": "Log", "FromAddress": "no-reply@yourdomain.com", "FromName": "Your App" },
   "Sms":   { "Provider": "Log", "DefaultCountryCode": "91" }
   ```
   Production examples:
   ```json
   "Email": {
     "Provider": "Smtp", "FromAddress": "no-reply@yourdomain.com", "FromName": "Your App",
     "Smtp": { "Host": "smtp.office365.com", "Port": 587, "EnableSsl": true, "Username": "no-reply@yourdomain.com", "Password": "<app password>" }
   },
   "Sms": {
     "Provider": "Msg91", "DefaultCountryCode": "91",
     "Msg91": { "AuthKey": "<key>", "Templates": { "otp": "<DLT template id>" } }
   }
   ```
   - **Passwords and keys** go in user-secrets (development) or the server's `appsettings.Production.json`. Never commit them.
   - **SMTP:** port 587 with STARTTLS. Port 465 is not supported by .NET's SMTP client.
   - **India SMS:** it must use DLT-registered templates. Code sends a template name (for example `otp`) plus variables, and MSG91 maps the name to your template id. Twilio sends the plain text instead.
5. **Send** messages:
   ```csharp
   public class ReceiptMailer(IEmailSender email, ISmsSender sms)
   {
       public async Task SendAsync(string to, string mobile, byte[] pdf, CancellationToken ct)
       {
           await email.SendAsync(new EmailMessage(to, "Your receipt")
           {
               HtmlBody = MessageTemplate.Render("<p>Dear {{name}}, thank you for your payment.</p>",
                   new Dictionary<string, string?> { ["name"] = "Anil" }, htmlEncode: true),
               Attachments = [new EmailAttachment("receipt.pdf", "application/pdf", pdf)],
           }, ct);

           await sms.SendAsync(new SmsMessage(mobile, "Payment received. Thank you.") { Template = "receipt" }, ct);
       }
   }
   ```
   A failed send throws `CommunicationException`. Its message is safe to show to users; the provider's full error is in the log.

## Develop and release

```bash
dotnet test CommunicationProvider.sln
```

The 26 tests make no network calls; providers are checked by capturing the requests they would send.

**Releasing a version:**
1. Merge the change to `main`.
2. Push a tag, for example:
   ```bash
   git tag v1.0.1
   git push origin v1.0.1
   ```
3. GitHub Actions builds and tests the code, then publishes `Kinichit.Communication 1.0.1` to GitHub Packages.

Version numbers follow semantic versioning:
- **1.0.x** for fixes;
- **1.x.0** for new providers or options;
- **2.0.0** when existing code would need changes.

Each app upgrades when it chooses, with `dotnet add package Kinichit.Communication --version <new>`.
