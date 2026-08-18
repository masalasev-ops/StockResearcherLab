using System.Globalization;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// The wall clock of a range execution's work unit, rendered for the run log [3.17].
///
/// **C01 has reported this since 3.11 and the other six had nothing** [`UniverseBuilder`
/// range detail]. 3.17 asks for the full rebuild's timing and `BUILD_PLAN.md` makes it a
/// definition-of-done line, and a compute stage is not run twice: a stage swept without
/// instrumentation is a figure that cannot be recovered afterwards, because the only
/// thing left is `run_log.duration_ms` and that is one number for the whole range.
///
/// **First against median is the figure worth having, not the total.** The total is
/// already in `run_log`. What is not is whether the working set survives between units
/// inside one process, which is the question the cost brackets in `PROGRESS.md` turn on:
/// item 32 measured the same statement at 531.1s cold and 22.6s warm, a factor of
/// twenty-three, and no figure on record covers many units inside one run.
///
/// **The unit is the stage's own partition key and is named rather than assumed.**
/// C08, C09 and C35 are ticker-partitioned, so their unit is a chunk of tickers and a
/// per-date figure does not exist: one chunk computes every date in the range for two
/// hundred names. C10, C34 and C11 are date-partitioned and their unit is a date
/// [`CLAUDE.md` §5].
/// </summary>
public static class RangeTiming
{
    /// <summary>
    /// One sentence naming the unit, the three order statistics and the total.
    ///
    /// **First and last are chronological and the median is of the sorted list**, which
    /// is C01's reading and the only one that answers the warm-up question: a first far
    /// above the median is a cold cache paid once, and a last far above it is something
    /// degrading as the run goes.
    /// </summary>
    /// <param name="unit">Singular, lower case, for example <c>date</c> or <c>chunk of 200 ticker(s)</c>.</param>
    /// <param name="elapsedMs">One entry per unit, in the order the units ran.</param>
    public static string Describe(string unit, IReadOnlyList<long> elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(elapsedMs);

        if (elapsedMs.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Per {unit}: no unit ran, so nothing is timed.");
        }

        var ordered = elapsedMs.OrderBy(static x => x).ToList();

        return string.Format(
            CultureInfo.InvariantCulture,
            "Per {0}: first {1:N0} ms, median {2:N0} ms, last {3:N0} ms, total {4:N0} ms over {5:N0} unit(s).",
            unit, elapsedMs[0], ordered[ordered.Count / 2], elapsedMs[^1], elapsedMs.Sum(), elapsedMs.Count);
    }
}
