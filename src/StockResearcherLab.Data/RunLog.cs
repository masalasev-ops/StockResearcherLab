using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Data;

/// <summary>
/// Where a stage's next range execution picks up [3.6].
/// </summary>
/// <param name="LastDateCovered">
/// `run_date` on the newest range row. A completed or halted execution records the
/// date it reached; a failed one records its range start, being the only position it
/// can prove.
/// </param>
/// <param name="Position">
/// The ticker a halted sweep stopped at, parsed back out of the run log line. Null for
/// a date-partitioned execution and for a failed one, which records no position.
/// </param>
/// <param name="Status">`ok`, `halted` or `failed`.</param>
/// <param name="RunLogId">
/// The row, so a refusal can name the thing the operator has to look at rather than
/// describe it.
/// </param>
/// <param name="From">
/// The range start the row records, or null where the line cannot be parsed for one.
/// **A resume point belongs to the range that produced it** [item 22]: the position is
/// a ticker, and which pool that ticker indexes into is decided by the range, so a
/// position taken from a narrower range resumes into a wider pool and skips every name
/// the narrow one did not contain.
/// </param>
/// <param name="To">The range end the row records, on the same reasoning.</param>
public sealed record ResumePoint(
    DateOnly LastDateCovered, string? Position, string Status, long RunLogId,
    DateOnly? From, DateOnly? To)
{
    /// <summary>
    /// Whether this row was produced by the range now being asked for. A row whose
    /// range could not be parsed matches nothing, which fails towards the refusal
    /// rather than towards a resume.
    /// </summary>
    public bool CoversRange(DateOnly from, DateOnly to) => From == from && To == to;

    public bool WasHalted => string.Equals(Status, "halted", StringComparison.Ordinal);

    /// <summary>The recorded range as it appears in the line, for a message to quote.</summary>
    public string RecordedRange =>
        From is DateOnly f && To is DateOnly t
            ? f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".." +
              t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "(unparsed)";
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
    /// The furthest point any range execution of <paramref name="stage"/> can prove it
    /// reached, or null where it has never run one [3.6].
    ///
    /// **Highest `run_date`, not newest row, and not filtered by status.** A halted row
    /// carries the date it reached and a failed one carries its range start, so every
    /// row states a position it can prove and the maximum over them is the answer. A
    /// status filter would be a second rule the caller has to remember; the rows are
    /// written so that none is needed.
    ///
    /// The tie-break is `run_log_id` descending, so two executions ending on one date
    /// resolve to the later one rather than to whichever the planner happened to emit
    /// first [`CLAUDE.md` §6].
    ///
    /// **A range row is one whose line opens `range `**, which `BackfillRun.Describe`
    /// guarantees. `error` is the only free-text column this table has, so that prefix
    /// is the only marker available without a schema change.
    ///
    /// **One row decides everything and it is the newest**, which is what makes the
    /// range check a comparison rather than a search [item 22]. Returning the newest
    /// row whose range happened to match would let an old matching halt outrank a newer
    /// execution over a different range, and data has been written since that newer one.
    /// So this reads the last thing that happened and `BackfillRun` decides what it
    /// means: matching resumes, differing refuses.
    /// </summary>
    public async Task<ResumePoint?> LastRangeRunAsync(string stage, CancellationToken ct = default)
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

        var error = reader.IsDBNull(2) ? null : reader.GetString(2);
        var (from, to) = RangeIn(error);

        return new ResumePoint(
            DateOnly.FromDateTime(reader.GetDateTime(0)),
            PositionIn(error),
            reader.GetString(1),
            reader.GetInt64(3),
            from,
            to);
    }

    /// <summary>
    /// The range out of `range 2021-01-04..2021-01-08, reached ...`, or two nulls where
    /// the line does not open with one.
    ///
    /// Anchored at the start and exact on the format, because `BackfillRun.Range`
    /// composes it in invariant culture and a permissive parse here would accept a
    /// shape that side never writes. A line this cannot read yields nulls, which
    /// `CoversRange` turns into a refusal rather than into a resume.
    /// </summary>
    private static (DateOnly? From, DateOnly? To) RangeIn(string? error)
    {
        if (error is null)
        {
            return (null, null);
        }

        var match = RangePattern.Match(error);

        return match.Success
               && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
               && DateOnly.TryParseExact(match.Groups[2].Value, "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
            ? (from, to)
            : (null, null);
    }

    private static readonly Regex RangePattern = new(
        @"^range (\d{4}-\d{2}-\d{2})\.\.(\d{4}-\d{2}-\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The ticker out of `..., reached 2021-06-30 at AAPL.US. ...`, or null where the
    /// line records no position.
    ///
    /// Parsed rather than stored in a column of its own, because a column is a schema
    /// change and this is one writer and one reader of one sentence that
    /// `BackfillRun.Describe` composes. Recorded as a limit rather than discovered: if
    /// a second thing ever needs the position, it gets a column.
    ///
    /// **The sentence ends at a period followed by a space, never at the first
    /// period.** Every ticker in this system carries one, so splitting on a bare `.`
    /// returns `L07` for `L07.US` and the resumption still looks plausible: the
    /// truncated form sorts just below the real one, so the sweep resumes at the right
    /// place and reports the wrong ticker. Found by a test asserting what the run log
    /// says rather than only how many calls followed.
    /// </summary>
    internal static string? PositionIn(string? error)
    {
        if (error is null)
        {
            return null;
        }

        const string marker = " at ";
        var at = error.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = error[(at + marker.Length)..];
        var stop = rest.IndexOf(". ", StringComparison.Ordinal);
        var position = (stop < 0 ? rest : rest[..stop]).Trim().TrimEnd('.');

        return position.Length == 0 ? null : position;
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
