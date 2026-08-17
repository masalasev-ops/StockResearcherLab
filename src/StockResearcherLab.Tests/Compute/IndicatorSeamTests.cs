using System.Globalization;
using System.Net;
using System.Text;
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
/// C08's seam: the range path and the nightly path compute one date the same way.
///
/// **This is 3.13's done-when and open item 33's last piece.** The range path reads one
/// deeper series per ticker and slices a window out of it in memory; the nightly path
/// asks the store for that window directly. Everything after the slice is the same public
/// `Compute`, so if the two windows agree the two outputs agree, and if they do not
/// nothing errors: a window one bar short nulls the columns needing 272 bars, and a
/// window reaching one bar past the date computes a metric out of a price nobody could
/// have seen on it.
///
/// **The output alone is not enough and that is why the window is asserted too.** Two
/// paths wrong the same way produce equal rows, and an equality test over them looks
/// exactly like a passing one. So the depth is pinned independently at
/// `IndicatorEngine.RequiredBars`, which is 272 because `dist_52w_high_20d_change` needs a
/// 52-week high and another twenty trading dates behind it: exactly that many bars gives
/// the column a value and one fewer gives it null, on **both** paths. That number comes
/// from the fixture's own bar count rather than from either path.
/// </summary>
[Collection("database")]
public sealed class IndicatorSeamTests
{
    private const string Prefix = "SRLSEAM";

    /// <summary>
    /// The date computed. After `ConfigSeeder.SeedInstant` so config resolves for it, and
    /// with bars seeded past it so a window reaching forward is observable rather than
    /// merely absent.
    /// </summary>
    private static readonly DateOnly At = new(2020, 6, 30);

    private static readonly string[] Names = [Prefix + "1.US", Prefix + "2.US", Prefix + "3.US"];

    /// <summary>
    /// The full-history case: deep enough that every column has its window, and carrying
    /// bars after <see cref="At"/> that neither path may reach.
    /// </summary>
    private const int DeepHistory = 420;

    private const int BarsPastTheDate = 15;

    /// <summary>
    /// **The anchor.** One date computed both ways, all seventeen columns compared.
    /// </summary>
    [Fact]
    public async Task TheRangePathWritesTheSameRowsAsTheNightlyPathForOneDate()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(DeepHistory, ct).ConfigureAwait(true);

        await RunNightlyAsync(ct).ConfigureAwait(true);
        var nightly = await RowsAsync(ct).ConfigureAwait(true);

        Assert.Equal(Names.Length, nightly.Count);

        // Not two rows of nulls compared to each other, which every wrong window would
        // also satisfy.
        Assert.Contains(nightly, r => r.Values.Any(v => v is not null));

        await ClearOutputAsync(ct).ConfigureAwait(true);

        await RunRangeAsync(ct).ConfigureAwait(true);
        var range = await RowsAsync(ct).ConfigureAwait(true);

        Assert.Equal(nightly.Count, range.Count);

        for (var i = 0; i < nightly.Count; i++)
        {
            Assert.Equal(nightly[i].Ticker, range[i].Ticker);
            Assert.Equal(nightly[i].Values, range[i].Values);
        }
    }

    /// <summary>
    /// **The window, pinned at the bar count that decides it, on both paths.**
    ///
    /// `dist_52w_high_20d_change` needs `RequiredBars` bars and no fewer. Seeded with
    /// exactly that many at or before the date it has a value; seeded with one fewer it is
    /// null. If either path took a window a bar shallower or a bar deeper than the
    /// statement's, one of these four readings moves, and the equality test above would
    /// still pass.
    /// </summary>
    [Fact]
    public async Task TheBindingColumnTurnsNullAtTheSameBarCountOnBothPaths()
    {
        var ct = TestContext.Current.CancellationToken;

        await ResetAsync(IndicatorEngine.RequiredBars, ct).ConfigureAwait(true);

        await RunNightlyAsync(ct).ConfigureAwait(true);
        Assert.NotNull(await BindingColumnAsync(ct).ConfigureAwait(true));

        await ClearOutputAsync(ct).ConfigureAwait(true);
        await RunRangeAsync(ct).ConfigureAwait(true);
        Assert.NotNull(await BindingColumnAsync(ct).ConfigureAwait(true));

        // One bar shallower. The same date, the same everything else.
        await ResetAsync(IndicatorEngine.RequiredBars - 1, ct).ConfigureAwait(true);

        await RunNightlyAsync(ct).ConfigureAwait(true);
        Assert.Null(await BindingColumnAsync(ct).ConfigureAwait(true));

        await ClearOutputAsync(ct).ConfigureAwait(true);
        await RunRangeAsync(ct).ConfigureAwait(true);
        Assert.Null(await BindingColumnAsync(ct).ConfigureAwait(true));
    }

    // ----------------------------------------------------------- harness ---

    private sealed record Snapshot(string Ticker, IReadOnlyList<object?> Values);

    private static async Task RunNightlyAsync(CancellationToken ct)
    {
        var stage = new IndicatorEngine();
        var config = new ConfigStore(TestDatabase.ConnectionString);

        var context = new StageContext(
            At,
            await config.RequireVersionAsync(At, ct).ConfigureAwait(false),
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(At),
            config);

        await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The range path through `BackfillRun`, which is the only route to a
    /// `BackfillContext` and therefore the only way to reach `ExecuteRangeAsync` as the
    /// worker reaches it [D-93].
    ///
    /// The allowance is constructed and never consulted: a compute stage makes no
    /// provider call, and a double that would fail if one were made is the assertion.
    /// </summary>
    private static async Task RunRangeAsync(CancellationToken ct)
    {
        var stage = new IndicatorEngine();

        var run = new BackfillRun(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            new FrozenClock(At),
            TestDatabase.ConnectionString,
            new UnitAllowance(new EodhdClient(
                new HttpClient(new NoProviderCall()) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
                "test-token", new FrozenClock(At))));

        var result = await run.RunAsync(stage.Name, At, At, ct).ConfigureAwait(false);

        Assert.False(result.WasHalted, result.Detail ?? "halted with no detail");
    }

    /// <summary>This fixture's rows for the date, ordinal by ticker, every column in order.</summary>
    private static async Task<IReadOnlyList<Snapshot>> RowsAsync(CancellationToken ct)
    {
        var columns = string.Join(", ", IndicatorEngine.Columns.Skip(2));

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT ticker, {columns} FROM indicator_daily WHERE date = @d AND ticker LIKE @p;", conn);
        cmd.Parameters.AddWithValue("d", At);
        cmd.Parameters.AddWithValue("p", Prefix + "%");

        var rows = new List<Snapshot>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new List<object?>(r.FieldCount - 1);
            for (var i = 1; i < r.FieldCount; i++)
            {
                values.Add(await r.IsDBNullAsync(i, ct).ConfigureAwait(false) ? null : r.GetValue(i));
            }

            rows.Add(new Snapshot(r.GetString(0), values));
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.Ticker, b.Ticker));
        return rows;
    }

    /// <summary>`dist_52w_high_20d_change` for the first fixture name, which is the column the depth decides.</summary>
    private static async Task<object?> BindingColumnAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT dist_52w_high_20d_change FROM indicator_daily WHERE ticker = @t AND date = @d;", conn);
        cmd.Parameters.AddWithValue("t", Names[0]);
        cmd.Parameters.AddWithValue("d", At);

        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

        // No row at all is a broken fixture rather than a null column, and the two would
        // otherwise be the same reading.
        Assert.True(value is not null, $"No indicator_daily row for {Names[0]} on {At:yyyy-MM-dd}.");

        return value is DBNull ? null : value;
    }

    private static async Task ClearOutputAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM indicator_daily WHERE date = @d AND ticker LIKE @p;", conn);
        cmd.Parameters.AddWithValue("d", At);
        cmd.Parameters.AddWithValue("p", Prefix + "%");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Three names with <paramref name="depth"/> bars at or before the date and
    /// <see cref="BarsPastTheDate"/> after it, a benchmark series, and membership.
    ///
    /// **Bars after the date are the point of that number.** A window that reached forward
    /// would be invisible against a fixture ending on the date, and it is the mistake that
    /// costs the most: a metric computed from a price nobody could have seen makes a
    /// backfilled screen look prescient and nothing about the row says so.
    ///
    /// The series moves rather than being flat, because a flat series satisfies any window
    /// claim including a wrong one.
    /// </summary>
    private static async Task ResetAsync(int depth, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        foreach (var sql in new[]
                 {
                     "DELETE FROM indicator_daily WHERE ticker LIKE @p",
                     "DELETE FROM price_daily WHERE ticker LIKE @p",
                     "DELETE FROM security_daily WHERE ticker LIKE @p",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", Prefix + "%");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var n = 0; n < Names.Length; n++)
        {
            await SeriesAsync(conn, Names[n], depth, 40m + (n * 13m), 1 + n, ct).ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, 'SRLTEST-SEAM', 'SRLTEST-SEAM', 1000000000, true)
                ON CONFLICT (ticker, date) DO UPDATE SET is_active = true, sector = EXCLUDED.sector;
                """, conn);
            cmd.Parameters.AddWithValue("t", Names[n]);
            cmd.Parameters.AddWithValue("d", At.AddDays(-30));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // The benchmark, whatever else is in the table under that name. Both paths read
        // the same rows, so a series another fixture put there moves both equally.
        await SeriesAsync(conn, IndicatorEngine.Benchmark, DeepHistory, 300m, 7, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="depth"/> bars ending on the date, plus a tail after it. Prices
    /// follow a sine so the series has shape, and the tail is deliberately far from the
    /// body so a window that reached into it would move every column at once.
    /// </summary>
    private static async Task SeriesAsync(
        NpgsqlConnection conn, string ticker, int depth, decimal basePrice, int seed, CancellationToken ct)
    {
        for (var i = 0; i < depth + BarsPastTheDate; i++)
        {
            var date = At.AddDays(i - depth + 1);
            var past = date > At;

            var close = basePrice
                + (decimal) (Math.Sin((i + seed) / 9.0) * 6)
                + (i * 0.05m)
                + (past ? 500m : 0m);

            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
                VALUES (@t, @d, @c, @h, @l, @c, @c, @v)
                ON CONFLICT (ticker, date) DO NOTHING;
                """, conn);

            cmd.Parameters.AddWithValue("t", ticker);
            cmd.Parameters.AddWithValue("d", date);
            cmd.Parameters.AddWithValue("c", close);
            cmd.Parameters.AddWithValue("h", close + 1.5m);
            cmd.Parameters.AddWithValue("l", close - 1.5m);
            cmd.Parameters.AddWithValue("v", 250_000L + (i * 37L));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow => new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        public DateOnly Today => today;
    }

    /// <summary>A compute stage makes no provider call, and this fails the test if one is made.</summary>
    private sealed class NoProviderCall : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new NotSupportedException(
                "A compute stage reached the provider at " + request.RequestUri);
    }
}
