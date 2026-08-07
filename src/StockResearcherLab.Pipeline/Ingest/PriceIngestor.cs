using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// C02. One bulk call per date into <c>price_daily</c>, over a trailing window
/// rather than tonight alone.
///
/// **Why a window and not one night** [A10, A26]. A session accretes for hours and
/// sometimes into the following evening: 2026-08-04 was read at 44,665, 44,686 and
/// 44,708 across one evening and finished at 50,228. A date loaded once, on the
/// night it was newest, would keep whatever partial count it had for ever. Reloading
/// a trailing window is what actually heals that, and D-68's idempotent upsert is
/// what makes reloading safe.
///
/// **Partial dates are loaded deliberately.** This stage does not decide what is
/// usable; C07 does, and it needs the short date present in <c>price_daily</c> to
/// measure it against the trailing median [D-70]. A stage that filtered here would
/// hide the evidence the guard runs on.
///
/// **Dates come from the run date, never from a database clock** [A2]. There is no
/// trading calendar until C07 exists, so the window is calendar dates and a
/// non-session returns an empty array that is tolerated rather than treated as a
/// fault. Weekends and holidays simply contribute nothing.
/// </summary>
public sealed class PriceIngestor : IStage
{
    /// <summary>The columns this stage writes, declared so the write is checked against them [A27].</summary>
    public static readonly string[] Columns =
        ["ticker", "date", "open", "high", "low", "close", "adj_close", "volume"];

    private static readonly string[] ConflictTarget = ["ticker", "date"];

    private readonly EodhdClient _client;

    public PriceIngestor(EodhdClient client) => _client = client;

    public string Name => "PriceIngestor";

    /// <summary>
    /// Nothing. The bulk feed is the source and it is not a table, so the declared
    /// read set is empty and the guard has nothing to permit.
    /// </summary>
    public IReadOnlyList<string> ReadSet { get; } = [];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("price_daily", WriteOperation.Insert, Columns)];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var window = await ReadWindowAsync(context, ct).ConfigureAwait(false);

        long written = 0;

        // Newest first, counting back from the run date inclusive. The run date's
        // own session may be in progress and is still loaded, for the reason in the
        // class comment.
        for (var i = 0; i < window; i++)
        {
            var date = context.Date.AddDays(-i);
            written += await LoadDateAsync(context, date, ct).ConfigureAwait(false);
        }

        return new StageResult(written);
    }

    private static async Task<int> ReadWindowAsync(StageContext context, CancellationToken ct)
    {
        var row = await context.Config
            .RequireAsync("price.reload_window_days", context.Date, ct).ConfigureAwait(false);

        if (!int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var window)
            || window < 1)
        {
            throw new InvalidOperationException(
                $"price.reload_window_days resolved to '{row.Value}', which is not a positive whole " +
                "number of days. It is coupled to freshness.settled_window_days and lowering it is " +
                "not a local change [A26].");
        }

        return window;
    }

    private async Task<long> LoadDateAsync(StageContext context, DateOnly date, CancellationToken ct)
    {
        var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var doc = await _client.GetAsync(
            "eod-bulk-last-day/US", [("date", iso)], ct).ConfigureAwait(false);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"The bulk end-of-day feed returned {doc.RootElement.ValueKind} for {iso} rather than " +
                "an array. A shape change is a fault rather than an empty session.");
        }

        var bars = Parse(doc.RootElement, date);

        // A non-session returns an empty array rather than an error, so this is the
        // ordinary case for a weekend or a holiday and not a fault [A26].
        if (bars.Count == 0)
        {
            return 0;
        }

        return await context.Data.BulkUpsertAsync(
            "price_daily", Columns, ConflictTarget,
            async (writer, c) =>
            {
                foreach (var bar in bars)
                {
                    await writer.StartRowAsync(c).ConfigureAwait(false);
                    await writer.WriteAsync(bar.Ticker, c).ConfigureAwait(false);
                    await writer.WriteAsync(bar.Date, c).ConfigureAwait(false);
                    await writer.WriteAsync(bar.Open, c).ConfigureAwait(false);
                    await writer.WriteAsync(bar.High, c).ConfigureAwait(false);
                    await writer.WriteAsync(bar.Low, c).ConfigureAwait(false);
                    await writer.WriteAsync(bar.Close, c).ConfigureAwait(false);
                    await writer.WriteAsync(bar.AdjClose, c).ConfigureAwait(false);
                    await writer.WriteAsync(bar.Volume, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The feed's rows as bars, sorted by ticker.
    ///
    /// Sorted explicitly because COPY order reaches the table and two runs of one
    /// stage over one date must produce byte-identical output [CLAUDE.md section 6].
    /// The feed's own order is not specified anywhere.
    ///
    /// **The date on the row is used, not the date requested.** They agree today,
    /// and a feed that returned a different day for a requested one would otherwise
    /// write rows under the wrong date silently.
    /// </summary>
    private static List<Bar> Parse(JsonElement rows, DateOnly requested)
    {
        var bars = new List<Bar>(rows.GetArrayLength());

        foreach (var row in rows.EnumerateArray())
        {
            var code = Text(row, "code");
            var exchange = Text(row, "exchange_short_name");

            if (code is null || exchange is null)
            {
                throw new InvalidOperationException(
                    "A bulk end-of-day row carries no code or no exchange_short_name, so it cannot " +
                    "be keyed. The ticker is the two joined, as the rest of this system spells it.");
            }

            var date = Date(row, "date")
                ?? throw new InvalidOperationException("A bulk end-of-day row carries no date.");

            if (date != requested)
            {
                throw new InvalidOperationException(
                    $"The bulk feed returned a row dated {date:yyyy-MM-dd} for a request for " +
                    $"{requested:yyyy-MM-dd}. Writing it would file bars under a day they did not " +
                    "belong to, which no later stage could detect.");
            }

            bars.Add(new Bar(
                code + "." + exchange,
                date,
                Money(row, "open"),
                Money(row, "high"),
                Money(row, "low"),
                Money(row, "close"),
                Money(row, "adjusted_close"),
                Volume(row)));
        }

        // Ordinal, so the order does not depend on a locale.
        bars.Sort(static (a, b) => string.CompareOrdinal(a.Ticker, b.Ticker));
        return bars;
    }

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateOnly? Date(JsonElement row, string name)
        => row.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
           && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var d)
            ? d
            : null;

    /// <summary>
    /// A price. Decimal, never a float or a double, in any monetary path
    /// [INVARIANT 16]. Absent stays null rather than becoming zero: a zero price is
    /// a real value and would pass every screen that reads it [CLAUDE.md section 6].
    /// </summary>
    private static decimal? Money(JsonElement row, string name)
        => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDecimal()
            : null;

    private static long? Volume(JsonElement row)
    {
        if (!row.TryGetProperty("volume", out var v) || v.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        // The feed sends volume as a JSON number that is sometimes fractional on
        // thinly traded names, and the column is bigint.
        return v.TryGetInt64(out var exact) ? exact : (long) v.GetDecimal();
    }

    private readonly record struct Bar(
        string Ticker, DateOnly Date,
        decimal? Open, decimal? High, decimal? Low, decimal? Close, decimal? AdjClose, long? Volume);
}
