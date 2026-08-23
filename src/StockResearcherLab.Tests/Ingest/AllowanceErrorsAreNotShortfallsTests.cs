using System.Net;
using System.Text;
using StockResearcherLab.Core;
using StockResearcherLab.Data.Eodhd;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// An allowance error in flight fails the stage; a server that ran out of pages does
/// not [3.4, D-71].
///
/// **The two are one absorbed observation apart and that is why this is a test.** An
/// allowance wall is a pre-flight verdict from `/api/user` before a call is made. A
/// short page is a response shape that arrived, and D-71 records it and continues,
/// because a provider disagreeing with itself cannot be recovered by asking again. A
/// `402` or a `429` reaching the paging client is neither: it is an in-flight failure,
/// and absorbing it as a shortfall would report a complete sweep over a partial one.
/// </summary>
public sealed class AllowanceErrorsAreNotShortfallsTests
{
    private const string Token = "fake-token-not-a-secret";

    private static readonly IClock Frozen =
        new FixedClock(new DateTimeOffset(2026, 8, 12, 2, 52, 0, TimeSpan.Zero), new DateOnly(2026, 8, 11));

    private static EodhdClient Client(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri(EodhdUrl.BaseAddress) }, Token, Frozen);

    /// <summary>
    /// 402 is the allowance being refused mid-sweep. It fails the stage rather than
    /// coming back as rows the caller would record as everything there was.
    /// </summary>
    [Fact]
    public async Task A402ReachingThePagingClientFailsTheStage()
    {
        var client = Client(new StatusHandler(HttpStatusCode.PaymentRequired, "Daily limit exceeded"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAllPagesAsync("sec-filings/CCS.US/form4", [], pageSize: 50,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("402", ex.Message, StringComparison.Ordinal);
        Assert.IsNotType<PagedReadIncompleteException>(ex);
    }

    /// <summary>429 is the same case through a different door: rate rather than allowance, still in flight.</summary>
    [Fact]
    public async Task A429ReachingThePagingClientFailsTheStage()
    {
        var client = Client(new StatusHandler(HttpStatusCode.TooManyRequests, "Too many requests"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAllPagesAsync("sec-filings/CCS.US/form4", [], pageSize: 50,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("429", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contrast, in the same file so the distinction is read rather than inferred.
    /// The server offered no next link and still delivered fewer rows than it claimed,
    /// so everything available has been asked for and another call cannot recover the
    /// rest. That is carried back as a shortfall for the stage to record [D-71].
    /// </summary>
    [Fact]
    public async Task AServerThatRanOutOfPagesIsAShortfallRatherThanAFailure()
    {
        var client = Client(new ShortLastPageHandler(rows: 30, claimedTotal: 100));

        var read = await client.GetAllPagesAsync(
            "sec-filings/CCS.US/form4", [], pageSize: 50, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(30, read.Rows.Count);
        Assert.Equal(100, read.ReportedTotal);
        Assert.Equal(70, read.Shortfall);

        // At the oldest end, so it sits outside every trailing window and leaves
        // insider_net_90d_usd and distinct_buyer_count untouched.
        Assert.Equal(ShortfallPosition.Final, read.Position);
    }

    /// <summary>Returns one status for every request, with the provider's own words in the body.</summary>
    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StatusHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/plain"),
            });
    }

    /// <summary>One page, short of the total it claims, offering no next link.</summary>
    private sealed class ShortLastPageHandler : HttpMessageHandler
    {
        private readonly int _rows;
        private readonly int _claimedTotal;

        public ShortLastPageHandler(int rows, int claimedTotal)
        {
            _rows = rows;
            _claimedTotal = claimedTotal;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var data = string.Join(",", Enumerable.Range(0, _rows).Select(i => $"{{\"id\":{i}}}"));
            var body = $"{{\"data\":[{data}],\"meta\":{{\"total\":{_claimedTotal}}},\"links\":{{}}}}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
