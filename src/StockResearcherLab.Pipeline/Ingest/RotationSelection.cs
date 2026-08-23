namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// The ordering rule both per-ticker rotations use [D-95].
///
/// **What is shared is the function, never the table.** Two components writing one
/// table is two claims on one component-table-operation triple [INVARIANT 10], so C03
/// keeps `fundamental_fetch_attempt` and C05 gets `flow_fetch_attempt`. Sharing the
/// rule is what stops the two rotations drifting apart, which is exactly what happened
/// once already: C05 was written from C03's ordering by hand and then kept the defect
/// after C03 was fixed [D-91, which names C05 as not closed by it].
///
/// **Pure, so two consecutive passes can be asserted without a provider or a
/// database.** Every input arrives as data and the result is a function of it.
/// </summary>
public static class RotationSelection
{
    /// <param name="Selected">This run's tickers, in the order they will be walked.</param>
    /// <param name="PoolSize">The whole pool the selection was drawn from.</param>
    /// <param name="NeverAttempted">Pool members with no attempt row at all.</param>
    /// <param name="NewInSelection">How many of this run's selection were among them.</param>
    /// <param name="RefreshedInSelection">The rest, which have been attempted before.</param>
    /// <param name="OldestAttemptInSelection">
    /// The oldest attempt date among the refreshed ones, or null where none is.
    /// **This is the number that says the rotation is still moving**: it advances run
    /// by run once coverage completes, where a row count does not move at all.
    /// </param>
    public readonly record struct Result(
        IReadOnlyList<string> Selected,
        int PoolSize,
        int NeverAttempted,
        int NewInSelection,
        int RefreshedInSelection,
        DateOnly? OldestAttemptInSelection);

    /// <summary>
    /// Never attempted first, then oldest attempt first, then pool members that are in
    /// the universe, then ticker ordinal.
    ///
    /// **Attempted, not fetched, and the difference is the whole defect** [0006,
    /// D-91, D-95]. Asking whether a ticker has a row in the table the fetch writes
    /// makes the never-fetched group empty once coverage completes, so every
    /// subsequent run re-selects the same alphabetically-first names for ever, and a
    /// ticker whose fetch returned nothing is never distinguishable from one nobody
    /// has asked about.
    ///
    /// **Coverage before freshness while coverage is incomplete**, because a name
    /// absent from the store cannot be screened at all where a name whose figures are
    /// a few days old still can.
    ///
    /// **Universe membership is a tiebreak and not a tier.** Ranking it above
    /// freshness would starve every pool member outside the universe permanently,
    /// which is this same defect in another dress: the universe is refreshed every run
    /// and is therefore never exhausted. Among names of equal staleness a universe
    /// member goes first, which is the preference a tier was reaching for without the
    /// starvation.
    /// </summary>
    /// <param name="pool">Everything eligible this run. C03's is the candidate set; C05's is the universe.</param>
    /// <param name="attempted">
    /// Ticker to the date it was last attempted, read **strictly before** the run
    /// date by the caller. That read is what makes the rotation advance between dates
    /// rather than between runs, so a re-run of one date selects the same names
    /// [`CLAUDE.md` §6, INVARIANT 13].
    /// </param>
    /// <param name="inUniverse">The tiebreak set. Pass the pool itself where the two are the same.</param>
    /// <param name="maxPerRun">The per-run bound, which is a rate limit and never a filter [INVARIANT 1].</param>
    /// <param name="justReported">
    /// Tickers that have reported earnings inside the run's backward window, which jump
    /// the queue [D-74, `ARCHITECTURE.html` §3's C03 Reads cell].
    ///
    /// **Below never-attempted and above staleness.** A name absent from the store
    /// cannot be screened at all, so coverage still comes first; among names that have
    /// been fetched, one that has just reported is stale in a way its attempt date does
    /// not show, which is the whole reason the catalogue asks for this read.
    ///
    /// Empty for C05, whose pool has no earnings tier. Passing it anyway keeps one
    /// function rather than two.
    /// </param>
    public static Result For(
        IReadOnlyList<string> pool,
        IReadOnlyDictionary<string, DateOnly> attempted,
        IReadOnlySet<string> inUniverse,
        int maxPerRun,
        IReadOnlySet<string>? justReported = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(attempted);
        ArgumentNullException.ThrowIfNull(inUniverse);

        var reported = justReported ?? new HashSet<string>(StringComparer.Ordinal);

        var selected = pool
            .OrderBy(t => attempted.ContainsKey(t) ? 1 : 0)
            .ThenBy(t => reported.Contains(t) ? 0 : 1)
            .ThenBy(t => attempted.TryGetValue(t, out var d) ? d : DateOnly.MinValue)
            .ThenBy(t => inUniverse.Contains(t) ? 0 : 1)
            .ThenBy(t => t, StringComparer.Ordinal)
            .Take(maxPerRun)
            .ToList();

        var refreshed = selected.Where(attempted.ContainsKey).ToList();

        return new Result(
            selected,
            pool.Count,
            pool.Count(t => !attempted.ContainsKey(t)),
            selected.Count - refreshed.Count,
            refreshed.Count,
            refreshed.Count == 0 ? null : refreshed.Min(t => attempted[t]));
    }
}
