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
/// Checkpoint 4.7. S5, the gated screen: its two composites, the three stabilisation
/// conditions, and the two news conditions failing open below the article threshold.
///
/// **A gated name carries null and not a low score.** The screen has no opinion about a
/// name it does not rank, and a low score would put that name into the population its
/// own floor is the 98th percentile of and pull the floor down. Every assertion here
/// distinguishes null from low rather than checking that a name failed to be a candidate.
///
/// **The fail-open rule is the one thing here that must not be tidied into a fail-closed
/// one** [`ARCHITECTURE.html` §05]. Thinly covered names are exactly what this screen's
/// small slots exist to find, and failing closed on them deletes them and reintroduces
/// the megacap tilt through the arithmetic rather than through a ranker.
/// </summary>
[Collection("database")]
public sealed class ScreenGateTests
{
    /// <summary>Distinct from every other Select fixture's date, so no by-date cleanup collides.</summary>
    private static readonly DateOnly RunDate = new(2021, 4, 1);

    private const string Screen = "S5";
    private const string Bucket = "srltest-gate";
    private const string Sector = "srltest-gate";

    /// <summary>The name every fixture starts from, eligible until a test spoils one input.</summary>
    private const string Name = "SRLGATE.ONE";

    /// <summary>A second name, used where a comparison between two is the assertion.</summary>
    private const string Other = "SRLGATE.TWO";

    // The worked example's own figures [`WORKED_EXAMPLE.md` §3]. Quality composite 84,
    // top quintile against a threshold of 80; technical composite 12, bottom quintile
    // against 20; article count z 0.4 against a bound of 1.0; sentiment 7-day against
    // 30-day +0.05 against a floor of 0.
    private const double QualityComposite = 84d;
    private const double TechnicalComposite = 12d;
    private const double SettledZ = 0.4d;
    private const double SettledDelta = 0.05d;

    /// <summary>A quality composite inside the top quintile but below the threshold.</summary>
    private const double QualityBelowThreshold = 79d;

    /// <summary>A technical composite above the bottom quintile, so not beaten down.</summary>
    private const double TechnicalAboveThreshold = 21d;

    // ------------------------------------------------ the headline assertion ---

    /// <summary>
    /// **S5 scores with S1 absent from the registry entirely** [D-120].
    ///
    /// This is the assertion that the metric-list copy is real rather than a reference.
    /// §05 states S5's gate as "S1 top quintile AND technical bottom quintile", which
    /// read literally is one screen reading another; if any part of that reading had
    /// survived into the code, removing S1 from config would leave S5 unable to compute
    /// its quality composite, and the name would carry null.
    ///
    /// S1's rows are deleted rather than its state set to retired, because a retired
    /// screen is still in the store and a reference to it would still resolve.
    /// </summary>
    [Fact]
    public async Task S5ScoresWithS1AbsentFromTheRegistryEntirely()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await ExecAsync("DELETE FROM config_rows WHERE key LIKE 'screens.S1.%';", ct);

            var registered = await ScreenRegistry
                .IdsAsync(new ConfigStore(TestDatabase.ConnectionString), RunDate, ct).ConfigureAwait(true);

            Assert.DoesNotContain("S1", registered);

            await RunAsync(ct);

            Assert.NotNull(await ScoreAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);

            // The seeder is the only writer of S1's rows and is idempotent, so putting
            // them back is a reseed rather than a fixture of its own.
            await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The structural half of the same rule, beside 4.3's facade test: **S5's own
    /// statement names no other screen's id**. A composite built from a reference rather
    /// than a copy would have to name the screen it referenced somewhere in the SQL.
    /// </summary>
    [Fact]
    public async Task TheStatementNamesNoOtherScreensId()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        var sql = ScreenEngine.ScoreSql(await LoadAsync(ct), RunDate, 1);

        foreach (var other in new[] { "S1", "S2", "S3", "S4" })
        {
            Assert.DoesNotContain(other, sql, StringComparison.Ordinal);
        }

        Assert.Contains("'S5'", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asking S5's facade for S1's metric list throws, which is INVARIANT 2 made
    /// unavailable rather than discouraged [D-120, D-6].
    /// </summary>
    [Fact]
    public void TheS5FacadeRefusesS1sMetricList()
    {
        var facade = new ScreenConfigFacade(Screen, new ConfigStore(TestDatabase.ConnectionString));

        Assert.False(facade.CanRead("screens.S1.metrics"));
        Assert.Throws<ForeignScreenConfigException>(() => facade.EnsureCanRead("screens.S1.metrics"));

        // And the older top-level form is not a hole in the same rule.
        Assert.False(facade.CanRead("s1.metrics"));
        Assert.True(facade.CanRead("s5.stabilisation_z_max"));
        Assert.True(facade.CanRead("screens.floor_percentile"));
    }

    // ------------------------------------------------------- the composites ---

    /// <summary>
    /// **A name outside the quality quintile carries null and not a low score.** One
    /// point below the threshold is enough, which is what makes this an assertion about
    /// the comparison rather than about a name that happens to be poor.
    /// </summary>
    [Fact]
    public async Task ANameBelowTheQualityThresholdCarriesNullAndNotALowScore()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await RunAsync(ct);
            Assert.NotNull(await ScoreAsync(Name, ct));

            await SetQualityAsync(Name, QualityBelowThreshold, ct);
            await RunAsync(ct);
            Assert.Null(await ScoreAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// A name that has not fallen far enough to be in the bottom technical quintile is
    /// not this screen's business, and carries null for the same reason.
    /// </summary>
    [Fact]
    public async Task ANameAboveTheTechnicalThresholdCarriesNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await SetTechnicalAsync(Name, TechnicalAboveThreshold, ct);
            await RunAsync(ct);

            Assert.Null(await ScoreAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A composite that cannot be computed fails the gate.** Below the composite's own
    /// minimum input count it is unknown, and unknown is not evidence that a name is a
    /// good business beaten down. The name still gets a row, being a member.
    /// </summary>
    [Fact]
    public async Task AQualityCompositeBelowItsMinimumInputCountFailsTheGate()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            // Three of seven present against a minimum of five.
            await ExecAsync(
                "UPDATE valuation_daily SET roic_4q_change_pctile = NULL, " +
                "gross_margin_4q_change_pctile = NULL, net_debt_ebitda_pctile = NULL, " +
                "accruals_pctile = NULL WHERE ticker = @t AND date = @d;",
                ct, ("t", Name), ("d", RunDate));

            await RunAsync(ct);

            Assert.Null(await ScoreAsync(Name, ct));
            Assert.True(await RowExistsAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ---------------------------------------------- the stabilisation gate ---

    /// <summary>
    /// **Below the article threshold both news conditions are treated as satisfied**
    /// [§05]. The z-score and the sentiment difference here are both far outside their
    /// bounds and the name is scored anyway, which is the fail-open rule and not a
    /// tolerance.
    /// </summary>
    [Fact]
    public async Task BelowTheArticleThresholdBothNewsConditionsPass()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await SetSentimentAsync(Name, z: 5.0, delta: -1.0, ct);
            await SetArticlesAsync(Name, 2, ct);

            await RunAsync(ct);

            Assert.NotNull(await ScoreAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A name with no `sentiment_daily` rows at all is below the threshold**, a day
    /// with no row being a day with no articles rather than a day nobody looked
    /// [`SCHEMA.md`]. This is the thinly covered small cap the rule exists for.
    /// </summary>
    [Fact]
    public async Task ANameWithNoArticleRowsAtAllFailsOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await SetSentimentAsync(Name, z: 5.0, delta: -1.0, ct);
            await ExecAsync("DELETE FROM sentiment_daily WHERE ticker = @t;", ct, ("t", Name));

            await RunAsync(ct);

            Assert.NotNull(await ScoreAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **At the threshold the conditions are evaluated**, and a news cycle still raging
    /// fails the burnout condition. Three articles rather than two is the whole
    /// difference from the test above, which is what makes this the boundary and not a
    /// second case of the same thing.
    /// </summary>
    [Fact]
    public async Task AtTheArticleThresholdABurningNewsCycleFailsTheBurnoutCondition()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await SetArticlesAsync(Name, 3, ct);
            await SetSentimentAsync(Name, z: 1.5, delta: SettledDelta, ct);

            await RunAsync(ct);
            Assert.Null(await ScoreAsync(Name, ct));

            // And exactly at the bound, which is stated as "< 1.0" and so excludes 1.0.
            await SetSentimentAsync(Name, z: 1.0, delta: SettledDelta, ct);
            await RunAsync(ct);
            Assert.Null(await ScoreAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// At the threshold, deteriorating sentiment fails the third condition. Stated as
    /// "≥ 0", so zero itself passes and anything below it does not.
    /// </summary>
    [Fact]
    public async Task AtTheArticleThresholdDeterioratingSentimentFails()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await SetArticlesAsync(Name, 3, ct);

            await SetSentimentAsync(Name, z: SettledZ, delta: -0.01, ct);
            await RunAsync(ct);
            Assert.Null(await ScoreAsync(Name, ct));

            await SetSentimentAsync(Name, z: SettledZ, delta: 0d, ct);
            await RunAsync(ct);
            Assert.NotNull(await ScoreAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **The price condition does not fail open, and that asymmetry is deliberate**
    /// [§05]. The fail-open rule is scoped to the two news conditions, and the reason
    /// given is coverage: a thinly covered name has no articles, which is ordinary, while
    /// every member has a price history by construction of the universe.
    /// </summary>
    [Fact]
    public async Task ThePriceConditionDoesNotFailOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            // Fallen and not turning: below the 20-day average with a negative slope.
            await SetPriceAsync(Name, dist20Dma: -0.04, slope: -0.01, ct);
            await RunAsync(ct);
            Assert.Null(await ScoreAsync(Name, ct));

            // Either half is enough on its own, the condition being an OR.
            await SetPriceAsync(Name, dist20Dma: 0.01, slope: -0.01, ct);
            await RunAsync(ct);
            Assert.NotNull(await ScoreAsync(Name, ct));

            await SetPriceAsync(Name, dist20Dma: -0.04, slope: 0.01, ct);
            await RunAsync(ct);
            Assert.NotNull(await ScoreAsync(Name, ct));

            // Unknown is not waved through, which is where it differs from the news half.
            await SetPriceAsync(Name, dist20Dma: null, slope: null, ct);
            await RunAsync(ct);
            Assert.Null(await ScoreAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // -------------------------------------------------------- and it ranks ---

    /// <summary>
    /// **The ranking metric is distance below the 200-day average**, read `low`, so of
    /// two eligible names the one further below its own average scores higher
    /// [`WORKED_EXAMPLE.md` §3].
    ///
    /// The percentile is what the screen ranks on rather than the raw distance, so the
    /// name further below sits at the lower percentile and direction `low` turns that
    /// into the higher score. Asserting the ordering rather than the two numbers is what
    /// makes this a statement about the direction.
    /// </summary>
    [Fact]
    public async Task TheNameFurtherBelowItsTwoHundredDayAverageScoresHigher()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await SeedEligibleNameAsync(Other, ct);

            // dist_200dma percentile 5 is further below its own average than 30 is.
            await SetRankingAsync(Name, 5, ct);
            await SetRankingAsync(Other, 30, ct);

            await RunAsync(ct);

            var deeper = (await ScoreAsync(Name, ct))!.Value;
            var shallower = (await ScoreAsync(Other, ct))!.Value;

            Assert.True(
                deeper > shallower,
                $"the name at the 5th percentile of distance below its 200-day average scored " +
                deeper.ToString("0.####", CultureInfo.InvariantCulture) +
                " and the one at the 30th scored " +
                shallower.ToString("0.####", CultureInfo.InvariantCulture) +
                ". S5 reads this metric low, so further below ranks higher [D-113].");

            // 100 minus the stored percentile, stated as a figure rather than as an
            // ordering, so a direction read the wrong way cannot pass by luck.
            Assert.Equal(95d, deeper, 4);
            Assert.Equal(70d, shallower, 4);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **The floor is drawn over the non-null population only**, so a gated name does not
    /// enter its own screen's distribution [D-115]. Two names of three are gated here and
    /// the recorded p98 is the surviving one's score, not a percentile over three slots
    /// of which two are empty.
    /// </summary>
    [Fact]
    public async Task TheFloorIsDrawnOverTheUngatedPopulationOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await SeedEligibleNameAsync(Other, ct);
            await SetRankingAsync(Name, 5, ct);
            await SetRankingAsync(Other, 30, ct);

            // The second name is gated out. If it entered the distribution as a low
            // score the p98 would sit between the two rather than on the survivor.
            await SetTechnicalAsync(Other, TechnicalAboveThreshold, ct);

            await RunAsync(ct);

            Assert.Equal(95d, (await TrailingP98Async(ct))!.Value, 4);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------- the gate as a whole ---

    /// <summary>
    /// The worked example's own trace, end to end: quality 84, technical 12, close above
    /// the 20-day average, article count z 0.4, sentiment delta +0.05. Every condition
    /// met, so the name is ranked [`WORKED_EXAMPLE.md` §3].
    /// </summary>
    [Fact]
    public async Task TheWorkedExamplesGateReproduces()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await SetArticlesAsync(Name, 4, ct);
            await SetSentimentAsync(Name, SettledZ, SettledDelta, ct);
            await SetPriceAsync(Name, dist20Dma: (42.10d - 41.60d) / 41.60d, slope: null, ct);

            await RunAsync(ct);

            Assert.NotNull(await ScoreAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **S5 is the only gated screen, and it is gated by carrying the config rows rather
    /// than by being called S5 anywhere.** A second gated screen is a set of inserts
    /// [`CLAUDE.md` §5].
    /// </summary>
    [Fact]
    public async Task S5IsTheOnlyGatedScreenAndIsGatedByItsConfig()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        var config = new ConfigStore(TestDatabase.ConnectionString);
        var gated = new List<string>();

        foreach (var id in await ScreenRegistry.IdsAsync(config, RunDate, ct).ConfigureAwait(true))
        {
            var definition = await ScreenRegistry.LoadOneAsync(config, id, RunDate, ct).ConfigureAwait(true);

            if (definition.IsGated)
            {
                gated.Add(id);
            }
        }

        Assert.Equal([Screen], gated);
    }

    /// <summary>
    /// **A screen with no gate emits the statement it emitted before gates existed, byte
    /// for byte.** Without this the gate is a branch every screen runs through and the
    /// snapshot assertions at 4.4 and 4.6 stop meaning what they say.
    /// </summary>
    [Fact]
    public async Task AnUngatedScreensStatementCarriesNoGateClause()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        var config = new ConfigStore(TestDatabase.ConnectionString);

        foreach (var id in new[] { "S1", "S2", "S3", "S4" })
        {
            var sql = ScreenEngine.ScoreSql(
                await ScreenRegistry.LoadOneAsync(config, id, RunDate, ct).ConfigureAwait(true), RunDate, 1);

            Assert.DoesNotContain("WHEN NOT (", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("articles_7d", sql, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// **Half a gate fails the stage rather than reading as no gate.** A screen that
    /// silently stopped gating because one row was missed would rank the whole universe
    /// on distance below the 200-day average, which is the falling-knife screen §05 says
    /// the gate exists to prevent, and every number it produced would look ordinary.
    /// </summary>
    [Fact]
    public async Task AHalfWrittenGateFailsRatherThanReadingAsNoGate()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

        try
        {
            await ExecAsync(
                "DELETE FROM config_rows WHERE key = 'screens.S5.technical_metrics';", ct);

            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => LoadAsync(ct));
        }
        finally
        {
            await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The seven-day article window is counted in calendar days, not sessions, and the
    /// statement says so rather than resting on a comment.
    ///
    /// **This is the opposite of the trailing floor window and right for the opposite
    /// reason.** A floor is drawn over dates a screen scored, which are sessions. News
    /// arrives on days the exchange is shut, so a seven-session window over a holiday
    /// week reaches back nine or ten days of coverage and reads a different quantity than
    /// §05's seven days. The distinction is stated here because 4.5's own window was
    /// wrong in exactly the other direction and nothing caught it.
    /// </summary>
    [Fact]
    public async Task TheArticleWindowIsSevenCalendarDaysEndingOnTheDate()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEligibleAsync(ct);

        try
        {
            await SetSentimentAsync(Name, z: 5.0, delta: -1.0, ct);

            // Three articles seven days back, which is outside a window of D-6 to D.
            await ExecAsync("DELETE FROM sentiment_daily WHERE ticker = @t;", ct, ("t", Name));
            await InsertArticlesAsync(Name, RunDate.AddDays(-7), 3, ct);

            await RunAsync(ct);
            Assert.NotNull(await ScoreAsync(Name, ct));

            // The same three one day later are inside it.
            await ExecAsync("DELETE FROM sentiment_daily WHERE ticker = @t;", ct, ("t", Name));
            await InsertArticlesAsync(Name, RunDate.AddDays(-6), 3, ct);

            await RunAsync(ct);
            Assert.Null(await ScoreAsync(Name, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // ------------------------------------------------------------- plumbing ---

    private static Task<ScreenDefinition> LoadAsync(CancellationToken ct)
        => ScreenRegistry.LoadOneAsync(
            new ConfigStore(TestDatabase.ConnectionString), Screen, RunDate, ct);

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

    /// <summary>One name that clears every condition, which each test then spoils in one place.</summary>
    private static async Task SeedEligibleAsync(CancellationToken ct)
    {
        await ClearAsync(ct);
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);
        await SeedEligibleNameAsync(Name, ct);
    }

    private static async Task SeedEligibleNameAsync(string ticker, CancellationToken ct)
    {
        await ExecAsync("""
            INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
            VALUES (@t, @d, @sec, @b, 3200000000, TRUE)
            ON CONFLICT (ticker, date) DO UPDATE SET is_active = TRUE;
            """, ct, ("t", ticker), ("d", RunDate), ("sec", Sector), ("b", Bucket));

        await SetQualityAsync(ticker, QualityComposite, ct);
        await SetTechnicalAsync(ticker, TechnicalComposite, ct);
        await SetRankingAsync(ticker, 5, ct);
        await SetPriceAsync(ticker, dist20Dma: 0.012, slope: 0.004, ct);
        await SetSentimentAsync(ticker, SettledZ, SettledDelta, ct);
        await SetArticlesAsync(ticker, 4, ct);
    }

    /// <summary>
    /// All seven quality inputs placed at the percentile their own direction turns into
    /// the composite asked for, so the composite is that figure at any weights. The three
    /// read `low` are stored at 100 minus it.
    /// </summary>
    private static async Task SetQualityAsync(string ticker, double composite, CancellationToken ct)
        => await ExecAsync("""
            INSERT INTO valuation_daily (ticker, date, fcf_yield_pctile, ev_ebit_vs_own_5y_pctile,
                roic_4q_change_pctile, gross_margin_4q_change_pctile, net_debt_ebitda_pctile,
                accruals_pctile, share_count_change_pctile)
            VALUES (@t, @d, @h, @l, @h, @h, @l, @l, @l)
            ON CONFLICT (ticker, date) DO UPDATE SET
                fcf_yield_pctile = EXCLUDED.fcf_yield_pctile,
                ev_ebit_vs_own_5y_pctile = EXCLUDED.ev_ebit_vs_own_5y_pctile,
                roic_4q_change_pctile = EXCLUDED.roic_4q_change_pctile,
                gross_margin_4q_change_pctile = EXCLUDED.gross_margin_4q_change_pctile,
                net_debt_ebitda_pctile = EXCLUDED.net_debt_ebitda_pctile,
                accruals_pctile = EXCLUDED.accruals_pctile,
                share_count_change_pctile = EXCLUDED.share_count_change_pctile;
            """, ct, ("t", ticker), ("d", RunDate),
            ("h", (float) composite), ("l", (float) (100d - composite)));

    /// <summary>
    /// The three technical inputs, every one read `high`, so the composite is the value
    /// they are all placed at. <c>dist_200dma_pctile</c> is one of them and is also the
    /// screen's ranking metric, so <see cref="SetRankingAsync"/> moves it afterwards
    /// where a test needs the two to differ.
    /// </summary>
    private static async Task SetTechnicalAsync(string ticker, double composite, CancellationToken ct)
        => await ExecAsync("""
            INSERT INTO indicator_daily (ticker, date, dist_200dma_pctile, dist_52w_high_pctile,
                rs_change_63d_pctile)
            VALUES (@t, @d, @v, @v, @v)
            ON CONFLICT (ticker, date) DO UPDATE SET
                dist_200dma_pctile = EXCLUDED.dist_200dma_pctile,
                dist_52w_high_pctile = EXCLUDED.dist_52w_high_pctile,
                rs_change_63d_pctile = EXCLUDED.rs_change_63d_pctile;
            """, ct, ("t", ticker), ("d", RunDate), ("v", (float) composite));

    /// <summary>
    /// The ranking metric alone, leaving the other two technical inputs where they are.
    ///
    /// The technical composite moves with it, which is a property of the design rather
    /// than of this fixture: S5's ranking metric is one of its three technical inputs.
    /// The values used stay inside the bottom quintile at every setting these tests use.
    /// </summary>
    private static async Task SetRankingAsync(string ticker, double percentile, CancellationToken ct)
        => await ExecAsync(
            "UPDATE indicator_daily SET dist_200dma_pctile = @v WHERE ticker = @t AND date = @d;",
            ct, ("v", (float) percentile), ("t", ticker), ("d", RunDate));

    private static async Task SetPriceAsync(
        string ticker, double? dist20Dma, double? slope, CancellationToken ct)
        => await ExecAsync(
            "UPDATE indicator_daily SET dist_20dma = @a, rs_20d_slope = @b " +
            "WHERE ticker = @t AND date = @d;",
            ct, ("a", (object?) (float?) dist20Dma ?? DBNull.Value),
            ("b", (object?) (float?) slope ?? DBNull.Value),
            ("t", ticker), ("d", RunDate));

    private static async Task SetSentimentAsync(
        string ticker, double z, double delta, CancellationToken ct)
        => await ExecAsync("""
            INSERT INTO sentiment_derived_daily (ticker, date, article_count_z_own_90d,
                sentiment_delta_7v30, sentiment_7d_level)
            VALUES (@t, @d, @z, @dl, 0)
            ON CONFLICT (ticker, date) DO UPDATE SET
                article_count_z_own_90d = EXCLUDED.article_count_z_own_90d,
                sentiment_delta_7v30 = EXCLUDED.sentiment_delta_7v30;
            """, ct, ("t", ticker), ("d", RunDate), ("z", (float) z), ("dl", (float) delta));

    /// <summary>The whole seven-day count on the run date itself, where the window is not the point.</summary>
    private static async Task SetArticlesAsync(string ticker, int articles, CancellationToken ct)
    {
        await ExecAsync("DELETE FROM sentiment_daily WHERE ticker = @t;", ct, ("t", ticker));
        await InsertArticlesAsync(ticker, RunDate, articles, ct);
    }

    private static async Task InsertArticlesAsync(
        string ticker, DateOnly date, int articles, CancellationToken ct)
        => await ExecAsync("""
            INSERT INTO sentiment_daily (ticker, date, article_count, sentiment_score)
            VALUES (@t, @d, @n, 0)
            ON CONFLICT (ticker, date) DO UPDATE SET article_count = EXCLUDED.article_count;
            """, ct, ("t", ticker), ("d", date), ("n", articles));

    /// <summary>
    /// By date rather than by screen, for the reason `ScreenEngineTests` records: a run
    /// here scores every registered screen, so a screen-scoped delete leaves the others
    /// behind and breaks an assertion in a different file.
    /// </summary>
    private static async Task ClearAsync(CancellationToken ct)
    {
        var tickers = new[] { Name, Other };

        await ExecAsync("DELETE FROM screen_score_daily WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM screen_history WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM indicator_daily WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync("DELETE FROM valuation_daily WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync("DELETE FROM sentiment_derived_daily WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync("DELETE FROM sentiment_daily WHERE ticker = ANY(@t);", ct, ("t", tickers));
        await ExecAsync("DELETE FROM security_daily WHERE ticker = ANY(@t);", ct, ("t", tickers));
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

    private static async Task<double?> ScoreAsync(string ticker, CancellationToken ct)
    {
        var value = await ScalarAsync(
            "SELECT score FROM screen_score_daily WHERE screen_id = @s AND ticker = @t AND date = @d;",
            ct, ticker);

        return value is null or DBNull ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> RowExistsAsync(string ticker, CancellationToken ct)
        => await ScalarAsync(
            "SELECT ticker FROM screen_score_daily WHERE screen_id = @s AND ticker = @t AND date = @d;",
            ct, ticker) is not null;

    private static async Task<double?> TrailingP98Async(CancellationToken ct)
    {
        var value = await ScalarAsync(
            "SELECT p98_trailing FROM screen_history WHERE screen_id = @s AND date = @d;", ct, Name);

        return value is null or DBNull ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static async Task<object?> ScalarAsync(string sql, CancellationToken ct, string ticker)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("s", Screen);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);
    }

    /// <summary>Per-file, as every other stage fixture in this suite keeps its own.</summary>
    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }
}
