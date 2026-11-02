using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kinichit.Communication.Tests;

/// <summary>
/// Store behaviour, run against every store. The SqlServer store is included when COMMUNICATION_TEST_SQLSERVER holds a connection
/// string to a database the tests may create schemas in (each test uses its own schema and drops it afterwards).
/// </summary>
public class EmailQueueStoreTests
{
    public static TheoryData<string> Stores()
    {
        var data = new TheoryData<string> { EmailQueueStores.InMemory };
        if (TestStore.SqlConnectionString is not null) data.Add(EmailQueueStores.SqlServer);
        return data;
    }

    private static readonly DateTime T0 = new(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc);

    [Theory, MemberData(nameof(Stores))]
    public async Task A_taken_email_comes_back_whole(string kind)
    {
        await using var t = await TestStore.CreateAsync(kind);
        var message = new EmailMessage([new EmailAddress("anil@example.com", "Kumar, Anil"), new EmailAddress("b@example.com")], "Invoice INV/7")
        {
            Cc = [new EmailAddress("cc@example.com", "Accounts")],
            Bcc = [new EmailAddress("audit@example.com")],
            From = new EmailAddress("billing@kinichit.com", "Billing"),
            HtmlBody = "<p>Dear Anil, ₹28,000 is due.</p>",
            Attachments = [new EmailAttachment("invoice.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4")),
                new EmailAttachment("notes.txt", "text/plain", [1, 2, 3])],
        };
        var id = await t.AddAsync(message, T0, category: "invoices", template: "invoice-issued", enableSsl: true);

        await using (var lease = await t.Store.TakeNextAsync(T0, default))
        {
            var e = lease!.Email;
            Assert.Equal((id, 0, "invoices", "invoice-issued", true), (e.Id, e.Retries, e.Category, e.Template, e.EnableSsl));
            var m = e.Message;
            Assert.Equal(["Kumar, Anil <anil@example.com>", "b@example.com"], m.To.Select(a => a.ToString()));
            Assert.Equal("Accounts", Assert.Single(m.Cc).Name);
            Assert.Equal("audit@example.com", Assert.Single(m.Bcc).Address);
            Assert.Equal(("billing@kinichit.com", "Billing"), (m.From!.Address, m.From.Name));
            Assert.Equal(message.HtmlBody, m.HtmlBody);
            Assert.Null(m.TextBody);
            Assert.Equal(["invoice.pdf", "notes.txt"], m.Attachments.Select(a => a.FileName));
            Assert.Equal(new byte[] { 1, 2, 3 }, m.Attachments[1].Content);
        }

        var d = (await t.Store.GetAsync(id, default))!;
        Assert.Equal((EmailQueueStatus.Pending, 0), (d.Entry.Status, d.Entry.Retries)); // taken but not finished: unchanged
        Assert.Equal("\"Kumar, Anil\" <anil@example.com>, b@example.com", d.Entry.To);
        Assert.Equal("\"Accounts\" <cc@example.com>", d.Entry.Cc);
        Assert.Equal("invoice.pdf, notes.txt", d.Entry.Attachments);
        Assert.Equal(("billing@kinichit.com", "Billing", message.HtmlBody, true, true), (d.SenderAddress, d.SenderName, d.Body, d.Html, d.EnableSsl));
        Assert.Equal(DateTimeKind.Utc, d.Entry.Date.Kind);
        Assert.Null(await t.Store.GetAsync(Guid.NewGuid(), default));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task A_text_only_email_is_kept_with_Html_off(string kind)
    {
        await using var t = await TestStore.CreateAsync(kind);
        var id = await t.AddAsync(new EmailMessage("a@example.com", "Plain") { TextBody = "Just text", From = new EmailAddress("x@example.com") }, T0);
        var d = (await t.Store.GetAsync(id, default))!;
        Assert.Equal(("Just text", false), (d.Body, d.Html));
        await using var lease = await t.Store.TakeNextAsync(T0, default);
        Assert.Equal(("Just text", null), (lease!.Email.Message.TextBody, lease.Email.Message.HtmlBody));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Take_skips_held_and_taken_emails_and_a_dropped_lease_changes_nothing(string kind)
    {
        await using var t = await TestStore.CreateAsync(kind);
        var first = await t.AddAsync(Mail("1"), T0);
        var later = await t.AddAsync(Mail("2"), T0.AddHours(1)); // held until then
        var second = await t.AddAsync(Mail("3"), T0.AddSeconds(1));

        var a = await t.Store.TakeNextAsync(T0.AddMinutes(1), default);
        var b = await t.Store.TakeNextAsync(T0.AddMinutes(1), default);
        Assert.Equal([first, second], new[] { a!.Email.Id, b!.Email.Id }); // oldest first, held one left alone
        Assert.Null(await t.Store.TakeNextAsync(T0.AddMinutes(1), default));

        // The app stopped mid-send: the lease is dropped, the email is as it was and can be taken again.
        await a.DisposeAsync();
        await b.DisposeAsync();
        await using var again = await t.Store.TakeNextAsync(T0.AddMinutes(1), default);
        Assert.Equal((first, 0), (again!.Email.Id, again.Email.Retries));

        await again.CompleteAsync(default);
        await using var next = await t.Store.TakeNextAsync(T0.AddMinutes(2), default);
        Assert.Equal(second, next!.Email.Id);
        await next.CompleteAsync(default);
        await using var held = await t.Store.TakeNextAsync(T0.AddHours(2), default);
        Assert.Equal(later, held!.Email.Id);
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Parallel_takes_never_share_an_email(string kind)
    {
        await using var t = await TestStore.CreateAsync(kind);
        for (var i = 0; i < 20; i++) await t.AddAsync(Mail($"m{i}"), T0.AddSeconds(i));

        var leases = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => t.Store.TakeNextAsync(T0.AddMinutes(1), default))));
        try
        {
            var ids = leases.Select(l => l!.Email.Id).ToList();
            Assert.Equal(20, ids.Distinct().Count());
            Assert.Null(await t.Store.TakeNextAsync(T0.AddMinutes(1), default));
        }
        finally
        {
            foreach (var l in leases) if (l is not null) await l.DisposeAsync();
        }
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Sent_failed_waits_and_retry(string kind)
    {
        await using var t = await TestStore.CreateAsync(kind);
        var ok = await t.AddAsync(Mail("ok"), T0);
        var flaky = await t.AddAsync(Mail("flaky"), T0.AddSeconds(1));
        var dead = await t.AddAsync(Mail("dead"), T0.AddSeconds(2));
        await using (var l = await t.Store.TakeNextAsync(T0.AddSeconds(5), default)) await l!.CompleteAsync(default);
        await using (var l = await t.Store.TakeNextAsync(T0.AddSeconds(5), default)) await l!.FailAsync(giveUp: false, default);
        await using (var l = await t.Store.TakeNextAsync(T0.AddSeconds(5), default)) await l!.FailAsync(giveUp: true, default);

        Assert.Equal((EmailQueueStatus.Sent, 1), Entry(await t.Store.GetAsync(ok, default)));
        Assert.Equal((EmailQueueStatus.Pending, 1), Entry(await t.Store.GetAsync(flaky, default)));
        Assert.Equal((EmailQueueStatus.Failed, 5), Entry(await t.Store.GetAsync(dead, default)));
        Assert.Equal(new EmailQueueCounts(1, 1, 1), await t.Store.CountsAsync(default));

        // After one failed try, the next comes 1 minute after its Date.
        Assert.Null(await t.Store.TakeNextAsync(T0.AddSeconds(30), default));
        await using (var l = await t.Store.TakeNextAsync(T0.AddMinutes(1).AddSeconds(1), default)) Assert.Equal(flaky, l!.Email.Id);

        Assert.False(await t.Store.RetryAsync(ok, T0, default));
        Assert.False(await t.Store.RetryAsync(flaky, T0, default));
        Assert.True(await t.Store.RetryAsync(dead, T0.AddMinutes(3), default));
        var retried = (await t.Store.GetAsync(dead, default))!.Entry;
        Assert.Equal((EmailQueueStatus.Pending, 0, T0.AddMinutes(3)), (retried.Status, retried.Retries, retried.Date));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task List_filters_searches_and_pages_newest_first(string kind)
    {
        await using var t = await TestStore.CreateAsync(kind);
        var a = await t.AddAsync(new EmailMessage("anil@example.com", "Receipt for order 50% off") { TextBody = "x" }, T0, "orders", "receipt");
        var b = await t.AddAsync(new EmailMessage("bela@example.com", "Welcome") { TextBody = "x", Cc = [new EmailAddress("boss@example.com")] },
            T0.AddMinutes(1), "accounts", "welcome");
        var c = await t.AddAsync(new EmailMessage("chitra@example.com", "Receipt for order 2") { TextBody = "x" }, T0.AddMinutes(2), "orders", "receipt");
        await using (var l = await t.Store.TakeNextAsync(T0.AddMinutes(5), default)) await l!.CompleteAsync(default); // a: Sent

        var all = await t.Store.ListAsync(new EmailQueueFilter(), default);
        Assert.Equal(3, all.Total);
        Assert.Equal([c, b, a], all.Items.Select(i => i.Id));
        Assert.Equal([c], (await t.Store.ListAsync(new EmailQueueFilter { Template = "receipt", Status = EmailQueueStatus.Pending }, default)).Items.Select(i => i.Id));
        Assert.Equal([a], (await t.Store.ListAsync(new EmailQueueFilter { Status = EmailQueueStatus.Sent }, default)).Items.Select(i => i.Id));
        Assert.Equal([b], (await t.Store.ListAsync(new EmailQueueFilter { Category = "accounts" }, default)).Items.Select(i => i.Id));
        Assert.Equal([a], (await t.Store.ListAsync(new EmailQueueFilter { Search = "50%" }, default)).Items.Select(i => i.Id)); // % is literal
        Assert.Equal([b], (await t.Store.ListAsync(new EmailQueueFilter { Search = "BOSS@" }, default)).Items.Select(i => i.Id)); // Cc too
        Assert.Equal([b], (await t.Store.ListAsync(new EmailQueueFilter { FromUtc = T0.AddMinutes(1), ToUtc = T0.AddMinutes(2) }, default))
            .Items.Select(i => i.Id));

        var page2 = await t.Store.ListAsync(new EmailQueueFilter { Page = 2, PageSize = 2 }, default);
        Assert.Equal((3, 2, 2), (page2.Total, page2.Page, page2.PageSize));
        Assert.Equal([a], page2.Items.Select(i => i.Id));
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Clean_up_deletes_only_old_sent_and_failed_emails(string kind)
    {
        await using var t = await TestStore.CreateAsync(kind);
        var oldSent = await t.AddAsync(Mail("old sent", attach: true), T0);
        var oldFailed = await t.AddAsync(Mail("old failed"), T0.AddSeconds(1));
        var oldPending = await t.AddAsync(Mail("old pending"), T0.AddSeconds(2));
        await using (var l = await t.Store.TakeNextAsync(T0.AddMinutes(1), default)) await l!.CompleteAsync(default);
        await using (var l = await t.Store.TakeNextAsync(T0.AddMinutes(1), default)) await l!.FailAsync(giveUp: true, default);
        var newSent = await t.AddAsync(Mail("new sent"), T0.AddDays(10));
        await using (var pending = await t.Store.TakeNextAsync(T0.AddDays(10), default)) // keeps oldPending out of the way, unchanged
        {
            Assert.Equal(oldPending, pending!.Email.Id);
            await using var l = await t.Store.TakeNextAsync(T0.AddDays(10), default);
            Assert.Equal(newSent, l!.Email.Id);
            await l.CompleteAsync(default);
        }

        Assert.Equal(2, await t.Store.DeleteFinishedAsync(T0.AddDays(5), default));
        Assert.Equal([newSent, oldPending], (await t.Store.ListAsync(new EmailQueueFilter(), default)).Items.Select(i => i.Id));
        Assert.Null(await t.Store.GetAsync(oldSent, default));
        Assert.Null(await t.Store.GetAsync(oldFailed, default));
    }

    private static (EmailQueueStatus, int) Entry(EmailQueueEntryDetail? d) => (d!.Entry.Status, d.Entry.Retries);

    private static EmailMessage Mail(string subject, bool attach = false) => new("to@example.com", subject)
    {
        TextBody = "Hello",
        From = new EmailAddress("no-reply@example.com"),
        Attachments = attach ? [new EmailAttachment("a.txt", "text/plain", [65])] : [],
    };
}

public class EmailQueueProcessorTests
{
    private static readonly DateTime T0 = new(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        private InMemoryEmailQueueStore? _store;
        public ManualClock Clock { get; } = new(T0);
        public ScriptedSender Sender { get; } = new();
        public EmailQueueOptions Options { get; } = new() { Store = EmailQueueStores.InMemory };
        public EmailOptions Email { get; } = new() { FromAddress = "no-reply@kinichit.com", FromName = "Kinichit", Smtp = { EnableSsl = true } };
        /// <summary>Created on first use, after the test has set <see cref="Options"/>.</summary>
        public InMemoryEmailQueueStore Store => _store ??= new InMemoryEmailQueueStore(Options.MaxAttempts);
        public IEmailQueue Queue => new EmailQueue(Store, Microsoft.Extensions.Options.Options.Create(Email), Clock, NullLogger<EmailQueue>.Instance);
        public EmailQueueProcessor Processor => new(Store, Sender, Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<EmailQueueProcessor>.Instance);
        public async Task<EmailQueueEntryDetail> DetailAsync(Guid id) => (await Store.GetAsync(id, default))!;
    }

    [Fact]
    public async Task Sends_due_emails_and_marks_them_sent()
    {
        var h = new Harness();
        var id = await h.Queue.EnqueueAsync(new EmailMessage("anil@example.com", "Welcome") { HtmlBody = "<p>Hi</p>", TextBody = "Hi" }, "accounts", "welcome");
        var held = await h.Queue.EnqueueAsync(new EmailMessage("anil@example.com", "Reminder") { TextBody = "Tomorrow" }, sendAfterUtc: T0.AddDays(1));

        Assert.Equal(new EmailQueueRunResult(1, 0, 0), await h.Processor.ProcessAsync());
        var sentMail = Assert.Single(h.Sender.Sent);
        Assert.Equal(("Welcome", "no-reply@kinichit.com"), (sentMail.Subject, sentMail.From!.Address));
        var d = await h.DetailAsync(id);
        Assert.Equal((EmailQueueStatus.Sent, 1, "accounts", "welcome"), (d.Entry.Status, d.Entry.Retries, d.Entry.Category, d.Entry.Template));
        Assert.Equal(("no-reply@kinichit.com", "Kinichit", "<p>Hi</p>", true, true), (d.SenderAddress, d.SenderName, d.Body, d.Html, d.EnableSsl));
        Assert.Equal(EmailQueueStatus.Pending, (await h.DetailAsync(held)).Entry.Status);

        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, (await h.Processor.ProcessAsync()).Sent);
    }

    [Fact]
    public async Task Failures_wait_then_give_up_after_max_attempts()
    {
        var h = new Harness();
        h.Options.MaxAttempts = 3;
        h.Sender.Fail = _ => new CommunicationException("Could not send the email right now.", new InvalidOperationException("451 4.7.0 Try again later"));
        var id = await h.Queue.EnqueueAsync(new EmailMessage("anil@example.com", "Welcome") { TextBody = "Hi" });

        Assert.Equal(new EmailQueueRunResult(0, 1, 0), await h.Processor.ProcessAsync());
        Assert.Equal((EmailQueueStatus.Pending, 1), ((await h.DetailAsync(id)).Entry.Status, (await h.DetailAsync(id)).Entry.Retries));

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(EmailQueueRunResult.None, await h.Processor.ProcessAsync()); // waits 1 minute
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(new EmailQueueRunResult(0, 1, 0), await h.Processor.ProcessAsync());

        h.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(EmailQueueRunResult.None, await h.Processor.ProcessAsync()); // then 5 more minutes (6 after Date)
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new EmailQueueRunResult(0, 0, 1), await h.Processor.ProcessAsync());
        Assert.Equal((EmailQueueStatus.Failed, 3), ((await h.DetailAsync(id)).Entry.Status, (await h.DetailAsync(id)).Entry.Retries));

        // An admin presses Retry once the mailbox works again.
        h.Sender.Fail = null;
        Assert.True(await h.Queue.RetryAsync(id));
        Assert.Equal(new EmailQueueRunResult(1, 0, 0), await h.Processor.ProcessAsync());
        Assert.Equal(EmailQueueStatus.Sent, (await h.DetailAsync(id)).Entry.Status);
    }

    [Fact]
    public async Task An_invalid_email_fails_without_retrying()
    {
        var h = new Harness();
        h.Sender.Fail = _ => new ArgumentException("An email needs a body.");
        var id = await h.Queue.EnqueueAsync(new EmailMessage("anil@example.com", "x") { TextBody = "y" });
        Assert.Equal(new EmailQueueRunResult(0, 0, 1), await h.Processor.ProcessAsync());
        Assert.Equal((EmailQueueStatus.Failed, 5), ((await h.DetailAsync(id)).Entry.Status, (await h.DetailAsync(id)).Entry.Retries));
    }

    [Fact]
    public async Task One_bad_email_does_not_stop_the_others()
    {
        var h = new Harness();
        h.Sender.Fail = m => m.Subject == "bad" ? new CommunicationException("nope") : null;
        await h.Queue.EnqueueAsync(new EmailMessage("a@example.com", "bad") { TextBody = "x" });
        await h.Queue.EnqueueAsync(new EmailMessage("b@example.com", "good") { TextBody = "x" });
        Assert.Equal(new EmailQueueRunResult(1, 1, 0), await h.Processor.ProcessAsync());
        Assert.Equal(new EmailQueueCounts(1, 1, 0), await h.Queue.CountsAsync());
    }

    [Fact]
    public async Task Paused_sending_keeps_emails_pending()
    {
        var h = new Harness();
        h.Options.SendingEnabled = false;
        await h.Queue.EnqueueAsync(new EmailMessage("a@example.com", "x") { TextBody = "x" });
        Assert.Equal(EmailQueueRunResult.None, await h.Processor.ProcessAsync());
        Assert.Empty(h.Sender.Sent);
        Assert.Equal(1, (await h.Queue.CountsAsync()).Pending);
    }

    [Fact]
    public async Task A_run_stops_at_max_per_run_oldest_first()
    {
        var h = new Harness();
        h.Options.MaxPerRun = 5;
        for (var i = 0; i < 7; i++)
        {
            await h.Queue.EnqueueAsync(new EmailMessage("a@example.com", $"m{i}") { TextBody = "x" });
            h.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(5, (await h.Processor.ProcessAsync()).Sent);
        Assert.Equal(["m0", "m1", "m2", "m3", "m4"], h.Sender.Sent.Select(m => m.Subject));
        Assert.Equal(2, (await h.Processor.ProcessAsync()).Sent);
    }

    [Fact]
    public async Task Clean_up_uses_retain_days()
    {
        var h = new Harness();
        h.Options.RetainDays = 30;
        await h.Queue.EnqueueAsync(new EmailMessage("a@example.com", "old") { TextBody = "x" });
        await h.Processor.ProcessAsync();
        h.Clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, await h.Processor.CleanUpAsync());
        h.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await h.Processor.CleanUpAsync());
    }

    public static TheoryData<EmailMessage, string?> Unqueueable() => new()
    {
        { new EmailMessage("not-an-address", "Hi") { TextBody = "x" }, null },
        { new EmailMessage("a@example.com", "") { TextBody = "x" }, null },
        { new EmailMessage("a@example.com", new string('s', 201)) { TextBody = "x" }, null },
        { new EmailMessage("a@example.com", "Hi") { TextBody = "x", ReplyTo = new EmailAddress("r@example.com") }, null },
        { new EmailMessage("a@example.com", "Hi") { TextBody = "x" }, new string('c', 201) },
    };

    [Theory, MemberData(nameof(Unqueueable))]
    public async Task Enqueue_rejects_emails_the_table_cannot_hold_or_that_could_never_be_sent(EmailMessage message, string? category)
    {
        var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Queue.EnqueueAsync(message, category));
        Assert.Equal(new EmailQueueCounts(0, 0, 0), await h.Queue.CountsAsync());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(9, 60)]
    public void Retry_delays_grow_then_stay_hourly(int attempts, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), EmailQueueProcessor.RetryDelay(attempts));
}

public class EmailQueueRegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings, bool withCommunication = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        if (withCommunication) services.AddCommunication(config);
        services.AddEmailQueue(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Uses_the_default_connection_string_for_sql_server()
    {
        using var sp = Build(new() { ["ConnectionStrings:DefaultConnection"] = "Server=.;Database=App;Integrated Security=true" });
        Assert.IsType<SqlServerEmailQueueStore>(sp.GetRequiredService<IEmailQueueStore>());
        Assert.NotNull(sp.GetRequiredService<IEmailQueue>());
        Assert.NotNull(sp.GetRequiredService<EmailQueueProcessor>());
    }

    [Fact]
    public void In_memory_store_needs_no_database()
    {
        using var sp = Build(new() { ["EmailQueue:Store"] = "inmemory" });
        Assert.IsType<InMemoryEmailQueueStore>(sp.GetRequiredService<IEmailQueueStore>());
    }

    [Fact]
    public void A_store_registered_by_the_app_wins()
    {
        var services = new ServiceCollection().AddLogging();
        var mine = new InMemoryEmailQueueStore();
        services.AddSingleton<IEmailQueueStore>(mine);
        services.AddCommunication(new ConfigurationBuilder().Build());
        services.AddEmailQueue(new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();
        Assert.Same(mine, sp.GetRequiredService<IEmailQueueStore>());
    }

    [Theory]
    [InlineData("EmailQueue:Store", "SqlServer")]          // no connection string anywhere
    [InlineData("EmailQueue:Store", "Redis")]
    public void Misconfiguration_fails_at_startup(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => Build(new() { [key] = value }));

    [Fact]
    public void A_bad_schema_name_fails_at_startup() =>
        Assert.Throws<ArgumentException>(() => Build(new() { ["ConnectionStrings:DefaultConnection"] = "Server=.", ["EmailQueue:Schema"] = "dbo]; DROP TABLE x;--" }));

    [Fact]
    public void Needs_add_communication_first()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new() { ["EmailQueue:Store"] = "InMemory" }, withCommunication: false));
        Assert.Contains("AddCommunication", ex.Message);
    }
}

/// <summary>A store for one test; the SqlServer one gets its own schema, dropped on dispose.</summary>
internal sealed class TestStore : IAsyncDisposable
{
    public static string? SqlConnectionString => Environment.GetEnvironmentVariable("COMMUNICATION_TEST_SQLSERVER") is { Length: > 0 } cs ? cs : null;

    private readonly string? _schema;
    public IEmailQueueStore Store { get; }

    private TestStore(IEmailQueueStore store, string? schema)
    {
        Store = store;
        _schema = schema;
    }

    public static async Task<TestStore> CreateAsync(string kind)
    {
        if (kind == EmailQueueStores.InMemory) return new TestStore(new InMemoryEmailQueueStore(), null);
        var schema = "t" + Guid.NewGuid().ToString("N")[..12];
        var store = new SqlServerEmailQueueStore(SqlConnectionString!, schema);
        await store.PrepareSchemaAsync();
        return new TestStore(store, schema);
    }

    public async Task<Guid> AddAsync(EmailMessage message, DateTime date, string? category = null, string? template = null, bool enableSsl = false)
    {
        var email = new QueuedEmail { Message = message, Category = category, Template = template, Date = date, EnableSsl = enableSsl };
        await Store.AddAsync(email, default);
        return email.Id;
    }

    public async ValueTask DisposeAsync()
    {
        if (_schema is null) return;
        await using var c = new SqlConnection(SqlConnectionString);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(
            $"DROP TABLE [{_schema}].[EmailQueueItemAttachments]; DROP TABLE [{_schema}].[EmailQueueItems]; DROP SCHEMA [{_schema}];", c);
        await cmd.ExecuteNonQueryAsync();
    }
}

internal sealed class ManualClock(DateTime start) : TimeProvider
{
    public DateTime Now { get; private set; } = start;
    public void Advance(TimeSpan by) => Now += by;
    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

internal sealed class ScriptedSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];
    /// <summary>Returns the exception to throw for a message, or null to send it.</summary>
    public Func<EmailMessage, Exception?>? Fail { get; set; }

    public Task<SendResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (Fail?.Invoke(message) is { } ex) throw ex;
        Sent.Add(message);
        return Task.FromResult(new SendResult("Fake", $"msg-{Sent.Count}"));
    }
}
