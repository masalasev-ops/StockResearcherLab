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
            "SELECT count(*) FROM security_daily WHERE is_active AND size_bucket IS NULL;", conn);

        var withoutABucket = (long?) await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        Assert.True(withoutABucket == 0,
            $"{withoutABucket} active security row(s) carry no size_bucket. The fallback stops at " +
            "size bucket alone because a member always has one, so a null bucket means the third " +
            "step D-10 refuses is now reachable.");
    }

    // ------------------------------------------------- the cell store [D-107] ---

    /// <summary>
    /// The stored population is the one the ranking used, asserted against the fixture's
    /// own arithmetic rather than against the statement that wrote it.
    ///
    /// BROAD carries sixteen non-null members against a floor of fifteen, so its cell
    /// clears and `ranked_scope` is `cell`. THIN carries five, so it does not, and it
    /// falls back to bucket A's whole non-null population of twenty-one. **Sixteen
    /// against five against twenty-one is the whole panel in three numbers**: a
    /// percentile of 80 means one thing over five members and another over twenty-one,
    /// and until 0014 nothing downstream could tell which.
    /// </summary>
    [Fact]
    public async Task TheStoredCellCarriesThePopulationTheRankingUsed()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await RunAsync(ct).ConfigureAwait(true);

        var broad = await CellAsync(BucketA, SectorBroad, ct).ConfigureAwait(true);
        Assert.Equal(16, broad.CellMembers);
        Assert.Equal(21, broad.BucketMembers);
        Assert.Equal(MinMembers, broad.MinMembers);
        Assert.Equal("cell", broad.RankedScope);

        var thin = await CellAsync(BucketA, SectorThin, ct).ConfigureAwait(true);
        Assert.Equal(5, thin.CellMembers);
        Assert.Equal(21, thin.BucketMembers);
        Assert.Equal("bucket", thin.RankedScope);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// A name with no sector forms no cell, so its row carries a null `cell_members`
    /// rather than a cell of zero, and the fallback it took is what `ranked_scope` says.
    ///
    /// Zero and absent are different facts here in the way `CLAUDE.md` §6 means: a cell
    /// of zero would say the sector was ranked and found empty, where the truth is that
    /// no sector cell was formed at all [`METRICS.md` §6.4].
    /// </summary>
    [Fact]
    public async Task ANameWithNoSectorCarriesANullCellCountRatherThanAZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await RunAsync(ct).ConfigureAwait(true);

        var none = await CellAsync(BucketB, null, ct).ConfigureAwait(true);

        Assert.Null(none.CellMembers);
        Assert.Equal("bucket", none.RankedScope);
        Assert.True(none.BucketMembers >= MinMembers,
            $"bucket B carries {none.BucketMembers} non-null member(s), so the bucket fallback " +
            "could not have been what ranked these rows and this assertion is testing nothing.");

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// A cell whose bucket is also below the floor is ranked by nothing, and the store
    /// says so rather than leaving a reader to infer it from two counts and a threshold.
    /// </summary>
    [Fact]
    public async Task ACellWhoseBucketIsAlsoThinIsRecordedAsRankedByNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await RunAsync(ct).ConfigureAwait(true);

        var tiny = await CellAsync(BucketC, SectorTiny, ct).ConfigureAwait(true);

        Assert.True(tiny.BucketMembers < MinMembers,
            $"bucket C carries {tiny.BucketMembers} non-null member(s) against a floor of " +
            $"{MinMembers}, so it clears and this is not the case the assertion is for.");

        Assert.Equal("none", tiny.RankedScope);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// **The stored cells reproduce what the run log reports for the same date.**
    ///
    /// `FallbackReportSql` counts rows per scope and the cell store counts members per
    /// cell, and the two have to agree: a ticker ranked in a sector cell is one of that
    /// cell's members, so summing `cell_members` over the cells whose scope is `cell`
    /// gives the same number the report calls `in_cell`. They are computed by two
    /// statements from one CTE, and this is the assertion that keeps them one answer
    /// rather than two [D-107].
    /// </summary>
    [Fact]
    public async Task TheStoredCellsAgreeWithTheFallbackReportForTheSameDate()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await RunAsync(ct).ConfigureAwait(true);

        var source = PercentileEngine.Sources.Single(s => s.Table == "indicator_daily");

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        var reported = 0L;
        await using (var cmd = new NpgsqlCommand(
            PercentileEngine.FallbackReportSql(source, RunDate, MinMembers), conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(true))
            {
                if (string.Equals(r.GetString(0), Metric, StringComparison.Ordinal))
                {
                    reported = r.GetInt64(1);
                }
            }
        }

        await using var stored = new NpgsqlCommand(
            """
            SELECT coalesce(sum(cell_members), 0)
            FROM percentile_cell_daily
            WHERE date = @d AND metric = @m AND ranked_scope = 'cell';
            """, conn);
        stored.Parameters.AddWithValue("d", RunDate);
        stored.Parameters.AddWithValue("m", Metric);

        var summed = (long?) await stored.ExecuteScalarAsync(ct).ConfigureAwait(true);

        Assert.True(reported > 0,
            "The fallback report counted nothing ranked in a cell, so this comparison would " +
            "hold over two zeroes and say nothing.");

        Assert.Equal(reported, summed);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The marker advances with the pass, and a date beyond it is distinguishable from a
    /// date whose cells simply do not exist [D-106, D-107].
    /// </summary>
    [Fact]
    public async Task TheCoverageMarkerRecordsTheDateTheWriteReached()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);
        await RunAsync(ct).ConfigureAwait(true);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT source_table, covered_from, covered_to FROM percentile_cell_coverage ORDER BY source_table;",
            conn);

        var covered = new List<(string Source, DateOnly From, DateOnly To)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(true))
            {
                covered.Add((r.GetString(0), r.GetFieldValue<DateOnly>(1), r.GetFieldValue<DateOnly>(2)));
            }
        }

        Assert.Equal(
            PercentileEngine.Sources.Select(s => s.Table).OrderBy(t => t, StringComparer.Ordinal),
            covered.Select(c => c.Source));

        // Read as coverage, never as equality: the marker spans this run's date and
        // whatever earlier runs on this database reached, and the assertion is that this
        // date is inside it rather than that it equals it [D-106].
        Assert.All(covered, c => Assert.True(c.From <= RunDate && c.To >= RunDate,
            $"{c.Source} covers {c.From} to {c.To}, which does not contain {RunDate}."));

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads one cell row for one metric. The sector is matched with
    /// `IS NOT DISTINCT FROM`, the same comparison the unique index makes under
    /// `NULLS NOT DISTINCT`, so the row that carries no sector is reachable and is not
    /// confused with the empty-string cell C11 ranks separately [0016].
    /// </summary>
    private static async Task<(int? CellMembers, int BucketMembers, int MinMembers, string RankedScope)>
        CellAsync(string bucket, string? sector, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT cell_members, bucket_members, min_members, ranked_scope
            FROM percentile_cell_daily
            WHERE date = @d AND size_bucket = @b AND sector IS NOT DISTINCT FROM @s AND metric = @m;
            """, conn);
        cmd.Parameters.AddWithValue("d", RunDate);
        cmd.Parameters.AddWithValue("b", bucket);
        cmd.Parameters.AddWithValue("s", (object?) sector ?? DBNull.Value);
        cmd.Parameters.AddWithValue("m", Metric);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        Assert.True(await r.ReadAsync(ct).ConfigureAwait(false),
            $"No cell row for {bucket}/{sector ?? "(no sector)"}/{Metric} on {RunDate}.");

        return (
            await r.IsDBNullAsync(0, ct).ConfigureAwait(false) ? null : r.GetInt32(0),
            r.GetInt32(1),
            r.GetInt32(2),
            r.GetString(3));
    }

    // ------------------------------------------------------------- declared ---

    /// <summary>
    /// Thirty columns over four tables, each declared as an <c>Update</c> of a column
    /// set disjoint from what the metric engine inserts [D-77], plus the two stores this
    /// stage owns whole from 3.5.2 [D-107].
    ///
    /// **The two are separated by operation rather than counted together.** The four are
    /// updates of somebody else's rows and the two are inserts of this stage's own, and
    /// collapsing them into one count would hide the case INVARIANT 10 is read per
    /// operation for.
    /// </summary>
    [Fact]
    public void TheDeclaredWritesAreThirtyPercentileColumnsOverFourTables()
    {
        var stage = new PercentileEngine();

        var ranked = stage.WriteSet.Where(w => w.Operation == WriteOperation.Update).ToList();
        var owned = stage.WriteSet.Where(w => w.Operation == WriteOperation.Insert).ToList();

        Assert.Equal(4, ranked.Count);
        Assert.Equal(30, ranked.Sum(w => w.Columns.Count));
        Assert.All(ranked, w => Assert.All(w.Columns,
            c => Assert.EndsWith(PercentileEngine.Suffix, c, StringComparison.Ordinal)));

        // The cell store and its coverage marker, owned whole rather than by column set.
        Assert.Equal(
            ["percentile_cell_coverage", "percentile_cell_daily"],
            owned.Select(w => w.Table).OrderBy(t => t, StringComparer.Ordinal));

        // The five the Reads cell names, and no more.
        Assert.Equal(
            ["flow_daily", "indicator_daily", "security_daily", "sentiment_derived_daily", "valuation_daily"],
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
                -- **`security_daily` from 3.12**, which is where C11 takes the cell now.
                -- Dated well before any fixture date because the read is the most recent
                -- row at or before the date being ranked [D-92], so one early row stands
                -- for a membership that never changes across the fixture's window.
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, DATE '2000-01-01', @s, @b, 1000000000, true)
                ON CONFLICT (ticker, date) DO UPDATE SET
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
            "DELETE FROM security_daily WHERE size_bucket = ANY(@b);", conn))
        {
            cmd.Parameters.AddWithValue("b", new[] { BucketA, BucketB, BucketC });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // The cells this fixture's buckets produced [D-107, 0014]. Scoped to the fixture's
        // own buckets rather than to the date, because the date is shared with nothing but
        // the coverage marker is not: the marker is left standing deliberately, being a
        // record of what a pass reached rather than of rows that exist.
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM percentile_cell_daily WHERE date = @d AND size_bucket = ANY(@b);", conn))
        {
            cmd.Parameters.AddWithValue("d", RunDate);
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
