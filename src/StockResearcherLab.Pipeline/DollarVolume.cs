namespace StockResearcherLab.Pipeline;

/// <summary>
/// The 20-day median dollar volume, defined once because two components read it.
///
/// **C01 applies it weekly as one of D-4's six absolute criteria and C08 writes it
/// daily as an `indicator_daily` column.** Two definitions of one number, in two
/// components, at two cadences, is how the universe and the dossier come to disagree
/// about whether a name is liquid: C01 admits a name on its own reading of the floor
/// and the participation cap then sizes an order against a different one. So the
/// aggregate, the window and the row filter live here and both statements interpolate
/// them.
///
/// **Raw close times raw volume, and it needs no adjustment.** That product is the
/// dollars that actually changed hands on the date, and both factors are unadjusted, so
/// a split leaves it correct where it would corrupt any ratio built on prices across
/// time [`METRICS.md` §1.1].
///
/// **`percentile_cont` interpolates rather than picking a row.** Over twenty bars it
/// returns the mean of the tenth and eleventh, which is what a C# reimplementation
/// would have to reproduce exactly and is the second reason this is shared SQL rather
/// than a rule stated twice.
/// </summary>
public static class DollarVolume
{
    /// <summary>Bars in the window, counting back from the date being computed.</summary>
    public const int WindowBars = 20;

    /// <summary>
    /// The aggregate, applied to rows already narrowed to one ticker's window and
    /// grouped by ticker.
    /// </summary>
    public const string MedianExpression =
        "percentile_cont(0.5) WITHIN GROUP (ORDER BY close * volume)";

    /// <summary>
    /// Bars carrying both factors. A ticker with a gap takes its median over fewer
    /// than twenty points rather than being nulled, which is the behaviour C01 has had
    /// since 1.5 and is a deliberate exception to the rule that insufficient history is
    /// null: the alternative is that one absent bar in twenty removes a name from the
    /// universe [`METRICS.md` §2].
    /// </summary>
    public const string RowFilter = "close IS NOT NULL AND volume IS NOT NULL";
}
