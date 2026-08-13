using Npgsql;
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

    // ------------------------------------- the connection retry [item 23] ---
    //
    // The 3.6 sweep failed at a TimeoutException inside AuthenticateSASL, two hours
    // and about 29,500 units in. Only the establishment of a connection retries: a
    // handshake that did not complete wrote nothing and read nothing, so asking again
    // is the same request rather than a second attempt at a side effect.

    /// <summary>
    /// **Nothing is retried once a connection is open.** A host that does not resolve
    /// never gets past the handshake, so it exercises the retry; a statement that
    /// throws against a live connection must surface on the first attempt.
    ///
    /// The count is asserted rather than only the outcome, because a retry that fires
    /// and one that does not both end in the same exception here.
    ///
    /// **A connect-phase timeout is a bare `TimeoutException`**, not an
    /// `NpgsqlException`, and this test is what established that. The predicate was
    /// written against the 3.6 failure, which was an `NpgsqlException` wrapping one,
    /// and it would have looked right while missing this.
    /// </summary>
    [Fact]
    public async Task AConnectionThatCannotBeEstablishedIsRetriedAndTheCountIsReported()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString)
        {
            Host = "srl-no-such-host.invalid",
            Timeout = 1,
        }.ConnectionString;

        var data = new StageData(unreachable, new DeclaredAccess("GuardedStage", ["run_log"], []));

        Assert.Equal(0, data.ConnectionRetries);

        await Assert.ThrowsAsync<TimeoutException>(
            () => data.ReadAsync("run_log", "SELECT 1;", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        // Three attempts is two retries, and the third failure is the one that throws.
        Assert.Equal(2, data.ConnectionRetries);
    }

    /// <summary>
    /// A live connection whose statement fails is not retried. The write already
    /// reached the server, so asking again is a second attempt at a side effect, and
    /// tolerating one reports a completed sweep over a partial load [`RUNBOOK.md`].
    /// </summary>
    [Fact]
    public async Task AStatementThatFailsOnAnOpenConnectionIsNotRetried()
    {
        var data = DataFor(["run_log"], []);

        await Assert.ThrowsAsync<PostgresException>(
            () => data.ReadAsync(
                "run_log", "SELECT no_such_column FROM run_log;", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(0, data.ConnectionRetries);
    }

    /// <summary>
    /// A server that answers and refuses is not retried either. `PostgresException` is
    /// the server speaking rather than failing to, and a bad password says the same
    /// thing three times while the backoff is spent for nothing.
    /// </summary>
    [Fact]
    public async Task ARefusedLoginIsNotRetried()
    {
        var wrong = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString)
        {
            Password = "srl-not-the-password",
        }.ConnectionString;

        var data = new StageData(wrong, new DeclaredAccess("GuardedStage", ["run_log"], []));

        await Assert.ThrowsAsync<PostgresException>(
            () => data.ReadAsync("run_log", "SELECT 1;", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(0, data.ConnectionRetries);
    }
}
