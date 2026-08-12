using System.Text.Json;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// The exchange symbol list, reduced to the instruments D-4 admits.
///
/// Shared by C01, which applies it as one of the six absolute criteria, and C03,
/// which applies it to avoid spending a per-ticker call on something the universe
/// will reject anyway. The US list carries 51,401 instruments of which 18,156 are
/// common stock; the rest are funds, ETFs, preferreds, warrants, units, notes and
/// indices, and every one of them is out under D-2 and D-4.
///
/// **This is not a downstream narrowing.** The list is part of the universe
/// definition itself, which is the one place anything is filtered out
/// [D-5, INVARIANT 1]. C03 applying it early is the same filter applied sooner, not
/// a second one.
/// </summary>
public static class SymbolList
{
    /// <summary>
    /// D-4 admits common stock and ADRs and excludes funds, trusts and SPACs. The
    /// provider types ADRs as common stock, so this one value covers both.
    /// </summary>
    public const string AdmittedType = "Common Stock";

    /// <summary>Ticker to listed name, for everything admitted. One call.</summary>
    public static Task<IReadOnlyDictionary<string, string>> AdmittedAsync(
        EodhdClient client, CancellationToken ct = default)
        => AdmittedFromAsync(client, [], ct);

    /// <summary>
    /// The same, for instruments that have stopped trading [3.1, 3.6].
    ///
    /// **`delisted=1` returns only delisted instruments and is disjoint from the live
    /// list**, measured at 3.1: 59,183 codes against the live 51,651 with an
    /// intersection of zero. So the price pool is the union of the two calls rather
    /// than this one alone, and a caller wanting everything makes both.
    ///
    /// **Both forms are called rather than assuming the parameter is honoured.** A
    /// parameter this provider ignores comes back as a 200 holding the wrong answer,
    /// which is how form4's paging was misread at 1.9, and the disjointness above is
    /// what proves it is honoured here.
    ///
    /// 32,611 of the delisted list are common stock. The symbol list carries no
    /// delisting date, so which of them traded inside the backfill window is not
    /// answerable from here: it is the last bar `eod/{t}` returns, and the call is what
    /// reveals it.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, string>> AdmittedDelistedAsync(
        EodhdClient client, CancellationToken ct = default)
        => AdmittedFromAsync(client, [("delisted", "1")], ct);

    private static async Task<IReadOnlyDictionary<string, string>> AdmittedFromAsync(
        EodhdClient client, (string Name, string Value)[] query, CancellationToken ct)
    {
        using var doc = await client.GetAsync("exchange-symbol-list/US", query, ct).ConfigureAwait(false);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "exchange-symbol-list/US did not return an array. The universe definition cannot be " +
                "applied without it, and building a universe without the instrument-type criterion " +
                "would admit every fund on the exchange [D-2, D-4].");
        }

        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Type", out var type)
                || !string.Equals(type.GetString(), AdmittedType, StringComparison.Ordinal))
            {
                continue;
            }

            var code = row.TryGetProperty("Code", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            // price_daily keys on the provider's own ticker form, the code plus the
            // exchange short name, which for this feed is always US.
            map[code + ".US"] = row.TryGetProperty("Name", out var n) ? n.GetString() ?? code : code;
        }

        if (map.Count == 0)
        {
            throw new InvalidOperationException(
                "exchange-symbol-list/US returned no instrument typed '" + AdmittedType +
                "'. That is a shape change rather than an empty exchange, and it would silently " +
                "empty the universe.");
        }

        return map;
    }
}
