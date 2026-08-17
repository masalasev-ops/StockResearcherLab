using System.Globalization;
using StockResearcherLab.Core;
using StockResearcherLab.Data.Eodhd;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// C05's range mode, which is 3.9's sweep.
///
/// **The line this file exists for is 3.9's done-when: a gated halt mid-walk and a
/// D-71 short page appear as two different things in the run log, asserted rather than
/// assumed.** They are one absorbed observation apart. A gated stop is this system
/// declining to spend; a shortfall is the provider withholding rows it claimed to have.
/// Collapsing the first into the second would put a spending decision into the record
/// as a provider defect and leave a short history unremarked, and both readings look
/// entirely normal from a row count.
///
/// **The distinction is asserted at the two layers where it can be lost**, which is
/// where the walk ends and where the line is composed, rather than through a sweep.
/// C05's pool is the universe as of its date, read from `security_daily` [3.12], and when
/// this was written the suite ran against the developer database, so a range execution
/// here would walk the live universe and stamp `flow_fetch_attempt` for every real ticker
/// at this fixture's range end. That was item 26's harm exactly. **The database half is
/// closed** [item 26, 3.13] and the resumption half is still recorded as owed rather than
/// proved.
/// </summary>
public sealed class FlowRangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 2, 52, 0, TimeSpan.Zero);

    // ------------------------------- where the walk ends [3.9, D-71] ---

    /// <summary>
    /// A gate that refuses stops the walk and is **not** a shortfall, even though rows
    /// the endpoint claimed did not arrive.
    ///
    /// The rows that did not arrive were never asked for, so counting them as withheld
    /// would be this system recording its own decision as the provider's fault.
    /// </summary>
    [Fact]
    public async Task AGatedWalkStopsWithoutRecordingAShortfall()
    {
        var ct = TestContext.Current.CancellationToken;

        var handler = new PagingHandler(pages: 3, rowsPerPage: 2, claimedTotal: 6);
        var client = Client(handler);

        var allowed = 1;

        var read = await client.GetAllPagesAsync(
            "sec-filings/X.US/form4", [], pageSize: 2, ct,
            beforePage: _ => Task.FromResult(allowed-- > 0)).ConfigureAwait(true);

        Assert.True(read.StoppedByGate);

        // Two of six rows arrived and four did not, and the shortfall is zero anyway.
        Assert.Equal(2, read.Rows.Count);
        Assert.Equal(6, read.ReportedTotal);
        Assert.Equal(0, read.Shortfall);
        Assert.Equal(ShortfallPosition.None, read.Position);

        Assert.Equal(1, handler.PagesServed);
    }

    /// <summary>
    /// The same endpoint, ungated, running the server out of pages while rows are still
    /// unaccounted for. This is D-71's shortfall and it does carry a position.
    /// </summary>
    [Fact]
    public async Task AnUngatedWalkThatRunsOutOfPagesShortIsAShortfallWithAPosition()
    {
        var ct = TestContext.Current.CancellationToken;

        var handler = new PagingHandler(pages: 1, rowsPerPage: 2, claimedTotal: 5);

        var read = await Client(handler).GetAllPagesAsync(
            "sec-filings/X.US/form4", [], pageSize: 2, ct).ConfigureAwait(true);

        Assert.False(read.StoppedByGate);
        Assert.Equal(3, read.Shortfall);
        Assert.Equal(ShortfallPosition.Final, read.Position);
    }

    /// <summary>
    /// **The gate is asked before the first page**, so a sweep with nothing left spends
    /// nothing discovering that.
    /// </summary>
    [Fact]
    public async Task AGateThatRefusesImmediatelyCostsNoPage()
    {
        var ct = TestContext.Current.CancellationToken;

        var handler = new PagingHandler(pages: 3, rowsPerPage: 2, claimedTotal: 6);

        var read = await Client(handler).GetAllPagesAsync(
            "sec-filings/X.US/form4", [], pageSize: 2, ct,
            beforePage: _ => Task.FromResult(false)).ConfigureAwait(true);

        Assert.True(read.StoppedByGate);
        Assert.Empty(read.Rows);
        Assert.Equal(0, handler.PagesServed);
    }

    // ------------------------------------ how the line is composed [3.9] ---

    /// <summary>
    /// The two endings render as two sentences, and neither borrows the other's
    /// vocabulary.
    /// </summary>
    [Fact]
    public void TheGatedHaltAndTheShortfallReadAsTwoDifferentThings()
    {
        var gated = FlowIngestor.DescribeGatedHalt("SRLC.US");

        var shortfall = FlowIngestor.DescribeShortfalls(
            [new FlowIngestor.Shortfall("SRLB.US", 3, ShortfallPosition.Final)]);

        // The halt names its ticker, says what the next run will do, and denies the
        // other reading in the words a reader would search for.
        Assert.Contains("HALTED on the allowance gate at SRLC.US", gated, StringComparison.Ordinal);
        Assert.Contains("walked again from its first page", gated, StringComparison.Ordinal);
        Assert.Contains("is not a D-71 shortfall", gated, StringComparison.Ordinal);

        // The shortfall counts one ticker, which is the one that under-delivered. The
        // gated ticker is not in this list and must not be countable from it.
        Assert.Contains("1 ticker(s) under-delivered", shortfall, StringComparison.Ordinal);
        Assert.Contains("SRLB.US 3 final", shortfall, StringComparison.Ordinal);
        Assert.DoesNotContain("SRLC.US", shortfall, StringComparison.Ordinal);

        // And the halt is not counted as an under-delivery anywhere.
        Assert.DoesNotContain("under-delivered", gated, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run with no shortfall says so rather than saying nothing, which is what makes
    /// the absence readable [D-71].
    /// </summary>
    [Fact]
    public void NoShortfallIsStatedRatherThanOmitted()
        => Assert.Equal(
            "No ticker under-delivered against meta.total [D-71]",
            FlowIngestor.DescribeShortfalls([]));

    // ----------------------------------------------------------- harness ---

    private static EodhdClient Client(PagingHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri(EodhdUrl.BaseAddress) },
            "test-token", new FixedClock(Now));

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;

        public DateOnly Today => DateOnly.FromDateTime(utcNow.UtcDateTime);
    }

    /// <summary>
    /// A paged endpoint offering <paramref name="pages"/> pages and claiming
    /// <paramref name="claimedTotal"/> rows, whether or not it delivers them.
    /// </summary>
    private sealed class PagingHandler(int pages, int rowsPerPage, int claimedTotal) : HttpMessageHandler
    {
        private int _served;

        public int PagesServed => _served;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _served);

            var offset = Offset(request.RequestUri!.Query);
            var page = offset / rowsPerPage;
            var last = page >= pages - 1;

            var rows = string.Join(",", Enumerable.Range(0, rowsPerPage)
                .Select(i => Filing(offset + i)));

            var links = last
                ? "{}"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    """{{"next":"https://example.invalid/n?page[offset]={0}"}}""",
                    offset + rowsPerPage);

            var body = string.Format(
                CultureInfo.InvariantCulture,
                """{{"data":[{0}],"meta":{{"total":{1}}},"links":{2}}}""",
                rows, claimedTotal, links);

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }

        /// <summary>
        /// One Form 4 filing, unique on its accession. Built with <c>string.Format</c>
        /// because the nested objects close with more braces than an interpolated raw
        /// string can carry as content.
        /// </summary>
        private static string Filing(int n) => string.Format(
            CultureInfo.InvariantCulture,
            """
            {{"accessionNo":"ACC-{0:D4}","filedAt":"2024-03-04","transactionDate":"2024-03-01",
              "reportingOwner":{{"cik":"000{0}","name":"Owner {0}"}},
              "nonDerivativeTable":{{"transactions":[
                {{"transactionCode":"P","securityTitle":"Common",
                  "shares":{{"value":100}},"pricePerShare":{{"value":10}},
                  "sharesOwnedFollowingTransaction":{{"value":1000}},
                  "acquiredDisposed":{{"value":"A"}}}}]}}}}
            """,
            n);

        private static int Offset(string query)
        {
            const string Key = "page[offset]=";
            var decoded = Uri.UnescapeDataString(query);
            var at = decoded.IndexOf(Key, StringComparison.Ordinal);
            if (at < 0)
            {
                return 0;
            }

            var value = decoded[(at + Key.Length)..];
            var end = value.IndexOf('&', StringComparison.Ordinal);
            return int.Parse(end < 0 ? value : value[..end], CultureInfo.InvariantCulture);
        }
    }
}
