using System.Globalization;
using System.Net;
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
public sealed class PriceIngestor : IBackfillStage
{
    /// <summary>The columns this stage writes, declared so the write is checked against them [A27].</summary>
    public static readonly string[] Columns =
        ["ticker", "date", "open", "high", "low", "close", "adj_close", "volume"];

    private static readonly string[] ConflictTarget = ["ticker", "date"];

    /// <summary>
    /// The attempt record [0010]. Written for every ticker a sweep dispatched, whether
    /// or not the fetch yielded bars, which is what makes an absent row mean never
    /// attempted rather than never yielded.
    /// </summary>
    public static readonly string[] AttemptColumns =
        ["ticker", "last_attempted_date", "last_yield_date", "rows_last_attempt"];

    private static readonly string[] AttemptConflictTarget = ["ticker"];

    private readonly EodhdClient _client;

    public PriceIngestor(EodhdClient client) => _client = client;

    public string Name => "PriceIngestor";

    /// <summary>
    /// Nothing. The bulk feed is the source and it is not a table, so the declared
    /// read set is empty and the guard has nothing to permit.
    ///
    /// `price_fetch_attempt` is not here and belongs in neither list twice. A stage may
    /// read what it writes, which is what `DeclaredAccess.CanRead` says: the sweep
    /// reads its own attempt record back to decide what is left to dispatch [0010].
    /// </summary>
    public IReadOnlyList<string> ReadSet { get; } = [];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [
            new TableWrite("price_daily", WriteOperation.Insert, Columns),
            new TableWrite("price_fetch_attempt", WriteOperation.Insert, AttemptColumns),
        ];

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

    // ------------------------------------------------------- range mode [3.6] ---

    /// <summary>
    /// The whole history of every admitted common stock, live and delisted, one call
    /// per ticker [D-93, §19].
    ///
    /// **The bulk feed is not used here and the reversal is the point.** Bulk by date
    /// buys every name for one day at 100 units; `eod/{t}` buys one name for every day
    /// at 1. Five years over 50,785 names is 50,785 units this way and about 126,000
    /// the other, for the same rows, which is roughly sixty times more for using either
    /// endpoint for the other's job [`PROGRESS.md`, endpoint weights].
    ///
    /// **The pool is every admitted common stock and not the universe** [D-2, D-4,
    /// INVARIANT 1]. Price, liquidity, market cap and history are per-date criteria and
    /// pre-applying them would delete from history exactly the names that later failed.
    /// Instrument type is the one criterion that is not per-date and `SymbolList`
    /// already applies it, which is the same universe filter applied sooner rather than
    /// a second one.
    ///
    /// **Plus the reference series, which are read and never selected** [D-104]. They are
    /// unioned into the pool because a component that measures against a series needs it
    /// fetched, and they reach no other table, so D-2 and INVARIANT 1 are untouched: what
    /// they govern is what can be selected, and a reference series cannot.
    ///
    /// **Depth is whatever the call returns.** One unit buys five years or twenty, so
    /// the load depth is a disk decision rather than a unit one, and deep history
    /// cannot be re-fetched cheaply once it is the deep past [D-94]. The range bounds
    /// what compute runs over, not what is loaded.
    ///
    /// **Config resolves as of the range end, and that is not the case D-93 governs.**
    /// This sweep is ticker-partitioned: there is no date being computed, and no
    /// configured value reaches a row it writes, the bars being the provider's own. The
    /// keys it reads are operational, the concurrency and the gate's three figures. A
    /// date-partitioned stage resolves per date and 3.13 onward is where that matters.
    /// </summary>
    public async Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = await context.ForDateAsync(context.To, ct).ConfigureAwait(false);

        var concurrency = (int) await LongAsync(settings, "backfill.ticker_concurrency", ct).ConfigureAwait(false);
        var weight = await LongAsync(settings, "backfill.weight_eod", ct).ConfigureAwait(false);
        var reserve = await LongAsync(settings, "backfill.unit_reserve", ct).ConfigureAwait(false);
        var allowance = await LongAsync(settings, "backfill.daily_unit_allowance", ct).ConfigureAwait(false);

        var pool = await PoolAsync(ct).ConfigureAwait(false);

        // **Resumption is a set difference against the attempt record** [0010]. Every
        // attempt in one sweep is stamped with the range start, so the remaining set is
        // the pool minus the tickers already carrying one at that date. A sweep that
        // halted on the gate, one whose upsert timed out and one whose process was
        // killed all resume identically, because none of them is asked what it did:
        // the rows say.
        //
        // **The range start rather than the range end**, because a sweep spans days and
        // `to` defaults to today. Keying on the end would make a sweep re-invoked the
        // next morning a different sweep with an empty attempt set, which is the whole
        // pool again.
        var already = await AttemptedAsync(settings, context.From, ct).ConfigureAwait(false);
        var remaining = pool.Where(t => !already.Contains(t)).ToList();

        long written = 0;
        var loaded = 0;
        var halted = false;
        string? haltDetail = null;

        // Ordinal order, in chunks of the configured concurrency. **The gate is asked
        // once per chunk, not once per ticker** [3.6]. A refusal stops the sweep at a
        // chunk boundary, so the tickers it did not reach are exactly the ones carrying
        // no attempt row, which is what the next run dispatches.
        //
        // **Per ticker doubled the request count for nothing.** `/api/user` costs no
        // units and does cost a request, so a gate read per ticker put 50,785 of them
        // beside 50,785 `eod/{t}` calls, and against the provider's 1,000-a-minute
        // limiter that takes the sweep's floor from about 51 minutes to about 102. The
        // property the per-ticker read protected is unchanged: up to `concurrency`
        // units are spent past one reading, which the reserve absorbs many times over
        // at eight units against fifty thousand. The gate is a projection and the
        // reading is the verdict [3.4].
        foreach (var chunk in Chunks(remaining, concurrency))
        {
            var decision = await context.NextUnitAsync(weight, reserve, allowance, ct).ConfigureAwait(false);

            List<string> dispatch;

            if (decision.Fits)
            {
                dispatch = [.. chunk];
            }
            else
            {
                dispatch = [];

                // A flag rather than the detail's nullness. The halt is a fact about
                // the gate and the line is a description of it, so reading the second
                // for the first makes an empty string a completed sweep.
                halted = true;
                haltDetail = decision.Detail;
            }

            if (dispatch.Count > 0)
            {
                // Each worker writes through its own binary COPY stream, which
                // BulkUpsertAsync gives it by opening its own connection per call
                // [§19]. Rows inside a stream are sorted by date; across streams the
                // order cannot reach the table, every write being keyed on
                // (ticker, date) [D-68, CLAUDE.md section 6].
                var counts = new long[dispatch.Count];

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, dispatch.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                    async (i, token) =>
                        counts[i] = await LoadSeriesAsync(settings, ticker: dispatch[i], token)
                            .ConfigureAwait(false))
                    .ConfigureAwait(false);

                // **Committed per chunk, which is what bounds a hard kill's cost**
                // [0010]. The attempt rows land after the bars they describe, so a
                // process killed between the two re-fetches this chunk and writes the
                // same bars again rather than skipping them: the failure that costs
                // work is the safe one and the failure that skips work cannot happen.
                // A chunk whose fetch throws records no attempts at all, so at most
                // `concurrency` tickers are re-dispatched.
                await RecordAttemptsAsync(settings, dispatch, counts, context.From, ct)
                    .ConfigureAwait(false);

                written += counts.Sum();
                loaded += dispatch.Count;
            }

            if (halted)
            {
                break;
            }
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} bar(s) over {1:N0} of {2:N0} admitted common stock(s), live and delisted, plus " +
            "{4:N0} reference series [D-104]. {3:N0} " +
            "carried an attempt for this range already and were not dispatched [0010].",
            written, loaded, pool.Count, pool.Count - remaining.Count, ReferenceSeries.All.Count);

        // **An empty remaining set is `covered` rather than `ok` with a zero** [3.16].
        // The distinction is only knowable here, where both the pool and the remaining
        // set are in hand, and the sequence driver's zero-row halt rests on it.
        if (halted)
        {
            return BackfillResult.Halted(written, context.To, detail + " " + haltDetail);
        }

        return remaining.Count == 0
            ? BackfillResult.Covered(context.To, detail)
            : BackfillResult.Completed(written, context.To, detail);
    }

    /// <summary>
    /// The tickers already carrying an attempt at this range's start, which are the
    /// ones this run does not dispatch [0010].
    ///
    /// The whole set is read once rather than a row per ticker, the pool being fifty
    /// thousand names and the table one row each.
    /// </summary>
    private static async Task<HashSet<string>> AttemptedAsync(
        StageContext context, DateOnly rangeStart, CancellationToken ct)
    {
        var asOf = rangeStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var rows = await context.Data.ReadAsync(
            "price_fetch_attempt",
            $"""
             SELECT ticker
             FROM price_fetch_attempt
             WHERE last_attempted_date = DATE '{asOf}'
             ORDER BY ticker;
             """,
            ct).ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// One attempt row per dispatched ticker, upserted on ticker so a re-run over the
    /// same range writes what the first run wrote [D-68].
    ///
    /// `last_yield_date` carries the range start where the ticker returned bars and
    /// null where it returned none, which is the distinction an absent row cannot make:
    /// absent means never attempted, null means attempted and empty [0010,
    /// `CLAUDE.md` §6]. A ticker the price endpoint answers `404` for is therefore
    /// attempted once and never re-fetched, where a presence test would re-ask it for
    /// ever.
    ///
    /// Sorted before the copy, because COPY order reaches the table and an unsorted
    /// enumeration is not a deterministic output [`CLAUDE.md` §6].
    /// </summary>
    private static async Task RecordAttemptsAsync(
        StageContext context, IReadOnlyList<string> dispatched, long[] counts,
        DateOnly rangeStart, CancellationToken ct)
    {
        var attempts = new List<Attempt>(dispatched.Count);

        for (var i = 0; i < dispatched.Count; i++)
        {
            attempts.Add(new Attempt(
                dispatched[i], rangeStart, counts[i] > 0 ? rangeStart : null, counts[i]));
        }

        attempts.Sort((a, b) => string.CompareOrdinal(a.Ticker, b.Ticker));

        await context.Data.BulkUpsertAsync(
            "price_fetch_attempt", AttemptColumns, AttemptConflictTarget,
            async (w, c) =>
            {
                foreach (var a in attempts)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(a.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(a.AttemptedOn, c).ConfigureAwait(false);
                    await w.WriteAsync(a.LastYield, c).ConfigureAwait(false);
                    await w.WriteAsync(a.Rows, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
    }

    private sealed record Attempt(
        string Ticker, DateOnly AttemptedOn, DateOnly? LastYield, long Rows);

    /// <summary>
    /// Every admitted common stock, live and delisted, plus every reference series,
    /// ordinal.
    ///
    /// Two calls at one unit each. The two lists are disjoint, measured at 3.1, so this
    /// is a union rather than a superset taken from one of them.
    ///
    /// **The reference series are unioned in and are not an admission** [D-104]. They go
    /// no further than `price_daily`: C01 writes `security` and `security_daily` from the
    /// symbol list and never from this pool, so a series here is fetched and is a member
    /// of nothing. **This is the line that was missing.** C08 and C10 both read `SPY.US`
    /// and nothing had ever fetched it, because the pool was admitted common stock and the
    /// benchmark is an ETF, and the cost was 2,690,981 indicator rows with null relative
    /// strength and a regime column with no `risk_off` in it. Nothing errored.
    /// </summary>
    public async Task<IReadOnlyList<string>> PoolAsync(CancellationToken ct = default)
    {
        var live = await SymbolList.AdmittedAsync(_client, ct).ConfigureAwait(false);
        var delisted = await SymbolList.AdmittedDelistedAsync(_client, ct).ConfigureAwait(false);

        var pool = new SortedSet<string>(live.Keys, StringComparer.Ordinal);
        pool.UnionWith(delisted.Keys);
        pool.UnionWith(ReferenceSeries.All);

        return pool.ToList();
    }

    /// <summary>Fixed-size groups, in order, without materialising the whole pool twice.</summary>
    private static IEnumerable<IReadOnlyList<string>> Chunks(IReadOnlyList<string> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return items.Skip(i).Take(size).ToList();
        }
    }

    private async Task<long> LoadSeriesAsync(StageContext context, string ticker, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await _client.GetAsync("eod/" + ticker, [("period", "d")], ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // **A 404 alone, and the narrowness is the point** [3.6]. A ticker the
            // price endpoint does not carry writes nothing and is not a reason to fail
            // a sweep of fifty thousand names.
            //
            // Everything else is rethrown. A 402 or a 429 is the allowance wall reached
            // in flight, which the gate's reserve makes the designed case rather than
            // the unlikely one, and a 402 persists for the day: swallowed here it would
            // write nothing for this ticker and nothing for every ticker after it,
            // then return having completed over a partial load. A stage completes or it
            // fails the run [`CLAUDE.md` §6], and the pre-flight gate and the in-flight
            // error are two different observations that must not collapse into each
            // other [3.4].
            return 0;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"eod/{ticker} returned {doc.RootElement.ValueKind} rather than an array. A shape " +
                    "change is a fault rather than an empty series.");
            }

            var bars = ParseSeries(doc.RootElement, ticker);

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
    }

    /// <summary>
    /// One ticker's series. The per-ticker endpoint carries no `code` and no
    /// `exchange_short_name`, the ticker being in the path, so the row shape differs
    /// from the bulk feed's and is parsed separately rather than coerced into it.
    ///
    /// Sorted by date, because COPY order reaches the table and two runs over one
    /// ticker must produce byte-identical output [`CLAUDE.md` §6]. The endpoint returns
    /// oldest first today and nothing contracts that it will.
    /// </summary>
    public static IReadOnlyList<SeriesBar> ParseSeries(JsonElement rows, string ticker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        var bars = new List<SeriesBar>(rows.GetArrayLength());

        foreach (var row in rows.EnumerateArray())
        {
            var date = Date(row, "date")
                ?? throw new InvalidOperationException(
                    $"A row of eod/{ticker} carries no date, so it cannot be keyed.");

            bars.Add(new SeriesBar(
                ticker,
                date,
                Money(row, "open"),
                Money(row, "high"),
                Money(row, "low"),
                Money(row, "close"),
                Money(row, "adjusted_close"),
                Volume(row)));
        }

        bars.Sort(static (a, b) => a.Date.CompareTo(b.Date));
        return bars;
    }

    /// <summary>One bar of a per-ticker series. Public so the parse is asserted without a live call.</summary>
    public readonly record struct SeriesBar(
        string Ticker, DateOnly Date,
        decimal? Open, decimal? High, decimal? Low, decimal? Close, decimal? AdjClose, long? Volume);

    private static async Task<long> LongAsync(StageContext context, string key, CancellationToken ct)
        => ConfigValue.Long(await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false));

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
