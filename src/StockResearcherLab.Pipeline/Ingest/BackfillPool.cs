using System.Globalization;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// The half of a backfill pool that every sweep able to reach delisted names shares
/// [D-101].
///
/// **Stated once because three pools with two definitions is the shape this phase has
/// found every silent hole in.** C03's pool was widened at 3.7 and C04's and C06's at
/// 3.8 and 3.10, and a rule that has to be looked up per component is one a later
/// session gets wrong. The live half differs legitimately between them, C03 taking the
/// candidate pool and the other two the universe; the delisted half is one set and is
/// derived here.
///
/// **C05 cannot use this and that is not an omission.** `sec-filings` answers 404 for a
/// delisted ticker against the same string `eod/{t}` and `fundamentals/{t}` return
/// series for, so the bias there is a fact about the provider rather than a choice
/// [open item 12].
/// </summary>
public static class BackfillPool
{
    /// <summary>
    /// Every admitted delisted common stock carrying a <c>price_daily</c> bar at or
    /// after <paramref name="windowStart"/>.
    ///
    /// Names that stopped trading before the window are excluded and cost nothing. The
    /// symbol list carries no delisting date, so the last bar is the only date there is
    /// and 3.6 having loaded prices first is what makes the set computable at all.
    ///
    /// **The caller declares `price_daily`**, which is what puts the read in its §3
    /// Reads cell rather than hiding it behind a helper [D-101].
    /// </summary>
    public static async Task<IReadOnlySet<string>> DelistedWithBarsInWindowAsync(
        EodhdClient client, StageContext context, DateOnly windowStart, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var delisted = await SymbolList.AdmittedDelistedAsync(client, ct).ConfigureAwait(false);

        var from = windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var traded = await context.Data.ReadAsync(
            "price_daily",
            $"""
             SELECT DISTINCT ticker FROM price_daily
             WHERE date >= DATE '{from}'
             ORDER BY ticker;
             """,
            ct).ConfigureAwait(false);

        var inWindow = traded.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);

        return delisted.Keys.Where(inWindow.Contains).ToHashSet(StringComparer.Ordinal);
    }
}
