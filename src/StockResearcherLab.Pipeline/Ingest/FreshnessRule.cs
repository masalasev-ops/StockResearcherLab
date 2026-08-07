using System.Globalization;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// The three checks, as a pure function over row counts by date.
///
/// Separated from the stage so the rule can be tested without a database or a
/// provider. The rule is the part that goes wrong quietly: a guard that passes for
/// the wrong reason produces a night of orders on incomplete bars and looks exactly
/// like a guard that passed for the right one.
/// </summary>
public static class FreshnessRule
{
    /// <summary>
    /// <paramref name="countsNewestFirst"/> is one entry per date present in
    /// <c>price_daily</c>, newest first. A date with no session never lands a row,
    /// so these are trading dates by construction [A28].
    /// </summary>
    /// <param name="mostRecentCompletedSession">
    /// From the exchange calendar, not from <c>price_daily</c>. Deriving it from the
    /// table would make recency circular: comparing the newest stored date against
    /// itself cannot detect a whole missing session, which is half of what the check
    /// is for [A4].
    /// </param>
    public static FreshnessVerdict Evaluate(
        IReadOnlyList<(DateOnly Date, long Rows)> countsNewestFirst,
        DateOnly mostRecentCompletedSession,
        long abortBelow,
        long alertBelow,
        decimal settledFraction,
        int settledWindowDays)
    {
        ArgumentNullException.ThrowIfNull(countsNewestFirst);

        var steps = new List<FreshnessStep>();

        if (countsNewestFirst.Count == 0)
        {
            steps.Add(new FreshnessStep(default, 0, FreshnessOutcome.NoData,
                "price_daily holds no rows at all"));
            return new FreshnessVerdict(null, false, false, steps);
        }

        // RECENCY, once, against the newest date there is. A provider that has not
        // updated and a run that was missed both look like this, and neither is
        // something a later date can rescue.
        var newest = countsNewestFirst[0].Date;
        if (newest < mostRecentCompletedSession)
        {
            steps.Add(new FreshnessStep(newest, countsNewestFirst[0].Rows, FreshnessOutcome.Stale,
                Iso(newest) + " is the newest date held and the most recent completed session is " +
                Iso(mostRecentCompletedSession)));
            return new FreshnessVerdict(null, false, false, steps);
        }

        var skipped = false;

        for (var i = 0; i < countsNewestFirst.Count; i++)
        {
            var (date, rows) = countsNewestFirst[i];

            // COMPLETENESS, before settledness and deliberately so. A file that is
            // catastrophically short fails both, and the two mean different things:
            // truncated is a fault to abort on, still filling is a date to skip
            // [D-59, D-64].
            if (rows < abortBelow)
            {
                steps.Add(new FreshnessStep(date, rows, FreshnessOutcome.Truncated,
                    Iso(date) + " holds " + N(rows) + " rows, below the abort floor of " + N(abortBelow)));
                return new FreshnessVerdict(null, false, skipped, steps);
            }

            // SETTLEDNESS, relative to the trailing population and computed from
            // price_daily alone. Not a re-fetch: a stage is a pure function of its
            // date and config version, and two databases with identical contents
            // must agree [D-70].
            var prior = countsNewestFirst.Skip(i + 1).Take(settledWindowDays).Select(x => x.Rows).ToList();

            if (prior.Count < settledWindowDays)
            {
                // Not enough history to take a median over. Settledness is not
                // evaluated and the run log says so. Aborting would mean the first
                // run ever cannot bootstrap; passing silently would turn the check
                // off when the data is least trustworthy. Recency and completeness
                // still apply, and D-64 gives the absolute floors exactly this job
                // [A26].
                skipped = true;
                steps.Add(new FreshnessStep(date, rows, FreshnessOutcome.Usable,
                    Iso(date) + " holds " + N(rows) + " rows; settledness skipped, only " +
                    prior.Count.ToString(CultureInfo.InvariantCulture) + " of " +
                    settledWindowDays.ToString(CultureInfo.InvariantCulture) + " prior dates present"));

                return new FreshnessVerdict(date, rows < alertBelow, true, steps);
            }

            var median = Median(prior);
            var floor = median * settledFraction;

            if (rows < floor)
            {
                steps.Add(new FreshnessStep(date, rows, FreshnessOutcome.Unsettled,
                    Iso(date) + " holds " + N(rows) + " rows against a trailing median of " +
                    N(median) + ", below " + settledFraction.ToString("0.##", CultureInfo.InvariantCulture) +
                    " of it"));
                continue;
            }

            steps.Add(new FreshnessStep(date, rows, FreshnessOutcome.Usable,
                Iso(date) + " holds " + N(rows) + " rows against a trailing median of " + N(median)));

            return new FreshnessVerdict(date, rows < alertBelow, skipped, steps);
        }

        // Every date held is still filling. Nothing to run on.
        return new FreshnessVerdict(null, false, skipped, steps);
    }

    /// <summary>
    /// A median, not a mean. One date stuck short cannot move it, which matters
    /// because a date short enough to fail settledness stays in this window until
    /// C02's reload tops it up [A26].
    /// </summary>
    private static decimal Median(IReadOnlyList<long> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;

        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string N(decimal n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
