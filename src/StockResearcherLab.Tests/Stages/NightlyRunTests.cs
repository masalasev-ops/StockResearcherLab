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
            "SELECT count(*) FROM price_daily WHERE ticker LIKE @p;", conn);
        cmd.Parameters.AddWithValue("p", Marker + "%");
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM price_daily WHERE ticker LIKE @p;", conn);
        cmd.Parameters.AddWithValue("p", Marker + "%");
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

        var writer = new MarkerWriter("SentimentIngestor");
        var night = Build(new AbortingGuard(), writer);

        var result = await night.ExecuteAsync(
            new DateOnly(2026, 8, 7), 1, ["FreshnessGuard", "SentimentIngestor"], ct).ConfigureAwait(true);

        Assert.False(result.Completed);
        Assert.Null(result.TradingDate);

        // The later stage never ran, so nothing it writes exists.
        Assert.False(writer.Ran);
        Assert.Equal(0, await MarkerRowsAsync(ct).ConfigureAwait(true));

        Assert.Equal("failed", result.Steps[0].Outcome);
        Assert.DoesNotContain(result.Steps, s => string.Equals(s.Stage, "SentimentIngestor", StringComparison.Ordinal));
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
        var writer = new MarkerWriter("SentimentIngestor");
        var night = Build(new BlessingGuard(blessed), writer);

        var result = await night.ExecuteAsync(
            new DateOnly(2026, 8, 7), 1, ["FreshnessGuard", "SentimentIngestor"], ct).ConfigureAwait(true);

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

        var second = new MarkerWriter("SentimentIngestor");
        var night = Build(
            new BlessingGuard(new DateOnly(2026, 8, 5)),
            new SilentWriter("FundamentalsIngestor"),
            second);

        var result = await night.ExecuteAsync(
            new DateOnly(2026, 8, 7), 1,
            ["FreshnessGuard", "FundamentalsIngestor", "SentimentIngestor"], ct).ConfigureAwait(true);

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

        var writer = new MarkerWriter("SentimentIngestor");
        var night = Build(new BlessingGuard(new DateOnly(2026, 8, 5)), writer);

        var result = await night.ExecuteAsync(
            new DateOnly(2026, 8, 7), 1, ["FreshnessGuard", "SentimentIngestor"], ct).ConfigureAwait(true);

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
            new DateOnly(2026, 8, 7), 1, ["FreshnessGuard", "FlowEngine"], ct).ConfigureAwait(true);

        Assert.True(result.Completed);

        var missing = Assert.Single(result.Steps, s => string.Equals(s.Stage, "FlowEngine", StringComparison.Ordinal));
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

    // --------------------------------------------------------------- doubles ---

    private sealed class AbortingGuard : IStage
    {
        public string Name => "FreshnessGuard";

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => throw new FreshnessAbortException(
                new FreshnessVerdict(null, false, false,
                    [new FreshnessStep(context.Date, 0, FreshnessOutcome.NoData, "nothing held")]));
    }

    private sealed class BlessingGuard(DateOnly date) : IStage
    {
        public string Name => "FreshnessGuard";

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
