using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// Checkpoint 4.4. C13 scores every active member on the percentile store alone.
///
/// **The reference arithmetic is written out in the test rather than reimplemented.**
/// A test that recomputed the score with the same expression the stage uses would agree
/// with itself whatever either did. The figures below are hand-computed and stated as
/// constants [D-112].
/// </summary>
[Collection("database")]
public sealed class ScreenEngineTests
{
    private const string ScreenId = "SRLTESTQ";
    private const string Bucket = "srltest-screen";
    private const string Sector = "srltest-sector";

    private static readonly DateOnly RunDate = new(2021, 2, 1);

    /// <summary>Full: 80 high, 30 low so 70 adjusted, 60 high. Weights 1, 2, 1.</summary>
    private const string Full = "SRLTEST.FULL";

    /// <summary>One input absent, so it scores over the two it has.</summary>
    private const string Partial = "SRLTEST.PART";

    /// <summary>One input present against a minimum of two, so its score is null.</summary>
    private const string Thin = "SRLTEST.THIN";

    /// <summary>No row in any metric store, and still a member, so still a row.</summary>
    private const string Bare = "SRLTEST.BARE";

    // (1*80 + 2*70 + 1*60) / (1+2+1) = 280 / 4
    private const double FullScore = 70d;

    // (2*70 + 1*60) / (2+1) = 200 / 3. Scored over what is present, not over four
    // slots of which one is empty, which would give (0+140+60)/4 = 50.
    private const double PartialScore = 200d / 3d;

    private const double PartialIfNullScoredZero = 50d;

    private static string MetricsJson(bool withBonus) =>
        """
        [{"metric":"fcf_yield","direction":"high","weight":1},
         {"metric":"net_debt_ebitda","direction":"low","weight":2},
         {"metric":"adx14","direction":"high","weight":1}
        """
        + (withBonus ? """,{"metric":"base_breakout_flag","kind":"bonus","points":10}]""" : "]");

    // ------------------------------------------------------------------ the runs ---

    [Fact]
    public async Task TheHandComputedReferenceReproducesExactly()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedFixtureAsync(withBonus: false, ct);

        try
        {
            await RunAsync(ct);

            Assert.Equal(FullScore, (await ScoreAsync(Full, ct))!.Value, 4);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A name with one null input scores over its present inputs and is not scored as
    /// if at the bottom** [D-112, `CLAUDE.md` §6]. The figure a sum would give is
    /// asserted to differ, so the test fails if the arithmetic ever becomes one.
    /// </summary>
    [Fact]
    public async Task ANullInputScoresOverThePresentOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedFixtureAsync(withBonus: false, ct);

        try
        {
            await RunAsync(ct);

            var score = await ScoreAsync(Partial, ct);

            Assert.Equal(PartialScore, score!.Value, 4);
            Assert.NotEqual(PartialIfNullScoredZero, score.Value, 4);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    [Fact]
    public async Task ANameBelowMinInputsCarriesNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedFixtureAsync(withBonus: false, ct);

        try
        {
            await RunAsync(ct);

            Assert.Null(await ScoreAsync(Thin, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **The row count equals the membership count exactly**, which is INVARIANT 1
    /// checked mechanically rather than argued. A member with no row in any metric store
    /// still gets a row, with a null score, because it is a member. A screen that scored
    /// a shortlist would draw its floor over a population ranking had already narrowed.
    /// </summary>
    [Fact]
    public async Task TheRowCountEqualsTheMembershipCountExactly()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedFixtureAsync(withBonus: false, ct);

        try
        {
            await RunAsync(ct);

            Assert.Equal(await MemberCountAsync(ct), await ScoredCountAsync(ct));
            Assert.NotNull(await RowExistsAsync(Bare, ct));
            Assert.Null(await ScoreAsync(Bare, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>Floors and ranks are 4.5's, so nothing is ranked here [D-115].</summary>
    [Fact]
    public async Task NothingIsRankedYet()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedFixtureAsync(withBonus: false, ct);

        try
        {
            await RunAsync(ct);

            Assert.Equal(0L, await ScalarAsync(
                "SELECT count(*) FROM screen_score_daily WHERE screen_id = @s AND rank_within_screen IS NOT NULL;",
                ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>Two runs over one date are byte-identical [`CLAUDE.md` §6].</summary>
    [Fact]
    public async Task TwoRunsOverOneDateAreIdentical()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedFixtureAsync(withBonus: false, ct);

        try
        {
            await RunAsync(ct);
            var first = await DigestAsync(ct);

            await RunAsync(ct);
            var second = await DigestAsync(ct);

            Assert.Equal(first, second);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// The bonus is applied outside the mean, at true, false and null, and **null scores
    /// identically to false** [D-114].
    /// </summary>
    [Fact]
    public async Task TheBonusIsAppliedOutsideTheMeanAndNullMatchesFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedFixtureAsync(withBonus: true, ct);

        try
        {
            await SetFlagAsync(Full, true, ct);
            await RunAsync(ct);
            Assert.Equal(FullScore + 10d, (await ScoreAsync(Full, ct))!.Value, 4);

            await SetFlagAsync(Full, false, ct);
            await RunAsync(ct);
            var atFalse = (await ScoreAsync(Full, ct))!.Value;
            Assert.Equal(FullScore, atFalse, 4);

            await SetFlagAsync(Full, null, ct);
            await RunAsync(ct);
            Assert.Equal(atFalse, (await ScoreAsync(Full, ct))!.Value, 4);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------------- the statement itself ---

    /// <summary>
    /// Direction <c>low</c> is applied as 100 minus the stored percentile, which is
    /// D-113's own example: <c>net_debt_ebitda</c> at the 95th contributes 5 and not 95.
    /// </summary>
    [Fact]
    public void DirectionLowIsAppliedAsOneHundredMinusThePercentile()
    {
        var sql = ScreenEngine.ScoreSql(
            new ScreenDefinition(
                "SX",
                [ScreenMetric.Ranked("net_debt_ebitda", MetricDirection.Low)],
                1, ScreenState.Live, 8),
            RunDate, 1);

        Assert.Contains("(100.0 - val.net_debt_ebitda_pctile)", sql, StringComparison.Ordinal);
    }

    /// <summary>The statement names one screen's metrics and reaches no other screen [INVARIANT 2].</summary>
    [Fact]
    public void TheStatementNamesNoOtherScreen()
    {
        var sql = ScreenEngine.ScoreSql(
            new ScreenDefinition(
                "S5",
                [ScreenMetric.Ranked("dist_200dma", MetricDirection.Low)],
                1, ScreenState.Live, 8),
            RunDate, 1);

        foreach (var other in new[] { "'S1'", "'S2'", "'S3'", "'S4'" })
        {
            Assert.DoesNotContain(other, sql, StringComparison.Ordinal);
        }
    }

    /// <summary>A metric nothing percentiles fails the stage rather than scoring null everywhere.</summary>
    [Fact]
    public void AMetricOnNoPercentileSourceFailsClosed()
        => Assert.Throws<InvalidOperationException>(() => ScreenEngine.ScoreSql(
            new ScreenDefinition(
                "SX",
                [ScreenMetric.Ranked("not_a_metric", MetricDirection.High)],
                1, ScreenState.Live, 8),
            RunDate, 1));

    /// <summary>Two builds of one definition emit identical SQL, which is what makes the run reproducible.</summary>
    [Fact]
    public void TheStatementIsByteIdenticalAcrossBuilds()
    {
        var definition = new ScreenDefinition(
            "SX",
            [
                ScreenMetric.Ranked("fcf_yield", MetricDirection.High),
                ScreenMetric.Ranked("adx14", MetricDirection.High, 2),
                ScreenMetric.Bonus("base_breakout_flag", 10),
            ],
            1, ScreenState.Live, 8);

        Assert.Equal(
            ScreenEngine.ScoreSql(definition, RunDate, 1),
            ScreenEngine.ScoreSql(definition, RunDate, 1));
    }

    // ------------------------------------------------------------------ plumbing ---

    private static async Task RunAsync(CancellationToken ct)
    {
        var stage = new ScreenEngine();

        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(RunDate),
            new ConfigStore(TestDatabase.ConnectionString));

        await stage.ExecuteAsync(context, ct).ConfigureAwait(true);
    }

    private static async Task SeedFixtureAsync(bool withBonus, CancellationToken ct)
    {
        await ClearAsync(ct);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        // The fabricated screen, seeded as config. Nothing in code knows this screen
        // exists, which is the property 4.6 turns into its own assertion.
        await ExecAsync(conn, """
            INSERT INTO config_rows (key, version, value, set_at, set_by) VALUES
              (@m, 1, @mv::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@i, 1, '2'::jsonb,  TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@s, 1, '"live"'::jsonb, TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test'),
              (@l, 1, '8'::jsonb,  TIMESTAMPTZ '2020-01-01 12:00:00Z', 'test')
            ON CONFLICT (key, version) DO UPDATE SET value = EXCLUDED.value;
            """, ct,
            ("m", $"screens.{ScreenId}.metrics"), ("mv", MetricsJson(withBonus)),
            ("i", $"screens.{ScreenId}.min_inputs"),
            ("s", $"screens.{ScreenId}.state"),
            ("l", $"screens.{ScreenId}.slots"));

        foreach (var ticker in new[] { Full, Partial, Thin, Bare })
        {
            await ExecAsync(conn, """
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, @sec, @b, 1000000000, TRUE)
                ON CONFLICT (ticker, date) DO UPDATE SET is_active = TRUE;
                """, ct, ("t", ticker), ("d", RunDate), ("sec", Sector), ("b", Bucket));
        }

        // Percentiles as they would already stand, this stage reading them rather than
        // computing them: the researcher never sees a raw series and neither does a
        // screen [D-16 in spirit, METRICS.md section 6].
        await InsertValuationAsync(conn, Full, 80, 30, ct);
        await InsertIndicatorAsync(conn, Full, 60, ct);

        await InsertValuationAsync(conn, Partial, null, 30, ct);
        await InsertIndicatorAsync(conn, Partial, 60, ct);

        await InsertValuationAsync(conn, Thin, null, null, ct);
        await InsertIndicatorAsync(conn, Thin, 60, ct);
    }

    private static async Task InsertValuationAsync(
        NpgsqlConnection conn, string ticker, double? fcf, double? netDebt, CancellationToken ct)
        => await ExecAsync(conn, """
            INSERT INTO valuation_daily (ticker, date, fcf_yield_pctile, net_debt_ebitda_pctile)
            VALUES (@t, @d, @f, @n)
            ON CONFLICT (ticker, date) DO UPDATE SET
                fcf_yield_pctile = EXCLUDED.fcf_yield_pctile,
                net_debt_ebitda_pctile = EXCLUDED.net_debt_ebitda_pctile;
            """, ct, ("t", ticker), ("d", RunDate),
            ("f", (object?) (float?) fcf ?? DBNull.Value),
            ("n", (object?) (float?) netDebt ?? DBNull.Value));

    private static async Task InsertIndicatorAsync(
        NpgsqlConnection conn, string ticker, double? adx, CancellationToken ct)
        => await ExecAsync(conn, """
            INSERT INTO indicator_daily (ticker, date, adx14_pctile)
            VALUES (@t, @d, @a)
            ON CONFLICT (ticker, date) DO UPDATE SET adx14_pctile = EXCLUDED.adx14_pctile;
            """, ct, ("t", ticker), ("d", RunDate),
            ("a", (object?) (float?) adx ?? DBNull.Value));

    private static async Task SetFlagAsync(string ticker, bool? value, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        await ExecAsync(conn,
            "UPDATE indicator_daily SET base_breakout_flag = @v WHERE ticker = @t AND date = @d;",
            ct, ("v", (object?) value ?? DBNull.Value), ("t", ticker), ("d", RunDate));
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        var tickers = new[] { Full, Partial, Thin, Bare };

        // **By date rather than by screen id.** The stage scores every registered screen,
        // so a run here writes S1 to S5 as well as the fabricated one wherever the suite
        // has seeded them. Deleting only this fixture's screen left those behind and
        // broke 4.1's zero-row assertion from a different test file, which is exactly the
        // kind of cross-test leak a screen-scoped delete hides.
        await ExecAsync(conn, "DELETE FROM screen_score_daily WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync(conn, "DELETE FROM valuation_daily WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync(conn, "DELETE FROM indicator_daily WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync(conn, "DELETE FROM security_daily WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync(conn, "DELETE FROM config_rows WHERE key LIKE @k;", ct, ("k", $"screens.{ScreenId}.%"));
    }

    private static async Task ExecAsync(
        NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);

        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
    }

    private static async Task<double?> ScoreAsync(string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT score FROM screen_score_daily WHERE screen_id = @s AND ticker = @t AND date = @d;", conn);

        cmd.Parameters.AddWithValue("s", ScreenId);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        return value is null or DBNull ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> RowExistsAsync(string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT ticker FROM screen_score_daily WHERE screen_id = @s AND ticker = @t AND date = @d;", conn);

        cmd.Parameters.AddWithValue("s", ScreenId);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        return (string?) await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);
    }

    private static async Task<long> ScoredCountAsync(CancellationToken ct)
        => await ScalarAsync(
            "SELECT count(*) FROM screen_score_daily WHERE screen_id = @s AND date = @d;", ct);

    private static async Task<long> MemberCountAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM ({Universe.MembersAsOf(RunDate).TrimEnd(';')}) x;", conn);

        return (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    private static async Task<long> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("s", ScreenId);
        cmd.Parameters.AddWithValue("d", RunDate);

        return (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    /// <summary>Every scored row for this screen, ordered, as one string.</summary>
    private static async Task<string> DigestAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT string_agg(ticker || '=' || coalesce(score::text, 'null') || '/' || config_version,
                              ',' ORDER BY ticker)
            FROM screen_score_daily WHERE screen_id = @s AND date = @d;
            """, conn);

        cmd.Parameters.AddWithValue("s", ScreenId);
        cmd.Parameters.AddWithValue("d", RunDate);

        return (string?) await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true) ?? string.Empty;
    }

    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }
}
