using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// The frontier rule, without a database [item 60].
///
/// **The first case is the measurement that produced the item**, reproduced as counts.
/// It is the case that matters: the newest date present and the newest date carrying a
/// real bar count differed by three sessions, and taking the first is what wrote 2,864
/// `indicator_daily` rows that were a byte-repeat of the day before.
/// </summary>
public sealed class PriceFrontierRuleTests
{
    private const int Window = 20;
    private const decimal Fraction = 0.95m;

    /// <summary>
    /// The store as it stood on 2026-08-21, newest first: three benchmark-only dates on
    /// top of a settled one. The counts are the measured ones.
    /// </summary>
    private static IReadOnlyList<(DateOnly, long)> Measured()
    {
        var counts = new List<(DateOnly, long)>
        {
            (new DateOnly(2026, 8, 17), 1),
            (new DateOnly(2026, 8, 14), 1),
            (new DateOnly(2026, 8, 13), 1),
            (new DateOnly(2026, 8, 12), 3024),
            (new DateOnly(2026, 8, 11), 3031),
            (new DateOnly(2026, 8, 10), 3031),
            (new DateOnly(2026, 8, 7), 3031),
            (new DateOnly(2026, 8, 6), 3031),
            (new DateOnly(2026, 8, 5), 3032),
            (new DateOnly(2026, 8, 4), 3032),
            (new DateOnly(2026, 8, 3), 3031),
            (new DateOnly(2026, 7, 31), 3034),
            (new DateOnly(2026, 7, 30), 3035),
            (new DateOnly(2026, 7, 29), 3034),
            (new DateOnly(2026, 7, 28), 3035),
            (new DateOnly(2026, 7, 27), 3037),
            (new DateOnly(2026, 7, 24), 3038),
            (new DateOnly(2026, 7, 23), 3035),
            (new DateOnly(2026, 7, 22), 3038),
            (new DateOnly(2026, 7, 21), 3040),
            (new DateOnly(2026, 7, 20), 3039),
            (new DateOnly(2026, 7, 17), 3042),
            (new DateOnly(2026, 7, 16), 3042),
            (new DateOnly(2026, 7, 15), 3042),
        };

        // Enough behind the settled candidate for the median to be taken rather than
        // skipped, which is the branch under test.
        var oldest = new DateOnly(2026, 7, 14);
        for (var i = 0; i < Window; i++)
        {
            counts.Add((oldest.AddDays(-i), 3041));
        }

        return counts;
    }

    /// <summary>
    /// **The anchor.** 2026-08-12 and not 2026-08-17, which is the entire item.
    /// </summary>
    [Fact]
    public void TheFrontierIsTheNewestDateCarryingARealBarCountRatherThanTheNewestDatePresent()
    {
        var verdict = PriceFrontier.Evaluate(Measured(), Window, Fraction);

        Assert.Equal(new DateOnly(2026, 8, 12), verdict.Date);

        // The three it walked past are named with their counts, so the refusal an
        // operator reads says what was wrong rather than only that something was.
        Assert.Contains("Skipped 3 newer date(s)", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("2026-08-17 holds 1 bar(s)", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("2026-08-14 holds 1 bar(s)", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("2026-08-13 holds 1 bar(s)", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("2026-08-12 holds 3,024 bar(s)", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary case, where nothing was skipped. It is stated rather than left
    /// silent, for the reason D-98's rule one table over gives about the zero.
    /// </summary>
    [Fact]
    public void ASettledNewestDateIsTheFrontierAndTheAbsenceOfSkippedDatesIsStated()
    {
        var counts = Measured().Skip(3).ToList();
        var verdict = PriceFrontier.Evaluate(counts, Window, Fraction);

        Assert.Equal(new DateOnly(2026, 8, 12), verdict.Date);
        Assert.Contains("No newer date was skipped.", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// C07's own fallback [A26]. A store with less history than the window cannot have a
    /// median taken over it, and refusing would mean a freshly seeded store can never be
    /// computed over at all. The verdict says which branch it took.
    /// </summary>
    [Fact]
    public void ADateWithLessThanAFullWindowBehindItIsTakenAndTheSkippedCheckIsSaidSo()
    {
        var counts = new List<(DateOnly, long)>
        {
            (new DateOnly(2026, 8, 12), 3024),
            (new DateOnly(2026, 8, 11), 3031),
        };

        var verdict = PriceFrontier.Evaluate(counts, Window, Fraction);

        Assert.Equal(new DateOnly(2026, 8, 12), verdict.Date);
        Assert.Contains("taken without a settledness check, only 1 of 20", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>An empty table has no frontier, and null is not a date.</summary>
    [Fact]
    public void AnEmptyTableHasNoFrontier()
    {
        var verdict = PriceFrontier.Evaluate([], Window, Fraction);

        Assert.Null(verdict.Date);
        Assert.Contains("holds no rows at all", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The boundary is stated rather than inferred. At exactly the floor the date is
    /// settled; one bar below it is not.
    /// </summary>
    [Theory]
    [InlineData(2850, true)]   // 0.95 * 3000 = 2850, exactly the floor
    [InlineData(2849, false)]
    public void TheFloorIsInclusive(long newestRows, bool isFrontier)
    {
        var counts = new List<(DateOnly, long)> { (new DateOnly(2026, 8, 12), newestRows) };
        for (var i = 1; i <= Window; i++)
        {
            counts.Add((new DateOnly(2026, 8, 12).AddDays(-i), 3000));
        }

        // One settled date behind the window, so a rejected candidate still has a
        // frontier to fall back to and the two cases differ only in which date.
        for (var i = 1; i <= Window; i++)
        {
            counts.Add((new DateOnly(2026, 8, 12).AddDays(-Window - i), 3000));
        }

        var verdict = PriceFrontier.Evaluate(counts, Window, Fraction);

        Assert.Equal(
            isFrontier ? new DateOnly(2026, 8, 12) : new DateOnly(2026, 8, 11),
            verdict.Date);
    }
}

/// <summary>
/// The driver refuses a range end past the frontier, asserted in both directions
/// [item 60].
///
/// **The fixture sits in 2030 and it is deliberate.** The frontier is a property of the
/// whole of `price_daily`, so a fixture that made it earlier would be changing what
/// every other class in this suite runs against, and this class is not in the `database`
/// collection. Rows newer than every other fixture can only move the frontier forward,
/// which no other test depends on, and the refusal is then produced by asking for an end
/// past a high frontier rather than by lowering one.
/// </summary>
public sealed class BackfillFrontierTests
{
    private const string Prefix = "SRLFRNT";

    /// <summary>Twenty-five tickers a date, which is the settled population here.</summary>
    private const int Population = 25;

    private static readonly DateOnly Oldest = new(2030, 1, 1);

    /// <summary>The last date carrying the full population, and therefore the frontier.</summary>
    private static readonly DateOnly Frontier = new(2030, 2, 9);

    /// <summary>Benchmark-only dates on top of it, which is the shape item 60 measured.</summary>
    private static readonly DateOnly[] ThinTail =
    [
        new(2030, 2, 10), new(2030, 2, 11), new(2030, 2, 12),
    ];

    private static readonly DateTimeOffset Now = new(2030, 2, 13, 2, 52, 0, TimeSpan.Zero);

    private static IClock Clock => new FixedClock(Now, new DateOnly(2030, 2, 12));

    // ------------------------------------------------------ both directions ---

    /// <summary>
    /// **The refusal.** 2030-02-11 is present in `price_daily` and carries one bar, so a
    /// driver reading the newest date present would accept it and every compute stage
    /// would write a row against a session that never had a population.
    /// </summary>
    [Fact]
    public async Task ARangeEndPastTheFrontierIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await PrepareAsync(ct).ConfigureAwait(true);

        var stage = new CalendarStage();

        var ex = await Assert.ThrowsAsync<RangeEndBeyondFrontierException>(
            () => RunAsync(stage, Oldest.AddDays(30), ThinTail[1], ct)).ConfigureAwait(true);

        Assert.Equal(ThinTail[1], ex.RangeEnd);
        Assert.Equal(Frontier, ex.Frontier);

        // Refused, not clamped. The stage never saw a date set at all.
        Assert.Null(stage.Sessions);

        // The message says what to do and why the narrowing was not done for them.
        Assert.Contains("2030-02-09", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refused rather than clamped", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The other direction, and it is the half that would go silently missing.** A
    /// guard that refuses everything passes the refusal test and is useless, so the
    /// frontier itself is asserted as accepted rather than only a date well inside it.
    /// </summary>
    [Fact]
    public async Task ARangeEndingAtTheFrontierIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await PrepareAsync(ct).ConfigureAwait(true);

        var stage = new CalendarStage();
        var from = Frontier.AddDays(-4);

        var result = await RunAsync(stage, from, Frontier, ct).ConfigureAwait(true);

        Assert.Equal("ok", result.Status);
        Assert.NotNull(stage.Sessions);

        // The sessions are the ones the store holds over that range, ending at the
        // frontier, so the guard passed the real date set through rather than an empty
        // one that would read as a completed run.
        Assert.Equal(
            new[]
            {
                Frontier.AddDays(-4), Frontier.AddDays(-3), Frontier.AddDays(-2),
                Frontier.AddDays(-1), Frontier,
            },
            stage.Sessions!.ToArray());
    }

    /// <summary>
    /// The refusal fails the run and is recorded, rather than being swallowed or
    /// producing a short date set [`CLAUDE.md` §6].
    /// </summary>
    [Fact]
    public async Task TheRefusalFailsTheRunAndLandsInTheRunLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await PrepareAsync(ct).ConfigureAwait(true);

        await Assert.ThrowsAsync<RangeEndBeyondFrontierException>(
            () => RunAsync(new CalendarStage(), Oldest.AddDays(30), ThinTail[2], ct)).ConfigureAwait(true);

        var (status, rowsWritten, error) = await LastRunAsync(CalendarStage.StageName, ct).ConfigureAwait(true);

        Assert.Equal("failed", status);
        Assert.Null(rowsWritten);
        Assert.Contains("range 2030-01-31..2030-02-12 FAILED", error ?? "", StringComparison.Ordinal);
        Assert.Contains("ingest frontier is 2030-02-09", error ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// **The placement, asserted, because getting it wrong would be silent in the other
    /// direction.** A range end past the frontier is an operator error for a compute
    /// stage and the ordinary case for an ingest one: fetching the dates the store does
    /// not hold yet is how the frontier moves at all. So the guard binds the calendar
    /// rather than the run, and a stage that never resolves a date set is never refused.
    ///
    /// A check at the top of `BackfillRun.RunAsync` would pass every test above and make
    /// the backfill unable to extend the store, which no test above would notice.
    /// </summary>
    [Fact]
    public async Task AStageThatNeverResolvesTheCalendarIsNotRefusedAtAll()
    {
        var ct = TestContext.Current.CancellationToken;
        await PrepareAsync(ct).ConfigureAwait(true);

        var stage = new SweepStage();

        // The same range end the compute stage was refused for, and further still.
        var result = await RunAsync(stage, Oldest.AddDays(30), Frontier.AddYears(1), ct).ConfigureAwait(true);

        Assert.Equal("ok", result.Status);
        Assert.True(stage.Ran);
    }

    // ------------------------------------------------------------ harness ---

    private static async Task<BackfillResult> RunAsync(
        IBackfillStage stage, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var run = new BackfillRun(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            Clock,
            TestDatabase.ConnectionString,
            new StubAllowance(DateOnly.FromDateTime(Now.UtcDateTime)));

        return await run.RunAsync(stage.Name, from, to, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Config, then the bars. `ON CONFLICT DO NOTHING`, so a second test class running
    /// concurrently finds the rows already there rather than racing to write them.
    /// </summary>
    private static async Task PrepareAsync(CancellationToken ct)
    {
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(false);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        // The settled block: Population tickers on every date up to and including the
        // frontier.
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            SELECT @p || lpad(t::text, 3, '0') || '.US', d::date, 10, 11, 9, 10, 10, 100000
            FROM generate_series(DATE '2030-01-01', DATE '2030-02-09', INTERVAL '1 day') AS d,
                 generate_series(1, @n) AS t
            ON CONFLICT (ticker, date) DO NOTHING;
            """, conn))
        {
            cmd.Parameters.AddWithValue("p", Prefix);
            cmd.Parameters.AddWithValue("n", Population);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // The tail: one ticker only, which is the benchmark-load shape D-104 produced.
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            SELECT @p || '001.US', d::date, 10, 11, 9, 10, 10, 100000
            FROM generate_series(DATE '2030-02-10', DATE '2030-02-12', INTERVAL '1 day') AS d
            ON CONFLICT (ticker, date) DO NOTHING;
            """, conn))
        {
            cmd.Parameters.AddWithValue("p", Prefix);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<(string Status, long? RowsWritten, string? Error)> LastRunAsync(
        string stage, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT status, rows_written, error FROM run_log WHERE stage = @s " +
            "ORDER BY run_log_id DESC LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", stage);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        Assert.True(await r.ReadAsync(ct).ConfigureAwait(false), "No run_log row for " + stage + ".");

        return (
            r.GetString(0),
            await r.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : r.GetInt64(1),
            await r.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : r.GetString(2));
    }

    /// <summary>A date-partitioned stage: it resolves the calendar, so the guard binds it.</summary>
    private sealed class CalendarStage : IBackfillStage
    {
        public const string StageName = "SrlTestFrontierCalendarStage";

        public string Name => StageName;

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public IReadOnlyList<DateOnly>? Sessions { get; private set; }

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);

        public async Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
        {
            Sessions = await context.SessionsAsync(ct).ConfigureAwait(false);
            return BackfillResult.Completed(Sessions.Count, context.To);
        }
    }

    /// <summary>
    /// A ticker-partitioned sweep: it never resolves the calendar, because its dates
    /// come from the provider rather than from the store.
    /// </summary>
    private sealed class SweepStage : IBackfillStage
    {
        public const string StageName = "SrlTestFrontierSweepStage";

        public string Name => StageName;

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public bool Ran { get; private set; }

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);

        public Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
        {
            Ran = true;
            return Task.FromResult(BackfillResult.Completed(1, context.To));
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow, DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;

        public DateOnly Today => today;
    }

    /// <summary>Never consulted here; the stages above ask the gate for nothing.</summary>
    private sealed class StubAllowance(DateOnly stampedOn) : IUnitAllowance
    {
        public Task<AllowanceReading> ReadAsync(CancellationToken ct = default)
            => Task.FromResult(new AllowanceReading(0, 100_000, stampedOn));
    }
}
