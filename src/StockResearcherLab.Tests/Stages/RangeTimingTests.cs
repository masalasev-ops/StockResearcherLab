using StockResearcherLab.Pipeline;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// The work-unit wall clock a range execution reports [3.17].
///
/// **It is tested because it cannot be re-taken.** A compute stage is not run twice, so
/// a figure this renders wrongly is a figure the phase records wrongly and nothing
/// afterwards can correct: the run is gone. The arithmetic is three order statistics and
/// a sum, which is exactly the size of thing that gets an off-by-one and looks fine.
/// </summary>
public sealed class RangeTimingTests
{
    /// <summary>
    /// First and last are chronological and the median is of the sorted list, which are
    /// three different readings and only agree on a monotonic series.
    ///
    /// The series here is deliberately not monotonic and not sorted: 900 first, then the
    /// body, then 100 last. A median taken as `elapsed[count / 2]` off the unsorted list
    /// would answer 300 rather than 400, and a first taken off the sorted list would
    /// answer 100 rather than 900. Both are the mistake that makes a cold-start figure
    /// read as a warm one.
    /// </summary>
    [Fact]
    public void FirstAndLastAreChronologicalAndTheMedianIsOfTheSortedList()
    {
        var line = RangeTiming.Describe("date", [900, 200, 300, 400, 500, 100]);

        Assert.Contains("first 900 ms", line, StringComparison.Ordinal);
        Assert.Contains("median 400 ms", line, StringComparison.Ordinal);
        Assert.Contains("last 100 ms", line, StringComparison.Ordinal);
        Assert.Contains("total 2,400 ms", line, StringComparison.Ordinal);
        Assert.Contains("over 6 unit(s)", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// An odd count takes the true middle. Stated separately because `count / 2` is
    /// right for odd and is the upper of the two middles for even, and a reader checking
    /// one case would not know which convention the other follows.
    /// </summary>
    [Fact]
    public void AnOddCountTakesTheTrueMiddle()
    {
        var line = RangeTiming.Describe("date", [50, 10, 30]);

        Assert.Contains("first 50 ms", line, StringComparison.Ordinal);
        Assert.Contains("median 30 ms", line, StringComparison.Ordinal);
        Assert.Contains("last 30 ms", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The unit is named rather than assumed, because three of the six compute stages are
    /// ticker-partitioned and have no per-date figure at all [`CLAUDE.md` §5].
    /// </summary>
    [Fact]
    public void TheUnitIsNamedInTheLine()
    {
        Assert.Contains(
            "Per chunk of 200 ticker(s):",
            RangeTiming.Describe("chunk of 200 ticker(s)", [1]),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// No unit ran says so rather than reporting four zeroes, which would be a stage that
    /// did its work instantly.
    /// </summary>
    [Fact]
    public void NoUnitSaysSoRatherThanReportingZeroes()
    {
        var line = RangeTiming.Describe("date", []);

        Assert.Contains("no unit ran", line, StringComparison.Ordinal);
        Assert.DoesNotContain("median", line, StringComparison.Ordinal);
    }

    // ------------------------------------------ the spans outside the loop ---

    /// <summary>
    /// Marks are the spans between calls, named and summed [item 43].
    ///
    /// Driven off an injected millisecond source rather than a stopwatch, so the
    /// assertion is on arithmetic rather than on how long the test machine took.
    /// </summary>
    [Fact]
    public void MarksAreTheSpansBetweenCallsAndTheSumIsTheirTotal()
    {
        var now = 0L;
        var phases = new PhaseTimer(() => now);

        now = 40;
        phases.Mark("calendar");
        now = 140;
        phases.Mark("epochs");
        now = 1_140;
        phases.Mark("composites");

        Assert.Equal([("calendar", 40L), ("epochs", 100L), ("composites", 1_000L)], phases.Phases);

        var line = phases.Describe();
        Assert.Contains("calendar 40 ms", line, StringComparison.Ordinal);
        Assert.Contains("epochs 100 ms", line, StringComparison.Ordinal);
        Assert.Contains("composites 1,000 ms", line, StringComparison.Ordinal);
        Assert.Contains("1,140 ms in all", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// **`Skip` drops a span rather than folding it into the next phase**, which is the
    /// property C10 depends on: its write is one batched COPY after the date loop, so the
    /// loop's span sits between the last setup mark and the write and is already counted
    /// by the per-date figures.
    ///
    /// Without the drop, C10's write would be reported as the loop plus the write and the
    /// two accounts would double-count the larger of them.
    /// </summary>
    [Fact]
    public void SkipDropsASpanRatherThanFoldingItIntoTheNextPhase()
    {
        var now = 0L;
        var phases = new PhaseTimer(() => now);

        now = 10;
        phases.Mark("calendar");

        now = 9_000;      // the date loop, timed by the per-unit figures
        phases.Skip();

        now = 9_250;
        phases.Mark("write");

        Assert.Equal([("calendar", 10L), ("write", 250L)], phases.Phases);
        Assert.DoesNotContain("8,990", phases.Describe(), StringComparison.Ordinal);
        Assert.Contains("260 ms in all", phases.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void NoPhaseSaysSoRatherThanReportingAZero()
        => Assert.Contains(
            "nothing timed", new PhaseTimer(() => 0).Describe(), StringComparison.Ordinal);
}
