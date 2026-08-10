using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// C06. The calendar of dated corporate events: earnings, splits and dividend
/// ex-dates, narrowed to the universe and written at ticker by event.
///
/// **Three sources, and the cheap form of each is not the same form** [1.8,
/// measured]. Earnings are forward-looking, so the call is a date range and the
/// answer covers the whole market at weight 1. Splits and dividends have no forward
/// bulk, so the nightly call is the bulk feed for the run date at weight 100 each.
/// Three calls a night, 201 units, against 4,000 for the per-ticker form over a
/// 2,000-name universe.
///
/// The backfill reverses that and is phase 3's, not built here: <c>splits/{t}</c>
/// and <c>div/{t}</c> return full history at weight 1, so five years costs about
/// 4,000 units across the universe where re-running the nightly bulk over 1,260
/// sessions would cost 252,000. Same split as C02's, and for the same reason
/// [ARCHITECTURE section 19].
/// </summary>
public sealed class EventsIngestor : IStage
{
    public static readonly string[] EventColumns =
        ["ticker", "event_type", "event_date", "announced_date"];

    /// <summary>
    /// <c>announced_date</c> is not a key part: it is the attribute most likely to
    /// be revised after the fact, and a revision should update the row rather than
    /// insert a second event [0002].
    /// </summary>
    private static readonly string[] EventKey = ["ticker", "event_type", "event_date"];

    /// <summary>
    /// The three values <c>event_type</c> takes. The type says which date
    /// <c>event_date</c> is, which matters because a dividend carries four dates in
    /// the payload and exactly one of them reaches the column.
    /// </summary>
    public const string Earnings = "earnings";
    public const string Split = "split";
    public const string DividendEx = "dividend_ex";

    private readonly EodhdClient _client;

    public EventsIngestor(EodhdClient client) => _client = client;

    public string Name => "EventsIngestor";

    public IReadOnlyList<string> ReadSet { get; } = ["security"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("events", WriteOperation.Insert, EventColumns)];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var forward = (int) await LongAsync(context, "events.earnings_forward_days", ct).ConfigureAwait(false);
        var backward = (int) await LongAsync(context, "events.earnings_backward_days", ct).ConfigureAwait(false);

        var universe = await UniverseAsync(context, ct).ConfigureAwait(false);
        if (universe.Count == 0)
        {
            // A stage completes or it fails the run, and an events pass over an
            // empty universe is the universe stage not having run [CLAUDE.md
            // section 6].
            throw new InvalidOperationException(
                "security holds no active row, so there is no universe to narrow events to.");
        }

        var rows = new List<EventRow>();
        rows.AddRange(await EarningsAsync(context, universe, backward, forward, ct).ConfigureAwait(false));
        rows.AddRange(await BulkAsync(context, universe, "splits", ct).ConfigureAwait(false));
        rows.AddRange(await BulkAsync(context, universe, "dividends", ct).ConfigureAwait(false));

        if (rows.Count == 0)
        {
            return new StageResult(0, "ok", "No event in the universe on this date or in the earnings window.");
        }

        // Ordered by the key, so COPY order is stable across runs [CLAUDE.md
        // section 6]. Distinct on the key too: the provider can list one earnings
        // date twice across a range boundary, and two staging rows with the same
        // key make the upsert's own ON CONFLICT arbitrary.
        var ordered = rows
            .GroupBy(r => (r.Ticker, r.EventType, r.EventDate))
            .Select(g => g.First())
            .OrderBy(r => r.Ticker, StringComparer.Ordinal)
            .ThenBy(r => r.EventType, StringComparer.Ordinal)
            .ThenBy(r => r.EventDate)
            .ToList();

        var written = await context.Data.BulkUpsertAsync(
            "events", EventColumns, EventKey,
            async (w, c) =>
            {
                foreach (var r in ordered)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(r.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(r.EventType, c).ConfigureAwait(false);
                    await w.WriteAsync(r.EventDate, c).ConfigureAwait(false);
                    await w.WriteAsync(r.AnnouncedDate, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);

        var counts = ordered
            .GroupBy(r => r.EventType)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => string.Format(CultureInfo.InvariantCulture, "{0} {1:N0}", g.Key, g.Count()));

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} event row(s) over a {1:N0} name universe: {2}. Earnings window {3} to {4}; " +
            "splits and dividend ex-dates for {5:yyyy-MM-dd} only, the bulk feed having no forward form",
            written, universe.Count, string.Join(", ", counts),
            context.Date.AddDays(-backward).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            context.Date.AddDays(forward).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            context.Date);

        return new StageResult(written, "ok", detail);
    }

    private static async Task<HashSet<string>> UniverseAsync(StageContext context, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "security", "SELECT ticker FROM security WHERE is_active;", ct).ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The earnings calendar, one call for a date range across the whole market.
    ///
    /// It is global rather than US, returning <c>.BSE</c> and every other exchange
    /// in the same array, so the universe narrowing is what makes it a US feed
    /// rather than a <c>?exchange=</c> parameter.
    ///
    /// The window reaches backwards as well as forwards because two readers want
    /// different halves of it: C12's earnings blackout needs the next report date,
    /// and C03's rotation lets a name that has just reported jump the queue.
    /// </summary>
    private async Task<IReadOnlyList<EventRow>> EarningsAsync(
        StageContext context, HashSet<string> universe, int backward, int forward, CancellationToken ct)
    {
        using var doc = await _client.GetAsync(
            "calendar/earnings",
            [
                ("from", context.Date.AddDays(-backward).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("to", context.Date.AddDays(forward).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ],
            ct).ConfigureAwait(false);

        return ParseEarnings(doc.RootElement, universe);
    }

    public static IReadOnlyList<EventRow> ParseEarnings(JsonElement root, HashSet<string> universe)
    {
        var rows = new List<EventRow>();

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("earnings", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var ticker = Text(e, "code");

            // report_date is the date the company reports; `date` beside it is the
            // fiscal period end and is not this event.
            var reportDate = Date(e, "report_date");

            if (ticker is null || reportDate is null || !universe.Contains(ticker))
            {
                continue;
            }

            // The payload carries no date on which the schedule became public, so
            // announced_date is null and null means unknown rather than
            // simultaneous [CLAUDE.md section 6].
            rows.Add(new EventRow(ticker, Earnings, reportDate.Value, null));
        }

        return rows;
    }

    /// <summary>
    /// Splits or dividends for one date, market-wide, from the bulk feed.
    ///
    /// <paramref name="type"/> is the provider's own parameter value. A weekend or
    /// holiday returns an empty array rather than an error, same as the price feed
    /// [1.9], so an empty day is not a failure.
    /// </summary>
    private async Task<IReadOnlyList<EventRow>> BulkAsync(
        StageContext context, HashSet<string> universe, string type, CancellationToken ct)
    {
        using var doc = await _client.GetAsync(
            "eod-bulk-last-day/US",
            [
                ("type", type),
                ("date", context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ],
            ct).ConfigureAwait(false);

        return ParseBulk(doc.RootElement, universe, type);
    }

    public static IReadOnlyList<EventRow> ParseBulk(JsonElement root, HashSet<string> universe, string type)
    {
        var rows = new List<EventRow>();

        if (root.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        var eventType = type == "splits" ? Split : DividendEx;

        foreach (var e in arrayOf(root))
        {
            // The bulk feeds send code and exchange separately where the calendar
            // sends one qualified symbol, so the ticker is assembled here rather
            // than read.
            var code = Text(e, "code");
            var exchange = Text(e, "exchange");
            var date = Date(e, "date");

            if (code is null || exchange is null || date is null)
            {
                continue;
            }

            var ticker = code + "." + exchange;
            if (!universe.Contains(ticker))
            {
                continue;
            }

            // A dividend's declarationDate is the date the ex-date became public,
            // which is what announced_date is for. A split's payload carries no
            // such date, so it stays null.
            var announced = eventType == DividendEx ? Date(e, "declarationDate") : null;

            rows.Add(new EventRow(ticker, eventType, date.Value, announced));
        }

        return rows;

        static IEnumerable<JsonElement> arrayOf(JsonElement a)
            => a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object);
    }

    /// <param name="EventDate">
    /// The date the event happens: the report date for earnings, the effective date
    /// for a split, the ex-date for a dividend.
    /// </param>
    /// <param name="AnnouncedDate">
    /// The date it became public where the provider says so, null where it does not.
    /// Populated for dividends only.
    /// </param>
    public readonly record struct EventRow(
        string Ticker, string EventType, DateOnly EventDate, DateOnly? AnnouncedDate);

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && !string.IsNullOrEmpty(v.GetString())
            ? v.GetString()
            : null;

    private static DateOnly? Date(JsonElement e, string name)
    {
        var s = Text(e, name);

        if (s is null)
        {
            return null;
        }

        return s.Length >= 10
               && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private static async Task<long> LongAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);
        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a whole number.");
    }
}
