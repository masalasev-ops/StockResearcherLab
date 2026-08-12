using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core;

namespace StockResearcherLab.Data.Eodhd;

/// <summary>
/// Thrown when a paged read stopped **while the endpoint was still offering a next
/// link** and collected fewer rows than the endpoint said it had. A partial ingest
/// that completes is worse than one that fails, because the next stage cannot tell
/// a short table from a real one [CLAUDE.md section 6].
///
/// **This is not thrown when the server itself ran out** [D-71]. A loop that ends
/// because `links.next` is absent has asked for everything there is; if the rows
/// are still short of `meta.total`, the provider is disagreeing with itself and
/// asking again cannot recover them. That case is carried back in
/// <see cref="PagedRead.Shortfall"/> and recorded by the stage.
///
/// The two are told apart by which condition ended the loop, which is observable
/// rather than judged, and they are the two `break` statements in
/// <see cref="EodhdClient.GetAllPagesAsync"/>.
/// </summary>
public sealed class PagedReadIncompleteException : InvalidOperationException
{
    public PagedReadIncompleteException(string path, int collected, int reportedTotal)
        : base($"Paged read of '{path}' stopped while the endpoint was still offering a next link, " +
               $"having collected {collected.ToString("N0", CultureInfo.InvariantCulture)} rows against a " +
               $"reported total of {reportedTotal.ToString("N0", CultureInfo.InvariantCulture)}. " +
               "This is the client failing to ask rather than the server running out, and what was " +
               "missed is unknown, so it fails the stage [A20, D-71].")
    {
        Collected = collected;
        ReportedTotal = reportedTotal;
    }

    public int Collected { get; }

    public int ReportedTotal { get; }
}

/// <summary>One page of a paged endpoint, plus what the endpoint said about the whole set.</summary>
/// <param name="Rows">The page's rows, already detached from the response document.</param>
/// <param name="Total">
/// <c>meta.total</c>, or null when absent. Measured at 1.9 to match the filings
/// index exactly on CCS.US, NVDA.US and PHAT.US, which is what makes it worth
/// asserting against rather than trusting.
/// </param>
/// <param name="NextPath">
/// <c>links.next</c>, or null when the endpoint says there is no more.
/// **Termination keys on this and never on a short page** [A20]: a total that is an
/// exact multiple of the page size makes the last full page indistinguishable from
/// a middle one, and a short page mid-sequence would end the loop early and
/// silently.
/// </param>
public readonly record struct EodhdPage(
    IReadOnlyList<JsonElement> Rows, int? Total, string? NextPath);

/// <summary>
/// Where a shortfall sat in the pages that were collected, which decides whether it
/// can reach a trailing window [D-71].
///
/// A page before the last returning fewer rows than the page size puts missing rows
/// **inside** the history, so a trailing-90-day metric can be affected. A final page
/// shorter than `meta.total` minus its own offset puts them at the **oldest** end,
/// outside every trailing window, and leaves `insider_net_90d_usd` and
/// `distinct_buyer_count` untouched.
///
/// Derived from the page shapes already collected. No extra call is made to find
/// out, which is what lets the position be recorded for every affected ticker
/// rather than for a sample.
/// </summary>
public enum ShortfallPosition
{
    /// <summary>Nothing missing, or no `meta.total` to compare against.</summary>
    None,

    /// <summary>At least one page before the last came back short.</summary>
    Interior,

    /// <summary>Only the final page came back short of what the total implied.</summary>
    Final,

    /// <summary>Both, so the interior reading governs.</summary>
    Both,
}

/// <summary>
/// Every row a paged endpoint gave up, plus what it claimed and what it withheld.
/// </summary>
/// <param name="Rows">Everything collected, in the order the endpoint sent it.</param>
/// <param name="ReportedTotal"><c>meta.total</c>, or null where the endpoint sends none.</param>
/// <param name="Shortfall">
/// <c>meta.total</c> minus the rows delivered, or zero. Non-zero only where the
/// server ran out of pages first, because the other case throws [D-71].
/// </param>
/// <param name="Position">Where in the sequence the missing rows sat.</param>
public readonly record struct PagedRead(
    IReadOnlyList<JsonElement> Rows, int? ReportedTotal, int Shortfall, ShortfallPosition Position);

/// <summary>
/// The typed client for the data provider. No maintained C# client exists, so a
/// thin one is written here, which also keeps point-in-time discipline under
/// direct control [D-50].
///
/// Four properties of the request form are pinned by tests rather than by habit,
/// because the provider's answer to each being wrong is a 4xx that reads like
/// something else: <c>api_token</c> in the query string, an explicit
/// <c>fmt=json</c>, the <c>::</c> filter with its colons percent-encoded, and the
/// <c>page[offset]</c> paging form with its brackets percent-encoded.
/// </summary>
public sealed class EodhdClient
{
    private readonly HttpClient _http;
    private readonly string _apiToken;
    private readonly EodhdRateLimiter _limiter;

    /// <summary>
    /// An <see cref="HttpClient"/> configured the way this client wants one.
    ///
    /// <c>PooledConnectionLifetime</c> is the reason this exists. A single
    /// long-lived HttpClient is what avoids socket exhaustion, but it also pins DNS
    /// for the life of the process, so a provider that moves an address is followed
    /// only after a restart. Recycling pooled connections on a timer is the other
    /// thing IHttpClientFactory does, and it is the half that matters here: a
    /// nightly run is minutes, but phase 3's backfill holds one process for hours
    /// [A22]. One property rather than a package and a DI registration.
    /// </summary>
    public static HttpClient CreateHttpClient(TimeSpan? pooledConnectionLifetime = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = pooledConnectionLifetime ?? TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(EodhdUrl.BaseAddress),
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public EodhdClient(HttpClient http, string apiToken, IClock clock, int requestsPerMinute = 1000)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        _http = http;
        _apiToken = apiToken;
        _limiter = new EodhdRateLimiter(clock, requestsPerMinute);

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(EodhdUrl.BaseAddress);
        }
    }

    /// <summary>The request path this client would send, for a test to assert verbatim [A19].</summary>
    public string PathFor(string path, IEnumerable<(string Name, string Value)> query)
        => EodhdUrl.Build(path, query, _apiToken);

    /// <summary>One call. The response body parsed as JSON.</summary>
    public async Task<JsonDocument> GetAsync(
        string path, IEnumerable<(string Name, string Value)> query, CancellationToken ct = default)
    {
        var relative = EodhdUrl.Build(path, query, _apiToken);
        return await SendAsync(relative, path, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(string relative, string pathForErrors, CancellationToken ct)
    {
        await _limiter.WaitAsync(ct).ConfigureAwait(false);

        using var response = await _http.GetAsync(relative, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The path is named and the token is not. The body is included because
            // this provider says useful things in it, including naming the paging
            // form in a 422.
            //
            // **The status code is carried on the exception and not only in the
            // message** [3.6]. Every caller that tolerates a missing ticker does so by
            // catching this type, and without the code the only way to tell a 404 from
            // a 402 is to parse the text. A sweep that catches both writes nothing for
            // the ticker that hit the wall and then nothing for every ticker after it,
            // because a 402 persists for the day, and returns having completed over a
            // partial load. That is the failure D-71 and the allowance gate both exist
            // to prevent, arriving one layer down in the caller's catch.
            throw new HttpRequestException(
                $"'{pathForErrors}' returned {(int)response.StatusCode}. Body: " +
                (body.Length > 400 ? body[..400] + "..." : body),
                inner: null,
                statusCode: response.StatusCode);
        }

        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// One page of a paged endpoint, in the <c>{data, meta, links}</c> envelope
    /// this provider uses for filings.
    /// </summary>
    public async Task<EodhdPage> GetPageAsync(
        string path, IEnumerable<(string Name, string Value)> query, int offset, int limit,
        CancellationToken ct = default)
    {
        var full = query.Concat(EodhdUrl.Page(offset, limit));
        using var doc = await GetAsync(path, full, ct).ConfigureAwait(false);
        return ReadPage(doc.RootElement);
    }

    /// <summary>
    /// Every row of a paged endpoint, walked to the end.
    ///
    /// Terminates when the endpoint stops offering a next link, never on a short
    /// page [A20]. A total that is an exact multiple of the page size makes the last
    /// full page indistinguishable from a middle one, so a short page cannot be the
    /// signal.
    ///
    /// **Two ways to end short and they are not the same failure** [D-71]. Ending
    /// while `links.next` is still offered is this client failing to ask: what was
    /// missed is unknown, asking again would fix it, and it throws. Ending because
    /// the server offered no next link and still delivering fewer rows than
    /// `meta.total` is the provider disagreeing with itself: everything available
    /// has been asked for, another call cannot recover the rest, and it is returned
    /// as a shortfall for the stage to record.
    ///
    /// The distinction is which `break` ran, which is why it is structural rather
    /// than a judgement about how short is too short. No threshold appears here and
    /// none is to be added without evidence gathered after D-71 was written.
    /// </summary>
    public async Task<PagedRead> GetAllPagesAsync(
        string path, IEnumerable<(string Name, string Value)> query, int pageSize,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var materialised = query.ToList();
        var all = new List<JsonElement>();
        int? total = null;
        var offset = 0;

        // The page shapes, kept so the position of a shortfall can be derived from
        // pages already collected rather than from a second read [D-71].
        var interiorShort = false;
        var lastOffset = 0;
        var lastRows = 0;
        var serverRanOut = false;

        while (true)
        {
            var page = await GetPageAsync(path, materialised, offset, pageSize, ct).ConfigureAwait(false);
            total ??= page.Total;
            all.AddRange(page.Rows);

            lastOffset = offset;
            lastRows = page.Rows.Count;

            if (page.NextPath is null)
            {
                serverRanOut = true;
                break;
            }

            // Short and not last: the missing rows sit inside the history rather
            // than at its oldest end, so a trailing window can reach them.
            if (page.Rows.Count < pageSize)
            {
                interiorShort = true;
            }

            offset += pageSize;

            // A next link that yields nothing would otherwise spin. The endpoint
            // has never done this; the loop does not depend on it not doing it.
            // This is the client stopping while more was on offer, so it falls to
            // the throw below rather than to the shortfall.
            if (page.Rows.Count == 0)
            {
                break;
            }
        }

        if (total is not int reported || all.Count == reported)
        {
            return new PagedRead(all, total, 0, ShortfallPosition.None);
        }

        if (!serverRanOut)
        {
            throw new PagedReadIncompleteException(path, all.Count, reported);
        }

        // More rows than claimed is not a shortfall and is not this decision's
        // subject. Reported as zero rather than as a negative, since the caller
        // records a shortfall and there is none.
        var missing = reported - all.Count;
        if (missing <= 0)
        {
            return new PagedRead(all, total, 0, ShortfallPosition.None);
        }

        var finalShort = lastOffset + lastRows < reported;

        var position = (interiorShort, finalShort) switch
        {
            (true, true) => ShortfallPosition.Both,
            (true, false) => ShortfallPosition.Interior,
            (false, true) => ShortfallPosition.Final,

            // Neither page shape explains it, which means the pages overlapped or
            // the endpoint counted rows it never listed. Interior is the reading
            // that assumes the metric is affected, and assuming the safer of two
            // is wrong in the direction that gets noticed.
            _ => ShortfallPosition.Interior,
        };

        return new PagedRead(all, total, missing, position);
    }

    private static EodhdPage ReadPage(JsonElement root)
    {
        var rows = new List<JsonElement>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in root.EnumerateArray())
            {
                rows.Add(row.Clone());
            }

            return new EodhdPage(rows, null, null);
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in data.EnumerateArray())
            {
                rows.Add(row.Clone());
            }
        }

        int? total = null;
        if (root.TryGetProperty("meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("total", out var t)
            && t.TryGetInt32(out var parsed))
        {
            total = parsed;
        }

        string? next = null;
        if (root.TryGetProperty("links", out var links)
            && links.ValueKind == JsonValueKind.Object
            && links.TryGetProperty("next", out var n)
            && n.ValueKind == JsonValueKind.String)
        {
            next = n.GetString();
        }

        return new EodhdPage(rows, total, next);
    }
}
