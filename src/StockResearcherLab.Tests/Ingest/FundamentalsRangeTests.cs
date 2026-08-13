using System.Globalization;
using System.Net;
using System.Text;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Data.Eodhd;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// C03's range mode resuming, which is 3.7's sweep and had no test.
///
/// **C02 had this asserted and C03 did not**, and the two resume differently: C02
/// stamps its attempt with the range start and C03 with the range end, because for
/// C03 the nightly call and the sweep call are the same call [D-99]. So the property
/// that holds for one is not evidence about the other, and the asymmetry is exactly
/// the kind that reads as correct until a sweep spends a day re-fetching.
///
/// **The observable is which tickers were asked for, not the row count.** A sweep
/// that restarts writes a perfectly healthy number of rows over the names it already
/// had, which is what made the frozen rotation survive three runs and a sign-off.
/// </summary>
[Collection("database")]
public sealed class FundamentalsRangeTests
{
    private const string Prefix = "SRLFRG";

    /// <summary>
    /// Six pool members in the provider's dash form, because `SymbolList.AdmittedAsync`
    /// keys its map on `Code + ".US"` and the bootstrap pool intersects `price_daily`
    /// with it.
    /// </summary>
    private static readonly string[] Pool =
    [
        Prefix + "A.US", Prefix + "B.US", Prefix + "C.US",
        Prefix + "D.US", Prefix + "E.US", Prefix + "F.US",
    ];

    /// <summary>The sweep's range. `To` is the attempt stamp, which is the whole point.</summary>
    private static readonly DateOnly From = new(2021, 1, 4);

    private static readonly DateOnly To = new(2026, 8, 5);

    /// <summary>
    /// Spent units that leave room for exactly three tickers.
    ///
    /// The pool build bills two symbol lists at one each, so the gate first sees
    /// 49,962 of 100,000 with a reserve of 50,000, which is 38 above it. Three calls at
    /// ten fit and the fourth projects at ten against eight.
    /// </summary>
    private const int RoomForThree = 49_960;

    // ------------------------------------------------- resumption [D-99] ---

    /// <summary>
    /// **The property 3.7's sweep is run on.** A sweep halted by the allowance gate
    /// resumes over exactly the tickers carrying no attempt row for this range, and it
    /// does not ask again for one it already fetched.
    ///
    /// Asserted as set equality against the complement rather than as a count, because
    /// a restart and a correct resumption of the same size are the same number.
    /// </summary>
    [Fact]
    public async Task AHaltedSweepResumesOverExactlyTheTickersWithNoAttemptRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var first = new RangeHandler(alreadySpent: RoomForThree);
        var halted = await RunAsync(first, To, ct).ConfigureAwait(true);

        Assert.True(halted.WasHalted);
        Assert.Equal(3, first.Asked.Count);

        var attemptedAfterHalt = await AttemptedAsync(ct).ConfigureAwait(true);
        Assert.Equal(first.Asked.Order(StringComparer.Ordinal), attemptedAfterHalt);

        // A fresh provider day. Nothing tells the sweep where it stopped; it asks the
        // store what is left.
        var second = new RangeHandler(alreadySpent: 0);
        var completed = await RunAsync(second, To, ct).ConfigureAwait(true);

        Assert.False(completed.WasHalted);

        // Exactly the complement, both directions. Neither a restart nor a skip.
        Assert.Equal(
            Pool.Except(first.Asked, StringComparer.Ordinal).Order(StringComparer.Ordinal),
            second.Asked.Order(StringComparer.Ordinal));

        Assert.Equal(Pool.Order(StringComparer.Ordinal), await AttemptedAsync(ct).ConfigureAwait(true));
    }

    /// <summary>
    /// A sweep that finished asks for nothing when it is run again, which is what makes
    /// a re-invocation safe rather than a second bill.
    /// </summary>
    [Fact]
    public async Task ACompletedSweepReInvokedOverTheSameRangeDispatchesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var first = new RangeHandler(alreadySpent: 0);
        await RunAsync(first, To, ct).ConfigureAwait(true);

        Assert.Equal(Pool.Order(StringComparer.Ordinal), first.Asked.Order(StringComparer.Ordinal));

        var again = new RangeHandler(alreadySpent: 0);
        var result = await RunAsync(again, To, ct).ConfigureAwait(true);

        Assert.Empty(again.Asked);
        Assert.Equal(0, result.RowsWritten);
        Assert.False(result.WasHalted);
    }

    /// <summary>
    /// **A different range end is a different sweep, and this is a known property
    /// rather than a defect** [D-99].
    ///
    /// C03 stamps the attempt with the range end because that column also orders the
    /// nightly rotation's staleness, so an attempt stamped with a 2021 window start
    /// would put every swept ticker back at the head of the rotation. The cost is that
    /// `to` is load-bearing across the days a sweep spans, and `to` defaults to today.
    /// Asking for a different one re-dispatches the whole pool at ten units a name.
    ///
    /// It is asserted so that it is a fact somebody can find rather than a warning in
    /// a runbook, which is what `RUNBOOK.md` says about passing both dates.
    /// </summary>
    [Fact]
    public async Task ASweepAskedForADifferentRangeEndStartsOver()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var first = new RangeHandler(alreadySpent: 0);
        await RunAsync(first, To, ct).ConfigureAwait(true);

        Assert.Equal(6, first.Asked.Count);

        var shifted = new RangeHandler(alreadySpent: 0);
        await RunAsync(shifted, To.AddDays(1), ct).ConfigureAwait(true);

        Assert.Equal(Pool.Order(StringComparer.Ordinal), shifted.Asked.Order(StringComparer.Ordinal));
    }

    // ----------------------------------------------------------- harness ---

    private static async Task<BackfillResult> RunAsync(
        RangeHandler handler, DateOnly to, CancellationToken ct)
    {
        var stage = new FundamentalsIngestor(Client(handler, to));

        var run = new BackfillRun(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            new FrozenClock(to),
            TestDatabase.ConnectionString,
            new UnitAllowance(Client(handler, to)));

        return await run.RunAsync(stage.Name, From, to, ct).ConfigureAwait(false);
    }

    private static EodhdClient Client(RangeHandler handler, DateOnly to)
        => new(new HttpClient(handler) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
            "test-token", new FrozenClock(to));

    /// <summary>This fixture's tickers carrying an attempt at the range end, ordinal.</summary>
    private static async Task<IReadOnlyList<string>> AttemptedAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT ticker FROM fundamental_fetch_attempt " +
            "WHERE ticker LIKE @p AND last_attempted_date = @d ORDER BY ticker;", conn);
        cmd.Parameters.AddWithValue("p", Prefix + "%");
        cmd.Parameters.AddWithValue("d", To);

        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) rows.Add(r.GetString(0));
        return rows;
    }

    /// <summary>
    /// This fixture's rows only, so a developer database keeps everything else.
    ///
    /// **`run_log` is deliberately not cleared.** Deleting a stage's range rows is what
    /// took a real sweep's account at open item 22, and nothing here asserts on them.
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        foreach (var sql in new[]
                 {
                     "DELETE FROM fundamental_fetch_attempt WHERE ticker LIKE @p",
                     "DELETE FROM fundamental_snapshot WHERE ticker LIKE @p",
                     "DELETE FROM institutional_holding WHERE ticker LIKE @p",
                     "DELETE FROM price_daily WHERE ticker LIKE @p",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", Prefix + "%");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Above the price, liquidity and history floors, and reaching past the window
        // start, so every member clears the bootstrap and only resumption decides who
        // is asked for.
        foreach (var ticker in Pool)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
                SELECT @t, d::date, 50, 50, 50, 50, 50, 1000000
                FROM generate_series(DATE '2025-07-01', DATE '2026-08-05', INTERVAL '1 day') AS d
                ON CONFLICT (ticker, date) DO NOTHING;
                """, conn);
            cmd.Parameters.AddWithValue("t", ticker);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The run's clock. Nothing here depends on it moving, and the gate reads the
    /// provider's day rather than this [Allowance.cs].
    /// </summary>
    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow => new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        public DateOnly Today => today;
    }

    /// <summary>
    /// Two symbol lists, `/api/user`, and one `fundamentals/{t}` per ticker.
    ///
    /// **The counter is the provider's, not the stage's.** Nothing tells this handler
    /// what was spent; it bills what it served, at one unit a symbol list and ten a
    /// fundamentals call, which is `backfill.weight_fundamentals`. That is what makes
    /// the gate halt on its own arithmetic rather than on a number the test chose.
    /// </summary>
    private sealed class RangeHandler(int alreadySpent) : HttpMessageHandler
    {
        private int _symbolListCalls;
        private int _fundamentalsCalls;

        /// <summary>Every ticker this handler was asked for, in order.</summary>
        public List<string> Asked { get; } = [];

        private int Billable => _symbolListCalls + (_fundamentalsCalls * 10);

        /// <summary>
        /// The real UTC date, because the gate compares the reading's stamp against the
        /// provider's day rather than against `IClock.Today`, and a mismatch is a
        /// `Stale` verdict that halts without spending.
        /// </summary>
        private static DateOnly ProviderDate => DateOnly.FromDateTime(DateTime.UtcNow);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/user?", StringComparison.Ordinal))
            {
                return Json(string.Format(
                    CultureInfo.InvariantCulture,
                    """{{"apiRequests":{0},"apiRequestsDate":"{1}","dailyRateLimit":100000,"extraLimit":0}}""",
                    alreadySpent + Billable,
                    ProviderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }

            if (url.Contains("exchange-symbol-list", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _symbolListCalls);

                // The delisted list is empty, so the pool is the live half alone and
                // every assertion reads one set rather than a union of two.
                if (url.Contains("delisted=1", StringComparison.Ordinal))
                {
                    return Json("[]");
                }

                return Json("[" + string.Join(",", Pool.Select(t => t[..^3]).Select(c =>
                    $$"""{"Code":"{{c}}","Name":"{{c}} Inc","Type":"Common Stock"}""")) + "]");
            }

            Interlocked.Increment(ref _fundamentalsCalls);

            var path = request.RequestUri.AbsolutePath;
            Asked.Add(path[(path.LastIndexOf('/') + 1)..]);

            return Json("""
                {"Balance_Sheet":{"quarterly":{
                  "2026-03-31":{"filing_date":"2026-05-04","totalAssets":"1000"}}}}
                """);
        }

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
