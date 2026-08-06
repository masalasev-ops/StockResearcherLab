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

    [Fact]
    public async Task TheNoOpStageRunsAndLogsOk()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = TestDatabase.ConnectionString;
        var runLog = new RunLog(cs);
        var registry = PipelineComposition.BuildRegistry(cs);

        var before = (await runLog.RecentAsync(500, ct).ConfigureAwait(true))
            .Count(r => string.Equals(r.Stage, "NoOpStage", StringComparison.Ordinal));

        var result = await new StageRunner(registry, runLog, Clock, cs)
            .RunAsync("NoOpStage", Clock.Today, configVersion: 1, ct).ConfigureAwait(true);

        Assert.Equal(0, result.RowsWritten);

        var rows = (await runLog.RecentAsync(500, ct).ConfigureAwait(true))
            .Where(r => string.Equals(r.Stage, "NoOpStage", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(before + 1, rows.Count);

        var latest = rows[0];
        Assert.Equal("ok", latest.Status);
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
