using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Data;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// **These asserted through `RecentAsync(200)` and could not keep doing so** [1.8].
/// `run_log` accumulates and is ordered `run_date DESC, started_at DESC`, so a row
/// written at a fixed 2026-08-06 sinks below the window as soon as two hundred rows
/// carry a later run date, which is what the other stages' fixed clocks produce.
/// The round trip then fails on a database that has been used rather than on one
/// that is wrong, and it passed in CI throughout because CI drops the database
/// first.
///
/// A round trip is read back by its own id. `RecentAsync`'s window and ordering are
/// a separate assertion below, made against rows this test wrote rather than
/// against whatever happens to be newest.
/// </summary>
public sealed class RunLogTests
{
    [Fact]
    public async Task ARecordedRunRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var runLog = new RunLog(TestDatabase.ConnectionString);

        // Fixed rather than DateTime.Now. Nothing in a test reads the machine
        // clock either, or the test stops being reproducible for the same reason
        // a stage would [INVARIANT 11].
        var clock = new FixedClock(
            new DateTimeOffset(2026, 8, 6, 22, 30, 0, TimeSpan.Zero),
            new DateOnly(2026, 8, 6));

        var stage = "RunLogRoundTrip-" + Guid.NewGuid().ToString("N")[..8];

        var id = await runLog.RecordAsync(
            clock.Today, stage, "ok", clock.UtcNow,
            durationMs: 1234, rowsWritten: 7, error: null, ct).ConfigureAwait(true);

        Assert.True(id > 0);

        var written = await ByIdAsync(id, ct).ConfigureAwait(true);

        Assert.Equal(stage, written.Stage);
        Assert.Equal(clock.Today, written.RunDate);
        Assert.Equal("ok", written.Status);
        Assert.Equal(1234, written.DurationMs);
        Assert.Equal(7, written.RowsWritten);
        Assert.Null(written.Error);
        Assert.Equal(clock.UtcNow, written.StartedAt);
    }

    [Fact]
    public async Task AFailedRunRecordsItsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var runLog = new RunLog(TestDatabase.ConnectionString);
        var stage = "RunLogFailure-" + Guid.NewGuid().ToString("N")[..8];

        var id = await runLog.RecordAsync(
            new DateOnly(2026, 8, 6), stage, "failed",
            new DateTimeOffset(2026, 8, 6, 22, 31, 0, TimeSpan.Zero),
            durationMs: 12, rowsWritten: null,
            error: "deliberate", ct).ConfigureAwait(true);

        var written = await ByIdAsync(id, ct).ConfigureAwait(true);

        Assert.Equal("failed", written.Status);
        Assert.Equal("deliberate", written.Error);

        // Null means unknown, never zero. A failed stage wrote an unknown number
        // of rows, not zero of them [CLAUDE.md section 6].
        Assert.Null(written.RowsWritten);
    }

    /// <summary>
    /// The viewer's window. Three rows on one run date, written in a known order,
    /// and the newest started_at must come back first however many other rows the
    /// table holds.
    /// </summary>
    [Fact]
    public async Task RecentReturnsTheNewestFirstAndRespectsItsLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var runLog = new RunLog(TestDatabase.ConnectionString);

        // Far enough ahead of every other fixed clock in the suite that these
        // three are the newest rows in the table, which is what lets the ordering
        // be asserted at all.
        var runDate = new DateOnly(2099, 12, 31);
        var tag = Guid.NewGuid().ToString("N")[..8];

        foreach (var minute in new[] { 0, 5, 10 })
        {
            await runLog.RecordAsync(
                runDate, "RunLogRecent-" + tag + "-" + minute.ToString("00", null), "ok",
                new DateTimeOffset(2099, 12, 31, 22, minute, 0, TimeSpan.Zero),
                durationMs: 1, rowsWritten: 0, error: null, ct).ConfigureAwait(true);
        }

        var recent = await runLog.RecentAsync(3, ct).ConfigureAwait(true);

        Assert.Equal(3, recent.Count);
        Assert.Equal(
            ["RunLogRecent-" + tag + "-10", "RunLogRecent-" + tag + "-05", "RunLogRecent-" + tag + "-00"],
            recent.Select(r => r.Stage));

        await ClearAsync(runDate, ct).ConfigureAwait(true);
    }

    private static async Task<RunLogEntry> ByIdAsync(long id, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT run_log_id, run_date, stage, status, started_at, duration_ms, rows_written, error
            FROM run_log WHERE run_log_id = @id;
            """, conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        Assert.True(await r.ReadAsync(ct).ConfigureAwait(false), "No run_log row with id " + id + ".");

        return new RunLogEntry(
            r.GetInt64(0),
            DateOnly.FromDateTime(r.GetDateTime(1)),
            r.GetString(2),
            r.GetString(3),
            r.GetFieldValue<DateTimeOffset>(4),
            await r.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : r.GetInt32(5),
            await r.IsDBNullAsync(6, ct).ConfigureAwait(false) ? null : r.GetInt64(6),
            await r.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : r.GetString(7));
    }

    private static async Task ClearAsync(DateOnly runDate, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM run_log WHERE run_date = @d;", conn);
        cmd.Parameters.AddWithValue("d", runDate);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
