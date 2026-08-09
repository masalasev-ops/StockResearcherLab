using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// C05. Two source tables at their own natural grain, which the compute layer
/// derives <c>flow_daily</c> from [D-61].
///
/// **The two halves do not promise the same thing** [D-69, 1.9]. Form 4 pages
/// properly and is fully backfillable: <c>meta.total</c> matched the filings index
/// on every ticker checked. <c>Holders::Institutions</c> is a top-20 snapshot at one
/// or two report dates with no 13f endpoint behind it, so
/// <c>inst_ownership_change</c> has no history to compute over and accumulates
/// forward only. The table still ingests, because a current top-20 holder list is a
/// usable static feature; it is the change metric that has no series.
///
/// **Never the legacy `insider-transactions` endpoint.** It returned zero over 90
/// days for all seven probe names including the control, and market-wide it is stale
/// by about three months and carries US Congress member trades, which are not Form 4
/// insider filings.
/// </summary>
public sealed class FlowIngestor : IStage
{
    public static readonly string[] InsiderColumns =
    [
        "ticker", "accession_number", "transaction_side", "transaction_ordinal",
        "filed_at", "transaction_date", "reporting_owner_cik", "reporting_owner_name",
        "transaction_code", "security_title", "shares_amount", "price_per_share",
        "total_value", "shares_owned_after", "acquired_or_disposed",
    ];

    public static readonly string[] HoldingColumns =
    ["ticker", "report_date", "holder_name", "shares", "change", "change_pct"];

    private static readonly string[] InsiderKey =
        ["ticker", "accession_number", "transaction_side", "transaction_ordinal"];

    private static readonly string[] HoldingKey = ["ticker", "report_date", "holder_name"];

    private readonly EodhdClient _client;

    public FlowIngestor(EodhdClient client) => _client = client;

    public string Name => "FlowIngestor";

    public IReadOnlyList<string> ReadSet { get; } = ["security"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new TableWrite("insider_transaction", WriteOperation.Insert, InsiderColumns),
        new TableWrite("institutional_holding", WriteOperation.Insert, HoldingColumns),
    ];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var maxPerRun = (int) await LongAsync(context, "flow.max_tickers_per_run", ct).ConfigureAwait(false);
        var pageSize = (int) await LongAsync(context, "flow.form4_page_size", ct).ConfigureAwait(false);

        var tickers = await UniverseAsync(context, maxPerRun, ct).ConfigureAwait(false);

        long insiderRows = 0;
        long holdingRows = 0;

        // Per affected ticker, because a count alone cannot say whether the missing
        // rows can reach a trailing window [D-71]. Sorted before rendering, since
        // the tickers are walked in a fixed order but the list still reaches output.
        var shortfalls = new List<Shortfall>();

        foreach (var ticker in tickers)
        {
            insiderRows += await LoadInsiderAsync(context, ticker, pageSize, shortfalls, ct).ConfigureAwait(false);
            holdingRows += await LoadHoldersAsync(context, ticker, ct).ConfigureAwait(false);
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} insider transaction row(s) and {1:N0} institutional holding row(s) over {2:N0} " +
            "ticker(s). Holdings are a top-20 snapshot rather than a series, so inst_ownership_change " +
            "accumulates forward only [D-69]. {3}",
            insiderRows, holdingRows, tickers.Count, DescribeShortfalls(shortfalls));

        return new StageResult(insiderRows + holdingRows, "ok", detail);
    }

    /// <param name="Position">
    /// `interior` means at least one page before the last came back short, so the
    /// missing rows sit inside the history and a trailing-90-day metric can be
    /// affected. `final` means they sit at the oldest end, outside every trailing
    /// window [D-71].
    /// </param>
    public readonly record struct Shortfall(string Ticker, int Rows, ShortfallPosition Position);

    /// <summary>
    /// The two counts D-71 requires in the run log, and the per-ticker positions
    /// behind them.
    ///
    /// **Uncapped**, unlike the wide-filer list C03 renders. The decision asks for
    /// the position per affected ticker and a truncated list answers it for a
    /// sample, which is the difference between a record and an impression. At the
    /// measured rate of 43 tickers in 250 a full universe pass renders a few
    /// hundred entries into one text column, which is the evidence file rather than
    /// a summary of it.
    /// </summary>
    public static string DescribeShortfalls(IReadOnlyList<Shortfall> shortfalls)
    {
        if (shortfalls.Count == 0)
        {
            return "No ticker under-delivered against meta.total [D-71]";
        }

        var ordered = shortfalls
            .OrderBy(x => x.Ticker, StringComparer.Ordinal)
            .ToList();

        var interior = ordered.Count(x => x.Position is ShortfallPosition.Interior or ShortfallPosition.Both);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} ticker(s) under-delivered against meta.total, {1:N0} row(s) short in total, of which " +
            "{2:N0} ticker(s) are short inside the history where a trailing window can reach them and " +
            "{3:N0} only at the oldest end [D-71]: {4}",
            ordered.Count,
            ordered.Sum(x => x.Rows),
            interior,
            ordered.Count - interior,
            string.Join(", ", ordered.Select(x => string.Format(
                CultureInfo.InvariantCulture, "{0} {1} {2}",
                x.Ticker, x.Rows, x.Position.ToString().ToLowerInvariant()))));
    }

    private static async Task<IReadOnlyList<string>> UniverseAsync(
        StageContext context, int maxPerRun, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "security",
            "SELECT ticker FROM security WHERE is_active ORDER BY ticker LIMIT " +
            maxPerRun.ToString(CultureInfo.InvariantCulture) + ";",
            ct).ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToList();
    }

    /// <summary>
    /// Form 4, walked to the end on <c>page[offset]</c> and <c>page[limit]</c>.
    ///
    /// `limit` and `offset` are accepted and silently ignored by this endpoint, so a
    /// call using them returns one page and looks complete [1.9].
    ///
    /// The client compares the collected count against <c>meta.total</c> and the two
    /// outcomes are not the same failure [D-71]. Stopping while a next link is still
    /// offered fails the stage, because what was missed is unknown and asking again
    /// would fix it. Running the server out of pages and still coming up short is
    /// the provider disagreeing with itself, and is recorded here rather than
    /// halting the night: at the measured rate a universe pass would never complete.
    /// </summary>
    private async Task<long> LoadInsiderAsync(
        StageContext context, string ticker, int pageSize, List<Shortfall> shortfalls,
        CancellationToken ct)
    {
        PagedRead read;
        try
        {
            read = await _client.GetAllPagesAsync(
                "sec-filings/" + ticker + "/form4", [], pageSize, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // A ticker with no filings index is the ordinary case for a recent
            // listing. Not a reason to fail the night.
            return 0;
        }

        // The server ran out of pages with rows still unaccounted for. Recorded and
        // continued, because asking again cannot produce them and halting means a
        // universe pass never completes [D-71]. The client throws instead where the
        // loop stopped while a next link was still on offer.
        if (read.Shortfall > 0)
        {
            shortfalls.Add(new Shortfall(ticker, read.Shortfall, read.Position));
        }

        var rows = ParseFilings(ticker, read.Rows);
        if (rows.Count == 0)
        {
            return 0;
        }

        return await context.Data.BulkUpsertAsync(
            "insider_transaction", InsiderColumns, InsiderKey,
            async (w, c) =>
            {
                foreach (var r in rows)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(r.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Accession, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Side, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Ordinal, c).ConfigureAwait(false);
                    await w.WriteAsync(r.FiledAt, c).ConfigureAwait(false);
                    await w.WriteAsync(r.TransactionDate, c).ConfigureAwait(false);
                    await w.WriteAsync(r.OwnerCik, c).ConfigureAwait(false);
                    await w.WriteAsync(r.OwnerName, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Code, c).ConfigureAwait(false);
                    await w.WriteAsync(r.SecurityTitle, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Shares, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Price, c).ConfigureAwait(false);
                    await w.WriteAsync(r.TotalValue, c).ConfigureAwait(false);
                    await w.WriteAsync(r.SharesOwnedAfter, c).ConfigureAwait(false);
                    await w.WriteAsync(r.AcquiredOrDisposed, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
    }

    /// <param name="Ordinal">
    /// Position within its array, as the provider ordered it. **This is a key part
    /// and not a detail** [D-68 reopened at 1.7]: over 1,069 real transactions no
    /// combination of a transaction's own attributes was unique, because two line
    /// items in one filing can be identical on every value the provider sends.
    /// </param>
    public readonly record struct InsiderRow(
        string Ticker, string Accession, string Side, int Ordinal,
        DateOnly? FiledAt, DateOnly? TransactionDate, string? OwnerCik, string? OwnerName,
        string? Code, string? SecurityTitle, decimal? Shares, decimal? Price,
        decimal? TotalValue, decimal? SharesOwnedAfter, string? AcquiredOrDisposed);

    public static IReadOnlyList<InsiderRow> ParseFilings(string ticker, IReadOnlyList<JsonElement> filings)
    {
        var rows = new List<InsiderRow>();

        foreach (var filing in filings)
        {
            if (filing.ValueKind != JsonValueKind.Object
                || !filing.TryGetProperty("accession_number", out var acc)
                || acc.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var accession = acc.GetString()!;
            var filedAt = Date(filing, "filed_at");

            // Both arrays, numbered independently, because a Form 4 reports
            // non-derivative and derivative holdings separately.
            foreach (var side in new[] { "non_derivative", "derivative" })
            {
                if (!filing.TryGetProperty(side, out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var ordinal = 0;
                foreach (var tx in arr.EnumerateArray())
                {
                    if (tx.ValueKind != JsonValueKind.Object)
                    {
                        ordinal++;
                        continue;
                    }

                    rows.Add(new InsiderRow(
                        ticker, accession, side, ordinal++,
                        filedAt,
                        Date(tx, "transaction_date"),
                        Text(tx, "reporting_owner_cik"),
                        Text(tx, "reporting_owner_name"),
                        Text(tx, "transaction_code"),
                        Text(tx, "security_title"),
                        Money(tx, "shares_amount"),
                        Money(tx, "price_per_share"),
                        Money(tx, "total_value"),
                        Money(tx, "shares_owned_after"),
                        Text(tx, "acquired_or_disposed")));
                }
            }
        }

        // Ordinal by the key, so COPY order is stable across runs.
        rows.Sort(static (a, b) =>
        {
            var t = string.CompareOrdinal(a.Accession, b.Accession);
            if (t != 0) return t;
            t = string.CompareOrdinal(a.Side, b.Side);
            return t != 0 ? t : a.Ordinal.CompareTo(b.Ordinal);
        });

        return rows;
    }

    /// <summary>
    /// The top-20 institutional holders, which is a snapshot and not a series
    /// [D-69]. Ingested because a current holder list is a usable static feature.
    /// </summary>
    private async Task<long> LoadHoldersAsync(StageContext context, string ticker, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await _client.GetAsync(
                "fundamentals/" + ticker,
                [("filter", "Holders::Institutions")],
                ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return 0;
        }

        using (doc)
        {
            var rows = ParseHolders(ticker, doc.RootElement);
            if (rows.Count == 0)
            {
                return 0;
            }

            return await context.Data.BulkUpsertAsync(
                "institutional_holding", HoldingColumns, HoldingKey,
                async (w, c) =>
                {
                    foreach (var h in rows)
                    {
                        await w.StartRowAsync(c).ConfigureAwait(false);
                        await w.WriteAsync(h.Ticker, c).ConfigureAwait(false);
                        await w.WriteAsync(h.ReportDate, c).ConfigureAwait(false);
                        await w.WriteAsync(h.HolderName, c).ConfigureAwait(false);
                        await w.WriteAsync(h.Shares, c).ConfigureAwait(false);
                        await w.WriteAsync(h.Change, c).ConfigureAwait(false);
                        await w.WriteAsync(h.ChangePct, c).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);
        }
    }

    public readonly record struct HoldingRow(
        string Ticker, DateOnly ReportDate, string HolderName,
        decimal? Shares, decimal? Change, float? ChangePct);

    public static IReadOnlyList<HoldingRow> ParseHolders(string ticker, JsonElement root)
    {
        var rows = new List<HoldingRow>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            return rows;
        }

        // Keyed "0", "1", "2" rather than an array, which is why a caller expecting
        // an array reads nothing rather than failing [1.9].
        foreach (var entry in root.EnumerateObject())
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

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && !string.IsNullOrEmpty(v.GetString())
            ? v.GetString()
            : null;

    /// <summary>
    /// The provider sends transaction dates as ISO instants and report dates as
    /// plain dates, so both forms are accepted. A trading date is a label rather
    /// than a timezone conversion, so the date part is taken as written.
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

    /// <summary>Money and share counts as decimal [INVARIANT 16]. Absent stays null: zero shares is a real value.</summary>
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

    private static async Task<long> LongAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);
        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a whole number.");
    }
}
