using System.Globalization;
using System.Text.Json;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// One reported fiscal period, as `Earnings::History` sends it [D-96].
/// </summary>
/// <param name="ReportDate">
/// When the result landed, or null. **Every read keys on this**, which is
/// `filing_date_effective`'s analogue one table over [INVARIANT 12]. A null one is
/// stored and is unreadable.
/// </param>
/// <param name="BeforeAfterMarket">
/// `BeforeMarket`, `AfterMarket` or whatever else the provider sends, verbatim.
/// Load-bearing rather than descriptive: a result released after the close of day D is
/// reacted to on D+1 and one released before the open of D is reacted to on D.
/// </param>
/// <param name="SurpriseFraction">
/// The provider's own surprise, **divided by 100 on the way in**, which is the rule
/// every ratio in this system follows [`METRICS.md`].
/// </param>
public readonly record struct EarningsPeriod(
    string Ticker,
    DateOnly PeriodEnd,
    DateOnly? ReportDate,
    string? BeforeAfterMarket,
    float? EpsActual,
    float? EpsEstimate,
    float? SurpriseFraction);

/// <summary>
/// `Earnings::History` out of the `fundamentals/{t}` payload C03 already fetches
/// [D-96].
///
/// Pure and separate from the stage, so the shape is asserted against a captured
/// payload rather than a live call, which is the same split `EodhdUrl.Build` has from
/// the client and for the same reason [A19].
/// </summary>
public static class EarningsHistory
{
    /// <summary>
    /// Every period the block carries, ordinal by `period_end`.
    ///
    /// **The key is the entry's own `date` field and not the object key**, which agree
    /// on every entry 3.1 read and are not contracted to. The object key is what the
    /// provider chose to index by; `date` is what the row is about.
    ///
    /// Sorted, because COPY order reaches the table and object enumeration order is
    /// unspecified [`CLAUDE.md` §6].
    /// </summary>
    /// <param name="root">The whole unfiltered payload, or the `Earnings` block alone.</param>
    public static IReadOnlyList<EarningsPeriod> Parse(JsonElement root, string ticker)
        => Parse(root, ticker, out _);

    /// <summary>
    /// As above, reporting how many entries were dropped as duplicates of a period end
    /// already seen.
    ///
    /// **Taking the period end from `date` gives up the uniqueness the object key had
    /// by construction, and this is where it is given back** [D-96]. Object keys are
    /// unique within a payload; `date` values are not. A restated quarter, an amended
    /// filing, or a provider indexing by report date and carrying two reports for one
    /// period all produce two entries resolving to one `(ticker, period_end)`, which is
    /// `earnings_history`'s primary key. Written unguarded, the two arrive in one
    /// statement and Postgres raises `ON CONFLICT DO UPDATE command cannot affect row a
    /// second time`: a stage failure on one ticker, mid-sweep, after the units for
    /// everything before it are spent.
    ///
    /// **The key is not the answer.** The semantic argument for `date` holds, that the
    /// key is what the provider chose to index by and `date` is what the row is about.
    /// What it needs is the guarantee added back explicitly rather than inherited.
    ///
    /// **The later report date wins, and the last seen wins where they tie or are
    /// absent.** Document order is what `JsonDocument` preserves, so "last seen" is a
    /// property of the payload rather than of a hash order that could differ between
    /// runs [`CLAUDE.md` §6].
    /// </summary>
    /// <param name="collisions">
    /// Entries dropped. Reported to the run log rather than swallowed, so a condition
    /// currently assumed rare is measured [D-96].
    /// </param>
    public static IReadOnlyList<EarningsPeriod> Parse(JsonElement root, string ticker, out int collisions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        collisions = 0;

        if (!TryHistory(root, out var history))
        {
            return [];
        }

        var byPeriod = new Dictionary<DateOnly, EarningsPeriod>();

        foreach (var entry in history.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var periodEnd = Date(entry.Value, "date") ?? Iso(entry.Name);
            if (periodEnd is null)
            {
                // Neither the entry nor its key is a date this can key on. Dropped
                // rather than guessed: a fabricated period end would collide with a
                // real one under the primary key.
                continue;
            }

            var candidate = new EarningsPeriod(
                ticker,
                periodEnd.Value,
                Date(entry.Value, "reportDate"),
                Text(entry.Value, "beforeAfterMarket"),
                Number(entry.Value, "epsActual"),
                Number(entry.Value, "epsEstimate"),
                Fraction(entry.Value, "surprisePercent"));

            if (!byPeriod.TryGetValue(periodEnd.Value, out var existing))
            {
                byPeriod[periodEnd.Value] = candidate;
                continue;
            }

            collisions++;

            if (Wins(candidate, existing))
            {
                byPeriod[periodEnd.Value] = candidate;
            }
        }

        // Sorted out of the dictionary, because a Dictionary's enumeration order is
        // unspecified and COPY order reaches the table [`CLAUDE.md` §6].
        var periods = byPeriod.Values.ToList();
        periods.Sort(static (a, b) => a.PeriodEnd.CompareTo(b.PeriodEnd));
        return periods;
    }

    /// <summary>
    /// Which of two entries for one period end is kept. The later report date, and the
    /// later in document order where they tie or are absent.
    ///
    /// A dated entry always beats an undated one, whichever came first: the undated one
    /// is unreadable by the `report_date &lt;= date` rule anyway, so keeping it would
    /// discard the only usable row of the pair.
    /// </summary>
    private static bool Wins(EarningsPeriod candidate, EarningsPeriod existing)
        => (candidate.ReportDate, existing.ReportDate) switch
        {
            (DateOnly c, DateOnly e) => c >= e,
            (not null, null) => true,
            (null, not null) => false,
            _ => true,
        };

    /// <summary>
    /// The `Earnings::History` object, whether handed the whole payload or the
    /// `Earnings` block. Absent on a ticker the provider carries no earnings for, which
    /// is an empty result rather than a fault.
    /// </summary>
    private static bool TryHistory(JsonElement root, out JsonElement history)
    {
        history = default;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var block = root.TryGetProperty("Earnings", out var earnings) ? earnings : root;

        return block.ValueKind == JsonValueKind.Object
               && block.TryGetProperty("History", out history)
               && history.ValueKind == JsonValueKind.Object;
    }

    private static DateOnly? Date(JsonElement row, string name)
        => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? Iso(v.GetString())
            : null;

    private static DateOnly? Iso(string? text)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d)
            ? d
            : null;

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// A per-share figure. Absent stays null rather than becoming zero: a zero EPS is a
    /// real value and a company reporting exactly nothing is a different fact from one
    /// the provider has no figure for [`CLAUDE.md` §6].
    /// </summary>
    private static float? Number(JsonElement row, string name)
        => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (float) v.GetDouble()
            : null;

    /// <summary>
    /// The provider's percent as a fraction. 106.3492 becomes 1.063492, which is what
    /// the column is named for.
    /// </summary>
    private static float? Fraction(JsonElement row, string name)
        => Number(row, name) is float percent ? percent / 100f : null;
}
