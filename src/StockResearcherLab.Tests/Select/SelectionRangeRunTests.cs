using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// Checkpoint 4.14's two refusals, which are the whole safety of the checkpoint.
///
/// **4.14 is the phase's largest irreversible act.** It writes `attribution` rows carrying
/// scores frozen as they stood on the night, and no later pass may rewrite them
/// [INVARIANT 4, D-40, `RUNBOOK.md`]. So the two ways it can go wrong are worth a fixture
/// each, and both are cases where the wrong behaviour produces no error at all.
///
/// **The run itself is not exercised here.** It is a one-off pass over 1,457 sessions
/// against a store this suite does not have, and what is testable is the refusals. The run
/// is recorded in `PROGRESS.md` with the sha and the row count it froze, which is what
/// 4.14's own scope asks for.
/// </summary>
[Collection("database")]
public sealed class SelectionRangeRunTests
{
    private static readonly DateOnly From = new(2022, 4, 4);

    private static readonly DateOnly To = new(2022, 4, 8);

    private const string Ticker = "SRLSEL.AA";

    /// <summary>
    /// **A range whose `attribution` already carries a row is refused, and nothing is
    /// written.**
    ///
    /// Relying on the per-row `ON CONFLICT (ticker, date) DO NOTHING` would be INVARIANT 4
    /// holding at the statement level, and it would leave the run reporting success having
    /// written rows for some dates and skipped others. That half-written state is the
    /// hardest one to reason about afterwards, and it is the state a frozen table cannot
    /// be pulled back out of.
    /// </summary>
    [Fact]
    public async Task ARangeWhoseAttributionHasStartedIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;

        await ClearAsync(ct);

        try
        {
            // Real sessions, because the range check runs first and correctly so: a
            // refusal about a range is not meaningful over a range with no session in it.
            await SeedSessionsAsync(ct);

            await ExecAsync("""
                INSERT INTO attribution
                    (ticker, date, screens_surfacing, surfaced_as, config_version)
                VALUES (@t, @d, ARRAY['S1'], 'candidate', 1);
                """, ct, ("t", Ticker), ("d", From.AddDays(1)));

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Run().RunAsync(From, To, ct));

            // The message names the count and the dates, so an operator meeting it knows
            // whether the record started here or somewhere else in the range.
            Assert.Contains("already holds 1 row(s)", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("never re-run", thrown.Message, StringComparison.Ordinal);

            // And it refused before running anything, so no gate row exists for the range.
            Assert.Equal(0, await GateRowsAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **An empty range is not silently a success.** A range naming no session would run
    /// nothing, write nothing and report a clean pass, which on the checkpoint that starts
    /// the record reads as a record that started.
    /// </summary>
    [Fact]
    public async Task ARangeWithNoSessionFails()
    {
        var ct = TestContext.Current.CancellationToken;

        await ClearAsync(ct);

        // 1998 predates every partition and every price row this suite writes.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run().RunAsync(new DateOnly(1998, 1, 5), new DateOnly(1998, 1, 9), ct));

        Assert.Contains("no session", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("report success", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// **C13 is not in the sequence and that is asserted rather than left to a comment.**
    /// 4.13 filled `screen_score_daily` in two passes, and a re-run of C13 over the same
    /// range would rewrite thirty million rows to the same values while its pass-one upsert
    /// wrote `rank_within_screen` null, clearing ranks pass two had drawn.
    ///
    /// The order is otherwise §03's: C12 before C14 because the allocator joins
    /// `gate_result`, and C28 last because it reads the `candidate_set` C14 just wrote.
    /// </summary>
    [Fact]
    public void TheOrderIsTheSelectionOrderWithoutTheScreenEngine()
    {
        Assert.Equal(
            ["GateEngine", "CandidateAllocator", "ConcentrationMonitor"],
            SelectionRangeRun.Order);

        Assert.DoesNotContain("ScreenEngine", SelectionRangeRun.Order);

        // Every name is a component the nightly selection order also names, so this is a
        // subset of the sequence rather than a second sequence that could disagree.
        Assert.All(SelectionRangeRun.Order, name =>
            Assert.Contains(name, StockResearcherLab.Pipeline.BackfillSequence.SelectionOrder));
    }

    // ---------------------------------------------------------------- helpers ---

    private static SelectionRangeRun Run()
        => new(TestDatabase.ConnectionString, new FrozenClock(From));

    /// <summary>
    /// A session list for the range, which the driver reads out of `price_daily` rather
    /// than off a calendar: a calendar walk would name days the exchange did not trade.
    /// </summary>
    private static async Task SeedSessionsAsync(CancellationToken ct)
    {
        for (var day = From; day <= To; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            await ExecAsync("""
                INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
                VALUES (@t, @d, 100, 100, 100, 100, 100, 1000000)
                ON CONFLICT (ticker, date) DO NOTHING;
                """, ct, ("t", Ticker), ("d", day));
        }
    }

    private static async Task<int> GateRowsAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*)::bigint FROM gate_result WHERE date BETWEEN @f AND @t;", conn);

        cmd.Parameters.AddWithValue("f", From);
        cmd.Parameters.AddWithValue("t", To);

        return (int) (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await ExecAsync("DELETE FROM attribution WHERE date BETWEEN @f AND @t;",
            ct, ("f", From), ("t", To));
        await ExecAsync("DELETE FROM candidate_set WHERE date BETWEEN @f AND @t;",
            ct, ("f", From), ("t", To));
        await ExecAsync("DELETE FROM gate_result WHERE date BETWEEN @f AND @t;",
            ct, ("f", From), ("t", To));
        await ExecAsync("DELETE FROM price_daily WHERE ticker = @t;", ct, ("t", Ticker));
    }

    private static async Task ExecAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Per-file, as every other stage fixture in this suite keeps its own. The clock
    /// reaches the `run_log` row and nothing else: every stage runs on a date the driver
    /// hands it [INVARIANT 11].
    /// </summary>
    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }
}
