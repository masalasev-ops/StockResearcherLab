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
/// Checkpoint 1.2. C02 loads a trailing window of calendar dates rather than
/// tonight alone, because a session accretes for hours and a date loaded once on
/// the night it was newest keeps its partial count for ever [A10, A26].
///
/// No test here makes a live call. The bulk feed is stubbed, so the assertions are
/// about what the stage does with a payload rather than about the provider.
/// </summary>
[Collection("database")]
public sealed class PriceIngestorTests
{
    private const string Prefix = "SRLT";

    /// <summary>
    /// Every ticker any test here can create: the prefix, a per-test letter, and a
    /// small index. Enumerated so cleanup can use equality, which uses the primary
    /// key, rather than a prefix match, which does not.
    /// </summary>
    private static readonly string[] AllTestTickers =
        (from letter in "ABCDEFGH"
         from n in Enumerable.Range(0, 5)
         select Prefix + letter + n + ".US").ToArray();

    private static StageContext ContextFor(PriceIngestor stage, DateOnly date, int window)
        => new(
            date,
            configVersion: 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(date),
            new StubConfig(window));

    private static PriceIngestor StageFor(HttpMessageHandler handler)
        => new(new EodhdClient(new HttpClient(handler), "fake-token", new FrozenClock(new DateOnly(2026, 8, 7))));

    private static string Row(string code, DateOnly date, decimal close, long volume)
        => $$"""
            {"code":"{{code}}","exchange_short_name":"US","date":"{{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}}",
             "open":{{close}},"high":{{close}},"low":{{close}},"close":{{close}},
             "adjusted_close":{{close}},"volume":{{volume}}}
            """.Replace("\n", "").Replace("\r", "");

    private static async Task<(long Rows, decimal? Close)> ReadAsync(string ticker, DateOnly date, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*), max(close) FROM price_daily WHERE ticker = @t AND date = @d;", conn);
        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", date);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        return (r.GetInt64(0), await r.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : r.GetDecimal(1));
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        // Exact tickers rather than a prefix. price_daily holds millions of rows
        // once real bars land, and neither a LIKE prefix nor a range with a high
        // sentinel uses the (ticker, date) index under a linguistic collation: the
        // first went to a sequential scan and timed out, the second silently matched
        // nothing because punctuation does not sort after digits. Equality does use
        // the index, and these tests know exactly which tickers they create.
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM price_daily WHERE ticker = ANY(@t);", conn);
        cmd.Parameters.AddWithValue("t", AllTestTickers);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ the window ---

    [Fact]
    public async Task TheWindowIsCalendarDatesCountingBackFromTheRunDateInclusive()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        var handler = new BulkHandler(Prefix + "A");
        var stage = StageFor(handler);
        var runDate = new DateOnly(2026, 8, 7);

        await stage.ExecuteAsync(ContextFor(stage, runDate, window: 5), ct).ConfigureAwait(true);

        // Five calls, newest first, inclusive of the run date. Calendar dates and
        // not trading dates: there is no calendar until C07 exists.
        Assert.Equal(
            [
                new DateOnly(2026, 8, 7), new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 5),
                new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 3),
            ],
            handler.Requested);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The run date's own session may be in progress, and it is loaded anyway. C02
    /// does not decide what is usable; C07 does, and it needs the short date present
    /// to measure it against the trailing median [D-70].
    /// </summary>
    [Fact]
    public async Task TheRunDateIsLoadedEvenThoughItsSessionMayBeIncomplete()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        var handler = new BulkHandler(Prefix + "B");
        var stage = StageFor(handler);
        var runDate = new DateOnly(2026, 8, 7);

        await stage.ExecuteAsync(ContextFor(stage, runDate, window: 2), ct).ConfigureAwait(true);

        var landed = await ReadAsync(Prefix + "B0.US", runDate, ct).ConfigureAwait(true);
        Assert.Equal(1, landed.Rows);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// A weekend or a holiday returns an empty array rather than an error, so it is
    /// the ordinary case and contributes nothing [A26].
    /// </summary>
    [Fact]
    public async Task ANonSessionDateReturnsAnEmptyArrayAndIsTolerated()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        // 2026-08-08 and 08-09 are a weekend; the handler returns [] for them.
        var handler = new BulkHandler(Prefix + "C", emptyOn: [new DateOnly(2026, 8, 8), new DateOnly(2026, 8, 9)]);
        var stage = StageFor(handler);

        var result = await stage.ExecuteAsync(
            ContextFor(stage, new DateOnly(2026, 8, 9), window: 3), ct).ConfigureAwait(true);

        // Three dates requested, two empty, one row written.
        Assert.Equal(3, handler.Requested.Count);
        Assert.Equal(1, result.RowsWritten);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    // ------------------------------------------------------- D-68 idempotence ---

    [Fact]
    public async Task RunningTheSameDateTwiceLeavesTheRowCountUnchangedAndTheValuesEqual()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        var handler = new BulkHandler(Prefix + "D", close: 12.50m);
        var stage = StageFor(handler);
        var runDate = new DateOnly(2026, 8, 7);

        await stage.ExecuteAsync(ContextFor(stage, runDate, window: 1), ct).ConfigureAwait(true);
        var first = await ReadAsync(Prefix + "D0.US", runDate, ct).ConfigureAwait(true);

        await stage.ExecuteAsync(ContextFor(stage, runDate, window: 1), ct).ConfigureAwait(true);
        var second = await ReadAsync(Prefix + "D0.US", runDate, ct).ConfigureAwait(true);

        Assert.Equal(1, first.Rows);
        Assert.Equal(1, second.Rows);
        Assert.Equal(first.Close, second.Close);
        Assert.Equal(12.50m, second.Close);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The case the reload window exists for. A date loaded short is topped up on a
    /// later run, so the second load must overwrite rather than be ignored [A10].
    /// </summary>
    [Fact]
    public async Task AShortDateIsToppedUpByALaterRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct).ConfigureAwait(true);

        var runDate = new DateOnly(2026, 8, 7);

        // First run: one ticker, as a session still filling would give.
        var partial = StageFor(new BulkHandler(Prefix + "E", close: 1.00m, tickerCount: 1));
        await partial.ExecuteAsync(ContextFor(partial, runDate, window: 1), ct).ConfigureAwait(true);

        // Later run: three tickers and a moved price, as the finished session gives.
        var settled = StageFor(new BulkHandler(Prefix + "E", close: 2.00m, tickerCount: 3));
        await settled.ExecuteAsync(ContextFor(settled, runDate, window: 1), ct).ConfigureAwait(true);

        await using var conn = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM price_daily WHERE ticker = ANY(@t) AND date = @d;", conn);
        cmd.Parameters.AddWithValue("t", Enumerable.Range(0, 5).Select(n => Prefix + "E" + n + ".US").ToArray());
        cmd.Parameters.AddWithValue("d", runDate);

        Assert.Equal(3L, (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!);

        var first = await ReadAsync(Prefix + "E0.US", runDate, ct).ConfigureAwait(true);
        Assert.Equal(2.00m, first.Close);

        await ClearAsync(ct).ConfigureAwait(true);
    }

    // ------------------------------------------------------------- integrity ---

    /// <summary>
    /// A feed returning a different day for a requested one would file bars under a
    /// day they did not belong to, and no later stage could detect it.
    /// </summary>
    [Fact]
    public async Task ARowDatedOtherThanTheDateRequestedFailsTheStage()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new BulkHandler(Prefix + "F", dateOverride: new DateOnly(1999, 1, 1));
        var stage = StageFor(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => stage.ExecuteAsync(ContextFor(stage, new DateOnly(2026, 8, 7), window: 1), ct))
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task AWindowThatIsNotAPositiveNumberFailsTheStage()
    {
        var ct = TestContext.Current.CancellationToken;
        var stage = StageFor(new BulkHandler(Prefix + "G"));

        var context = new StageContext(
            new DateOnly(2026, 8, 7), 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FrozenClock(new DateOnly(2026, 8, 7)),
            new StubConfig(0));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => stage.ExecuteAsync(context, ct)).ConfigureAwait(true);
    }

    /// <summary>
    /// **Two writes since 0010, and the read set is still empty.** `price_fetch_attempt`
    /// is written by the range mode and read back by it, and a stage may read what it
    /// writes without declaring it twice [`DeclaredAccess.CanRead`]. Asserted as the
    /// whole set rather than as "contains price_daily", so a third write has to be
    /// declared here before it is declared anywhere else.
    /// </summary>
    [Fact]
    public void TheDeclaredWriteSetNamesPriceDailyAndItsAttemptRecordWithTheirColumns()
    {
        var stage = StageFor(new BulkHandler(Prefix + "H"));

        Assert.Collection(
            stage.WriteSet,
            write =>
            {
                Assert.Equal("price_daily", write.Table);
                Assert.Equal(WriteOperation.Insert, write.Operation);
                Assert.Equal(PriceIngestor.Columns, write.Columns);
            },
            write =>
            {
                Assert.Equal("price_fetch_attempt", write.Table);
                Assert.Equal(WriteOperation.Insert, write.Operation);
                Assert.Equal(PriceIngestor.AttemptColumns, write.Columns);
            });

        Assert.Empty(stage.ReadSet);
    }

    // --------------------------------------------------------------- doubles ---

    private sealed class FrozenClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.Year, today.Month, today.Day, 21, 30, 0, TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

    private sealed class StubConfig(int window) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => Task.FromResult<ConfigRow?>(new ConfigRow(
                key, 1, window.ToString(CultureInfo.InvariantCulture), new DateOnly(2020, 1, 1)));

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        // A stage resolves keys, never the store-wide version: that is the runner's
        // to resolve and the stage's to be handed [checkpoint 1.13].
        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");
    }

    /// <summary>The bulk feed, stubbed. Records the dates asked for, in order.</summary>
    private sealed class BulkHandler(
        string codePrefix,
        decimal close = 10.00m,
        long volume = 1000,
        int tickerCount = 1,
        DateOnly[]? emptyOn = null,
        DateOnly? dateOverride = null) : HttpMessageHandler
    {
        public List<DateOnly> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            var marker = "date=";
            var i = query.IndexOf(marker, StringComparison.Ordinal);
            var rest = query[(i + marker.Length)..];
            var end = rest.IndexOf('&');
            var asked = DateOnly.ParseExact(end < 0 ? rest : rest[..end], "yyyy-MM-dd", CultureInfo.InvariantCulture);

            Requested.Add(asked);

            var body = emptyOn is not null && emptyOn.Contains(asked)
                ? "[]"
                : "[" + string.Join(",", Enumerable.Range(0, tickerCount)
                    .Select(n => Row(codePrefix + n.ToString(CultureInfo.InvariantCulture),
                        dateOverride ?? asked, close, volume))) + "]";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
