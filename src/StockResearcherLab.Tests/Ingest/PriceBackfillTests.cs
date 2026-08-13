using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
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
///
/// **This class runs the real component under its real name, so its `run_log` rows are
/// indistinguishable from a real sweep's** [item 22]. It cleared them before each test
/// and not after, and the halted row the last test left stood in front of the first
/// real 3.6 sweep: `BackfillRun` would have resumed from `L07.US` and skipped every
/// admitted ticker below it. `DisposeAsync` clears now, which stops the ordinary case
/// arriving; it does not close the case of a test crashing before it, and that is why
/// the range-matching rule rather than this is what carries the weight.
/// </summary>
public sealed class PriceBackfillTests : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Runs after every test in this class, passing or failing, which is the half
    /// `SeedAsync` could not do by clearing first.
    /// </summary>
    public async ValueTask DisposeAsync()
        => await ClearRunLogAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

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

    // ------------------------ pooling, the frontier and the retry [item 23] ---

    /// <summary>
    /// **Physical connection opens against the worker count, measured rather than
    /// argued.** The 3.6 sweep failed at a `TimeoutException` inside `AuthenticateSASL`,
    /// which only runs when a physical connection is established, and the question that
    /// raised was whether `BulkUpsertAsync` opening a connection per call defeats
    /// pooling and pays a TCP, TLS and SASL round trip per ticker.
    ///
    /// It does not. `pg_stat_database.sessions` is a cumulative count of sessions
    /// established, so its delta across a few hundred tickers is the answer, and the
    /// answer is the worker count rather than the ticker count.
    ///
    /// **Asserted as an upper bound rather than an equality.** The floor is the pool
    /// growing to the configured concurrency; above that the number depends on how the
    /// workers interleave, and other connections this test's own harness opens land in
    /// the same counter. What the bound rules out is the defect: one open per ticker.
    /// </summary>
    [Fact]
    public async Task ConnectionsArePooledSoAFewHundredTickersOpenAboutTheWorkerCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        const int Tickers = 400;

        var handler = new ProviderDouble(liveCount: Tickers);

        var before = await SessionsAsync(ct).ConfigureAwait(true);
        var result = await RunAsync(handler, ct).ConfigureAwait(true);
        var after = await SessionsAsync(ct).ConfigureAwait(true);

        var opens = after - before;
        var concurrency = await ConcurrencyAsync(ct).ConfigureAwait(true);

        // The sweep really did walk the pool, so the measurement is not over nothing.
        Assert.False(result.WasHalted);
        Assert.Equal(Tickers + 2, handler.SeriesCalls);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"physical opens {opens} over {handler.SeriesCalls} tickers at concurrency {concurrency}");

        // The defect this rules out is one open per ticker. A generous multiple of the
        // worker count still sits two orders of magnitude below that.
        Assert.True(opens <= (concurrency * 4) + 8,
            $"{opens} physical connection open(s) over {handler.SeriesCalls} ticker(s) at concurrency " +
            $"{concurrency}. Pooling has stopped working, and a sweep pays a TCP, TLS and SASL round " +
            "trip per ticker.");
    }

    /// <summary>
    /// **A failure records the lowest ticker still in flight** [item 23]. The completed
    /// set is not a prefix, because workers finish out of order; the frontier is, so
    /// every ticker strictly below the minimum in-flight one was dispatched and
    /// finished.
    ///
    /// The fixture is a middle ticker whose series comes back as an object, which
    /// `LoadSeriesAsync` calls a shape change and throws on. Lower tickers in earlier
    /// chunks have completed.
    /// </summary>
    [Fact]
    public async Task AFailureRecordsTheLowestTickerStillInFlight()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        // The 200th of 400, so many chunks completed before it.
        const string Throws = "L0200.US";

        var handler = new ProviderDouble(liveCount: 400, malformed: Throws);

        await Assert.ThrowsAsync<RangeExecutionFailedException>(
            () => RunAsync(handler, ct)).ConfigureAwait(true);

        var row = await LastRangeRowAsync(ct).ConfigureAwait(true);

        Assert.Equal("failed", row.Status);

        var position = PositionIn(row.Error);

        // A position at all, which the old rule could not produce.
        Assert.NotNull(position);

        // **Never above the ticker that threw**, which is the property that matters: a
        // position above it would skip the failure itself and everything between.
        Assert.True(string.CompareOrdinal(position, Throws) <= 0,
            $"recorded position {position} is above the ticker that threw, {Throws}.");

        // And it is inside the failing ticker's own chunk rather than back at the start
        // of the pool, so the blast radius really is bounded by the concurrency.
        var concurrency = await ConcurrencyAsync(ct).ConfigureAwait(true);
        var pool = await new PriceIngestor(Client(new ProviderDouble(liveCount: 400)))
            .PoolAsync(ct).ConfigureAwait(true);

        var throwsAt = pool.ToList().IndexOf(Throws);
        var positionAt = pool.ToList().IndexOf(position!);

        Assert.True(throwsAt - positionAt < concurrency,
            $"position {position} is {throwsAt - positionAt} tickers below {Throws}, which is more than " +
            $"the concurrency of {concurrency}. The blast radius is meant to be one chunk.");

        // The line says which kind of position this is, because a halt and a failure
        // mean different things to whoever reads the log.
        Assert.Contains("FAILED rather than halted", row.Error ?? "", StringComparison.Ordinal);

        // And the retry count is stated, including its zero.
        Assert.Contains("connection open(s) retried", row.Error ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// **The resume re-dispatches the recorded ticker and everything above it**, so the
    /// failure costs at most one chunk rather than the sweep. It re-fetches the position
    /// itself rather than starting after it, which is what makes the ticker that threw
    /// get another attempt.
    /// </summary>
    [Fact]
    public async Task AResumeFromAFailurePositionReDispatchesThatTickerAndEverythingAbove()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        const string Throws = "L0200.US";

        await Assert.ThrowsAsync<RangeExecutionFailedException>(
            () => RunAsync(new ProviderDouble(liveCount: 400, malformed: Throws), ct)).ConfigureAwait(true);

        var position = PositionIn((await LastRangeRowAsync(ct).ConfigureAwait(true)).Error)!;

        // The same range, so the resume is not refused, and nothing malformed this time.
        var second = new ProviderDouble(liveCount: 400);
        var result = await RunAsync(second, ct).ConfigureAwait(true);

        Assert.False(result.WasHalted);

        var pool = await new PriceIngestor(Client(new ProviderDouble(liveCount: 400)))
            .PoolAsync(ct).ConfigureAwait(true);

        var expected = pool.Count(t => string.CompareOrdinal(t, position) >= 0);

        // Exactly the tail from the recorded position, inclusive. Two symbol-list calls
        // are not series calls and are not counted here.
        Assert.Equal(expected, second.SeriesCalls);

        // The one that threw is in that set rather than skipped past.
        Assert.True(string.CompareOrdinal(Throws, position) >= 0);
    }

    // ------------------------------------------------ leaving nothing [22] ---

    /// <summary>
    /// **The cleanup is checked against the table rather than assumed** [item 22]. A
    /// test cannot assert what its own `DisposeAsync` does after it, so this asserts the
    /// thing `DisposeAsync` calls: a sweep writes a range row, and the clear removes it.
    ///
    /// The row this leaves behind is the one that stood in front of the first real 3.6
    /// sweep, so the count going to zero is the whole of what changed here.
    /// </summary>
    [Fact]
    public async Task TheHarnessLeavesNoRangeRowBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var handler = new ProviderDouble(alreadySpent: 49_995);
        var result = await RunAsync(handler, ct).ConfigureAwait(true);

        // The sweep really did write one, so the assertion below is not vacuous.
        Assert.True(result.WasHalted);
        Assert.Equal(1, await RangeRowCountAsync(ct).ConfigureAwait(true));

        await ClearRunLogAsync(ct).ConfigureAwait(true);

        Assert.Equal(0, await RangeRowCountAsync(ct).ConfigureAwait(true));
    }

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
        await ClearRunLogAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// This stage's `run_log` rows, gone. Called before each test and again from
    /// `DisposeAsync` after it [item 22].
    /// </summary>
    private static async Task ClearRunLogAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "DELETE FROM run_log WHERE stage = 'PriceIngestor';", conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sessions established against this database, cumulative since the stats were
    /// last reset. The delta across a sweep is its physical connection opens, which is
    /// the number pooling is asserted through.
    /// </summary>
    private static async Task<long> SessionsAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT sessions FROM pg_stat_database WHERE datname = current_database();", conn);

        return (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>The worker count the sweep actually used, read from the seeded config rather than assumed.</summary>
    private static async Task<int> ConcurrencyAsync(CancellationToken ct)
        => (int) ConfigValue.Long(await new ConfigStore(TestDatabase.ConnectionString)
            .RequireAsync("backfill.ticker_concurrency", new DateOnly(2021, 1, 8), ct).ConfigureAwait(false));

    /// <summary>The newest range row for this stage.</summary>
    private static async Task<(string Status, string? Error)> LastRangeRowAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT status, error FROM run_log WHERE stage = 'PriceIngestor' AND error LIKE 'range %' " +
            "ORDER BY run_log_id DESC LIMIT 1;", conn);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        Assert.True(await r.ReadAsync(ct).ConfigureAwait(false), "No range row for PriceIngestor.");

        return (r.GetString(0), await r.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : r.GetString(1));
    }

    /// <summary>
    /// The ticker out of a run log line, the same way `RunLog` reads it. Duplicated
    /// here rather than reaching for the internal, because the parser is private and
    /// this asserts what the line says rather than what the parser does.
    /// </summary>
    private static string? PositionIn(string? error)
    {
        if (error is null)
        {
            return null;
        }

        const string marker = " at ";
        var at = error.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = error[(at + marker.Length)..];
        var stop = rest.IndexOf(". ", StringComparison.Ordinal);

        return (stop < 0 ? rest : rest[..stop]).Trim().TrimEnd('.') is { Length: > 0 } p ? p : null;
    }

    private static async Task<long> RangeRowCountAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM run_log WHERE stage = 'PriceIngestor' AND error LIKE 'range %';", conn);

        return (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
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

        private readonly int _liveCount;
        private readonly string? _malformed;

        /// <param name="liveCount">How many live common stocks the symbol list carries.</param>
        /// <param name="malformed">
        /// A ticker whose series comes back as an object rather than an array, which
        /// `LoadSeriesAsync` treats as a shape change and throws on. It is a per-ticker
        /// throw inside the parallel body, which is what the frontier rule is asserted
        /// through.
        /// </param>
        public ProviderDouble(
            int alreadySpent = 0, IReadOnlyDictionary<string, HttpStatusCode>? failures = null,
            int liveCount = 20, string? malformed = null)
        {
            _alreadySpent = alreadySpent;
            _failures = failures ?? new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal);
            _liveCount = liveCount;
            _malformed = malformed;
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
                    // Two digits at the default twenty, which is what every assertion
                    // naming `L07.US` reads, and four above it so a few hundred still
                    // sort ordinally: `L100` sorts below `L20` and the frontier rule is
                    // asserted on that order.
                    : Symbols(Enumerable.Range(1, _liveCount)
                        .Select(i => ("L" + i.ToString(
                            _liveCount <= 99 ? "D2" : "D4", CultureInfo.InvariantCulture), "Common Stock"))
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

                // An object rather than an array. `LoadSeriesAsync` calls that a shape
                // change and throws, which is a per-ticker throw inside the parallel
                // body: the frontier rule's fixture.
                body = string.Equals(ticker, _malformed, StringComparison.Ordinal)
                    ? "{}"
                    : """[{"date":"2021-01-04","open":1,"high":2,"low":1,"close":2,"adjusted_close":2,"volume":10}]""";
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
