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

        // **A loose index scan, because the plain `SELECT DISTINCT date` does not
        // complete against the loaded store** [item 41]. Measured 2026-08-18: `run_log`
        // 1715, C08 over the whole window, failed at 300,254 ms with nothing written,
        // which is `Command Timeout=300` and therefore a statement that did not finish
        // in five minutes rather than a fault to retry. D-102 measured why one table
        // over: **Postgres does not do a loose index scan for `DISTINCT`**, so the plain
        // form is one full pass over every index entry, 109.8 million of them here.
        //
        // Each iteration is one `min(date)` bounded below by the date before it, which
        // the planner answers as an index descent and a single row. About 1,400 descents
        // over the five-year window against that one full pass.
        //
        // **The index is `price_daily_date_ticker_ix (date, ticker)` from 0007 and the
        // primary key cannot serve this at all.** `PRIMARY KEY (ticker, date)` leads on
        // ticker [0001], so a date-bounded skip has an unconstrained leading column and
        // no descent to make. This is the difference D-102's case hides: `DISTINCT
        // ticker` and `DISTINCT date` read as the same shape over the same table, and the
        // first is served by the primary key while the second needs the index 0007 added
        // for the per-date reads. The two are not interchangeable and the plan is what
        // says so.
        //
        // **D-102 rejected this form and the reason it gives is why it is taken here.**
        // Inside `LiquidAsync` it regressed about seventeenfold, because a recursive CTE
        // reports a fixed estimate of 100 rows whatever the data and every node above it
        // was then costed for a hundred tickers when 88,341 arrived. Here the CTE is the
        // whole statement: nothing sits above it to be mis-costed, and the true
        // cardinality is roughly 1,400 sessions against that same estimate. Same form,
        // different surroundings, and D-102's own text names the surroundings as the
        // reason.
        //
        // **Set identity is what was proved and speed is only why it was changed**
        // [item 41]. A loose scan that skipped a date would drop a session from every
        // compute stage in the range silently, which is the harm that made taking the
        // calendar from one ticker's series unattractive, arriving through another door.
        // The timing is taken from the run rather than from a probe, which is D-102's
        // closing warning applied to its own rejection.
        var rows = await data.ReadAsync(
            "price_daily",
            $"""
             WITH RECURSIVE sessions AS (
                 SELECT min(date) AS date
                 FROM price_daily
                 WHERE date BETWEEN DATE '{Iso(from)}' AND DATE '{Iso(to)}'

                 UNION ALL

                 SELECT (SELECT min(p.date)
                         FROM price_daily p
                         WHERE p.date > s.date AND p.date <= DATE '{Iso(to)}')
                 FROM sessions s
                 WHERE s.date IS NOT NULL
             )
             SELECT date FROM sessions WHERE date IS NOT NULL ORDER BY date;
             """,
            ct).ConfigureAwait(false);

        return rows.Select(r => DateOnly.FromDateTime((DateTime) r[0]!)).ToList();
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
