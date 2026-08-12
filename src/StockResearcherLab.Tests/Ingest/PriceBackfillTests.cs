using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
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
/// C02's range mode [3.6]. The pool, the per-ticker parse, and the gate.
///
/// **No live call.** The handler below serves the two symbol lists and the per-ticker
/// series, so the pool arithmetic and the halt are exercised against a fixed answer
/// rather than against what the endpoint happened to do today.
/// </summary>
public sealed class PriceBackfillTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 2, 52, 0, TimeSpan.Zero);

    private static IClock Clock => new FixedClock(Now, new DateOnly(2026, 8, 11));

    private static DateOnly ProviderDate => DateOnly.FromDateTime(Now.UtcDateTime);

    // ------------------------------------------------------------ the parse ---

    /// <summary>
    /// The per-ticker endpoint carries no `code` and no `exchange_short_name`, the
    /// ticker being in the path, so its rows are a different shape from the bulk
    /// feed's. Parsed separately rather than coerced.
    /// </summary>
    [Fact]
    public void TheSeriesParseTakesItsTickerFromTheCallAndItsDatesFromTheRows()
    {
        using var doc = JsonDocument.Parse("""
            [{"date":"2021-01-04","open":10.5,"high":11,"low":10,"close":10.75,
              "adjusted_close":10.61,"volume":123456},
             {"date":"2021-01-05","open":10.8,"high":11.2,"low":10.7,"close":11.1,
              "adjusted_close":10.96,"volume":98765}]
            """);

        var bars = PriceIngestor.ParseSeries(doc.RootElement, "DGDM.US");

        Assert.Equal(2, bars.Count);
        Assert.All(bars, b => Assert.Equal("DGDM.US", b.Ticker));
        Assert.Equal(new DateOnly(2021, 1, 4), bars[0].Date);
        Assert.Equal(10.61m, bars[0].AdjClose);
        Assert.Equal(123456L, bars[0].Volume);
    }

    /// <summary>
    /// Sorted by date, because COPY order reaches the table and two runs over one
    /// ticker must produce byte-identical output. The endpoint returns oldest first
    /// today and nothing contracts that it will.
    /// </summary>
    [Fact]
    public void TheSeriesIsSortedByDateWhateverOrderTheEndpointSendsIt()
    {
        using var doc = JsonDocument.Parse("""
            [{"date":"2021-03-01","close":3},{"date":"2021-01-04","close":1},{"date":"2021-02-01","close":2}]
            """);

        var bars = PriceIngestor.ParseSeries(doc.RootElement, "T.US");

        Assert.Equal(
            [new DateOnly(2021, 1, 4), new DateOnly(2021, 2, 1), new DateOnly(2021, 3, 1)],
            bars.Select(b => b.Date).ToArray());
    }

    /// <summary>
    /// Absent stays null rather than becoming zero. A zero price is a real value and
    /// would pass every screen that reads it [`CLAUDE.md` §6].
    /// </summary>
    [Fact]
    public void AnAbsentFieldIsNullRatherThanZero()
    {
        using var doc = JsonDocument.Parse("""[{"date":"2021-01-04","close":10}]""");

        var bar = PriceIngestor.ParseSeries(doc.RootElement, "T.US").Single();

        Assert.Equal(10m, bar.Close);
        Assert.Null(bar.Open);
        Assert.Null(bar.AdjClose);
        Assert.Null(bar.Volume);
    }

    [Fact]
    public void ARowWithNoDateFailsRatherThanBeingKeyedOnSomethingElse()
    {
        using var doc = JsonDocument.Parse("""[{"close":10}]""");

        Assert.Throws<InvalidOperationException>(() => PriceIngestor.ParseSeries(doc.RootElement, "T.US"));
    }

    // ------------------------------------------------------------- the pool ---

    /// <summary>
    /// Every admitted common stock, live and delisted, and the two lists are disjoint
    /// [3.1]. A pool taken from either alone is survivorship-filtered in one direction
    /// or holds only the dead in the other.
    /// </summary>
    [Fact]
    public async Task ThePoolIsTheUnionOfTheLiveAndDelistedAdmittedLists()
    {
        var handler = new ProviderDouble();
        var stage = new PriceIngestor(Client(handler));

        var pool = await stage.PoolAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        // Twenty live common stocks and two delisted. The funds and ETFs in both lists
        // are out under D-4, so a pool that took the lists whole would be 25.
        Assert.Equal(22, pool.Count);
        Assert.Equal("D01.US", pool[0]);
        Assert.Equal("L20.US", pool[^1]);
        Assert.DoesNotContain("LETF.US", pool);
        Assert.DoesNotContain("DFUND.US", pool);

        Assert.Equal(2, handler.SymbolListCalls);
    }

    // ------------------------------------------------------------- the gate ---

    /// <summary>
    /// The gate stops the sweep at a ticker rather than mid-flight, and the position
    /// recorded is the first ticker not dispatched.
    /// </summary>
    [Fact]
    public async Task TheSweepHaltsAtATickerAndRecordsTheFirstOneItDidNotReach()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        // 100,000 limit, 50,000 reserve, 49,995 already spent. The two symbol-list
        // calls take it to 49,997, leaving three above the reserve, so the first chunk
        // of eight is gated and dispatched; those eight take it to 50,005 and the
        // second chunk's first gate refuses.
        //
        // The chunk is what makes this exact rather than racy: every gate in a chunk is
        // asked before any of its work is dispatched, so the overshoot is bounded by
        // the concurrency and the reserve absorbs it many times over [3.6].
        var handler = new ProviderDouble(alreadySpent: 49_995);
        var result = await RunAsync(handler, ct).ConfigureAwait(true);

        Assert.True(result.WasHalted);
        Assert.Equal(8, handler.SeriesCalls);

        // The ninth of the twenty-two, which is the first one not dispatched.
        Assert.Equal("L07.US", result.Position);
    }

    /// <summary>
    /// **The gate is asked once per chunk, not once per ticker** [3.6].
    ///
    /// `/api/user` costs no units and does cost a request. Per ticker, the sweep put
    /// 50,785 gate reads beside 50,785 `eod/{t}` calls, so against the provider's
    /// 1,000-a-minute limiter its floor doubled from about 51 minutes to about 102 for
    /// a property the reserve already guarantees.
    /// </summary>
    [Fact]
    public async Task TheGateIsAskedOncePerChunkRatherThanOncePerTicker()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var handler = new ProviderDouble();
        var result = await RunAsync(handler, ct).ConfigureAwait(true);

        Assert.False(result.WasHalted);
        Assert.Equal(22, handler.SeriesCalls);

        // Twenty-two tickers at a concurrency of eight is three chunks.
        Assert.Equal(3, handler.UserCalls);

        // The pool size plus the chunk count plus the two symbol lists, against the
        // 46 the per-ticker read would have made.
        Assert.Equal(27, handler.TotalRequests);
    }

    /// <summary>
    /// The next run picks up where the halt left off rather than starting over, which
    /// is the whole of what the position is for. Everything before it is skipped and
    /// nothing is re-fetched.
    /// </summary>
    [Fact]
    public async Task TheNextRunResumesFromTheHaltRatherThanStartingOver()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var first = new ProviderDouble(alreadySpent: 49_995);
        var halted = await RunAsync(first, ct).ConfigureAwait(true);

        Assert.True(halted.WasHalted);
        Assert.Equal("L07.US", halted.Position);

        // Room to finish this time.
        var second = new ProviderDouble();
        var done = await RunAsync(second, ct).ConfigureAwait(true);

        Assert.False(done.WasHalted);

        // Fourteen rather than twenty-two: the eight the halted run loaded are not
        // asked for again, which is the whole of what the position is for.
        Assert.Equal(14, second.SeriesCalls);
        Assert.Contains("Resumed from L07.US", done.Detail ?? "", StringComparison.Ordinal);
    }

    // --------------------------------------------- in flight, not pre-flight ---

    /// <summary>
    /// A ticker the endpoint does not carry writes nothing and the sweep runs on. That
    /// is the ordinary case for a recent listing and for the delisted names 3.1 found
    /// `sec-filings` refusing.
    /// </summary>
    [Fact]
    public async Task A404OnOneTickerLeavesTheSweepRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var handler = new ProviderDouble(
            failures: new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal)
            {
                ["L05.US"] = HttpStatusCode.NotFound,
            });

        var result = await RunAsync(handler, ct).ConfigureAwait(true);

        Assert.False(result.WasHalted);
        Assert.Equal(22, handler.SeriesCalls);

        // Twenty-one wrote a bar; L05 wrote none and did not stop the other twenty-one.
        Assert.Equal(21, result.RowsWritten);
    }

    /// <summary>
    /// **Everything other than a 404 fails the sweep, and this is the defect that was
    /// there before 3.6's second pass** [3.4].
    ///
    /// The catch was on `HttpRequestException` whole, and `EodhdClient` throws that for
    /// every non-success status. A 402 is the allowance wall reached in flight, which
    /// the gate's reserve makes the designed case rather than the unlikely one, and it
    /// persists for the day: swallowed, the sweep would write nothing for that ticker
    /// and nothing for any ticker after it, then return `Completed` over a partial
    /// load. A stage completes or it fails the run.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnythingOtherThanA404FailsTheSweep(HttpStatusCode status)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var handler = new ProviderDouble(
            failures: new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal)
            {
                ["L05.US"] = status,
            });

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync(handler, ct)).ConfigureAwait(true);

        var http = Unwrap(thrown);

        Assert.NotNull(http);
        Assert.Equal(status, http!.StatusCode);
    }

    /// <summary>
    /// The status is on the exception rather than only in its message, which is what
    /// lets the caller tell a 404 from a 402 without parsing text.
    /// </summary>
    [Fact]
    public async Task TheClientsExceptionCarriesTheStatusCode()
    {
        var handler = new ProviderDouble(
            failures: new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal)
            {
                ["X.US"] = HttpStatusCode.PaymentRequired,
            });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(handler).GetAsync("eod/X.US", [], TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.PaymentRequired, ex.StatusCode);
    }

    /// <summary>
    /// `Parallel.ForEachAsync` surfaces one exception directly and several as an
    /// aggregate, and which of those happens depends on scheduling. Unwrapped rather
    /// than asserted on, so the test is about the status and not about the scheduler.
    /// </summary>
    private static HttpRequestException? Unwrap(Exception ex)
        => ex switch
        {
            HttpRequestException http => http,
            AggregateException agg => agg.InnerExceptions.Select(Unwrap).FirstOrDefault(e => e is not null),
            _ => ex.InnerException is null ? null : Unwrap(ex.InnerException),
        };

    // ------------------------------------------------------------- harness ---

    private static EodhdClient Client(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri(EodhdUrl.BaseAddress) }, "fake-token", Clock);

    /// <summary>
    /// Seeds config and clears this stage's range rows.
    ///
    /// **The clear is not tidiness.** Resumption reads `run_log` and that table
    /// persists between test runs, so without it a test resumes from a row an earlier
    /// test wrote and its call count is whatever the previous test happened to leave.
    /// Found by exactly that: a run expected to sweep 22 tickers swept 14.
    /// </summary>
    private static async Task SeedAsync(CancellationToken ct)
    {
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedAsync(ct).ConfigureAwait(false);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "DELETE FROM run_log WHERE stage = 'PriceIngestor';", conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<BackfillResult> RunAsync(ProviderDouble handler, CancellationToken ct)
    {
        var stage = new PriceIngestor(Client(handler));

        var run = new BackfillRun(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            Clock,
            TestDatabase.ConnectionString,
            Allowance(handler));

        return await run.RunAsync(stage.Name, new DateOnly(2021, 1, 4), new DateOnly(2021, 1, 8), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The real <c>UnitAllowance</c> over the double, so the gate's reads are HTTP
    /// requests the double counts and `/api/user`'s parse is exercised end to end.
    ///
    /// **The stage does not tell the counter what it spent, and neither does the real
    /// one.** An earlier version of this had the sweep call a `Spend` method the
    /// production code never calls, so the reading never moved and nothing ever halted.
    /// The provider bills and `/api/user` reports; the double models that.
    /// </summary>
    private static IUnitAllowance Allowance(ProviderDouble handler)
        => new UnitAllowance(Client(handler));

    /// <summary>
    /// Two symbol lists, `/api/user`, and a series per ticker. Twenty-two admitted
    /// common stocks across the two lists, which is more than one chunk at the
    /// configured concurrency, plus instruments D-4 excludes so the type filter is
    /// exercised rather than assumed.
    ///
    /// `eod/{t}` and the symbol list are billed at one unit each and `/api/user` at
    /// none, which is what 3.1 measured. Every one of them is a request.
    /// </summary>
    private sealed class ProviderDouble : HttpMessageHandler
    {
        private readonly int _alreadySpent;
        private readonly IReadOnlyDictionary<string, HttpStatusCode> _failures;

        private int _symbolListCalls;
        private int _seriesCalls;
        private int _userCalls;

        public ProviderDouble(
            int alreadySpent = 0, IReadOnlyDictionary<string, HttpStatusCode>? failures = null)
        {
            _alreadySpent = alreadySpent;
            _failures = failures ?? new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal);
        }

        public int SymbolListCalls => Volatile.Read(ref _symbolListCalls);

        public int SeriesCalls => Volatile.Read(ref _seriesCalls);

        /// <summary>Gate reads. One per chunk since 3.6; one per ticker before it.</summary>
        public int UserCalls => Volatile.Read(ref _userCalls);

        /// <summary>Every request, billed or not. The provider's rate limiter counts these.</summary>
        public int TotalRequests => SymbolListCalls + SeriesCalls + UserCalls;

        /// <summary>What the provider would have billed. `/api/user` is free [3.1].</summary>
        public int BillableUnits => SymbolListCalls + SeriesCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();

            string body;

            if (url.Contains("/user?", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _userCalls);

                body = string.Format(
                    CultureInfo.InvariantCulture,
                    """{{"apiRequests":{0},"apiRequestsDate":"{1}","dailyRateLimit":100000,"extraLimit":0}}""",
                    _alreadySpent + BillableUnits,
                    ProviderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            else if (url.Contains("exchange-symbol-list", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _symbolListCalls);

                body = url.Contains("delisted=1", StringComparison.Ordinal)
                    ? Symbols([("D01", "Common Stock"), ("D02", "Common Stock"), ("DFUND", "FUND")])
                    : Symbols(Enumerable.Range(1, 20)
                        .Select(i => ("L" + i.ToString("D2", CultureInfo.InvariantCulture), "Common Stock"))
                        .Concat([("LETF", "ETF"), ("LFUND", "Mutual Fund")])
                        .ToArray());
            }
            else if (url.Contains("/eod/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _seriesCalls);

                var ticker = TickerIn(url);
                if (_failures.TryGetValue(ticker, out var status))
                {
                    return Task.FromResult(new HttpResponseMessage(status)
                    {
                        Content = new StringContent(
                            status == HttpStatusCode.NotFound ? "Symbol not found" : "refused",
                            Encoding.UTF8, "text/plain"),
                    });
                }

                body = """[{"date":"2021-01-04","open":1,"high":2,"low":1,"close":2,"adjusted_close":2,"volume":10}]""";
            }
            else
            {
                throw new InvalidOperationException("Unexpected call in a test double: " + url);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private static string TickerIn(string url)
        {
            var start = url.IndexOf("/eod/", StringComparison.Ordinal) + "/eod/".Length;
            var end = url.IndexOf('?', start);
            return end < 0 ? url[start..] : url[start..end];
        }

        private static string Symbols((string Code, string Type)[] rows)
            => "[" + string.Join(",", rows.Select(r => string.Format(
                CultureInfo.InvariantCulture,
                """{{"Code":"{0}","Name":"{0} Inc","Country":"USA","Exchange":"NASDAQ","Currency":"USD","Type":"{1}","Isin":""}}""",
                r.Code, r.Type))) + "]";
    }
}
