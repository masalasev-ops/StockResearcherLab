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
/// C09's seam: the range path and the nightly path compute one date the same way.
///
/// **C09's window is not a bar count, it is what was public on the date** [INVARIANT 12,
/// D-46]. The nightly path narrows on `period_end` in SQL and applies the point-in-time
/// filter inside `Compute`; the range path reads a ticker's periods once for the whole
/// range and reproduces both halves per date. Two filters in two places is the shape that
/// drifts, and the drift is silent in the direction that matters: a filing read a day
/// early makes every quality screen look excellent in backfill and ordinary live, which
/// is the failure `CLAUDE.md` §1 opens with.
///
/// **So the boundary is asserted as well as the output.** A quarter files on a date the
/// fixture chooses. The day before it, neither path may see it; on it, both must.
///
/// **What each of these two tests can and cannot fail on was established by mutation
/// rather than claimed**, and the answer corrected the first draft of this file.
///
/// Replacing the range path's per-date `filing_date_effective &lt;= date` with
/// `period_end &lt;= date` fails **neither** test, because `Compute` applies the
/// point-in-time filter itself and both paths reach it. That is the design working:
/// INVARIANT 12 lives in one place and no seam can lose it. So
/// `NeitherPathSeesAQuarterBeforeItsFilingDate` pins that neither path bypasses that
/// filter, which is worth pinning, and it is **not** evidence about the range path's own
/// narrowing.
///
/// Widening `MonthEnds.At` from "strictly before the date" to "at or before" fails the
/// anchor. That is a real seam: the nightly path takes month ends from a statement and
/// the range path derives them in memory, and a date that is itself a month end would
/// then carry its own close inside its own five-year history. The anchor is therefore
/// known to be load-bearing over the half that is genuinely duplicated.
/// </summary>
[Collection("database")]
public sealed class ValuationSeamTests
{
    private const string Prefix = "SRLVAL";

    private static readonly string[] Names = [Prefix + "1.US", Prefix + "2.US"];

    /// <summary>The quarter whose arrival this fixture is built around, and the day it became public.</summary>
    private static readonly DateOnly PeriodEnd = new(2020, 3, 31);

    private static readonly DateOnly FiledOn = new(2020, 5, 15);

    /// <summary>
    /// The anchor date. A month end, so `MonthEnds.At`'s "strictly before the as-of date"
    /// rule is exercised rather than skipped: a boundary written as "at or before" would
    /// put the date's own close into its own five-year history.
    /// </summary>
    private static readonly DateOnly At = new(2020, 6, 30);

    private static readonly DateOnly PriceFrom = new(2015, 1, 1);

    private static readonly DateOnly PriceTo = new(2020, 7, 31);

    /// <summary>
    /// **The anchor**, over a range of dates rather than one.
    ///
    /// **A single-date range would not test the thing this exists for**, and that was
    /// found by mutating the range path rather than by reasoning: `RangeFilingsAsync`
    /// already narrows on `filing_date_effective &lt;= to` in SQL, so on a range whose
    /// only date is `to`, replacing the per-date point-in-time filter with `period_end
    /// &lt;= date` changes nothing and the comparison stays green. The per-date filter is
    /// load-bearing only where the range spans a filing, so every range here does.
    /// </summary>
    [Fact]
    public async Task TheRangePathWritesTheSameRowsAsTheNightlyPathOnEveryDateOfARange()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var dates = Span(FiledOn.AddDays(-2), At);
        var both = await NightlyThenRangeAsync(dates, ct).ConfigureAwait(true);

        foreach (var date in dates)
        {
            var nightly = both.Nightly[date];
            var range = both.Range[date];

            Assert.Equal(Names.Length, nightly.Count);
            Assert.Contains(nightly, r => r.Values.Any(v => v is not null));
            Assert.Equal(nightly.Count, range.Count);

            for (var i = 0; i < nightly.Count; i++)
            {
                Assert.Equal(nightly[i].Ticker, range[i].Ticker);
                Assert.Equal(nightly[i].Values, range[i].Values);
            }
        }
    }

    /// <summary>
    /// **The window, pinned at the filing date, on both paths.**
    ///
    /// The quarter ending 2020-03-31 becomes public on 2020-05-15. On 2020-05-14 both
    /// paths must compute from the quarter before it and on 2020-05-15 from this one, so
    /// the row moves across that boundary and moves the same way twice. A path keying on
    /// `period_end` rather than on the filing date would have this quarter six weeks early
    /// and both readings would be equal.
    ///
    /// **The range spans the boundary**, which is what makes the range path's own per-date
    /// filter decide the answer rather than the statement that fetched the periods.
    /// </summary>
    [Fact]
    public async Task NeitherPathSeesAQuarterBeforeItsFilingDate()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var before = FiledOn.AddDays(-1);
        var both = await NightlyThenRangeAsync([before, FiledOn], ct).ConfigureAwait(true);

        // The two paths agree on each date, which is the seam.
        Assert.Equal(both.Nightly[before][0].Values, both.Range[before][0].Values);
        Assert.Equal(both.Nightly[FiledOn][0].Values, both.Range[FiledOn][0].Values);

        // And the filing is what moved them, which is what makes the agreement above a
        // statement about the boundary rather than about a value that never changes.
        Assert.NotEqual(both.Nightly[before][0].Values, both.Nightly[FiledOn][0].Values);
        Assert.NotEqual(both.Range[before][0].Values, both.Range[FiledOn][0].Values);
    }

    // ----------------------------------------------------------- harness ---

    private sealed record Snapshot(string Ticker, IReadOnlyList<object?> Values);

    private sealed record Both(
        IReadOnlyDictionary<DateOnly, IReadOnlyList<Snapshot>> Nightly,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<Snapshot>> Range);

    /// <summary>Every date from one to the other inclusive, which the fixture prices every one of.</summary>
    private static IReadOnlyList<DateOnly> Span(DateOnly from, DateOnly to)
    {
        var dates = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1)) dates.Add(d);
        return dates;
    }

    /// <summary>
    /// Each date by the nightly path one at a time, then, on a cleared table, all of them
    /// in one range execution.
    ///
    /// **The clear is not tidiness**: the write is an upsert on `(ticker, date)`, so
    /// without it the range run would leave the nightly rows in place wherever the two
    /// agreed and the comparison would be of a snapshot with itself.
    /// </summary>
    private static async Task<Both> NightlyThenRangeAsync(IReadOnlyList<DateOnly> dates, CancellationToken ct)
    {
        var nightly = new Dictionary<DateOnly, IReadOnlyList<Snapshot>>();
        var range = new Dictionary<DateOnly, IReadOnlyList<Snapshot>>();

        foreach (var date in dates)
        {
            await ClearOutputAsync(date, ct).ConfigureAwait(false);
            await RunNightlyAsync(date, ct).ConfigureAwait(false);
            nightly[date] = await RowsAsync(date, ct).ConfigureAwait(false);
            await ClearOutputAsync(date, ct).ConfigureAwait(false);
        }

        await RunRangeAsync(dates[0], dates[^1], ct).ConfigureAwait(false);

        foreach (var date in dates)
        {
            range[date] = await RowsAsync(date, ct).ConfigureAwait(false);
        }

        return new Both(nightly, range);
    }

    private static async Task RunNightlyAsync(DateOnly date, CancellationToken ct)
    {
        var stage = new ValuationEngine();
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
    /// The range path through `BackfillRun`, which is the only route to a
    /// `BackfillContext` and therefore the only way to reach `ExecuteRangeAsync` as the
    /// worker reaches it [D-93]. The allowance is constructed and never consulted: a
    /// compute stage makes no provider call, and the double fails the test if one is made.
    /// </summary>
    private static async Task RunRangeAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var stage = new ValuationEngine();

        var run = new BackfillRun(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            new FrozenClock(to),
            TestDatabase.ConnectionString,
            new UnitAllowance(new EodhdClient(
                new HttpClient(new NoProviderCall()) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
                "test-token", new FrozenClock(to))));

        var result = await run.RunAsync(stage.Name, from, to, ct).ConfigureAwait(false);

        Assert.False(result.WasHalted, result.Detail ?? "halted with no detail");
    }

    /// <summary>
    /// This fixture's rows for the date, ordinal by ticker.
    ///
    /// **Array columns are flattened to text rather than compared as objects**, because
    /// `last_two_earnings_surprises` comes back as a `float[]` and two equal arrays are
    /// different references. An equality that silently compared references would fail on
    /// agreement rather than on disagreement.
    /// </summary>
    private static async Task<IReadOnlyList<Snapshot>> RowsAsync(DateOnly date, CancellationToken ct)
    {
        var columns = string.Join(", ", ValuationEngine.Columns.Skip(2));

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT ticker, {columns} FROM valuation_daily WHERE date = @d AND ticker LIKE @p;", conn);
        cmd.Parameters.AddWithValue("d", date);
        cmd.Parameters.AddWithValue("p", Prefix + "%");

        var rows = new List<Snapshot>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new List<object?>(r.FieldCount - 1);

            for (var i = 1; i < r.FieldCount; i++)
            {
                if (await r.IsDBNullAsync(i, ct).ConfigureAwait(false))
                {
                    values.Add(null);
                    continue;
                }

                var value = r.GetValue(i);
                values.Add(value is Array a
                    ? string.Join(",", a.Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)))
                    : value);
            }

            rows.Add(new Snapshot(r.GetString(0), values));
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.Ticker, b.Ticker));
        return rows;
    }

    private static async Task ClearOutputAsync(DateOnly date, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM valuation_daily WHERE date = @d AND ticker LIKE @p;", conn);
        cmd.Parameters.AddWithValue("d", date);
        cmd.Parameters.AddWithValue("p", Prefix + "%");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Two names, five and a half years of daily closes, and quarterly filings whose
    /// effective filing dates are forty-five days behind their period ends.
    ///
    /// **Every quarter's filing date is stated rather than derived from the period end by
    /// the code under test.** The fixture is the independent statement of what was public
    /// when, which is what lets the boundary test mean anything.
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        foreach (var sql in new[]
                 {
                     "DELETE FROM valuation_daily WHERE ticker LIKE @p",
                     "DELETE FROM fundamental_snapshot WHERE ticker LIKE @p",
                     "DELETE FROM earnings_history WHERE ticker LIKE @p",
                     "DELETE FROM price_daily WHERE ticker LIKE @p",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", Prefix + "%");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var n = 0; n < Names.Length; n++)
        {
            await PricesAsync(conn, Names[n], 60m + (n * 20m), ct).ConfigureAwait(false);
            await FilingsAsync(conn, Names[n], n, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A daily close with shape, written in one statement. A flat series would satisfy the
    /// month-end and close-on-the-date rules trivially, including wrong ones.
    /// </summary>
    private static async Task PricesAsync(
        NpgsqlConnection conn, string ticker, decimal basePrice, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            SELECT @t, d::date,
                   c, c + 1, c - 1, c, c, 500000
            FROM (
                SELECT d, @b::numeric
                       + ((d::date - @f::date)::numeric * 0.01)
                       + (8 * sin((d::date - @f::date)::numeric / 9)) AS c
                FROM generate_series(@f::date, @s::date, INTERVAL '1 day') AS d
            ) priced
            ON CONFLICT (ticker, date) DO NOTHING;
            """, conn);

        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("b", basePrice);
        cmd.Parameters.AddWithValue("f", PriceFrom);
        cmd.Parameters.AddWithValue("s", PriceTo);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sixteen quarters ending at <see cref="PeriodEnd"/> and one after it, each filed
    /// forty-five days after its period end. The one after is what makes the anchor date's
    /// window a decision rather than a coincidence: its period end is inside the seven-year
    /// window and its filing date is not yet reached.
    /// </summary>
    private static async Task FilingsAsync(
        NpgsqlConnection conn, string ticker, int n, CancellationToken ct)
    {
        for (var q = 16; q >= -1; q--)
        {
            var end = PeriodEnd.AddMonths(-3 * q);
            var filed = end.AddDays(45);

            // A moving fundamental, so a quarter arriving changes the row. Scaled per
            // ticker so the two names are not the same company under two labels.
            var scale = 1m + (q * 0.05m) + (n * 0.3m);

            await using var cmd = new NpgsqlCommand(
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
                    (@t, @pe, 'quarterly', @fd, @fd, 'none', 2000 * @k, 300 * @k,
                     100, 100, 900 * @k, NULL,
                     100, 500 * @k, 500 * @k, 300 * @k,
                     50 * @k, 100 * @k, 30 * @k, 40 * @k, 10 * @k,
                     100 * @k, -40, 25 * @k,
                     1000)
                ON CONFLICT (ticker, period_end, period_type) DO NOTHING;
                """, conn);

            cmd.Parameters.AddWithValue("t", ticker);
            cmd.Parameters.AddWithValue("pe", end);
            cmd.Parameters.AddWithValue("fd", filed);
            cmd.Parameters.AddWithValue("k", scale);
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
