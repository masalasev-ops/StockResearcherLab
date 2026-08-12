using System.Globalization;
using System.Text.Json;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// One institutional holder's position, as <c>Holders::Institutions</c> sends it
/// [D-61].
/// </summary>
/// <param name="ReportDate">
/// The period the position is stated as of, which is the source's own grain. It is a
/// period end rather than a filing date, and C34 is where the difference is handled.
/// </param>
/// <param name="ChangePct">
/// The provider's own percentage change in the share count, verbatim.
/// <c>institutional_holding.change_pct</c> is declared as not money in `SCHEMA.md`
/// [INVARIANT 16].
/// </param>
public readonly record struct HoldingRow(
    string Ticker, DateOnly ReportDate, string HolderName,
    decimal? Shares, decimal? Change, float? ChangePct);

/// <summary>
/// <c>Holders::Institutions</c> out of the <c>fundamentals/{t}</c> payload C03 already
/// fetches [D-98].
///
/// **The block was bought twice.** C05 called the same endpoint with
/// <c>filter=Holders::Institutions</c>, which is a projection of the document C03 has
/// received unfiltered since 3.7, at 10 units a ticker. That the two agree is measured
/// rather than inferred: twenty entries both ways on CCS.US and NVDA.US, agreeing row
/// for row [`docs/evidence/phase-3/holders-filtered-vs-unfiltered-20260812.txt`].
///
/// **It is a top-20 snapshot rather than a series** [D-69], measured false at 1.9.
/// CCS.US and NVDA.US return twenty entries at a single report date and BXC.US twenty
/// across two, there is no 13f endpoint behind it, and the filings index lists no such
/// form. So <c>inst_ownership_change</c> has nothing to compute a change over and
/// accumulates forward only. The table still ingests, because a current top-20 holder
/// list is a usable static feature; it is the change metric that has no series. That
/// is also why no sweep re-fetches it: a universe pass would buy one current snapshot
/// 2,841 times where the nightly rotation covers the pool in days [D-98].
///
/// Pure and separate from the stage, so the shape is asserted against a captured
/// payload rather than a live call, which is the same split <see cref="EarningsHistory"/>
/// has and for the same reason.
/// </summary>
public static class InstitutionalHolders
{
    public static readonly string[] Columns =
        ["ticker", "report_date", "holder_name", "shares", "change", "change_pct"];

    /// <summary>The grain, and so the upsert's conflict target.</summary>
    public static readonly string[] Key = ["ticker", "report_date", "holder_name"];

    /// <summary>
    /// Every holder the block carries, ordinal by report date then holder name.
    ///
    /// Sorted, because COPY order reaches the table and object enumeration order is
    /// unspecified [`CLAUDE.md` §6].
    /// </summary>
    /// <param name="root">
    /// The whole unfiltered payload, the <c>Holders</c> block alone, or the entries
    /// object the filtered call returns at its root. Three shapes rather than two,
    /// because the captured evidence is the middle one: the filtered response is the
    /// entries object itself [1.9, endpoint sweep], the unfiltered payload nests it
    /// under <c>Holders</c>, and what was captured off the unfiltered call is the block
    /// between them.
    /// </param>
    public static IReadOnlyList<HoldingRow> Parse(JsonElement root, string ticker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        var rows = new List<HoldingRow>();

        if (!TryEntries(root, out var entries))
        {
            return rows;
        }

        // Keyed "0", "1", "2" rather than an array, which is why a caller expecting an
        // array reads nothing rather than failing [1.9].
        foreach (var entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Text(entry.Value, "name");
            var date = Date(entry.Value, "date");

            // Both are key parts, so a row missing either cannot be written at the
            // declared grain and is dropped rather than given a placeholder.
            if (name is null || date is null)
            {
                continue;
            }

            rows.Add(new HoldingRow(
                ticker, date.Value, name,
                Money(entry.Value, "currentShares"),
                Money(entry.Value, "change"),
                Pct(entry.Value, "change_p")));
        }

        rows.Sort(static (a, b) =>
        {
            var t = a.ReportDate.CompareTo(b.ReportDate);
            return t != 0 ? t : string.CompareOrdinal(a.HolderName, b.HolderName);
        });

        return rows;
    }

    /// <summary>
    /// The entries object, whichever of the three shapes it arrived in. Absent on a
    /// ticker the provider carries no holders for, which is an empty result rather
    /// than a fault.
    ///
    /// **The `ValueKind` guards are not defensive padding.** The fundamentals endpoint
    /// answers a ticker it carries nothing for with a bare JSON string, and
    /// `TryGetProperty` throws on anything that is not an object rather than returning
    /// false.
    ///
    /// Peeling is unambiguous because an entry key is a position, so an entries object
    /// never carries a member called <c>Holders</c> or <c>Institutions</c>.
    /// </summary>
    private static bool TryEntries(JsonElement root, out JsonElement entries)
    {
        entries = default;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var block = root.TryGetProperty("Holders", out var holders) ? holders : root;

        if (block.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        entries = block.TryGetProperty("Institutions", out var institutions) ? institutions : block;

        return entries.ValueKind == JsonValueKind.Object;
    }

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && !string.IsNullOrEmpty(v.GetString())
            ? v.GetString()
            : null;

    /// <summary>
    /// Report dates arrive as plain dates. An ISO instant is accepted too, because
    /// this parse came from the component that reads both forms and narrowing it would
    /// be a change rather than a move. A trading date is a label rather than a timezone
    /// conversion, so the date part is taken as written.
    /// </summary>
    private static DateOnly? Date(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var s = v.GetString();
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        return s.Length >= 10
               && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)
            ? iso
            : null;
    }

    /// <summary>Share counts as decimal [INVARIANT 16]. Absent stays null: zero shares is a real value.</summary>
    private static decimal? Money(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(
                v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null,
            _ => null,
        };
    }

    private static float? Pct(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetSingle(out var f)
            ? f
            : null;
}
