using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// 2.5's third done-when, which was that <c>median_dollar_volume_20d</c> agrees with
/// C01 for the same ticker and date. It was asserted by construction rather than by a
/// test, and the construction had a defect the first real run found.
///
/// **`percentile_cont` is defined on `double precision` and on `interval`.** Given a
/// `numeric` argument Postgres casts it and returns a double, so C08's read back into
/// `decimal?` threw `InvalidCastException` the first time the stage ran against the
/// store. Nothing before this pointed at it: C01 compares the value inside SQL and
/// never sees its type, and every C08 test to that point handed
/// `medianDollarVolume` straight to <c>Compute</c> rather than going through the
/// expression that produces it.
///
/// So the shared expression is exercised here through both of its readers, at a date
/// no real price series reaches.
/// </summary>
[Collection("database")]
public sealed class DollarVolumeTests
{
    private const string Ticker = "SRLTEST.MDV";

    private static readonly DateOnly RunDate = new(2001, 6, 20);

    /// <summary>
    /// Twenty bars, closing at 10 with volume 100 through 2,000 in steps of 100, so the
    /// dollar volumes are 1,000 through 20,000 in steps of 1,000.
    ///
    /// `percentile_cont` interpolates rather than picking a row, so over an even count
    /// it returns the mean of the tenth and eleventh: 10,000 and 11,000, giving 10,500.
    /// `percentile_disc` would return 10,000, which is why the choice between them is a
    /// definition rather than a detail.
    /// </summary>
    private const decimal ExpectedMedian = 10_500m;

    [Fact]
    public async Task TheColumnC08WritesIsTheNumberC01SFloorReads()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedAsync(ct).ConfigureAwait(true);
        await RunIndicatorEngineAsync(ct).ConfigureAwait(true);

        var written = await WrittenAsync(ct).ConfigureAwait(true);

        Assert.Equal(ExpectedMedian, written);

        // The same expression through the shape C01 applies its floor with. One SQL
        // definition read by two components is the point of the shared constant, and
        // two definitions of one number at two cadences is how the universe and the
        // participation cap come to disagree about whether a name is liquid.
        Assert.Equal(ExpectedMedian, await AsC01ReadsItAsync(ct).ConfigureAwait(true));

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The type the expression returns, asserted where a reader can see it. A double
    /// reaching a `numeric` column through an implicit cast is invisible until
    /// something reads it back.
    /// </summary>
    [Fact]
    public async Task TheExpressionReturnsNumericRatherThanDoublePrecision()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT pg_typeof({DollarVolume.MedianExpression})::text
            FROM price_daily
            WHERE ticker = @t AND {DollarVolume.RowFilter};
            """, conn);
        cmd.Parameters.AddWithValue("t", Ticker);

        Assert.Equal("numeric", (string?) await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true));

        await ClearAsync(ct).ConfigureAwait(true);
    }

    // --------------------------------------------------------------- rigging ---

    private static async Task RunIndicatorEngineAsync(CancellationToken ct)
    {
        var stage = new IndicatorEngine();

        var context = new StageContext(
            RunDate, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(RunDate),
            new StubConfig());

        await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
    }

    private static async Task<decimal?> WrittenAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT median_dollar_volume_20d FROM indicator_daily WHERE ticker = @t AND date = @d;",
            conn);
        cmd.Parameters.AddWithValue("t", Ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        Assert.True(await r.ReadAsync(ct).ConfigureAwait(false),
            "C08 wrote no row for the seeded ticker, so there is nothing to compare.");

        return await r.IsDBNullAsync(0, ct).ConfigureAwait(false) ? null : r.GetDecimal(0);
    }

    /// <summary>C01's shape: the last twenty bars per ticker, filtered and aggregated through the shared constant.</summary>
    private static async Task<decimal?> AsC01ReadsItAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"""
            WITH bars AS (
                SELECT ticker, close, volume,
                       row_number() OVER (PARTITION BY ticker ORDER BY date DESC) AS rn
                FROM price_daily
                WHERE date <= @d AND ticker = @t
            )
            SELECT {DollarVolume.MedianExpression}
            FROM bars
            WHERE rn <= {DollarVolume.WindowBars.ToString(CultureInfo.InvariantCulture)}
              AND {DollarVolume.RowFilter}
            GROUP BY ticker;
            """, conn);
        cmd.Parameters.AddWithValue("t", Ticker);
        cmd.Parameters.AddWithValue("d", RunDate);

        return (decimal?) await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task SeedAsync(CancellationToken ct)
    {
        await ClearAsync(ct).ConfigureAwait(false);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand(
            """
            -- `security_daily` from 3.12, dated before any fixture date because the
            -- membership read takes the most recent row at or before it [D-92].
            INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
            VALUES (@t, DATE '2000-01-01', 'SRLTEST-MDV', 'SRLTEST-MDV', 1000000000, true)
            ON CONFLICT (ticker, date) DO UPDATE SET is_active = true;
            """, conn))
        {
            cmd.Parameters.AddWithValue("t", Ticker);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var i = 1; i <= DollarVolume.WindowBars; i++)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
                VALUES (@t, @d, 10, 10, 10, 10, 10, @v)
                ON CONFLICT (ticker, date) DO UPDATE SET volume = EXCLUDED.volume;
                """, conn);
            cmd.Parameters.AddWithValue("t", Ticker);
            cmd.Parameters.AddWithValue("d", RunDate.AddDays(i - DollarVolume.WindowBars));
            cmd.Parameters.AddWithValue("v", (long) i * 100);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        foreach (var sql in new[]
                 {
                     "DELETE FROM indicator_daily WHERE ticker = @t;",
                     "DELETE FROM price_daily WHERE ticker = @t;",
                     "DELETE FROM security_daily WHERE ticker = @t;",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("t", Ticker);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

    /// <summary>
    /// C08 resolves four keys and they are not interchangeable, so this answers per
    /// key rather than returning one number to all of them.
    /// </summary>
    private sealed class StubConfig : IConfigStore
    {
        private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
        {
            ["indicator.wilder_warmup_bars"] = "250",
            ["indicator.base_lookback_days"] = "60",
            ["indicator.base_max_range_pct"] = "0.25",
            ["market.sector_composite_min_members"] = "5",
        };

        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => Task.FromResult<ConfigRow?>(Values.TryGetValue(key, out var v)
                ? new ConfigRow(key, 1, v, new DateOnly(2000, 1, 1))
                : null);

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => await ResolveAsync(key, asOf, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException($"The stage resolved '{key}', which this stub does not know.");

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");
    }
}
