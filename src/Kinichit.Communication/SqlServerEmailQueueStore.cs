using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Kinichit.Communication;

/// <summary>
/// Keeps the queue in the app's own SQL Server database, in EngageNow's layout: <c>[dbo].[EmailQueueItems]</c>, plus
/// <c>[dbo].[EmailQueueItemAttachments]</c> for attachment contents. The tables are created on first use (like Hangfire's), so no
/// app migration is needed.
/// </summary>
public sealed partial class SqlServerEmailQueueStore : IEmailQueueStore
{
    private readonly string _connectionString;
    private readonly string _schema;
    private readonly bool _prepareSchema;
    private readonly int _maxAttempts;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;

    private string Q => $"[{_schema}].[EmailQueueItems]";
    private string A => $"[{_schema}].[EmailQueueItemAttachments]";

    public SqlServerEmailQueueStore(string connectionString, string schema = "dbo", bool prepareSchema = true, int maxAttempts = 5)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A connection string is required.", nameof(connectionString));
        if (!SchemaName().IsMatch(schema)) throw new ArgumentException($"'{schema}' is not a valid schema name.", nameof(schema));
        _connectionString = connectionString;
        _schema = schema;
        _prepareSchema = prepareSchema;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex SchemaName();

    private const string EntryColumns = "[Id], [IsDeleted], [Category], [Template], [To], [Cc], [Bcc], [Subject], [Retries], [Date], [Attachments]";

    /// <summary>Minutes after Date before a message with this many retries is due again (see <see cref="QueueText.MinutesAfterDate"/>).</summary>
    private const string WaitMinutes = "CASE WHEN [Retries] <= 0 THEN 0 WHEN [Retries] = 1 THEN 1 WHEN [Retries] = 2 THEN 6 WHEN [Retries] = 3 THEN 21 ELSE 81 + 60 * ([Retries] - 4) END";

    public async Task AddAsync(QueuedEmail email, CancellationToken ct)
    {
        var m = email.Message;
        var html = m.HtmlBody is not null;
        await using var c = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        await using (var cmd = Command(c, tx, $"""
            INSERT INTO {Q} ([IsDeleted], [Id], [Body], [Date], [EnableSsl], [Html], [SenderAddress], [SenderName], [Subject], [To], [Attachments],
                [Bcc], [Cc], [Retries], [Category], [Template])
            VALUES (0, @id, @body, @date, @ssl, @html, @senderAddress, @senderName, @subject, @to, @attachments, @bcc, @cc, 0, @category, @template)
            """))
        {
            cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = email.Id;
            cmd.Parameters.Add("@body", SqlDbType.NVarChar, -1).Value = (object?)(html ? m.HtmlBody : m.TextBody) ?? DBNull.Value;
            cmd.Parameters.Add("@date", SqlDbType.DateTime2).Value = email.Date;
            cmd.Parameters.Add("@ssl", SqlDbType.Bit).Value = email.EnableSsl;
            cmd.Parameters.Add("@html", SqlDbType.Bit).Value = html;
            cmd.Parameters.Add("@senderAddress", SqlDbType.NVarChar, 200).Value = (object?)m.From?.Address ?? DBNull.Value;
            cmd.Parameters.Add("@senderName", SqlDbType.NVarChar, 200).Value = (object?)m.From?.Name ?? DBNull.Value;
            cmd.Parameters.Add("@subject", SqlDbType.NVarChar, 200).Value = m.Subject;
            cmd.Parameters.Add("@to", SqlDbType.NVarChar, 500).Value = (object?)QueueText.JoinOrNull(m.To) ?? DBNull.Value;
            cmd.Parameters.Add("@attachments", SqlDbType.NVarChar, 200).Value = (object?)QueueText.AttachmentNames(m.Attachments) ?? DBNull.Value;
            cmd.Parameters.Add("@bcc", SqlDbType.NVarChar, 1000).Value = (object?)QueueText.JoinOrNull(m.Bcc) ?? DBNull.Value;
            cmd.Parameters.Add("@cc", SqlDbType.NVarChar, 1000).Value = (object?)QueueText.JoinOrNull(m.Cc) ?? DBNull.Value;
            cmd.Parameters.Add("@category", SqlDbType.NVarChar, 200).Value = (object?)email.Category ?? DBNull.Value;
            cmd.Parameters.Add("@template", SqlDbType.VarChar, 200).Value = (object?)email.Template ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        for (var i = 0; i < m.Attachments.Count; i++)
        {
            var a = m.Attachments[i];
            await using var cmd = Command(c, tx,
                $"INSERT INTO {A} ([EmailQueueItemId], [Ordinal], [FileName], [ContentType], [Content]) VALUES (@id, @ordinal, @name, @type, @content)");
            cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = email.Id;
            cmd.Parameters.Add("@ordinal", SqlDbType.Int).Value = i;
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 260).Value = a.FileName;
            cmd.Parameters.Add("@type", SqlDbType.NVarChar, 200).Value = a.ContentType;
            cmd.Parameters.Add("@content", SqlDbType.VarBinary, -1).Value = a.Content;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IEmailQueueLease?> TakeNextAsync(DateTime nowUtc, CancellationToken ct)
    {
        var c = await OpenAsync(ct);
        SqlTransaction? tx = null;
        try
        {
            tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            // UPDLOCK keeps the row locked until the lease commits or rolls back; READPAST makes other runs skip it.
            QueuedEmail? email = null;
            await using (var cmd = Command(c, tx, $"""
                SELECT TOP (1) [Id], [Body], [Date], [EnableSsl], [Html], [SenderAddress], [SenderName], [Subject], [To], [Bcc], [Cc], [Retries],
                    [Category], [Template]
                FROM {Q} WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE [IsDeleted] = 0 AND [Retries] < @max AND DATEADD(minute, {WaitMinutes}, [Date]) <= @now
                ORDER BY [Date]
                """))
            {
                cmd.Parameters.Add("@max", SqlDbType.Int).Value = _maxAttempts;
                cmd.Parameters.Add("@now", SqlDbType.DateTime2).Value = nowUtc;
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    var html = r.GetBoolean(4);
                    var body = Str(r, 1);
                    email = new QueuedEmail
                    {
                        Id = r.GetGuid(0),
                        Date = Utc(r.GetDateTime(2)),
                        EnableSsl = r.GetBoolean(3),
                        Retries = r.GetInt32(11),
                        Category = Str(r, 12),
                        Template = Str(r, 13),
                        Message = new EmailMessage(QueueText.Parse(Str(r, 8)), r.GetString(7))
                        {
                            Bcc = QueueText.Parse(Str(r, 9)),
                            Cc = QueueText.Parse(Str(r, 10)),
                            From = Str(r, 5) is { } sender ? new EmailAddress(sender, Str(r, 6)) : null,
                            HtmlBody = html ? body : null,
                            TextBody = html ? null : body,
                        },
                    };
                }
            }
            if (email is null)
            {
                await DisposeAsync(c, tx, rollback: true);
                return null;
            }

            var attachments = new List<EmailAttachment>();
            await using (var cmd = Command(c, tx,
                $"SELECT [FileName], [ContentType], [Content] FROM {A} WHERE [EmailQueueItemId] = @id ORDER BY [Ordinal]"))
            {
                cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = email.Id;
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) attachments.Add(new EmailAttachment(r.GetString(0), r.GetString(1), (byte[])r[2]));
            }
            email.Message.Attachments.AddRange(attachments);
            return new Lease(this, c, tx, email);
        }
        catch
        {
            await DisposeAsync(c, tx, rollback: true);
            throw;
        }
    }

    private sealed class Lease(SqlServerEmailQueueStore store, SqlConnection c, SqlTransaction tx, QueuedEmail email) : IEmailQueueLease
    {
        private bool _finished;
        public QueuedEmail Email => email;

        public Task CompleteAsync(CancellationToken ct) =>
            FinishAsync($"UPDATE {store.Q} SET [IsDeleted] = 1, [Retries] = [Retries] + 1 WHERE [Id] = @id", ct);

        public Task FailAsync(bool giveUp, CancellationToken ct) => FinishAsync(giveUp
            ? $"UPDATE {store.Q} SET [Retries] = CASE WHEN [Retries] + 1 > {store._maxAttempts} THEN [Retries] + 1 ELSE {store._maxAttempts} END WHERE [Id] = @id"
            : $"UPDATE {store.Q} SET [Retries] = [Retries] + 1 WHERE [Id] = @id", ct);

        private async Task FinishAsync(string sql, CancellationToken ct)
        {
            if (_finished) throw new InvalidOperationException("This email was already completed or failed.");
            await using (var cmd = Command(c, tx, sql))
            {
                cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = email.Id;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            _finished = true;
        }

        public async ValueTask DisposeAsync() => await SqlServerEmailQueueStore.DisposeAsync(c, tx, rollback: !_finished);
    }

    public async Task<EmailQueuePage> ListAsync(EmailQueueFilter filter, CancellationToken ct)
    {
        var (page, size) = QueueText.Paging(filter);
        var where = new List<string>();
        await using var c = await OpenAsync(ct);
        await using var count = Command(c, null, "");
        await using var list = Command(c, null, "");
        void Param(string name, SqlDbType type, int size, object value)
        {
            count.Parameters.Add(name, type, size).Value = value;
            list.Parameters.Add(name, type, size).Value = value;
        }
        if (filter.Status is { } status) where.Add(StatusCondition(status));
        if (!string.IsNullOrWhiteSpace(filter.Category)) { where.Add("[Category] = @category"); Param("@category", SqlDbType.NVarChar, 200, filter.Category.Trim()); }
        if (!string.IsNullOrWhiteSpace(filter.Template)) { where.Add("[Template] = @template"); Param("@template", SqlDbType.VarChar, 200, filter.Template.Trim()); }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            where.Add(@"([To] LIKE @search ESCAPE '\' OR [Cc] LIKE @search ESCAPE '\' OR [Bcc] LIKE @search ESCAPE '\' OR [Subject] LIKE @search ESCAPE '\')");
            Param("@search", SqlDbType.NVarChar, 1100, "%" + LikeEscape().Replace(filter.Search.Trim(), @"\$0") + "%");
        }
        if (filter.FromUtc is { } from) { where.Add("[Date] >= @from"); Param("@from", SqlDbType.DateTime2, 0, from); }
        if (filter.ToUtc is { } to) { where.Add("[Date] < @to"); Param("@to", SqlDbType.DateTime2, 0, to); }
        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);

        count.CommandText = $"SELECT COUNT(*) FROM {Q} {whereSql}";
        var total = (int)(await count.ExecuteScalarAsync(ct))!;

        list.CommandText = $"SELECT {EntryColumns} FROM {Q} {whereSql} ORDER BY [Date] DESC, [Id] OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
        list.Parameters.Add("@skip", SqlDbType.Int).Value = (page - 1) * size;
        list.Parameters.Add("@take", SqlDbType.Int).Value = size;
        var items = new List<EmailQueueEntry>();
        await using (var r = await list.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) items.Add(ReadEntry(r));
        return new EmailQueuePage(items, total, page, size);
    }

    private string StatusCondition(EmailQueueStatus status) => status switch
    {
        EmailQueueStatus.Sent => "[IsDeleted] = 1",
        EmailQueueStatus.Failed => $"([IsDeleted] = 0 AND [Retries] >= {_maxAttempts})",
        _ => $"([IsDeleted] = 0 AND [Retries] < {_maxAttempts})",
    };

    [GeneratedRegex(@"[\\%_\[]")]
    private static partial Regex LikeEscape();

    public async Task<EmailQueueEntryDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Command(c, null, $"SELECT {EntryColumns}, [SenderAddress], [SenderName], [Body], [Html], [EnableSsl] FROM {Q} WHERE [Id] = @id");
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new EmailQueueEntryDetail(ReadEntry(r), Str(r, 11), Str(r, 12), Str(r, 13), r.GetBoolean(14), r.GetBoolean(15));
    }

    public async Task<EmailQueueCounts> CountsAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Command(c, null, $"""
            SELECT ISNULL(SUM(CASE WHEN {StatusCondition(EmailQueueStatus.Pending)} THEN 1 ELSE 0 END), 0),
                   ISNULL(SUM(CASE WHEN {StatusCondition(EmailQueueStatus.Sent)} THEN 1 ELSE 0 END), 0),
                   ISNULL(SUM(CASE WHEN {StatusCondition(EmailQueueStatus.Failed)} THEN 1 ELSE 0 END), 0)
            FROM {Q}
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new EmailQueueCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
    }

    public async Task<bool> RetryAsync(Guid id, DateTime nowUtc, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Command(c, null,
            $"UPDATE {Q} SET [Retries] = 0, [Date] = @now WHERE [Id] = @id AND {StatusCondition(EmailQueueStatus.Failed)}");
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        cmd.Parameters.Add("@now", SqlDbType.DateTime2).Value = nowUtc;
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> DeleteFinishedAsync(DateTime beforeUtc, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        var total = 0;
        while (true)
        {
            // Small batches keep the locks short on a busy table; attachments go with ON DELETE CASCADE.
            await using var cmd = Command(c, null, $"""
                DELETE TOP (1000) FROM {Q}
                WHERE ({StatusCondition(EmailQueueStatus.Sent)} OR {StatusCondition(EmailQueueStatus.Failed)}) AND [Date] < @before
                """);
            cmd.Parameters.Add("@before", SqlDbType.DateTime2).Value = beforeUtc;
            var n = await cmd.ExecuteNonQueryAsync(ct);
            total += n;
            if (n < 1000) return total;
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        if (_prepareSchema && !_schemaReady) await PrepareSchemaAsync(ct);
        var c = new SqlConnection(_connectionString);
        await c.OpenAsync(ct);
        return c;
    }

    /// <summary>Creates the schema and tables when missing. Safe to run from several app instances at once (sp_getapplock).</summary>
    public async Task PrepareSchemaAsync(CancellationToken ct = default)
    {
        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await using var c = new SqlConnection(_connectionString);
            await c.OpenAsync(ct);
            var s = _schema;
            await using var cmd = Command(c, null, $"""
                EXEC sp_getapplock @Resource = N'Kinichit.Communication.EmailQueue.Schema', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 30000;
                IF SCHEMA_ID(N'{s}') IS NULL EXEC(N'CREATE SCHEMA [{s}]');
                IF OBJECT_ID(N'[{s}].[EmailQueueItems]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [{s}].[EmailQueueItems] (
                        [IsDeleted] bit NOT NULL,
                        [Id] uniqueidentifier NOT NULL,
                        [Body] nvarchar(max) NULL,
                        [Date] datetime2(7) NOT NULL,
                        [EnableSsl] bit NOT NULL,
                        [Html] bit NOT NULL,
                        [SenderAddress] nvarchar(200) NULL,
                        [SenderName] nvarchar(200) NULL,
                        [Subject] nvarchar(200) NOT NULL,
                        [To] nvarchar(500) NULL,
                        [Attachments] nvarchar(200) NULL,
                        [Bcc] nvarchar(1000) NULL,
                        [Cc] nvarchar(1000) NULL,
                        [Retries] int NOT NULL,
                        [Category] nvarchar(200) NULL,
                        [Template] varchar(200) NULL,
                        CONSTRAINT [PK_{s}_EmailQueueItems] PRIMARY KEY NONCLUSTERED ([Id])
                    );
                    CREATE CLUSTERED INDEX [CX_EmailQueueItems_Date] ON [{s}].[EmailQueueItems] ([Date]);
                    CREATE INDEX [IX_EmailQueueItems_Unsent] ON [{s}].[EmailQueueItems] ([IsDeleted], [Retries], [Date]);
                END;
                IF OBJECT_ID(N'[{s}].[EmailQueueItemAttachments]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [{s}].[EmailQueueItemAttachments] (
                        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_{s}_EmailQueueItemAttachments] PRIMARY KEY,
                        [EmailQueueItemId] uniqueidentifier NOT NULL
                            CONSTRAINT [FK_{s}_EmailQueueItemAttachments_EmailQueueItems] REFERENCES [{s}].[EmailQueueItems] ([Id]) ON DELETE CASCADE,
                        [Ordinal] int NOT NULL,
                        [FileName] nvarchar(260) NOT NULL,
                        [ContentType] nvarchar(200) NOT NULL,
                        [Content] varbinary(max) NOT NULL
                    );
                    CREATE INDEX [IX_EmailQueueItemAttachments_EmailQueueItemId] ON [{s}].[EmailQueueItemAttachments] ([EmailQueueItemId]);
                END;
                EXEC sp_releaseapplock @Resource = N'Kinichit.Communication.EmailQueue.Schema', @LockOwner = N'Session';
                """);
            await cmd.ExecuteNonQueryAsync(ct);
            _schemaReady = true;
        }
        finally { _schemaLock.Release(); }
    }

    private EmailQueueEntry ReadEntry(SqlDataReader r) => new(
        r.GetGuid(0), QueueText.StatusOf(r.GetBoolean(1), r.GetInt32(8), _maxAttempts), Str(r, 2), Str(r, 3), Str(r, 4) ?? "", Str(r, 5), Str(r, 6),
        r.GetString(7), r.GetInt32(8), Utc(r.GetDateTime(9)), Str(r, 10));

    private static async Task DisposeAsync(SqlConnection c, SqlTransaction? tx, bool rollback)
    {
        if (tx is not null)
        {
            if (rollback)
            {
                try { await tx.RollbackAsync(); }
                catch (Exception) { /* connection already broken: the server rolls back on its own */ }
            }
            await tx.DisposeAsync();
        }
        await c.DisposeAsync();
    }

    private static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private static SqlCommand Command(SqlConnection c, SqlTransaction? tx, string sql) => new(sql, c, tx) { CommandTimeout = 60 };
}
