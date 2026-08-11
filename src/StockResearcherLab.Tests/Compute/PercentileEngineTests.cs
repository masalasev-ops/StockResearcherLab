using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// C11's spot check, over a seeded universe whose every cell size is chosen so that
/// each expected percentile is an exact fraction.
///
/// Phase 2's second definition of done is that a percentile spot-check confirms cell
/// membership is correct and the fallback fires where cells are thin. These are those,
/// and each expectation is arithmetic written out at its assertion: with a population
/// of n, the k-th smallest value takes (k - 1) / (n - 1) x 100, so the smallest is 0
/// and the largest is 100 whatever n is. What discriminates is the value in between,
/// which is different for a cell of sixteen and a bucket of twenty-one.
///
/// **The seeded date is in 2001.** The universe is inserted into the real `security`
/// table and removed afterwards, and a date no real price series reaches is what keeps
/// the fixture's cells from being joined by two and a half thousand live names.
/// </summary>
[Collection("database")]
public sealed class PercentileEngineTests
{
    private static readonly DateOnly RunDate = new(2001, 4, 18);

    private const int MinMembers = 15;

    /// <summary>The metric the fixture varies. Everything else on the row is null and ranks nowhere.</summary>
    private const string Metric = "atr_pct";

    private const string BucketA = "SRLTEST-A";
    private const string BucketB = "SRLTEST-B";
    private const string BucketC = "SRLTEST-C";

    private const string SectorBroad = "SRLTEST-BROAD";
    private const string SectorThin = "SRLTEST-THIN";
    private const string SectorX = "SRLTEST-X";
    private const string SectorTiny = "SRLTEST-TINY";

    /// <summary>
    /// Cell membership, and the null population that is not part of it.
    ///
    /// Bucket A holds two sectors. BROAD carries sixteen values, 1 through 16, and four
    /// members whose metric is null. THIN carries five, 101 through 105.
    ///
    /// BROAD clears the floor on sixteen non-null members, so its rows are ranked
    /// inside the sector cell: value 1 takes 0, value 16 takes 100, value 9 takes
    /// 8/15 x 100 = 53.33. Ranked against the bucket instead, value 16 would take
    /// 15/20 x 100 = 75 and value 9 would take 8/20 x 100 = 40, so the numbers separate
    /// the two readings rather than agreeing with both.
    ///
    /// **The four nulls are not in the denominator.** Counted in it, the population
    /// would be twenty and value 16 would take 15/19 x 100 = 78.9. That number looks
    /// exactly like a percentile and there is nothing downstream that could tell the
    /// difference, which is why `METRICS.md` §6.2 makes the floor a test on the
    /// population being ranked rather than on the cell.
    /// </summary>
    [Fact]
    public async Task ACellThatClearsTheFloorIsRankedInsideItsSector()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await RunAsync(ct).ConfigureAwait(true);

        Assert.Equal(0f, await RankedAsync(Name(SectorBroad, 1), ct).ConfigureAwait(true), 3);
        Assert.Equal(100f, await RankedAsync(Name(SectorBroad, 16), ct).ConfigureAwait(true), 3);

        Assert.Equal(
            (float) (8.0 / 15.0 * 100.0),
            await RankedAsync(Name(SectorBroad, 9), ct).ConfigureAwait(true),
            3);

        // The four with no value carry no percentile, rather than the zero a rank over
        // a partition containing them would have produced.
        for (var i = 17; i <= 20; i++)
        {
            Assert.Null(await PercentileAsync(Name(SectorBroad, i), ct).ConfigureAwait(true));
        }

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The fallback fires where the cell is thin and does not where it is not.
    ///
    /// THIN carries five members against a floor of fifteen, so its rows are ranked
    /// against bucket A's whole non-null population of twenty-one: sixteen from BROAD
    /// at 1 to 16 and five from THIN at 101 to 105. THIN's lowest therefore sits
    /// seventeenth of twenty-one and takes 16/20 x 100 = 80, and its highest takes 100.
    ///
    /// Ranked inside its own five-member cell instead, the lowest would take 0. Eighty
    /// against zero is the whole of D-10 in one number.
    /// </summary>
    [Fact]
    public async Task AThinCellFallsBackToTheSizeBucketAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await RunAsync(ct).ConfigureAwait(true);

        Assert.Equal(80f, await RankedAsync(Name(SectorThin, 101), ct).ConfigureAwait(true), 3);
        Assert.Equal(100f, await RankedAsync(Name(SectorThin, 105), ct).ConfigureAwait(true), 3);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// A null sector goes straight to the bucket-only fallback and does not form a cell
    /// of its own.
    ///
    /// Bucket B holds sixteen members with no sector, at 1 to 16, and fifteen in sector
    /// X at 17 to 31. Postgres groups nulls together in a <c>PARTITION BY</c>, so
    /// without the rule the sixteen would form a sixteen-member cell, clear the floor,
    /// and be ranked against each other under a column that says sector. That cell
    /// would put value 16 at 100. Sent to the bucket of thirty-one it sits sixteenth
    /// and takes 15/30 x 100 = 50.
    ///
    /// Sector X clears the floor at exactly fifteen and is ranked inside itself, so its
    /// lowest takes 0 where the bucket would have given it 16/30 x 100 = 53.33.
    /// </summary>
    [Fact]
    public async Task ANullSectorIsRankedInItsBucketRatherThanAgainstOtherNullSectors()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await RunAsync(ct).ConfigureAwait(true);

        Assert.Equal(50f, await RankedAsync(Name("NOSECTOR", 16), ct).ConfigureAwait(true), 3);
        Assert.Equal(0f, await RankedAsync(Name("NOSECTOR", 1), ct).ConfigureAwait(true), 3);

        Assert.Equal(0f, await RankedAsync(Name(SectorX, 17), ct).ConfigureAwait(true), 3);
        Assert.Equal(100f, await RankedAsync(Name(SectorX, 31), ct).ConfigureAwait(true), 3);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// When the size bucket alone is also below the floor the percentile is null, never
    /// a rank across buckets.
    ///
    /// Bucket C holds ten members. Ranked against bucket A they would take real-looking
    /// numbers, and ranking a $1B name against megacaps is the exact thing D-10 exists
    /// to prevent, so a second fallback that did it would be worse than no value.
    /// </summary>
    [Fact]
    public async Task AThinBucketCarriesNullRatherThanACrossBucketRank()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await RunAsync(ct).ConfigureAwait(true);

        for (var v = 1; v <= 10; v++)
        {
            Assert.Null(await PercentileAsync(Name(SectorTiny, v), ct).ConfigureAwait(true));
        }

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Ties take the same percentile, which is what removes the tie-break and with it
    /// the determinism hazard a tie-break would introduce.
    ///
    /// Two of BROAD's sixteen are given the same value here, so under
    /// <c>row_number()</c> one of them would take a higher rank than the other and
    /// which one would depend on the plan.
    /// </summary>
    [Fact]
    public async Task TiedValuesTakeTheSamePercentile()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        await ExecuteAsync(
            $"UPDATE indicator_daily SET {Metric} = 4 WHERE ticker = @t AND date = @d;",
            Name(SectorBroad, 5), ct).ConfigureAwait(true);

        await RunAsync(ct).ConfigureAwait(true);

        var four = await RankedAsync(Name(SectorBroad, 4), ct).ConfigureAwait(true);
        var alsoFour = await RankedAsync(Name(SectorBroad, 5), ct).ConfigureAwait(true);

        Assert.Equal(four, alsoFour, 5);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Running the same date twice leaves every percentile equal. D-68 on this stage's
    /// own operation, which for an update is the value rather than the row count.
    /// </summary>
    [Fact]
    public async Task RunningTheSameDateTwiceLeavesEveryPercentileEqual()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        await RunAsync(ct).ConfigureAwait(true);
        var first = await AllPercentilesAsync(ct).ConfigureAwait(true);

        await RunAsync(ct).ConfigureAwait(true);
        var second = await AllPercentilesAsync(ct).ConfigureAwait(true);

        Assert.Equal(first, second);
        Assert.NotEmpty(first);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// A universe member's size bucket cannot be null, which is what lets the fallback
    /// stop at two steps rather than needing a third.
    ///
    /// A member cleared D-4's market capitalisation floor, so it has a market
    /// capitalisation, so C01 assigned it a bucket. The sector hole is real and the
    /// bucket hole is not, and a reader cannot otherwise tell which of the two was
    /// reasoned about [`METRICS.md` §6.4].
    ///
    /// **This is the rule rather than the data, and that is the half that survives a
    /// fresh database.** <c>ci.ps1</c> drops and recreates its own, so a query over
    /// `security` there asserts over nothing at all. The rule is total: every market
    /// capitalisation takes one of three values and none takes null, so the claim fails
    /// here if C01 ever gains a fourth branch that returns one.
    /// </summary>
    [Fact]
    public void TheSizeBucketRuleIsTotalAndNeverReturnsNull()
    {
        const decimal largeFloor = 10_000_000_000m;
        const decimal midFloor = 2_000_000_000m;

        decimal[] capitalisations =
        [
            300_000_000m, midFloor - 1m, midFloor, midFloor + 1m,
            largeFloor - 1m, largeFloor, largeFloor + 1m, 4_000_000_000_000m,
        ];

        foreach (var cap in capitalisations)
        {
            var bucket = UniverseBuilder.Bucket(cap, largeFloor, midFloor);

            Assert.Contains(bucket, new[] { "large", "mid", "small" });
        }

        // The boundaries themselves are inside the larger bucket, which is what makes
        // the three exhaustive rather than leaving a gap between them.
        Assert.Equal("large", UniverseBuilder.Bucket(largeFloor, largeFloor, midFloor));
        Assert.Equal("mid", UniverseBuilder.Bucket(midFloor, largeFloor, midFloor));
        Assert.Equal("small", UniverseBuilder.Bucket(midFloor - 1m, largeFloor, midFloor));
    }

    /// <summary>
    /// The same claim against whatever universe is in the store, which is the half that
    /// would catch C01 writing a null through some route the rule above does not cover.
    ///
    /// **It asserts nothing against an empty database and that is deliberate.** The
    /// rule test above is what holds in CI; this one is what holds against a store C01
    /// has actually built, and requiring a populated universe here would fail every
    /// run from a fresh schema for a reason that is not a defect.
    /// </summary>
    [Fact]
    public async Task NoActiveUniverseMemberCarriesANullSizeBucket()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM security WHERE is_active AND size_bucket IS NULL;", conn);

        var withoutABucket = (long?) await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        Assert.True(withoutABucket == 0,
            $"{withoutABucket} active security row(s) carry no size_bucket. The fallback stops at " +
            "size bucket alone because a member always has one, so a null bucket means the third " +
            "step D-10 refuses is now reachable.");
    }

    // ------------------------------------------------------------- declared ---

    /// <summary>
    /// Thirty columns over four tables, each declared as an <c>Update</c> of a column
    /// set disjoint from what the metric engine inserts [D-77].
    /// </summary>
    [Fact]
    public void TheDeclaredWritesAreThirtyPercentileColumnsOverFourTables()
    {
        var stage = new PercentileEngine();

        Assert.Equal(4, stage.WriteSet.Count);
        Assert.All(stage.WriteSet, w => Assert.Equal(WriteOperation.Update, w.Operation));
        Assert.Equal(30, stage.WriteSet.Sum(w => w.Columns.Count));
        Assert.All(stage.WriteSet, w => Assert.All(w.Columns,
            c => Assert.EndsWith(PercentileEngine.Suffix, c, StringComparison.Ordinal)));

        // The five the Reads cell names, and no more.
        Assert.Equal(
            ["flow_daily", "indicator_daily", "security", "sentiment_derived_daily", "valuation_daily"],
            stage.ReadSet.OrderBy(t => t, StringComparer.Ordinal));

        // base_breakout_flag is the one column with a named reader that is not ranked.
        // A percentile over a two-valued column collapses to two values.
        Assert.DoesNotContain(
            PercentileEngine.Sources.SelectMany(s => s.Metrics), m =>
                string.Equals(m, "base_breakout_flag", StringComparison.Ordinal));
    }

    // --------------------------------------------------------------- rigging ---

    private static async Task RunAsync(CancellationToken ct)
    {
        var stage = new PercentileEngine();

        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(RunDate),
            new StubConfig(MinMembers));

        await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
    }

    private static string Name(string group, int value)
        => string.Create(CultureInfo.InvariantCulture, $"SRLTEST.P{group}{value}");

    /// <summary>
    /// The percentile, asserted to be present. A null where a number is expected is a
    /// different failure from a wrong number and says so rather than throwing on a
    /// dereference.
    /// </summary>
    private static async Task<float> RankedAsync(string ticker, CancellationToken ct)
    {
        var value = await PercentileAsync(ticker, ct).ConfigureAwait(false);

        Assert.True(value.HasValue, $"{ticker} carries no percentile, where one was expected.");

        return value.Value;
    }

    private static async Task<float?> PercentileAsync(string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Metric}{PercentileEngine.Suffix} FROM indicator_daily WHERE ticker = @t AND date = @d;",
            conn);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        Assert.True(await r.ReadAsync(ct).ConfigureAwait(false), $"{ticker} has no row to read.");

        return await r.IsDBNullAsync(0, ct).ConfigureAwait(false) ? null : r.GetFloat(0);
    }

    private static async Task<IReadOnlyList<(string Ticker, float? Value)>> AllPercentilesAsync(
        CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT ticker, {Metric}{PercentileEngine.Suffix}
            FROM indicator_daily WHERE date = @d ORDER BY ticker;
            """, conn);
        cmd.Parameters.AddWithValue("d", RunDate);

        var rows = new List<(string, float?)>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add((r.GetString(0),
                await r.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : r.GetFloat(1)));
        }

        return rows;
    }

    private static async Task ExecuteAsync(string sql, string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", RunDate);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The seeded universe. Every member is inserted into `security` and
    /// `indicator_daily` and removed afterwards.
    ///
    ///   bucket A  sector BROAD  16 members at 1..16, plus 4 carrying no value
    ///             sector THIN    5 members at 101..105
    ///   bucket B  no sector     16 members at 1..16
    ///             sector X      15 members at 17..31
    ///   bucket C  sector TINY   10 members at 1..10
    /// </summary>
    private static async Task SeedAsync(CancellationToken ct)
    {
        await ClearAsync(ct).ConfigureAwait(false);

        var members = new List<(string Ticker, string Bucket, string? Sector, double? Value)>();

        for (var v = 1; v <= 16; v++)
        {
            members.Add((Name(SectorBroad, v), BucketA, SectorBroad, v));
        }

        for (var v = 17; v <= 20; v++)
        {
            members.Add((Name(SectorBroad, v), BucketA, SectorBroad, null));
        }

        for (var v = 101; v <= 105; v++)
        {
            members.Add((Name(SectorThin, v), BucketA, SectorThin, v));
        }

        for (var v = 1; v <= 16; v++)
        {
            members.Add((Name("NOSECTOR", v), BucketB, null, v));
        }

        for (var v = 17; v <= 31; v++)
        {
            members.Add((Name(SectorX, v), BucketB, SectorX, v));
        }

        for (var v = 1; v <= 10; v++)
        {
            members.Add((Name(SectorTiny, v), BucketC, SectorTiny, v));
        }

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        foreach (var (ticker, bucket, sector, value) in members)
        {
            await using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO security (ticker, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @s, @b, 1000000000, true)
                ON CONFLICT (ticker) DO UPDATE SET
                    sector = EXCLUDED.sector, size_bucket = EXCLUDED.size_bucket,
                    is_active = EXCLUDED.is_active;
                """, conn))
            {
                cmd.Parameters.AddWithValue("t", ticker);
                cmd.Parameters.AddWithValue("s", (object?) sector ?? DBNull.Value);
                cmd.Parameters.AddWithValue("b", bucket);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var cmd = new NpgsqlCommand(
                $"""
                INSERT INTO indicator_daily (ticker, date, {Metric})
                VALUES (@t, @d, @v)
                ON CONFLICT (ticker, date) DO UPDATE SET {Metric} = EXCLUDED.{Metric};
                """, conn))
            {
                cmd.Parameters.AddWithValue("t", ticker);
                cmd.Parameters.AddWithValue("d", RunDate);
                cmd.Parameters.AddWithValue("v", (object?) value ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Removed by the seeded date and by the ticker prefix through an equality list
    /// rather than a LIKE, which does not use the index under this collation and timed
    /// out against a populated table [1.5, FlowEngineTests].
    /// </summary>
    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM indicator_daily WHERE date = @d;", conn))
        {
            cmd.Parameters.AddWithValue("d", RunDate);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM security WHERE size_bucket = ANY(@b);", conn))
        {
            cmd.Parameters.AddWithValue("b", new[] { BucketA, BucketB, BucketC });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

    private sealed class StubConfig(int minMembers) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => Task.FromResult<ConfigRow?>(new ConfigRow(
                key, 1, minMembers.ToString(CultureInfo.InvariantCulture), new DateOnly(2000, 1, 1)));

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");
    }
}
