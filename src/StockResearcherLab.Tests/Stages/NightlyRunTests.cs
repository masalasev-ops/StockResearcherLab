using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// Checkpoint 1.14. The sequence exists so that 1.3's definition of done can be
/// proved at all: "aborts the run and produces no orders" needs a run to abort.
///
/// Parallelism lives inside a stage and never across stages, because the guard and
/// the zero-row halt both depend on a stage being finished before the next reads
/// what it wrote [CLAUDE.md section 5].
/// </summary>
[Collection("database")]
public sealed class NightlyRunTests
{
    private const string Marker = "SRLNIGHT";

    /// <summary>
    /// Test doubles never answer to a catalogue component name.
    ///
    /// StageRunner writes a run_log row under whatever Name a stage gives, and
    /// run_log is the operational record that C28 and the run viewer read. A test
    /// row under a real component name makes "did the guard run tonight"
    /// unanswerable from the table, and it inflated the FreshnessGuard count from
    /// three real runs to nine. The RunLogRoundTrip tests already avoid this by
    /// suffixing a guid; this follows them.
    /// </summary>
    private static readonly string Suffix = Guid.NewGuid().ToString("N")[..8];

    private static readonly string GuardName = "TestGuard-" + Suffix;
    private static readonly string WriterName = "TestWriter-" + Suffix;
    private static readonly string SilentName = "TestSilentWriter-" + Suffix;
    private static readonly string AbsentName = "TestNeverRegistered-" + Suffix;

    private static readonly IClock Clock = new FixedClock(
        new DateTimeOffset(2026, 8, 7, 23, 0, 0, TimeSpan.Zero), new DateOnly(2026, 8, 7));

    private static NightlyRun Build(params IWriteOwner[] owners)
    {
        var cs = TestDatabase.ConnectionString;
        var all = owners.Append(new RunLog(cs)).ToList();
        var registry = new StageRegistry(all);

        return new NightlyRun(registry, new StageRunner(registry, new RunLog(cs), Clock, cs));
    }

    private static async Task<long> MarkerRowsAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM price_daily WHERE ticker >= @lo AND ticker < @hi;", conn);
        cmd.Parameters.AddWithValue("lo", Marker);
        cmd.Parameters.AddWithValue("hi", Marker + "~");
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM price_daily WHERE ticker >= @lo AND ticker < @hi;", conn);
        cmd.Parameters.AddWithValue("lo", Marker);
        cmd.Parameters.AddWithValue("hi", Marker + "~");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 1.3's definition of done. The guard finds nothing usable, and no table a
    /// later stage writes gains a row.
    /// </summary>
    [Fact]
    public async Task AGuardAbortLeavesNoRowsInAnyTableALaterStageWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        var writer = new MarkerWriter(WriterName);
        var night = Build(new AbortingGuard(), writer);

        var result = await night.ExecuteAsync(
            new DateOnly(2026, 8, 7), 1, [GuardName, WriterName], ct).ConfigureAwait(true);

        Assert.False(result.Completed);
        Assert.Null(result.TradingDate);

        // The later stage never ran, so nothing it writes exists.
        Assert.False(writer.Ran);
        Assert.Equal(0, await MarkerRowsAsync(ct).ConfigureAwait(true));

        Assert.Equal("failed", result.Steps[0].Outcome);
        Assert.DoesNotContain(result.Steps, s => string.Equals(s.Stage, WriterName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The guard's date, not the run date. Every stage after it works on the date it
    /// blessed, which is the whole reason the guard returns one [D-70].
    /// </summary>
    [Fact]
    public async Task TheDateTheGuardBlessedIsCarriedIntoEveryLaterStage()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        var blessed = new DateOnly(2026, 8, 5);
        var writer = new MarkerWriter(WriterName);
        var night = Build(new BlessingGuard(blessed), writer);

        var result = await night.ExecuteAsync(
            new DateOnly(2026, 8, 7), 1, [GuardName, WriterName], ct).ConfigureAwait(true);

        Assert.True(result.Completed);
        Assert.Equal(blessed, result.TradingDate);

        // The run was started for the 7th and the later stage saw the 5th.
        Assert.Equal(blessed, writer.SawDate);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The halt keys on a stage that writes having written nothing, not on the count
    /// alone. Zero rows is legitimate for a stage that declares no writes, which is
    /// exactly what the guard is.
    /// </summary>
    [Fact]
    public async Task AWritingStageThatProducesNothingHaltsEverythingAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;

        var second = new MarkerWriter(WriterName);
        var night = Build(
            new BlessingGuard(new DateOnly(2026, 8, 5)),
            new SilentWriter(SilentName),
            second);

        var result = await night.ExecuteAsync(
            new DateOnly(2026, 8, 7), 1,
            [GuardName, SilentName, WriterName], ct).ConfigureAwait(true);

        Assert.False(result.Completed);
        Assert.False(second.Ran);
        Assert.Contains(result.Steps, s => string.Equals(s.Outcome, "halted", StringComparison.Ordinal));
    }

    /// <summary>
    /// The guard writes nothing and that is not a halt. Without this distinction the
    /// sequence would stop at its own guard every night.
    /// </summary>
    [Fact]
    public async Task AStageThatDeclaresNoWritesProducingZeroRowsDoesNotHalt()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        var writer = new MarkerWriter(WriterName);
        var night = Build(new BlessingGuard(new DateOnly(2026, 8, 5)), writer);

        var result = await night.ExecuteAsync(
            new DateOnly(2026, 8, 7), 1, [GuardName, WriterName], ct).ConfigureAwait(true);

        Assert.True(result.Completed);
        Assert.True(writer.Ran);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// A stage later checkpoints build is reported rather than skipped in silence, so
    /// a night that ran short says so.
    /// </summary>
    [Fact]
    public async Task AnUnregisteredStageIsReportedRatherThanSkippedSilently()
    {
        var ct = TestContext.Current.CancellationToken;
        var night = Build(new BlessingGuard(new DateOnly(2026, 8, 5)));

        var result = await night.ExecuteAsync(
            new DateOnly(2026, 8, 7), 1, [GuardName, AbsentName], ct).ConfigureAwait(true);

        Assert.True(result.Completed);

        var missing = Assert.Single(result.Steps, s => string.Equals(s.Stage, AbsentName, StringComparison.Ordinal));
        Assert.Equal("not registered", missing.Outcome);
    }

    [Fact]
    public void TheEveningOrderMatchesTheArchitectureAndNamesOnlyCatalogueComponents()
    {
        var declared = ArchitectureDocument.ComponentNames();

        foreach (var name in NightlyRun.EveningOrder)
        {
            Assert.Contains(name, declared);
        }

        // The guard sits second, after the ingest that gives it something to judge.
        Assert.Equal("PriceIngestor", NightlyRun.EveningOrder[0]);
        Assert.Equal("FreshnessGuard", NightlyRun.EveningOrder[1]);

        // C01 runs weekly rather than nightly and is driven separately.
        Assert.DoesNotContain("UniverseBuilder", NightlyRun.EveningOrder);
    }

    /// <summary>
    /// Checkpoint 2.12. The compute layer's two ordering constraints, which are the
    /// only ones in the evening order that are about data rather than about the clock.
    ///
    /// **C10 reads `indicator_daily`, so it runs after C08** even though both sit in
    /// the 18:05 slot. Breadth is counted off `dist_200dma` rather than recomputed from
    /// prices, so there is one definition of "above its own 200-day average" and it
    /// belongs to the column that already carries it.
    ///
    /// **C11 is last** because it ranks what the four metric engines wrote. Run before
    /// any of them it would rank a table that is still yesterday's, and every
    /// percentile would be a real number computed over the wrong night.
    ///
    /// Stated as positions rather than as a comment, because a stage appended to the
    /// end of the array is the ordinary way a new one arrives and would put itself
    /// after C11 without anyone deciding that.
    /// </summary>
    [Fact]
    public void TheComputeStagesSitInDependencyOrder()
    {
        var order = NightlyRun.EveningOrder;

        Assert.Equal(16, order.Length);

        Assert.True(
            Array.IndexOf(order, "MarketContextEngine") > Array.IndexOf(order, "IndicatorEngine"),
            "C10 counts breadth off indicator_daily, so it cannot run before C08 wrote it.");

        // C11 was last until 4.12 put the selection layer behind it. It is still last of
        // the compute layer, which is what this assertion was always about.
        Assert.Equal("ConcentrationMonitor", order[^1]);

        foreach (var engine in new[]
                 {
                     "FlowEngine", "IndicatorEngine", "ValuationEngine", "SentimentEngine",
                 })
        {
            Assert.True(
                Array.IndexOf(order, engine) < Array.IndexOf(order, "PercentileEngine"),
                $"C11 ranks what {engine} writes, so it cannot run before it.");
        }

        foreach (var selection in new[]
                 {
                     "GateEngine", "ScreenEngine", "CandidateAllocator", "ConcentrationMonitor",
                 })
        {
            Assert.True(
                Array.IndexOf(order, selection) > Array.IndexOf(order, "PercentileEngine"),
                $"{selection} reads the percentile store, so it cannot run before C11 wrote it.");
        }

        // C13 scores on what C11 ranked and C14 allocates what C13 ranked. C12 is before
        // C13 because section 04 puts it there, and never after: C13 does not read
        // gate_result and must not [D-117].
        Assert.True(Array.IndexOf(order, "GateEngine") < Array.IndexOf(order, "ScreenEngine"));
        Assert.True(Array.IndexOf(order, "ScreenEngine") < Array.IndexOf(order, "CandidateAllocator"));
        Assert.True(
            Array.IndexOf(order, "CandidateAllocator") < Array.IndexOf(order, "ConcentrationMonitor"),
            "C28 measures the candidate set, so it cannot run before C14 wrote it.");
    }

    /// <summary>
    /// Checkpoint 4.12. The selection order is the tail of the evening order and not a
    /// second opinion about it.
    ///
    /// **A separate constant rather than a slice**, because a slice is an index into a
    /// list whose contents change. What must not drift is which stages a range run
    /// touches, so the two lists are held against each other here instead.
    /// </summary>
    [Fact]
    public void TheSelectionOrderIsTheTailOfTheEveningOrder()
    {
        Assert.Equal(
            NightlyRun.EveningOrder[^BackfillSequence.SelectionOrder.Length..],
            BackfillSequence.SelectionOrder);

        // And every name in it exists, which is 4.12's own done-when: the selection order
        // names only components that exist.
        var declared = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .Select(o => o.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in BackfillSequence.SelectionOrder)
        {
            Assert.Contains(name, declared);
        }
    }

    /// <summary>
    /// The convention this file follows, asserted rather than trusted to a comment.
    /// A double named for a real component writes a run_log row indistinguishable
    /// from the real one's, which is what made the FreshnessGuard count read nine
    /// when three real runs had happened.
    /// </summary>
    [Fact]
    public void NoTestDoubleAnswersToACatalogueComponentName()
    {
        var declared = ArchitectureDocument.ComponentNames();

        foreach (var name in new[] { GuardName, WriterName, SilentName, AbsentName })
        {
            Assert.DoesNotContain(name, declared);
        }
    }

    // --------------------------------------------------------------- doubles ---

    private sealed class AbortingGuard : IStage
    {
        public string Name { get; } = GuardName;

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => throw new FreshnessAbortException(
                new FreshnessVerdict(null, false, false,
                    [new FreshnessStep(context.Date, 0, FreshnessOutcome.NoData, "nothing held")]));
    }

    private sealed class BlessingGuard(DateOnly date) : IStage
    {
        public string Name { get; } = GuardName;

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None with { TradingDate = date });
    }

    /// <summary>Writes one row and records the date it was handed.</summary>
    private sealed class MarkerWriter(string name) : IStage
    {
        public string Name { get; } = name;

        public bool Ran { get; private set; }

        public DateOnly? SawDate { get; private set; }

        public IReadOnlyList<string> ReadSet { get; } = [];

        public IReadOnlyList<TableWrite> WriteSet { get; } =
            [new TableWrite("price_daily", WriteOperation.Insert, ["ticker", "date", "close"])];

        public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
        {
            Ran = true;
            SawDate = context.Date;

            var rows = await context.Data.BulkUpsertAsync(
                "price_daily", ["ticker", "date", "close"], ["ticker", "date"],
                async (w, c) =>
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(Marker + "1.US", c).ConfigureAwait(false);
                    await w.WriteAsync(context.Date, c).ConfigureAwait(false);
                    await w.WriteAsync(1.00m, c).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

            return new StageResult(rows);
        }
    }

    /// <summary>Declares a write and produces nothing, which is the halt condition.</summary>
    private sealed class SilentWriter(string name) : IStage
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> ReadSet { get; } = [];

        public IReadOnlyList<TableWrite> WriteSet { get; } =
            [new TableWrite("fundamental_snapshot", WriteOperation.Insert)];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);
    }
}
