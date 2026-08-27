using System.Globalization;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// Thrown when no date passes all three checks. Aborts the run: a night with no
/// usable price date produces no orders rather than orders on bars nobody can vouch
/// for [D-65, RUNBOOK].
/// </summary>
public sealed class FreshnessAbortException : InvalidOperationException
{
    public FreshnessAbortException(FreshnessVerdict verdict)
        : base("The freshness guard found no usable trading date, so the run stops here and produces " +
               "no orders. " + verdict.Summary())
        => Verdict = verdict;

    public FreshnessVerdict Verdict { get; }
}

/// <summary>
/// C07. Recency, completeness and settledness, which are three different things
/// [D-65, D-70].
///
/// **Writes nothing.** It emits through the run log via C27 rather than writing it,
/// so <c>run_log</c> keeps one writer [INVARIANT 10]. It also has no route to
/// <c>alert</c>, which has ConcentrationMonitor as its only writer, so the alert
/// band is a run-log status and not an alert row [A3].
///
/// **Makes exactly one provider call**, for the exchange calendar. It does not
/// re-fetch prices: D-70 dropped that, because a stage is a pure function of its
/// date and config version and a guard whose verdict depends on what it saw during
/// a previous wall-clock run is not.
/// </summary>
public sealed class FreshnessGuard : IStage
{
    private readonly EodhdClient _client;

    public FreshnessGuard(EodhdClient client) => _client = client;

    public string Name => "FreshnessGuard";

    public IReadOnlyList<string> ReadSet { get; } = ["price_daily", "run_log"];

    /// <summary>Nothing. See the class comment.</summary>
    public IReadOnlyList<TableWrite> WriteSet { get; } = [];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var verdict = await EvaluateAsync(context, ct).ConfigureAwait(false);

        if (verdict.UsableDate is null)
        {
            throw new FreshnessAbortException(verdict);
        }

        // Zero rows written, which is legitimate for a stage that declares no
        // writes. The verdict reaches the run log through the runner rather than
        // through this stage writing anything, so run_log keeps one writer and
        // alert keeps ConcentrationMonitor [INVARIANT 10, A3].
        //
        // A band or a skipped settledness check is recorded with a status an
        // operator can distinguish from a clean night. Recording it as "ok" would
        // make the alert indistinguishable from its absence, which is the whole
        // failure the band exists to prevent.
        // The usable date travels back with the result. This is the one stage that
        // returns one, because it is the one that decides which stored date the
        // rest of the night works on.
        return verdict.Alert || verdict.SettlednessSkipped
            ? StageResult.Alert(0, verdict.Summary()) with { TradingDate = verdict.UsableDate }
            : StageResult.None with { TradingDate = verdict.UsableDate };
    }

    /// <summary>
    /// The verdict, without aborting on it. 1.14's sequence calls this so it can
    /// carry the usable date into every later stage, and <see cref="ExecuteAsync"/>
    /// calls it too rather than the two computing it separately.
    /// </summary>
    public async Task<FreshnessVerdict> EvaluateAsync(StageContext context, CancellationToken ct = default)
    {
        var abortBelow = await LongAsync(context, "freshness.row_count_abort_below", ct).ConfigureAwait(false);
        var alertBelow = await LongAsync(context, "freshness.row_count_alert_below", ct).ConfigureAwait(false);
        var window = (int) await LongAsync(context, "freshness.settled_window_days", ct).ConfigureAwait(false);
        var fraction = await DecimalAsync(context, "freshness.settled_fraction", ct).ConfigureAwait(false);

        var calendar = await ExchangeCalendar.FetchAsync(_client, ct).ConfigureAwait(false);
        var session = calendar.MostRecentCompletedSession(context.Date);

        var counts = await CountsAsync(context, window, ct).ConfigureAwait(false);

        return FreshnessRule.Evaluate(counts, session, abortBelow, alertBelow, fraction, window);
    }

    /// <summary>
    /// Row counts by date, newest first.
    ///
    /// One read rather than a query per candidate, so the walk-back and the median
    /// are computation over a single snapshot rather than a sequence of reads that
    /// could disagree with each other.
    /// </summary>
    private static async Task<IReadOnlyList<(DateOnly Date, long Rows)>> CountsAsync(
        StageContext context, int window, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "price_daily", CountsSql(context.Date, window), ct).ConfigureAwait(false);

        var counts = new List<(DateOnly, long)>(rows.Count);
        foreach (var row in rows)
        {
            counts.Add((DateOnly.FromDateTime((DateTime) row[0]!), (long) row[1]!));
        }

        return counts;
    }

    /// <summary>
    /// The read this guard makes, as a function of the date it was asked about.
    ///
    /// **The bound on `date` is what makes this stage a pure function of its date**
    /// [`CLAUDE.md` section 5, D-70]. Without it the guard reads whatever the table's
    /// newest rows happen to be at the moment it runs, which broke in two ways at once
    /// and neither announced itself [R.1]:
    ///
    /// A run for a past date aborted on a session it was never asked about, so no
    /// night could be replayed once a later partial date existed. And the evening
    /// sequence ingested the current session as its first step and then aborted on the
    /// rows it had just written, which is a halt a run causes itself and cannot clear
    /// by being run again.
    ///
    /// **Public so it can be asserted directly.** The rule this feeds is a pure
    /// function and is tested as one; the read was the untested half, and the untested
    /// half is the half that was wrong.
    /// </summary>
    /// <param name="asOf">The stage's date. Rows after it are not this run's to see.</param>
    /// <param name="window">`freshness.settled_window_days`, which sets how far back to read.</param>
    public static string CountsSql(DateOnly asOf, int window)
    {
        // Enough for a full window behind every candidate the walk-back could reach.
        // Both interpolations are values this method was handed rather than input: an
        // int it computed, and a date rendered invariantly [CLAUDE.md section 6].
        // IStageData's read route takes no parameters.
        var limit = (window * 2) + 40;

        return "SELECT date, count(*) FROM price_daily WHERE date <= DATE '" +
            asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
            "' GROUP BY date ORDER BY date DESC LIMIT " +
            limit.ToString(CultureInfo.InvariantCulture) + ";";
    }

    private static async Task<long> LongAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);

        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException(
                $"{key} resolved to '{row.Value}', which is not a whole number.");
    }

    private static async Task<decimal> DecimalAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);

        return decimal.TryParse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException(
                $"{key} resolved to '{row.Value}', which is not a number.");
    }
}
