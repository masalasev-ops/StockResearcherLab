using Npgsql;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Config;

/// <summary>
/// Checkpoint 4.3. Config is what a screen is, so it precedes the engine: otherwise the
/// row's shape becomes whatever the engine happened to need, and
/// <c>screens.&lt;id&gt;.metrics</c> is the one key in this system whose shape is a
/// design decision rather than a value [<c>CLAUDE.md</c> section 5, <c>METRICS.md</c>
/// section 8].
/// </summary>
public sealed class ScreenMetricListTests
{
    private static ConfigRow Row(string value, string key = "screens.S1.metrics")
        => new(key, 1, value, new DateOnly(2021, 1, 4));

    [Fact]
    public void ADirectionAndAWeightAreRead()
    {
        var metrics = ScreenMetricList.Parse(Row(
            """[{"metric":"fcf_yield","direction":"high","weight":2}]"""));

        var one = Assert.Single(metrics);

        Assert.Equal("fcf_yield", one.Metric);
        Assert.Equal(MetricDirection.High, one.Direction);
        Assert.Equal(2d, one.Weight);
        Assert.False(one.IsBonus);
    }

    /// <summary>
    /// Weights are carried per metric and default to 1. The shape has to carry them
    /// even where every seeded value is one, because adding the field later is a config
    /// migration across every screen row [D-112].
    /// </summary>
    [Fact]
    public void AnAbsentWeightDefaultsToOne()
    {
        var metrics = ScreenMetricList.Parse(Row("""[{"metric":"roic_4q_change","direction":"high"}]"""));

        Assert.Equal(1d, Assert.Single(metrics).Weight);
    }

    /// <summary>
    /// **Fails the stage closed rather than defaulting to high.** Defaulting would
    /// invert three of S1's seven inputs and produce an entirely plausible score
    /// [D-113].
    /// </summary>
    [Fact]
    public void AnUnrecognisedDirectionFailsClosed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ScreenMetricList.Parse(Row(
            """[{"metric":"fcf_yield","direction":"ascending"}]""")));

        Assert.Contains("'high' and 'low'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDirectionFailsClosed()
        => Assert.Throws<InvalidOperationException>(() => ScreenMetricList.Parse(Row(
            """[{"metric":"fcf_yield"}]""")));

    [Fact]
    public void ABonusCarriesItsPointsAndNoDirection()
    {
        var metrics = ScreenMetricList.Parse(Row(
            """[{"metric":"base_breakout_flag","kind":"bonus","points":10}]"""));

        var one = Assert.Single(metrics);

        Assert.True(one.IsBonus);
        Assert.Equal(10d, one.BonusPoints);
    }

    [Fact]
    public void ABonusWithoutPointsFailsClosed()
        => Assert.Throws<InvalidOperationException>(() => ScreenMetricList.Parse(Row(
            """[{"metric":"base_breakout_flag","kind":"bonus"}]""")));

    [Fact]
    public void AnUnrecognisedKindFailsClosed()
        => Assert.Throws<InvalidOperationException>(() => ScreenMetricList.Parse(Row(
            """[{"metric":"base_breakout_flag","kind":"penalty","points":10}]""")));

    /// <summary>
    /// A weight at or below zero either removes the metric from the list while leaving
    /// it visible there, or inverts it behind the direction's back.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveWeightFailsClosed(int weight)
        => Assert.Throws<InvalidOperationException>(() => ScreenMetricList.Parse(Row(
            $$"""[{"metric":"fcf_yield","direction":"high","weight":{{weight}}}]""")));

    [Fact]
    public void AnEmptyListFailsClosed()
        => Assert.Throws<InvalidOperationException>(() => ScreenMetricList.Parse(Row("[]")));

    [Fact]
    public void ANonArrayFailsClosed()
        => Assert.Throws<InvalidOperationException>(() => ScreenMetricList.Parse(Row("""{"metric":"x"}""")));

    /// <summary>The two halves are separated by the record rather than by every reader.</summary>
    [Fact]
    public void RankedAndBonusMetricsAreSeparated()
    {
        var metrics = ScreenMetricList.Parse(Row(
            """
            [{"metric":"adx14","direction":"high"},
             {"metric":"base_breakout_flag","kind":"bonus","points":10}]
            """));

        var definition = new ScreenDefinition("S2", metrics, 1, ScreenState.Live, 8);

        Assert.Equal("adx14", Assert.Single(definition.RankedMetrics).Metric);
        Assert.Equal("base_breakout_flag", Assert.Single(definition.Bonuses).Metric);
    }
}

/// <summary>
/// The per-screen facade, which is where INVARIANT 2 stops being a rule someone
/// remembers. It is <c>DeclaredAccess</c>'s idiom applied to configuration: the
/// shortcut is unavailable rather than discouraged [D-120].
/// </summary>
public sealed class ScreenConfigFacadeTests
{
    private sealed class UnusedStore : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => throw new InvalidOperationException("The facade should have refused before reaching the store.");

        public Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => throw new InvalidOperationException("The facade should have refused before reaching the store.");

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static ScreenConfigFacade For(string screenId) => new(screenId, new UnusedStore());

    /// <summary>D-120's own test: asking the S5 facade for S1's metric list throws.</summary>
    [Fact]
    public void AskingForAnotherScreensKeyThrows()
    {
        var ex = Assert.Throws<ForeignScreenConfigException>(
            () => For("S5").EnsureCanRead("screens.S1.metrics"));

        Assert.Equal("S5", ex.ScreenId);
        Assert.Equal("screens.S1.metrics", ex.Key);
    }

    [Fact]
    public void AScreenReadsItsOwnKeys()
    {
        var facade = For("S5");

        Assert.True(facade.CanRead("screens.S5.metrics"));
        Assert.True(facade.CanRead("screens.S5.quality_metrics"));
        Assert.True(facade.CanRead("screens.S5.technical_metrics"));
    }

    /// <summary>
    /// The older top-level form predates the <c>screens.</c> namespace and is as much
    /// S5's as the newer one. A guard that only knew the newer prefix would be a hole.
    /// </summary>
    [Fact]
    public void TheOlderTopLevelFormIsScopedToo()
    {
        Assert.True(For("S5").CanRead("s5.stabilisation_z_max"));
        Assert.False(For("S1").CanRead("s5.stabilisation_z_max"));
    }

    /// <summary>
    /// Shared keys are reachable by every screen. <c>screens.floor_percentile</c> is
    /// one rule for all screens rather than one screen's property.
    /// </summary>
    [Theory]
    [InlineData("screens.floor_percentile")]
    [InlineData("screens.floor_lookback_days")]
    [InlineData("gates.gap_pct")]
    [InlineData("tuner.slot_cap")]
    public void SharedKeysAreReachable(string key)
        => Assert.True(For("S1").CanRead(key));

    /// <summary>The facade refuses before the store is reached, not after.</summary>
    [Fact]
    public async Task RequireRefusesBeforeReachingTheStore()
        => await Assert.ThrowsAsync<ForeignScreenConfigException>(
            async () => await For("S5").RequireAsync(
                "screens.S1.metrics", new DateOnly(2021, 1, 4), TestContext.Current.CancellationToken));

    /// <summary>A call site builds its own key rather than typing another screen's.</summary>
    [Fact]
    public void OwnBuildsThisScreensKey()
        => Assert.Equal("screens.S3.metrics", For("S3").Own("metrics"));
}

/// <summary>
/// The seeded rows against the real store: every screen key resolves at the window
/// start and at the frontier, and a later version does not change what a 2021 date
/// resolves to [INVARIANT 13].
/// </summary>
[Collection("database")]
public sealed class SeededScreenConfigTests
{
    private static readonly DateOnly WindowStart = new(2021, 1, 4);
    private static readonly DateOnly Frontier = new(2026, 8, 12);

    private static ConfigStore Store() => new(TestDatabase.ConnectionString);

    private static async Task SeedAsync(CancellationToken ct)
        => await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);

    [Fact]
    public async Task EveryScreenAndGateKeyResolvesAtBothEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var store = Store();

        var keys = ConfigSeeder.Keys
            .Select(k => k.Key)
            .Where(k => k.StartsWith("screens.", StringComparison.Ordinal)
                        || k.StartsWith("gates.", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(keys);

        foreach (var key in keys)
        {
            Assert.NotNull(await store.ResolveAsync(key, WindowStart, ct).ConfigureAwait(true));
            Assert.NotNull(await store.ResolveAsync(key, Frontier, ct).ConfigureAwait(true));
        }
    }

    /// <summary>
    /// **The registered screens are S1 to S5 live and three shadows, and the split by
    /// state is the assertion** [D-129]. It read "exactly S1 to S5, and no family member
    /// is registered" until Q.7, on D-119's deferral. D-129 takes that decision for the
    /// three backfillable members and takes it before 4.14, because a screen registered
    /// after the attribution write can never carry a backfilled row.
    ///
    /// **X-PEAD is asserted absent rather than left unmentioned.** D-90 is `OPEN` and its
    /// input has no backfillable history, so its absence is a fork nobody has taken and
    /// not an oversight in this list [`SCREEN_LIFECYCLE.md` §7.5].
    /// </summary>
    [Fact]
    public async Task TheRegisteredScreensAreFiveLiveAndThreeShadows()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var config = Store();
        var registered = await ScreenRegistry.LoadAsync(config, WindowStart, ct).ConfigureAwait(true);

        Assert.Equal(
            ["S1", "S2", "S3", "S4", "S5"],
            registered.Where(s => s.State == ScreenState.Live)
                .Select(s => s.ScreenId).OrderBy(id => id, StringComparer.Ordinal));

        Assert.Equal(
            ["X-ACC", "X-FM", "X-NSI"],
            registered.Where(s => s.State == ScreenState.Shadow)
                .Select(s => s.ScreenId).OrderBy(id => id, StringComparer.Ordinal));

        Assert.DoesNotContain("X-PEAD", registered.Select(s => s.ScreenId));

        // Retired screens are not scored at all, so an id in this state would silently
        // leave the score table. None exists yet and the assertion says so.
        Assert.DoesNotContain(registered, s => s.State == ScreenState.Retired);

        // The shadows are registered as of the window start and not only as of today,
        // which is what lets pass one score them across the whole backfill range. A
        // set_at later than the range would register them for no historical date and the
        // range run would write nothing for them while reporting success.
        Assert.Equal(8, registered.Count);
    }

    /// <summary>
    /// **Each shadow ranks on one `valuation_daily` column that C09 already writes and
    /// C11 already percentiles**, which is why the whole cost of the family is these
    /// config rows [`SCREEN_LIFECYCLE.md` §7.4]. Asserted because a shadow ranking on a
    /// column nothing percentiles scores null everywhere and reads as a screen that
    /// found nothing.
    /// </summary>
    [Theory]
    [InlineData("X-NSI", "share_count_change", MetricDirection.Low)]
    [InlineData("X-ACC", "accruals", MetricDirection.Low)]
    [InlineData("X-FM", "revenue_growth_4q_trend", MetricDirection.High)]
    public async Task EachShadowRanksOnOnePercentiledColumn(
        string screenId, string metric, MetricDirection direction)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var definition = await ScreenRegistry
            .LoadOneAsync(Store(), screenId, WindowStart, ct).ConfigureAwait(true);

        var only = Assert.Single(definition.Metrics);

        Assert.Equal(metric, only.Metric);
        Assert.Equal(direction, only.Direction);
        Assert.Equal(1, definition.MinInputs);
        Assert.Empty(definition.Bonuses);
        Assert.Null(definition.Eligibility);

        Assert.Contains(metric, PercentileEngine.Sources.SelectMany(s => s.Metrics));
    }

    /// <summary>The three _inv names in section 05 are seeded as direction low [D-113].</summary>
    [Fact]
    public async Task TheThreeInvertedMetricsAreSeededLow()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var row = await Store().RequireAsync("screens.S1.metrics", WindowStart, ct).ConfigureAwait(true);
        var metrics = ScreenMetricList.Parse(row).ToDictionary(m => m.Metric, StringComparer.Ordinal);

        foreach (var name in new[] { "net_debt_ebitda", "accruals", "share_count_change" })
        {
            Assert.Equal(MetricDirection.Low, metrics[name].Direction);
        }

        Assert.Equal(MetricDirection.High, metrics["fcf_yield"].Direction);
    }

    /// <summary>
    /// S4 carries two metrics and <c>inst_ownership_change</c> appears in no screen's
    /// list, while the column is still written and still percentiled [D-118].
    /// </summary>
    [Fact]
    public async Task InstitutionalOwnershipChangeIsInNoScreensList()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        foreach (var (key, _) in ConfigSeeder.Keys.Where(k => k.Key.EndsWith("metrics", StringComparison.Ordinal)))
        {
            var row = await Store().RequireAsync(key, WindowStart, ct).ConfigureAwait(true);

            Assert.DoesNotContain(
                ScreenMetricList.Parse(row),
                m => string.Equals(m.Metric, "inst_ownership_change", StringComparison.Ordinal));
        }

        var s4 = await Store().RequireAsync("screens.S4.metrics", WindowStart, ct).ConfigureAwait(true);
        Assert.Equal(2, ScreenMetricList.Parse(s4).Count);
    }

    /// <summary>
    /// **A version 2 seeded with a later <c>set_at</c> does not change what a 2021 date
    /// resolves to** [INVARIANT 13]. A screen definition resolved as of today against a
    /// 2022 date scores that date under definitions it was not scored under, and looks
    /// right doing it.
    /// </summary>
    [Fact]
    public async Task ALaterVersionDoesNotChangeWhatAPastDateResolvesTo()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var store = Store();
        var before = await store.RequireAsync("screens.S3.min_inputs", WindowStart, ct).ConfigureAwait(true);

        await using (var conn = new NpgsqlConnection(TestDatabase.ConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(true);
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO config_rows (key, version, value, set_at, set_by)
                VALUES ('screens.S3.min_inputs', 2, '2'::jsonb, TIMESTAMPTZ '2026-01-02 00:00:00Z', 'test')
                ON CONFLICT (key, version) DO NOTHING;
                """, conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
        }

        try
        {
            var past = await store.RequireAsync("screens.S3.min_inputs", WindowStart, ct).ConfigureAwait(true);
            var now = await store.RequireAsync("screens.S3.min_inputs", Frontier, ct).ConfigureAwait(true);

            Assert.Equal(before.Value, past.Value);
            Assert.Equal(1, past.Version);
            Assert.Equal(2, now.Version);
        }
        finally
        {
            await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(true);
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM config_rows WHERE key = 'screens.S3.min_inputs' AND version = 2;", conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>Running the seeder twice inserts nothing the second time.</summary>
    [Fact]
    public async Task TheSeederIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        Assert.Equal(0, await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true));
    }

    /// <summary>The three retired quota keys are absent from the seeder [D-116].</summary>
    [Theory]
    [InlineData("screens.quota_large")]
    [InlineData("screens.quota_mid")]
    [InlineData("screens.quota_small")]
    public void TheRetiredQuotaKeysAreNotSeeded(string key)
        => Assert.DoesNotContain(ConfigSeeder.Keys, k => string.Equals(k.Key, key, StringComparison.Ordinal));
}
