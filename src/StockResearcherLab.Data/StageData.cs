using Npgsql;
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

    public StageData(string connectionString, DeclaredAccess access)
    {
        _connectionString = connectionString;
        _access = access;
    }

    public async Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
        string table, string sql, CancellationToken ct = default)
    {
        _access.EnsureCanRead(table);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
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

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
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
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var staging = "srl_stage_" + table.Replace('.', '_');
        var columnList = string.Join(", ", columns.Select(Quote));

        // CREATE TABLE AS ... WITH NO DATA rather than LIKE. LIKE copies NOT NULL,
        // which puts a NOT NULL identity primary key into the staging table that
        // the COPY never writes, so the copy fails on a constraint the target
        // generates for itself. That is every table with a surrogate key: order,
        // position, and insider_transaction and events, which are the two A7 and
        // A11 exist for. AS SELECT takes the column types and none of the
        // constraints, which is exactly what a staging table wants [A25].
        //
        // The table goes when the connection closes; there is no transaction here
        // and none is wanted, because phase 3 loads five years through this path.
        await ExecuteAsync(conn,
            $"CREATE TEMP TABLE {Quote(staging)} AS SELECT {columnList} FROM {Quote(table)} WITH NO DATA;",
            ct).ConfigureAwait(false);

        await using (var importer = await conn.BeginBinaryImportAsync(
            $"COPY {Quote(staging)} ({columnList}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false))
        {
            await write(new NpgsqlBulkWriter(importer), ct).ConfigureAwait(false);
            await importer.CompleteAsync(ct).ConfigureAwait(false);
        }

        // The updatable columns are everything the caller writes that is not part
        // of the key, so a re-run replaces values without touching the key.
        var updatable = columns
            .Where(c => !conflictTarget.Contains(c, StringComparer.Ordinal))
            .ToList();

        var action = updatable.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updatable.Select(c => $"{Quote(c)} = EXCLUDED.{Quote(c)}"));

        var upsert =
            $"INSERT INTO {Quote(table)} ({columnList}) SELECT {columnList} FROM {Quote(staging)} " +
            $"ON CONFLICT ({string.Join(", ", conflictTarget.Select(Quote))}) {action};";

        var affected = await ExecuteAsync(conn, upsert, ct).ConfigureAwait(false);

        await ExecuteAsync(conn, $"DROP TABLE IF EXISTS {Quote(staging)};", ct).ConfigureAwait(false);

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

    private static string Quote(string identifier) => "\"" + identifier + "\"";

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
    }
}
