using Npgsql;
using NpgsqlTypes;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Data;

/// <summary>
/// The database behind <see cref="IStageData"/>. Every call checks the named
/// table against the stage's declared sets first, so an undeclared access fails
/// before a connection is touched rather than succeeding quietly.
///
/// The check is here rather than in the stage because a stage checking itself is
/// not a check.
/// </summary>
public sealed class StageData : IStageData
{
    private readonly string _connectionString;
    private readonly DeclaredAccess _access;

    private int _connectionRetries;

    public StageData(string connectionString, DeclaredAccess access)
    {
        _connectionString = connectionString;
        _access = access;
    }

    /// <summary>
    /// How many times establishing a connection had to be retried across this stage's
    /// run. Reported in the run log beside the coverage figures, because a retry that
    /// fires constantly is a pooling problem still present and a count nobody reports
    /// is a symptom nobody sees.
    /// </summary>
    public int ConnectionRetries => Volatile.Read(ref _connectionRetries);

    /// <summary>
    /// Attempts at establishing a connection, this one included. Three, which is two
    /// retries.
    ///
    /// **Only the establishment retries. Not the COPY, not the upsert, not a query.**
    /// A failed write is a lost write for data already fetched and paid for, and
    /// retrying one would eventually mean tolerating one, which reports a completed
    /// sweep over a partial load [`RUNBOOK.md`]. A handshake that did not complete
    /// wrote nothing and read nothing, so asking again is the same request rather than
    /// a second attempt at a side effect.
    ///
    /// **Not a config key**, on the precedent the transport already sets: the rate
    /// limiter's 1,000 a minute and the HTTP handler's pooled-connection lifetime are
    /// constants in code, and config in this system is for values the experiment
    /// turns on [`CLAUDE.md` §8]. This one is a property of the data layer.
    /// </summary>
    private const int OpenAttempts = 3;

    private static readonly TimeSpan[] OpenBackoff =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];

    /// <summary>
    /// An open connection, retrying the handshake and nothing else.
    ///
    /// **`PostgresException` is not retried**, being the server answering rather than
    /// failing to: a bad password or a missing database says the same thing three times
    /// and the delay is spent for nothing.
    ///
    /// **Two shapes are retried and the second was found by testing rather than by
    /// reading** [item 23]. The 3.6 failure was an `NpgsqlException` wrapping a
    /// `TimeoutException`, thrown while reading the server's reply inside
    /// `AuthenticateSASL`. A timeout in the earlier connect phase is not wrapped at
    /// all: it surfaces as a bare `TimeoutException` out of
    /// `NpgsqlConnector.ConnectAsync`. A predicate naming only `NpgsqlException` would
    /// have looked correct, matched the failure that prompted it, and missed the
    /// commoner one.
    ///
    /// **A socket error decides on its code and overrides both shapes** [D-100]. The
    /// predicate above named exception types, and a connect-phase `SocketException` is
    /// neither of them, so the commonest transient database fault was not retried at
    /// all while a wrapped `HostNotFound` was retried three times for nothing.
    /// <see cref="TransientFault"/> names the retryable codes once for this layer and
    /// for the provider client, because two separately reasoned rules for one
    /// distinction drift.
    /// </summary>
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var conn = new NpgsqlConnection(_connectionString);

            try
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                return conn;
            }
            catch (Exception ex) when (
                ShouldRetryOpen(ex) && attempt < OpenAttempts && !ct.IsCancellationRequested)
            {
                await conn.DisposeAsync().ConfigureAwait(false);

                Interlocked.Increment(ref _connectionRetries);

                await Task.Delay(OpenBackoff[attempt - 1], ct).ConfigureAwait(false);
            }
            catch
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// Whether a failed handshake is worth attempting again [D-100].
    ///
    /// **A socket error is decided on its code and nothing else looks at it.** That is
    /// what stops `HostNotFound`, which is a connection string that is wrong, from
    /// spending three attempts and the backoff on an answer that cannot change, and it
    /// stops a bare `ConnectionReset` from failing a sweep on the first occurrence.
    ///
    /// **The type test only runs where there is no socket error to read.** Npgsql
    /// raises timeouts of its own during the handshake with nothing underneath them,
    /// and those stay retryable on the reasoning [item 23] established: a handshake
    /// that did not complete wrote nothing and read nothing.
    /// </summary>
    private static bool ShouldRetryOpen(Exception ex)
        => TransientFault.ClassifySocket(ex) switch
        {
            SocketVerdict.Transient => true,
            SocketVerdict.Permanent => false,
            _ => ex is NpgsqlException and not PostgresException || ex is TimeoutException,
        };

    public async Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
        string table, string sql, CancellationToken ct = default, int? commandTimeoutSeconds = null)
    {
        _access.EnsureCanRead(table);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);

        // Null leaves the connection string's value in force, which is what every
        // statement but the two pool builds uses [D-102].
        if (commandTimeoutSeconds is { } seconds)
        {
            cmd.CommandTimeout = seconds;
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, ct).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public async Task<long> WriteAsync(
        string table, WriteOperation operation, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        _access.EnsureCanWrite(table, operation);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);

        if (parameters is not null)
        {
            // Sorted explicitly. Dictionary enumeration order is unspecified and
            // must never reach output, and a command's parameter order is output
            // in every sense that matters here [CLAUDE.md section 6].
            foreach (var key in parameters.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                cmd.Parameters.AddWithValue(key, parameters[key] ?? DBNull.Value);
            }
        }

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<long> BulkUpsertAsync(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> conflictTarget,
        Func<IBulkWriter, CancellationToken, Task> write,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(conflictTarget);
        ArgumentNullException.ThrowIfNull(write);

        if (columns.Count == 0)
        {
            throw new ArgumentException("A bulk write needs at least one column.", nameof(columns));
        }

        if (conflictTarget.Count == 0)
        {
            throw new ArgumentException(
                "A bulk write needs a conflict target. Every stage write is idempotent on the " +
                "table's own grain, so there is always one [D-68].", nameof(conflictTarget));
        }

        // Before a connection is opened, exactly as the read and write routes do.
        // The fast path must not be the way the guard gets bypassed.
        _access.EnsureCanWrite(table, WriteOperation.Insert);

        // Column-level ownership, enforced rather than declared [A27]. This is the
        // first place a stage states in code exactly which columns it writes, which
        // is what TableWrite.Columns was carried forward from phase 0 waiting for.
        _access.EnsureColumnsDeclared(table, WriteOperation.Insert, columns);

        // A conflict target the write does not supply can never match, so every
        // re-run inserts another row while the statement succeeds and the count
        // climbs. That is D-68 silently not holding, and it is not a SQL error:
        // ON CONFLICT on a generated key is valid and simply never fires [A27].
        var unwritten = conflictTarget
            .Where(c => !columns.Contains(c, StringComparer.Ordinal))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        if (unwritten.Count > 0)
        {
            throw new ArgumentException(
                $"Conflict target column(s) {string.Join(", ", unwritten)} are not written by this " +
                $"bulk load into '{table}'. A target the write does not supply never matches, so the " +
                "upsert inserts a duplicate on every re-run while succeeding. Idempotence on the " +
                "table's own grain is what D-68 requires.", nameof(conflictTarget));
        }

        foreach (var name in columns.Concat(conflictTarget))
        {
            RejectUnsafeIdentifier(name);
        }

        RejectUnsafeIdentifier(table);

        // One connection for the whole operation. The staging table is TEMP and is
        // invisible to any other connection, so the COPY and the upsert cannot be
        // split across two [A24]. TEMP rather than a named UNLOGGED table because a
        // crashed run would leave a named one populated for the next run's insert
        // to pick up, and two stages loading at once would collide on the name.
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // The statements are built in BulkUpsertSql so they can be asserted
        // verbatim without executing, which is the only way to test the quoting of
        // a reserved-word table that carries no upsertable unique index [A25].
        var staging = BulkUpsertSql.StagingNameFor(table);

        await ExecuteAsync(conn, BulkUpsertSql.CreateStaging(staging, table, columns), ct)
            .ConfigureAwait(false);

        await using (var importer = await conn.BeginBinaryImportAsync(
            BulkUpsertSql.Copy(staging, columns), ct).ConfigureAwait(false))
        {
            await write(new NpgsqlBulkWriter(importer), ct).ConfigureAwait(false);
            await importer.CompleteAsync(ct).ConfigureAwait(false);
        }

        var affected = await ExecuteAsync(
            conn, BulkUpsertSql.Upsert(table, staging, columns, conflictTarget), ct).ConfigureAwait(false);

        await ExecuteAsync(conn, BulkUpsertSql.DropStaging(staging), ct).ConfigureAwait(false);

        return affected;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Identifiers are interpolated into DDL, which takes no parameters, so they are
    /// restricted rather than trusted. Every one comes from a stage's own declared
    /// column set today; this is what keeps that true if one ever comes from a
    /// config row.
    /// </summary>
    private static void RejectUnsafeIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException(
                $"'{name}' is not a plain identifier. Table and column names reach DDL, which takes " +
                "no parameters, so they are restricted to letters, digits and underscore.");
        }
    }

    private sealed class NpgsqlBulkWriter(NpgsqlBinaryImporter importer) : IBulkWriter
    {
        public async Task StartRowAsync(CancellationToken ct = default)
            => await importer.StartRowAsync(ct).ConfigureAwait(false);

        public async Task WriteAsync<T>(T? value, CancellationToken ct = default)
        {
            if (value is null)
            {
                await importer.WriteNullAsync(ct).ConfigureAwait(false);
                return;
            }

            await importer.WriteAsync(value, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// The one place a wire type is named rather than inferred.
        ///
        /// Inferred from a string it is `text`, and `text` and `jsonb` differ in
        /// binary COPY by one leading version byte. Postgres then reads the
        /// document's opening brace as that byte, so the failure names a version
        /// number of 123 and neither the column nor the row [2.9, found at 2.12].
        /// </summary>
        public async Task WriteJsonAsync(string? json, CancellationToken ct = default)
        {
            if (json is null)
            {
                await importer.WriteNullAsync(ct).ConfigureAwait(false);
                return;
            }

            await importer.WriteAsync(json, NpgsqlDbType.Jsonb, ct).ConfigureAwait(false);
        }
    }
}
