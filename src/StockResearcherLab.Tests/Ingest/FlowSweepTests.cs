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
/// C05's range mode end to end, which open item 33 named first and which
/// `FlowRangeTests` says was composed rather than swept.
///
/// **What the two layers could not cover is the interaction between them.**
/// `FlowRangeTests` asserts where a walk ends and how the run log line is composed, both
/// against the client and the formatter. Neither can see what the stage does with a
/// gated walk, and that is where the property worth the most sits: **a ticker the gate
/// stopped mid-walk takes no attempt row and is walked again from its first page.**
/// Stamping it would freeze a partial history behind a record saying it was covered,
/// which is the one outcome the attempt record exists to prevent, and nothing about it is
/// visible from a row count.
///
/// **The pool carries names this fixture does not own** and cannot, three other fixtures
/// seeding `security_daily` at 2000-01-01. So nothing here asserts a pool: what is
/// asserted is that exactly as many walks fit as the allowance pays for, that the halted
/// ticker is the one without a row, and that the next run picks it up. Those hold whatever
/// else is in the universe.
/// </summary>
[Collection("database")]
public sealed class FlowSweepTests
{
    private const string Prefix = "SRLFLW";

    /// <summary>
    /// The range. Both ends after `ConfigSeeder.SeedInstant` so config resolves, and both
    /// before `backfill.window_start` so a fixture attempt row cannot be read as a real
    /// one [D-99, item 22]. C05 stamps the range end, which is the half of D-99's
    /// asymmetry this class exercises.
    /// </summary>
    private static readonly DateOnly From = new(2020, 3, 2);

    private static readonly DateOnly To = new(2020, 3, 27);

    private static readonly DateTimeOffset Now = new(2026, 8, 5, 2, 52, 0, TimeSpan.Zero);

    private static DateOnly ProviderDate => DateOnly.FromDateTime(Now.UtcDateTime);

    private const int Members = 8;

    /// <summary>
    /// Spent units that pay for four pages and refuse the fifth.
    ///
    /// Each ticker here is one page at `backfill.weight_form4_page`, which is ten. The
    /// gate first sees 49,955 of 100,000 against a reserve of 50,000, so 45 is available
    /// and four pages at ten fit. The fifth is asked for against five and is refused
    /// **before its first page is fetched**, which is the case this test is written on.
    /// </summary>
    private const int RoomForFourWalks = 49_955;

    /// <summary>
    /// **The property only an end-to-end run can show.** The gate stops a walk, the stage
    /// records no attempt for that ticker, and the next run walks it from the beginning.
    /// </summary>
    [Fact]
    public async Task AGatedTickerTakesNoAttemptRowAndIsWalkedAgainByTheNextRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var first = new FlowHandler(alreadySpent: RoomForFourWalks);
        var halted = await RunAsync(first, ct).ConfigureAwait(true);

        Assert.True(halted.WasHalted);
        Assert.Equal(4, first.Asked.Count);

        // The ticker the gate stopped, taken from the line an operator would read rather
        // than from the fixture, so the assertion is about what was reported.
        var stopped = HaltedOn(halted.Detail);
        Assert.DoesNotContain(stopped, first.Asked, StringComparer.Ordinal);

        var attempted = await AttemptedAsync(ct).ConfigureAwait(true);

        // Exactly the four that completed a walk. Not the fifth.
        Assert.Equal(first.Asked.Order(StringComparer.Ordinal), attempted);
        Assert.DoesNotContain(stopped, attempted, StringComparer.Ordinal);

        // A fresh provider day. The gated ticker is first in what remains, because
        // remaining is the pool less the attempt set and it never left it.
        var second = new FlowHandler(alreadySpent: 0);
        var completed = await RunAsync(second, ct).ConfigureAwait(true);

        Assert.False(completed.WasHalted);
        Assert.Contains(stopped, second.Asked, StringComparer.Ordinal);

        // And nothing that already carried a row was asked for again.
        Assert.Empty(first.Asked.Intersect(second.Asked, StringComparer.Ordinal));
    }

    /// <summary>
    /// A completed sweep re-invoked over the same range walks nothing, which is what
    /// makes a re-invocation safe rather than a second bill at ten units a page.
    /// </summary>
    [Fact]
    public async Task ACompletedSweepReInvokedOverTheSameRangeWalksNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var first = new FlowHandler(alreadySpent: 0);
        var completed = await RunAsync(first, ct).ConfigureAwait(true);

        Assert.False(completed.WasHalted);
        Assert.True(first.Asked.Count >= Members,
            $"The sweep walked {first.Asked.Count} names against a fixture of {Members}.");

        var again = new FlowHandler(alreadySpent: 0);
        var result = await RunAsync(again, ct).ConfigureAwait(true);

        Assert.Empty(again.Asked);
        Assert.Equal(0, result.RowsWritten);
        Assert.False(result.WasHalted);
    }

    /// <summary>
    /// **A 404 is a fact about the ticker rather than a fault**, so it takes an attempt
    /// row with no yield date and is not offered again. Without that, the names the
    /// filings index does not carry are re-asked on every run for the life of the sweep,
    /// which is the defect 0008 found on the nightly path fourteen tickers at a time.
    /// </summary>
    [Fact]
    public async Task ATickerTheFilingsIndexDoesNotCarryIsAttemptedOnceAndNotAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var absent = Ticker(1);

        var first = new FlowHandler(alreadySpent: 0) { NotFound = absent };
        await RunAsync(first, ct).ConfigureAwait(true);

        Assert.Contains(absent, first.Asked, StringComparer.Ordinal);
        Assert.Contains(absent, await AttemptedAsync(ct).ConfigureAwait(true));
        Assert.Null(await YieldDateAsync(absent, ct).ConfigureAwait(true));

        var again = new FlowHandler(alreadySpent: 0) { NotFound = absent };
        await RunAsync(again, ct).ConfigureAwait(true);

        Assert.Empty(again.Asked);
    }

    // ----------------------------------------------------------- harness ---

    private static string Ticker(int i)
        => Prefix + i.ToString("000", CultureInfo.InvariantCulture) + ".US";

    /// <summary>
    /// The ticker named in the halt sentence `FlowIngestor.DescribeGatedHalt` composes.
    /// Read out of the detail rather than predicted, so this test asserts the line an
    /// operator acts on rather than a value it already knew.
    /// </summary>
    private static string HaltedOn(string? detail)
    {
        const string Marker = "HALTED on the allowance gate at ";

        Assert.NotNull(detail);
        var at = detail.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "The halted run's detail carries no gated-halt sentence: " + detail);

        var rest = detail[(at + Marker.Length)..];
        return rest[..rest.IndexOf(',', StringComparison.Ordinal)];
    }

    private static async Task<BackfillResult> RunAsync(FlowHandler handler, CancellationToken ct)
    {
        var stage = new FlowIngestor(Client(handler));

        var run = new BackfillRun(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            new FixedClock(Now, To),
            TestDatabase.ConnectionString,
            new UnitAllowance(Client(handler)));

        return await run.RunAsync(stage.Name, From, To, ct).ConfigureAwait(false);
    }

    private static EodhdClient Client(FlowHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
            "test-token", new FixedClock(Now, To));

    /// <summary>
    /// Tickers carrying an attempt at this range's end. Sorted in C# because every
    /// comparison here is ordinal and the server's collation is not.
    /// </summary>
    private static async Task<IReadOnlyList<string>> AttemptedAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT ticker FROM flow_fetch_attempt WHERE last_attempted_date = @d;", conn);
        cmd.Parameters.AddWithValue("d", To);

        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) rows.Add(r.GetString(0));

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static async Task<DateOnly?> YieldDateAsync(string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT last_yield_date FROM flow_fetch_attempt WHERE ticker = @t;", conn);
        cmd.Parameters.AddWithValue("t", ticker);

        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? null : (DateOnly) value;
    }

    /// <summary>
    /// This fixture's rows, and the rows a run leaves on names it does not own. The
    /// attempt clear is by date, which is safe here and would not be on the developer
    /// store: this is the suite's own database and the date precedes
    /// `backfill.window_start` [item 22, item 26].
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM flow_fetch_attempt WHERE last_attempted_date = @d;", conn))
        {
            cmd.Parameters.AddWithValue("d", To);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM insider_transaction WHERE accession_number LIKE 'SRLFLW-%';", conn))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM security_daily WHERE ticker LIKE @p;", conn))
        {
            cmd.Parameters.AddWithValue("p", Prefix + "%");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var i = 1; i <= Members; i++)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, 'SRLTEST-FLW', 'SRLTEST-FLW', 1000000000, true)
                ON CONFLICT (ticker, date) DO UPDATE SET is_active = true;
                """, conn);
            cmd.Parameters.AddWithValue("t", Ticker(i));
            cmd.Parameters.AddWithValue("d", From);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow, DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;

        public DateOnly Today => today;
    }

    /// <summary>
    /// `/api/user`, and one complete `form4` page per ticker.
    ///
    /// **One page, and `meta.total` agrees with what it serves.** A short page would be a
    /// D-71 shortfall, which is the other observation and is asserted separately at
    /// `FlowRangeTests`; mixing the two into one fixture is what would make a halt and a
    /// shortfall indistinguishable here as well.
    ///
    /// The counter is the provider's: ten units a page, which is
    /// `backfill.weight_form4_page`, so the gate halts on its own arithmetic.
    /// </summary>
    private sealed class FlowHandler(int alreadySpent) : HttpMessageHandler
    {
        private int _pagesServed;

        /// <summary>Every ticker whose filings were asked for, in order.</summary>
        public List<string> Asked { get; } = [];

        /// <summary>A ticker the filings index answers 404 for.</summary>
        public string? NotFound { get; init; }

        private int Billable => _pagesServed * 10;

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

            var path = request.RequestUri.AbsolutePath.TrimEnd('/');
            var ticker = path[(path.LastIndexOf('/', path.LastIndexOf('/') - 1) + 1)..];
            ticker = ticker[..ticker.IndexOf('/', StringComparison.Ordinal)];

            Asked.Add(ticker);

            if (string.Equals(ticker, NotFound, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("Symbol not found", Encoding.UTF8, "text/plain"),
                });
            }

            Interlocked.Increment(ref _pagesServed);

            var body = string.Format(
                CultureInfo.InvariantCulture,
                """{{"data":[{0},{1}],"meta":{{"total":2}},"links":{{}}}}""",
                Filing(ticker, 1), Filing(ticker, 2));

            return Json(body);
        }

        /// <summary>
        /// One Form 4 filing in the shape `ParseFilings` reads, unique on its accession
        /// across tickers so the reset can take this fixture's rows and no others.
        ///
        /// **The field names are the provider's own** as 1.9 captured them,
        /// `accession_number` and `non_derivative` rather than the camel-cased names an
        /// SEC document uses. A fixture in the wrong shape parses to nothing and every
        /// assertion below about attempt rows still passes, which is why this one carries
        /// rows the test reads back.
        /// </summary>
        private static string Filing(string ticker, int n) => string.Format(
            CultureInfo.InvariantCulture,
            """
            {{"accession_number":"SRLFLW-{0}-{1:D2}","filed_at":"2020-03-04",
              "non_derivative":[
                {{"transaction_date":"2020-03-01","reporting_owner_cik":"000{1}",
                  "reporting_owner_name":"Owner {1}","transaction_code":"P",
                  "security_title":"Common","shares_amount":"100","price_per_share":"10",
                  "total_value":"1000","shares_owned_after":"1000",
                  "acquired_or_disposed":"A"}}]}}
            """,
            ticker, n);

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
