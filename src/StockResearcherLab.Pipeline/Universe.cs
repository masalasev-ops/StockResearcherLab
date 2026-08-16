using System.Globalization;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// "The universe on a date", stated once [D-92, 3.12].
///
/// **The most recent `security_daily` row at or before the date, and never the newest row
/// outright.** That is the join D-92 specifies and the reason the table exists: a
/// membership read that takes today's row ranks a 2021 date inside its 2026 cell, and a
/// percentile computed over a slightly wrong cell is not inspectable afterwards, because
/// nothing downstream can see the cell it was computed over.
///
/// **Stated here because five components ask the same question.** Five copies of a
/// `DISTINCT ON ... ORDER BY date DESC` is the shape this phase has found every silent
/// hole in: one copy gets a bound or a fix the others do not, which is what happened to
/// the two pool statements at D-102 and to `BackfillPool`'s read at 3.8.
///
/// **The caller still declares `security_daily` in its own read set**, which is what puts
/// the read in its §3 Reads cell rather than hiding it behind a helper [D-101]. This
/// supplies the statement; it does not supply the declaration.
/// </summary>
public static class Universe
{
    /// <summary>
    /// A subquery yielding one row per ticker: the row in force on
    /// <paramref name="asOf"/>, whether or not it says the ticker is a member.
    ///
    /// **`is_active` is deliberately not filtered here.** Filtering inside the pick would
    /// take the most recent row that happens to be active, which resurrects a name whose
    /// latest row is the departure that ended its membership. The caller filters after
    /// the row is chosen, and C11 does not filter at all because its join is a LEFT JOIN
    /// on purpose.
    /// </summary>
    public static string AsOf(DateOnly asOf) => AsOf($"DATE '{Iso(asOf)}'");

    /// <summary>
    /// The same, for a caller that already holds the date as a SQL literal. C11 builds
    /// its statement from one and would otherwise have to parse it back to a
    /// <see cref="DateOnly"/> only to format it again.
    /// </summary>
    public static string AsOf(string dateLiteral) => $"""
        (
            SELECT DISTINCT ON (ticker) ticker, sector, size_bucket, market_cap, is_active
            FROM security_daily
            WHERE date <= {dateLiteral}
            ORDER BY ticker, date DESC
        )
        """;

    /// <summary>Active members as of the date, ticker only, ordered. A whole statement.</summary>
    public static string MembersAsOf(DateOnly asOf)
        => $"SELECT m.ticker FROM {AsOf(asOf)} m WHERE m.is_active ORDER BY m.ticker;";

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
