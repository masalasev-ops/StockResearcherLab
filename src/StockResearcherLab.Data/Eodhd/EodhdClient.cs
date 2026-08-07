using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core;

namespace StockResearcherLab.Data.Eodhd;

/// <summary>
/// Thrown when a paged read collected a different number of distinct rows than the
/// endpoint said it had. A partial ingest that completes is worse than one that
/// fails, because the next stage cannot tell a short table from a real one
/// [CLAUDE.md section 6].
/// </summary>
public sealed class PagedReadIncompleteException : InvalidOperationException
{
    public PagedReadIncompleteException(string path, int collected, int reportedTotal)
        : base($"Paged read of '{path}' collected {collected.ToString("N0", CultureInfo.InvariantCulture)} " +
               $"rows against a reported total of {reportedTotal.ToString("N0", CultureInfo.InvariantCulture)}. " +
               "A short read that returns successfully looks identical to a complete one, so this fails " +
               "the stage rather than logging [A20].")
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
            throw new HttpRequestException(
                $"'{pathForErrors}' returned {(int)response.StatusCode}. Body: " +
                (body.Length > 400 ? body[..400] + "..." : body));
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
    /// page [A20]. Asserts the collected count against <c>meta.total</c> where the
    /// endpoint supplies one, so a partial read fails here rather than becoming a
    /// short table three stages later.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GetAllPagesAsync(
        string path, IEnumerable<(string Name, string Value)> query, int pageSize,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var materialised = query.ToList();
        var all = new List<JsonElement>();
        int? total = null;
        var offset = 0;

        while (true)
        {
            var page = await GetPageAsync(path, materialised, offset, pageSize, ct).ConfigureAwait(false);
            total ??= page.Total;
            all.AddRange(page.Rows);

            if (page.NextPath is null)
            {
                break;
            }

            offset += pageSize;

            // A next link that yields nothing would otherwise spin. The endpoint
            // has never done this; the loop does not depend on it not doing it.
            if (page.Rows.Count == 0)
            {
                break;
            }
        }

        if (total is int reported && all.Count != reported)
        {
            throw new PagedReadIncompleteException(path, all.Count, reported);
        }

        return all;
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
