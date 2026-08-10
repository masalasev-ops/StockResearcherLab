using System.Globalization;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>Why a candidate date was rejected, or that it was not.</summary>
public enum FreshnessOutcome
{
    /// <summary>Passed all three. This is the date the rest of the run uses.</summary>
    Usable,

    /// <summary>The newest date is older than the most recent completed session. Aborts [D-65].</summary>
    Stale,

    /// <summary>Below the abort floor. A truncated file rather than a filling one. Aborts [D-59].</summary>
    Truncated,

    /// <summary>Still filling. Not an abort: the walk-back tries the next date [D-70].</summary>
    Unsettled,

    /// <summary>Nothing in `price_daily` at all.</summary>
    NoData,
}

/// <param name="Date">The candidate.</param>
/// <param name="Rows">Its row count.</param>
/// <param name="Outcome">What it was.</param>
/// <param name="Note">One line for the run log, naming the numbers rather than summarising them.</param>
public sealed record FreshnessStep(DateOnly Date, long Rows, FreshnessOutcome Outcome, string Note);

/// <param name="UsableDate">The date the rest of the run uses, or null when the run must abort.</param>
/// <param name="Alert">True when the usable date is inside the alert band but above the abort floor.</param>
/// <param name="SettlednessSkipped">
/// True when there was not a full window of prior dates to take a median over, so
/// settledness was not evaluated. Recorded rather than silent: the guard is degraded
/// during bootstrap, not absent, and the run log says which [A26].
/// </param>
/// <param name="Steps">Every candidate considered, newest first.</param>
public sealed record FreshnessVerdict(
    DateOnly? UsableDate,
    bool Alert,
    bool SettlednessSkipped,
    IReadOnlyList<FreshnessStep> Steps)
{
    public string Summary()
    {
        var head = UsableDate is DateOnly d
            ? "usable " + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "no usable date";

        if (Alert)
        {
            head += ", inside the alert band";
        }

        if (SettlednessSkipped)
        {
            head += ", settledness skipped for want of history";
        }

        return head + ". " + string.Join("; ", Steps.Select(s => s.Note));
    }
}
