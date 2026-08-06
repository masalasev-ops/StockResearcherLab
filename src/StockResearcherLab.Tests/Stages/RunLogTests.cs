using StockResearcherLab.Core;
using StockResearcherLab.Data;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

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

        var recent = await runLog.RecentAsync(200, ct).ConfigureAwait(true);
        var written = recent.Single(r => string.Equals(r.Stage, stage, StringComparison.Ordinal));

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

        await runLog.RecordAsync(
            new DateOnly(2026, 8, 6), stage, "failed",
            new DateTimeOffset(2026, 8, 6, 22, 31, 0, TimeSpan.Zero),
            durationMs: 12, rowsWritten: null,
            error: "deliberate", ct).ConfigureAwait(true);

        var written = (await runLog.RecentAsync(200, ct).ConfigureAwait(true))
            .Single(r => string.Equals(r.Stage, stage, StringComparison.Ordinal));

        Assert.Equal("failed", written.Status);
        Assert.Equal("deliberate", written.Error);

        // Null means unknown, never zero. A failed stage wrote an unknown number
        // of rows, not zero of them [CLAUDE.md section 6].
        Assert.Null(written.RowsWritten);
    }
}
