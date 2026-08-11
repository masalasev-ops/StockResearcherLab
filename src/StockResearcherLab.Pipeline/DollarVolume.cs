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
    ///
    /// **Both casts are explicit and the inner one is where the money leaves numeric.**
    /// `percentile_cont` is defined on `double precision` and on `interval` and on
    /// nothing else, so `close * volume`, which is `numeric`, was being cast to a double
    /// by Postgres and the aggregate was returning one. Written out, a reader sees the
    /// one place in a monetary path where a double is unavoidable rather than finding it
    /// by its consequences [INVARIANT 16]. The outer cast puts the answer back into the
    /// type the column and the floor are both declared in.
    ///
    /// **It was implicit until 2.10 and that was a defect rather than a nicety.** C01
    /// compares the result inside SQL and never sees its type; C08 reads it back and
    /// cast it to `decimal?`, which threw `InvalidCastException` on the first real run
    /// and could not have been found any other way, because every test to that point
    /// passed the value into `Compute` rather than through this expression.
    ///
    /// A double carries about sixteen significant digits and a dollar volume reaches ten,
    /// so the interpolation is exact to far more places than a liquidity floor reads.
    /// `percentile_disc` would stay in `numeric` and would pick a row instead of
    /// interpolating, which is a different number and would move a universe C01 has been
    /// building on this definition since 1.5.
    /// </summary>
    public const string MedianExpression =
        "(percentile_cont(0.5) WITHIN GROUP (ORDER BY (close * volume)::double precision))::numeric";

    /// <summary>
    /// Bars carrying both factors. A ticker with a gap takes its median over fewer
    /// than twenty points rather than being nulled, which is the behaviour C01 has had
    /// since 1.5 and is a deliberate exception to the rule that insufficient history is
    /// null: the alternative is that one absent bar in twenty removes a name from the
    /// universe [`METRICS.md` §2].
    /// </summary>
    public const string RowFilter = "close IS NOT NULL AND volume IS NOT NULL";
}
