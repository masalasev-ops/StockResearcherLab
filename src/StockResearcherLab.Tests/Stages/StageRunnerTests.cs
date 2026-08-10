using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// The no-op stage end to end [0.7], and the path a stage takes when it fails.
/// </summary>
public sealed class StageRunnerTests
{
    private static readonly IClock Clock = new FixedClock(
        new DateTimeOffset(2026, 8, 6, 23, 0, 0, TimeSpan.Zero),
        new DateOnly(2026, 8, 6));

    /// <summary>
    /// The whole path: declaration, guard, execution, run_log row. It ran against
    /// the registered NoOpStage until 1.11 retired that, and now runs against a
    /// test-local stage instead.
    ///
    /// The subject was never the stage. It was the runner, and a stage registered
    /// in the product purely so a test had something to call is a component name
    /// `ARCHITECTURE.html` section 3 does not have [carried obligation 0 to 1].
    /// </summary>
    [Fact]
    public async Task AStageThatWritesNothingRunsAndLogsOk()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = TestDatabase.ConnectionString;
        var runLog = new RunLog(cs);

        var stageName = "Quiet-" + Guid.NewGuid().ToString("N")[..8];
        var registry = new StageRegistry([new QuietStage(stageName), new RunLog(cs)]);

        var result = await new StageRunner(registry, runLog, Clock, cs)
            .RunAsync(stageName, Clock.Today, configVersion: 1, ct).ConfigureAwait(true);

        Assert.Equal(0, result.RowsWritten);

        var rows = (await runLog.RecentAsync(500, ct).ConfigureAwait(true))
            .Where(r => string.Equals(r.Stage, stageName, StringComparison.Ordinal))
            .ToList();

        var latest = Assert.Single(rows);
        Assert.Equal("ok", latest.Status);

        // Zero rows is a legitimate result and not an error on its own. It is
        // recorded as zero rather than as unknown, which is what a stage that threw
        // records [CLAUDE.md section 6].
        Assert.Equal(0, latest.RowsWritten);
        Assert.Null(latest.Error);
        Assert.NotNull(latest.DurationMs);
    }

    [Fact]
    public async Task AStageThatReachesOutsideItsDeclaredSetFailsTheRunAndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = TestDatabase.ConnectionString;
        var runLog = new RunLog(cs);

        var stageName = "Trespasser-" + Guid.NewGuid().ToString("N")[..8];
        var registry = new StageRegistry([new TrespassingStage(stageName), new RunLog(cs)]);

        // Fails closed. The exception propagates rather than the runner swallowing
        // it and reporting a short result [CLAUDE.md section 6].
        await Assert.ThrowsAsync<UndeclaredTableAccessException>(
            () => new StageRunner(registry, runLog, Clock, cs)
                .RunAsync(stageName, Clock.Today, configVersion: 1, ct)).ConfigureAwait(true);

        var row = (await runLog.RecentAsync(500, ct).ConfigureAwait(true))
            .Single(r => string.Equals(r.Stage, stageName, StringComparison.Ordinal));

        Assert.Equal("failed", row.Status);
        Assert.Contains("does not declare", row.Error!, StringComparison.Ordinal);

        // Unknown, not zero. The stage threw partway and how many rows it wrote is
        // not something anyone knows [CLAUDE.md section 6].
        Assert.Null(row.RowsWritten);
    }

    [Fact]
    public async Task RunningAnUnregisteredStageIsRefused()
    {
        var cs = TestDatabase.ConnectionString;
        var runner = new StageRunner(PipelineComposition.BuildRegistry(cs), new RunLog(cs), Clock, cs);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync("NotRegistered", Clock.Today, 1, TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Contains("is registered", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads through the declared route, writes nothing, returns zero rows. It
    /// reads rather than doing nothing at all, because taking the route is what is
    /// being proved.
    /// </summary>
    private sealed class QuietStage(string name) : IStage
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> ReadSet { get; } = ["run_log"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
        {
            _ = await context.Data
                .ReadAsync("run_log", "SELECT count(*) FROM run_log;", ct).ConfigureAwait(false);

            return StageResult.None;
        }
    }

    /// <summary>Declares run_log and reads security. Both tables exist, so without the guard this succeeds.</summary>
    private sealed class TrespassingStage(string name) : IStage
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> ReadSet { get; } = ["run_log"];

        public IReadOnlyList<TableWrite> WriteSet { get; } = [];

        public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
        {
            await context.Data.ReadAsync("security", "SELECT count(*) FROM security;", ct).ConfigureAwait(false);
            return StageResult.None;
        }
    }
}
