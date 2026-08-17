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
/// C06's range mode end to end, which `EventsRangeTests` covers at the parse and not
/// through the stage [item 33].
///
/// **The property this adds is "both calls or neither".** C06 projects the sum of
/// `backfill.weight_splits` and `backfill.weight_dividends` and asks the gate once per
/// ticker, because a ticker whose splits landed and whose dividends did not would be a
/// half-covered name behind an attempt row saying it was covered. Nothing at the parse
/// layer can see that, and from a row count a half-covered ticker and an ordinary name
/// with no distributions are the same number.
///
/// **The pool carries names this fixture does not own**, three other fixtures seeding
/// `security_daily` at 2000-01-01, so nothing here asserts a pool. What is asserted is
/// how many tickers the allowance pays for, that each of them was asked for both
/// endpoints, and that the one the gate refused was asked for neither.
/// </summary>
[Collection("database")]
public sealed class EventsSweepTests
{
    private const string Prefix = "SRLEVT";

    /// <summary>
    /// The range. After `ConfigSeeder.SeedInstant` so config resolves, before
    /// `backfill.window_start` so a fixture attempt row cannot be read as a real one.
    /// C06 stamps the range start, which is C02's half of D-99's asymmetry.
    /// </summary>
    private static readonly DateOnly From = new(2020, 4, 1);

    private static readonly DateOnly To = new(2020, 4, 30);

    private static readonly DateTimeOffset Now = new(2026, 8, 5, 2, 52, 0, TimeSpan.Zero);

    private static DateOnly ProviderDate => DateOnly.FromDateTime(Now.UtcDateTime);

    private const int Members = 8;

    /// <summary>Delisted, with a bar inside the window, which is D-101's half of the pool.</summary>
    private const string Delisted = Prefix + "GONE.US";

    /// <summary>
    /// Spent units that pay for four tickers and refuse the fifth.
    ///
    /// The delisted symbol list bills one, so the gate first sees 49,991 of 100,000
    /// against a reserve of 50,000 and 9 is available. A ticker is two units, one split
    /// call and one dividend call, so four fit at eight and the fifth is refused against
    /// one.
    /// </summary>
    private const int RoomForFourTickers = 49_990;

    /// <summary>
    /// **A refused ticker is asked for neither endpoint**, which is what "both calls or
    /// neither" means at the boundary and is the only place it can be observed.
    /// </summary>
    [Fact]
    public async Task AGatedSweepLeavesTheRefusedTickerWithNeitherCallNorAnAttemptRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var first = new EventsHandler(alreadySpent: RoomForFourTickers);
        var halted = await RunAsync(first, ct).ConfigureAwait(true);

        Assert.True(halted.WasHalted);

        // Four tickers, each asked for both endpoints. Eight calls over four names.
        Assert.Equal(4, first.Asked.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, first.Asked.Count);

        foreach (var ticker in first.Asked.Distinct(StringComparer.Ordinal))
        {
            Assert.Equal(2, first.Asked.Count(t => string.Equals(t, ticker, StringComparison.Ordinal)));
        }

        var attempted = await AttemptedAsync(ct).ConfigureAwait(true);
        Assert.Equal(first.Asked.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), attempted);

        // The next run picks up the complement and asks nothing twice.
        var second = new EventsHandler(alreadySpent: 0);
        var completed = await RunAsync(second, ct).ConfigureAwait(true);

        Assert.False(completed.WasHalted);
        Assert.Empty(first.Asked.Intersect(second.Asked, StringComparer.Ordinal));
    }

    /// <summary>
    /// The fixture's own names all carry an attempt after a full sweep, the delisted one
    /// included, and a re-invocation dispatches nothing.
    /// </summary>
    [Fact]
    public async Task ACompletedSweepCoversTheDelistedHalfAndReInvokesToNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var first = new EventsHandler(alreadySpent: 0);
        var completed = await RunAsync(first, ct).ConfigureAwait(true);

        Assert.False(completed.WasHalted);

        var attempted = await AttemptedAsync(ct).ConfigureAwait(true);
        Assert.Equal(Fixture().Order(StringComparer.Ordinal), attempted.Where(IsFixture));

        var again = new EventsHandler(alreadySpent: 0);
        var result = await RunAsync(again, ct).ConfigureAwait(true);

        Assert.Empty(again.Asked);
        Assert.Equal(0, result.RowsWritten);
        Assert.False(result.WasHalted);
    }

    /// <summary>
    /// **A name with no distributions is attempted once and not asked again** [3.1
    /// measured `SPY.US` itself at zero splits]. An empty array is an ordinary name
    /// rather than a fault, and a resumption keyed on rows in `events` would re-ask it
    /// for the life of the sweep at two units a time.
    /// </summary>
    [Fact]
    public async Task ATickerWithNoDistributionsIsAttemptedOnceAndNotAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var empty = Ticker(1);

        var first = new EventsHandler(alreadySpent: 0) { Empty = empty };
        await RunAsync(first, ct).ConfigureAwait(true);

        Assert.Contains(empty, first.Asked, StringComparer.Ordinal);
        Assert.Contains(empty, await AttemptedAsync(ct).ConfigureAwait(true));

        var again = new EventsHandler(alreadySpent: 0) { Empty = empty };
        await RunAsync(again, ct).ConfigureAwait(true);

        Assert.Empty(again.Asked);
    }

    // ----------------------------------------------------------- harness ---

    private static string Ticker(int i)
        => Prefix + i.ToString("000", CultureInfo.InvariantCulture) + ".US";

    private static IReadOnlyList<string> Fixture()
        => [.. Enumerable.Range(1, Members).Select(Ticker), Delisted];

    private static bool IsFixture(string ticker)
        => ticker.StartsWith(Prefix, StringComparison.Ordinal);

    private static async Task<BackfillResult> RunAsync(EventsHandler handler, CancellationToken ct)
    {
        var stage = new EventsIngestor(Client(handler));

        var run = new BackfillRun(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            new FixedClock(Now, To),
            TestDatabase.ConnectionString,
            new UnitAllowance(Client(handler)));

        return await run.RunAsync(stage.Name, From, To, ct).ConfigureAwait(false);
    }

    private static EodhdClient Client(EventsHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
            "test-token", new FixedClock(Now, To));

    /// <summary>Sorted in C# because every comparison here is ordinal and the server's collation is not.</summary>
    private static async Task<IReadOnlyList<string>> AttemptedAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT ticker FROM event_fetch_attempt WHERE last_attempted_date = @d;", conn);
        cmd.Parameters.AddWithValue("d", From);

        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) rows.Add(r.GetString(0));

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>
    /// This fixture's rows, and the rows a run leaves on names it does not own. Clearing
    /// attempts by date is safe here and would not be on the developer store: this is the
    /// suite's own database and the date precedes `backfill.window_start` [item 22, item 26].
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM event_fetch_attempt WHERE last_attempted_date = @d;", conn))
        {
            cmd.Parameters.AddWithValue("d", From);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM events WHERE ticker LIKE @p;", conn))
        {
            cmd.Parameters.AddWithValue("p", Prefix + "%");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var sql in new[]
                 {
                     "DELETE FROM security_daily WHERE ticker LIKE @p",
                     "DELETE FROM price_daily WHERE ticker LIKE @p",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", Prefix + "%");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var i = 1; i <= Members; i++)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, 'SRLTEST-EVT', 'SRLTEST-EVT', 1000000000, true)
                ON CONFLICT (ticker, date) DO UPDATE SET is_active = true;
                """, conn);
            cmd.Parameters.AddWithValue("t", Ticker(i));
            cmd.Parameters.AddWithValue("d", From);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
            SELECT @t, d::date, 10, 10, 10, 10, 10, 100000
            FROM generate_series(@f::date, @s::date, INTERVAL '1 day') AS d
            ON CONFLICT (ticker, date) DO NOTHING;
            """, conn))
        {
            cmd.Parameters.AddWithValue("t", Delisted);
            cmd.Parameters.AddWithValue("f", From);
            cmd.Parameters.AddWithValue("s", To);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow, DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;

        public DateOnly Today => today;
    }

    /// <summary>
    /// `/api/user`, one delisted symbol list, and one `splits/{t}` and one `div/{t}` per
    /// ticker.
    ///
    /// **The counter is the provider's**: one unit a symbol list and one per distribution
    /// call, which is `backfill.weight_splits` and `backfill.weight_dividends`, so the
    /// gate halts on its own arithmetic rather than on a number this test chose.
    /// </summary>
    private sealed class EventsHandler(int alreadySpent) : HttpMessageHandler
    {
        private int _calls;

        /// <summary>Every ticker asked for, once per endpoint, in order.</summary>
        public List<string> Asked { get; } = [];

        /// <summary>A ticker both endpoints answer with an empty array for.</summary>
        public string? Empty { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/user?", StringComparison.Ordinal))
            {
                return Json(string.Format(
                    CultureInfo.InvariantCulture,
                    """{{"apiRequests":{0},"apiRequestsDate":"{1}","dailyRateLimit":100000,"extraLimit":0}}""",
                    alreadySpent + _calls,
                    ProviderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }

            if (url.Contains("exchange-symbol-list", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _calls);

                return Json(string.Format(
                    CultureInfo.InvariantCulture,
                    """[{{"Code":"{0}","Name":"{0} Inc","Type":"Common Stock"}}]""",
                    Delisted[..^3]));
            }

            Interlocked.Increment(ref _calls);

            var path = request.RequestUri.AbsolutePath;
            var ticker = path[(path.LastIndexOf('/') + 1)..];
            Asked.Add(ticker);

            if (string.Equals(ticker, Empty, StringComparison.Ordinal))
            {
                return Json("[]");
            }

            return Json(path.Contains("/splits/", StringComparison.Ordinal)
                ? """[{"date":"2020-04-06","split":"3.000000/1.000000"}]"""
                : """
                  [{"date":"2020-04-08","declarationDate":"2020-03-24","recordDate":"2020-04-09",
                    "paymentDate":"2020-04-30","period":"Quarterly","value":1.5,
                    "unadjustedValue":1.5,"currency":"USD"}]
                  """);
        }

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
