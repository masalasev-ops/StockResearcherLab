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
/// **This class runs the real component under its real name against the real database,
/// so what it writes is indistinguishable from a real sweep's** [item 22]. That has cut
/// both ways already. A halted `run_log` row it left behind stood in front of the first
/// real 3.6 sweep, which would have resumed from `L07.US` and skipped every admitted
/// ticker below it; the `DisposeAsync` clear added for that then deleted the real failed
/// sweep's row, because it deletes by stage name and the stage name is the same one.
///
/// **Resumption now lives in `price_fetch_attempt`, so the same collision would skip
/// tickers rather than lose a log line.** Two things keep it off the real rows, and
/// neither is a promise to remember something:
///
/// The fixture range starts at a date no real sweep uses, `backfill.window_start` being
/// 2021-01-04, so a fixture attempt is never read as a real one.
///
/// The clear names the fixture's own tickers rather than a date or a prefix. Deleting by
/// `last_attempted_date` would take out a real sweep's whole record, and deleting by
/// `L%` would take out real tickers.
///
/// **What is left costs a re-fetch and cannot cost a skip.** If a fixture ticker name
/// collides with a real one, the fixture overwrites that ticker's attempt row and the
/// next real sweep fetches it again for one unit. It cannot make a real sweep skip a
/// ticker, because a fixture row carries the fixture's date and the sweep reads its own.
/// </summary>
public sealed class PriceBackfillTests : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Runs after every test in this class, passing or failing, which is the half
    /// `SeedAsync` could not do by clearing first.
    /// </summary>
    public async ValueTask DisposeAsync()
        => await ClearAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

    private static readonly DateTimeOffset Now = new(2026, 8, 12, 2, 52, 0, TimeSpan.Zero);

    private static IClock Clock => new FixedClock(Now, new DateOnly(2026, 8, 11));

    private static DateOnly ProviderDate => DateOnly.FromDateTime(Now.UtcDateTime);

    /// <summary>
    /// The fixture range. **The start is deliberately not `backfill.window_start`**,
    /// which is 2021-01-04: attempts are stamped with the range start, so sharing one
    /// with the real sweep would let a fixture row be read as a real one.
    /// </summary>
    private static readonly DateOnly From = new(2019, 6, 3);

    /// <summary>
    /// The range end, which is what config resolves as of, so it stays inside the
    /// seeded configuration's life [D-72, D-94].
    /// </summary>
    private static readonly DateOnly To = new(2021, 1, 8);

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
    /// The gate stops the sweep at a chunk boundary rather than mid-flight, and what it
    /// reached is in the attempt record rather than in a position [0010].
    /// </summary>
    [Fact]
    public async Task TheSweepHaltsAtAChunkBoundaryAndOnlyTheDispatchedTickersCarryAnAttempt()
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

        // Eight attempts and no ninth. `L07.US` is the ninth ticker ordinally and the
        // first one not dispatched, so its absence is what the next run reads.
        var attempted = await AttemptedAsync(ct).ConfigureAwait(true);

        Assert.Equal(8, attempted.Count);
        Assert.DoesNotContain("L07.US", attempted);
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
    /// The next run picks up the tickers with no attempt row rather than starting over,
    /// which is the whole of what the record is for [0010].
    /// </summary>
    [Fact]
    public async Task TheNextRunDispatchesOnlyTheTickersWithNoAttemptForThisRange()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var first = new ProviderDouble(alreadySpent: 49_995);
        var halted = await RunAsync(first, ct).ConfigureAwait(true);

        Assert.True(halted.WasHalted);
        Assert.Equal(8, first.SeriesCalls);

        // Room to finish this time.
        var second = new ProviderDouble();
        var done = await RunAsync(second, ct).ConfigureAwait(true);

        Assert.False(done.WasHalted);

        // Fourteen rather than twenty-two: the eight the halted run loaded carry an
        // attempt for this range and are not asked for again.
        Assert.Equal(14, second.SeriesCalls);
        Assert.Equal(22, (await AttemptedAsync(ct).ConfigureAwait(true)).Count);
        Assert.Contains("8 carried an attempt", done.Detail ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// **A completed sweep re-invoked over the same range dispatches nothing** [0010].
    ///
    /// The two symbol-list calls still happen, the pool being what the difference is
    /// taken against, and not one `eod/{t}` follows them. Before the attempt record this
    /// was the case that spent a whole day of allowance re-fetching a finished load,
    /// which is what happened on 2026-08-13.
    /// </summary>
    [Fact]
    public async Task ACompletedSweepReInvokedOverTheSameRangeDispatchesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var first = new ProviderDouble();
        var done = await RunAsync(first, ct).ConfigureAwait(true);

        Assert.False(done.WasHalted);
        Assert.Equal(22, first.SeriesCalls);

        var again = new ProviderDouble();
        var second = await RunAsync(again, ct).ConfigureAwait(true);

        Assert.False(second.WasHalted);
        Assert.Equal(0, again.SeriesCalls);
        Assert.Equal(0, second.RowsWritten);
        Assert.Contains("22 carried an attempt", second.Detail ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// **A ticker that yields no rows is not re-fetched on the next run** [0010].
    ///
    /// This is the leak the presence predicate carries and the reason the record is of
    /// the attempt. A name the price endpoint answers `404` for writes no bars, so a
    /// test on rows in `price_daily` would call it never fetched and re-ask it on every
    /// run for ever. 0008 measured that at C05: fourteen of two hundred and fifty names,
    /// billed at ten units each, every night.
    ///
    /// `last_yield_date` is what keeps the two absences apart: null here, and an absent
    /// row for a ticker never dispatched.
    /// </summary>
    [Fact]
    public async Task ATickerThatYieldsNothingIsAttemptedOnceAndNotAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var failures = new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal)
        {
            ["L05.US"] = HttpStatusCode.NotFound,
        };

        var first = new ProviderDouble(failures: failures);
        await RunAsync(first, ct).ConfigureAwait(true);

        Assert.Equal(22, first.SeriesCalls);

        // Attempted, and recorded as having yielded nothing rather than as absent.
        var attempt = await AttemptAsync("L05.US", ct).ConfigureAwait(true);

        Assert.NotNull(attempt);
        Assert.Equal(From, attempt!.Value.Attempted);
        Assert.Null(attempt.Value.Yield);
        Assert.Equal(0, attempt.Value.Rows);

        // And a ticker that did yield carries the date, so null above is a fact rather
        // than the column never being written.
        var yielded = await AttemptAsync("L04.US", ct).ConfigureAwait(true);

        Assert.Equal(From, yielded!.Value.Yield);

        var second = new ProviderDouble(failures: failures);
        await RunAsync(second, ct).ConfigureAwait(true);

        Assert.Equal(0, second.SeriesCalls);
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
    /// **A sweep that dies resumes over exactly the tickers with no attempt row, and
    /// nothing else** [0010]. This is the property the frontier was reaching for and
    /// could only approximate: the completed set is not a prefix, because workers finish
    /// out of order, so a position had to be the lowest ticker still in flight and give
    /// back everything above it in that chunk.
    ///
    /// The fixture is a middle ticker whose series comes back as an object, which
    /// `LoadSeriesAsync` calls a shape change and throws on. It stands in for every way
    /// a sweep stops without saying so: the exception propagates out of the stage, and
    /// nothing about it is written down or read back.
    ///
    /// **The assertion is set equality rather than a count**, because a count is
    /// satisfied by re-dispatching the wrong tickers.
    /// </summary>
    [Fact]
    public async Task AFailedSweepResumesOverExactlyTheTickersWithNoAttemptRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        // The 200th of 400, so many chunks completed before it.
        const string Throws = "L0200.US";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(new ProviderDouble(liveCount: 400, malformed: Throws), ct)).ConfigureAwait(true);

        var row = await LastRangeRowAsync(ct).ConfigureAwait(true);

        Assert.Equal("failed", row.Status);
        Assert.Contains("FAILED", row.Error ?? "", StringComparison.Ordinal);

        // And the retry count is stated, including its zero [item 23].
        Assert.Contains("connection open(s) retried", row.Error ?? "", StringComparison.Ordinal);

        var attempted = await AttemptedAsync(ct).ConfigureAwait(true);

        // The failing chunk recorded nothing, so the ticker that threw is re-dispatched
        // rather than skipped past. That is the direction that matters: a re-fetch costs
        // a unit and a skip costs a hole no later stage can see.
        Assert.DoesNotContain(Throws, attempted);

        // The sweep really did get most of the way, so the assertion below is not being
        // made over an empty attempt set.
        Assert.True(attempted.Count > 100, $"only {attempted.Count} attempt row(s) before the throw.");

        var second = new ProviderDouble(liveCount: 400);
        var result = await RunAsync(second, ct).ConfigureAwait(true);

        Assert.False(result.WasHalted);

        var pool = await new PriceIngestor(Client(new ProviderDouble(liveCount: 400)))
            .PoolAsync(ct).ConfigureAwait(true);

        // Exactly the complement, by set rather than by count. Two symbol-list calls are
        // not series calls and are not counted here.
        Assert.Equal(
            pool.Where(t => !attempted.Contains(t)).ToList(),
            second.Fetched);

        // And afterwards every pool member carries one, so the two runs together are the
        // sweep the first one was meant to be.
        Assert.Equal(pool.Count, (await AttemptedAsync(ct).ConfigureAwait(true)).Count);
    }

    // ------------------------------------------------ leaving nothing [22] ---

    /// <summary>
    /// **The cleanup is checked against the tables rather than assumed** [item 22]. A
    /// test cannot assert what its own `DisposeAsync` does after it, so this asserts the
    /// thing `DisposeAsync` calls: a sweep writes a range row and a set of attempt rows,
    /// and the clear removes both.
    ///
    /// The attempt rows are the half that matters now. A run log row left behind is read
    /// by an operator; an attempt row left behind makes the next sweep skip a ticker.
    /// </summary>
    [Fact]
    public async Task TheHarnessLeavesNoRangeRowAndNoAttemptRowBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct).ConfigureAwait(true);

        var handler = new ProviderDouble(alreadySpent: 49_995);
        var result = await RunAsync(handler, ct).ConfigureAwait(true);

        // The sweep really did write both, so the assertions below are not vacuous.
        Assert.True(result.WasHalted);
        Assert.Equal(1, await RangeRowCountAsync(ct).ConfigureAwait(true));
        Assert.Equal(8, (await AttemptedAsync(ct).ConfigureAwait(true)).Count);

        await ClearAsync(ct).ConfigureAwait(true);

        Assert.Equal(0, await RangeRowCountAsync(ct).ConfigureAwait(true));
        Assert.Empty(await AttemptedAsync(ct).ConfigureAwait(true));
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
        await ClearAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What this class wrote, gone. Called before each test and again from
    /// `DisposeAsync` after it [item 22].
    ///
    /// **The attempt rows are deleted by ticker and never by date**, because the date is
    /// what a real sweep's whole record shares: one `DELETE ... WHERE
    /// last_attempted_date = ...` against the wrong date would erase the thing 0010
    /// exists to keep. The ticker list comes from the double rather than from a pattern,
    /// so it cannot widen by accident the way `LIKE 'L%'` would.
    ///
    /// The `run_log` clear still goes by stage name, and that is still the sharp edge
    /// that deleted the real failed sweep's row on 2026-08-13. It is survivable now
    /// rather than fixed: those rows are read by an operator and nothing resumes from
    /// them [0010, item 26].
    /// </summary>
    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = new Npgsql.NpgsqlCommand(
            "DELETE FROM run_log WHERE stage = 'PriceIngestor';", conn))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = new Npgsql.NpgsqlCommand(
            "DELETE FROM price_fetch_attempt WHERE ticker = ANY(@t);", conn))
        {
            cmd.Parameters.AddWithValue("t", ProviderDouble.EveryFixtureTicker());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The tickers carrying an attempt for the fixture range, ordinal.</summary>
    private static async Task<IReadOnlyList<string>> AttemptedAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT ticker FROM price_fetch_attempt WHERE ticker = ANY(@t) " +
            "AND last_attempted_date = @d ORDER BY ticker;", conn);
        cmd.Parameters.AddWithValue("t", ProviderDouble.EveryFixtureTicker());
        cmd.Parameters.AddWithValue("d", From);

        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(r.GetString(0));
        }

        return rows;
    }

    /// <summary>One attempt row, or null where the ticker has never been attempted.</summary>
    private static async Task<(DateOnly Attempted, DateOnly? Yield, long Rows)?> AttemptAsync(
        string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT last_attempted_date, last_yield_date, rows_last_attempt " +
            "FROM price_fetch_attempt WHERE ticker = @t;", conn);
        cmd.Parameters.AddWithValue("t", ticker);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return (DateOnly.FromDateTime(r.GetDateTime(0)),
                await r.IsDBNullAsync(1, ct).ConfigureAwait(false)
                    ? null
                    : DateOnly.FromDateTime(r.GetDateTime(1)),
                r.GetInt64(2));
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
            .RequireAsync("backfill.ticker_concurrency", To, ct).ConfigureAwait(false));

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

        return await run.RunAsync(stage.Name, From, To, ct).ConfigureAwait(false);
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

        private readonly List<string> _fetched = [];

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

        /// <summary>
        /// Which tickers were asked for, ordinal. Sorted here rather than recorded in
        /// order, because the workers finish out of order and an assertion against the
        /// arrival sequence would be asserting the scheduler.
        /// </summary>
        public IReadOnlyList<string> Fetched
        {
            get
            {
                lock (_fetched)
                {
                    var copy = _fetched.ToList();
                    copy.Sort(StringComparer.Ordinal);
                    return copy;
                }
            }
        }

        /// <summary>
        /// Every ticker any fixture in this class can produce, for a cleanup that names
        /// what it deletes instead of matching a pattern.
        ///
        /// Generated wider than the fixtures actually use, at both the two-digit and the
        /// four-digit width, so adding a `liveCount` between them does not silently leave
        /// rows behind. Deleting a name no test wrote is free.
        /// </summary>
        public static string[] EveryFixtureTicker()
            => [.. Enumerable.Range(1, 99).Select(i => "L" + i.ToString("D2", CultureInfo.InvariantCulture) + ".US"),
                .. Enumerable.Range(1, 999).Select(i => "L" + i.ToString("D4", CultureInfo.InvariantCulture) + ".US"),
                "LETF.US", "LFUND.US", "D01.US", "D02.US", "DFUND.US"];

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

                lock (_fetched)
                {
                    _fetched.Add(ticker);
                }

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
