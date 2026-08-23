using System.Diagnostics;
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

/// <summary>
/// The spans of a range execution that are not the work loop, named [item 43].
///
/// **The per-unit figures described 46.5 percent of the one stage that had run.** C08 at
/// `run_log` 1716 reported 950,200 ms across 22 chunks against a stage duration of
/// 2,043,579 ms, and the missing 1,093,379 ms was one unbroken span before the loop: the
/// calendar, the epoch map, a membership read and a composite build per epoch across 292
/// epochs, and the benchmark. Every range mode had that gap, because 3.17 wired
/// <see cref="RangeTiming"/> into the loops and nothing above them.
///
/// **Named phases rather than one setup total**, because two of the questions already
/// owed are about individual phases rather than about the sum: item 41's calendar read has
/// no isolated figure that is not a warm probe, and whether C08's setup is dominated by
/// 292 epochs or by one benchmark read decides entirely different things.
///
/// **Marks are spans between calls, so a span timed elsewhere has to be dropped rather
/// than left to fall into the next phase.** <see cref="Skip"/> is that, and it is what
/// C10 needs: its write is one batched COPY after the date loop, so the loop's span sits
/// between the last setup mark and the write.
/// </summary>
public sealed class PhaseTimer
{
    private readonly List<(string Phase, long Ms)> _phases = [];
    private readonly Func<long> _elapsedMs;
    private long _mark;

    /// <param name="elapsedMs">
    /// A monotonic millisecond source of any origin. Null takes a <see cref="Stopwatch"/>,
    /// which is a tick counter rather than a wall clock and is therefore not the ambient
    /// clock INVARIANT 11 forbids; C01 has read it directly since 3.11.
    ///
    /// **It is injectable so that `Skip` can be tested without a sleep.** The property
    /// that matters is that a dropped span does not fall into the next phase, and the only
    /// way to assert that against a real stopwatch is to make a span long enough to see,
    /// which is the shape of test item 29 recorded as passing only on a slow resolver.
    /// </param>
    public PhaseTimer(Func<long>? elapsedMs = null)
    {
        var stopwatch = Stopwatch.StartNew();

        _elapsedMs = elapsedMs ?? (() => stopwatch.ElapsedMilliseconds);
        _mark = _elapsedMs();
    }

    /// <summary>Every phase recorded, in the order they ran.</summary>
    public IReadOnlyList<(string Phase, long Ms)> Phases => _phases;

    /// <summary>Records the span since the previous mark under this name.</summary>
    public void Mark(string phase) => _phases.Add((phase, Take()));

    /// <summary>Drops the span since the previous mark, for work timed by something else.</summary>
    public void Skip() => Take();

    /// <summary>Every phase recorded, in the order they ran, and their sum.</summary>
    public string Describe()
    {
        if (_phases.Count == 0)
        {
            return "Outside the work loop: nothing timed.";
        }

        var parts = _phases.Select(p => string.Create(
            CultureInfo.InvariantCulture, $"{p.Phase} {p.Ms:N0} ms"));

        return string.Format(
            CultureInfo.InvariantCulture,
            "Outside the work loop: {0}; {1:N0} ms in all.",
            string.Join(", ", parts), _phases.Sum(p => p.Ms));
    }

    private long Take()
    {
        var now = _elapsedMs();
        var ms = now - _mark;
        _mark = now;
        return ms;
    }
}
