using System.Globalization;
using System.Text;

namespace StockResearcherLab.Data.Eodhd;

/// <summary>
/// Builds the query string for a provider call.
///
/// Separated from the client and made pure so that the request form can be
/// asserted character for character without a live call [A19]. A test that only
/// asserts a 200 passes on any form the endpoint happens to tolerate today, and
/// the endpoint's answer to a wrongly encoded parameter is a 422 that reads like
/// an entitlement problem. That is exactly how the paging form was misread the
/// first time: `limit` and `offset` are accepted and silently ignored, while
/// `page[offset]` and `page[limit]` are the form that works.
/// </summary>
public static class EodhdUrl
{
    public const string BaseAddress = "https://eodhd.com/api/";

    /// <summary>
    /// Path plus query, relative to <see cref="BaseAddress"/>.
    ///
    /// Every call carries <c>fmt=json</c> and <c>api_token</c> explicitly rather
    /// than relying on a default, and both are appended last so the caller cannot
    /// omit or duplicate them.
    /// </summary>
    /// <param name="path">Endpoint path with no leading slash, for example <c>eod-bulk-last-day/US</c>.</param>
    /// <param name="query">
    /// Parameters in the order they should appear. Order is preserved rather than
    /// sorted, because the produced string is asserted verbatim and a set's
    /// enumeration order is unspecified [CLAUDE.md section 6].
    /// </param>
    /// <param name="apiToken">Appended as <c>api_token</c>. Never logged by anything that calls this.</param>
    public static string Build(string path, IEnumerable<(string Name, string Value)> query, string apiToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var sb = new StringBuilder(path);
        var first = true;

        foreach (var (name, value) in query)
        {
            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(Escape(name)).Append('=').Append(Escape(value));
        }

        sb.Append(first ? '?' : '&').Append("fmt=json");
        sb.Append("&api_token=").Append(Escape(apiToken));

        return sb.ToString();
    }

    /// <summary>
    /// Percent-encodes a name or value.
    ///
    /// <see cref="Uri.EscapeDataString"/> rather than any of the Uri constructors
    /// or <c>UriBuilder</c>, because those normalise and can leave square brackets
    /// and colons unescaped depending on how the request is assembled. Both matter
    /// here: the <c>::</c> filter form and the <c>page[offset]</c> paging form are
    /// the two parameters this provider is fussy about, and a wrong escaping fails
    /// as a 422 rather than as a bad request.
    /// </summary>
    public static string Escape(string s) => Uri.EscapeDataString(s);

    /// <summary>The paging parameters, in the form the endpoint accepts [1.9].</summary>
    public static IEnumerable<(string Name, string Value)> Page(int offset, int limit)
    {
        yield return ("page[offset]", offset.ToString(CultureInfo.InvariantCulture));
        yield return ("page[limit]", limit.ToString(CultureInfo.InvariantCulture));
    }
}
