namespace StockResearcherLab.Core.Stages;

/// <summary>
/// A range execution that failed and can still prove how far it got.
///
/// **The run still fails and nothing is tolerated.** This carries a position out of a
/// throw so the failed row records one, which is the difference between losing a
/// sweep and losing at most <c>concurrency</c> tickers of it.
///
/// **Why a ticker-partitioned sweep can prove a position and the old rule said it
/// could not.** The old reasoning was that <c>Parallel.ForEachAsync</c> completes out
/// of order, so the completed set is not a prefix. That is true of the completed set
/// and false of the frontier: tickers are dispatched in sorted order, so every ticker
/// strictly below the lowest one still in flight was dispatched and finished. The
/// minimum in-flight ticker is therefore a position the execution can prove, in
/// exactly the sense a halt's position is.
///
/// **A stage that fails before dispatching anything carries no position**, which is
/// the case the old rule was right about, and <see cref="Position"/> is null there.
/// </summary>
public sealed class RangeExecutionFailedException : Exception
{
    public RangeExecutionFailedException(string? position, Exception inner)
        : base(
            position is null
                ? "The range execution failed before it dispatched anything, so it has no position to " +
                  "record and the next run starts over. Every write is idempotent on its own grain [D-68]."
                : $"The range execution failed with '{position}' the lowest ticker still in flight. " +
                  "Everything strictly below it was dispatched and finished, so that is the position " +
                  "recorded and the next run re-dispatches it and everything above.",
            inner)
        => Position = position;

    /// <summary>
    /// The lowest ticker still in flight when the failure landed, or null where nothing
    /// had been dispatched. Recorded in the same form a halt's position is, so
    /// resumption reads one rule rather than two.
    /// </summary>
    public string? Position { get; }
}
