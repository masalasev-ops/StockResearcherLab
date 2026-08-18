using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// The sources-in-order driver [3.16].
///
/// **Two things are asserted and they fail for different reasons.** The first is that
/// the declared order and the registry agree in both directions, which is what catches a
/// component gaining a range mode and nobody adding it to the sequence: that failure
/// writes a store missing one table and nothing errors, which is `CLAUDE.md` §1's shape
/// exactly. The second is the sequencing itself, over doubles, because the property
/// worth the most is what the driver does when a source stops it and no real stage can
/// be made to stop on demand without spending a day of allowance.
///
/// **The doubles carry the `SrlTest` prefix and their own run log rows.** Nothing here
/// asserts over a real component's name, so a fixture cannot be read as a real sweep
/// [item 22].
/// </summary>
public sealed class BackfillSequenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 2, 52, 0, TimeSpan.Zero);

    private static readonly DateOnly From = new(2021, 1, 4);

    private static readonly DateOnly To = new(2021, 1, 8);

    private static IClock Clock => new FixedClock(Now, new DateOnly(2026, 8, 11));

    private static DateOnly ProviderDate => DateOnly.FromDateTime(Now.UtcDateTime);

    // ------------------------------------------- the order and the registry ---

    /// <summary>
    /// Every name in the order is a registered component with a range mode.
    ///
    /// A typo in the order is otherwise found by running it, which for the ingest half
    /// means finding it after the sources before it have spent a day.
    /// </summary>
    [Fact]
    public void EverySourceInTheOrderIsARegisteredStageWithARangeMode()
    {
        var registry = new StageRegistry(
            PipelineComposition.AllOwnersForConformance(TestDatabase.ConnectionString));

        foreach (var name in BackfillSequence.SourceOrder)
        {
            var owner = registry.Find(name);

            Assert.True(owner is not null, name + " is in BackfillSequence.SourceOrder and is not registered.");
            Assert.True(
                owner is IBackfillStage,
                name + " is in BackfillSequence.SourceOrder and has no range mode [D-93].");
        }
    }

    /// <summary>
    /// **The direction that catches the silent failure.** A component gaining
    /// `IBackfillStage` and not gaining a line in the order produces a backfill that
    /// completes, reports plausible counts and leaves one table holding whatever the
    /// nightly runs put there. Nothing fails, and the first thing that reads the gap is
    /// a screen four phases later.
    ///
    /// C07 FreshnessGuard is not a counter-example: it has no range mode, which is the
    /// decision recorded on the order itself rather than an omission.
    /// </summary>
    [Fact]
    public void EveryRegisteredStageWithARangeModeIsInTheOrder()
    {
        var missing = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .OfType<IBackfillStage>()
            .Select(s => s.Name)
            .Where(n => !BackfillSequence.SourceOrder.Contains(n, StringComparer.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These components have a range mode and are not in BackfillSequence.SourceOrder, so a full " +
            "backfill would complete without ever running them: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The ingest sources come before C01, and C01 before the compute layer.
    ///
    /// Stated as a test rather than as a comment because the order is a plain array and
    /// an edit that reorders it is one line that nothing else notices. Membership is
    /// decided from `price_daily` and from the sector C03 carries [D-92, 3.7], and every
    /// compute stage reads `security_daily` [3.12].
    /// </summary>
    [Fact]
    public void TheUniverseSitsAfterTheIngestAndBeforeTheComputeLayer()
    {
        var order = BackfillSequence.SourceOrder;
        var universe = Array.IndexOf(order, "UniverseBuilder");

        Assert.True(universe > Array.IndexOf(order, "PriceIngestor"));
        Assert.True(universe > Array.IndexOf(order, "FundamentalsIngestor"));
        Assert.True(universe < Array.IndexOf(order, "IndicatorEngine"));
        Assert.True(universe < Array.IndexOf(order, "PercentileEngine"));

        // C10 reads indicator_daily and C11 ranks what the four above it wrote.
        Assert.True(Array.IndexOf(order, "MarketContextEngine") > Array.IndexOf(order, "IndicatorEngine"));
        Assert.Equal(order.Length - 1, Array.IndexOf(order, "PercentileEngine"));
    }

    // --------------------------------------------------------- the sequence ---

    [Fact]
    public async Task TheSourcesRunInTheDeclaredOrderAndEachFinishesBeforeTheNextBegins()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var log = new List<string>();
        var a = new ProbeStage("SrlTestSeqA", BackfillResult.Completed(3, To), log);
        var b = new ProbeStage("SrlTestSeqB", BackfillResult.Completed(4, To), log);
        var c = new ProbeStage("SrlTestSeqC", BackfillResult.Completed(5, To), log);

        var result = await RunAsync([c, a, b], ["SrlTestSeqA", "SrlTestSeqB", "SrlTestSeqC"], ct)
            .ConfigureAwait(true);

        Assert.True(result.Completed);
        Assert.Equal(0, result.ExitCode);

        // The registry sorts ordinally by name and the order is the driver's, so this
        // asserts the driver's rather than the registry's [StageRegistry].
        Assert.Equal(["SrlTestSeqA", "SrlTestSeqB", "SrlTestSeqC"], log);
        Assert.Equal(12L, result.Steps.Sum(s => s.RowsWritten ?? 0));
    }

    /// <summary>
    /// **The property 3.16 is for.** A rebuild is resumed by re-issuing the identical
    /// command, and every source that already finished reports `covered` and writes
    /// nothing. Reading that zero as a short table is what would make the driver refuse
    /// to resume, so the zero-row halt keys on the status rather than on the count.
    /// </summary>
    [Fact]
    public async Task ASourceThatReportsTheRangeAlreadyCoveredDoesNotStopTheSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var log = new List<string>();
        var done = new ProbeStage(
            "SrlTestSeqCovered", BackfillResult.Covered(To, "nothing left to dispatch"), log, writes: true);
        var next = new ProbeStage("SrlTestSeqAfterCovered", BackfillResult.Completed(7, To), log, writes: true);

        var result = await RunAsync(
            [done, next], ["SrlTestSeqCovered", "SrlTestSeqAfterCovered"], ct).ConfigureAwait(true);

        Assert.True(result.Completed);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["SrlTestSeqCovered", "SrlTestSeqAfterCovered"], log);
        Assert.Equal("covered", result.Steps[0].Outcome);
    }

    /// <summary>
    /// The zero-row halt, which is `NightlyRun`'s and is keyed the same way on the
    /// write set: a source that had work and wrote no row is a short table and every
    /// source after it would derive from one.
    /// </summary>
    [Fact]
    public async Task ASourceThatWritesAndProducedNoRowStopsTheSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var log = new List<string>();
        var empty = new ProbeStage("SrlTestSeqEmpty", BackfillResult.Completed(0, To), log, writes: true);
        var next = new ProbeStage("SrlTestSeqAfterEmpty", BackfillResult.Completed(7, To), log, writes: true);

        var result = await RunAsync([empty, next], ["SrlTestSeqEmpty", "SrlTestSeqAfterEmpty"], ct)
            .ConfigureAwait(true);

        Assert.False(result.Completed);
        Assert.False(result.HaltedOnAllowance);

        // Not 2. A source that wrote nothing is not a day to wait out.
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["SrlTestSeqEmpty"], log);
        Assert.Contains(result.Steps, s => s.Outcome == "wrote nothing");
        Assert.Contains(result.Steps, s => s.Stage == "SrlTestSeqAfterEmpty" && s.Outcome == "not reached");
    }

    /// <summary>
    /// Zero rows from a source that declares no write is a legitimate result and not a
    /// short table, which is the same keying `NightlyRun` uses for the freshness guard.
    /// </summary>
    [Fact]
    public async Task ASourceThatDeclaresNoWriteAndProducedNoRowDoesNotStopTheSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var log = new List<string>();
        var quiet = new ProbeStage("SrlTestSeqQuiet", BackfillResult.Completed(0, To), log);
        var next = new ProbeStage("SrlTestSeqAfterQuiet", BackfillResult.Completed(7, To), log);

        var result = await RunAsync([quiet, next], ["SrlTestSeqQuiet", "SrlTestSeqAfterQuiet"], ct)
            .ConfigureAwait(true);

        Assert.True(result.Completed);
        Assert.Equal(["SrlTestSeqQuiet", "SrlTestSeqAfterQuiet"], log);
    }

    /// <summary>
    /// A halt stops the sequence and is exit 2, because everything after it would derive
    /// from a store missing the rows the next run fetches. It is the mechanism working
    /// and a full rebuild halts in the ordinary course.
    /// </summary>
    [Fact]
    public async Task AHaltStopsTheSequenceAndTheSourcesAfterItAreRecordedAsNotReached()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var log = new List<string>();
        var halts = new ProbeStage(
            "SrlTestSeqHalts", BackfillResult.Halted(9, To, "out of allowance"), log, writes: true);
        var next = new ProbeStage("SrlTestSeqAfterHalt", BackfillResult.Completed(7, To), log, writes: true);

        var result = await RunAsync([halts, next], ["SrlTestSeqHalts", "SrlTestSeqAfterHalt"], ct)
            .ConfigureAwait(true);

        Assert.False(result.Completed);
        Assert.True(result.HaltedOnAllowance);
        Assert.Equal(2, result.ExitCode);

        Assert.Equal(["SrlTestSeqHalts"], log);
        Assert.Contains(result.Steps, s => s.Stage == "SrlTestSeqAfterHalt" && s.Outcome == "not reached");

        // The rows the halted source did write are kept and counted, which is what makes
        // a halt different from a failure rather than a softer word for one.
        Assert.Equal(9L, result.Steps.Sum(s => s.RowsWritten ?? 0));
    }

    /// <summary>
    /// A throw stops the sequence and is exit 1. The two outcomes do not share a code:
    /// a halt says run this again tomorrow and a failure says something is wrong, and a
    /// caller reading one number is where they would collapse.
    /// </summary>
    [Fact]
    public async Task AFailureStopsTheSequenceAndIsNotReportedAsAnAllowanceHalt()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var log = new List<string>();
        var boom = new ThrowingStage("SrlTestSeqThrows", log);
        var next = new ProbeStage("SrlTestSeqAfterThrow", BackfillResult.Completed(7, To), log);

        var result = await RunAsync([boom, next], ["SrlTestSeqThrows", "SrlTestSeqAfterThrow"], ct)
            .ConfigureAwait(true);

        Assert.False(result.Completed);
        Assert.False(result.HaltedOnAllowance);
        Assert.Equal(1, result.ExitCode);

        Assert.Equal(["SrlTestSeqThrows"], log);
        Assert.Contains(result.Steps, s => s.Stage == "SrlTestSeqThrows" && s.Outcome == "failed");
        Assert.Contains(result.Steps, s => s.Stage == "SrlTestSeqAfterThrow" && s.Outcome == "not reached");
    }

    /// <summary>
    /// A source the registry does not have stops the sequence, where a night reports it
    /// and carries on. The absence can only be a registry built without a provider
    /// token, and then the compute layer would run over an ingest that called nothing.
    /// </summary>
    [Fact]
    public async Task AnUnregisteredSourceStopsTheSequenceBeforeAnythingAfterItRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var log = new List<string>();
        var present = new ProbeStage("SrlTestSeqPresent", BackfillResult.Completed(2, To), log);

        var result = await RunAsync([present], ["SrlTestSeqAbsent", "SrlTestSeqPresent"], ct)
            .ConfigureAwait(true);

        Assert.False(result.Completed);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(log);
        Assert.Contains(result.Steps, s => s.Stage == "SrlTestSeqAbsent" && s.Outcome == "not registered");
        Assert.Contains(result.Steps, s => s.Stage == "SrlTestSeqPresent" && s.Outcome == "not reached");
    }

    /// <summary>A registered stage with no range mode stops it the same way [D-93].</summary>
    [Fact]
    public async Task ASourceWithNoRangeModeStopsTheSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var log = new List<string>();
        var nightlyOnly = new NightlyOnlyProbe("SrlTestSeqNightlyOnly");
        var next = new ProbeStage("SrlTestSeqAfterNightlyOnly", BackfillResult.Completed(2, To), log);

        var result = await RunAsync(
            [nightlyOnly, next], ["SrlTestSeqNightlyOnly", "SrlTestSeqAfterNightlyOnly"], ct)
            .ConfigureAwait(true);

        Assert.False(result.Completed);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(log);
        Assert.Contains(result.Steps, s => s.Stage == "SrlTestSeqNightlyOnly" && s.Outcome == "no range mode");
    }

    /// <summary>
    /// **The sequence is the same work as the hand-issued commands**, which is the claim
    /// that makes a rebuild and twelve `backfill &lt;stage&gt;` invocations
    /// interchangeable. It holds because every source goes through the same
    /// `BackfillRun`, and the run log rows are what say so.
    /// </summary>
    [Fact]
    public async Task EverySourceThatRanLeavesItsOwnRangeRowInTheRunLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await ClearRunLogAsync("SrlTestSeqLogged", ct).ConfigureAwait(true);

        var log = new List<string>();
        var one = new ProbeStage("SrlTestSeqLogged", BackfillResult.Completed(3, To), log);

        await RunAsync([one], ["SrlTestSeqLogged"], ct).ConfigureAwait(true);

        var line = await LastRangeLineAsync("SrlTestSeqLogged", ct).ConfigureAwait(true);

        Assert.StartsWith("range 2021-01-04..2021-01-08", line, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ harness ---

    private static async Task SeedAsync(CancellationToken ct)
        => await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(false);

    private static async Task<BackfillSequenceResult> RunAsync(
        IEnumerable<IWriteOwner> owners, IReadOnlyList<string> order, CancellationToken ct)
    {
        var registry = new StageRegistry(owners);

        var run = new BackfillRun(
            registry,
            new RunLog(TestDatabase.ConnectionString),
            Clock,
            TestDatabase.ConnectionString,
            new FixedAllowance(ProviderDate));

        return await new BackfillSequence(registry, run)
            .ExecuteAsync(From, To, order, ct).ConfigureAwait(false);
    }

    private static async Task ClearRunLogAsync(string stage, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM run_log WHERE stage = @s;", conn);
        cmd.Parameters.AddWithValue("s", stage);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> LastRangeLineAsync(string stage, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT error FROM run_log WHERE stage = @s ORDER BY run_log_id DESC LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("s", stage);

        return (string?) await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? string.Empty;
    }

    /// <summary>Reads an allowance nothing here spends, the gate not being what these tests are about.</summary>
    private sealed class FixedAllowance(DateOnly stampedOn) : IUnitAllowance
    {
        public Task<AllowanceReading> ReadAsync(CancellationToken ct = default)
            => Task.FromResult(new AllowanceReading(0, 100_000, stampedOn));
    }

    /// <summary>Returns what it was handed and records that it was reached, in order.</summary>
    private sealed class ProbeStage : IBackfillStage
    {
        private readonly BackfillResult _result;
        private readonly List<string> _log;

        public ProbeStage(string name, BackfillResult result, List<string> log, bool writes = false)
        {
            Name = name;
            _result = result;
            _log = log;

            WriteSet = writes
                ? [new TableWrite("price_daily", WriteOperation.Insert)]
                : [];
        }

        public string Name { get; }

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; }

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);

        public Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
        {
            _log.Add(Name);
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingStage(string name, List<string> log) : IBackfillStage
    {
        public string Name => name;

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);

        public Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
        {
            log.Add(Name);
            throw new InvalidOperationException("SrlTest: the range execution failed.");
        }
    }

    private sealed class NightlyOnlyProbe(string name) : IStage
    {
        public string Name => name;

        public IReadOnlyList<string> ReadSet { get; } = ["price_daily"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
            => Task.FromResult(StageResult.None);
    }
}
