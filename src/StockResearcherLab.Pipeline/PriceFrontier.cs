using System.Globalization;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// Thrown when a range execution is given an end the store cannot cover. Refuses
/// rather than clamps: a range end past the ingest frontier is an operator error, and
/// silently narrowing a three-year range is the class of thing this system keeps
/// finding [item 60].
/// </summary>
public sealed class RangeEndBeyondFrontierException : InvalidOperationException
{
    public RangeEndBeyondFrontierException(DateOnly rangeEnd, DateOnly? frontier, string detail)
        : base(
            "The range ends at " + Iso(rangeEnd) + " and the ingest frontier is " +
            (frontier is { } f ? Iso(f) : "nowhere: no date in price_daily carries a real bar count") +
            ". A compute stage over a date the ingest has not reached does not fail on the missing bar, " +
            "it computes a correct answer to the wrong question: the trailing window ending on the " +
            "unreached date holds the same bars as the window ending on the frontier, so the row is a " +
            "byte-repeat of the day before it and nothing says so [item 60]. Re-issue the range ending " +
            "at the frontier, or later once the ingest has reached it. It is refused rather than clamped " +
            "deliberately, because narrowing a range on the operator's behalf is the same silence in a " +
            "different place. " + detail)
    {
        RangeEnd = rangeEnd;
        Frontier = frontier;
    }

    public DateOnly RangeEnd { get; }

    /// <summary>Null where no date in the store carries a real bar count at all.</summary>
    public DateOnly? Frontier { get; }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>What the frontier is and how it was arrived at, for the message and for a test.</summary>
/// <param name="Date">The frontier, or null where no date carries a real bar count.</param>
/// <param name="Detail">The walk-back, stated: the date taken and every newer date skipped.</param>
public readonly record struct FrontierVerdict(DateOnly? Date, string Detail);

/// <summary>
/// The newest date <c>price_daily</c> holds a real session for, which is not the newest
/// date it holds [item 60].
///
/// **That distinction is the whole finding.** Measured 2026-08-21: the store held 3,024
/// bars on 2026-08-12 and exactly one on each of 2026-08-13, 2026-08-14 and 2026-08-17,
/// all three <c>SPY.US</c>, the benchmark load having run forward past where the equity
/// sweep stopped [D-104]. A range end of 2026-08-13 was accepted and
/// <c>indicator_daily</c> took 2,864 rows on it, every one for a ticker with no bar that
/// day and 2,864 of 2,864 identical to 2026-08-12 on <c>dist_200dma</c>, <c>adx14</c>
/// and <c>atr_pct</c>. **A trailing-window stage cannot fail on a missing bar**, so
/// nothing said so.
///
/// **The rule is C07's settledness and not a second definition of the same thing**
/// [D-65, D-70]. <see cref="Ingest.FreshnessGuard"/> asks exactly this question of the
/// nightly path, and the backfill had no equivalent and took its range end as given: a
/// guard on one path and not the other, which is the shape of the pool precondition and
/// the seeding race as well. So this reads <c>freshness.settled_window_days</c> and
/// <c>freshness.settled_fraction</c> rather than keys of its own, and the two paths
/// cannot drift on what a real bar count is.
///
/// **What it deliberately does not take from C07 is the two absolute floors.**
/// <c>freshness.row_count_abort_below</c> is 40,000, sized for the bulk feed's
/// whole-exchange row count; this store holds about 3,000 bars a session, so applying it
/// here would refuse every range ever issued. Recency is not taken either: it is a
/// comparison against the exchange calendar and therefore a provider call, and the
/// frontier is a question about what the store holds rather than about whether it is
/// current.
///
/// **The bound this rule has, stated rather than discovered later.** The median is over
/// the <c>settled_window_days</c> dates behind the candidate, so it survives up to half
/// that many thin dates at the head. A benchmark-only tail longer than half the window,
/// eleven dates at the seeded twenty, would make the trailing median thin as well and
/// the tail would read as settled. That is C07's property too and not a new one. It is
/// far outside the three days measured, and a benchmark load twenty sessions ahead of
/// the equity sweep is a larger operational problem than this guard.
/// </summary>
public static class PriceFrontier
{
    /// <summary>
    /// The rule, as a pure function over row counts by date, newest first.
    ///
    /// Separated from the read for the reason <see cref="Ingest.FreshnessRule"/> is: the
    /// rule is the part that goes wrong quietly, and a frontier that lands one date out
    /// passes for the wrong reason and looks exactly like one that landed right.
    /// </summary>
    /// <param name="countsNewestFirst">One entry per date present, newest first.</param>
    public static FrontierVerdict Evaluate(
        IReadOnlyList<(DateOnly Date, long Rows)> countsNewestFirst,
        int settledWindowDays,
        decimal settledFraction)
    {
        ArgumentNullException.ThrowIfNull(countsNewestFirst);

        if (countsNewestFirst.Count == 0)
        {
            return new FrontierVerdict(null, "price_daily holds no rows at all.");
        }

        var skipped = new List<string>();

        for (var i = 0; i < countsNewestFirst.Count; i++)
        {
            var (date, rows) = countsNewestFirst[i];
            var prior = countsNewestFirst.Skip(i + 1).Take(settledWindowDays).Select(x => x.Rows).ToList();

            if (prior.Count < settledWindowDays)
            {
                // Not enough history behind this date to take a median over, so
                // settledness is not evaluated and the date is taken. C07 does the same
                // and for the same reason [A26]: refusing would mean a store that has
                // just been seeded can never be computed over, and the alternative is a
                // check that is strictest when the data is thinnest.
                //
                // It also reaches here when the walk-back has run out of the rows the
                // read fetched, which is bounded deliberately. Both cases say which.
                return new FrontierVerdict(
                    date,
                    Line(date, rows, "taken without a settledness check, only " +
                                     N(prior.Count) + " of " + N(settledWindowDays) +
                                     " prior dates available") + After(skipped));
            }

            var median = Median(prior);
            var floor = median * settledFraction;

            if (rows < floor)
            {
                skipped.Add(Line(date, rows, "below " +
                    settledFraction.ToString("0.##", CultureInfo.InvariantCulture) +
                    " of a trailing median of " + N(median)));
                continue;
            }

            return new FrontierVerdict(
                date,
                Line(date, rows, "against a trailing median of " + N(median)) + After(skipped));
        }

        return new FrontierVerdict(
            null,
            "No date carries a real bar count. Skipped " + N(skipped.Count) + ": " +
            string.Join("; ", skipped) + ".");
    }

    /// <summary>
    /// The frontier as the store has it now.
    ///
    /// **Config resolves as of the date being asked about, never as of now**
    /// [D-43, INVARIANT 13]. The question is whether that date is coverable, so that
    /// date is what the settledness keys resolve against.
    /// </summary>
    public static async Task<FrontierVerdict> ReadAsync(
        IStageData data, IConfigStore config, DateOnly asOf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(config);

        var window = (int) await LongAsync(config, "freshness.settled_window_days", asOf, ct).ConfigureAwait(false);
        var fraction = await DecimalAsync(config, "freshness.settled_fraction", asOf, ct).ConfigureAwait(false);

        // Enough for a full window behind every candidate the walk-back could reach,
        // which is C07's own sizing. The value is an int this method computed rather
        // than input: IStageData's read route takes no parameters. A walk-back that ran
        // past it reports the shortfall rather than taking a date it could not check,
        // which is what the prior.Count branch above does.
        var limit = (window * 2) + 40;

        var rows = await data.ReadAsync(
            "price_daily",
            "SELECT date, count(*) FROM price_daily GROUP BY date ORDER BY date DESC LIMIT " +
            limit.ToString(CultureInfo.InvariantCulture) + ";",
            ct).ConfigureAwait(false);

        var counts = new List<(DateOnly, long)>(rows.Count);
        foreach (var row in rows)
        {
            counts.Add((DateOnly.FromDateTime((DateTime) row[0]!), (long) row[1]!));
        }

        return Evaluate(counts, window, fraction);
    }

    /// <summary>
    /// The frontier where the range end is covered by it, and a refusal where it is not.
    ///
    /// **Refuse, never clamp** [item 60]. A range end the store cannot cover is an
    /// operator error, and narrowing a three-year range on the operator's behalf puts
    /// the same silence one step further along.
    /// </summary>
    public static async Task<DateOnly> RequireCoveredAsync(
        IStageData data, IConfigStore config, DateOnly rangeEnd, CancellationToken ct = default)
    {
        var verdict = await ReadAsync(data, config, rangeEnd, ct).ConfigureAwait(false);

        if (verdict.Date is not { } frontier || rangeEnd > frontier)
        {
            throw new RangeEndBeyondFrontierException(rangeEnd, verdict.Date, verdict.Detail);
        }

        return frontier;
    }

    /// <summary>
    /// A median, not a mean, for the reason <see cref="Ingest.FreshnessRule"/> gives:
    /// one date stuck short cannot move it.
    /// </summary>
    private static decimal Median(IReadOnlyList<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;

        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    private static string Line(DateOnly date, long rows, string why)
        => Iso(date) + " holds " + N(rows) + " bar(s), " + why;

    /// <summary>
    /// The dates the walk-back passed over, always stated including their absence. A
    /// frontier that skipped four dates and one that skipped none are the same date and
    /// different situations, and the second is the ordinary one.
    /// </summary>
    private static string After(IReadOnlyList<string> skipped)
        => skipped.Count == 0
            ? ". No newer date was skipped."
            : ". Skipped " + N(skipped.Count) + " newer date(s): " + string.Join("; ", skipped) + ".";

    private static async Task<long> LongAsync(IConfigStore config, string key, DateOnly asOf, CancellationToken ct)
    {
        var row = await config.RequireAsync(key, asOf, ct).ConfigureAwait(false);

        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a whole number.");
    }

    private static async Task<decimal> DecimalAsync(IConfigStore config, string key, DateOnly asOf, CancellationToken ct)
    {
        var row = await config.RequireAsync(key, asOf, ct).ConfigureAwait(false);

        return decimal.TryParse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a number.");
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string N(decimal n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
