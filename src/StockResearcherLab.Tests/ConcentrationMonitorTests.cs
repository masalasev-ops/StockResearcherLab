using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests;

/// <summary>
/// Checkpoint 4.11. C28 measures the two diversity guarantees rather than assuming them.
///
/// **The whole point of the component is that it reads `security_daily` and not
/// `security`** [D-92, §8's B3]. `security` holds one row per ticker carrying today's
/// bucket, so a megacap share over a 2022 window computed from it classifies 2022 with
/// 2026 buckets. The last test here states that difference as a number rather than as an
/// argument.
/// </summary>
[Collection("database")]
public sealed class ConcentrationMonitorTests
{
    private static readonly DateOnly RunDate = new(2021, 8, 2);

    private const string Sector = "srltest-conc";

    // ------------------------------------------------------- the two alerts ---

    /// <summary>
    /// **A twenty-date window at forty percent megacap raises one alert**, forty being
    /// above `monitor.megacap_share_max` of a third, and the other stays quiet.
    /// </summary>
    [Fact]
    public async Task AWindowAtFortyPercentMegacapRaisesTheMegacapAlertAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            // Fifteen names a date over twenty dates, six large: 40 percent. Distinct
            // names per date, so the three hundred tickers clear the other bound and this
            // fixture raises one alert rather than both.
            await FillAsync(dates: 20, large: 6, other: 9, distinctPerDate: true, ct);

            await RunAsync(ct);

            Assert.Equal(["megacap_share"], await AlertTypesAsync(ct));
            Assert.Contains("40.0 percent", await DetailAsync("megacap_share", ct), StringComparison.Ordinal);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A sixty-date window at 249 distinct tickers raises the other**, 249 being below
    /// `monitor.distinct_tickers_60d_min` of 250 by one. One below rather than far below,
    /// so the assertion is about the comparison rather than about a thin window.
    /// </summary>
    [Fact]
    public async Task AWindowAtTwoHundredAndFortyNineDistinctTickersRaisesTheOtherAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await FillDistinctAsync(dates: 60, distinctTickers: 249, ct);

            await RunAsync(ct);

            Assert.Equal(["distinct_tickers_60d"], await AlertTypesAsync(ct));
            Assert.Contains("249 distinct", await DetailAsync("distinct_tickers_60d", ct), StringComparison.Ordinal);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>A clean window raises neither, and the measurement is still reported.</summary>
    [Fact]
    public async Task ACleanWindowRaisesNeither()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await FillDistinctAsync(dates: 60, distinctTickers: 250, ct);

            await RunAsync(ct);

            Assert.Empty(await AlertTypesAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// Two runs over one date leave one alert, not two. The date's own alerts are removed
    /// before it is measured again, both operations being this component's.
    /// </summary>
    [Fact]
    public async Task TwoRunsOverOneDateLeaveOneAlert()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        try
        {
            await FillAsync(dates: 20, large: 6, other: 9, distinctPerDate: true, ct);

            await RunAsync(ct);
            await RunAsync(ct);

            Assert.Equal(["megacap_share"], await AlertTypesAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>C28 is `alert`'s only declared writer [INVARIANT 10].</summary>
    [Fact]
    public void C28IsTheOnlyDeclaredWriterOfAlert()
    {
        var writers = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .Where(o => o.WriteSet.Any(w => w.Table == "alert"))
            .Select(o => o.Name)
            .ToList();

        Assert.Equal(["ConcentrationMonitor"], writers);
    }

    // --------------------------------------------------------- B3, as a number ---

    /// <summary>
    /// **The same window classified from `security` and from `security_daily` gives
    /// different answers**, which is §8's B3 stated as a number rather than as an
    /// argument.
    ///
    /// Ten candidates on a 2022 date. Two of them were mid then and are large now, which
    /// is the ordinary path of a company that grew. Point in time the megacap share is
    /// 20 percent; classified from today's buckets it is 40 percent, and a third is the
    /// bound, so the same window passes the guarantee on one reading and fails it on the
    /// other.
    ///
    /// The fixture is fabricated rather than drawn from the real store, because the
    /// suite's database carries no backfill and CI has none either. **The real figure was
    /// measured against the populated store and is recorded in `PROGRESS.md`**: on
    /// 2022-06-15, of 2,568 active members, 744 were `large` point in time and 824 are
    /// `large` today, so the universe's large share moves from 28.97 to 32.09 percent,
    /// and 1,117 of the 2,568 sit in a different bucket now than they did then.
    /// </summary>
    [Fact]
    public async Task TheSameWindowClassifiedFromSecurityAndSecurityDailyDiffers()
    {
        var ct = TestContext.Current.CancellationToken;
        var on = new DateOnly(2022, 6, 15);

        await ClearDriftAsync(on, ct);

        try
        {
            for (var i = 0; i < 10; i++)
            {
                var ticker = "SRLDRIFT." + i.ToString("00", CultureInfo.InvariantCulture);

                // Two names were large in 2022; two more grew into large by today.
                var thenBucket = i < 2 ? "large" : "mid";
                var nowBucket = i < 4 ? "large" : "mid";

                await ExecAsync("""
                    INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                    VALUES (@t, @d, @sec, @b, 1000000000, TRUE)
                    ON CONFLICT (ticker, date) DO UPDATE SET size_bucket = EXCLUDED.size_bucket;
                    """, ct, ("t", ticker), ("d", on), ("sec", Sector), ("b", thenBucket));

                await ExecAsync("""
                    INSERT INTO security (ticker, name, sector, size_bucket, is_active)
                    VALUES (@t, @t, @sec, @b, TRUE)
                    ON CONFLICT (ticker) DO UPDATE SET size_bucket = EXCLUDED.size_bucket;
                    """, ct, ("t", ticker), ("sec", Sector), ("b", nowBucket));

                await ExecAsync("""
                    INSERT INTO candidate_set (ticker, date, screens_surfacing, size_bucket)
                    VALUES (@t, @d, ARRAY['SRLDRIFT'], @b)
                    ON CONFLICT (ticker, date) DO NOTHING;
                    """, ct, ("t", ticker), ("d", on), ("b", thenBucket));
            }

            var pointInTime = await ShareAsync(PointInTimeSql(on), ct);
            var asOfToday = await ShareAsync(AsOfTodaySql(on), ct);

            Assert.Equal(0.20d, pointInTime, 4);
            Assert.Equal(0.40d, asOfToday, 4);

            // And the difference straddles the bound, so the two readings disagree about
            // whether the guarantee held rather than merely differing.
            Assert.True(pointInTime < 0.333d);
            Assert.True(asOfToday > 0.333d);
        }
        finally
        {
            await ClearDriftAsync(on, ct);
        }
    }

    /// <summary>The component's own statement, which is the point-in-time one.</summary>
    private static string PointInTimeSql(DateOnly on)
        => $"""
            SELECT count(*) FILTER (WHERE s.size_bucket = 'large')::numeric / NULLIF(count(*), 0)
            FROM candidate_set c
            LEFT JOIN LATERAL (
                SELECT sd.size_bucket FROM security_daily sd
                WHERE sd.ticker = c.ticker AND sd.date <= c.date
                ORDER BY sd.date DESC LIMIT 1
            ) s ON TRUE
            WHERE c.date = DATE '{on:yyyy-MM-dd}';
            """;

    /// <summary>The statement §3's cell said before D-74's amendment, kept only here.</summary>
    private static string AsOfTodaySql(DateOnly on)
        => $"""
            SELECT count(*) FILTER (WHERE s.size_bucket = 'large')::numeric / NULLIF(count(*), 0)
            FROM candidate_set c
            JOIN security s ON s.ticker = c.ticker
            WHERE c.date = DATE '{on:yyyy-MM-dd}';
            """;

    // ------------------------------------------------------------- plumbing ---

    private static async Task RunAsync(CancellationToken ct)
    {
        var stage = new ConcentrationMonitor();

        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(RunDate),
            new ConfigStore(TestDatabase.ConnectionString));

        await stage.ExecuteAsync(context, ct).ConfigureAwait(true);
    }

    private static async Task SeedAsync(CancellationToken ct)
    {
        await ClearAsync(ct);
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The same names on every date in the window, so the megacap share is the ratio asked
    /// for and the distinct count is the row count per date.
    /// </summary>
    private static async Task FillAsync(
        int dates, int large, int other, bool distinctPerDate, CancellationToken ct)
    {
        for (var day = 0; day < dates; day++)
        {
            var on = RunDate.AddDays(-day);

            for (var i = 0; i < large + other; i++)
            {
                var suffix = distinctPerDate
                    ? (day * (large + other) + i).ToString("0000", CultureInfo.InvariantCulture)
                    : i.ToString("0000", CultureInfo.InvariantCulture);

                await AddCandidateAsync("SRLCONC." + suffix, on, i < large ? "large" : "mid", ct);
            }
        }
    }

    /// <summary>
    /// A window carrying exactly the number of distinct tickers asked for, all mid, so the
    /// megacap alert cannot fire and the distinct one is on its own.
    /// </summary>
    private static async Task FillDistinctAsync(int dates, int distinctTickers, CancellationToken ct)
    {
        for (var day = 0; day < dates; day++)
        {
            var on = RunDate.AddDays(-day);

            // Five names a date, walking round the pool, so every one of the distinct
            // tickers appears at least once across the window.
            for (var i = 0; i < 5; i++)
            {
                var index = (day * 5 + i) % distinctTickers;

                await AddCandidateAsync(
                    "SRLCONC." + index.ToString("0000", CultureInfo.InvariantCulture), on, "mid", ct);
            }
        }
    }

    private static async Task AddCandidateAsync(
        string ticker, DateOnly on, string bucket, CancellationToken ct)
    {
        await ExecAsync("""
            INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
            VALUES (@t, @d, @sec, @b, 1000000000, TRUE)
            ON CONFLICT (ticker, date) DO UPDATE SET size_bucket = EXCLUDED.size_bucket;
            """, ct, ("t", ticker), ("d", on), ("sec", Sector), ("b", bucket));

        await ExecAsync("""
            INSERT INTO candidate_set (ticker, date, screens_surfacing, size_bucket)
            VALUES (@t, @d, ARRAY['SRLCONC'], @b)
            ON CONFLICT (ticker, date) DO NOTHING;
            """, ct, ("t", ticker), ("d", on), ("b", bucket));
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await ExecAsync("DELETE FROM alert WHERE date = @d;", ct, ("d", RunDate));
        await ExecAsync("DELETE FROM candidate_set WHERE ticker LIKE 'SRLCONC.%';", ct);
        await ExecAsync("DELETE FROM security_daily WHERE ticker LIKE 'SRLCONC.%';", ct);
    }

    private static async Task ClearDriftAsync(DateOnly on, CancellationToken ct)
    {
        await ExecAsync("DELETE FROM candidate_set WHERE date = @d;", ct, ("d", on));
        await ExecAsync("DELETE FROM security_daily WHERE ticker LIKE 'SRLDRIFT.%';", ct);
        await ExecAsync("DELETE FROM security WHERE ticker LIKE 'SRLDRIFT.%';", ct);
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

    private static async Task<double> ShareAsync(string sql, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(sql, conn);

        return Convert.ToDouble(
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> AlertTypesAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT alert_type FROM alert WHERE date = @d ORDER BY alert_type COLLATE \"C\";", conn);

        cmd.Parameters.AddWithValue("d", RunDate);

        var found = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

        while (await reader.ReadAsync(ct).ConfigureAwait(true))
        {
            found.Add(reader.GetString(0));
        }

        return found;
    }

    private static async Task<string> DetailAsync(string type, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT detail FROM alert WHERE date = @d AND alert_type = @t;", conn);

        cmd.Parameters.AddWithValue("d", RunDate);
        cmd.Parameters.AddWithValue("t", type);

        return (string) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    /// <summary>Per-file, as every other stage fixture in this suite keeps its own.</summary>
    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }
}
