namespace StockResearcherLab.Pipeline;

/// <summary>
/// The price series this system reads as comparisons rather than selects from [D-104].
///
/// **A reference series is fetched into `price_daily` and admitted to nothing.** It is
/// never written to <c>security</c> or <c>security_daily</c>, so it is a member of no
/// universe on any date, is never a candidate, is never ranked inside a cell and cannot
/// become a position. D-2 puts ETFs out of scope as candidates, which is a statement about
/// what can be selected; this is a statement about what can be read.
///
/// **Stated here because the pool and the readers have to agree, and for four months they
/// did not.** C02's backfill pool is every admitted common stock and the benchmark is an
/// ETF, so the sweep never asked for it: `SPY.US` held 265 bars against a five and a half
/// year window and no attempt row at all, and 2,690,981 of 4,143,273 `indicator_daily`
/// rows carried null relative strength because of it. Nothing errored. The list and the
/// pool being one statement is what stops a component depending on a series no sweep
/// fetches [D-104].
///
/// **A code constant rather than a config key, deliberately.** Config resolves as of the
/// simulated date [INVARIANT 13], so a benchmark held as a key resolves per date and a
/// change to it leaves one relative-strength column computed against two different series
/// with nothing in the row saying which. Changing a benchmark splits history into halves
/// that cannot be pooled [`CLAUDE.md` §12], and that is a code change carrying a decision.
/// </summary>
public static class ReferenceSeries
{
    /// <summary>
    /// The market benchmark. Read by C08 for the four relative-strength columns and by C10
    /// for the regime label's sign test, and by nothing else [D-104].
    /// </summary>
    public const string Benchmark = "SPY.US";

    /// <summary>
    /// Every reference series, ordinal, which C02's pool is unioned with.
    ///
    /// Adding one is a string here and no other change: the pool picks it up, the sweep
    /// fetches its whole history at one unit, and the component that wants it names it.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Benchmark];
}
