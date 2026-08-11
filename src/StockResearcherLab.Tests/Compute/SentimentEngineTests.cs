using StockResearcherLab.Pipeline.Compute;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// C35's reference.
///
/// **The references are closed forms rather than a second implementation.** A baseline
/// of ninety days where forty-five carry two articles and forty-five carry none has a
/// mean of exactly 1 and a population deviation of exactly 1, so every z-score below is
/// today's count less one, read off the fixture without running anything. Reproducing
/// the arithmetic in the test would assert only that two copies of it agree.
///
/// Two of these tests are the ones that fail if the asymmetry in `METRICS.md` §4.1 is
/// ever collapsed into one rule: the count zero-fills across an absent day and the
/// score does not.
/// </summary>
public sealed class SentimentEngineTests
{
    private static readonly DateOnly AsOf = new(2026, 8, 10);

    private const int MinBaselineDays = 20;

    /// <summary>
    /// The baseline is exactly specified, so the z-score is exactly specified.
    ///
    /// Forty-five days at two articles and forty-five with no row at all. Zero-filled
    /// the mean is 90/90 = 1 and every deviation is 1 either way, so the population
    /// deviation is 1. Four articles today is therefore three deviations above the
    /// ticker's own baseline.
    ///
    /// Without the zero-fill the mean would be 2 over the forty-five days that carry a
    /// row, the deviation would be zero, and the column would be null. So this pins
    /// the rule as well as the number.
    /// </summary>
    [Fact]
    public void TheZScoreIsAgainstTheTickersOwnZeroFilledBaseline()
    {
        var row = SentimentEngine.Compute(
            "SRLTEST.SENT", AsOf, AlternatingBaseline(articlesToday: 4), MinBaselineDays);

        Assert.Equal(3f, row.ArticleCountZOwn90D!.Value, 5);
    }

    /// <summary>
    /// The baseline excludes the date itself, so today's spike is measured against a
    /// history that does not contain it.
    ///
    /// The same fixture with a hundred-fold larger count today gives a z of 93 rather
    /// than a smaller one. If the date were inside the window it would move the mean
    /// by roughly one part in ninety and the deviation by far more, and the column
    /// would report a fraction of the move it exists to catch.
    /// </summary>
    [Fact]
    public void TheBaselineExcludesTheDateBeingComputed()
    {
        var row = SentimentEngine.Compute(
            "SRLTEST.SPIKE", AsOf, AlternatingBaseline(articlesToday: 94), MinBaselineDays);

        Assert.Equal(93f, row.ArticleCountZOwn90D!.Value, 4);
    }

    /// <summary>
    /// D-12, asserted rather than assumed. Two names whose own histories have the same
    /// shape and a hundred-fold different level score identically.
    ///
    /// A cross-sectional article count measures analyst coverage, which is a size
    /// proxy; against its own baseline it measures change in attention, which is the
    /// signal, and it is structurally anti-megacap. The widely covered name below
    /// would dominate any cross-sectional ranking and here it does not move at all.
    /// </summary>
    [Fact]
    public void TheZScoreIsNotCrossSectional()
    {
        var thin = SentimentEngine.Compute(
            "SRLTEST.THIN", AsOf, AlternatingBaseline(articlesToday: 4), MinBaselineDays);

        var covered = SentimentEngine.Compute(
            "SRLTEST.WIDE", AsOf, AlternatingBaseline(articlesToday: 400, scale: 100),
            MinBaselineDays);

        Assert.Equal(thin.ArticleCountZOwn90D!.Value, covered.ArticleCountZOwn90D!.Value, 4);
    }

    /// <summary>
    /// A count that never moves has no scale to express a deviation in, so the column
    /// is null rather than zero. Zero would say attention is exactly at its own
    /// baseline, which is a measurement, and this is an absence.
    /// </summary>
    [Fact]
    public void AFlatCountCarriesNoZScore()
    {
        var history = Enumerable.Range(0, SentimentEngine.BaselineDays + 1)
            .Select(i => new SentimentEngine.Day(AsOf.AddDays(-i), 3, 0.1f))
            .ToList();

        var row = SentimentEngine.Compute("SRLTEST.FLAT", AsOf, history, MinBaselineDays);

        Assert.Null(row.ArticleCountZOwn90D);

        // The tone columns are unaffected, which is what makes the null above a rule
        // about the deviation rather than an empty fixture.
        Assert.Equal(0.1f, row.Sentiment7DLevel!.Value, 6);
    }

    /// <summary>
    /// The seven-day level and the seven against thirty delta, as exact fractions.
    ///
    /// Rows on all thirty days ending the date, scoring 1 on the last seven and 0 on
    /// the twenty-three before. The level is 1, the thirty-day mean is 7/30, and the
    /// delta is 23/30.
    /// </summary>
    [Fact]
    public void TheToneWindowsReproduceTheirClosedForm()
    {
        var history = new List<SentimentEngine.Day>();

        for (var i = 0; i < SentimentEngine.BaselineDays; i++)
        {
            var date = AsOf.AddDays(-i);
            var recent = i < SentimentEngine.ShortWindowDays;

            history.Add(new SentimentEngine.Day(date, 1, recent ? 1f : 0f));
        }

        var row = SentimentEngine.Compute("SRLTEST.TONE", AsOf, history, MinBaselineDays);

        Assert.Equal(1f, row.Sentiment7DLevel!.Value, 6);
        Assert.Equal((float) (23.0 / 30.0), row.SentimentDelta7V30!.Value, 6);
    }

    /// <summary>
    /// The ordinary small-cap case, which phase P measured at 4 to 34 days of 180.
    ///
    /// Twenty-five scattered days carry a row and every one of them scores 0.5. Both
    /// tone windows are means over the rows that exist, so both are 0.5 and the delta
    /// is exactly zero.
    ///
    /// **This is the test that fails if the score is ever zero-filled with the count.**
    /// Zero-filling would put the thirty-day mean well below the seven-day one and the
    /// delta would come out positive on a name whose tone never changed, in proportion
    /// to how thinly covered it is. That is a size proxy arriving through the back
    /// door [D-12, `METRICS.md` §4.1].
    /// </summary>
    [Fact]
    public void AThinlyCoveredNameHasNoToneOnTheDaysItHasNoNews()
    {
        var history = new List<SentimentEngine.Day>();

        // Every third day, which puts rows inside both tone windows and gives thirty
        // days with a row in the baseline.
        for (var i = 0; i <= SentimentEngine.BaselineDays; i += 3)
        {
            history.Add(new SentimentEngine.Day(AsOf.AddDays(-i), 2, 0.5f));
        }

        var row = SentimentEngine.Compute("SRLTEST.THINLY", AsOf, history, MinBaselineDays);

        Assert.Equal(0.5f, row.Sentiment7DLevel!.Value, 6);
        Assert.Equal(0f, row.SentimentDelta7V30!.Value, 6);
    }

    /// <summary>
    /// Below the baseline floor every metric is null, because an absent day cannot be
    /// read as zero until the ingest has reached the ticker at all.
    ///
    /// Nineteen days inside the baseline against a floor of twenty, plus a row on the
    /// date itself, so the tone windows have something to average and are null anyway.
    /// That is the point: the floor gates all three rather than the z-score alone. The
    /// date's own row is outside the baseline, which is why it does not count toward
    /// the floor.
    /// </summary>
    [Fact]
    public void BelowTheBaselineFloorEveryMetricIsNull()
    {
        var history = Enumerable.Range(0, MinBaselineDays)
            .Select(i => new SentimentEngine.Day(AsOf.AddDays(-i), 2 + (i % 3), 0.4f))
            .ToList();

        var row = SentimentEngine.Compute("SRLTEST.NEW", AsOf, history, MinBaselineDays);

        Assert.Null(row.ArticleCountZOwn90D);
        Assert.Null(row.SentimentDelta7V30);
        Assert.Null(row.Sentiment7DLevel);
        Assert.False(row.ClearedBaselineFloor);

        // One more day inside the baseline and all three become computable, so the
        // nulls above are the floor rather than the fixture.
        history.Add(new SentimentEngine.Day(AsOf.AddDays(-MinBaselineDays), 5, 0.4f));

        var cleared = SentimentEngine.Compute("SRLTEST.NEW", AsOf, history, MinBaselineDays);

        Assert.True(cleared.ClearedBaselineFloor);
        Assert.NotNull(cleared.ArticleCountZOwn90D);
        Assert.NotNull(cleared.Sentiment7DLevel);
    }

    /// <summary>
    /// A row present with no tone is not a tone of zero. It contributes nothing to
    /// either mean, exactly as an absent day does, and where no scored row exists in a
    /// window that window is null.
    /// </summary>
    [Fact]
    public void ARowWithNoScoreContributesNothingRatherThanZero()
    {
        var history = new List<SentimentEngine.Day>();

        for (var i = 0; i < SentimentEngine.BaselineDays; i++)
        {
            // Scored only outside the seven-day window, so the level has nothing to
            // average and the delta has no short mean to difference.
            var scored = i >= SentimentEngine.ShortWindowDays;

            history.Add(new SentimentEngine.Day(AsOf.AddDays(-i), 2, scored ? 0.25f : null));
        }

        var row = SentimentEngine.Compute("SRLTEST.NOTONE", AsOf, history, MinBaselineDays);

        Assert.Null(row.Sentiment7DLevel);
        Assert.Null(row.SentimentDelta7V30);

        // The count is unaffected and is still measured, which is the asymmetry.
        Assert.True(row.ClearedBaselineFloor);
    }

    /// <summary>
    /// A row present with no count is unknown rather than zero, and it nulls the
    /// z-score. An absent day is a measurement and a present row with nothing in it is
    /// not, so the two cannot be treated alike.
    /// </summary>
    [Fact]
    public void ARowWithNoCountNullsTheZScoreRatherThanCountingAsZero()
    {
        var history = AlternatingBaseline(articlesToday: 4).ToList();
        history[^1] = history[^1] with { ArticleCount = null };

        var row = SentimentEngine.Compute("SRLTEST.NOCOUNT", AsOf, history, MinBaselineDays);

        Assert.Null(row.ArticleCountZOwn90D);
    }

    // ---------------------------------------------------------------- fixtures ---

    /// <summary>
    /// Forty-five days inside the baseline carry two articles times
    /// <paramref name="scale"/> and forty-five carry no row at all, plus the date
    /// itself. Zero-filled the mean is <paramref name="scale"/> and so is the
    /// population deviation.
    ///
    /// Every day carries the same tone, so the tone columns are constant and are not
    /// what any test using this fixture is about.
    /// </summary>
    private static IReadOnlyList<SentimentEngine.Day> AlternatingBaseline(int articlesToday, int scale = 1)
    {
        var history = new List<SentimentEngine.Day>();

        // i runs over the baseline window, which is the ninety days ending the day
        // before the date being computed.
        for (var i = 1; i <= SentimentEngine.BaselineDays; i++)
        {
            if (i % 2 == 1)
            {
                history.Add(new SentimentEngine.Day(AsOf.AddDays(-i), 2 * scale, 0.2f));
            }
        }

        history.Add(new SentimentEngine.Day(AsOf, articlesToday, 0.2f));

        return history;
    }
}
