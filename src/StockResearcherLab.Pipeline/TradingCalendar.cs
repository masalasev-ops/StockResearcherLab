using System.Globalization;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// The dates a range execution evaluates, which are the sessions <c>price_daily</c>
/// actually holds.
///
/// **Derived from the store rather than from a calendar, and shared rather than stated
/// twice.** C08 and C09 both walk a range and both need the same answer; two copies of
/// one `SELECT DISTINCT date` is the shape that drifts the moment one of them gains a
/// filter. C03's rotation ordering was copied by hand into C05 and kept the defect after
/// C03's was fixed, which is the precedent this avoids [D-95].
///
/// **A calendar walk would produce a row of nulls on a day the exchange did not trade**,
/// indistinguishable from a name with no history, which is the failure `CLAUDE.md` §6
/// describes rather than an ordinary bug. All market semantics here are US Eastern and a
/// trading date is the label the exchange gave a session.
/// </summary>
public static class TradingCalendar
{
    /// <summary>
    /// Every session between the two dates inclusive, ascending.
    ///
    /// The caller declares <c>price_daily</c>, which is what keeps the read in its §3
    /// Reads cell rather than hiding it behind a helper [D-101's precedent].
    /// </summary>
    public static async Task<IReadOnlyList<DateOnly>> SessionsAsync(
        StageContext context, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rows = await context.Data.ReadAsync(
            "price_daily",
            $"""
             SELECT DISTINCT date FROM price_daily
             WHERE date BETWEEN DATE '{Iso(from)}' AND DATE '{Iso(to)}'
             ORDER BY date;
             """,
            ct).ConfigureAwait(false);

        return rows.Select(r => DateOnly.FromDateTime((DateTime) r[0]!)).ToList();
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
