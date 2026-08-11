using Npgsql;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// D-80's three values, and the two refusals that make them a constraint rather than a
/// convention.
///
/// The label segments analysis, so a drifted or mistyped value lands in its own bucket
/// in every segmentation without ever erroring. That is why the enumeration is enforced
/// by the database and why two of these tests go to it rather than to the rule.
/// </summary>
[Collection("database")]
public sealed class MarketContextEngineTests
{
    private const double High = 0.60;
    private const double Low = 0.40;

    [Fact]
    public void BreadthHighAndTheBenchmarkAboveItsAverageIsRiskOn()
        => Assert.Equal(MarketContextEngine.RiskOn, MarketContextEngine.Regime(0.72, true, High, Low));

    [Fact]
    public void BreadthLowAndTheBenchmarkBelowItsAverageIsRiskOff()
        => Assert.Equal(MarketContextEngine.RiskOff, MarketContextEngine.Regime(0.28, false, High, Low));

    /// <summary>
    /// The case D-80 names directly, and the one that shows why no minimum run length
    /// is needed.
    ///
    /// A single crossing on either input moves the label to `mixed` rather than
    /// flipping it to the opposite, so the label cannot alternate between `risk_on` and
    /// `risk_off`. What is left is a boundary period read as a boundary period.
    /// </summary>
    [Fact]
    public void BreadthClearingTheHighWhileTheBenchmarkSitsBelowIsMixed()
    {
        Assert.Equal(MarketContextEngine.Mixed, MarketContextEngine.Regime(0.72, false, High, Low));
        Assert.Equal(MarketContextEngine.Mixed, MarketContextEngine.Regime(0.28, true, High, Low));
    }

    /// <summary>The thresholds are "at or above" and "at or below", so the boundary itself is inside.</summary>
    [Fact]
    public void TheThresholdsThemselvesAreInside()
    {
        Assert.Equal(MarketContextEngine.RiskOn, MarketContextEngine.Regime(High, true, High, Low));
        Assert.Equal(MarketContextEngine.RiskOff, MarketContextEngine.Regime(Low, false, High, Low));
    }

    /// <summary>
    /// VIX contributes nothing and the label does not go null with it. Three components
    /// read the label and a null would degrade all three over a column none of them
    /// reads [D-80].
    /// </summary>
    [Fact]
    public async Task ADateWithANullVixStillResolvesToALabel()
    {
        await using var conn = await TestDatabase.OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var date = new DateOnly(2001, 3, 14);
        await ClearAsync(conn, date).ConfigureAwait(true);

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO market_context_daily (date, breadth, vix, regime_label, sector_relative_strength)
            VALUES (@d, 0.7, NULL, 'risk_on', '{}'::jsonb);
            """, conn))
        {
            cmd.Parameters.AddWithValue("d", date);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await using (var cmd = new NpgsqlCommand(
            "SELECT regime_label, vix FROM market_context_daily WHERE date = @d;", conn))
        {
            cmd.Parameters.AddWithValue("d", date);
            await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.True(await r.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Equal("risk_on", r.GetString(0));
            Assert.True(await r.IsDBNullAsync(1, TestContext.Current.CancellationToken).ConfigureAwait(true));
        }

        await ClearAsync(conn, date).ConfigureAwait(true);
    }

    /// <summary>
    /// A fourth value is refused with 23514, the check violation.
    ///
    /// Without the constraint this row lands, reads as a regime nobody defined, and
    /// segments every analysis that groups on the column. Nothing errors at any point,
    /// which is the whole reason the enumeration is in the database.
    /// </summary>
    [Fact]
    public async Task AFourthValueIsRefusedByTheDatabase()
    {
        await using var conn = await TestDatabase.OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var date = new DateOnly(2001, 3, 15);
        await ClearAsync(conn, date).ConfigureAwait(true);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO market_context_daily (date, regime_label) VALUES (@d, 'risk-on');", conn);
        cmd.Parameters.AddWithValue("d", date);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal("23514", ex.SqlState);
    }

    /// <summary>A null label is refused with 23502, the not-null violation.</summary>
    [Fact]
    public async Task ANullLabelIsRefusedByTheDatabase()
    {
        await using var conn = await TestDatabase.OpenAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var date = new DateOnly(2001, 3, 16);
        await ClearAsync(conn, date).ConfigureAwait(true);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO market_context_daily (date, breadth) VALUES (@d, 0.5);", conn);
        cmd.Parameters.AddWithValue("d", date);

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal("23502", ex.SqlState);
    }

    /// <summary>
    /// The stage's own write path, which nothing exercised until 2.12 ran it.
    ///
    /// **`sector_relative_strength` is `jsonb` and binary COPY carries no type name.**
    /// The driver infers one from the CLR type, a string infers `text`, and the two
    /// formats differ by a leading one-byte format version. Postgres read the
    /// document's opening brace as that version and refused the row with "unsupported
    /// jsonb version number 123", which is `{`. The stage could not write at all, and
    /// every test above went to the rule or to the constraint rather than through the
    /// write, so all of them passed.
    ///
    /// This goes through <see cref="IBulkWriter"/> as the stage does, with the same
    /// column set, rather than through a plain `INSERT` where the type is named in the
    /// SQL and the failure cannot occur.
    /// </summary>
    [Fact]
    public async Task TheJsonbColumnIsWrittenThroughTheBulkPathAndReadsBackAsAnObject()
    {
        var ct = TestContext.Current.CancellationToken;
        var date = new DateOnly(2001, 3, 17);

        await using (var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true))
        {
            await ClearAsync(conn, date).ConfigureAwait(true);
        }

        var data = new StageData(
            TestDatabase.ConnectionString,
            new DeclaredAccess("TestMarketContext", [],
                [new TableWrite("market_context_daily", WriteOperation.Insert, MarketContextEngine.Columns)]));

        const string sectors = """{"Energy":-0.012340,"Technology":0.045600}""";

        await data.BulkUpsertAsync(
            "market_context_daily", MarketContextEngine.Columns, ["date"],
            async (w, c) =>
            {
                await w.StartRowAsync(c).ConfigureAwait(false);
                await w.WriteAsync(date, c).ConfigureAwait(false);
                await w.WriteAsync((float?) 0.55f, c).ConfigureAwait(false);
                await w.WriteAsync<float?>(null, c).ConfigureAwait(false);
                await w.WriteAsync(MarketContextEngine.Mixed, c).ConfigureAwait(false);
                await w.WriteJsonAsync(sectors, c).ConfigureAwait(false);
            }, ct).ConfigureAwait(true);

        await using (var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true))
        {
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT jsonb_typeof(sector_relative_strength),
                       (sector_relative_strength ->> 'Technology')::float8,
                       (SELECT count(*) FROM jsonb_object_keys(sector_relative_strength))
                FROM market_context_daily WHERE date = @d;
                """, conn))
            {
                cmd.Parameters.AddWithValue("d", date);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

                Assert.True(await r.ReadAsync(ct).ConfigureAwait(true));

                // Read back as a document rather than as a string, which is what
                // separates a jsonb column that holds JSON from one that holds bytes
                // Postgres could not parse.
                Assert.Equal("object", r.GetString(0));
                Assert.Equal(0.0456, r.GetDouble(1), 6);
                Assert.Equal(2L, r.GetInt64(2));
            }

            await ClearAsync(conn, date).ConfigureAwait(true);
        }
    }

    private static async Task ClearAsync(NpgsqlConnection conn, DateOnly date)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM market_context_daily WHERE date = @d;", conn);
        cmd.Parameters.AddWithValue("d", date);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
}
