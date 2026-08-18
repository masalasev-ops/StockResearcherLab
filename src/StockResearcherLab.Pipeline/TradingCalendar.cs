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
    /// **Takes the data accessor rather than a stage context, and that is what lets the
    /// driver resolve it** [3.14]. The read is guarded either way: whoever passes an
    /// accessor has declared <c>price_daily</c>, whether that is a stage through its §3
    /// Reads cell or `BackfillRun` through the scope it declares for itself. C34 and C35
    /// declare neither `price_daily` nor anything else carrying a session list, and
    /// widening a Reads cell is an authored `ARCHITECTURE.html` edit rather than a build
    /// session's [`CLAUDE.md` §13], so the calendar moved up to the driver instead of the
    /// contract moving out to fit it.
    /// </summary>
    public static async Task<IReadOnlyList<DateOnly>> SessionsAsync(
        IStageData data, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        var rows = await data.ReadAsync(
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
