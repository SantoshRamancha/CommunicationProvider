using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kinichit.Communication;

internal sealed class EmailQueue(IEmailQueueStore store, IOptions<EmailOptions> emailOptions, TimeProvider clock, ILogger<EmailQueue> logger)
    : IEmailQueue
{
    public async Task<Guid> EnqueueAsync(EmailMessage message, string? category = null, string? template = null, DateTime? sendAfterUtc = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var o = emailOptions.Value;
        var from = message.From ?? MailBuilder.DefaultFrom(o);
        Validate(message, from, category, template);

        var now = clock.GetUtcNow().UtcDateTime;
        var sendAfter = sendAfterUtc?.Kind == DateTimeKind.Local ? sendAfterUtc.Value.ToUniversalTime() : sendAfterUtc;
        var email = new QueuedEmail
        {
            // Only what the table keeps: one body (HTML when given), no Reply-To.
            Message = new EmailMessage(message.To, message.Subject)
            {
                Cc = message.Cc, Bcc = message.Bcc, From = from, Attachments = message.Attachments,
                HtmlBody = string.IsNullOrWhiteSpace(message.HtmlBody) ? null : message.HtmlBody,
                TextBody = string.IsNullOrWhiteSpace(message.HtmlBody) ? message.TextBody : null,
            },
            Category = Clean(category),
            Template = Clean(template),
            Date = sendAfter > now ? DateTime.SpecifyKind(sendAfter.Value, DateTimeKind.Utc) : now,
            EnableSsl = o.Smtp.EnableSsl,
        };
        await store.AddAsync(email, ct);
        logger.LogInformation("Email {Id} queued for {To}: {Subject}", email.Id, QueueText.Join(message.To), message.Subject);
        return email.Id;
    }

    public Task<EmailQueuePage> ListAsync(EmailQueueFilter filter, CancellationToken ct = default) => store.ListAsync(filter, ct);
    public Task<EmailQueueEntryDetail?> GetAsync(Guid id, CancellationToken ct = default) => store.GetAsync(id, ct);
    public Task<EmailQueueCounts> CountsAsync(CancellationToken ct = default) => store.CountsAsync(ct);
    public Task<bool> RetryAsync(Guid id, CancellationToken ct = default) => store.RetryAsync(id, clock.GetUtcNow().UtcDateTime, ct);

    /// <summary>Rejects what would only fail later in the job, or not fit the table, so the caller hears about it while it can still fix it.</summary>
    private static void Validate(EmailMessage m, EmailAddress from, string? category, string? template)
    {
        m.Validate();
        if (m.ReplyTo is not null) throw new ArgumentException("The email queue does not keep Reply-To; send this email with IEmailSender instead.");
        foreach (var a in m.To.Concat(m.Cc).Concat(m.Bcc).Append(from))
            if (!MailAddress.TryCreate(a.Address, a.Name, out _)) throw new ArgumentException($"'{a.Address}' is not a valid email address.");
        static void Max(string? value, int max, string what)
        {
            if (value?.Length > max) throw new ArgumentException($"The email's {what} can't be longer than {max} characters.");
        }
        Max(QueueText.Join(m.To), QueueText.ToMax, "To list");
        Max(QueueText.Join(m.Cc), QueueText.CcMax, "Cc list");
        Max(QueueText.Join(m.Bcc), QueueText.CcMax, "Bcc list");
        Max(m.Subject, QueueText.SubjectMax, "subject");
        Max(from.Address, QueueText.NameMax, "sender address");
        Max(from.Name, QueueText.NameMax, "sender name");
        Max(category?.Trim(), QueueText.NameMax, "category");
        Max(template?.Trim(), QueueText.NameMax, "template");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Sends what is due in the queue. Run <see cref="ProcessAsync"/> from a Hangfire recurring job (every 15 seconds or so) and
/// <see cref="CleanUpAsync"/> once a day. Runs at the same time are safe: each message is locked by the run sending it.
/// </summary>
public sealed class EmailQueueProcessor(
    IEmailQueueStore store, IEmailSender sender, IOptions<EmailQueueOptions> options, TimeProvider clock, ILogger<EmailQueueProcessor> logger)
{
    /// <summary>Wait before the next try after <paramref name="attempts"/> failed tries: 1, 5, 15, 60 minutes, then hourly.</summary>
    public static TimeSpan RetryDelay(int attempts) => QueueText.RetryDelays[Math.Clamp(attempts, 1, QueueText.RetryDelays.Length) - 1];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Sends due messages until none are left or <c>EmailQueue:MaxPerRun</c> is reached.</summary>
    public async Task<EmailQueueRunResult> ProcessAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.SendingEnabled)
        {
            logger.LogDebug("Email queue: sending is paused (EmailQueue:SendingEnabled = false)");
            return EmailQueueRunResult.None;
        }

        int sent = 0, retrying = 0, failed = 0;
        var maxPerRun = Math.Max(1, o.MaxPerRun);
        while (sent + retrying + failed < maxPerRun && !ct.IsCancellationRequested)
        {
            // Disposing the lease without completing it (cancelled mid-send) leaves the message as it was, for a later run.
            await using var lease = await store.TakeNextAsync(Now, ct);
            if (lease is null) break;
            var email = lease.Email;
            var attempt = email.Retries + 1;
            try
            {
                await sender.SendAsync(email.Message, ct);
                // CancellationToken.None: once sent, it must be recorded, or the next run would send it again.
                await lease.CompleteAsync(CancellationToken.None);
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // An invalid message fails the same way every time, so it isn't retried.
                var giveUp = ex is ArgumentException or FormatException || attempt >= Math.Max(1, o.MaxAttempts);
                await lease.FailAsync(giveUp, CancellationToken.None);
                if (giveUp)
                {
                    failed++;
                    logger.LogError(ex, "Email {Id} to {To} failed after {Attempts} attempt(s); giving up: {Reason}", email.Id,
                        QueueText.Join(email.Message.To), attempt, QueueText.Describe(ex));
                }
                else
                {
                    retrying++;
                    logger.LogWarning(ex, "Email {Id} to {To} failed (attempt {Attempts}); trying again in {Wait}: {Reason}", email.Id,
                        QueueText.Join(email.Message.To), attempt, RetryDelay(attempt), QueueText.Describe(ex));
                }
            }
        }

        if (sent + retrying + failed > 0)
            logger.LogInformation("Email queue run: {Sent} sent, {Retrying} to retry, {Failed} failed", sent, retrying, failed);
        return new EmailQueueRunResult(sent, retrying, failed);
    }

    /// <summary>Deletes Sent and Failed messages dated more than <c>EmailQueue:RetainDays</c> ago. Returns how many.</summary>
    public async Task<int> CleanUpAsync(CancellationToken ct = default)
    {
        var deleted = await store.DeleteFinishedAsync(Now.AddDays(-Math.Max(1, options.Value.RetainDays)), ct);
        if (deleted > 0) logger.LogInformation("Email queue clean-up: deleted {Count} old message(s)", deleted);
        return deleted;
    }
}

/// <summary>Column sizes, address text and retry timing shared by the stores.</summary>
internal static class QueueText
{
    public const int ToMax = 500, CcMax = 1000, SubjectMax = 200, NameMax = 200, AttachmentsMax = 200;

    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

    /// <summary>
    /// How long after its Date a message with <paramref name="retries"/> failed tries may be tried again: the waits added up
    /// (0, 1, 6, 21, 81 minutes, then +60 per try). The table has no "next attempt" column, so this is counted from Date.
    /// </summary>
    public static int MinutesAfterDate(int retries) => retries switch
    {
        <= 0 => 0,
        1 => 1,
        2 => 6,
        3 => 21,
        _ => 81 + 60 * (retries - 4),
    };

    public static EmailQueueStatus StatusOf(bool isDeleted, int retries, int maxAttempts) =>
        isDeleted ? EmailQueueStatus.Sent : retries >= maxAttempts ? EmailQueueStatus.Failed : EmailQueueStatus.Pending;

    /// <summary>Comma-separated addresses as <see cref="MailAddress"/> writes them (names quoted when needed), so they parse back exactly.</summary>
    public static string Join(IEnumerable<EmailAddress> addresses) =>
        string.Join(", ", addresses.Select(a => new MailAddress(a.Address, a.Name).ToString()));

    public static string? JoinOrNull(IEnumerable<EmailAddress> addresses) => Join(addresses) is { Length: > 0 } s ? s : null;

    public static List<EmailAddress> Parse(string? addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses)) return [];
        var parsed = new MailAddressCollection();
        parsed.Add(addresses);
        return parsed.Select(a => new EmailAddress(a.Address, string.IsNullOrEmpty(a.DisplayName) ? null : a.DisplayName)).ToList();
    }

    public static string? AttachmentNames(IEnumerable<EmailAttachment> attachments) =>
        string.Join(", ", attachments.Select(a => a.FileName)) is { Length: > 0 } s ? Truncate(s, AttachmentsMax) : null;

    /// <summary>
    /// The reason for the log. <see cref="CommunicationException"/> carries a message meant for end users; the provider's own
    /// reason (its inner exception) is what an admin needs.
    /// </summary>
    public static string Describe(Exception ex) =>
        (ex is CommunicationException { InnerException: { } inner } ? $"{ex.Message} {inner.Message}" : ex.Message).ReplaceLineEndings(" ");

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static (int Page, int PageSize) Paging(EmailQueueFilter f) => (Math.Max(1, f.Page), Math.Clamp(f.PageSize, 1, 500));
}
