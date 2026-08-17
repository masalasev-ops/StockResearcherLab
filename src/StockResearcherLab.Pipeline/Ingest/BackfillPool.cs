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
    ///
    /// **The statement carries the pool bound, and it did not when this helper was
    /// extracted** [D-102]. C03 gives the identical statement
    /// `universe.pool_statement_timeout_seconds` and records at its own call site that
    /// leaving it on the connection string's `Command Timeout` had already failed a sweep
    /// once. Extracting the shared half brought the SQL across and left the bound behind,
    /// so the two diverged again: 3.8's first day ran it warm at 3.1s and passed, and its
    /// second day timed out at 300 on a cold cache before a ticker was dispatched. One
    /// statement, one bound, resolved from the one key.
    /// </summary>
    public static async Task<IReadOnlySet<string>> DelistedWithBarsInWindowAsync(
        EodhdClient client, StageContext context, DateOnly windowStart, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var statementTimeout = await StatementTimeoutAsync(context, ct).ConfigureAwait(false);

        var delisted = await SymbolList.AdmittedDelistedAsync(client, ct).ConfigureAwait(false);

        var from = windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var traded = await context.Data.ReadAsync(
            "price_daily",
            $"""
             SELECT DISTINCT ticker FROM price_daily
             WHERE date >= DATE '{from}'
             ORDER BY ticker;
             """,
            ct, statementTimeout).ConfigureAwait(false);

        var inWindow = traded.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);

        return delisted.Keys.Where(inWindow.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolved as of the context's date rather than as of now, which is what every other
    /// reader of this key does [INVARIANT 13].
    /// </summary>
    private static async Task<int> StatementTimeoutAsync(StageContext context, CancellationToken ct)
    {
        const string Key = "universe.pool_statement_timeout_seconds";

        var row = await context.Config.RequireAsync(Key, context.Date, ct).ConfigureAwait(false);

        return int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{Key} resolved to '{row.Value}', which is not a whole number.");
    }
}
