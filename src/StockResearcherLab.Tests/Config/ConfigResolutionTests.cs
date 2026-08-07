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

    [Fact]
    public void TheSeederCoversEveryKeyPhaseOneConsumes()
    {
        // The count has been wrong three times, recorded as nine and corrected to
        // eleven, twelve and fourteen, and every correction was a key that existed
        // with nothing seeding it. Asserted rather than counted by hand.
        Assert.Equal(14, ConfigSeeder.Keys.Count);

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
                 })
        {
            Assert.Contains(ConfigSeeder.Keys, k => string.Equals(k.Key, required, StringComparison.Ordinal));
        }
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
        // InvariantGlobalization is on in Directory.Build.props and .NET therefore
        // cannot resolve any timezone id at all. The conversion itself lives in SQL,
        // where Postgres carries its own tzdata, and the end-to-end proof is
        // TheStoreResolvesASeededKeyAndRefusesADateBeforeIt.
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
