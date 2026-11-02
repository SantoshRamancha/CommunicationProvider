namespace Kinichit.Communication;

/// <summary>
/// Outbox for email, in EngageNow's <c>EmailQueueItems</c> layout: <see cref="EnqueueAsync"/> stores the message, and
/// <see cref="EmailQueueProcessor"/> (run by a Hangfire recurring job) sends it. A sent row gets <c>IsDeleted = 1</c>; an unsent
/// one keeps <c>IsDeleted = 0</c> with its <c>Retries</c>, so an admin screen can show what was sent and what wasn't.
/// </summary>
public interface IEmailQueue
{
    /// <summary>Stores the message for sending and returns its id. Throws <see cref="ArgumentException"/> for a message that can't be queued.</summary>
    /// <param name="message">
    /// The email; checked now, not when the job sends it. One body is kept: <see cref="EmailMessage.HtmlBody"/> when set (Html = 1),
    /// otherwise <see cref="EmailMessage.TextBody"/>. Reply-To is not supported by the queue.
    /// </param>
    /// <param name="category">Free text to group messages on the tracking screen, e.g. "orders".</param>
    /// <param name="template">Name of the template the app built the message from, e.g. "order-confirmation".</param>
    /// <param name="sendAfterUtc">Hold the message until this time (UTC); stored as its <c>Date</c>. Default: send on the next run.</param>
    /// <param name="ct">Cancels the insert.</param>
    Task<Guid> EnqueueAsync(EmailMessage message, string? category = null, string? template = null, DateTime? sendAfterUtc = null,
        CancellationToken ct = default);

    /// <summary>Messages matching the filter, newest first, without the body.</summary>
    Task<EmailQueuePage> ListAsync(EmailQueueFilter filter, CancellationToken ct = default);

    /// <summary>One message with its body and sender, or null.</summary>
    Task<EmailQueueEntryDetail?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Number of messages in each status.</summary>
    Task<EmailQueueCounts> CountsAsync(CancellationToken ct = default);

    /// <summary>Puts a <see cref="EmailQueueStatus.Failed"/> message back in the queue (Retries = 0, Date = now). False when it isn't Failed.</summary>
    Task<bool> RetryAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Worked out from the row: <c>IsDeleted = 1</c> is Sent; otherwise Failed once <c>Retries</c> reaches <c>EmailQueue:MaxAttempts</c>.</summary>
public enum EmailQueueStatus
{
    /// <summary>Not sent yet: waiting for its first try or its next one.</summary>
    Pending,
    Sent,
    /// <summary>Gave up after <c>EmailQueue:MaxAttempts</c> tries, or the message itself is invalid. Use Retry to send it again.</summary>
    Failed,
}

/// <summary>Configuration section <c>EmailQueue</c>.</summary>
public sealed class EmailQueueOptions
{
    /// <summary>SqlServer (default) or InMemory (tests and local experiments only: the queue is lost on restart).</summary>
    public string Store { get; set; } = EmailQueueStores.SqlServer;
    /// <summary>Connection string for the SqlServer store. When empty, <c>ConnectionStrings:{ConnectionStringName}</c> is used.</summary>
    public string? ConnectionString { get; set; }
    public string ConnectionStringName { get; set; } = "DefaultConnection";
    /// <summary>Database schema of the queue tables (<c>[dbo].[EmailQueueItems]</c>, <c>[dbo].[EmailQueueItemAttachments]</c>).</summary>
    public string Schema { get; set; } = "dbo";
    /// <summary>Create the tables on first use when they are missing (the app's login needs CREATE TABLE once).</summary>
    public bool PrepareSchema { get; set; } = true;
    /// <summary>False pauses sending: messages are still queued and stay Pending until it is true again.</summary>
    public bool SendingEnabled { get; set; } = true;
    /// <summary>Most messages one run sends; the rest wait for the next run.</summary>
    public int MaxPerRun { get; set; } = 200;
    /// <summary>Tries before a message counts as Failed. Waits between tries: 1, 5, 15, 60 minutes, then hourly.</summary>
    public int MaxAttempts { get; set; } = 5;
    /// <summary>Sent and Failed messages whose Date is older than this are deleted by <see cref="EmailQueueProcessor.CleanUpAsync"/>.</summary>
    public int RetainDays { get; set; } = 90;
}

public static class EmailQueueStores
{
    public const string SqlServer = "SqlServer";
    public const string InMemory = "InMemory";
}

/// <summary>Filter for <see cref="IEmailQueue.ListAsync"/>. <see cref="Search"/> matches To, Cc, Bcc and Subject.</summary>
public sealed record EmailQueueFilter
{
    public EmailQueueStatus? Status { get; init; }
    public string? Category { get; init; }
    public string? Template { get; init; }
    public string? Search { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// A queued message as the tracking screen lists it. <see cref="To"/>, <see cref="Cc"/> and <see cref="Bcc"/> are comma-separated
/// addresses; <see cref="Attachments"/> is the attachment file names; <see cref="Date"/> is when it was queued (or retried), UTC.
/// </summary>
public sealed record EmailQueueEntry(
    Guid Id, EmailQueueStatus Status, string? Category, string? Template, string To, string? Cc, string? Bcc, string Subject, int Retries,
    DateTime Date, string? Attachments);

/// <summary><see cref="Html"/> tells whether <see cref="Body"/> is HTML; <see cref="EnableSsl"/> is recorded from <c>Email:Smtp:EnableSsl</c>.</summary>
public sealed record EmailQueueEntryDetail(EmailQueueEntry Entry, string? SenderAddress, string? SenderName, string? Body, bool Html, bool EnableSsl);

public sealed record EmailQueuePage(IReadOnlyList<EmailQueueEntry> Items, int Total, int Page, int PageSize);

public sealed record EmailQueueCounts(int Pending, int Sent, int Failed);

/// <summary>Outcome of one <see cref="EmailQueueProcessor.ProcessAsync"/> run.</summary>
public sealed record EmailQueueRunResult(int Sent, int Retrying, int Failed)
{
    public static readonly EmailQueueRunResult None = new(0, 0, 0);
}

/// <summary>
/// Storage behind the queue. <see cref="SqlServerEmailQueueStore"/> and <see cref="InMemoryEmailQueueStore"/> ship with the
/// library; register your own implementation before <c>AddEmailQueue</c> to keep the queue somewhere else.
/// </summary>
public interface IEmailQueueStore
{
    Task AddAsync(QueuedEmail email, CancellationToken ct);

    /// <summary>
    /// Takes the oldest due message: not sent, fewer than MaxAttempts retries, and its wait after the last try is over (counted
    /// from Date). The message stays locked to this lease until it is completed, failed or disposed, so two runs never send the
    /// same message; disposing without completing (the app stopped mid-send) leaves it unchanged for a later run. Null when
    /// nothing is due.
    /// </summary>
    Task<IEmailQueueLease?> TakeNextAsync(DateTime nowUtc, CancellationToken ct);

    Task<EmailQueuePage> ListAsync(EmailQueueFilter filter, CancellationToken ct);
    Task<EmailQueueEntryDetail?> GetAsync(Guid id, CancellationToken ct);
    Task<EmailQueueCounts> CountsAsync(CancellationToken ct);

    /// <summary>Failed → Pending: Retries = 0 and Date = now (so the waits start again). False when the message isn't Failed.</summary>
    Task<bool> RetryAsync(Guid id, DateTime nowUtc, CancellationToken ct);

    /// <summary>Deletes Sent and Failed messages dated before <paramref name="beforeUtc"/>. Returns how many.</summary>
    Task<int> DeleteFinishedAsync(DateTime beforeUtc, CancellationToken ct);
}

/// <summary>A message taken for sending by <see cref="IEmailQueueStore.TakeNextAsync"/>.</summary>
public interface IEmailQueueLease : IAsyncDisposable
{
    QueuedEmail Email { get; }

    /// <summary>Sent: IsDeleted = 1, Retries + 1.</summary>
    Task CompleteAsync(CancellationToken ct);

    /// <summary>Not sent: Retries + 1, or straight to MaxAttempts when <paramref name="giveUp"/> (it can never be sent).</summary>
    Task FailAsync(bool giveUp, CancellationToken ct);
}

/// <summary>A message as the store keeps it. <see cref="Date"/> is when it may be sent (queued, retried or held until).</summary>
public sealed class QueuedEmail
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Has <see cref="EmailMessage.From"/> set: the message's own sender or <c>Email:FromAddress</c>.</summary>
    public required EmailMessage Message { get; init; }
    public string? Category { get; init; }
    public string? Template { get; init; }
    public DateTime Date { get; init; }
    public bool EnableSsl { get; init; }
    /// <summary>Tries before this one.</summary>
    public int Retries { get; init; }
}
