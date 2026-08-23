using System.Globalization;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// The dates on which universe membership can have changed, and which of them a given
/// trading date resolves through.
///
/// **Shared rather than stated twice, for the reason `TradingCalendar` is** [3.13, D-95].
/// C08 needs this to rebuild a sector composite once per epoch instead of once per date;
/// C35 needs the same map to iterate the universe a backfilled date actually had. Two
/// copies of one rule about which universe a past date sees is the shape that drifts, and
/// the drift is INVARIANT 13's failure rather than an ordinary bug: nothing errors when a
/// date is computed against a universe it never had.
///
/// It was `IndicatorEngine`'s until 3.14 and moved here whole rather than being
/// reimplemented beside it.
/// </summary>
public static class Membership
{
    /// <summary>
    /// The membership epochs a range spans: the distinct <c>security_daily</c> dates at
    /// or before each trading date, which is the set of dates on which membership can
    /// have changed.
    ///
    /// **This is what makes a range affordable, and it is exact rather than an
    /// approximation.** `Universe.AsOf(D)` takes each ticker's most recent row at or
    /// before D, and C01 writes weekly [3.11], so every trading date inside one week
    /// resolves the same member set. Work that depends only on the member set is
    /// therefore done once per epoch and not once per date: 292 times over this phase's
    /// window rather than about 1,260.
    ///
    /// **What may be reused across an epoch's dates is a per-stage question and not a
    /// property of this map.** C08 reuses a sector composite because it enters the output
    /// only as a ratio and a rescaling cancels; that argument is stated at its own call
    /// site and does not travel with this helper.
    ///
    /// The lower bound reaches back to the last epoch at or before <paramref name="from"/>
    /// rather than to `from` itself, because the range's first date resolves through an
    /// epoch that precedes it.
    /// </summary>
    public static async Task<IReadOnlyList<DateOnly>> EpochsAsync(
        StageContext context, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rows = await context.Data.ReadAsync(
            "security_daily",
            $"""
             SELECT DISTINCT date FROM security_daily
             WHERE date <= {Literal(to)}
               AND date >= COALESCE(
                   (SELECT max(date) FROM security_daily WHERE date <= {Literal(from)}),
                   {Literal(from)})
             ORDER BY date;
             """,
            ct).ConfigureAwait(false);

        return rows.Select(r => DateOnly.FromDateTime((DateTime) r[0]!)).ToList();
    }

    /// <summary>
    /// The epoch each trading date resolves its membership through, which is the latest
    /// <c>security_daily</c> date at or before it.
    ///
    /// A trading date earlier than every epoch has none, and that is not an error: it is
    /// a date C01 never evaluated, so it has no membership and no rows are written for
    /// it. Returning it as absent rather than as the first epoch is what keeps that
    /// distinction, since taking the earliest would stamp a later universe on a date the
    /// universe did not cover [INVARIANT 13].
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, DateOnly> EpochOf(
        IEnumerable<DateOnly> tradingDates, IReadOnlyList<DateOnly> epochs)
    {
        ArgumentNullException.ThrowIfNull(tradingDates);
        ArgumentNullException.ThrowIfNull(epochs);

        var map = new Dictionary<DateOnly, DateOnly>();

        foreach (var d in tradingDates)
        {
            DateOnly? found = null;

            foreach (var e in epochs)
            {
                if (e <= d)
                {
                    found = e;
                }
                else
                {
                    break;
                }
            }

            if (found is { } epoch)
            {
                map[d] = epoch;
            }
        }

        return map;
    }

    /// <summary>
    /// The active members of one epoch with their sectors, which is what a date resolving
    /// through that epoch iterates.
    ///
    /// The caller declares <c>security_daily</c>, which is what keeps the read in its §3
    /// Reads cell rather than hiding it behind a helper [D-101's precedent].
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string?>> MembersAsync(
        StageContext context, DateOnly epoch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rows = await context.Data.ReadAsync(
            "security_daily",
            $"SELECT m.ticker, m.sector FROM {Universe.AsOf(epoch)} m WHERE m.is_active ORDER BY m.ticker;",
            ct).ConfigureAwait(false);

        var members = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            members[(string) r[0]!] = r[1] as string;
        }

        return members;
    }

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
}
