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
    /// A seeded allowance below the next unit's weight halts the sweep with its
    /// position recorded and no rows lost [3.4].
    ///
    /// The stub spends as the stage works, which is what makes this the loop rather
    /// than a single comparison: ten units fit, the eleventh does not, and the ten are
    /// still written.
    /// </summary>
    [Fact]
    public async Task TheGateHaltsTheSweepWithItsPositionRecordedAndTheRowsAlreadyWrittenKept()
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

        // The position is where the next run resumes, so it is the ticker that was
        // refused rather than the last one that worked. T0010 would make resumption
        // read the pool's ordering to work out what comes next.
        Assert.Equal("T0011", result.Position);
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
        Assert.Null(result.Position);

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
        Assert.Equal("T0001", result.Position);
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

        // And the unfiltered maximum is the halted run's reached date, so resumption
        // goes back to 06-03 rather than forward to 06-30.
        var resume = await run.ResumeFromAsync(HaltThenFailStage.StageName, ct).ConfigureAwait(true);

        Assert.NotNull(resume);
        Assert.Equal(new DateOnly(2022, 6, 3), resume!.LastDateCovered);
        Assert.Equal("halted", resume.Status);
        Assert.Equal("T0007", resume.Position);
    }

    /// <summary>
    /// A sweep resumes only from a halt. A failed run records no position it can prove,
    /// so the next one starts over, which is safe because every write is idempotent on
    /// its own grain [D-68].
    /// </summary>
    [Fact]
    public async Task AFailedRunLeavesNoPositionToResumeFrom()
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

        var resume = await run.ResumeFromAsync(FailingStage.StageName, ct).ConfigureAwait(true);

        Assert.NotNull(resume);
        Assert.Equal("failed", resume!.Status);
        Assert.Null(resume.Position);
        Assert.Equal(new DateOnly(2022, 6, 1), resume.LastDateCovered);
    }

    // ------------------------------- a resume point belongs to its range [22] ---
    //
    // The position is a ticker and which pool it indexes into is decided by the range,
    // so a position taken from a narrower range resumes into a wider pool and skips
    // every name the narrow one did not contain. Found as a test fixture's halt
    // standing in front of a real sweep, which would have completed and reported a
    // plausible count over a partial load.

    /// <summary>
    /// The half that has to keep working. Same stage, same range, halted: the position
    /// is taken and the sweep picks up where it stopped.
    /// </summary>
    [Fact]
    public async Task AHaltedRunResumesFromItsPositionWhenTheRangeMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await ClearRunLogAsync(ResumeProbeStage.StageName, ct).ConfigureAwait(true);

        var from = new DateOnly(2022, 6, 1);
        var to = new DateOnly(2022, 6, 30);

        // A halt at T0007 over 2022-06-01..2022-06-30.
        var halting = new ResumeProbeStage(haltAt: "T0007");
        await RunFor(halting).RunAsync(ResumeProbeStage.StageName, from, to, ct).ConfigureAwait(true);

        // The same range again. The stage records what it was handed.
        var resuming = new ResumeProbeStage(haltAt: null);
        var result = await RunFor(resuming).RunAsync(ResumeProbeStage.StageName, from, to, ct)
            .ConfigureAwait(true);

        Assert.Equal("T0007", resuming.ResumedFrom);
        Assert.False(result.WasHalted);
    }

    /// <summary>
    /// **A halted row over a different range refuses, and refusing is the point.**
    ///
    /// Falling through to a fresh start is the wrong half of the fix, because `to`
    /// defaults to today: a sweep halted on day one and re-invoked on day two carries a
    /// different range, would match nothing, and would start again from the beginning.
    /// That is idempotent and therefore not corrupt, and it burns a day of allowance
    /// re-doing finished work and never reaches the end.
    ///
    /// The message has to name both ranges and the row, because the operator's next act
    /// is either to pass the recorded range or to delete that row, and neither is
    /// possible without knowing which row it is.
    /// </summary>
    [Fact]
    public async Task AHaltedRunOverADifferentRangeRefusesAndNamesBothRangesAndTheRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await ClearRunLogAsync(ResumeProbeStage.StageName, ct).ConfigureAwait(true);

        // Halted over the narrow range, which is the fixture row's shape.
        var halting = new ResumeProbeStage(haltAt: "L07.US");
        await RunFor(halting).RunAsync(
            ResumeProbeStage.StageName, new DateOnly(2021, 1, 4), new DateOnly(2021, 1, 8), ct)
            .ConfigureAwait(true);

        // The real sweep asks for the wide one.
        var wide = new ResumeProbeStage(haltAt: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunFor(wide).RunAsync(
                ResumeProbeStage.StageName, new DateOnly(2021, 1, 4), new DateOnly(2026, 8, 12), ct))
            .ConfigureAwait(true);

        // Both ranges, so the operator can see which is which.
        Assert.Contains("2021-01-04..2021-01-08", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2021-01-04..2026-08-12", ex.Message, StringComparison.Ordinal);

        // The row, by id, because "delete the row" is not actionable without one.
        var row = await LastRunLogAsync(ResumeProbeStage.StageName, ct).ConfigureAwait(true);
        Assert.Contains(
            row.RunLogId.ToString(CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        Assert.Contains("L07.US", ex.Message, StringComparison.Ordinal);

        // **It refused rather than starting over**, which is the assertion that
        // separates this fix from the wrong half of it. The stage never ran.
        Assert.False(wide.Ran);

        // And it did not record a run of its own: the refusal is before the try block
        // that writes, so the halted row is still the newest and still says what it
        // said. A refusal that logged would move the row it is complaining about.
        Assert.Equal("halted", row.Status);
    }

    /// <summary>
    /// A completed run over a different range is not a refusal. Only a halt carries a
    /// position, so only a halt can carry the wrong one.
    /// </summary>
    [Fact]
    public async Task ACompletedRunOverADifferentRangeDoesNotRefuse()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await ClearRunLogAsync(ResumeProbeStage.StageName, ct).ConfigureAwait(true);

        var first = new ResumeProbeStage(haltAt: null);
        await RunFor(first).RunAsync(
            ResumeProbeStage.StageName, new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 5), ct)
            .ConfigureAwait(true);

        var second = new ResumeProbeStage(haltAt: null);
        var result = await RunFor(second).RunAsync(
            ResumeProbeStage.StageName, new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 30), ct)
            .ConfigureAwait(true);

        Assert.True(second.Ran);
        Assert.Null(second.ResumedFrom);
        Assert.False(result.WasHalted);
    }

    /// <summary>
    /// The range is read back off the line `BackfillRun.Describe` composes, which is
    /// what the comparison rests on. Asserted through `ResumePoint` rather than against
    /// the parser, because the parser is private and the public surface is the answer.
    /// </summary>
    [Fact]
    public async Task AResumePointCarriesTheRangeItsRowRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await ClearRunLogAsync(ResumeProbeStage.StageName, ct).ConfigureAwait(true);

        var run = RunFor(new ResumeProbeStage(haltAt: "T0007"));
        await run.RunAsync(
            ResumeProbeStage.StageName, new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 30), ct)
            .ConfigureAwait(true);

        var resume = await run.ResumeFromAsync(ResumeProbeStage.StageName, ct).ConfigureAwait(true);

        Assert.NotNull(resume);
        Assert.Equal(new DateOnly(2022, 6, 1), resume!.From);
        Assert.Equal(new DateOnly(2022, 6, 30), resume.To);
        Assert.Equal("2022-06-01..2022-06-30", resume.RecordedRange);
        Assert.True(resume.CoversRange(new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 30)));
        Assert.False(resume.CoversRange(new DateOnly(2022, 6, 1), new DateOnly(2022, 6, 29)));
    }

    /// <summary>
    /// **A line whose range cannot be read fails towards the refusal.** The row is a
    /// range row by its prefix, so it is found, and it carries no range this can
    /// compare, so `CoversRange` is false and the run refuses. The alternative default
    /// is a resume against an unknown range, which is the thing the whole rule exists
    /// to stop.
    ///
    /// Written straight into `run_log`, because `BackfillRun.Describe` cannot produce
    /// this shape and the point is what happens when something else does.
    /// </summary>
    [Fact]
    public async Task ARangeRowWhoseRangeCannotBeReadRefusesRatherThanResuming()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await ClearRunLogAsync(ResumeProbeStage.StageName, ct).ConfigureAwait(true);

        await InsertUnparseableRangeRowAsync(ct).ConfigureAwait(true);

        var resume = await RunFor(new ResumeProbeStage(haltAt: null))
            .ResumeFromAsync(ResumeProbeStage.StageName, ct).ConfigureAwait(true);

        Assert.NotNull(resume);
        Assert.Null(resume!.From);
        Assert.Equal("(unparsed)", resume.RecordedRange);

        var stage = new ResumeProbeStage(haltAt: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunFor(stage).RunAsync(
                ResumeProbeStage.StageName, new DateOnly(2021, 1, 4), new DateOnly(2026, 8, 12), ct))
            .ConfigureAwait(true);

        Assert.False(stage.Ran);
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
                    return BackfillResult.Halted(written, context.To, ticker, decision.Detail);
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
    /// Records the resume position it was handed and whether it ran at all, and halts
    /// at a position of the caller's choosing. Both are what the range-matching rule is
    /// asserted through: a refusal has to be visible as the stage not running, not only
    /// as an exception type.
    /// </summary>
    private sealed class ResumeProbeStage(string? haltAt) : IBackfillStage
    {
        public const string StageName = "SrlTestResumeProbe";

        public string Name => StageName;

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public bool Ran { get; private set; }

        public string? ResumedFrom { get; private set; }

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);

        public Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
        {
            Ran = true;
            ResumedFrom = context.ResumeFrom;

            return Task.FromResult(haltAt is null
                ? BackfillResult.Completed(1, context.To, "probe completed.")
                : BackfillResult.Halted(1, context.To, haltAt, "probe halted."));
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
                ? Task.FromResult(BackfillResult.Halted(4, new DateOnly(2022, 6, 3), "T0007", "out of allowance"))
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
