using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Data.Eodhd;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// 3.14's three seams, written the way 3.13's two were and for the same reason: the range
/// path and the nightly path have to agree on every date, and where they do not, nothing
/// errors.
///
/// **Every range here spans more than one date, and that is the lesson 3.13 paid for.**
/// C09's first seam test used a single-date range and proved nothing, because the
/// statement that fetched the inputs already narrowed on the range end, so the per-date
/// work was redundant on a range whose only date was that end. A one-date range tests the
/// bounds of the fetch and not the loop over dates.
/// </summary>
internal static class Seam
{
    /// <summary>
    /// After `ConfigSeeder.SeedInstant` so config resolves, and before
    /// `backfill.window_start` so nothing here can be read as a real backfill row.
    /// </summary>
    public static readonly DateOnly From = new(2020, 9, 1);

    public static readonly DateOnly To = new(2020, 9, 5);

    public static IReadOnlyList<DateOnly> Dates()
    {
        var dates = new List<DateOnly>();
        for (var d = From; d <= To; d = d.AddDays(1)) dates.Add(d);
        return dates;
    }

    /// <summary>
    /// A range execution through `BackfillRun`, which is the only route to a
    /// `BackfillContext` and therefore the only way to reach `ExecuteRangeAsync` as the
    /// worker reaches it [D-93]. The allowance is constructed and never consulted; the
    /// handler fails the test if a compute stage reaches the provider.
    /// </summary>
    public static async Task RunRangeAsync(IStage stage, CancellationToken ct)
    {
        var run = new BackfillRun(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            new FrozenClock(To),
            TestDatabase.ConnectionString,
            new UnitAllowance(new EodhdClient(
                new HttpClient(new NoProviderCall()) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
                "test-token", new FrozenClock(To))));

        var result = await run.RunAsync(stage.Name, From, To, ct).ConfigureAwait(false);

        Assert.False(result.WasHalted, result.Detail ?? "halted with no detail");
    }

    public static async Task RunNightlyAsync(IStage stage, DateOnly date, CancellationToken ct)
    {
        var config = new ConfigStore(TestDatabase.ConnectionString);

        var context = new StageContext(
            date,
            await config.RequireVersionAsync(date, ct).ConfigureAwait(false),
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(date),
            config);

        await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rows for one date, every column after the key, ordinal by the first column.
    /// Arrays and json come back as reference types, so they are flattened to text: two
    /// equal arrays are different references and an equality over them would fail on
    /// agreement rather than on disagreement.
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyList<object?>>> RowsAsync(
        string sql, DateOnly date, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("d", date);

        var rows = new List<IReadOnlyList<object?>>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new List<object?>(r.FieldCount);

            for (var i = 0; i < r.FieldCount; i++)
            {
                if (await r.IsDBNullAsync(i, ct).ConfigureAwait(false))
                {
                    values.Add(null);
                    continue;
                }

                var v = r.GetValue(i);
                values.Add(v is Array a
                    ? string.Join(",", a.Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)))
                    : v);
            }

            rows.Add(values);
        }

        return rows;
    }

    public static async Task ExecuteAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The benchmark series `market.breadth_ma_days` needs behind the first date. Written
    /// with ON CONFLICT DO NOTHING, so a series another fixture put there stands: both
    /// paths read the same rows and a shared benchmark moves them equally.
    /// </summary>
    public static Task BenchmarkAsync(CancellationToken ct)
        => ExecuteAsync(
            """
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            SELECT @t, d::date, c, c + 1, c - 1, c, c, 900000
            FROM (
                SELECT d, 300::numeric + ((d::date - DATE '2019-06-01')::numeric * 0.05) AS c
                FROM generate_series(DATE '2019-06-01', DATE '2020-09-30', INTERVAL '1 day') AS d
            ) priced
            ON CONFLICT (ticker, date) DO NOTHING;
            """,
            c => c.Parameters.AddWithValue("t", IndicatorEngine.Benchmark), ct);

    public sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow => new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        public DateOnly Today => today;
    }

    public sealed class NoProviderCall : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new NotSupportedException("A compute stage reached the provider at " + request.RequestUri);
    }
}

/// <summary>
/// C35's seam. The nightly path reads one ninety-day window per date; the range path reads
/// a ticker's whole history once and hands the same `Compute` every date.
/// </summary>
[Collection("database")]
public sealed class SentimentRangeSeamTests
{
    private const string Prefix = "SRLSNS";

    private static readonly string[] Names = [Prefix + "1.US", Prefix + "2.US", Prefix + "3.US"];

    private const string Query =
        "SELECT ticker, article_count_z_own_90d, sentiment_delta_7v30, sentiment_7d_level " +
        "FROM sentiment_derived_daily WHERE date = @d AND ticker LIKE 'SRLSNS%' ORDER BY ticker;";

    /// <summary>
    /// **The anchor**, over five dates rather than one.
    /// </summary>
    [Fact]
    public async Task TheRangePathWritesTheSameRowsAsTheNightlyPathOnEveryDateOfARange()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var stage = new SentimentEngine();
        var nightly = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyList<object?>>>();

        foreach (var date in Seam.Dates())
        {
            await ClearAsync(date, ct).ConfigureAwait(true);
            await Seam.RunNightlyAsync(stage, date, ct).ConfigureAwait(true);
            nightly[date] = await Seam.RowsAsync(Query, date, ct).ConfigureAwait(true);
            await ClearAsync(date, ct).ConfigureAwait(true);
        }

        await Seam.RunRangeAsync(stage, ct).ConfigureAwait(true);

        foreach (var date in Seam.Dates())
        {
            var range = await Seam.RowsAsync(Query, date, ct).ConfigureAwait(true);

            Assert.Equal(Names.Length, nightly[date].Count);

            // Not rows of nulls compared with rows of nulls, which any wrong window also
            // satisfies. The fixture seeds enough days to clear the baseline floor.
            Assert.Contains(nightly[date], r => r.Skip(1).Any(v => v is not null));

            Assert.Equal(nightly[date].Count, range.Count);

            for (var i = 0; i < range.Count; i++)
            {
                Assert.Equal(nightly[date][i], range[i]);
            }
        }
    }

    /// <summary>
    /// **The property the range path rests on, stated where it lives.**
    ///
    /// C08 has to slice a bar window before calling `Compute` because its arithmetic
    /// consumes whatever list it is handed. `Compute` here derives all three of its windows
    /// from the date and then reads a dictionary, so the range path passes a ticker's whole
    /// history unsliced. That is only safe if days outside those windows are ignored on
    /// **both** sides: a day before the baseline must not enter the z-score, and a day
    /// after the date must not enter anything at all.
    ///
    /// The second half is the one that would be silent and expensive. A future day reaching
    /// the seven-day mean makes a backfilled sentiment level prescient, and the row it
    /// lands in reads like every other row.
    /// </summary>
    [Fact]
    public void ComputeIgnoresDaysOutsideItsOwnWindows()
    {
        var at = new DateOnly(2021, 6, 30);

        var inside = new List<SentimentEngine.Day>();
        for (var i = 0; i < 100; i++)
        {
            inside.Add(new SentimentEngine.Day(at.AddDays(-i), 3 + (i % 5), 0.1f * (i % 7)));
        }

        var padded = new List<SentimentEngine.Day>(inside);

        // Well before the ninety-day baseline, and after the date. Extreme values, so a
        // window that reached either way could not produce the same answer by luck.
        for (var i = 1; i <= 20; i++)
        {
            padded.Add(new SentimentEngine.Day(at.AddDays(-100 - i), 9_000, 9f));
            padded.Add(new SentimentEngine.Day(at.AddDays(i), 9_000, -9f));
        }

        var bare = SentimentEngine.Compute("SRLSNS.US", at, inside, 20);
        var wide = SentimentEngine.Compute("SRLSNS.US", at, padded, 20);

        Assert.True(bare.ClearedBaselineFloor);
        Assert.NotNull(bare.ArticleCountZOwn90D);

        Assert.Equal(bare.ArticleCountZOwn90D, wide.ArticleCountZOwn90D);
        Assert.Equal(bare.SentimentDelta7V30, wide.SentimentDelta7V30);
        Assert.Equal(bare.Sentiment7DLevel, wide.Sentiment7DLevel);
        Assert.Equal(bare.ClearedBaselineFloor, wide.ClearedBaselineFloor);
    }

    private static Task ClearAsync(DateOnly date, CancellationToken ct)
        => Seam.ExecuteAsync(
            "DELETE FROM sentiment_derived_daily WHERE date = @d AND ticker LIKE @p;",
            c =>
            {
                c.Parameters.AddWithValue("d", date);
                c.Parameters.AddWithValue("p", Prefix + "%");
            }, ct);

    /// <summary>
    /// Three members with 150 days of sentiment behind the range, which clears
    /// `sentiment.min_baseline_days` and leaves every metric computable. The counts and
    /// scores move, because a flat series satisfies any window claim including a wrong one.
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM sentiment_derived_daily WHERE ticker LIKE @p",
                     "DELETE FROM sentiment_daily WHERE ticker LIKE @p",
                     "DELETE FROM security_daily WHERE ticker LIKE @p",
                     "DELETE FROM price_daily WHERE ticker LIKE @p",
                 })
        {
            await Seam.ExecuteAsync(sql, c => c.Parameters.AddWithValue("p", Prefix + "%"), ct)
                .ConfigureAwait(false);
        }

        // The range's dates come from `price_daily`, so one member carries the sessions.
        await Seam.ExecuteAsync(
            """
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            SELECT @t, d::date, 10, 11, 9, 10, 10, 100000
            FROM generate_series(@f::date, @s::date, INTERVAL '1 day') AS d
            ON CONFLICT (ticker, date) DO NOTHING;
            """,
            c =>
            {
                c.Parameters.AddWithValue("t", Names[0]);
                c.Parameters.AddWithValue("f", Seam.From);
                c.Parameters.AddWithValue("s", Seam.To);
            }, ct).ConfigureAwait(false);

        for (var n = 0; n < Names.Length; n++)
        {
            await Seam.ExecuteAsync(
                """
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, 'SRLTEST-SNS', 'SRLTEST-SNS', 1000000000, true)
                ON CONFLICT (ticker, date) DO UPDATE SET is_active = true;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", Names[n]);
                    c.Parameters.AddWithValue("d", Seam.From.AddDays(-14));
                }, ct).ConfigureAwait(false);

            await Seam.ExecuteAsync(
                """
                INSERT INTO sentiment_daily (ticker, date, article_count, sentiment_score)
                SELECT @t, d::date,
                       (3 + ((d::date - DATE '2020-01-01') % 7) + @n)::int,
                       (sin((d::date - DATE '2020-01-01')::numeric / (5 + @n)) * 0.4)::real
                FROM generate_series(@f::date, @s::date, INTERVAL '1 day') AS d
                ON CONFLICT (ticker, date) DO NOTHING;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", Names[n]);
                    c.Parameters.AddWithValue("n", n);
                    c.Parameters.AddWithValue("f", Seam.From.AddDays(-150));
                    c.Parameters.AddWithValue("s", Seam.To);
                }, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// C34's seam. One statement per date either way, so what this pins is that the range loop
/// resolves the same config and the same bounds the nightly call would have.
/// </summary>
[Collection("database")]
public sealed class FlowRangeSeamTests
{
    private const string Prefix = "SRLFLS";

    private const string Query =
        "SELECT ticker, insider_net_90d_usd, distinct_buyer_count, inst_ownership_change " +
        "FROM flow_daily WHERE date = @d AND ticker LIKE 'SRLFLS%' ORDER BY ticker;";

    [Fact]
    public async Task TheRangePathWritesTheSameRowsAsTheNightlyPathOnEveryDateOfARange()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var stage = new FlowEngine();
        var nightly = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyList<object?>>>();

        foreach (var date in Seam.Dates())
        {
            await ClearAsync(date, ct).ConfigureAwait(true);
            await Seam.RunNightlyAsync(stage, date, ct).ConfigureAwait(true);
            nightly[date] = await Seam.RowsAsync(Query, date, ct).ConfigureAwait(true);
            await ClearAsync(date, ct).ConfigureAwait(true);
        }

        await Seam.RunRangeAsync(stage, ct).ConfigureAwait(true);

        foreach (var date in Seam.Dates())
        {
            var range = await Seam.RowsAsync(Query, date, ct).ConfigureAwait(true);

            Assert.NotEmpty(nightly[date]);
            Assert.Contains(nightly[date], r => r.Skip(1).Any(v => v is not null));
            Assert.Equal(nightly[date].Count, range.Count);

            for (var i = 0; i < range.Count; i++)
            {
                Assert.Equal(nightly[date][i], range[i]);
            }
        }
    }

    /// <summary>
    /// **A filing becomes visible mid-range, and both paths must see it on the same
    /// date** [INVARIANT 12's shape applied to flow].
    ///
    /// `filed_at` is when the filing became public and `transaction_date` is when the
    /// insider traded. The fixture files one purchase on the range's third date for a
    /// trade made before the range opened, so a path windowing on `transaction_date`
    /// alone has it from the first date. The row therefore moves on that date and moves
    /// the same way on both paths.
    /// </summary>
    [Fact]
    public async Task NeitherPathSeesAFilingBeforeItWasFiled()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var stage = new FlowEngine();
        var filed = Seam.From.AddDays(2);

        foreach (var date in Seam.Dates())
        {
            await ClearAsync(date, ct).ConfigureAwait(true);
        }

        await Seam.RunRangeAsync(stage, ct).ConfigureAwait(true);

        var before = await Seam.RowsAsync(Query, filed.AddDays(-1), ct).ConfigureAwait(true);
        var on = await Seam.RowsAsync(Query, filed, ct).ConfigureAwait(true);

        Assert.NotEmpty(before);
        Assert.NotEmpty(on);
        Assert.NotEqual(before, on);

        // And the nightly path agrees on both, which is what makes the move a seam
        // statement rather than a statement about the range loop alone.
        foreach (var date in new[] { filed.AddDays(-1), filed })
        {
            await ClearAsync(date, ct).ConfigureAwait(true);
            await Seam.RunNightlyAsync(stage, date, ct).ConfigureAwait(true);
        }

        Assert.Equal(before, await Seam.RowsAsync(Query, filed.AddDays(-1), ct).ConfigureAwait(true));
        Assert.Equal(on, await Seam.RowsAsync(Query, filed, ct).ConfigureAwait(true));
    }

    private static Task ClearAsync(DateOnly date, CancellationToken ct)
        => Seam.ExecuteAsync(
            "DELETE FROM flow_daily WHERE date = @d AND ticker LIKE @p;",
            c =>
            {
                c.Parameters.AddWithValue("d", date);
                c.Parameters.AddWithValue("p", Prefix + "%");
            }, ct);

    /// <summary>
    /// Two names with open-market purchases inside the ninety-day window, one of which is
    /// filed on the range's third date, and two institutional report dates far enough back
    /// to clear `flow.institutional_report_lag_days`.
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM flow_daily WHERE ticker LIKE @p",
                     "DELETE FROM insider_transaction WHERE ticker LIKE @p",
                     "DELETE FROM institutional_holding WHERE ticker LIKE @p",
                     "DELETE FROM price_daily WHERE ticker LIKE @p",
                 })
        {
            await Seam.ExecuteAsync(sql, c => c.Parameters.AddWithValue("p", Prefix + "%"), ct)
                .ConfigureAwait(false);
        }

        await Seam.ExecuteAsync(
            """
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            SELECT @t, d::date, 10, 11, 9, 10, 10, 100000
            FROM generate_series(@f::date, @s::date, INTERVAL '1 day') AS d
            ON CONFLICT (ticker, date) DO NOTHING;
            """,
            c =>
            {
                c.Parameters.AddWithValue("t", Prefix + "1.US");
                c.Parameters.AddWithValue("f", Seam.From);
                c.Parameters.AddWithValue("s", Seam.To);
            }, ct).ConfigureAwait(false);

        // Visible before the range opens.
        await InsiderAsync(Prefix + "1.US", "EARLY", Seam.From.AddDays(-30), Seam.From.AddDays(-20), 5000m, ct)
            .ConfigureAwait(false);

        // Traded before the range and filed on its third date, which is the case the
        // point-in-time test turns on.
        await InsiderAsync(Prefix + "1.US", "LATEFILE", Seam.From.AddDays(-10), Seam.From.AddDays(2), 7000m, ct)
            .ConfigureAwait(false);

        await InsiderAsync(Prefix + "2.US", "OTHER", Seam.From.AddDays(-40), Seam.From.AddDays(-35), 2500m, ct)
            .ConfigureAwait(false);

        // **Inside the ninety-day window and outside any shorter one.** Without a trade
        // here the fixture cannot tell `WindowDays` from half of it, and a range path
        // passing the wrong window would produce identical rows. Found by mutation: the
        // first version of this fixture put every trade within forty-five days and passed
        // with the range path's window halved.
        await InsiderAsync(Prefix + "1.US", "OLDBUY", Seam.From.AddDays(-70), Seam.From.AddDays(-65), 11000m, ct)
            .ConfigureAwait(false);

        foreach (var (ticker, report, shares) in new[]
                 {
                     (Prefix + "1.US", Seam.From.AddDays(-400), 1000L),
                     (Prefix + "1.US", Seam.From.AddDays(-220), 1400L),
                 })
        {
            await Seam.ExecuteAsync(
                """
                INSERT INTO institutional_holding (ticker, report_date, holder_name, shares)
                VALUES (@t, @r, 'SRLFLS Holder', @s)
                ON CONFLICT (ticker, report_date, holder_name) DO NOTHING;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", ticker);
                    c.Parameters.AddWithValue("r", report);
                    c.Parameters.AddWithValue("s", shares);
                }, ct).ConfigureAwait(false);
        }
    }

    private static Task InsiderAsync(
        string ticker, string tag, DateOnly traded, DateOnly filed, decimal usd, CancellationToken ct)
        => Seam.ExecuteAsync(
            """
            INSERT INTO insider_transaction
                (ticker, accession_number, transaction_side, transaction_ordinal,
                 filed_at, transaction_date, reporting_owner_cik, reporting_owner_name,
                 transaction_code, security_title, shares_amount, price_per_share,
                 total_value, shares_owned_after, acquired_or_disposed)
            VALUES (@t, @a, 'non_derivative', 0, @f, @x, @cik, 'Owner', 'P', 'Common',
                    100, 10, @v, 1000, 'A')
            ON CONFLICT (ticker, accession_number, transaction_side, transaction_ordinal) DO NOTHING;
            """,
            c =>
            {
                c.Parameters.AddWithValue("t", ticker);
                c.Parameters.AddWithValue("a", "SRLFLS-" + tag);
                c.Parameters.AddWithValue("f", filed);
                c.Parameters.AddWithValue("x", traded);
                c.Parameters.AddWithValue("cik", "CIK-" + tag);
                c.Parameters.AddWithValue("v", usd);
            }, ct);
}

/// <summary>
/// C10's seam. One row per date and no ticker, so the comparison is of the whole row and
/// the fixture has to make the regime label decidable rather than unknown.
/// </summary>
[Collection("database")]
public sealed class MarketContextRangeSeamTests
{
    private const string Prefix = "SRLMKT";

    private const string Query =
        "SELECT breadth, vix, regime_label, sector_relative_strength::text " +
        "FROM market_context_daily WHERE date = @d;";

    [Fact]
    public async Task TheRangePathWritesTheSameRowAsTheNightlyPathOnEveryDateOfARange()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var stage = new MarketContextEngine();
        var nightly = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyList<object?>>>();

        foreach (var date in Seam.Dates())
        {
            await ClearAsync(date, ct).ConfigureAwait(true);
            await Seam.RunNightlyAsync(stage, date, ct).ConfigureAwait(true);
            nightly[date] = await Seam.RowsAsync(Query, date, ct).ConfigureAwait(true);
            await ClearAsync(date, ct).ConfigureAwait(true);
        }

        await Seam.RunRangeAsync(stage, ct).ConfigureAwait(true);

        foreach (var date in Seam.Dates())
        {
            var row = Assert.Single(nightly[date]);

            // Breadth non-null is what makes the regime label a decision rather than the
            // `mixed` fallback, which every wrong read would also produce.
            Assert.NotNull(row[0]);

            Assert.Equal(nightly[date], await Seam.RowsAsync(Query, date, ct).ConfigureAwait(true));
        }

        // **Every date's breadth differs from every other date's**, which is what makes
        // the equality above a statement about per-date work rather than about a constant.
        // Without it a range path computing one date's breadth and writing it to all of
        // them passes, and that was observed rather than imagined.
        var breadths = Seam.Dates().Select(d => nightly[d][0][0]).ToList();
        Assert.Equal(breadths.Count, breadths.Distinct().Count());
    }

    private static Task ClearAsync(DateOnly date, CancellationToken ct)
        => Seam.ExecuteAsync(
            "DELETE FROM market_context_daily WHERE date = @d;",
            c => c.Parameters.AddWithValue("d", date), ct);

    /// <summary>
    /// Six members with an `indicator_daily` row per date carrying `dist_200dma`, and the
    /// benchmark's two hundred bars. Six because `market.sector_composite_min_members` is
    /// five and the sector block is otherwise empty, which would leave the json equal
    /// between paths for the wrong reason.
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM indicator_daily WHERE ticker LIKE @p",
                     "DELETE FROM security_daily WHERE ticker LIKE @p",
                     "DELETE FROM price_daily WHERE ticker LIKE @p",
                 })
        {
            await Seam.ExecuteAsync(sql, c => c.Parameters.AddWithValue("p", Prefix + "%"), ct)
                .ConfigureAwait(false);
        }

        await Seam.BenchmarkAsync(ct).ConfigureAwait(false);

        for (var n = 1; n <= 6; n++)
        {
            var ticker = Prefix + n.ToString("00", CultureInfo.InvariantCulture) + ".US";

            await Seam.ExecuteAsync(
                """
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, 'SRLTEST-MKT', 'SRLTEST-MKT', 1000000000, true)
                ON CONFLICT (ticker, date) DO UPDATE SET is_active = true, sector = EXCLUDED.sector;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", ticker);
                    c.Parameters.AddWithValue("d", Seam.From.AddDays(-21));
                }, ct).ConfigureAwait(false);

            await Seam.ExecuteAsync(
                """
                INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
                SELECT @t, d::date, c, c + 1, c - 1, c, c, 400000
                FROM (
                    SELECT d, (50 + @n)::numeric + ((d::date - DATE '2020-01-01')::numeric * 0.02) AS c
                    FROM generate_series(@f::date, @s::date, INTERVAL '1 day') AS d
                ) priced
                ON CONFLICT (ticker, date) DO NOTHING;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", ticker);
                    c.Parameters.AddWithValue("n", n);
                    c.Parameters.AddWithValue("f", Seam.From.AddDays(-120));
                    c.Parameters.AddWithValue("s", Seam.To);
                }, ct).ConfigureAwait(false);

            // **Breadth has to differ on every date of the range, and that is the whole
            // point of this shape.** A name goes above its average on the nth date, so the
            // count above runs 0, 1, 2, 3, 4 of six across the five dates and no two dates
            // share a breadth. Found by mutation: the first version of this fixture wrote a
            // constant per ticker, so every date had the same breadth and a range path
            // computing every date's breadth at the range end passed.
            await Seam.ExecuteAsync(
                """
                INSERT INTO indicator_daily (ticker, date, dist_200dma, rs_change_63d)
                SELECT @t, d::date,
                       CASE WHEN (d::date - @f::date) >= @n THEN 0.08 ELSE -0.05 END,
                       @rs
                FROM generate_series(@f::date, @s::date, INTERVAL '1 day') AS d
                ON CONFLICT (ticker, date) DO UPDATE SET
                    dist_200dma = EXCLUDED.dist_200dma, rs_change_63d = EXCLUDED.rs_change_63d;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", ticker);
                    c.Parameters.AddWithValue("n", n);
                    c.Parameters.AddWithValue("rs", 0.01f * n);
                    c.Parameters.AddWithValue("f", Seam.From);
                    c.Parameters.AddWithValue("s", Seam.To);
                }, ct).ConfigureAwait(false);
        }
    }
}
