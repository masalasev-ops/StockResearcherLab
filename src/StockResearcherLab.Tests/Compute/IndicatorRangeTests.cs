using StockResearcherLab.Pipeline;
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
        var map = Membership.EpochOf(
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

        var map = Membership.EpochOf(week, Weekly);

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
        var map = Membership.EpochOf(
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
        var map = Membership.EpochOf([new(2021, 1, 17)], Weekly);

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
        var map = Membership.EpochOf([new(2021, 1, 11), new(2021, 1, 12)], []);

        Assert.Empty(map);
    }
}

/// <summary>
/// The slice the range path hands `Compute`, asserted as a set of bars rather than
/// through its output [item 33, 3.13].
///
/// **The end-to-end seam test compares two outputs and this states the input.** Two paths
/// slicing the same wrong window produce equal rows, so an equality test over them reads
/// exactly like a passing one. What is stated here is the rule the nightly statement
/// implements, `rn <= bars` over `date <= D` ordered descending: the window ends on the
/// date, never after it, and is that many bars deep or everything there is.
/// </summary>
public sealed class IndicatorWindowTests
{
    private static readonly DateOnly Start = new(2021, 1, 4);

    private static IReadOnlyList<IndicatorEngine.Bar> History(int n)
        => [.. Enumerable.Range(0, n).Select(i =>
            new IndicatorEngine.Bar(Start.AddDays(i), 11, 9, 10, 10, 1000))];

    /// <summary>
    /// The last bar is the date itself, and the depth is exact. Both ends are asserted,
    /// because an off-by-one at either is a different defect and neither errors.
    /// </summary>
    [Fact]
    public void TheWindowEndsOnTheDateAndIsExactlyAsDeepAsAsked()
    {
        var history = History(100);
        var window = IndicatorEngine.Window(history, Start.AddDays(60), 20);

        Assert.Equal(20, window.Count);
        Assert.Equal(Start.AddDays(60), window[^1].Date);
        Assert.Equal(Start.AddDays(41), window[0].Date);
    }

    /// <summary>
    /// **No bar after the date, ever.** This is the mistake that costs the most and says
    /// least: a metric computed from a price nobody could have seen makes a backfilled
    /// screen look prescient, and the row it lands in is indistinguishable from a real one.
    /// </summary>
    [Fact]
    public void NoBarAfterTheDateEntersTheWindow()
    {
        var at = Start.AddDays(30);
        var window = IndicatorEngine.Window(History(100), at, 50);

        Assert.All(window, b => Assert.True(b.Date <= at, $"{b.Date:yyyy-MM-dd} is after {at:yyyy-MM-dd}."));
    }

    /// <summary>
    /// A history shallower than the window asked for gives everything it has rather than
    /// failing. `Compute` is what decides which columns a short window nulls, and that
    /// rule lives in one place [METRICS.md].
    /// </summary>
    [Fact]
    public void AHistoryShallowerThanTheWindowGivesEverythingItHas()
    {
        var window = IndicatorEngine.Window(History(12), Start.AddDays(11), 40);

        Assert.Equal(12, window.Count);
        Assert.Equal(Start, window[0].Date);
    }

    /// <summary>
    /// A date before the first bar gives nothing, and the caller writes no row for it.
    /// Empty rather than a row of nulls, which is what keeps a reconstructed universe from
    /// carrying a name on a date it had no history on.
    /// </summary>
    [Fact]
    public void ADateBeforeTheFirstBarGivesNothing()
        => Assert.Empty(IndicatorEngine.Window(History(10), Start.AddDays(-1), 20));

    /// <summary>
    /// A date the store has no bar for takes the last bar before it, which is what the
    /// nightly statement's `date &lt;= D` does on a holiday. The range path never asks for
    /// one, its dates coming from `price_daily` itself, and the rule is asserted anyway
    /// because the two would diverge silently if it changed.
    /// </summary>
    [Fact]
    public void ADateWithNoBarTakesTheLastOneBeforeIt()
    {
        IReadOnlyList<IndicatorEngine.Bar> history =
        [
            new(Start, 11, 9, 10, 10, 1000),
            new(Start.AddDays(1), 11, 9, 10, 10, 1000),
            new(Start.AddDays(5), 11, 9, 10, 10, 1000),
        ];

        var window = IndicatorEngine.Window(history, Start.AddDays(3), 10);

        Assert.Equal(2, window.Count);
        Assert.Equal(Start.AddDays(1), window[^1].Date);
    }
}

/// <summary>
/// **The one argument 3.13's cost rests on, asserted rather than reasoned.**
///
/// The range path rebuilds a sector composite once per membership epoch and reuses it
/// across that epoch's dates, where the nightly path rebuilds it per date from its own
/// trailing window. The two chains therefore start at different bars and differ by a
/// constant factor per sector. That is only safe because the composite reaches the output
/// through `Change`, which divides two of its levels.
///
/// **If that were wrong every backfilled row would be subtly wrong and nothing would
/// error**, which is the failure mode `CLAUDE.md` §1 describes. So it is a test and not a
/// comment: two composites differing by a constant must produce the identical column.
/// </summary>
public sealed class SectorCompositeRebasingTests
{
    private const int Bars = 300;

    private static readonly DateOnly Start = new(2021, 1, 4);

    /// <summary>
    /// A series with movement in it. A flat series would satisfy any rebasing claim
    /// trivially, including a wrong one.
    /// </summary>
    private static IReadOnlyList<IndicatorEngine.Bar> History()
    {
        var bars = new List<IndicatorEngine.Bar>(Bars);

        for (var i = 0; i < Bars; i++)
        {
            var close = 100m + (decimal) (Math.Sin(i / 9.0) * 14) + (i * 0.11m);

            bars.Add(new IndicatorEngine.Bar(
                Start.AddDays(i), close + 1.5m, close - 1.5m, close, close, 250_000 + (i * 37)));
        }

        return bars;
    }

    private static IReadOnlyDictionary<DateOnly, double> Composite(double scale)
    {
        var map = new Dictionary<DateOnly, double>();
        var level = scale;

        for (var i = 0; i < Bars; i++)
        {
            level *= 1 + (Math.Cos(i / 7.0) * 0.004) + 0.0003;
            map[Start.AddDays(i)] = level;
        }

        return map;
    }

    /// <summary>
    /// Two chains of the same returns from different bases give the identical
    /// `rs_change_vs_sector`, which is what lets one epoch's composite serve every date in
    /// that epoch.
    ///
    /// The scale is deliberately not a round number: a factor of 2 could hide an error
    /// that happens to be even, and 1.0 would test nothing at all.
    /// </summary>
    [Fact]
    public void ARebasedCompositeGivesTheIdenticalSectorColumn()
    {
        var history = History();
        var at = history[^1].Date;

        var fromOne = IndicatorEngine.Compute(
            "SRLREB.US", at, history, 14, 60, 0.35, 1_000_000m, null, Composite(1.0));

        var fromElsewhere = IndicatorEngine.Compute(
            "SRLREB.US", at, history, 14, 60, 0.35, 1_000_000m, null, Composite(7.3125));

        Assert.NotNull(fromOne.RsChangeVsSector);
        Assert.Equal(fromOne.RsChangeVsSector, fromElsewhere.RsChangeVsSector);
    }

    /// <summary>
    /// The guard on the test above: the column is sensitive to the composite's *shape*,
    /// so the equality it asserts is rebasing invariance and not the column being
    /// constant. A composite with different returns must move it.
    ///
    /// Without this, an implementation returning null or a fixed value would satisfy the
    /// invariance test perfectly.
    /// </summary>
    [Fact]
    public void ACompositeWithDifferentReturnsMovesTheSectorColumn()
    {
        var history = History();
        var at = history[^1].Date;

        var other = new Dictionary<DateOnly, double>();
        var level = 1.0;

        for (var i = 0; i < Bars; i++)
        {
            level *= 1 + (Math.Sin(i / 5.0) * 0.010) - 0.0007;
            other[Start.AddDays(i)] = level;
        }

        var baseline = IndicatorEngine.Compute(
            "SRLREB.US", at, history, 14, 60, 0.35, 1_000_000m, null, Composite(1.0));

        var moved = IndicatorEngine.Compute(
            "SRLREB.US", at, history, 14, 60, 0.35, 1_000_000m, null, other);

        Assert.NotEqual(baseline.RsChangeVsSector, moved.RsChangeVsSector);
    }

    /// <summary>
    /// A sector whose composite has a gap on the date 63 bars back yields null rather than
    /// reaching for the nearest level it has. `HAVING count(*) >= min_members` puts those
    /// gaps there deliberately, so a ratio spanning one is not computed against a day one
    /// name's noise, and the range path inherits the gaps rather than filling them.
    /// </summary>
    [Fact]
    public void AGapAtTheFarEndOfTheWindowIsNullRatherThanTheNearestLevel()
    {
        var history = History();
        var at = history[^1].Date;

        var gapped = new Dictionary<DateOnly, double>(Composite(1.0));
        gapped.Remove(history[^64].Date);

        var row = IndicatorEngine.Compute(
            "SRLREB.US", at, history, 14, 60, 0.35, 1_000_000m, null, gapped);

        Assert.Null(row.RsChangeVsSector);
    }
}
