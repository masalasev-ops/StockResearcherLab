using Npgsql;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// Checkpoint 4.13. The two-pass range run, and §5's pre-registered persistence measure.
///
/// **Pass two refuses to run unless pass one covered the calendar's every session.** A
/// floor is the 98th percentile of a screen's own trailing distribution [D-9, D-115], so
/// a floor drawn over a short score table comes from a population that does not exist and
/// looks entirely normal doing it: a number in the right range, on every date, against a
/// table with the right columns. Nothing downstream can tell the difference, which is why
/// the refusal has to be here.
/// </summary>
[Collection("database")]
public sealed class ScreenRangeRunTests
{
    /// <summary>
    /// A short window inside 2022, so the fixture's dates fall in one partition and no
    /// other Select fixture's by-date cleanup reaches them.
    /// </summary>
    private static readonly DateOnly From = new(2022, 3, 1);

    private static readonly DateOnly To = new(2022, 3, 10);

    private const string Screen = "SRLRANGE";
    private const string Sector = "srltest-range";
    private const string Name = "SRLR.A";

    private const string OneMetric =
        """[{"metric":"adx14","direction":"high","weight":1}]""";

    // ------------------------------------------------------------- the passes ---

    /// <summary>
    /// Pass one scores every session in the range and ranks nothing. `rank_within_screen`
    /// null everywhere afterwards is 4.13's own done-when.
    /// </summary>
    [Fact]
    public async Task PassOneScoresEverySessionAndRanksNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            var one = await Run().ScoreAsync(From, To, ct);

            Assert.Equal(await SessionCountAsync(ct), one.Dates);
            Assert.Equal(one.Dates, await ScoredDatesAsync(ct));
            Assert.Equal(0, await RankedRowsAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **An interrupted pass one blocks pass two rather than producing floors over a short
    /// table.** One session removed is enough, which is what makes this an assertion about
    /// the comparison rather than about an obviously empty table.
    /// </summary>
    [Fact]
    public async Task AnInterruptedPassOneBlocksPassTwo()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await Run().ScoreAsync(From, To, ct);

            var dropped = await OneScoredDateAsync(ct);
            await ExecAsync("DELETE FROM screen_score_daily WHERE date = @d;", ct, ("d", dropped));

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Run().FloorAsync(From, To, ct));

            Assert.Contains("will not run", thrown.Message, StringComparison.Ordinal);

            // And nothing was written by the refusal, so a caller that ignored the
            // exception would still find no floor rather than a partial one.
            Assert.Equal(0, await HistoryRowsAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// Pass two runs when pass one is complete, and writes a `screen_history` row per
    /// screen per session whether or not a floor exists on it.
    /// </summary>
    [Fact]
    public async Task PassTwoRunsWhenPassOneIsCompleteAndRecordsEverySession()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            var one = await Run().ScoreAsync(From, To, ct);
            var two = await Run().FloorAsync(From, To, ct);

            Assert.Equal(one.Dates, two.Dates);
            Assert.Equal(one.Dates, await HistoryRowsAsync(ct));

            // Below the lookback there is no floor and nothing is ranked, which over a
            // ten-session fixture is every session [D-115].
            Assert.Equal(0, await RankedRowsAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>Re-running one date reproduces it byte-identically.</summary>
    [Fact]
    public async Task ReRunningOneDateReproducesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await Run().ScoreAsync(From, To, ct);
            var first = await ScoreDigestAsync(ct);

            await Run().ScoreAsync(From, To, ct);

            Assert.Equal(first, await ScoreDigestAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// A range holding no session fails rather than reporting success over nothing, which
    /// is the silent zero `CLAUDE.md` §1 describes.
    /// </summary>
    [Fact]
    public async Task ARangeWithNoSessionFails()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run().ScoreAsync(new DateOnly(1990, 1, 1), new DateOnly(1990, 1, 5), ct));
    }

    // ------------------------------------------------- the persistence measure ---

    /// <summary>
    /// **The chance baseline is the closed form and not a simulation**, so it carries no
    /// seed and reproduces exactly. Two independent draws of k from n have an expected
    /// intersection of k²/n and an expected union of 2k - k²/n, so the Jaccard ratio is
    /// k / (2n - k).
    /// </summary>
    [Theory]
    [InlineData(50d, 2500d, 50d / 4950d)]
    [InlineData(1d, 1d, 1d)]
    [InlineData(0d, 100d, 0d)]
    [InlineData(10d, 0d, 0d)]
    public void TheChanceBaselineIsTheClosedForm(double ranked, double scored, double expected)
        => Assert.Equal(expected, PersistenceMeasure.ChanceOverlap(ranked, scored), 10);

    /// <summary>
    /// The Jaccard function is the database's and is exercised rather than assumed. It is
    /// called from inside an aggregate over twenty million rows, so it is schema rather
    /// than code [`0020`].
    /// </summary>
    [Theory]
    [InlineData("{A,B,C}", "{A,B,C}", 1.0)]
    [InlineData("{A,B,C}", "{D,E,F}", 0.0)]
    [InlineData("{A,B}", "{B,C}", 1.0 / 3.0)]
    [InlineData("{A,B,C,D}", "{C,D}", 0.5)]
    public async Task TheJaccardFunctionAgreesWithTheDefinition(string a, string b, double expected)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand("SELECT jaccard(@a::text[], @b::text[]);", conn);

        cmd.Parameters.AddWithValue("a", a);
        cmd.Parameters.AddWithValue("b", b);

        Assert.Equal(
            expected,
            Convert.ToDouble(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true),
                System.Globalization.CultureInfo.InvariantCulture),
            6);
    }

    /// <summary>
    /// **Two empty sets have no overlap rather than an overlap of zero**, and a null
    /// argument likewise. A date with no prior ranked set is filtered out of the measure
    /// rather than averaged in as a zero, which over the first 250 sessions of the window
    /// would drag every screen's figure toward zero and read as near-chance persistence on
    /// all five.
    /// </summary>
    [Fact]
    public async Task AnAbsentSetIsNullRatherThanZero()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        await using (var empty = new NpgsqlCommand(
            "SELECT jaccard('{}'::text[], '{}'::text[]);", conn))
        {
            Assert.True(await empty.ExecuteScalarAsync(ct).ConfigureAwait(true) is DBNull);
        }

        await using (var missing = new NpgsqlCommand(
            "SELECT jaccard(NULL::text[], '{A}'::text[]);", conn))
        {
            Assert.True(await missing.ExecuteScalarAsync(ct).ConfigureAwait(true) is DBNull);
        }
    }

    /// <summary>
    /// **The measure reads no forward return and no attribution row**, so it does not
    /// touch `CLAUDE.md` §11's prohibition on tuning screens on forward returns before
    /// anything has judged them. Asserted against the statement rather than argued.
    /// </summary>
    [Fact]
    public void TheMeasureReadsNoReturnAndNoAttribution()
    {
        var sql = PersistenceMeasure.Sql(From, To);

        Assert.DoesNotContain("attribution", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("return_", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("vs_peers", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("vs_spy", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate_set", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three lags are §5's three and are stated in the code as one list, so a fourth
    /// arriving would arrive in the statement too.
    /// </summary>
    [Fact]
    public void TheThreeLagsAreTheOnesStatedInAdvance()
        => Assert.Equal([1, 5, 21], PersistenceMeasure.Lags);

    /// <summary>
    /// The measure over a fixture whose ranked set is known: one name ranked on every
    /// session, so the overlap is 1 at every lag and the figures come back per screen.
    /// A degenerate fixture, and its job is to prove the statement runs and groups rather
    /// than to say anything about a screen.
    /// </summary>
    [Fact]
    public async Task TheMeasureReturnsARowPerScreenWithItsOwnBaseline()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await Run().ScoreAsync(From, To, ct);

            // Rank the one name on every session, the fixture window being far too short
            // for a floor to exist on its own.
            await ExecAsync(
                "UPDATE screen_score_daily SET rank_within_screen = 1 " +
                "WHERE date BETWEEN @f AND @t AND score IS NOT NULL;",
                ct, ("f", From), ("t", To));

            var measured = await PersistenceMeasure
                .MeasureAsync(TestDatabase.ConnectionString, From, To, ct);

            var screen = Assert.Single(measured);

            Assert.Equal(Screen, screen.ScreenId);
            Assert.True(screen.Pairs > 0);
            Assert.Equal(1d, screen.Lag1, 6);
            Assert.Equal(1d, screen.MeanRankedSize, 6);
            Assert.Equal(
                PersistenceMeasure.ChanceOverlap(screen.MeanRankedSize, screen.MeanScoredSize),
                screen.Chance,
                10);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------------------- plumbing ---

    private static ScreenRangeRun Run() => new(TestDatabase.ConnectionString);

    private static async Task SeedAsync(CancellationToken ct)
    {
        await ClearAsync(ct);
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        await ExecAsync("""
            INSERT INTO config_rows (key, version, value, set_at, set_by) VALUES
              (@m, 1, @mv::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@i, 1, '1'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@s, 1, '"live"'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@l, 1, '8'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test')
            ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value;
            """, ct,
            ("m", $"screens.{Screen}.metrics"), ("mv", OneMetric),
            ("i", $"screens.{Screen}.min_inputs"),
            ("s", $"screens.{Screen}.state"),
            ("l", $"screens.{Screen}.slots"));

        foreach (var id in new[] { "S1", "S2", "S3", "S4", "S5" })
        {
            await ExecAsync("""
                INSERT INTO config_rows (key, version, value, set_at, set_by)
                VALUES (@k, 2, '"retired"'::jsonb, TIMESTAMPTZ '2020-06-01 12:00:00Z', 'test')
                ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value;
                """, ct, ("k", $"screens.{id}.state"));
        }

        // A session list, which the range run reads out of price_daily rather than off a
        // calendar: a calendar walk would produce a row of nulls on a day the exchange did
        // not trade [TradingCalendar].
        for (var day = From; day <= To; day = day.AddDays(1))
        {
            await ExecAsync("""
                INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
                VALUES (@t, @d, 100, 100, 100, 100, 100, 1000000)
                ON CONFLICT (ticker, date) DO NOTHING;
                """, ct, ("t", Name), ("d", day));

            await ExecAsync("""
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, @sec, 'mid', 1000000000, TRUE)
                ON CONFLICT (ticker, date) DO UPDATE SET is_active = TRUE;
                """, ct, ("t", Name), ("d", day), ("sec", Sector));

            await ExecAsync("""
                INSERT INTO indicator_daily (ticker, date, adx14_pctile) VALUES (@t, @d, 60)
                ON CONFLICT (ticker, date) DO UPDATE SET adx14_pctile = 60;
                """, ct, ("t", Name), ("d", day));
        }
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await ExecAsync("DELETE FROM screen_score_daily WHERE date BETWEEN @f AND @t;",
            ct, ("f", From), ("t", To));
        await ExecAsync("DELETE FROM screen_history WHERE date BETWEEN @f AND @t;",
            ct, ("f", From), ("t", To));
        await ExecAsync("DELETE FROM price_daily WHERE ticker LIKE 'SRLR.%';", ct);
        await ExecAsync("DELETE FROM indicator_daily WHERE ticker LIKE 'SRLR.%';", ct);
        await ExecAsync("DELETE FROM security_daily WHERE ticker LIKE 'SRLR.%';", ct);
        await ExecAsync("DELETE FROM config_rows WHERE key LIKE @k;", ct, ("k", $"screens.{Screen}.%"));
        await ExecAsync("DELETE FROM config_rows WHERE key LIKE 'screens.S%.state' AND version = 2;", ct);
    }

    private static async Task ExecAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
    }

    private static async Task<int> SessionCountAsync(CancellationToken ct)
        => (int) await ScalarAsync(
            "SELECT count(DISTINCT date)::bigint FROM price_daily WHERE date BETWEEN @f AND @t;", ct);

    private static async Task<int> ScoredDatesAsync(CancellationToken ct)
        => (int) await ScalarAsync(
            "SELECT count(DISTINCT date)::bigint FROM screen_score_daily WHERE date BETWEEN @f AND @t;", ct);

    private static async Task<int> RankedRowsAsync(CancellationToken ct)
        => (int) await ScalarAsync(
            "SELECT count(*)::bigint FROM screen_score_daily " +
            "WHERE date BETWEEN @f AND @t AND rank_within_screen IS NOT NULL;", ct);

    private static async Task<int> HistoryRowsAsync(CancellationToken ct)
        => (int) await ScalarAsync(
            "SELECT count(*)::bigint FROM screen_history WHERE date BETWEEN @f AND @t;", ct);

    private static async Task<long> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("f", From);
        cmd.Parameters.AddWithValue("t", To);

        return (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    private static async Task<DateOnly> OneScoredDateAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT max(date) FROM screen_score_daily WHERE date BETWEEN @f AND @t;", conn);

        cmd.Parameters.AddWithValue("f", From);
        cmd.Parameters.AddWithValue("t", To);

        return DateOnly.FromDateTime(
            (DateTime) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!);
    }

    /// <summary>
    /// Every score in the range as one ordered string, so "reproduces byte-identically" is
    /// a comparison of the whole table rather than of a count.
    /// </summary>
    private static async Task<string> ScoreDigestAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT string_agg(x, '|' ORDER BY x COLLATE \"C\") FROM (" +
            "  SELECT date::text || ' ' || screen_id || ' ' || ticker || ' ' || " +
            "         coalesce(score::text, 'null') || ' ' || coalesce(rank_within_screen::text, 'null') AS x" +
            "  FROM screen_score_daily WHERE date BETWEEN @f AND @t) s;", conn);

        cmd.Parameters.AddWithValue("f", From);
        cmd.Parameters.AddWithValue("t", To);

        return (string) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }
}
