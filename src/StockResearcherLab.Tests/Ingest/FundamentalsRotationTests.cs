using System.Globalization;
using System.Net;
using System.Text;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Data.Eodhd;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// The fundamentals rotation, which stopped rotating once coverage completed [0006].
///
/// C03 ordered never-fetched first, where fetched meant any row in
/// `fundamental_snapshot`. Once the pool was covered that group was empty and the
/// same alphabetically-first names were selected on every run afterwards, for ever.
/// Measured: `capital_expenditures` for 482 tickers running contiguously from `A.US`
/// to `CCBG.US`, and two consecutive runs writing an identical 44,365 rows over an
/// identical 500 tickers.
///
/// **These assert the selection, not the row count.** A frozen rotation writes a
/// perfectly healthy number of rows, which is exactly why it survived three runs and
/// a phase sign-off without anything catching it. The handler records which tickers
/// were asked for, and that sequence is the observable.
/// </summary>
[Collection("database")]
public sealed class FundamentalsRotationTests
{
    private const string Prefix = "SRLROT";

    /// <summary>
    /// Six pool members, two runs of two, so a third run has somewhere to go.
    ///
    /// The dash form the provider uses, code plus exchange, because
    /// `SymbolList.AdmittedAsync` keys its map on `Code + ".US"` and the bootstrap
    /// pool intersects `price_daily` with it. A bare code matches nothing and the
    /// pool comes back empty.
    /// </summary>
    private static readonly string[] Pool =
    [
        Prefix + "A.US", Prefix + "B.US", Prefix + "C.US",
        Prefix + "D.US", Prefix + "E.US", Prefix + "F.US",
    ];

    private static readonly DateOnly Day1 = new(2026, 8, 3);
    private static readonly DateOnly Day2 = new(2026, 8, 4);
    private static readonly DateOnly Day3 = new(2026, 8, 5);

    // -------------------------------------------------------- the rotation ---

    /// <summary>
    /// The defect itself. Before 0006 both runs returned the same first two names,
    /// because "never fetched" was empty after run one and ticker ordinal decided
    /// everything after it.
    /// </summary>
    [Fact]
    public async Task TwoConsecutiveRunsOverAnUnchangedPoolSelectDisjointSets()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var handler = new FundamentalsHandler();
        var first = await RunAsync(handler, Day1, maxPerRun: 2, ct).ConfigureAwait(true);
        var second = await RunAsync(handler, Day2, maxPerRun: 2, ct).ConfigureAwait(true);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Empty(first.Intersect(second, StringComparer.Ordinal));
    }

    /// <summary>
    /// Coverage completes at run three of six-over-two, and the rotation has to keep
    /// going rather than freezing on whatever it selected last. A fourth run cycling
    /// back to the first pair is the rotation working, not repeating.
    /// </summary>
    [Fact]
    public async Task TheRotationKeepsCyclingAfterCoverageCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var handler = new FundamentalsHandler();
        var first = await RunAsync(handler, Day1, maxPerRun: 2, ct).ConfigureAwait(true);
        var second = await RunAsync(handler, Day2, maxPerRun: 2, ct).ConfigureAwait(true);
        var third = await RunAsync(handler, Day3, maxPerRun: 2, ct).ConfigureAwait(true);

        Assert.Empty(third.Intersect(first, StringComparer.Ordinal));
        Assert.Empty(third.Intersect(second, StringComparer.Ordinal));

        // Every pool member reached exactly once, which is the property a rotation
        // has and an alphabetical head does not.
        Assert.Equal(
            Pool.OrderBy(t => t, StringComparer.Ordinal),
            first.Concat(second).Concat(third).OrderBy(t => t, StringComparer.Ordinal));

        // And it cycles rather than stopping: run four returns to the oldest pair.
        var fourth = await RunAsync(handler, Day3.AddDays(1), maxPerRun: 2, ct).ConfigureAwait(true);
        Assert.Equal(first.OrderBy(t => t, StringComparer.Ordinal), fourth.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// The case a `fetched_at` column on `fundamental_snapshot` could not have
    /// fixed, and the reason the record is of the attempt.
    ///
    /// A ticker the endpoint carries no financials for answers with a bare string
    /// rather than an object, so it writes no row. Keyed on rows written, its
    /// freshness would never move and it would sit at the head of the rotation on
    /// every run afterwards. It is the fourteen-404s defect from the flow ingest,
    /// one component over.
    /// </summary>
    [Fact]
    public async Task ATickerWhoseFetchReturnsNoRowsDoesNotReappearInTheNextRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        // The first two selected both answer with the bare-string shape.
        var handler = new FundamentalsHandler(bareString: [Pool[0], Pool[1]]);

        var first = await RunAsync(handler, Day1, maxPerRun: 2, ct).ConfigureAwait(true);
        var second = await RunAsync(handler, Day2, maxPerRun: 2, ct).ConfigureAwait(true);

        Assert.Equal([Pool[0], Pool[1]], first.OrderBy(t => t, StringComparer.Ordinal));
        Assert.Empty(second.Intersect(first, StringComparer.Ordinal));

        // Attempted and recorded, with no yield date, which is the fact that
        // distinguishes it from a name nobody has asked about.
        var (attempted, yielded, rows) = await AttemptAsync(Pool[0], ct).ConfigureAwait(true);
        Assert.Equal(Day1, attempted);
        Assert.Null(yielded);
        Assert.Equal(0, rows);
    }

    /// <summary>
    /// A yield date already recorded is not erased by a later empty attempt. Without
    /// this the two absences collapse back into each other after one bad night.
    /// </summary>
    [Fact]
    public async Task AnEmptyAttemptDoesNotEraseAnEarlierYieldDate()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var yielding = new FundamentalsHandler();
        await RunAsync(yielding, Day1, maxPerRun: 6, ct).ConfigureAwait(true);

        var empty = new FundamentalsHandler(bareString: Pool);
        await RunAsync(empty, Day2, maxPerRun: 6, ct).ConfigureAwait(true);

        var (attempted, yielded, rows) = await AttemptAsync(Pool[0], ct).ConfigureAwait(true);
        Assert.Equal(Day2, attempted);
        Assert.Equal(Day1, yielded);
        Assert.Equal(0, rows);
    }

    // ------------------------------------------------------- determinism ---

    /// <summary>
    /// Two runs over the same date select the same names. The rotation advances
    /// between dates, never between runs, because a stage is a pure function of its
    /// date and config version [CLAUDE.md section 6].
    ///
    /// This is what the strictly-before read buys, and it is the property that would
    /// be lost by the obvious implementation of ordering by an attempt timestamp.
    /// </summary>
    [Fact]
    public async Task TwoRunsOverTheSameDateSelectTheSameNames()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var handler = new FundamentalsHandler();
        var first = await RunAsync(handler, Day1, maxPerRun: 2, ct).ConfigureAwait(true);
        var again = await RunAsync(handler, Day1, maxPerRun: 2, ct).ConfigureAwait(true);

        Assert.Equal(first, again);
    }

    // ---------------------------------------------------------- the log ---

    /// <summary>
    /// A frozen rotation has to be visible in the log rather than in a query someone
    /// thought to write. Run one is all new; run two is all refreshed and says so.
    /// </summary>
    [Fact]
    public async Task TheCoverageLineSeparatesNewFromRefreshed()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var handler = new FundamentalsHandler();

        var firstDetail = await DetailAsync(handler, Day1, maxPerRun: 6, ct).ConfigureAwait(true);
        Assert.Contains("6 new and 0 refreshed", firstDetail, StringComparison.Ordinal);
        Assert.Contains("6 have never been attempted", firstDetail, StringComparison.Ordinal);

        var secondDetail = await DetailAsync(handler, Day2, maxPerRun: 6, ct).ConfigureAwait(true);
        Assert.Contains("0 new and 6 refreshed", secondDetail, StringComparison.Ordinal);
        Assert.Contains("0 have never been attempted", secondDetail, StringComparison.Ordinal);
        Assert.Contains("oldest attempt in it dated 2026-08-03", secondDetail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- declaration ---

    [Fact]
    public void TheDeclaredWriteSetNamesBothTablesAndTheirColumns()
    {
        var stage = new FundamentalsIngestor(ClientFor(new FundamentalsHandler()));

        var snapshot = stage.WriteSet.Single(w => w.Table == "fundamental_snapshot");
        Assert.Equal(WriteOperation.Insert, snapshot.Operation);
        Assert.Equal(FundamentalsIngestor.Columns, snapshot.Columns);

        var attempt = stage.WriteSet.Single(w => w.Table == "fundamental_fetch_attempt");
        Assert.Equal(WriteOperation.Insert, attempt.Operation);
        Assert.Equal(FundamentalsIngestor.AttemptColumns, attempt.Columns);

        // Read back through the write declaration rather than declared twice, which
        // is what DeclaredAccess.CanRead permits and what fundamental_snapshot has
        // always relied on.
        Assert.DoesNotContain("fundamental_fetch_attempt", stage.ReadSet);
        Assert.DoesNotContain("fundamental_snapshot", stage.ReadSet);
    }

    // ----------------------------------------------------------- harness ---

    private static async Task<IReadOnlyList<string>> RunAsync(
        FundamentalsHandler handler, DateOnly date, int maxPerRun, CancellationToken ct)
    {
        handler.Asked.Clear();
        var stage = new FundamentalsIngestor(ClientFor(handler));

        await stage.ExecuteAsync(
            new StageContext(
                date, 1,
                new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
                new FrozenClock(date),
                new StubConfig(maxPerRun)),
            ct).ConfigureAwait(false);

        return handler.Asked.ToList();
    }

    private static async Task<string> DetailAsync(
        FundamentalsHandler handler, DateOnly date, int maxPerRun, CancellationToken ct)
    {
        handler.Asked.Clear();
        var stage = new FundamentalsIngestor(ClientFor(handler));

        var result = await stage.ExecuteAsync(
            new StageContext(
                date, 1,
                new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
                new FrozenClock(date),
                new StubConfig(maxPerRun)),
            ct).ConfigureAwait(false);

        return result.Detail ?? "";
    }

    private static EodhdClient ClientFor(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
            "test-token", new FrozenClock(Day1));

    /// <summary>
    /// The pool's price rows, and both stores this stage keys on, cleared. Only the
    /// marked tickers, so a developer database keeps everything else.
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        foreach (var sql in new[]
                 {
                     "DELETE FROM fundamental_fetch_attempt WHERE ticker LIKE @p",
                     "DELETE FROM fundamental_snapshot WHERE ticker LIKE @p",
                     "DELETE FROM price_daily WHERE ticker LIKE @p",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", Prefix + "%");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 250 sessions each, above the price, liquidity and history floors, so every
        // pool member clears the bootstrap and only the rotation decides the order.
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

    private static async Task<(DateOnly? Attempted, DateOnly? Yielded, long Rows)> AttemptAsync(
        string ticker, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT last_attempted_date, last_yield_date, rows_last_attempt " +
            "FROM fundamental_fetch_attempt WHERE ticker = @t;", conn);
        cmd.Parameters.AddWithValue("t", ticker);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        Assert.True(await r.ReadAsync(ct).ConfigureAwait(false), $"No attempt row for {ticker}.");

        return (r.GetFieldValue<DateOnly>(0),
                r.IsDBNull(1) ? null : r.GetFieldValue<DateOnly>(1),
                r.GetInt64(2));
    }

    // ----------------------------------------------------------- doubles ---

    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

    private sealed class StubConfig(int maxPerRun) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => Task.FromResult<ConfigRow?>(new ConfigRow(key, 1, Value(key), new DateOnly(2020, 1, 1)));

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        private string Value(string key) => key switch
        {
            "fundamentals.max_tickers_per_run" => maxPerRun.ToString(CultureInfo.InvariantCulture),
            "fundamentals.substitution_rate_alert" => "1",
            "fundamentals.widest_gap_alert_days" => "100000",
            "universe.min_price" => "5",
            "universe.min_adv_20d" => "2000000",
            "universe.min_history_days" => "250",
            _ => throw new InvalidOperationException($"The test config has no value for '{key}'."),
        };
    }

    /// <summary>
    /// The symbol list and the fundamentals endpoint. Records every ticker asked
    /// for, in order, which is the observable these tests assert on.
    /// </summary>
    private sealed class FundamentalsHandler(string[]? bareString = null) : HttpMessageHandler
    {
        private readonly HashSet<string> _bare =
            new(bareString ?? [], StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("exchange-symbol-list/US", StringComparison.Ordinal))
            {
                // The feed sends the bare code; the map appends the exchange.
                var rows = string.Join(",", Pool.Select(t => t[..^3]).Select(c =>
                    $$"""{"Code":"{{c}}","Name":"{{c}} Inc","Type":"Common Stock"}"""));

                return Json("[" + rows + "]");
            }

            var ticker = path[(path.LastIndexOf('/') + 1)..];
            Asked.Add(ticker);

            // A ticker the endpoint carries no financials for answers with a bare
            // string rather than an empty object [Statements.Parse].
            // `filter=Financials` returns the Financials object itself, so the block
            // names sit at the root [Statements.Parse].
            return Json(_bare.Contains(ticker)
                ? "\"NA\""
                : """
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
