using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Data;

/// <summary>
/// What a stage's last range execution did, for an operator to read before starting
/// another [3.6].
///
/// **Nothing resumes from this.** A ticker-partitioned sweep resumes on its own attempt
/// record and a date-partitioned one on `run_date`, so this row is reported and never
/// consulted. It carried a parsed-out ticker position until 0010, and three real
/// failures in one day produced that position once.
/// </summary>
/// <param name="LastDateCovered">
/// `run_date` on the newest range row. A completed or halted execution records the
/// date it reached; a failed one records its range start, being the only date it can
/// prove.
/// </param>
/// <param name="Status">`ok`, `halted` or `failed`.</param>
/// <param name="RunLogId">
/// The row, so a message can name the thing the operator has to look at rather than
/// describe it.
/// </param>
/// <param name="Detail">The line as recorded, which opens with the range it covered.</param>
public sealed record RangeRunReport(
    DateOnly LastDateCovered, string Status, long RunLogId, string? Detail)
{
    public bool WasHalted => string.Equals(Status, "halted", StringComparison.Ordinal);
}

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
    /// What the last range execution of <paramref name="stage"/> did, or null where it
    /// has never run one [3.6].
    ///
    /// **Reported, never consulted.** Resumption reads the stage's attempt record, so
    /// this exists to let an operator see what happened last time before spending a day
    /// of allowance, and nothing branches on it.
    ///
    /// **Highest `run_date`, tie-broken on `run_log_id` descending**, so two executions
    /// ending on one date resolve to the later one rather than to whichever the planner
    /// happened to emit first [`CLAUDE.md` §6].
    ///
    /// **A range row is one whose line opens `range `**, which `BackfillRun.Describe`
    /// guarantees. `error` is the only free-text column this table has, so that prefix
    /// is the only marker available without a schema change.
    /// </summary>
    public async Task<RangeRunReport?> LastRangeRunAsync(string stage, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT run_date, status, error, run_log_id
            FROM run_log
            WHERE stage = @stage AND error LIKE 'range %'
            ORDER BY run_date DESC, run_log_id DESC
            LIMIT 1;
            """, conn);
        cmd.Parameters.AddWithValue("stage", stage);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new RangeRunReport(
            DateOnly.FromDateTime(reader.GetDateTime(0)),
            reader.GetString(1),
            reader.GetInt64(3),
            reader.IsDBNull(2) ? null : reader.GetString(2));
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
