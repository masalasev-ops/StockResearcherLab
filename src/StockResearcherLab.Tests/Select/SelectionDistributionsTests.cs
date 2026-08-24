using StockResearcherLab.Core.Screens;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// The six done-when lines are six, and each is the line `BUILD_PLAN.md` states [4.14].
///
/// **A definition of done that can quietly lose a line is not one.** The measure runs
/// against a populated store this suite does not have, so what is asserted here is its
/// shape: that six lines come back, that each names the plan's own wording, and that the
/// size target is D-89's proportion rather than a number typed twice.
///
/// The figures themselves are in `PROGRESS.md` and are reproduced by re-running
/// `distributions` over the same range, which is what makes them traceable rather than
/// asserted [D-67].
/// </summary>
[Collection("database")]
public sealed class SelectionDistributionsTests
{
    /// <summary>
    /// The plan's six lines, in the order the measure yields them.
    ///
    /// Stated here so that a line removed from the measure fails rather than shortening
    /// the report, which is the failure this class exists for.
    /// </summary>
    private static readonly string[] Lines =
    [
        "a night yields roughly 26 to 30 candidates",
        "the 2/3/3 size distribution holds",
        "megacap share sits under a third, including inside the 2022 drawdown",
        "distinct tickers over any 60-day window exceed 250",
        "overlap between screens falls somewhere near 10 to 20 percent",
        "attribution rows exist for every candidate with the config version stamped",
    ];

    [Fact]
    public async Task TheMeasureYieldsTheSixLinesThePlanStates()
    {
        var ct = TestContext.Current.CancellationToken;

        // An empty range in the test database. Every query returns a row of nulls or
        // zeroes, which is enough to assert the shape and is deliberately not enough to
        // assert a figure: the figures belong to the populated store.
        var measured = await SelectionDistributions.MeasureAsync(
            TestDatabase.ConnectionString,
            new DateOnly(2021, 2, 1), new DateOnly(2021, 2, 5), ct).ConfigureAwait(true);

        Assert.Equal(Lines, measured.Select(m => m.Line));

        // Every line reports something, so a query that returned nothing cannot pass as a
        // line that held.
        Assert.All(measured, m => Assert.False(string.IsNullOrWhiteSpace(m.Measured)));
    }

    /// <summary>
    /// **The size target is D-89's proportion at eight slots and is derived, not typed.**
    /// D-116 retired `screens.quota_large`, `quota_mid` and `quota_small` precisely
    /// because three numbers that must sum to the slot count can be set so they do not,
    /// and a target restated here would be that defect returning in a test.
    /// </summary>
    [Fact]
    public void TheSizeTargetIsTheProportionAtEightSlots()
    {
        var quota = SlotQuota.For(8);

        Assert.Equal(2, quota.Large);
        Assert.Equal(3, quota.Mid);
        Assert.Equal(3, quota.Small);

        var target = SelectionDistributions.SizeTarget.ToDictionary(
            x => x.Bucket, x => x.Target, StringComparer.Ordinal);

        Assert.Equal(quota.Large / 8d, target["large"]);
        Assert.Equal(quota.Mid / 8d, target["mid"]);
        Assert.Equal(quota.Small / 8d, target["small"]);

        Assert.Equal(1d, target.Values.Sum(), 10);
    }

    /// <summary>
    /// **The drawdown segment reads `regime_label` and not a date window.** A window
    /// chosen by the build session is a window that can be chosen to suit the answer, and
    /// `market_context_daily.regime_label` is authored and closed to three values by
    /// migration `0005` [D-80].
    /// </summary>
    [Fact]
    public void TheDrawdownSegmentReadsTheRegimeLabel()
    {
        var sql = SelectionDistributions.SqlFor(
            "megacap share sits under a third, including inside the 2022 drawdown",
            new DateOnly(2021, 1, 11), new DateOnly(2026, 8, 12));

        Assert.Contains("regime_label = 'risk_off'", sql, StringComparison.Ordinal);
        Assert.Contains("market_context_daily", sql, StringComparison.Ordinal);

        // And no literal year, which is what an invented window would look like.
        Assert.DoesNotContain("2022-", sql, StringComparison.Ordinal);
    }
}
