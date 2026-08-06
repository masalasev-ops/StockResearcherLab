using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// The guard in front of the real database. Testing DeclaredAccess alone would
/// prove the rule is expressible, not that it is enforced on the path a stage
/// actually takes.
/// </summary>
public sealed class StageDataGuardTests
{
    private static StageData DataFor(IReadOnlyList<string> reads, IReadOnlyList<TableWrite> writes)
        => new(TestDatabase.ConnectionString, new DeclaredAccess("GuardedStage", reads, writes));

    [Fact]
    public async Task ADeclaredReadReachesTheDatabase()
    {
        var data = DataFor(["run_log"], []);

        var rows = await data.ReadAsync(
            "run_log", "SELECT count(*) FROM run_log;", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Single(rows);
    }

    [Fact]
    public async Task AnUndeclaredReadFailsBeforeTheDatabaseIsTouched()
    {
        // Declares run_log and reads security. The SQL below is valid and the
        // table exists, so if the guard were absent this would succeed and return
        // rows, which is exactly the silent success the declaration exists to
        // prevent.
        var data = DataFor(["run_log"], []);

        var ex = await Assert.ThrowsAsync<UndeclaredTableAccessException>(
            () => data.ReadAsync("security", "SELECT count(*) FROM security;", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal("GuardedStage", ex.Stage);
        Assert.Equal("security", ex.Table);
    }

    [Fact]
    public async Task AnUndeclaredWriteFailsBeforeTheDatabaseIsTouched()
    {
        var data = DataFor([], [new TableWrite("run_log", WriteOperation.Insert)]);

        var ex = await Assert.ThrowsAsync<UndeclaredTableAccessException>(
            () => data.WriteAsync(
                "alert", WriteOperation.Insert,
                "INSERT INTO alert (date, alert_type) VALUES (current_date, 'should-not-run');",
                null, TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal("alert", ex.Table);

        // And nothing was written, which is the half a thrown exception does not
        // prove on its own.
        var rows = await DataFor(["alert"], [])
            .ReadAsync("alert", "SELECT count(*) FROM alert WHERE alert_type = 'should-not-run';",
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(0L, Convert.ToInt64(rows[0][0], System.Globalization.CultureInfo.InvariantCulture));
    }
}
