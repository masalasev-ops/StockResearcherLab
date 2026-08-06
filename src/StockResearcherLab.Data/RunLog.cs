using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Data;

/// <summary>One row of run_log, as SCHEMA.md declares the table.</summary>
public sealed record RunLogEntry(
    long RunLogId,
    DateOnly RunDate,
    string Stage,
    string Status,
    DateTimeOffset StartedAt,
    long? DurationMs,
    long? RowsWritten,
    string? Error);

/// <summary>
/// C27 RunLog. Records status, duration and row count per stage.
///
/// Not a stage. It sits outside the layers in ARCHITECTURE.html section 3 and
/// still owns a table, which is why it is an <see cref="IWriteOwner"/> and
/// appears in the registry: a writer the conformance test cannot see is a writer
/// INVARIANT 10 is not enforced against.
///
/// The only component that writes run_log. C07 FreshnessGuard and C32
/// LocalModelClient emit through it rather than writing it themselves [N.3].
/// </summary>
public sealed class RunLog : IWriteOwner
{
    private readonly string _connectionString;

    public RunLog(string connectionString) => _connectionString = connectionString;

    public string Name => "RunLog";

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("run_log", WriteOperation.Insert)];

    public async Task<long> RecordAsync(
        DateOnly runDate,
        string stage,
        string status,
        DateTimeOffset startedAt,
        long? durationMs,
        long? rowsWritten,
        string? error,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO run_log (run_date, stage, status, started_at, duration_ms, rows_written, error)
            VALUES (@run_date, @stage, @status, @started_at, @duration_ms, @rows_written, @error)
            RETURNING run_log_id;
            """, conn);

        cmd.Parameters.AddWithValue("run_date", runDate);
        cmd.Parameters.AddWithValue("stage", stage);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("started_at", startedAt);
        cmd.Parameters.AddWithValue("duration_ms", (object?)durationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rows_written", (object?)rowsWritten ?? DBNull.Value);
        cmd.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);

        var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(id, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The most recent runs, newest first, for the viewer and for the Api.
    /// Ordered in SQL rather than in memory so the order does not depend on
    /// anything the caller happens to do afterwards.
    /// </summary>
    public async Task<IReadOnlyList<RunLogEntry>> RecentAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT run_log_id, run_date, stage, status, started_at, duration_ms, rows_written, error
            FROM run_log
            ORDER BY run_date DESC, started_at DESC, run_log_id DESC
            LIMIT @limit;
            """, conn);
        cmd.Parameters.AddWithValue("limit", limit);

        var rows = new List<RunLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new RunLogEntry(
                reader.GetInt64(0),
                DateOnly.FromDateTime(reader.GetDateTime(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }
}
