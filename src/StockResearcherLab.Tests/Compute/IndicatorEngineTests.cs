using StockResearcherLab.Pipeline.Compute;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// C08's reference. Phase 2's definition of done is that a known ticker's indicators
/// match a hand-computed reference, and these are those.
///
/// **The references are closed forms rather than a second implementation.** Wilder's
/// smoothing of a constant series is that constant, whatever the seed and however many
/// steps it runs, so a flat series pins ATR exactly without reimplementing the
/// recursion in the test. A series rising by a fixed amount every bar has one-sided
/// directional movement throughout, so its ADX is 100 for the same reason. Reproducing
/// the algorithm in the test would assert only that two copies of it agree, which is
/// what a reference is supposed to rule out.
///
/// The arithmetic behind each expectation is written out at its assertion.
/// </summary>
public sealed class IndicatorEngineTests
{
    private const int Warmup = 250;
    private const int BaseLookback = 60;
    private const double BaseMaxRange = 0.25;
    private const int Bars = 300;

    private static readonly DateOnly AsOf = new(2026, 8, 10);

    /// <summary>
    /// A flat series: close 100 every bar, high 101, low 99, volume 1,000, no split.
    /// Nine of the ten computed columns have an exact answer that can be read off those
    /// four numbers without running anything.
    /// </summary>
    [Fact]
    public void TheFlatSeriesReproducesItsClosedForm()
    {
        var row = IndicatorEngine.Compute(
            "SRLTEST.FLAT", AsOf, Flat(Bars), Warmup, BaseLookback, BaseMaxRange, medianDollarVolume: 2_000_000m);

        // True range is max(101-99, |101-100|, |99-100|) = 2 on every bar, and Wilder's
        // smoothing of a constant is that constant. atr_pct is 2/100.
        Assert.Equal(0.02f, row.AtrPct!.Value, 5);

        // Neither high nor low moves, so +DM and -DM are zero throughout, both
        // directional indicators are zero, and the index over them is zero.
        Assert.Equal(0f, row.Adx14!.Value, 5);

        // Every average equals the close, so both distances are zero.
        Assert.Equal(0f, row.Dist20Dma!.Value, 6);
        Assert.Equal(0f, row.Dist200Dma!.Value, 6);

        // The 52-week high is the adjusted high, 101, not the close. (100-101)/101.
        Assert.Equal((float) (-1.0 / 101.0), row.Dist52WHigh!.Value, 6);

        // The same value 20 bars earlier, so the change is zero.
        Assert.Equal(0f, row.Dist52WHigh20DChange!.Value, 6);

        // Volume is flat, so it sits exactly at its own average.
        Assert.Equal(1f, row.VolumeVs50DAvg!.Value, 6);

        // The 50 over 200 ratio is 1 on every bar, so its slope is zero.
        Assert.Equal(0f, row.Ma50200Slope!.Value, 6);

        // The window is a base, its range being (101-99)/99 = 2.02 percent against a
        // cap of 25, but the close does not clear the window high of 101.
        Assert.False(row.BaseBreakoutFlag!.Value);

        Assert.Equal(2_000_000m, row.MedianDollarVolume20D);
    }

    /// <summary>
    /// A series rising by exactly 1 a bar, with a 2-wide band around each close.
    ///
    /// Every bar has up = 1 and down = -1, so +DM is 1 and -DM is 0 throughout. True
    /// range is 2 every bar, so +DI smooths to 100 x 1/2 = 50 and -DI to 0, and
    /// DX = 100 x |50 - 0| / 50 = 100 at every step. Wilder's smoothing of a constant
    /// 100 is 100.
    /// </summary>
    [Fact]
    public void ASteadilyRisingSeriesHasAnIndexOfOneHundred()
    {
        var row = IndicatorEngine.Compute(
            "SRLTEST.RISE", AsOf, Rising(Bars), Warmup, BaseLookback, BaseMaxRange, medianDollarVolume: null);

        Assert.Equal(100f, row.Adx14!.Value, 4);

        // True range is 2 on every bar and the last close is 100 + 299.
        Assert.Equal((float) (2.0 / 399.0), row.AtrPct!.Value, 6);

        // Rising into its own high, so the distance closes rather than widens.
        Assert.True(row.Dist52WHigh20DChange!.Value > 0);

        // A 300-bar advance is not a base, so the flag is false however new the high.
        Assert.False(row.BaseBreakoutFlag!.Value);
    }

    /// <summary>
    /// The failure this stage is most likely to have and least likely to notice.
    ///
    /// A two for one split halves the raw close. Computed on the raw series a 200-day
    /// average spanning it reads a fifty percent decline, every screen downstream sees
    /// a name that has collapsed, and nothing errors. The provider adjusts only the
    /// close, so the high, the low and the volume are derived through the same factor.
    /// </summary>
    [Fact]
    public void ASplitInsideTheWindowDoesNotReadAsADecline()
    {
        var history = new List<IndicatorEngine.Bar>();

        for (var i = 0; i < Bars; i++)
        {
            var date = AsOf.AddDays(i - Bars + 1);

            // Raw prices halve at the split; adjusted prices do not move at all.
            var preSplit = i < Bars - 100;
            var raw = preSplit ? 200m : 100m;
            var volume = preSplit ? 500L : 1000L;

            history.Add(new IndicatorEngine.Bar(
                date, High: raw * 1.01m, Low: raw * 0.99m, Close: raw, AdjClose: 100m, Volume: volume));
        }

        var row = IndicatorEngine.Compute(
            "SRLTEST.SPLIT", AsOf, history, Warmup, BaseLookback, BaseMaxRange, medianDollarVolume: null);

        // The adjusted close never moves, so the name is exactly at its own average.
        Assert.Equal(0f, row.Dist200Dma!.Value, 6);

        // And exactly at its own adjusted high, rather than half of it.
        Assert.Equal((float) (-1.0 / 101.0), row.Dist52WHigh!.Value, 5);

        // Adjusted volume is 500 x 2 before the split and 1,000 x 1 after, so
        // participation is flat rather than doubled.
        Assert.Equal(1f, row.VolumeVs50DAvg!.Value, 5);
    }

    /// <summary>
    /// Insufficient history is null, never a shorter window. Substituting the 120 bars
    /// a name has for the 200 the column names produces a number that is not what the
    /// column says it is, on exactly the names whose history is thinnest.
    /// </summary>
    [Fact]
    public void TooLittleHistoryCarriesNullRatherThanAShorterWindow()
    {
        var row = IndicatorEngine.Compute(
            "SRLTEST.SHORT", AsOf, Flat(120), Warmup, BaseLookback, BaseMaxRange, medianDollarVolume: null);

        Assert.Null(row.Dist200Dma);
        Assert.Null(row.Dist52WHigh);
        Assert.Null(row.Dist52WHigh20DChange);
        Assert.Null(row.Ma50200Slope);

        // Twenty and fifty bars are inside 120, so these are computable and are.
        Assert.NotNull(row.Dist20Dma);
        Assert.NotNull(row.VolumeVs50DAvg);
    }

    /// <summary>
    /// The base condition is what stops the flag being permanently on.
    ///
    /// Without it a name in a steady advance sets a new window high most days, the flag
    /// carries no information, and every strong trend enters S2 twice. The rising
    /// series above is the case it excludes; this is the case it admits.
    /// </summary>
    [Fact]
    public void ABreakoutNeedsABaseBehindItAndNotJustANewHigh()
    {
        var history = new List<IndicatorEngine.Bar>();

        for (var i = 0; i < Bars; i++)
        {
            // Flat at 100 for the whole lookback, then one bar clearing the band.
            var close = i == Bars - 1 ? 130m : 100m;

            history.Add(new IndicatorEngine.Bar(
                AsOf.AddDays(i - Bars + 1),
                High: close * 1.01m, Low: close * 0.99m, Close: close, AdjClose: close, Volume: 1000));
        }

        var breakout = IndicatorEngine.Compute(
            "SRLTEST.BASE", AsOf, history, Warmup, BaseLookback, BaseMaxRange, medianDollarVolume: null);

        Assert.True(breakout.BaseBreakoutFlag!.Value);

        // The same close against a window that was trending rather than based.
        var trending = IndicatorEngine.Compute(
            "SRLTEST.TREND", AsOf, Rising(Bars), Warmup, BaseLookback, BaseMaxRange, medianDollarVolume: null);

        Assert.False(trending.BaseBreakoutFlag!.Value);
    }

    /// <summary>
    /// A gap in the window breaks the recursion rather than being smoothed over.
    /// Continuing across a hole reports a number computed over a series that does not
    /// exist, which is the same defect as a shorter window wearing the right name.
    /// </summary>
    [Fact]
    public void AGapInsideTheWilderWindowNullsTheSmoothedColumns()
    {
        var history = Flat(Bars).ToList();
        history[^40] = history[^40] with { High = null };

        var row = IndicatorEngine.Compute(
            "SRLTEST.GAP", AsOf, history, Warmup, BaseLookback, BaseMaxRange, medianDollarVolume: null);

        Assert.Null(row.AtrPct);
        Assert.Null(row.Adx14);
    }

    /// <summary>Close 100, high 101, low 99, volume 1,000, adjusted equal to raw.</summary>
    private static IReadOnlyList<IndicatorEngine.Bar> Flat(int count)
    {
        var history = new List<IndicatorEngine.Bar>(count);

        for (var i = 0; i < count; i++)
        {
            history.Add(new IndicatorEngine.Bar(
                AsOf.AddDays(i - count + 1), High: 101m, Low: 99m, Close: 100m, AdjClose: 100m, Volume: 1000));
        }

        return history;
    }

    /// <summary>Close rising by 1 a bar from 100, with a band of 1 either side.</summary>
    private static IReadOnlyList<IndicatorEngine.Bar> Rising(int count)
    {
        var history = new List<IndicatorEngine.Bar>(count);

        for (var i = 0; i < count; i++)
        {
            var close = 100m + i;

            history.Add(new IndicatorEngine.Bar(
                AsOf.AddDays(i - count + 1),
                High: close + 1m, Low: close - 1m, Close: close, AdjClose: close, Volume: 1000));
        }

        return history;
    }
}
