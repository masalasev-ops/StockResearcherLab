using StockResearcherLab.Pipeline.Ingest;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// C01's evaluation cadence over a range [3.11, D-92].
///
/// **The cadence is the assertion, not an implementation detail.** D-92 turns on
/// backfilled cells sitting on the identical population rule as live ones: C11 reads the
/// most recent `security_daily` row at or before the date, so a denser grain would put a
/// population the live system never had underneath a percentile floor, which is D-58's
/// principle applied to membership. `ARCHITECTURE.html` §3 runs C01 weekly on Sunday and
/// this is the test that the backfill agrees.
///
/// **What is not asserted here is a range execution end to end**, and the reason is open
/// item 33's one table further on: C01's pool is `price_daily`, a real table on the
/// developer database that the suite resolves its connection string against [open items
/// 10 and 26]. A range execution in a test would run `LiquidAsync` against 109.8 million
/// real rows per evaluation date. So what is covered is the layer where the cadence can
/// be lost, and the membership rules are covered by the criteria tests that already
/// exist.
/// </summary>
public sealed class UniverseCadenceTests
{
    [Fact]
    public void EveryEvaluationDateIsASunday()
    {
        var dates = UniverseBuilder.EvaluationDates(new DateOnly(2021, 1, 4), new DateOnly(2021, 6, 30));

        Assert.NotEmpty(dates);
        Assert.All(dates, d => Assert.Equal(DayOfWeek.Sunday, d.DayOfWeek));
    }

    /// <summary>
    /// The range start is a Monday and the first evaluation date is the Sunday after it,
    /// never the start itself. A date off the live cadence is one no live run would have
    /// produced, and C11 would read it as the population for every date until the next.
    /// </summary>
    [Fact]
    public void TheFirstDateIsTheFirstSundayAtOrAfterTheStartRatherThanTheStart()
    {
        var from = new DateOnly(2021, 1, 4);
        var dates = UniverseBuilder.EvaluationDates(from, new DateOnly(2021, 2, 28));

        Assert.Equal(DayOfWeek.Monday, from.DayOfWeek);
        Assert.Equal(new DateOnly(2021, 1, 10), dates[0]);
    }

    [Fact]
    public void ARangeThatStartsOnASundayTakesThatSunday()
    {
        var from = new DateOnly(2021, 1, 10);
        var dates = UniverseBuilder.EvaluationDates(from, new DateOnly(2021, 2, 28));

        Assert.Equal(DayOfWeek.Sunday, from.DayOfWeek);
        Assert.Equal(from, dates[0]);
    }

    /// <summary>
    /// Six days spanning no Sunday. The range detail reports it rather than the run
    /// dividing by zero over an empty list, which is what the guard in
    /// <c>ExecuteRangeAsync</c> is for.
    /// </summary>
    [Fact]
    public void ARangeSpanningNoSundayEvaluatesNothing()
    {
        var dates = UniverseBuilder.EvaluationDates(new DateOnly(2021, 1, 4), new DateOnly(2021, 1, 9));

        Assert.Empty(dates);
    }

    [Fact]
    public void TheLastDateIsOnOrBeforeTheRangeEnd()
    {
        var to = new DateOnly(2026, 8, 15);
        var dates = UniverseBuilder.EvaluationDates(new DateOnly(2021, 1, 4), to);

        Assert.True(dates[^1] <= to);
        Assert.True(dates[^1].AddDays(7) > to);
    }

    /// <summary>
    /// The phase's own window, pinned. `PROGRESS.md` projects this checkpoint's cost per
    /// evaluation date, so the count that projection multiplies is worth being a number a
    /// test holds rather than one arrived at by dividing days by seven: that arithmetic
    /// gives 293 and the cadence gives 292.
    /// </summary>
    [Fact]
    public void ThePhaseWindowIs292WeeklyDates()
    {
        var dates = UniverseBuilder.EvaluationDates(new DateOnly(2021, 1, 4), new DateOnly(2026, 8, 15));

        Assert.Equal(292, dates.Count);
        Assert.Equal(new DateOnly(2021, 1, 10), dates[0]);
        Assert.Equal(new DateOnly(2026, 8, 9), dates[^1]);
    }

    /// <summary>
    /// Two calls produce the identical sequence. Determinism is a correctness property
    /// here and the evaluation set is what every written row is keyed on
    /// [`CLAUDE.md` §6].
    /// </summary>
    [Fact]
    public void TheSequenceIsIdenticalAcrossCalls()
    {
        var from = new DateOnly(2021, 1, 4);
        var to = new DateOnly(2023, 12, 31);

        Assert.Equal(UniverseBuilder.EvaluationDates(from, to), UniverseBuilder.EvaluationDates(from, to));
    }
}
