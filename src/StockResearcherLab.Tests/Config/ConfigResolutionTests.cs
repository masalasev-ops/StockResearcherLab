using StockResearcherLab.Core.Config;
using StockResearcherLab.Data;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Config;

/// <summary>
/// INVARIANT 13, checked mechanically. Config resolves as of the simulated date,
/// never as of now [D-43].
///
/// The tuner rewrites slot allocations monthly, so a resolver that answers with
/// today's configuration for a historical date makes every analysis over history
/// answer a different question than the one asked, and it does that without
/// producing an error.
/// </summary>
public sealed class ConfigResolutionTests
{
    private const string Key = "screens.s1.slots";

    // Two versions of one key. The gap between them is where the interesting
    // question lives: a date after the first and before the second.
    private static readonly ConfigRow[] TwoVersions =
    [
        new(Key, 1, "8", new DateOnly(2024, 1, 1)),
        new(Key, 2, "12", new DateOnly(2025, 6, 1)),
    ];

    [Fact]
    public void ADateBetweenTwoVersionsResolvesToTheOlder()
    {
        var row = ConfigResolution.Resolve(TwoVersions, Key, new DateOnly(2025, 1, 15));

        Assert.NotNull(row);
        Assert.Equal(1, row!.Version);
        Assert.Equal("8", row.Value);
    }

    [Fact]
    public void ADateAfterBothVersionsResolvesToTheNewer()
    {
        var row = ConfigResolution.Resolve(TwoVersions, Key, new DateOnly(2026, 8, 7));

        Assert.NotNull(row);
        Assert.Equal(2, row!.Version);
        Assert.Equal("12", row.Value);
    }

    /// <summary>
    /// The case that catches a resolver reading MAX(version) unconditionally [A9].
    ///
    /// Such a resolver passes both tests above and fails only here, by handing a
    /// 2023 date the configuration that came into force in 2025. That is lookahead
    /// arriving through the component written to prevent it, and it produces no
    /// error at any point.
    /// </summary>
    [Fact]
    public void ADateBeforeEveryVersionResolvesToNothingRatherThanTheNewest()
    {
        var row = ConfigResolution.Resolve(TwoVersions, Key, new DateOnly(2023, 12, 31));

        Assert.Null(row);
    }

    [Fact]
    public void TheBoundaryDateItselfIsInForce()
    {
        // "At or before", not "before". A version set on a date governs that date,
        // which is the reading that makes a seeded row cover the first day of the
        // backfill window rather than missing it by one.
        var row = ConfigResolution.Resolve(TwoVersions, Key, new DateOnly(2024, 1, 1));

        Assert.NotNull(row);
        Assert.Equal(1, row!.Version);
    }

    [Fact]
    public void VersionRatherThanRowOrderDecidesTheWinner()
    {
        // Deliberately out of order. Enumeration order is unspecified for most
        // sources and must never decide an output [CLAUDE.md section 6], so the
        // rule compares versions explicitly.
        ConfigRow[] shuffled =
        [
            new(Key, 2, "12", new DateOnly(2024, 6, 1)),
            new(Key, 3, "4", new DateOnly(2024, 3, 1)),
            new(Key, 1, "8", new DateOnly(2024, 1, 1)),
        ];

        var row = ConfigResolution.Resolve(shuffled, Key, new DateOnly(2024, 12, 31));

        Assert.Equal(3, row!.Version);
    }

    [Fact]
    public void AnotherKeysRowsAreNotConsidered()
    {
        ConfigRow[] mixed =
        [
            new("universe.min_price", 9, "999", new DateOnly(2020, 1, 1)),
            new(Key, 1, "8", new DateOnly(2024, 1, 1)),
        ];

        var row = ConfigResolution.Resolve(mixed, Key, new DateOnly(2026, 1, 1));

        Assert.Equal("8", row!.Value);
    }

    // ------------------------------ the store-wide version, checkpoint 1.13 ---
    //
    // `Worker` carried `const int configVersion = 1` at both call sites until this
    // pass, which the checkpoint said would go. Nothing read it in phase 1, so
    // nothing failed; phase 4 stamps it onto every attribution row, and a literal 1
    // would have said every night ran under the same configuration whatever the
    // tuner had done, which is the segmentation CLAUDE.md section 8 exists to keep.

    /// <summary>
    /// Three keys. `screens.s1.slots` has reached version 3 and the other two are
    /// still at version 1, which is the 3, 1, 1 shape D-72 names.
    /// </summary>
    private static readonly ConfigRow[] ThreeOneOne =
    [
        new(Key, 1, "8", new DateOnly(2020, 1, 1)),
        new(Key, 2, "10", new DateOnly(2021, 1, 1)),
        new(Key, 3, "12", new DateOnly(2022, 1, 1)),
        new("universe.min_price", 1, "5", new DateOnly(2020, 1, 1)),
        new("tuner.slot_floor", 1, "4", new DateOnly(2020, 1, 1)),
    ];

    /// <summary>The same store after `tuner.slot_floor` moves to version 2, so 3, 2, 1.</summary>
    private static readonly ConfigRow[] ThreeTwoOne =
    [
        .. ThreeOneOne,
        new("tuner.slot_floor", 2, "5", new DateOnly(2023, 6, 1)),
    ];

    /// <summary>
    /// **The case D-72 exists for, and the one a maximum gets wrong.**
    ///
    /// The changed key is deliberately not the highest-versioned one: `tuner.slot_floor`
    /// goes from 1 to 2 while `screens.s1.slots` stays at 3. A maximum answers 3 both
    /// times, so two different configurations would carry the same stamp and the tuner
    /// segmenting on it would pool them. One plus revisions answers 3 then 4.
    ///
    /// Both rejected mechanisms are computed here rather than described, so this test
    /// states what each of them did instead of asserting only that the current one
    /// works. **It is the only test in this file that fails under a reversion to the
    /// maximum**, which is why it carries them both.
    /// </summary>
    [Fact]
    public void ChangingAKeyOtherThanTheHighestVersionedOneStillMovesTheStoreWideVersion()
    {
        var date = new DateOnly(2026, 8, 7);

        var before = ConfigResolution.ResolveVersion(ThreeOneOne, date);
        var after = ConfigResolution.ResolveVersion(ThreeTwoOne, date);

        // Two revisions of one key, so 3; then a third revision on another key, so 4.
        Assert.Equal(3, before);
        Assert.Equal(4, after);
        Assert.NotEqual(before, after);

        // The maximum: identical across the change, which is the defect D-72 names.
        Assert.Equal(
            ThreeOneOne.Max(r => r.Version),
            ThreeTwoOne.Max(r => r.Version));

        // **The discriminator is that the version moves and the maximum does not**,
        // not the absolute numbers: `before` is 3 and so is the maximum, coincidentally,
        // because this fixture's highest key version happens to equal its revision
        // count plus one. Asserting the movement rather than the value is what makes
        // this test fail on a reversion.
        Assert.NotEqual(
            ThreeOneOne.Max(r => r.Version),
            ThreeTwoOne.Max(r => r.Version) + (after - before));

        // The row count moves here too, so this fixture alone does not separate the
        // current rule from the row count. `SeedingAnAdditionalKeyLeavesEveryPriorDates
        // VersionUnchanged` is what does, and the two tests are a pair rather than
        // either one covering both.
        Assert.Equal(5, ThreeOneOne.Count(r => r.SetOn <= date));
        Assert.Equal(6, ThreeTwoOne.Count(r => r.SetOn <= date));
    }

    [Fact]
    public void TheStoreWideVersionCountsRevisionsRatherThanRows()
    {
        // Five rows are in force and three of them are initial seeds, so the version
        // is one plus the two revisions rather than five [D-72 as amended]. A seed
        // extends the configuration's schema; only a revision changes the
        // configuration in force.
        Assert.Equal(3, ConfigResolution.ResolveVersion(ThreeOneOne, new DateOnly(2026, 8, 7)));
    }

    /// <summary>
    /// **The amendment's whole point, asserted directly.**
    ///
    /// Every key is seeded at version 1 with one fixed backdated instant, so counting
    /// rows would make a phase seeding its keys raise the version for every date from
    /// the window start onward. A backfill re-run would then stamp a different version
    /// on identical data, and both numbers would look like plausible integers.
    /// </summary>
    [Fact]
    public void SeedingAnAdditionalKeyLeavesEveryPriorDatesVersionUnchanged()
    {
        // A key introduced by a later phase, backdated exactly as the seeder backdates.
        ConfigRow[] withANewKey =
        [
            .. ThreeTwoOne,
            new("percentile.cell_min_members", 1, "15", new DateOnly(2020, 1, 1)),
        ];

        foreach (var date in new[]
                 {
                     new DateOnly(2020, 1, 1), new DateOnly(2021, 6, 1),
                     new DateOnly(2023, 1, 1), new DateOnly(2026, 8, 7),
                 })
        {
            Assert.Equal(
                ConfigResolution.ResolveVersion(ThreeTwoOne, date),
                ConfigResolution.ResolveVersion(withANewKey, date));
        }

        // And a row count would not have been unchanged, computed here rather than
        // described, so the test states what the rejected mechanism did.
        Assert.NotEqual(
            ThreeTwoOne.Count(r => r.SetOn <= new DateOnly(2021, 6, 1)),
            withANewKey.Count(r => r.SetOn <= new DateOnly(2021, 6, 1)));
    }

    [Fact]
    public void TheStoreWideVersionIsAsOfTheDateRatherThanAsOfNow()
    {
        // The same failure Resolve has, one level up. A backfilled 2020 night stamped
        // with the version a 2022 change produced would say it ran under
        // configuration that did not exist yet [D-43, INVARIANT 13].
        //
        // Three seeds are in force through 2020 and none of them is a change, so the
        // store is at 1. The key's version 2 lands in 2021 and its version 3 in 2022.
        Assert.Equal(1, ConfigResolution.ResolveVersion(ThreeOneOne, new DateOnly(2020, 6, 1)));
        Assert.Equal(2, ConfigResolution.ResolveVersion(ThreeOneOne, new DateOnly(2021, 6, 1)));
        Assert.Equal(3, ConfigResolution.ResolveVersion(ThreeOneOne, new DateOnly(2022, 6, 1)));

        // And the 2023 change is invisible to a date before it.
        Assert.Equal(3, ConfigResolution.ResolveVersion(ThreeTwoOne, new DateOnly(2023, 1, 1)));
    }

    [Fact]
    public void ADateBeforeEveryRowHasNoStoreWideVersionRatherThanOne()
    {
        // **Null, and it cannot be inferred from the arithmetic** [D-72 as amended].
        // One plus zero revisions is 1, which is a real version, so a resolver that
        // returned the sum alone would answer 1 for a date before the seed and the
        // caller would stamp a run that had no configuration at all. The rows in force
        // are counted separately for exactly this case.
        Assert.Null(ConfigResolution.ResolveVersion(ThreeOneOne, new DateOnly(2019, 12, 31)));

        // The neighbouring date is 1 rather than null, which is what makes the case
        // above a separate check rather than a boundary of the same one.
        Assert.Equal(1, ConfigResolution.ResolveVersion(ThreeOneOne, new DateOnly(2020, 1, 1)));
    }

    [Fact]
    public void TheBoundaryDateCountsTheRowSetOnIt()
    {
        // "At or before", matching Resolve. A row set on the date governs the date, so
        // the three seeds are in force and the store is at 1 rather than absent.
        Assert.Equal(1, ConfigResolution.ResolveVersion(ThreeOneOne, new DateOnly(2020, 1, 1)));
    }

    [Fact]
    public async Task TheStoreResolvesAVersionAndRefusesADateBeforeTheSeed()
    {
        var seeder = new ConfigSeeder(TestDatabase.ConnectionString);
        await seeder.SeedAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var store = new ConfigStore(TestDatabase.ConnectionString);

        var version = await store.RequireVersionAsync(
            new DateOnly(2026, 8, 7), TestContext.Current.CancellationToken).ConfigureAwait(true);

        // **1 immediately after seeding, whatever the key count** [D-72 as amended].
        // Every seeded row is version 1, so none of them is a change and the store is
        // at its first version. This is the assertion that would have to be edited if
        // seeding ever started counting again, which is why it is stated as the
        // literal 1 rather than derived from the seeder.
        Assert.Equal(1, version);

        Assert.Null(await store.ResolveVersionAsync(
            new DateOnly(2019, 12, 31), TestContext.Current.CancellationToken).ConfigureAwait(true));

        await Assert.ThrowsAsync<ConfigVersionNotInForceException>(
            () => store.RequireVersionAsync(
                new DateOnly(2019, 12, 31),
                TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public void TheSeederCoversEveryKeyPhasesOneAndTwoConsume()
    {
        // The count has been wrong three times, recorded as nine and corrected to
        // eleven, twelve and fourteen, and every correction was a key that existed
        // with nothing seeding it. Asserted rather than counted by hand.
        //
        // Thirty at 2.4. percentile.cell_min_members is the fourth correction of
        // that same kind: it had been in CONFIG_REFERENCE.md since the corpus was
        // written and nothing seeded it, so the phase that first ranks anything
        // would have resolved nothing for it.
        //
        // Forty-two at 3.3: the backfill window start, the ticker concurrency, the
        // daily allowance and its reserve, and one weight for each of the six
        // endpoints a sweep calls.
        //
        // Forty-three at D-102, which gives the two pool statements a bound of their
        // own so the connection string's stays the value every other statement is
        // judged against.
        //
        // Forty-four at 3.5.3, which adds the record inspector's bar count. It is a
        // display bound rather than a metric window, and it is a key rather than a
        // literal for the reason section 8 gives: the two windows the same panel shows
        // are already owned elsewhere, and bars are the one thing it bounds that no
        // component does.
        //
        // Seventy-four at 4.3, which adds thirty: four per screen for the five live
        // screens, six for S5's two composites and their floors and quintiles, and four
        // gate thresholds. The count is asserted rather than described because a key
        // with nothing behind it is exactly what this catches [D-112 to D-120].
        //
        // Seventy-seven at 4.5, which adds the three shared screen rules. They are not
        // new: screens.slot_ceiling, screens.floor_percentile and
        // screens.floor_lookback_days have been in CONFIG_REFERENCE.md since the first
        // corpus with D-7 and D-9 behind them, and no seeder row. This assertion is the
        // check for a key with nothing behind it and the gap was the other way round,
        // which is why 4.5 found it by failing to resolve rather than by counting.
        Assert.Equal(77, ConfigSeeder.Keys.Count);

        var duplicates = ConfigSeeder.Keys
            .GroupBy(k => k.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Seeded twice: " + string.Join(", ", duplicates));

        foreach (var required in new[]
                 {
                     "freshness.settled_fraction",
                     "freshness.settled_window_days",
                     "price.reload_window_days",
                     "fundamentals.widest_gap_alert_days",
                     "fundamentals.max_tickers_per_run",
                     "sentiment.tickers_per_call",
                     "sentiment.lookback_days",
                     "flow.max_tickers_per_run",
                     "flow.form4_page_size",
                     "flow.institutional_report_lag_days",
                     "events.earnings_forward_days",
                     "events.earnings_backward_days",

                     // Phase 2 [2.4]. The two market.regime_breadth_* keys are
                     // deliberately absent: the rule they threshold is unauthored,
                     // and seeding a value for a rule that does not exist puts a
                     // number in the store nothing can be read against.
                     "percentile.cell_min_members",
                     "indicator.wilder_warmup_bars",
                     "indicator.base_lookback_days",
                     "indicator.base_max_range_pct",
                     "valuation.own_history_min_points",
                     "market.breadth_ma_days",
                     "market.sector_composite_min_members",
                     "sentiment.min_baseline_days",

                     // D-80's two, held back at 2.4 until the rule existed [2.9].
                     "market.regime_breadth_high",
                     "market.regime_breadth_low",

                     // Phase 3 [3.3]. The window start is D-94's; the allowance, its
                     // reserve and the six weights are what the gate at 3.4 projects
                     // with, and a weight at a call site is the magic number
                     // CLAUDE.md section 8 rules out.
                     "backfill.window_start",
                     "backfill.ticker_concurrency",
                     "backfill.daily_unit_allowance",
                     "backfill.unit_reserve",
                     "backfill.weight_eod",
                     "backfill.weight_fundamentals",
                     "backfill.weight_sentiments_per_ticker",
                     "backfill.weight_form4_page",
                     "backfill.weight_splits",
                     "backfill.weight_dividends",
                 })
        {
            Assert.Contains(ConfigSeeder.Keys, k => string.Equals(k.Key, required, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Every phase 3 key resolves for a simulated date, and the two that are not
    /// plain integers are read back as what they are [3.3].
    ///
    /// **The date key is the one this test exists for.** `config_rows.value` is
    /// `jsonb` and every key seeded before this phase was a number, so nothing had
    /// ever exercised a value that arrives quoted. A consumer parsing the raw text
    /// would get `"2021-01-04"` including its quotes and fail, or worse, trim them by
    /// hand and be wrong the first time a JSON escape appears.
    /// </summary>
    [Fact]
    public async Task EveryPhaseThreeKeyResolvesForASimulatedDateAndReadsBackAsItsType()
    {
        var seeder = new ConfigSeeder(TestDatabase.ConnectionString);
        await seeder.SeedAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var store = new ConfigStore(TestDatabase.ConnectionString);

        // Inside the backfill window and years before today, so this is resolution as
        // of a simulated date rather than as of now [D-43, INVARIANT 13].
        var simulated = new DateOnly(2022, 6, 15);

        var backfillKeys = ConfigSeeder.Keys
            .Where(k => k.Key.StartsWith("backfill.", StringComparison.Ordinal))
            .Select(k => k.Key)
            .ToList();

        Assert.Equal(10, backfillKeys.Count);

        foreach (var key in backfillKeys)
        {
            var row = await store.RequireAsync(key, simulated, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(1, row.Version);
        }

        var windowStart = ConfigValue.Date(
            await store.RequireAsync("backfill.window_start", simulated, TestContext.Current.CancellationToken)
                .ConfigureAwait(true));

        Assert.Equal(new DateOnly(2021, 1, 4), windowStart);

        // The window start must be resolvable itself, which means at or after the seed
        // instant. A window opening before it would give every date in the range a
        // config version of null and fail the run rather than compute against nothing
        // [D-72, D-94].
        Assert.True(
            windowStart >= DateOnly.FromDateTime(ConfigSeeder.SeedInstant.UtcDateTime),
            "The backfill window opens before the seed instant, so no stage could resolve config " +
            "for the first date it would compute.");

        var allowance = ConfigValue.Long(
            await store.RequireAsync("backfill.daily_unit_allowance", simulated, TestContext.Current.CancellationToken)
                .ConfigureAwait(true));

        var reserve = ConfigValue.Long(
            await store.RequireAsync("backfill.unit_reserve", simulated, TestContext.Current.CancellationToken)
                .ConfigureAwait(true));

        // 100,000 is what /api/user's dailyRateLimit read at 3.1, and the reserve is
        // what a sweep may not eat into. A reserve at or above the allowance would
        // halt every sweep before its first call, which is a configuration that looks
        // cautious and does nothing.
        Assert.Equal(100_000, allowance);
        Assert.True(reserve > 0 && reserve < allowance,
            $"The reserve is {reserve} against an allowance of {allowance}, which leaves a sweep " +
            "nothing to spend.");
    }

    /// <summary>
    /// The weights are the ones measured, not plausible ones [3.1, 3.3].
    ///
    /// They are measurements and can go stale if the provider re-prices, which is why
    /// the gate projects with them and never decides with them. What decides is the
    /// reading from `/api/user`. This asserts the projection starts from what was
    /// measured rather than from what someone remembered.
    /// </summary>
    [Fact]
    public void TheSeededEndpointWeightsAreTheMeasuredOnes()
    {
        var weights = ConfigSeeder.Keys
            .Where(k => k.Key.StartsWith("backfill.weight_", StringComparison.Ordinal))
            .ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);

        Assert.Equal("1", weights["backfill.weight_eod"]);
        Assert.Equal("10", weights["backfill.weight_fundamentals"]);
        Assert.Equal("5", weights["backfill.weight_sentiments_per_ticker"]);
        Assert.Equal("10", weights["backfill.weight_form4_page"]);
        Assert.Equal("1", weights["backfill.weight_splits"]);
        Assert.Equal("1", weights["backfill.weight_dividends"]);
    }

    [Fact]
    public void TheSeedInstantPrecedesTheBackfillWindow()
    {
        // D-47 backfills five years. A seeded row stamped later than the earliest
        // date resolved for is invisible to every date before it, and the failure
        // is silent: today's resolution still works [A9].
        Assert.True(
            ConfigSeeder.SeedInstant <= new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "The seed instant must sit at or before the start of the five-year backfill window.");
    }

    [Fact]
    public void TheSeedInstantMeansTheSameDateInEasternAsItReads()
    {
        // A midnight-UTC stamp lands at 19:00 the previous day in New York, so the
        // row comes into force a day before every line of prose about it says. That
        // is the off-by-one CLAUDE.md section 6 warns about, and it is invisible to
        // any test written against a single timezone. It was found by the store
        // test below returning a row for a date that should have had none.
        //
        // Asserted on the UTC hour rather than by converting, because
        // InvariantGlobalization is on in Directory.Build.props and the IANA id
        // "America/New_York" therefore does not resolve on Windows: timezone data
        // comes from the registry, and the IANA-to-Windows mapping is the part ICU
        // supplies. The Windows id still resolves, which is why SystemClock tries
        // both in that order and works. The conversion for config lives in SQL
        // anyway, where Postgres carries its own tzdata, and the end-to-end proof
        // is TheStoreResolvesASeededKeyAndRefusesADateBeforeIt.
        //
        // US Eastern is UTC-5 or UTC-4 and never ahead of UTC, so an instant at or
        // after 05:00 UTC cannot fall back into the previous Eastern day.
        Assert.True(
            ConfigSeeder.SeedInstant.UtcDateTime.Hour >= 5,
            $"The seed instant is {ConfigSeeder.SeedInstant.UtcDateTime:HH:mm} UTC, which is " +
            "before 05:00 and therefore lands on the previous day in US Eastern. The row would " +
            "come into force a day earlier than every document about it says.");
    }

    [Fact]
    public async Task SeedingTwiceInsertsNothingTheSecondTime()
    {
        var seeder = new ConfigSeeder(TestDatabase.ConnectionString);

        await seeder.SeedAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var second = await seeder.SeedAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(0, second);
    }

    [Fact]
    public async Task TheStoreResolvesASeededKeyAndRefusesADateBeforeIt()
    {
        var seeder = new ConfigSeeder(TestDatabase.ConnectionString);
        await seeder.SeedAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var store = new ConfigStore(TestDatabase.ConnectionString);

        var inForce = await store.RequireAsync(
            "freshness.settled_fraction", new DateOnly(2026, 8, 7),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("0.95", inForce.Value);

        // Before the seed instant. Absent rather than current.
        var earlier = await store.ResolveAsync(
            "freshness.settled_fraction", new DateOnly(2019, 12, 31),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Null(earlier);

        await Assert.ThrowsAsync<ConfigNotInForceException>(
            () => store.RequireAsync(
                "freshness.settled_fraction", new DateOnly(2019, 12, 31),
                TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }
}
