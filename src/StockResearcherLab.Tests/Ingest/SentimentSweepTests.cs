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
/// C04's range mode end to end, which open item 33 recorded as owed and the developer
/// database made impossible to write.
///
/// **What was blocking it was the store and not the stage.** C04's live half is the
/// universe as of its date, read from `security_daily`, so a range execution in a test
/// walked whatever universe the connected database held and stamped
/// `sentiment_fetch_attempt` for every real ticker in it. The suite now derives and
/// prepares its own database [item 26, 3.13], so the universe an execution here walks is
/// the fixture's and a stamped attempt row is a fixture row.
///
/// **The property is D-99's and it is the one a multi-day sweep is run on**: a halted
/// sweep resumes over the tickers carrying no attempt row for this range and does not ask
/// again for one it already fetched. C02 had this asserted and C04 did not, and the two
/// stamp different ends, so the property holding for one is not evidence about the other.
///
/// **The assertions are made over the run's own dispatch sets rather than over a
/// hardcoded pool.** Three fixtures seed `security_daily` at 2000-01-01, so the live half
/// here carries names this class does not own and cannot predict, and a test asserting a
/// literal pool would fail on another fixture's rows rather than on this stage. What is
/// asserted instead is exactly what D-99 promises and nothing about the pool's
/// composition: no name is dispatched twice, the fixture's own complement is dispatched
/// both directions, and a third run asks for nothing at all. A pool the fixture cannot
/// enumerate weakens none of those.
/// </summary>
[Collection("database")]
public sealed class SentimentSweepTests
{
    private const string Prefix = "SRLSWP";

    /// <summary>
    /// The range. **Both ends sit after `ConfigSeeder.SeedInstant` and before
    /// `backfill.window_start`**: the first because config resolved for a date before the
    /// seed instant resolves to no version at all and fails the run, and the second so a
    /// fixture attempt row can never be read as one a real sweep wrote [D-99, item 22].
    /// </summary>
    private static readonly DateOnly From = new(2020, 2, 3);

    private static readonly DateOnly To = new(2020, 2, 28);

    private static readonly DateTimeOffset Now = new(2026, 8, 5, 2, 52, 0, TimeSpan.Zero);

    private static DateOnly ProviderDate => DateOnly.FromDateTime(Now.UtcDateTime);

    /// <summary>`sentiment.tickers_per_call`, which decides where the halt can land.</summary>
    private const int Batch = 50;

    /// <summary>
    /// Sixty live members, which is more than one batch and is the whole reason for the
    /// number. A pool inside one batch cannot halt part way and cannot resume.
    /// </summary>
    private const int Live = 60;

    /// <summary>
    /// Delisted, with a bar inside the window, so D-101's half of the pool is exercised
    /// rather than assumed. Ordinally after every numbered name, so it lands in the
    /// second batch with them.
    /// </summary>
    private const string Delisted = Prefix + "GONE.US";

    /// <summary>
    /// Spent units that leave room for exactly one batch.
    ///
    /// The delisted symbol list bills one, so the first gate sees 49,701 of 100,000
    /// against a reserve of 50,000, which is 299 above it: a batch of fifty at five units
    /// a ticker is 250 and fits. That batch then bills 250, so the second gate sees 49
    /// above the reserve and a batch of eleven at 55 does not. **The arithmetic is the
    /// provider's own**: nothing tells the handler what was spent and it bills what it
    /// serves, so the halt is the gate's decision rather than the test's.
    /// </summary>
    private const int RoomForOneBatch = 49_700;

    /// <summary>
    /// **The property 3.9's sweep will be run on**, asserted through C04 rather than
    /// reasoned across from C02.
    /// </summary>
    [Fact]
    public async Task AHaltedSweepResumesOverExactlyTheTickersWithNoAttemptRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var first = new SweepHandler(alreadySpent: RoomForOneBatch);
        var halted = await RunAsync(first, ct).ConfigureAwait(true);

        Assert.True(halted.WasHalted);

        // The halt lands on a batch boundary rather than racily, which is what makes the
        // complement below a set the next run can be checked against.
        Assert.Equal(Batch, first.Asked.Count);

        var attemptedAfterHalt = await AttemptedAsync(ct).ConfigureAwait(true);
        Assert.Equal(
            first.Asked.Where(IsFixture).Order(StringComparer.Ordinal),
            attemptedAfterHalt.Where(IsFixture));

        // A fresh provider day. Nothing tells the sweep where it stopped; it asks the
        // store what carries no attempt row for this range.
        var second = new SweepHandler(alreadySpent: 0);
        var completed = await RunAsync(second, ct).ConfigureAwait(true);

        Assert.False(completed.WasHalted);

        // **No name is asked for twice**, which is the half that costs money and the half
        // a restart of the same size is indistinguishable from by row count.
        Assert.Empty(first.Asked.Intersect(second.Asked, StringComparer.Ordinal));

        // The fixture's own complement, both directions. Neither a restart nor a skip.
        Assert.Equal(
            Fixture().Except(first.Asked, StringComparer.Ordinal).Order(StringComparer.Ordinal),
            second.Asked.Where(IsFixture).Order(StringComparer.Ordinal));

        Assert.Equal(Fixture().Order(StringComparer.Ordinal), await FixtureAttemptedAsync(ct).ConfigureAwait(true));
    }

    /// <summary>
    /// A sweep that finished asks for nothing when it is run again, which is what makes a
    /// re-invocation safe rather than a second bill. **The pool is refetched on every run
    /// and the delisted list is served again**, so this also asserts that the second cost
    /// is one symbol list rather than one symbol list plus the whole universe.
    /// </summary>
    [Fact]
    public async Task ACompletedSweepReInvokedOverTheSameRangeDispatchesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var first = new SweepHandler(alreadySpent: 0);
        var completed = await RunAsync(first, ct).ConfigureAwait(true);

        Assert.False(completed.WasHalted);
        Assert.Contains(Delisted, first.Asked, StringComparer.Ordinal);
        Assert.True(first.Asked.Count >= Live + 1,
            $"The sweep asked for {first.Asked.Count} names against a fixture of {Live + 1}.");

        var again = new SweepHandler(alreadySpent: 0);
        var result = await RunAsync(again, ct).ConfigureAwait(true);

        Assert.Empty(again.Asked);
        Assert.Equal(0, result.RowsWritten);
        Assert.False(result.WasHalted);

        // **And it says so in its status rather than only in its count** [3.16]. The
        // sequence driver halts on a source that writes no row, and this zero is the one
        // case where that is the completed state rather than a short table.
        Assert.True(result.WasCovered);
    }

    /// <summary>
    /// **A ticker nobody wrote about is attempted once and not asked again** [D-12].
    ///
    /// The endpoint returns no days for it, and a resumption keyed on rows in
    /// `sentiment_daily` would call it never fetched and re-ask it for the life of the
    /// sweep. Those are exactly the thinly covered names S3 exists to find, so the one
    /// that costs the most to re-ask is the one the design most wants kept.
    /// </summary>
    [Fact]
    public async Task ATickerThatReturnsNoDaysIsAttemptedOnceAndNotAskedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var silent = Ticker(1);

        var first = new SweepHandler(alreadySpent: 0) { Silent = silent };
        await RunAsync(first, ct).ConfigureAwait(true);

        Assert.Contains(silent, first.Asked, StringComparer.Ordinal);
        Assert.Contains(silent, await FixtureAttemptedAsync(ct).ConfigureAwait(true));
        Assert.Equal(0, await DaysAsync(silent, ct).ConfigureAwait(true));

        var again = new SweepHandler(alreadySpent: 0) { Silent = silent };
        await RunAsync(again, ct).ConfigureAwait(true);

        Assert.Empty(again.Asked);
    }

    // ----------------------------------------------------------- harness ---

    private static string Ticker(int i)
        => Prefix + i.ToString("000", CultureInfo.InvariantCulture) + ".US";

    private static IReadOnlyList<string> Fixture()
        => [.. Enumerable.Range(1, Live).Select(Ticker), Delisted];

    private static bool IsFixture(string ticker)
        => ticker.StartsWith(Prefix, StringComparison.Ordinal);

    private static async Task<BackfillResult> RunAsync(SweepHandler handler, CancellationToken ct)
    {
        var stage = new SentimentIngestor(Client(handler));

        var run = new BackfillRun(
            new StageRegistry([stage]),
            new RunLog(TestDatabase.ConnectionString),
            new FixedClock(Now, To),
            TestDatabase.ConnectionString,
            new UnitAllowance(Client(handler)));

        return await run.RunAsync(stage.Name, From, To, ct).ConfigureAwait(false);
    }

    private static EodhdClient Client(SweepHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
            "test-token", new FixedClock(Now, To));

    /// <summary>
    /// Every ticker carrying an attempt at this range's start.
    ///
    /// **Sorted in C# rather than by the statement**, because the server's collation is
    /// not ordinal and every comparison here is. A `ORDER BY ticker` compared against
    /// `StringComparer.Ordinal` agrees on this fixture's names and would stop agreeing on
    /// the first one that mixed case.
    /// </summary>
    private static async Task<IReadOnlyList<string>> AttemptedAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT ticker FROM sentiment_fetch_attempt WHERE last_attempted_date = @d;", conn);
        cmd.Parameters.AddWithValue("d", From);

        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) rows.Add(r.GetString(0));

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static async Task<IReadOnlyList<string>> FixtureAttemptedAsync(CancellationToken ct)
        => (await AttemptedAsync(ct).ConfigureAwait(false)).Where(IsFixture).ToList();

    private static async Task<long> DaysAsync(string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM sentiment_daily WHERE ticker = @t;", conn);
        cmd.Parameters.AddWithValue("t", ticker);
        return (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// The fixture's rows, and the rows any run of it leaves on names it does not own.
    ///
    /// **The attempt clear is by date and that is safe here for a reason that did not
    /// exist before 3.13**: this is the suite's own database, and the date is one no real
    /// sweep can carry, sitting before `backfill.window_start`. On the developer store
    /// the same statement is what took a real sweep's record [item 22].
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM sentiment_fetch_attempt WHERE last_attempted_date = @d;", conn))
        {
            cmd.Parameters.AddWithValue("d", From);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM sentiment_daily WHERE date BETWEEN @f AND @t;", conn))
        {
            cmd.Parameters.AddWithValue("f", From);
            cmd.Parameters.AddWithValue("t", To);
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

        for (var i = 1; i <= Live; i++)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, 'SRLTEST-SWP', 'SRLTEST-SWP', 1000000000, true)
                ON CONFLICT (ticker, date) DO UPDATE SET is_active = true;
                """, conn);
            cmd.Parameters.AddWithValue("t", Ticker(i));
            cmd.Parameters.AddWithValue("d", From);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // The delisted half. It is not in `security_daily`, which is the point: it reaches
        // the pool through the symbol list and its bar inside the window [D-101].
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
    /// One delisted symbol list, `/api/user`, and one `sentiments` call per batch.
    ///
    /// **The counter is the provider's, not the stage's.** It bills one unit a symbol
    /// list and five per ticker in a batch, which is `backfill.weight_sentiments_per_ticker`
    /// and is what the endpoint was measured at, so the gate halts on its own arithmetic
    /// rather than on a number this test chose.
    /// </summary>
    private sealed class SweepHandler(int alreadySpent) : HttpMessageHandler
    {
        private int _symbolListCalls;
        private int _tickersServed;

        /// <summary>Every ticker this handler was asked for, in order.</summary>
        public List<string> Asked { get; } = [];

        /// <summary>A ticker the endpoint returns no days for, standing for a name nobody wrote about.</summary>
        public string? Silent { get; init; }

        private int Billable => _symbolListCalls + (_tickersServed * 5);

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

                // **One admitted name, never an empty list.** `SymbolList` treats a list
                // with no `Common Stock` as a shape change rather than an empty exchange,
                // which is the guard that stops a silently emptied universe.
                return Json(string.Format(
                    CultureInfo.InvariantCulture,
                    """[{{"Code":"{0}","Name":"{0} Inc","Type":"Common Stock"}}]""",
                    Delisted[..^3]));
            }

            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var tickers = Symbols(query);

            Asked.AddRange(tickers);
            Interlocked.Add(ref _tickersServed, tickers.Count);

            var day = From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var body = string.Join(",", tickers
                .Where(t => !string.Equals(t, Silent, StringComparison.Ordinal))
                .Select(t => "\"" + t + "\":[{\"date\":\"" + day + "\",\"count\":3,\"normalized\":0.25}]"));

            return Json("{" + body + "}");
        }

        /// <summary>
        /// The `s=` parameter, unescaped. Read from the request rather than tracked by
        /// this class, so what is asserted is what was sent.
        /// </summary>
        private static IReadOnlyList<string> Symbols(string query)
        {
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                if (part.StartsWith("s=", StringComparison.Ordinal))
                {
                    return part[2..].Split(',', StringSplitOptions.RemoveEmptyEntries);
                }
            }

            return [];
        }

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
