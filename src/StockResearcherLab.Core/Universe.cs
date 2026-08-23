using System.Globalization;

namespace StockResearcherLab.Core;

/// <summary>
/// "The universe on a date", stated once [D-92, 3.12].
///
/// **The most recent `security_daily` row at or before the date, and never the newest row
/// outright.** That is the join D-92 specifies and the reason the table exists: a
/// membership read that takes today's row ranks a 2021 date inside its 2026 cell, and a
/// percentile computed over a slightly wrong cell is not inspectable afterwards, because
/// nothing downstream can see the cell it was computed over.
///
/// **Stated here because five components ask the same question.** Five copies of one
/// as-of pick is the shape this phase has found every silent hole in: one copy gets a
/// bound or a fix the others do not, which is what happened to the two pool statements at
/// D-102 and to `BackfillPool`'s read at 3.8. **3.17 is the demonstration.** The statement
/// was replaced whole for a form that reads thousands of index rows instead of hundreds of
/// thousands, and because it is one statement every reader took the change and no reader
/// took it alone. A second variant for the two stages that measured slow would have been
/// the first copy.
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
    ///
    /// **A loose index scan over the ticker column, and the reason is the same one D-102
    /// records for the calendar.** `DISTINCT ON (ticker) ... WHERE date &lt;= D` reads
    /// every `security_daily` row at or before D and dedups them, which is 771,145 rows
    /// at the end of this phase's window and grows with where D sits in it, so a stage
    /// issuing it once per date pays more for each date than for the one before. This
    /// walks the distinct tickers instead and takes one lookup each: 4,290 probes rather
    /// than a sort of three quarters of a million rows. Measured warm at the range's
    /// start, middle and end: 130, 288 and 464 ms the old way against 38, 52 and 65 ms
    /// this way, identical member sets at all three [3.17].
    ///
    /// **This was the smaller half of what made C10 take 391.8 minutes**, and it is
    /// stated so the figure is not read as belonging here: about 8 minutes of it. The
    /// larger half was the sector composite reading every bar its members ever had, and
    /// that is fixed at its own call site.
    ///
    /// **Exactly `DISTINCT ON` by construction rather than by argument**, which is why
    /// this form and not the cheaper one. Reading the single evaluation date at or before
    /// D is 1 ms rather than 65, and it is equivalent only while C01 writes every member
    /// on every evaluation date: it borrows a property of a different component, and the
    /// fixture that proves a departed name stays departed would have to be rewritten to
    /// match the writer before it would pass. The lookup per ticker borrows nothing.
    /// </summary>
    public static string AsOf(string dateLiteral) => $"""
        (
            WITH RECURSIVE tickers AS (
                SELECT min(ticker) AS ticker FROM security_daily WHERE date <= {dateLiteral}
                UNION ALL
                SELECT (SELECT min(s.ticker) FROM security_daily s
                        WHERE s.ticker > t.ticker AND s.date <= {dateLiteral})
                FROM tickers t WHERE t.ticker IS NOT NULL
            )
            SELECT r.ticker, r.sector, r.size_bucket, r.market_cap, r.is_active
            FROM tickers t
            CROSS JOIN LATERAL (
                SELECT s.ticker, s.sector, s.size_bucket, s.market_cap, s.is_active
                FROM security_daily s
                WHERE s.ticker = t.ticker AND s.date <= {dateLiteral}
                ORDER BY s.date DESC
                LIMIT 1
            ) r
            WHERE t.ticker IS NOT NULL
        )
        """;

    /// <summary>Active members as of the date, ticker only, ordered. A whole statement.</summary>
    public static string MembersAsOf(DateOnly asOf)
        => $"SELECT m.ticker FROM {AsOf(asOf)} m WHERE m.is_active ORDER BY m.ticker;";

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
