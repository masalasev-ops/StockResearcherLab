using System.Net;
using System.Text;
using System.Text.Json;
using StockResearcherLab.Core;
using StockResearcherLab.Data.Eodhd;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// Checkpoint 1.1's four request-form properties, asserted on the produced query
/// string rather than on a call succeeding, plus the paging loop.
///
/// **Why the string and not the response.** A test that asserts a 200 passes on any
/// form the endpoint happens to tolerate today, and this provider's answer to a
/// wrongly encoded parameter is a 422 that reads like an entitlement problem. That
/// is exactly how the paging form was misread at 1.9: `limit` and `offset` are
/// accepted and silently ignored, while `page[offset]` and `page[limit]` work. None
/// of these tests makes a live call.
/// </summary>
public sealed class EodhdClientTests
{
    private const string Token = "fake-token-not-a-secret";

    private static EodhdClient Client(HttpMessageHandler? handler = null)
        => new(new HttpClient(handler ?? new NeverCalledHandler()), Token, new FrozenClock());

    // ---------------------------------------------------------------- form ---

    [Fact]
    public void TheTokenGoesInTheQueryStringAndFmtIsAlwaysExplicit()
    {
        var path = Client().PathFor("eod-bulk-last-day/US", []);

        Assert.Equal("eod-bulk-last-day/US?fmt=json&api_token=fake-token-not-a-secret", path);
    }

    [Fact]
    public void FmtIsExplicitEvenWhenOtherParametersArePresent()
    {
        var path = Client().PathFor("eod-bulk-last-day/US", [("date", "2026-08-05")]);

        Assert.Equal(
            "eod-bulk-last-day/US?date=2026-08-05&fmt=json&api_token=fake-token-not-a-secret",
            path);
    }

    [Fact]
    public void TheFilterFormHasItsColonsPercentEncoded()
    {
        // Financials::Balance_Sheet::quarterly. Unencoded colons are tolerated in
        // some positions of a query string and not others, and the failure is a 422
        // rather than a bad request.
        var path = Client().PathFor(
            "fundamentals/CCS.US", [("filter", "Financials::Balance_Sheet::quarterly")]);

        Assert.Equal(
            "fundamentals/CCS.US?filter=Financials%3A%3ABalance_Sheet%3A%3Aquarterly" +
            "&fmt=json&api_token=fake-token-not-a-secret",
            path);
    }

    /// <summary>
    /// A19. Square brackets are not legal unencoded in a query string and .NET's
    /// Uri handling does not reliably escape them, so the produced string is pinned
    /// character for character. If the escaping ever changes this fails, which is
    /// the point: the endpoint would answer a mangled form with a 422 that reads
    /// like entitlement.
    /// </summary>
    [Fact]
    public void ThePagingFormHasItsBracketsPercentEncoded()
    {
        var path = Client().PathFor(
            "sec-filings/CCS.US/form4", EodhdUrl.Page(offset: 50, limit: 50));

        Assert.Equal(
            "sec-filings/CCS.US/form4?page%5Boffset%5D=50&page%5Blimit%5D=50" +
            "&fmt=json&api_token=fake-token-not-a-secret",
            path);
    }

    // -------------------------------------------------------------- paging ---

    /// <summary>
    /// A20. The total is an exact multiple of the page size, which is the case a
    /// loop terminating on a short page gets wrong: the last full page is
    /// indistinguishable from a middle one, so such a loop asks for one more.
    /// Termination keys on links.next instead.
    /// </summary>
    [Fact]
    public async Task PagingTerminatesOnTheAbsentNextLinkWhenTheTotalIsAnExactMultiple()
    {
        // 100 rows, 4 pages of 25. Every page is full, including the last.
        var handler = new PagedHandler(total: 100, pageSize: 25);
        var client = Client(handler);

        var rows = await client.GetAllPagesAsync(
            "sec-filings/CCS.US/form4", [], pageSize: 25,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(100, rows.Count);
        Assert.Equal(4, handler.Calls);

        // Distinct, so a loop that re-requested the same offset would fail here.
        var ids = rows.Select(r => r.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(100, ids.Distinct().Count());
    }

    [Fact]
    public async Task PagingWalksAPartialLastPage()
    {
        var handler = new PagedHandler(total: 90, pageSize: 25);
        var client = Client(handler);

        var rows = await client.GetAllPagesAsync(
            "sec-filings/CCS.US/form4", [], pageSize: 25,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(90, rows.Count);
        Assert.Equal(4, handler.Calls);
    }

    /// <summary>
    /// A20. A short read that returns successfully looks identical to a complete
    /// one, so it fails the stage rather than being logged.
    /// </summary>
    [Fact]
    public async Task APagedReadShortOfItsReportedTotalFailsRatherThanReturning()
    {
        // Says 100, stops offering a next link after two pages of 25.
        var handler = new PagedHandler(total: 100, pageSize: 25, stopAfterPages: 2);
        var client = Client(handler);

        var ex = await Assert.ThrowsAsync<PagedReadIncompleteException>(
            () => client.GetAllPagesAsync(
                "sec-filings/CCS.US/form4", [], pageSize: 25,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(50, ex.Collected);
        Assert.Equal(100, ex.ReportedTotal);
    }

    [Fact]
    public async Task AnUnpagedArrayResponseIsReturnedWhole()
    {
        var handler = new StubHandler("""[{"id":1},{"id":2}]""");
        var client = Client(handler);

        using var doc = await client.GetAsync(
            "eod-bulk-last-day/US", [], TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }

    // ---------------------------------------------------------- rate limit ---

    [Fact]
    public async Task TheRateLimiterAdmitsUpToItsCeilingWithoutWaiting()
    {
        var limiter = new EodhdRateLimiter(new FrozenClock(), requestsPerMinute: 3);

        for (var i = 0; i < 3; i++)
        {
            await limiter.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        Assert.Equal(3, limiter.InWindow);
    }

    [Fact]
    public async Task TheRateLimiterForgetsRequestsOlderThanItsWindow()
    {
        var clock = new FrozenClock();
        var limiter = new EodhdRateLimiter(clock, requestsPerMinute: 2);

        await limiter.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await limiter.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, limiter.InWindow);

        // A minute later the window is empty and the next call does not block.
        clock.Advance(TimeSpan.FromMinutes(1));
        await limiter.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(1, limiter.InWindow);
    }

    // ------------------------------------------------------------- doubles ---

    /// <summary>Injected rather than read, so the limiter's window can be moved without waiting [INVARIANT 11].</summary>
    private sealed class FrozenClock : IClock
    {
        private DateTimeOffset _now = new(2026, 8, 7, 21, 30, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow => _now;

        public DateOnly Today => DateOnly.FromDateTime(_now.UtcDateTime);

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException(
                "This test asserts the request form and must not make a call.");
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>
    /// The provider's {data, meta, links} envelope. Offers links.next until the set
    /// is exhausted, which is what the loop is required to terminate on.
    /// </summary>
    private sealed class PagedHandler(int total, int pageSize, int? stopAfterPages = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;

            var query = request.RequestUri!.Query;
            var offset = ReadOffset(query);
            var count = Math.Max(0, Math.Min(pageSize, total - offset));

            var rows = string.Join(",", Enumerable.Range(offset, count).Select(i => $$"""{"id":{{i}}}"""));

            var served = offset + count;
            var more = served < total && (stopAfterPages is null || Calls < stopAfterPages);
            var links = more
                ? $$"""{"next":"/api/x?page%5Boffset%5D={{served}}&page%5Blimit%5D={{pageSize}}"}"""
                : "{}";

            var body = $$"""{"data":[{{rows}}],"meta":{"total":{{total}}},"links":{{links}}}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private static int ReadOffset(string query)
        {
            // The handler decodes what the client encoded, which is itself a check
            // that the brackets survived the round trip.
            var decoded = Uri.UnescapeDataString(query);
            var marker = "page[offset]=";
            var i = decoded.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0)
            {
                throw new InvalidOperationException(
                    $"No page[offset] in '{decoded}'. The client is not sending the paging form " +
                    "the endpoint accepts, which is silently ignored rather than rejected [1.9].");
            }

            var rest = decoded[(i + marker.Length)..];
            var end = rest.IndexOf('&');
            return int.Parse(end < 0 ? rest : rest[..end], System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
