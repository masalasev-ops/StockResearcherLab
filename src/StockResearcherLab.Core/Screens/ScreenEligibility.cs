namespace StockResearcherLab.Core.Screens;

/// <summary>
/// A weighted mean over a metric list that is not the screen's ranking score, composed
/// for one screen out of that screen's own configuration [D-120].
///
/// **The arithmetic is D-112's, applied one level across rather than one level up.** A
/// composite is the same weighted mean over the same percentile store that a score is,
/// so a name below <see cref="MinInputs"/> present inputs has no composite rather than a
/// low one, and a screen gated on a composite it cannot compute surfaces nothing for
/// that name. That is the null rule reaching the gate as well as the score.
/// </summary>
/// <param name="Metrics">The ranked metrics this composite is the mean of.</param>
/// <param name="MinInputs">Below this many present inputs the composite is unknown.</param>
public sealed record ScreenComposite(IReadOnlyList<ScreenMetric> Metrics, int MinInputs);

/// <summary>
/// A screen's eligibility gate: the two composites and the three stabilisation
/// conditions that decide whether a name is ranked by this screen at all
/// [`ARCHITECTURE.html` §05, D-120].
///
/// **A screen has one of these because its configuration carries the keys, not because
/// code knows its id.** S5 is the only screen with a gate today, and it is gated by
/// having <c>quality_metrics</c> in config rather than by being called S5 anywhere. A
/// second gated screen is a set of config rows [`CLAUDE.md` §5].
///
/// **The composites are S5's own lists and duplicate S1's content on purpose.** §05
/// states the gate as "S1 top quintile AND technical bottom quintile", which read
/// literally is one screen reading another. Removing the duplication is what INVARIANT 2
/// forbids in the invariant's own words, and what holds it is the per-screen facade
/// these lists are loaded through [D-120, D-6].
///
/// **The two news conditions fail open below the article threshold and the price
/// condition does not.** Thinly covered names are exactly what this screen's small slots
/// exist to find, and failing closed on them would delete them and reintroduce the
/// megacap tilt through the arithmetic [§05].
/// </summary>
/// <param name="Quality">The composite a name must sit at or above <paramref name="QualityMin"/> on.</param>
/// <param name="QualityMin">Top quintile on the 0 to 100 scale, so 80 [`METRICS.md` §6.3].</param>
/// <param name="Technical">The composite a name must sit at or below <paramref name="TechnicalMax"/> on.</param>
/// <param name="TechnicalMax">Bottom quintile on the same scale, so 20.</param>
/// <param name="StabilisationZMax">
/// The article-count z-score at or above which the news cycle is still raging and the
/// burnout condition fails.
/// </param>
/// <param name="SentimentDeltaMin">
/// The 7-day against 30-day sentiment difference below which fresh bad news is still
/// arriving.
/// </param>
/// <param name="NewsGateMinArticles">
/// The seven-day article count below which both news conditions are treated as
/// satisfied. This is the fail-open threshold and it is the one rule here that must not
/// be tidied into a fail-closed one.
/// </param>
public sealed record ScreenEligibility(
    ScreenComposite Quality,
    double QualityMin,
    ScreenComposite Technical,
    double TechnicalMax,
    double StabilisationZMax,
    double SentimentDeltaMin,
    int NewsGateMinArticles);
