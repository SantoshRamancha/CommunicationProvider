namespace Kinichit.Communication;

/// <summary>Keeps the queue in memory, with the same rules as the SQL Server store. For tests only: everything is lost when the app stops.</summary>
public sealed class InMemoryEmailQueueStore(int maxAttempts = 5) : IEmailQueueStore
{
    private sealed class Row
    {
        public required QueuedEmail Email;
        public bool IsDeleted;
        public int Retries;
        public DateTime Date;
        public bool Taken;
    }

    private readonly int _maxAttempts = Math.Max(1, maxAttempts);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Row> _rows = [];

    public Task AddAsync(QueuedEmail email, CancellationToken ct)
    {
        lock (_gate) _rows.Add(email.Id, new Row { Email = email, Date = email.Date });
        return Task.CompletedTask;
    }

    public Task<IEmailQueueLease?> TakeNextAsync(DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            var row = _rows.Values
                .Where(r => !r.Taken && !r.IsDeleted && r.Retries < _maxAttempts && r.Date.AddMinutes(QueueText.MinutesAfterDate(r.Retries)) <= nowUtc)
                .OrderBy(r => r.Date).FirstOrDefault();
            if (row is null) return Task.FromResult<IEmailQueueLease?>(null);
            row.Taken = true;
            var email = new QueuedEmail
            {
                Id = row.Email.Id, Message = row.Email.Message, Category = row.Email.Category, Template = row.Email.Template, Date = row.Date,
                EnableSsl = row.Email.EnableSsl, Retries = row.Retries,
            };
            return Task.FromResult<IEmailQueueLease?>(new Lease(this, row, email));
        }
    }

    private sealed class Lease(InMemoryEmailQueueStore store, Row row, QueuedEmail email) : IEmailQueueLease
    {
        private bool _finished;
        public QueuedEmail Email => email;

        public Task CompleteAsync(CancellationToken ct) => Finish(r =>
        {
            r.IsDeleted = true;
            r.Retries++;
        });

        public Task FailAsync(bool giveUp, CancellationToken ct) => Finish(r => r.Retries = giveUp ? Math.Max(r.Retries + 1, store._maxAttempts) : r.Retries + 1);

        private Task Finish(Action<Row> change)
        {
            lock (store._gate)
            {
                if (_finished) throw new InvalidOperationException("This email was already completed or failed.");
                change(row);
                row.Taken = false;
                _finished = true;
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            lock (store._gate) row.Taken = false;
            return ValueTask.CompletedTask;
        }
    }

    public Task<EmailQueuePage> ListAsync(EmailQueueFilter filter, CancellationToken ct)
    {
        var (page, size) = QueueText.Paging(filter);
        lock (_gate)
        {
            IEnumerable<Row> q = _rows.Values;
            if (filter.Status is { } s) q = q.Where(r => Status(r) == s);
            if (!string.IsNullOrWhiteSpace(filter.Category)) q = q.Where(r => string.Equals(r.Email.Category, filter.Category.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filter.Template)) q = q.Where(r => string.Equals(r.Email.Template, filter.Template.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var term = filter.Search.Trim();
                var m = (Row r) => r.Email.Message;
                q = q.Where(r => new[] { QueueText.Join(m(r).To), QueueText.Join(m(r).Cc), QueueText.Join(m(r).Bcc), m(r).Subject }
                    .Any(text => text.Contains(term, StringComparison.OrdinalIgnoreCase)));
            }
            if (filter.FromUtc is { } from) q = q.Where(r => r.Date >= from);
            if (filter.ToUtc is { } to) q = q.Where(r => r.Date < to);
            var all = q.OrderByDescending(r => r.Date).ThenBy(r => r.Email.Id).ToList();
            return Task.FromResult(new EmailQueuePage(all.Skip((page - 1) * size).Take(size).Select(Entry).ToList(), all.Count, page, size));
        }
    }

    public Task<EmailQueueEntryDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var r)) return Task.FromResult<EmailQueueEntryDetail?>(null);
            var m = r.Email.Message;
            var html = m.HtmlBody is not null;
            return Task.FromResult<EmailQueueEntryDetail?>(
                new EmailQueueEntryDetail(Entry(r), m.From?.Address, m.From?.Name, html ? m.HtmlBody : m.TextBody, html, r.Email.EnableSsl));
        }
    }

    public Task<EmailQueueCounts> CountsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            int Count(EmailQueueStatus s) => _rows.Values.Count(r => Status(r) == s);
            return Task.FromResult(new EmailQueueCounts(Count(EmailQueueStatus.Pending), Count(EmailQueueStatus.Sent), Count(EmailQueueStatus.Failed)));
        }
    }

    public Task<bool> RetryAsync(Guid id, DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var r) || Status(r) != EmailQueueStatus.Failed) return Task.FromResult(false);
            r.Retries = 0;
            r.Date = nowUtc;
            return Task.FromResult(true);
        }
    }

    public Task<int> DeleteFinishedAsync(DateTime beforeUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            var old = _rows.Values.Where(r => Status(r) != EmailQueueStatus.Pending && r.Date < beforeUtc).Select(r => r.Email.Id).ToList();
            foreach (var id in old) _rows.Remove(id);
            return Task.FromResult(old.Count);
        }
    }

    private EmailQueueStatus Status(Row r) => QueueText.StatusOf(r.IsDeleted, r.Retries, _maxAttempts);

    private EmailQueueEntry Entry(Row r)
    {
        var m = r.Email.Message;
        return new EmailQueueEntry(r.Email.Id, Status(r), r.Email.Category, r.Email.Template, QueueText.Join(m.To), QueueText.JoinOrNull(m.Cc),
            QueueText.JoinOrNull(m.Bcc), m.Subject, r.Retries, r.Date, QueueText.AttachmentNames(m.Attachments));
    }
}
