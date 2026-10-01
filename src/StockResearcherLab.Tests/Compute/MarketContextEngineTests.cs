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

    /// <summary>
    /// The sector value is a difference of two trailing returns, not a ratio of two
    /// growth factors [`METRICS.md`, D-158, 5.5.3].
    ///
    /// One sector at +1 percent a day and a universe at +0.4 percent, both over 63
    /// sessions. The sector's 63-session return is 1.01^63 - 1 and the universe's is
    /// 1.004^63 - 1, so the value is 1.01^63 - 1.004^63, about 0.584. The ratio the
    /// code emitted until 5.5.3 is 1.01^63 / 1.004^63 - 1, about 0.452: the same sign,
    /// a plausible size, and a different number, which is why nothing noticed.
    /// </summary>
    [Fact]
    public void TheSectorValueIsTheSectorReturnMinusTheUniverseReturn()
    {
        var rows = new List<MarketContextEngine.CompositeReturn>();

        for (var i = 0; i < 63; i++)
        {
            var date = new DateOnly(1994, 1, 3).AddDays(i);
            rows.Add(new MarketContextEngine.CompositeReturn(null, date, 0.004));
            rows.Add(new MarketContextEngine.CompositeReturn("SRL55-Alpha", date, 0.01));
        }

        var values = Parse(MarketContextEngine.SectorRelativeStrength(rows));

        Assert.Equal(new[] { "SRL55-Alpha" }, values.Keys.ToArray());
        Assert.Equal(Math.Pow(1.01, 63) - Math.Pow(1.004, 63), values["SRL55-Alpha"], 5);
    }

    /// <summary>
    /// **Both composites are chained over the same 63 sessions, the universe's most recent**
    /// [`METRICS.md`, "over the same window", D-158, 5.5.3].
    ///
    /// A member whose last 64 bars straddle a hole reaches one session further back and
    /// returns a row dated before every other member's window. Five such members anywhere in
    /// the universe carry the universe composite onto that date, and a chain over every date
    /// it is given then compares a 63-session sector return with a 64-session universe one.
    /// The stray date here carries a return of +50 percent, standing for a few gap names'
    /// return across their holes, and it must move nothing.
    /// </summary>
    [Fact]
    public void AnOlderDateTheUniverseAloneCarriesIsOutsideTheWindow()
    {
        var rows = new List<MarketContextEngine.CompositeReturn>
        {
            new(null, new DateOnly(1994, 1, 2), 0.5),
        };

        for (var i = 0; i < 63; i++)
        {
            var date = new DateOnly(1994, 1, 3).AddDays(i);
            rows.Add(new MarketContextEngine.CompositeReturn(null, date, 0.004));
            rows.Add(new MarketContextEngine.CompositeReturn("SRL55-Alpha", date, 0.01));
        }

        var values = Parse(MarketContextEngine.SectorRelativeStrength(rows));

        Assert.Equal(Math.Pow(1.01, 63) - Math.Pow(1.004, 63), values["SRL55-Alpha"], 5);
    }

    /// <summary>
    /// A sector that does not cover every session of the window carries no value, rather than
    /// a return over a different window from the universe's. Here Beta has 63 rows, one of
    /// them on the older stray date and none on the window's last session.
    /// </summary>
    [Fact]
    public void ASectorThatDoesNotCoverTheWindowCarriesNoValue()
    {
        var rows = new List<MarketContextEngine.CompositeReturn>
        {
            new(null, new DateOnly(1994, 1, 2), 0.0),
            new("SRL55-Beta", new DateOnly(1994, 1, 2), 0.0),
        };

        for (var i = 0; i < 63; i++)
        {
            var date = new DateOnly(1994, 1, 3).AddDays(i);
            rows.Add(new MarketContextEngine.CompositeReturn(null, date, 0.004));
            rows.Add(new MarketContextEngine.CompositeReturn("SRL55-Alpha", date, 0.01));

            if (i < 62)
            {
                rows.Add(new MarketContextEngine.CompositeReturn("SRL55-Beta", date, 0.0));
            }
        }

        var values = Parse(MarketContextEngine.SectorRelativeStrength(rows));

        Assert.Equal(new[] { "SRL55-Alpha" }, values.Keys.ToArray());
    }

    /// <summary>
    /// The universe composite is the mean over every active member, not the mean of the
    /// sector means, and it is computed by the stage's own statement against a store
    /// [`METRICS.md`, D-158, 5.5.3].
    ///
    /// Twenty members over 70 sessions, each moving by a constant fraction a day so that
    /// every composite is a closed form. Alpha: five members at +1 percent. Beta: ten at
    /// zero. Gamma: two at -1 percent, below the five-member floor, so it carries no key
    /// of its own. And three members with no sector at zero, which carry no key either.
    /// **All twenty are in the universe composite**: (5 x 0.01 + 2 x -0.01) / 20 is
    /// 0.0015 a day. The mean of the sector means the code took until 5.5.3 counts only
    /// Alpha and Beta, (0.01 + 0) / 2 = 0.005 a day, which weighs Alpha's five as much as
    /// Beta's ten and leaves out the five members in no qualifying sector.
    ///
    /// Alpha is then 1.01^63 - 1.0015^63 and Beta is 1 - 1.0015^63.
    /// </summary>
    [Fact]
    public async Task TheUniverseCompositeWeighsEveryActiveMemberEqually()
    {
        var ct = TestContext.Current.CancellationToken;
        var start = new DateOnly(1994, 1, 3);
        const int sessions = 70;
        var asOf = start.AddDays(sessions - 1);

        var members = new List<(string Ticker, string? Sector, double Daily)>();
        members.AddRange(Enumerable.Range(1, 5).Select(i => ($"SRL55A{i:00}.US", (string?) "SRL55-Alpha", 0.01)));
        members.AddRange(Enumerable.Range(1, 10).Select(i => ($"SRL55B{i:00}.US", (string?) "SRL55-Beta", 0.0)));
        members.AddRange(Enumerable.Range(1, 2).Select(i => ($"SRL55C{i:00}.US", (string?) "SRL55-Gamma", -0.01)));
        members.AddRange(Enumerable.Range(1, 3).Select(i => ($"SRL55N{i:00}.US", (string?) null, 0.0)));

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await ClearFixtureAsync(conn).ConfigureAwait(true);

        foreach (var (ticker, sector, daily) in members)
        {
            await using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, @s, 'small', 1000000000, true);
                """, conn))
            {
                cmd.Parameters.AddWithValue("t", ticker);
                cmd.Parameters.AddWithValue("d", start);
                cmd.Parameters.AddWithValue("s", (object?) sector ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
            }

            for (var i = 0; i < sessions; i++)
            {
                var price = Math.Round((decimal) (100 * Math.Pow(1 + daily, i)), 6);

                await using var bar = new NpgsqlCommand(
                    """
                    INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
                    VALUES (@t, @d, @p, @p, @p, @p, @p, 1000);
                    """, conn);
                bar.Parameters.AddWithValue("t", ticker);
                bar.Parameters.AddWithValue("d", start.AddDays(i));
                bar.Parameters.AddWithValue("p", price);
                await bar.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
            }
        }

        var rows = new List<MarketContextEngine.CompositeReturn>();

        await using (var cmd = new NpgsqlCommand(MarketContextEngine.SectorCompositeSql(asOf, 5), conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(true))
            {
                rows.Add(new MarketContextEngine.CompositeReturn(
                    await r.IsDBNullAsync(0, ct).ConfigureAwait(true) ? null : r.GetString(0),
                    DateOnly.FromDateTime(r.GetDateTime(1)),
                    (double) r.GetDecimal(2)));
            }
        }

        await ClearFixtureAsync(conn).ConfigureAwait(true);

        var values = Parse(MarketContextEngine.SectorRelativeStrength(rows));

        Assert.Equal(new[] { "SRL55-Alpha", "SRL55-Beta" }, values.Keys.ToArray());
        Assert.Equal(Math.Pow(1.01, 63) - Math.Pow(1.0015, 63), values["SRL55-Alpha"], 5);
        Assert.Equal(1 - Math.Pow(1.0015, 63), values["SRL55-Beta"], 5);
    }

    /// <summary>The emitted object as sector to value.</summary>
    private static SortedDictionary<string, double> Parse(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var values = new SortedDictionary<string, double>(StringComparer.Ordinal);

        foreach (var p in doc.RootElement.EnumerateObject())
        {
            values[p.Name] = p.Value.GetDouble();
        }

        return values;
    }

    private static async Task ClearFixtureAsync(NpgsqlConnection conn)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM price_daily WHERE ticker LIKE 'SRL55%';",
                     "DELETE FROM security_daily WHERE ticker LIKE 'SRL55%';",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    private static async Task ClearAsync(NpgsqlConnection conn, DateOnly date)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM market_context_daily WHERE date = @d;", conn);
        cmd.Parameters.AddWithValue("d", date);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
}
