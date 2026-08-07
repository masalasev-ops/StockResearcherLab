using Npgsql;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// Checkpoint 1.12, the staged bulk load path.
///
/// COPY takes no ON CONFLICT, so a copy straight into the target throws a duplicate
/// key error the second time a date is loaded, while D-68 requires every stage write
/// to be idempotent on the table's own grain. The route is COPY into a TEMP staging
/// table then one INSERT ... SELECT ... ON CONFLICT DO UPDATE, inside one connection
/// [A2, A24].
/// </summary>
[Collection("database")]
public sealed class BulkUpsertTests
{
    private static readonly string[] PriceColumns =
        ["ticker", "date", "open", "high", "low", "close", "adj_close", "volume"];

    private static readonly string[] PriceKey = ["ticker", "date"];

    private static StageData DataFor(params string[] writableTables)
    {
        var writes = writableTables
            .Select(t => new TableWrite(t, WriteOperation.Insert))
            .ToList();

        return new StageData(
            TestDatabase.ConnectionString,
            new DeclaredAccess("TestStage", [], writes));
    }

    private static async Task WriteBarAsync(IBulkWriter w, string ticker, DateOnly date, decimal close, CancellationToken ct)
    {
        await w.StartRowAsync(ct).ConfigureAwait(false);
        await w.WriteAsync(ticker, ct).ConfigureAwait(false);
        await w.WriteAsync(date, ct).ConfigureAwait(false);
        await w.WriteAsync(close, ct).ConfigureAwait(false);   // open
        await w.WriteAsync(close, ct).ConfigureAwait(false);   // high
        await w.WriteAsync(close, ct).ConfigureAwait(false);   // low
        await w.WriteAsync(close, ct).ConfigureAwait(false);   // close
        await w.WriteAsync(close, ct).ConfigureAwait(false);   // adj_close
        await w.WriteAsync(1_000L, ct).ConfigureAwait(false);  // volume, bigint
    }

    private static async Task<(long Rows, decimal? Close)> ReadAsync(string ticker, DateOnly date, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*), max(close) FROM price_daily WHERE ticker = @t AND date = @d;", conn);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", date);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        return (r.GetInt64(0), await r.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : r.GetDecimal(1));
    }

    /// <summary>
    /// The undeclared-access assertion, made of the fast path exactly as it is made
    /// of the read and write routes. The COPY path must not be the way the guard
    /// gets bypassed, so it throws before a connection opens.
    /// </summary>
    [Fact]
    public async Task AStageBulkLoadingATableItDoesNotDeclareThrowsBeforeAnythingOpens()
    {
        var data = DataFor("run_log");

        var ex = await Assert.ThrowsAsync<UndeclaredTableAccessException>(
            () => data.BulkUpsertAsync(
                "price_daily", PriceColumns, PriceKey,
                (_, _) => throw new InvalidOperationException("must not be reached"),
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal("price_daily", ex.Table);
    }

    [Fact]
    public async Task LoadingTheSameRowTwiceLeavesTheRowCountUnchangedAndTheValuesEqual()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = DataFor("price_daily");
        var ticker = "SRLTEST.BULK";
        var date = new DateOnly(2026, 8, 5);

        await ClearAsync(ticker, ct).ConfigureAwait(true);

        await data.BulkUpsertAsync("price_daily", PriceColumns, PriceKey,
            (w, c) => WriteBarAsync(w, ticker, date, 10.25m, c), ct).ConfigureAwait(true);

        var first = await ReadAsync(ticker, date, ct).ConfigureAwait(true);
        Assert.Equal(1, first.Rows);
        Assert.Equal(10.25m, first.Close);

        // Again, same key. D-68: a re-run replaces rather than duplicates.
        await data.BulkUpsertAsync("price_daily", PriceColumns, PriceKey,
            (w, c) => WriteBarAsync(w, ticker, date, 10.25m, c), ct).ConfigureAwait(true);

        var second = await ReadAsync(ticker, date, ct).ConfigureAwait(true);
        Assert.Equal(1, second.Rows);
        Assert.Equal(10.25m, second.Close);

        await ClearAsync(ticker, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The case that makes the re-load window work. A day loaded short is topped up
    /// on a later run, so the second load must overwrite the value rather than be
    /// ignored [A10].
    /// </summary>
    [Fact]
    public async Task ReloadingADateWithADifferentValueOverwritesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = DataFor("price_daily");
        var ticker = "SRLTEST.RELOAD";
        var date = new DateOnly(2026, 8, 4);

        await ClearAsync(ticker, ct).ConfigureAwait(true);

        await data.BulkUpsertAsync("price_daily", PriceColumns, PriceKey,
            (w, c) => WriteBarAsync(w, ticker, date, 1.00m, c), ct).ConfigureAwait(true);

        await data.BulkUpsertAsync("price_daily", PriceColumns, PriceKey,
            (w, c) => WriteBarAsync(w, ticker, date, 2.50m, c), ct).ConfigureAwait(true);

        var after = await ReadAsync(ticker, date, ct).ConfigureAwait(true);
        Assert.Equal(1, after.Rows);
        Assert.Equal(2.50m, after.Close);

        await ClearAsync(ticker, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// A24. The staging table is TEMP and therefore invisible to any other
    /// connection. Splitting the COPY and the upsert across two connections would
    /// fail with a relation-does-not-exist error that reads like a migration
    /// problem, which is why the scope is in the signature rather than left to a
    /// caller to hold open.
    /// </summary>
    [Fact]
    public async Task TheStagingTableIsInvisibleToAnotherConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = DataFor("price_daily");
        var ticker = "SRLTEST.TEMP";
        var date = new DateOnly(2026, 8, 3);

        await ClearAsync(ticker, ct).ConfigureAwait(true);

        bool? visibleFromElsewhere = null;

        await data.BulkUpsertAsync("price_daily", PriceColumns, PriceKey,
            async (w, c) =>
            {
                await WriteBarAsync(w, ticker, date, 3.00m, c).ConfigureAwait(false);

                // A second connection, while the staging table exists on the first.
                await using var other = new NpgsqlConnection(TestDatabase.ConnectionString);
                await other.OpenAsync(c).ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand(
                    "SELECT to_regclass('srl_stage_price_daily') IS NOT NULL;", other);
                visibleFromElsewhere = (bool?)await cmd.ExecuteScalarAsync(c).ConfigureAwait(false);
            }, ct).ConfigureAwait(true);

        Assert.False(visibleFromElsewhere,
            "The staging table was visible to a second connection, so it is not TEMP. A named " +
            "staging table leaves rows behind after a crash and collides when two stages load " +
            "at once [A24].");

        await ClearAsync(ticker, ct).ConfigureAwait(true);
    }

    [Fact]
    public async Task AConflictTargetIsRequired()
    {
        var data = DataFor("price_daily");

        await Assert.ThrowsAsync<ArgumentException>(
            () => data.BulkUpsertAsync(
                "price_daily", PriceColumns, [],
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task AnIdentifierThatIsNotPlainIsRefused()
    {
        var data = DataFor("price_daily");

        await Assert.ThrowsAsync<ArgumentException>(
            () => data.BulkUpsertAsync(
                "price_daily", ["ticker\"; DROP TABLE price_daily; --"], PriceKey,
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    private static async Task ClearAsync(string ticker, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM price_daily WHERE ticker = @t;", conn);
        cmd.Parameters.AddWithValue("t", ticker);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
