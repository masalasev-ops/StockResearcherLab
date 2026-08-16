using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// The three phase 2 stages whose own write path no test executed, each run here as
/// the runner runs it and read back out of the table it wrote.
///
/// **This exists because two components reached the first real night broken at the
/// write and both had thorough tests.** C08's cast defect and C10's jsonb defect were
/// found by running rather than by reading, and neither is reachable by an arithmetic
/// test: one is the type an aggregate returns, the other is the format byte a binary
/// COPY stream carries. What separates a test that could have caught them from one
/// that could not is whether it calls the stage's own <c>ExecuteAsync</c> against a
/// database. 2.5's correction did, in <see cref="DollarVolumeTests"/>. 2.9's did not,
/// and the phase 2 sign-off review demonstrated the consequence by reverting the fix
/// and watching all 221 tests pass.
///
/// C34 and C11 already had one. C09, C35 and C10 did not, and these are those.
///
/// **The assertions are deliberately thin on arithmetic.** The closed forms belong to
/// <see cref="ValuationEngineTests"/>, <see cref="SentimentEngineTests"/> and
/// <see cref="MarketContextEngineTests"/> and are not restated. What is asserted here
/// is that the stage ran end to end, that every column type survived the write, and
/// that the value read back is the value the stage computed rather than whatever the
/// column happened to hold.
/// </summary>
[Collection("database")]
public sealed class StageWritePathTests
{
    private const string ValuationTicker = "SRLTEST.WPVAL";
    private const string SentimentTicker = "SRLTEST.WPSENT";

    private static readonly DateOnly ValuationDate = new(2001, 7, 11);
    private static readonly DateOnly SentimentDate = new(2001, 10, 3);
    private static readonly DateOnly ContextDate = new(2001, 9, 19);

    // ------------------------------------------------------ C09 ValuationEngine ---

    /// <summary>
    /// C09 over one seeded company, through its own write path.
    ///
    /// The figures divide into round numbers so each expectation is arithmetic rather
    /// than the engine's formula restated: cash from operating 100 a quarter against
    /// capital expenditure 25 gives trailing free cash flow of 300 over a market
    /// capitalisation of 1,000 shares at 2, and enterprise value 2,500 over trailing
    /// EBIT of 200 gives 12.5.
    ///
    /// **`cash_on_hand` carries D-81's shape**: no cash and equivalents line, a
    /// reported cash line of 900 and 100 of short-term investments. The old rule wrote
    /// 100 here. The column is `numeric` and the ratios are `real`, so one row exercises
    /// both, and `last_two_earnings_surprises` is `real[]` written null, which is a
    /// third wire format again.
    /// </summary>
    [Fact]
    public async Task TheValuationEngineWritesThroughItsOwnPathAndTheRowReadsBack()
    {
        var ct = TestContext.Current.CancellationToken;

        await ClearValuationAsync(ct).ConfigureAwait(true);
        await SeedValuationAsync(ct).ConfigureAwait(true);

        var stage = new ValuationEngine();
        var result = await stage.ExecuteAsync(ContextFor(stage, ValuationDate), ct).ConfigureAwait(true);

        Assert.True(result.RowsWritten > 0, "C09 wrote no rows, so there is nothing to read back.");

        await using (var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true))
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT fcf_yield, ev_ebit, cash_on_hand, quarterly_burn_rate,
                       last_two_earnings_surprises IS NULL
                FROM valuation_daily WHERE ticker = @t AND date = @d;
                """, conn);
            cmd.Parameters.AddWithValue("t", ValuationTicker);
            cmd.Parameters.AddWithValue("d", ValuationDate);

            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

            Assert.True(await r.ReadAsync(ct).ConfigureAwait(true),
                "C09 wrote no row for the seeded ticker.");

            Assert.Equal(0.15f, r.GetFloat(0), 5);
            Assert.Equal(12.5f, r.GetFloat(1), 4);

            // Read as a decimal, which is what caught C08's aggregate returning a
            // double. A numeric column read back into the type it is declared in is
            // the assertion, not the value alone.
            Assert.Equal(1000m, r.GetDecimal(2));
            Assert.Equal(0m, r.GetDecimal(3));
            Assert.True(r.GetBoolean(4), "last_two_earnings_surprises should be written null [phase 3].");
        }

        await ClearValuationAsync(ct).ConfigureAwait(true);
    }

    // ------------------------------------------------------- C35 SentimentEngine ---

    /// <summary>
    /// C35 over one seeded name with forty days of coverage, through its own write
    /// path.
    ///
    /// All three columns are `real` and all three are computed here rather than left
    /// null, because a stage that wrote three nulls would read back identically to one
    /// that had not run.
    /// </summary>
    [Fact]
    public async Task TheSentimentEngineWritesThroughItsOwnPathAndTheRowReadsBack()
    {
        var ct = TestContext.Current.CancellationToken;

        await ClearSentimentAsync(ct).ConfigureAwait(true);
        await SeedSentimentAsync(ct).ConfigureAwait(true);

        var stage = new SentimentEngine();
        var result = await stage.ExecuteAsync(ContextFor(stage, SentimentDate), ct).ConfigureAwait(true);

        Assert.True(result.RowsWritten > 0, "C35 wrote no rows, so there is nothing to read back.");

        await using (var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true))
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT article_count_z_own_90d, sentiment_delta_7v30, sentiment_7d_level
                FROM sentiment_derived_daily WHERE ticker = @t AND date = @d;
                """, conn);
            cmd.Parameters.AddWithValue("t", SentimentTicker);
            cmd.Parameters.AddWithValue("d", SentimentDate);

            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

            Assert.True(await r.ReadAsync(ct).ConfigureAwait(true),
                "C35 wrote no row for the seeded ticker.");

            for (var i = 0; i < 3; i++)
            {
                Assert.False(await r.IsDBNullAsync(i, ct).ConfigureAwait(true),
                    $"Column {i} came back null, so the seeded history did not clear the baseline floor.");
            }

            // The tone is 0.25 on every baseline day and 0.75 on the date itself, so
            // the seven-day mean sits above the thirty-day mean by a positive amount.
            Assert.True(r.GetFloat(1) > 0, "sentiment_delta_7v30 should be positive on a rising tone.");
        }

        await ClearSentimentAsync(ct).ConfigureAwait(true);
    }

    // --------------------------------------------------- C10 MarketContextEngine ---

    /// <summary>
    /// C10 through its own write path, which is the one this file exists for.
    ///
    /// **`sector_relative_strength` is `jsonb` and binary COPY carries no type name.**
    /// A string infers `text`, the two formats differ by a leading one-byte version,
    /// and Postgres reads the document's opening brace as that version and refuses the
    /// row with "unsupported jsonb version number 123". An empty object opens with the
    /// same brace, so this needs no seeded universe to catch it: what it needs is to be
    /// the stage's own call rather than a lambda written beside it.
    ///
    /// <see cref="MarketContextEngineTests.TheJsonbColumnIsWrittenThroughTheBulkPathAndReadsBackAsAnObject"/>
    /// asserts the mechanism works. This asserts the stage uses it. Reverting
    /// `MarketContextEngine` to <c>WriteAsync</c> leaves that test passing and fails
    /// this one, which is the whole difference.
    /// </summary>
    [Fact]
    public async Task TheMarketContextEngineWritesJsonbThroughItsOwnPath()
    {
        var ct = TestContext.Current.CancellationToken;

        await ClearContextAsync(ct).ConfigureAwait(true);

        var stage = new MarketContextEngine();
        var result = await stage.ExecuteAsync(ContextFor(stage, ContextDate), ct).ConfigureAwait(true);

        Assert.Equal(1, result.RowsWritten);

        await using (var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true))
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT jsonb_typeof(sector_relative_strength), regime_label, vix IS NULL
                FROM market_context_daily WHERE date = @d;
                """, conn);
            cmd.Parameters.AddWithValue("d", ContextDate);

            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

            Assert.True(await r.ReadAsync(ct).ConfigureAwait(true), "C10 wrote no row.");

            // A document rather than bytes Postgres could not parse. This is the
            // assertion the stage had none of until now.
            Assert.Equal("object", r.GetString(0));

            Assert.Contains(
                r.GetString(1),
                new[] { MarketContextEngine.RiskOn, MarketContextEngine.RiskOff, MarketContextEngine.Mixed },
                StringComparer.Ordinal);

            // No series in this feed, and the column is written rather than omitted
            // [METRICS.md section 5].
            Assert.True(r.GetBoolean(2));
        }

        await ClearContextAsync(ct).ConfigureAwait(true);
    }

    // --------------------------------------------------------------- rigging ---

    private static StageContext ContextFor(IStage stage, DateOnly date)
        => new(
            date, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(date),
            new StubConfig());

    private static async Task ExecuteAsync(string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Eight identical quarters and one price. Every figure divides, so the two ratios
    /// asserted above are arithmetic rather than a second implementation.
    /// </summary>
    private static async Task SeedValuationAsync(CancellationToken ct)
    {
        await ExecuteAsync(
            """
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            VALUES (@t, @d, 2, 2, 2, 2, 2, 1000)
            ON CONFLICT (ticker, date) DO UPDATE SET close = EXCLUDED.close;
            """,
            c =>
            {
                c.Parameters.AddWithValue("t", ValuationTicker);
                c.Parameters.AddWithValue("d", ValuationDate);
            }, ct).ConfigureAwait(false);

        for (var q = 0; q < 8; q++)
        {
            var periodEnd = ValuationDate.AddDays(-30 - (91 * q));

            await ExecuteAsync(
                """
                INSERT INTO fundamental_snapshot
                    (ticker, period_end, period_type, filing_date, filing_date_effective,
                     filing_date_unknown_reason, total_assets, total_current_liabilities,
                     goodwill, intangible_assets, cash, cash_and_equivalents,
                     short_term_investments, net_debt, total_revenue, cost_of_revenue,
                     ebit, ebitda, net_income, income_before_tax, income_tax_expense,
                     cash_from_operating, cash_from_investing, capital_expenditures,
                     shares_outstanding)
                VALUES
                    (@t, @pe, 'quarterly', @fd, @fd, 'none', 2000, 300,
                     100, 100, 900, NULL,
                     100, 500, 500, 300,
                     50, 100, 30, 40, 10,
                     100, -40, 25,
                     1000)
                ON CONFLICT (ticker, period_end, period_type) DO NOTHING;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", ValuationTicker);
                    c.Parameters.AddWithValue("pe", periodEnd);
                    c.Parameters.AddWithValue("fd", periodEnd.AddDays(20));
                }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Forty days of coverage inside the ninety-day baseline, with a count that moves
    /// so the z-score has a deviation to divide by, and a tone that rises on the date.
    /// </summary>
    private static async Task SeedSentimentAsync(CancellationToken ct)
    {
        await ExecuteAsync(
            """
            -- `security_daily` from 3.12. Dated before any fixture date because the
            -- membership read takes the most recent row at or before it [D-92].
            INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
            VALUES (@t, DATE '2000-01-01', 'SRLTEST-WP', 'SRLTEST-WP', 1000000000, true)
            ON CONFLICT (ticker, date) DO UPDATE SET is_active = true;
            """,
            c => c.Parameters.AddWithValue("t", SentimentTicker), ct).ConfigureAwait(false);

        for (var i = 1; i <= 40; i++)
        {
            await ExecuteAsync(
                """
                INSERT INTO sentiment_daily (ticker, date, article_count, sentiment_score)
                VALUES (@t, @d, @n, 0.25)
                ON CONFLICT (ticker, date) DO UPDATE SET article_count = EXCLUDED.article_count;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", SentimentTicker);
                    c.Parameters.AddWithValue("d", SentimentDate.AddDays(-i));
                    c.Parameters.AddWithValue("n", (i % 5) + 1);
                }, ct).ConfigureAwait(false);
        }

        await ExecuteAsync(
            """
            INSERT INTO sentiment_daily (ticker, date, article_count, sentiment_score)
            VALUES (@t, @d, 12, 0.75)
            ON CONFLICT (ticker, date) DO UPDATE SET article_count = EXCLUDED.article_count;
            """,
            c =>
            {
                c.Parameters.AddWithValue("t", SentimentTicker);
                c.Parameters.AddWithValue("d", SentimentDate);
            }, ct).ConfigureAwait(false);
    }

    private static async Task ClearValuationAsync(CancellationToken ct)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM valuation_daily WHERE ticker = @t;",
                     "DELETE FROM fundamental_snapshot WHERE ticker = @t;",
                     "DELETE FROM price_daily WHERE ticker = @t;",
                 })
        {
            await ExecuteAsync(sql, c => c.Parameters.AddWithValue("t", ValuationTicker), ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task ClearSentimentAsync(CancellationToken ct)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM sentiment_derived_daily WHERE ticker = @t;",
                     "DELETE FROM sentiment_daily WHERE ticker = @t;",
                     "DELETE FROM security_daily WHERE ticker = @t;",
                 })
        {
            await ExecuteAsync(sql, c => c.Parameters.AddWithValue("t", SentimentTicker), ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task ClearContextAsync(CancellationToken ct)
        => await ExecuteAsync(
            "DELETE FROM market_context_daily WHERE date = @d;",
            c => c.Parameters.AddWithValue("d", ContextDate), ct).ConfigureAwait(false);

    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

    /// <summary>
    /// The keys the three stages resolve, answered per key rather than one number to
    /// all of them, so a stage reading a key this does not know fails by name.
    /// </summary>
    private sealed class StubConfig : IConfigStore
    {
        private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
        {
            ["valuation.own_history_min_points"] = "24",
            ["sentiment.min_baseline_days"] = "20",
            ["market.breadth_ma_days"] = "200",
            ["market.sector_composite_min_members"] = "5",
            ["market.regime_breadth_high"] = "0.60",
            ["market.regime_breadth_low"] = "0.40",
        };

        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => Task.FromResult<ConfigRow?>(Values.TryGetValue(key, out var v)
                ? new ConfigRow(key, 1, v, new DateOnly(2000, 1, 1))
                : null);

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => await ResolveAsync(key, asOf, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException(
                   string.Create(CultureInfo.InvariantCulture,
                       $"The stage resolved '{key}', which this stub does not know."));

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");
    }
}
