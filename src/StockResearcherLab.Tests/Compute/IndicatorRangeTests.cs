using StockResearcherLab.Pipeline.Compute;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// 3.13's range primitives, asserted without a database [D-103].
///
/// **`EpochOf` is where a range execution can silently stamp the wrong universe on a
/// past date**, which is INVARIANT 13's failure and the one D-92 and `security_daily`
/// exist to prevent. It is a pure function of two date lists, so it is tested as one.
///
/// The other two primitives are single statements and are exercised by the range
/// execution rather than here; what cannot be exercised that way is the mapping, because
/// a wrong epoch produces a full row of plausible numbers rather than an error.
/// </summary>
public sealed class IndicatorEpochTests
{
    private static readonly DateOnly[] Weekly =
    [
        new(2021, 1, 10), new(2021, 1, 17), new(2021, 1, 24),
    ];

    /// <summary>
    /// A trading date takes the latest epoch at or before it, which is the same rule
    /// `Universe.AsOf` applies inside SQL. Stated here as the C# half of one rule, so the
    /// two cannot drift the way the universe reads did before 3.12.
    /// </summary>
    [Fact]
    public void ATradingDateTakesTheLatestEpochAtOrBeforeIt()
    {
        var map = IndicatorEngine.EpochOf(
            [new(2021, 1, 11), new(2021, 1, 15), new(2021, 1, 18), new(2021, 1, 25)], Weekly);

        Assert.Equal(new DateOnly(2021, 1, 10), map[new(2021, 1, 11)]);
        Assert.Equal(new DateOnly(2021, 1, 10), map[new(2021, 1, 15)]);
        Assert.Equal(new DateOnly(2021, 1, 17), map[new(2021, 1, 18)]);
        Assert.Equal(new DateOnly(2021, 1, 24), map[new(2021, 1, 25)]);
    }

    /// <summary>
    /// **Every trading date inside one week resolves the same epoch, and that is the
    /// property the checkpoint's cost rests on.** The sector composite is rebuilt per
    /// epoch rather than per date because of it, which is 292 rebuilds over this phase's
    /// window rather than about 1,260. If C01's cadence ever stops being weekly this test
    /// still passes and the saving simply shrinks, so the cost claim is recorded against
    /// the cadence test rather than against this one.
    /// </summary>
    [Fact]
    public void EveryTradingDateInOneWeekSharesAnEpoch()
    {
        var week = new DateOnly[]
        {
            new(2021, 1, 11), new(2021, 1, 12), new(2021, 1, 13),
            new(2021, 1, 14), new(2021, 1, 15),
        };

        var map = IndicatorEngine.EpochOf(week, Weekly);

        Assert.Single(map.Values.Distinct());
        Assert.Equal(new DateOnly(2021, 1, 10), map.Values.First());
    }

    /// <summary>
    /// **A date before every epoch is absent rather than mapped to the earliest**, which
    /// is the whole reason this is a lookup returning a partial map instead of a clamp.
    /// C01 never evaluated such a date, so it has no membership; taking the first epoch
    /// would stamp a January universe on a date in the December before it and produce a
    /// full row of numbers computed against a universe that did not exist yet.
    /// </summary>
    [Fact]
    public void ADateBeforeEveryEpochHasNoneRatherThanTheEarliest()
    {
        var map = IndicatorEngine.EpochOf(
            [new(2021, 1, 4), new(2021, 1, 8), new(2021, 1, 11)], Weekly);

        Assert.False(map.ContainsKey(new(2021, 1, 4)));
        Assert.False(map.ContainsKey(new(2021, 1, 8)));
        Assert.True(map.ContainsKey(new(2021, 1, 11)));
    }

    /// <summary>
    /// A trading date falling exactly on an epoch takes that epoch and not the one
    /// before, because `Universe.AsOf` is `date &lt;= asOf` rather than a strict
    /// inequality. The boundary is asserted because an off-by-one here is a whole week of
    /// stale membership and nothing errors.
    /// </summary>
    [Fact]
    public void ADateOnAnEpochTakesThatEpochRatherThanThePreviousOne()
    {
        var map = IndicatorEngine.EpochOf([new(2021, 1, 17)], Weekly);

        Assert.Equal(new DateOnly(2021, 1, 17), map[new(2021, 1, 17)]);
    }

    /// <summary>
    /// No epochs at all maps nothing, which is what an unfilled `security_daily` produces
    /// and is the state 3.8's second day swept straight through. A range execution reading
    /// an empty map writes no rows, and that is the honest answer rather than a row per
    /// date computed against no universe.
    /// </summary>
    [Fact]
    public void NoEpochsMapsNothingRatherThanEverything()
    {
        var map = IndicatorEngine.EpochOf([new(2021, 1, 11), new(2021, 1, 12)], []);

        Assert.Empty(map);
    }
}
