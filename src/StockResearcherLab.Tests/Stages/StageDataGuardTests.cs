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
    /// **A transient connect failure is retried, and a connect that times out is the
    /// transient case** [D-100].
    ///
    /// **The trigger is a blackholed address, and it is guaranteed rather than
    /// convenient** [D-103]. `203.0.113.1` is TEST-NET-3, reserved by RFC 5737 for
    /// documentation and allocated to nobody, so nothing answers for it anywhere and the
    /// connect attempt is dropped rather than refused: the one-second timeout is the only
    /// thing that can end it. That is a property of the address registry rather than of
    /// this machine's resolver, network speed or auth method, which is what D-103's check
    /// asks of a trigger.
    ///
    /// **An address rather than a name deliberately**: the predecessor
    /// dialled `srl-no-such-host.invalid`, and a name that does not resolve fails in
    /// the resolver before any connect is attempted, which is the permanent case one
    /// test down. That test passed only while the local resolver happened to take
    /// longer than a second to say so, which made it pass under network load and fail
    /// on a healthy machine.
    ///
    /// The count is asserted rather than only the outcome, because a retry that fires
    /// and one that does not both end in the same exception here.
    ///
    /// **The exception type is deliberately not asserted.** A connect timeout surfaces
    /// as a bare `TimeoutException`, as an `NpgsqlException` wrapping one, or as a
    /// `SocketException` carrying `TimedOut`, depending on where in the handshake it
    /// lands, and the point of D-100 is that all three are one fault. Asserting the
    /// type is what pinned the predecessor to the shape of one observation.
    /// </summary>
    [Fact]
    public async Task ATransientConnectFailureIsRetriedAndTheCountIsReported()
    {
        var unroutable = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString)
        {
            Host = "203.0.113.1",
            Timeout = 1,
        }.ConnectionString;

        var data = new StageData(unroutable, new DeclaredAccess("GuardedStage", ["run_log"], []));

        Assert.Equal(0, data.ConnectionRetries);

        var thrown = await Record.ExceptionAsync(
            () => data.ReadAsync("run_log", "SELECT 1;", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.NotNull(thrown);

        // The server never answered, so whatever this is, it is not the server speaking.
        Assert.IsNotType<PostgresException>(thrown);

        // Three attempts is two retries, and the third failure is the one that throws.
        Assert.Equal(2, data.ConnectionRetries);
    }

    /// <summary>
    /// **A permanent connect failure is not retried, and this is the half the
    /// predecessor exercised while claiming to test the other** [D-100].
    ///
    /// **The trigger is a `.invalid` name, and it is guaranteed rather than
    /// convenient** [D-103]. RFC 2606 reserves `invalid` and RFC 6761 specifies that
    /// resolvers answer names under it negatively; it is not delegated in the root zone,
    /// so there is no address for one to return. The fault is therefore `HostNotFound`
    /// on any machine, rather than depending on how this one is configured.
    ///
    /// `HostNotFound` is a connection string that is wrong. It answers identically
    /// three times, so the attempts and the backoff are spent on something that cannot
    /// succeed, and the run learns at the third failure what it knew at the first.
    ///
    /// **The timeout is generous rather than one second, and that is what makes this
    /// test say the same thing every time.** With a one-second timeout the verdict
    /// turns on whether the resolver answers inside it: fast, and the fault is
    /// `HostNotFound`; slow, and the fault is a timeout and this asserts the opposite
    /// of what it pins. Fifteen seconds means resolution always finishes first.
    /// </summary>
    [Fact]
    public async Task APermanentConnectFailureIsNotRetried()
    {
        var unresolvable = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString)
        {
            Host = "srl-no-such-host.invalid",
            Timeout = 15,
        }.ConnectionString;

        var data = new StageData(unresolvable, new DeclaredAccess("GuardedStage", ["run_log"], []));

        var thrown = await Record.ExceptionAsync(
            () => data.ReadAsync("run_log", "SELECT 1;", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.NotNull(thrown);
        Assert.Equal(SocketVerdict.Permanent, TransientFault.ClassifySocket(thrown));
        Assert.Equal(0, data.ConnectionRetries);
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
    public async Task AConnectionTheServerRefusesIsNotRetried()
    {
        // **A database that cannot exist, not a password that is wrong** [D-103].
        //
        // The predecessor set `Password = "srl-not-the-password"` and asserted the
        // refusal. That is a property of the server's authentication method rather than
        // of this system: `ci.yml` runs Postgres with `POSTGRES_HOST_AUTH_METHOD: trust`,
        // which accepts any credential without checking, so no login is refusable there
        // and the read simply succeeded. It passed on every developer machine, where
        // Postgres asks for a password, and it never once passed on CI: 23 consecutive
        // red runs from the commit that wrote it.
        //
        // **This is open item 29's shape a second time**, fixed the way D-100 fixed it:
        // replace a trigger that depends on the environment with one that does not.
        // `invalid_catalog_name` is raised during connection startup whatever the auth
        // method, so the property under test is unchanged and now holds everywhere.
        // D-103 is the rule the two instances share, and the check it states is whether
        // this test would behave identically on a machine configured differently.
        var refused = new NpgsqlConnectionStringBuilder(TestDatabase.ConnectionString)
        {
            Database = "srl_no_such_database",
        }.ConnectionString;

        var data = new StageData(refused, new DeclaredAccess("GuardedStage", ["run_log"], []));

        await Assert.ThrowsAsync<PostgresException>(
            () => data.ReadAsync("run_log", "SELECT 1;", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(0, data.ConnectionRetries);
    }
}
