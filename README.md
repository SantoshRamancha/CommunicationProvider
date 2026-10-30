# CommunicationProvider (`Kinichit.Communication`)

This is a shared .NET 8 library for sending **email** and **SMS** from any of our applications. Each channel has one interface, and the provider (the service that actually sends) is chosen in configuration, the same way as StorageProvider.

| Channel | Interface | Providers |
|---|---|---|
| Email | `IEmailSender` | `Log` (default: writes to the log), `Pickup` (saves `.eml` files to a folder), `Smtp` (Office 365, Gmail/Workspace, Zoho, any host), `SendGrid` |
| SMS | `ISmsSender` | `Log` (default), `Twilio` (free text), `Msg91` (India DLT templates) |
| Email queue | `IEmailQueue` | SQL Server tables in the app's own database; a Hangfire job sends them ([below](#email-queue)) |

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
   dotnet add package Kinichit.Communication --version 1.1.0
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

## Email queue

`IEmailSender` sends straight away, so when it fails during a request the email is lost, and nobody can see later what went out. The queue is the same process EngageNow uses, in the same `EmailQueueItems` table layout: the app **stores** each email, and a **Hangfire** job sends it, so you can always see what was sent and what wasn't.

- **Where it lives:** `[dbo].[EmailQueueItems]` in the app's own SQL Server database, plus `[dbo].[EmailQueueItemAttachments]` for attachment contents. Both are created on first use (as Hangfire creates its own tables), so the app needs no migration.
- **Columns:** `IsDeleted`, `Id`, `Body`, `Date`, `EnableSsl`, `Html`, `SenderAddress`, `SenderName`, `Subject`, `To`, `Attachments`, `Bcc`, `Cc`, `Retries`, `Category` and `Template`, as in EngageNow without the per-row SMTP settings.
  - `To`, `Cc` and `Bcc` are comma-separated addresses; `Attachments` lists the file names.
  - `Body` is the HTML body (`Html = 1`), or the text body when there is no HTML (`Html = 0`).
  - `Date` is when the email was queued (UTC), or when it may be sent if it was held or retried.
  - `EnableSsl` records `Email:Smtp:EnableSsl`; sending uses the `Email` settings.
- **Sent / not sent:** a sent email gets `IsDeleted = 1`. An unsent one keeps `IsDeleted = 0`, and `Retries` counts its failed tries. After 5 (`MaxAttempts`) it counts as Failed and is not tried again until an admin presses **Retry**. The tries are 1, 5, 15 and 60 minutes apart, counted from `Date`. An invalid email fails at once. The reason for a failure is in the app's log.
- **Safe to run twice:** the run sending an email keeps its row locked, so another run or app instance never sends it too. If the app stops in the middle of a send, the lock is released and a later run sends it (so in that rare case it can arrive twice).

### Set it up in an app

1. **Register** it after `AddCommunication`, and add Hangfire (packages `Hangfire.AspNetCore` and `Hangfire.SqlServer`):
   ```csharp
   builder.Services.AddCommunication(builder.Configuration);
   builder.Services.AddEmailQueue(builder.Configuration);   // uses ConnectionStrings:DefaultConnection

   builder.Services.AddHangfire(c => c.UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection")));
   builder.Services.AddHangfireServer();
   ```
2. **Schedule** the two jobs at startup:
   ```csharp
   var jobs = app.Services.GetRequiredService<IRecurringJobManager>();
   jobs.AddOrUpdate<EmailQueueProcessor>("email-queue", p => p.ProcessAsync(CancellationToken.None), "*/15 * * * * *");  // every 15 seconds
   jobs.AddOrUpdate<EmailQueueProcessor>("email-queue-cleanup", p => p.CleanUpAsync(CancellationToken.None), Cron.Daily(3));
   ```
   Hangfire passes its own cancellation token in place of `CancellationToken.None`.
3. **Queue** emails instead of sending them:
   ```csharp
   public class OrderMailer(IEmailQueue queue)
   {
       public Task ConfirmAsync(Order order, CancellationToken ct) =>
           queue.EnqueueAsync(new EmailMessage(order.CustomerEmail, $"Order {order.Number} confirmed") { HtmlBody = "<p>…</p>" },
               category: "orders", template: "order-confirmation", ct: ct);
   }
   ```
   `EnqueueAsync` checks the email immediately and throws `ArgumentException` if it could never be sent or doesn't fit the table (for example a subject over 200 characters, or a Reply-To, which the table has no column for). The sender is the email's `From`, or `Email:FromAddress` / `FromName`.
4. **Track** them on an admin screen with the same `IEmailQueue`:
   - `ListAsync(new EmailQueueFilter { Status = EmailQueueStatus.Failed, Search = "anil@" })` lists emails, newest first, without the body. You can filter by status, category, template, a date range and search text (To, Cc, Bcc, Subject).
   - `GetAsync(id)` returns one email with its body and sender.
   - `CountsAsync()` returns the number of Pending, Sent and Failed emails.
   - `RetryAsync(id)` sends a Failed email again.

   The table can also be read directly in SSMS.

### Settings (`EmailQueue` section, all optional)

| Setting | Default | |
|---|---|---|
| `Store` | `SqlServer` | `InMemory` for tests (lost on restart) |
| `ConnectionString` | — | When empty, `ConnectionStrings:{ConnectionStringName}` is used |
| `ConnectionStringName` | `DefaultConnection` | |
| `Schema` | `dbo` | Schema of the two tables |
| `PrepareSchema` | `true` | Create the tables on first use. The app's login needs CREATE TABLE once; set `false` if a DBA creates them |
| `SendingEnabled` | `true` | `false` pauses sending; emails stay Pending |
| `MaxPerRun` | `200` | Most emails one run sends |
| `MaxAttempts` | `5` | Tries before an email counts as Failed |
| `RetainDays` | `90` | The daily clean-up deletes Sent and Failed emails dated more than this many days ago |

The queue keeps the email body, so a password or OTP in an email stays in the table until the clean-up deletes it. Send such emails with `IEmailSender` directly instead.

## Develop and release

```bash
dotnet test CommunicationProvider.sln
```

The tests make no network calls; providers are checked by capturing the requests they would send. The email queue tests run against the in-memory store. To also run them against SQL Server, set `COMMUNICATION_TEST_SQLSERVER` to a connection string for a database where the tests may create schemas; each test creates its own schema and drops it afterwards. GitHub Actions does this with a temporary SQL Server container.

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
