using System.Net;
using System.Net.Sockets;
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

        var read = await client.GetAllPagesAsync(
            "sec-filings/CCS.US/form4", [], pageSize: 25,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(100, read.Rows.Count);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(0, read.Shortfall);

        // Distinct, so a loop that re-requested the same offset would fail here.
        var ids = read.Rows.Select(r => r.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(100, ids.Distinct().Count());
    }

    /// <summary>
    /// A22. The loop terminates on links.next but builds the next request from
    /// offset arithmetic, so two things have to agree and only one was tested.
    /// This asserts the URL the client builds for page n+1 against the links.next
    /// the endpoint offered on page n, character for character.
    ///
    /// Offset drift would otherwise surface as a silently skipped or repeated page,
    /// which returns successfully and produces a short table.
    /// </summary>
    [Fact]
    public async Task EachRequestMatchesTheNextLinkTheEndpointOffered()
    {
        var handler = new PagedHandler(total: 100, pageSize: 25);

        await Client(handler).GetAllPagesAsync(
            "sec-filings/CCS.US/form4", [], pageSize: 25,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        // Three successors offered across four pages, and the second through
        // fourth requests are what they should be.
        Assert.Equal(3, handler.ExpectedNext.Count);
        Assert.Equal(handler.ExpectedNext, handler.Requested.Skip(1).ToList());
    }

    [Fact]
    public async Task PagingWalksAPartialLastPage()
    {
        var handler = new PagedHandler(total: 90, pageSize: 25);
        var client = Client(handler);

        var read = await client.GetAllPagesAsync(
            "sec-filings/CCS.US/form4", [], pageSize: 25,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(90, read.Rows.Count);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(0, read.Shortfall);
    }

    /// <summary>
    /// D-71, the fatal half. The loop ends while <c>links.next</c> is still being
    /// offered, which is this client failing to ask rather than the server running
    /// out. What was missed is unknown and another call would fix it, so it throws
    /// at exactly the strictness A20 set.
    ///
    /// The client only stops with a next link in hand when a page comes back empty,
    /// so that is what the handler does: it offers a successor and then serves
    /// nothing.
    /// </summary>
    [Fact]
    public async Task APagedReadThatStopsWhileTheEndpointStillOffersMoreFails()
    {
        // Says 100, offers a next link throughout, serves nothing from page three.
        var handler = new PagedHandler(total: 100, pageSize: 25, emptyFromPage: 3);
        var client = Client(handler);

        var ex = await Assert.ThrowsAsync<PagedReadIncompleteException>(
            () => client.GetAllPagesAsync(
                "sec-filings/CCS.US/form4", [], pageSize: 25,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(50, ex.Collected);
        Assert.Equal(100, ex.ReportedTotal);
    }

    /// <summary>
    /// D-71, the recorded half, and the case that was measured: 43 tickers in 250,
    /// every one of them having walked the server's own pagination to its end.
    ///
    /// The endpoint stops offering a next link with rows still unaccounted for.
    /// Everything available has been asked for and asking again cannot produce the
    /// rest, so the shortfall comes back for the stage to record rather than
    /// halting the night.
    ///
    /// **This is the assertion that changed direction at D-71** and it is the same
    /// fixture as before: what used to be proof of a throw is now proof of a
    /// recorded shortfall, which is the whole content of the decision.
    /// </summary>
    [Fact]
    public async Task APagedReadTheServerRanOutOfIsRecordedRatherThanThrown()
    {
        // Says 100, stops offering a next link after two pages of 25.
        var handler = new PagedHandler(total: 100, pageSize: 25, stopAfterPages: 2);

        var read = await Client(handler).GetAllPagesAsync(
            "sec-filings/CCS.US/form4", [], pageSize: 25,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(50, read.Rows.Count);
        Assert.Equal(100, read.ReportedTotal);
        Assert.Equal(50, read.Shortfall);

        // Every page served was full, so what is missing sits past the last one.
        Assert.Equal(ShortfallPosition.Final, read.Position);
    }

    /// <summary>
    /// D-71's position rule, which is what decides whether a shortfall can reach a
    /// trailing-90-day window. A page before the last coming back short puts the
    /// missing rows inside the history; only a short final page puts them at the
    /// oldest end.
    ///
    /// This is AAON.US reduced to a fixture. Thirteen pages, offsets 0 to 600, the
    /// last returning exactly the 43 rows that 643 minus 600 predicts, and page
    /// eight returning 48 where every other full page returned 50.
    /// </summary>
    [Fact]
    public async Task AShortPageBeforeTheLastPutsTheMissingRowsInsideTheHistory()
    {
        var handler = new PagedHandler(total: 100, pageSize: 25, shortInteriorPage: 2);

        var read = await Client(handler).GetAllPagesAsync(
            "sec-filings/CCS.US/form4", [], pageSize: 25,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(2, read.Shortfall);
        Assert.Equal(ShortfallPosition.Interior, read.Position);
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

    // ------------------------------------ the transport retry [D-100] ---
    //
    // Run 1515 lost a 128-minute sweep and about 13,500 tickers' work to one reset
    // socket in roughly 50,000 requests, on the free gate read. This client asked once
    // and threw on anything. What is retried is decided on the socket error code and on
    // four statuses, and everything else still fails on the first attempt.

    /// <summary>
    /// A reset connection is the fault that ended run 1515. The second attempt is
    /// allowed to be the one that works.
    /// </summary>
    [Fact]
    public async Task AResetConnectionIsRetriedAndTheAttemptAfterItSucceeds()
    {
        var handler = new TransportFaultHandler(SocketError.ConnectionReset, failFor: 1);
        var client = Client(handler);

        using var doc = await client.GetAsync("eod/AAA.US", [], TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, client.TransportRetries);
    }

    /// <summary>
    /// **`HostNotFound` is a configuration error wearing a transport error's type.** It
    /// answers the same way three times, so the attempts and the backoff buy nothing.
    /// This is the case that makes the rule a code test rather than a type test.
    /// </summary>
    [Fact]
    public async Task AHostThatDoesNotResolveIsNotRetried()
    {
        var handler = new TransportFaultHandler(SocketError.HostNotFound, failFor: int.MaxValue);
        var client = Client(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("eod/AAA.US", [], TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, client.TransportRetries);
    }

    /// <summary>
    /// 429 is the provider asking for a moment, which the rate limiter is supposed to
    /// prevent and does not guarantee. It is one of four statuses worth asking again on.
    /// </summary>
    [Fact]
    public async Task AThrottledResponseIsRetried()
    {
        var handler = new StatusHandler(HttpStatusCode.TooManyRequests, failFor: 1);
        var client = Client(handler);

        using var doc = await client.GetAsync("eod/AAA.US", [], TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, client.TransportRetries);
    }

    /// <summary>
    /// **402 stays fatal on the first attempt.** An exhausted allowance persists for the
    /// provider's day, so a retry spends the wall clock against a wall that will not
    /// move, and the stage has to fail rather than complete over a partial load [3.4].
    /// </summary>
    [Fact]
    public async Task AnExhaustedAllowanceIsNotRetried()
    {
        var handler = new StatusHandler(HttpStatusCode.PaymentRequired, failFor: int.MaxValue);
        var client = Client(handler);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("eod/AAA.US", [], TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.PaymentRequired, thrown.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, client.TransportRetries);
    }

    /// <summary>
    /// **404 stays a fact about the ticker.** The callers that tolerate it catch it and
    /// record zero rows, which `PriceBackfillTests` asserts end to end; asking again
    /// would put the same true question twice.
    /// </summary>
    [Fact]
    public async Task ATickerTheProviderDoesNotCarryIsNotRetried()
    {
        var handler = new StatusHandler(HttpStatusCode.NotFound, failFor: int.MaxValue);
        var client = Client(handler);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("eod/AAA.US", [], TestContext.Current.CancellationToken))
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, thrown.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, client.TransportRetries);
    }

    /// <summary>
    /// A transport fault of the given socket code for the first <paramref name="failFor"/>
    /// calls, then an empty array. Shaped the way <see cref="HttpClient"/> surfaces one,
    /// which is an <see cref="HttpRequestException"/> with the socket error inside it and
    /// no status code.
    /// </summary>
    private sealed class TransportFaultHandler(SocketError code, int failFor) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;

            if (Calls <= failFor)
            {
                throw new HttpRequestException(
                    $"stub transport fault {code}", new SocketException((int) code));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>The given status for the first <paramref name="failFor"/> calls, then an empty array.</summary>
    private sealed class StatusHandler(HttpStatusCode status, int failFor) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;

            return Task.FromResult(Calls <= failFor
                ? new HttpResponseMessage(status)
                {
                    Content = new StringContent("{\"message\":\"stub\"}", Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
        }
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
    /// <param name="stopAfterPages">Stop offering links.next after this many pages, with rows still owing.</param>
    /// <param name="emptyFromPage">Serve no rows from this page on, while still offering links.next.</param>
    /// <param name="shortInteriorPage">Serve two rows fewer on this page, which is not the last.</param>
    private sealed class PagedHandler(
        int total, int pageSize, int? stopAfterPages = null,
        int? emptyFromPage = null, int? shortInteriorPage = null) : HttpMessageHandler
    {
        private const string Path = "sec-filings/CCS.US/form4";

        public int Calls { get; private set; }

        /// <summary>What links.next offered, in order. One entry per page that had a successor.</summary>
        public List<string> ExpectedNext { get; } = [];

        /// <summary>What the client actually asked for, path and page parameters only.</summary>
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;

            var query = request.RequestUri!.Query;
            Requested.Add(WithoutTokenAndFmt(request.RequestUri));
            var offset = ReadOffset(query);
            var count = Math.Max(0, Math.Min(pageSize, total - offset));

            if (emptyFromPage is int empty && Calls >= empty)
            {
                count = 0;
            }
            else if (shortInteriorPage is int shortPage && Calls == shortPage)
            {
                count = Math.Max(0, count - 2);
            }

            var rows = string.Join(",", Enumerable.Range(offset, count).Select(i => $$"""{"id":{{i}}}"""));

            // The offset the client should ask for next, which follows the page
            // size rather than what this page happened to serve. A short page does
            // not shift the window, exactly as the real endpoint behaves: AAON's
            // page eight returned 48 and page nine still began at offset 450.
            var served = offset + Math.Min(pageSize, Math.Max(0, total - offset));

            var more = served < total
                       && (stopAfterPages is null || Calls < stopAfterPages)
                       && (emptyFromPage is null || Calls < emptyFromPage + 1);

            // links.next is emitted in exactly the form the client should build for
            // the following page, so the test can compare the two [A22]. `served`
            // is computed here from this handler's own accounting while the client
            // computes offset += pageSize, so the comparison is between two
            // independent arithmetic paths rather than a value echoed back.
            //
            // The real endpoint's links.next carries no api_token, checked at 1.9,
            // so the token is absent here too and the comparison is made on the
            // path and page parameters.
            var links = more
                ? $$"""{"next":"{{Path}}?page%5Boffset%5D={{served}}&page%5Blimit%5D={{pageSize}}"}"""
                : "{}";

            if (more)
            {
                ExpectedNext.Add($"{Path}?page%5Boffset%5D={served}&page%5Blimit%5D={pageSize}");
            }

            var body = $$"""{"data":[{{rows}}],"meta":{"total":{{total}}},"links":{{links}}}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        /// <summary>
        /// The request reduced to path plus page parameters, which is the shape
        /// links.next comes in. Escaping is preserved rather than normalised,
        /// because the escaping is half of what is being compared.
        /// </summary>
        private static string WithoutTokenAndFmt(Uri uri)
        {
            var kept = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("api_token=", StringComparison.Ordinal)
                            && !p.StartsWith("fmt=", StringComparison.Ordinal));

            return Path + "?" + string.Join("&", kept);
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
