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
/// The range contract and the allowance gate [3.4, D-93].
///
/// The stage below implements both entry points, which is what D-93 requires of every
/// component that gains a range mode: `ExecuteRangeAsync` beside `ExecuteAsync`, on
/// one class, with one write set and one read set. It exists here rather than being a
/// real stage because 3.4 builds the contract and 3.13 onward is what puts it on the
/// components.
/// </summary>
/// **In the `database` collection since phase 5's O.2, and the reason is a shared store
/// rather than this class's own subject.** It asserts a store-wide config version, which
/// is true only while no row anywhere carries a later version set at or before the date
/// it asks about. `AttributionWriteTests` and `CandidateAllocatorTests` each insert a
/// version 2 row at `set_at` 2020-06-01 and delete it again, and both are in that
/// collection; this class was not, so it ran in parallel with them and failed whenever
/// the window happened to overlap. Intermittent, and it surfaced in `ci.ps1` rather than
/// locally because `ci.ps1` drops both databases and the suite then runs against a store
/// with nothing else in it.
[Collection("database")]
public sealed class BackfillRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 2, 52, 0, TimeSpan.Zero);

    // Today is US Eastern and is deliberately a different date from the UTC one, which
    // is the case 3.1 measured: at 02:52 UTC on 2026-08-12 New York was still on
    // 2026-08-11 and the provider's counter had already moved.
    private static IClock Clock => new FixedClock(Now, new DateOnly(2026, 8, 11));

    // ------------------------------------------- config resolves per date ---

    /// <summary>
    /// D-93. A window key resolved at the range end would give a backfilled date a
    /// different answer from a nightly re-run of the same date, which is exactly the
    /// equality the phase's fourth done-when line asserts.
    ///
    /// The context makes this structural rather than a convention: it carries no date
    /// and no version at all, so the only route to a `StageContext` inside a range is
    /// `ForDateAsync`, which resolves the date it is given.
    /// </summary>
    [Fact]
    public async Task ARangeExecutionResolvesConfigOncePerDateRatherThanOnceForTheRange()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var stage = new WalkingStage();
        var result = await RunAsync(stage, new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 5), ct)
            .ConfigureAwait(true);

        Assert.Equal(
            new[]
            {
                new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 2), new DateOnly(2022, 6, 3),
                new DateOnly(2022, 6, 4), new DateOnly(2022, 6, 5),
            },
            stage.Resolved.Select(r => r.Date).ToArray());

        // Every date resolved a version of its own, and the version is the one in
        // force on that date rather than the one in force at the range end.
        Assert.All(stage.Resolved, r => Assert.Equal(1, r.Version));
        Assert.Equal(new DateOnly(2022, 6, 5), result.LastDateCovered);
        Assert.False(result.WasHalted);
    }

    /// <summary>
    /// The range bound is enforced rather than documented. A stage computing a date it
    /// was not given is the failure the bound exists to prevent, and it would be
    /// invisible in the output.
    /// </summary>
    [Fact]
    public async Task ADateOutsideTheRangeCannotBeResolvedAtAll()
    {
        var ct = TestContext.Current.CancellationToken;

        var context = new BackfillContext(
            new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 5),
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(new WalkingStage())),
            Clock, new ConfigStore(TestDatabase.ConnectionString), new StubAllowance(0, 100_000, ProviderDate));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => context.ForDateAsync(new DateOnly(2022, 5, 31), ct)).ConfigureAwait(true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => context.ForDateAsync(new DateOnly(2022, 6, 6), ct)).ConfigureAwait(true);
    }

    /// <summary>
    /// D-94's floor. `ConfigSeeder.SeedInstant` is 2020-01-01, so a window opening
    /// before it resolves to nothing for every date in the range.
    ///
    /// **It fails the run rather than computing against a configuration that resolved
    /// to nothing**, which is the failure that would otherwise be silent: today's
    /// resolution still works, so nothing about the process looks wrong.
    /// </summary>
    [Fact]
    public async Task AWindowStartBeforeTheSeedInstantFailsWithTheConfigVersionError()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var stage = new WalkingStage();

        await Assert.ThrowsAsync<ConfigVersionNotInForceException>(
            () => RunAsync(stage, new DateOnly(2019, 12, 30), new DateOnly(2019, 12, 31), ct))
            .ConfigureAwait(true);

        // Recorded as a failed run rather than swallowed, with rows_written null
        // because a stage that threw wrote an unknown number of rows.
        var row = await LastRunLogAsync(stage.Name, ct).ConfigureAwait(true);
        Assert.Equal("failed", row.Status);
        Assert.Null(row.RowsWritten);
        Assert.Contains("range 2019-12-30..2019-12-31", row.Error ?? "", StringComparison.Ordinal);
    }

    // ------------------------------------------------- the declared sets ---

    /// <summary>
    /// The range path is handed the same `DeclaredAccess` the nightly path is, so an
    /// undeclared table throws before a connection opens.
    ///
    /// **Asserted against an unreachable connection string**, which is what makes
    /// "before a connection opens" an assertion rather than a claim: if the guard ran
    /// second, this would fail with a socket error instead.
    /// </summary>
    [Fact]
    public async Task AnUndeclaredTableThrowsBeforeAConnectionOpens()
    {
        var ct = TestContext.Current.CancellationToken;
        const string unreachable = "Host=127.0.0.1;Port=1;Database=srl_no_such_db;Username=none;Password=none;Timeout=1";

        var stage = new WalkingStage();
        var context = new BackfillContext(
            new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 5),
            new StageData(unreachable, new DeclaredAccess(stage)),
            Clock, new ConfigStore(unreachable), new StubAllowance(0, 100_000, ProviderDate));

        // price_daily is declared; attribution is not.
        var ex = await Assert.ThrowsAsync<UndeclaredTableAccessException>(
            () => context.Data.ReadAsync("attribution", "SELECT 1;", ct)).ConfigureAwait(true);

        Assert.Equal("attribution", ex.Table);
        Assert.Equal(stage.Name, ex.Stage);
    }

    // ----------------------------------------------- the allowance gate ---

    /// <summary>
    /// A seeded allowance below the next unit's weight halts the sweep with no rows
    /// lost [3.4].
    ///
    /// The stub spends as the stage works, which is what makes this the loop rather
    /// than a single comparison: ten units fit, the eleventh does not, and the ten are
    /// still written.
    /// </summary>
    [Fact]
    public async Task TheGateHaltsTheSweepAndKeepsTheRowsAlreadyWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        // 100,000 limit, 50,000 reserve, 49,900 already spent. 100 units are left
        // above the reserve and each ticker costs 10, so ten fit and the eleventh
        // does not.
        var allowance = new StubAllowance(used: 49_900, limit: 100_000, stampedOn: ProviderDate);
        var stage = new SweepingStage(tickers: 15, weightPerTicker: 10, reserve: 50_000);

        var result = await RunAsync(stage, new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 1), ct, allowance)
            .ConfigureAwait(true);

        Assert.True(result.WasHalted);
        Assert.Equal(10, result.RowsWritten);

        Assert.Equal(new DateOnly(2022, 6, 1), result.LastDateCovered);

        // Ten worked and the eleventh was refused, so the gate was asked eleven times.
        Assert.Equal(11, stage.GateReads);
        Assert.Equal(50_000, allowance.Used);

        var row = await LastRunLogAsync(stage.Name, ct).ConfigureAwait(true);
        Assert.Equal("halted", row.Status);
        Assert.Equal(10, row.RowsWritten);
        Assert.Contains("range 2022-06-01..2022-06-01", row.Error ?? "", StringComparison.Ordinal);
        Assert.Contains("T0011", row.Error ?? "", StringComparison.Ordinal);
        Assert.Contains("HALTED", row.Error ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The same sweep with room to run completes, so the test above is measuring the
    /// gate rather than a sweep that could never finish.
    /// </summary>
    [Fact]
    public async Task TheSameSweepCompletesWhenTheAllowanceHasRoom()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var allowance = new StubAllowance(used: 0, limit: 100_000, stampedOn: ProviderDate);
        var stage = new SweepingStage(tickers: 15, weightPerTicker: 10, reserve: 50_000);

        var result = await RunAsync(stage, new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 1), ct, allowance)
            .ConfigureAwait(true);

        Assert.False(result.WasHalted);
        Assert.Equal(15, result.RowsWritten);

        var row = await LastRunLogAsync(stage.Name, ct).ConfigureAwait(true);
        Assert.Equal("ok", row.Status);
        Assert.DoesNotContain("HALTED", row.Error ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A stale reading halts the sweep before its first unit, and says something
    /// different from an exhausted one. The two are one absorbed observation apart.
    /// </summary>
    [Fact]
    public async Task AStaleAllowanceReadingHaltsBeforeAnythingIsSpent()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        // Stamped the day before the provider date, which is what 3.1 read at 02:52
        // UTC while the counter still carried the previous day's 90,518.
        var allowance = new StubAllowance(used: 0, limit: 100_000, stampedOn: ProviderDate.AddDays(-1));
        var stage = new SweepingStage(tickers: 15, weightPerTicker: 10, reserve: 50_000);

        var result = await RunAsync(stage, new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 1), ct, allowance)
            .ConfigureAwait(true);

        Assert.True(result.WasHalted);
        Assert.Equal(0, result.RowsWritten);
        Assert.Equal(0, allowance.Used);
        Assert.Contains("stamped", result.Detail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A stage registered without a range mode has none, rather than acquiring one by
    /// being run through this. The registry is the single declaration of who touches
    /// what, and the message says why a second class is not the answer.
    /// </summary>
    [Fact]
    public async Task AStageWithNoRangeModeIsRefusedRatherThanImprovised()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = new BackfillRun(
            new StageRegistry([new NightlyOnlyStage()]),
            new RunLog(TestDatabase.ConnectionString),
            Clock,
            TestDatabase.ConnectionString,
            new StubAllowance(0, 100_000, ProviderDate));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.RunAsync("SrlTestNightlyOnly", new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 2), ct))
            .ConfigureAwait(true);

        Assert.Contains("IBackfillStage", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------- the failure path [3.6] ---

    /// <summary>
    /// **A failed range execution records its range start, and this is the test that
    /// decision was taken for** [3.6].
    ///
    /// Resumption takes the highest `run_date` among a stage's range rows and does not
    /// filter on status. A failed run stamped with its range END would be the highest
    /// row, so the next run would resume from after everything the failure skipped and
    /// the gap would never be revisited. Stamped with its start, the maximum falls back
    /// to whatever an earlier run can prove it reached.
    ///
    /// The two errors are not symmetric, which is why the conservative stamp wins:
    /// resuming too early re-does work that is idempotent per grain, and resuming too
    /// late leaves a hole no later stage can see [D-68].
    /// </summary>
    [Fact]
    public async Task AFailedRunAboveAHaltedOneDoesNotCarryResumptionPastTheHalt()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await ClearRunLogAsync(HaltThenFailStage.StageName, ct).ConfigureAwait(true);

        var run = new BackfillRun(
            new StageRegistry([new HaltThenFailStage()]),
            new RunLog(TestDatabase.ConnectionString),
            Clock,
            TestDatabase.ConnectionString,
            new StubAllowance(0, 100_000, ProviderDate));

        // First, a halt that reached 2022-06-03 inside a range ending 2022-06-30.
        var halted = await run.RunAsync(
            HaltThenFailStage.StageName, new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 30), ct)
            .ConfigureAwait(true);

        Assert.True(halted.WasHalted);

        // Then a run over the same range that throws. It is the newer row.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.RunAsync(
                HaltThenFailStage.StageName, new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 30), ct))
            .ConfigureAwait(true);

        var failedRow = await LastRunLogAsync(HaltThenFailStage.StageName, ct).ConfigureAwait(true);
        Assert.Equal("failed", failedRow.Status);

        // The range start, not the range end. 2022-06-30 here is the defect.
        Assert.Equal(new DateOnly(2022, 6, 1), failedRow.RunDate);

        // And the report is the halted run's reached date, because 06-03 is the highest
        // run_date of the two rows. Nothing resumes from it; it is what an operator
        // reads before spending another day [0010].
        var report = await run.LastRangeRunAsync(HaltThenFailStage.StageName, ct).ConfigureAwait(true);

        Assert.NotNull(report);
        Assert.Equal(new DateOnly(2022, 6, 3), report!.LastDateCovered);
        Assert.Equal("halted", report.Status);
    }

    /// <summary>
    /// A failed run reports its range start, which is the only date it can prove, and
    /// the line says it failed rather than describing a position it does not have.
    /// </summary>
    [Fact]
    public async Task AFailedRunReportsItsRangeStartAndNoPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await ClearRunLogAsync(FailingStage.StageName, ct).ConfigureAwait(true);

        var run = new BackfillRun(
            new StageRegistry([new FailingStage()]),
            new RunLog(TestDatabase.ConnectionString),
            Clock,
            TestDatabase.ConnectionString,
            new StubAllowance(0, 100_000, ProviderDate));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.RunAsync(FailingStage.StageName, new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 30), ct))
            .ConfigureAwait(true);

        var report = await run.LastRangeRunAsync(FailingStage.StageName, ct).ConfigureAwait(true);

        Assert.NotNull(report);
        Assert.Equal("failed", report!.Status);
        Assert.Equal(new DateOnly(2022, 6, 1), report.LastDateCovered);
        Assert.Contains("range 2022-06-01..2022-06-30 FAILED", report.Detail ?? "", StringComparison.Ordinal);
    }

    // ------------------------- a previous row does not stop a run [0010] ---
    //
    // The range-matching refusal lived here. It existed because a halted row carried a
    // ticker position and a position taken from a narrower range resumes into a wider
    // pool, skipping every name the narrow one did not contain. There is no position
    // now: resumption is the stage's own attempt record, so a row over any range is a
    // row about the past and stops nothing.

    /// <summary>
    /// **A halted row over a different range no longer refuses.** That refusal was the
    /// right answer to a position belonging to a range, and it is the wrong answer to a
    /// record that keys on the range start itself: a sweep re-invoked the next morning
    /// picks up its own attempt rows whatever `to` defaults to.
    /// </summary>
    [Fact]
    public async Task AHaltedRowOverADifferentRangeDoesNotStopTheNextRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await ClearRunLogAsync(ResumeProbeStage.StageName, ct).ConfigureAwait(true);

        var halting = new ResumeProbeStage(halts: true);
        await RunFor(halting).RunAsync(
            ResumeProbeStage.StageName, new DateOnly(2021, 1, 4), new DateOnly(2021, 1, 8), ct)
            .ConfigureAwait(true);

        var wide = new ResumeProbeStage(halts: false);
        var result = await RunFor(wide).RunAsync(
            ResumeProbeStage.StageName, new DateOnly(2021, 1, 4), new DateOnly(2026, 8, 12), ct)
            .ConfigureAwait(true);

        Assert.True(wide.Ran);
        Assert.False(result.WasHalted);
    }

    /// <summary>
    /// **A row whose line `BackfillRun.Describe` could not have written stops nothing
    /// either**, which is the case the old rule failed towards a refusal on. Nothing
    /// parses the line for a range any more, so a row it cannot read is a row it does
    /// not read.
    /// </summary>
    [Fact]
    public async Task ARangeRowNothingCouldHaveWrittenStopsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await ClearRunLogAsync(ResumeProbeStage.StageName, ct).ConfigureAwait(true);

        await InsertUnparseableRangeRowAsync(ct).ConfigureAwait(true);

        var stage = new ResumeProbeStage(halts: false);
        var result = await RunFor(stage).RunAsync(
            ResumeProbeStage.StageName, new DateOnly(2021, 1, 4), new DateOnly(2026, 8, 12), ct)
            .ConfigureAwait(true);

        Assert.True(stage.Ran);
        Assert.False(result.WasHalted);
    }

    [Fact]
    public void ARangeThatEndsBeforeItStartsIsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new BackfillContext(
            new DateOnly(2022, 6, 5), new DateOnly(2022, 6, 1),
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(new WalkingStage())),
            Clock, new ConfigStore(TestDatabase.ConnectionString), new StubAllowance(0, 100_000, ProviderDate)));
    }

    // ------------------------------------------------------------ harness ---

    private static DateOnly ProviderDate => DateOnly.FromDateTime(Now.UtcDateTime);

    private static async Task SeedAsync(CancellationToken ct)
        => await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(false);

    private static async Task<BackfillResult> RunAsync(
        IBackfillStage stage, DateOnly from, DateOnly to, CancellationToken ct, IUnitAllowance? allowance = null)
    {
        var run = new BackfillRun(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            Clock,
            TestDatabase.ConnectionString,
            allowance ?? new StubAllowance(0, 100_000, ProviderDate));

        return await run.RunAsync(stage.Name, from, to, ct).ConfigureAwait(false);
    }

    private static async Task<RunLogEntry> LastRunLogAsync(string stage, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT run_log_id, run_date, stage, status, started_at, duration_ms, rows_written, error
            FROM run_log WHERE stage = @stage
            ORDER BY run_log_id DESC LIMIT 1;
            """, conn);
        cmd.Parameters.AddWithValue("stage", stage);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        Assert.True(await r.ReadAsync(ct).ConfigureAwait(false), "No run_log row for " + stage + ".");

        return new RunLogEntry(
            r.GetInt64(0),
            DateOnly.FromDateTime(r.GetDateTime(1)),
            r.GetString(2),
            r.GetString(3),
            r.GetFieldValue<DateTimeOffset>(4),
            await r.IsDBNullAsync(5, ct).ConfigureAwait(false) ? null : r.GetInt64(5),
            await r.IsDBNullAsync(6, ct).ConfigureAwait(false) ? null : r.GetInt64(6),
            await r.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : r.GetString(7));
    }

    /// <summary>Reads a fixed allowance and spends it as the caller works, which is the loop the gate sits in.</summary>
    private sealed class StubAllowance : IUnitAllowance
    {
        private readonly int _limit;
        private readonly DateOnly _stampedOn;

        public StubAllowance(int used, int limit, DateOnly stampedOn)
        {
            Used = used;
            _limit = limit;
            _stampedOn = stampedOn;
        }

        public int Used { get; private set; }

        public int Reads { get; private set; }

        public void Spend(int units) => Used += units;

        public Task<AllowanceReading> ReadAsync(CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(new AllowanceReading(Used, _limit, _stampedOn));
        }
    }

    /// <summary>
    /// Both entry points on one class, which is the shape D-93 requires. Walks the
    /// range one date at a time and records the version each date resolved.
    /// </summary>
    private sealed class WalkingStage : IBackfillStage
    {
        public string Name => "SrlTestWalkingStage";

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public List<(DateOnly Date, int Version)> Resolved { get; } = [];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
        {
            Resolved.Add((context.Date, context.ConfigVersion));
            return Task.FromResult(StageResult.None);
        }

        public async Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
        {
            var last = context.From;

            foreach (var date in context.Dates())
            {
                // The whole point: one resolution per date, through the only route
                // there is to one.
                var forDate = await context.ForDateAsync(date, ct).ConfigureAwait(false);
                await ExecuteAsync(forDate, ct).ConfigureAwait(false);
                last = date;
            }

            return BackfillResult.Completed(Resolved.Count, last);
        }
    }

    /// <summary>
    /// A ticker-partitioned sweep. Asks the gate before each unit of work and halts
    /// cleanly where it refuses, keeping everything written so far.
    /// </summary>
    private sealed class SweepingStage : IBackfillStage
    {
        private readonly int _tickers;
        private readonly int _weight;
        private readonly long _reserve;

        public SweepingStage(int tickers, int weightPerTicker, long reserve)
        {
            _tickers = tickers;
            _weight = weightPerTicker;
            _reserve = reserve;
        }

        public string Name => "SrlTestSweepingStage";

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public int GateReads { get; private set; }

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);

        public async Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
        {
            var written = 0L;

            for (var i = 1; i <= _tickers; i++)
            {
                var ticker = "T" + i.ToString("D4", CultureInfo.InvariantCulture);

                GateReads++;

                // The three figures a real sweep resolves from config for the date it
                // is working on: its own endpoint weight, the reserve, and the
                // configured allowance the provider reading is compared against.
                var decision = await context
                    .NextUnitAsync(_weight, _reserve, configuredLimit: 100_000, ct)
                    .ConfigureAwait(false);

                if (!decision.Fits)
                {
                    return BackfillResult.Halted(
                        written, context.To, "refused at " + ticker + ". " + decision.Detail);
                }

                // The work. Spending is the stub's, standing in for the provider
                // call the real sweep would make here.
                if (context.Allowance is StubAllowance stub)
                {
                    stub.Spend(_weight);
                }

                written++;
            }

            return BackfillResult.Completed(written, context.To);
        }
    }

    /// <summary>
    /// A range row `BackfillRun.Describe` could not have written: it opens with the
    /// prefix the reader keys on and carries no dates the parse can take.
    /// </summary>
    private static async Task InsertUnparseableRangeRowAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO run_log (run_date, stage, status, started_at, duration_ms, rows_written, error)
            VALUES (DATE '2021-01-08', @s, 'halted', now(), 1, 1,
                    'range whenever..whenever, reached 2021-01-08 at L07.US. HALTED.');
            """, conn);
        cmd.Parameters.AddWithValue("s", ResumeProbeStage.StageName);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static BackfillRun RunFor(IBackfillStage stage)
        => new(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            Clock,
            TestDatabase.ConnectionString,
            new StubAllowance(0, 100_000, ProviderDate));

    /// <summary>
    /// Records whether it ran at all, and halts or completes at the caller's choosing.
    ///
    /// It carried the resume position it was handed until 0010, which is how the
    /// range-matching rule was asserted. There is no position now and no rule to assert:
    /// what survives is `Ran`, which is what says a previous row did not stop this run.
    /// </summary>
    private sealed class ResumeProbeStage(bool halts) : IBackfillStage
    {
        public const string StageName = "SrlTestResumeProbe";

        public string Name => StageName;

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public bool Ran { get; private set; }

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);

        public Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
        {
            Ran = true;

            return Task.FromResult(halts
                ? BackfillResult.Halted(1, context.To, "probe halted.")
                : BackfillResult.Completed(1, context.To, "probe completed."));
        }
    }

    private static async Task ClearRunLogAsync(string stage, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM run_log WHERE stage = @s;", conn);
        cmd.Parameters.AddWithValue("s", stage);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Halts at a known position on its first run, then throws on every one after.</summary>
    private sealed class HaltThenFailStage : IBackfillStage
    {
        public const string StageName = "SrlTestHaltThenFail";

        private int _runs;

        public string Name => StageName;

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);

        public Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
            => ++_runs == 1
                ? Task.FromResult(BackfillResult.Halted(4, new DateOnly(2022, 6, 3), "out of allowance"))
                : throw new InvalidOperationException("SrlTest: the range execution failed.");
    }

    /// <summary>Throws on every run, so the failed row is the only one there is.</summary>
    private sealed class FailingStage : IBackfillStage
    {
        public const string StageName = "SrlTestFailing";

        public string Name => StageName;

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);

        public Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("SrlTest: the range execution failed.");
    }

    /// <summary>A stage with a nightly mode and no range mode, which is most of them.</summary>
    private sealed class NightlyOnlyStage : IStage
    {
        public string Name => "SrlTestNightlyOnly";

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);
    }
}
