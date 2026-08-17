using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// C04. Sentiment for the whole universe, with no pre-selection [D-23].
///
/// **This is the component convergence got into once already.** Sentiment was pulled
/// only for the top 400 by prior screen score, so a thinly covered name having the
/// exact coverage spike the sentiment screen exists to catch had no data and could
/// never be found [`ARCHITECTURE.html` §20, D-5]. Absolute filters live in the
/// universe definition and nowhere else, so this stage takes `security` whole and
/// narrows by nothing.
///
/// **A sparse series is the ordinary state, not a fault.** The probe measured 4 to
/// 122 days with a row out of 180 on small caps, and rows appear only on days that
/// carry news. A day with no row is a day nobody wrote about, which is not the same
/// as a day of zero attention: a z-score of zero says attention is exactly at its own
/// baseline, absent says nobody knows, and the screens treat those differently
/// [D-12, `CLAUDE.md` §6]. No row is fabricated to fill a gap.
///
/// **UNVERIFIED AGAINST THE PROVIDER.** The endpoint's row fields are taken from the
/// documented shape and from what the phase P probe reported measuring, not from a
/// recorded transcript: 1.9 captured only the envelope, an object keyed by ticker.
/// The parser therefore accepts the documented names and tolerates their absence
/// rather than assuming. Live verification is owed and is blocked on the provider
/// allowance.
/// </summary>
public sealed class SentimentIngestor : IStage, IBackfillStage
{
    public static readonly string[] Columns = ["ticker", "date", "article_count", "sentiment_score"];

    /// <summary>
    /// The attempt record [D-99, 0011]. Written for every ticker a dispatched batch
    /// named, whether or not it came back with days, because on this endpoint yielding
    /// nothing is the ordinary case rather than the exception.
    /// </summary>
    public static readonly string[] AttemptColumns =
        ["ticker", "last_attempted_date", "last_yield_date", "rows_last_attempt"];

    private static readonly string[] ConflictTarget = ["ticker", "date"];

    private static readonly string[] AttemptConflictTarget = ["ticker"];

    private readonly EodhdClient _client;

    public SentimentIngestor(EodhdClient client) => _client = client;

    public string Name => "SentimentIngestor";

    // `price_daily` is read by the sweep alone, for the in-window delisted names D-101
    // widened the pool to. `sentiment_fetch_attempt` is in neither list twice: a stage
    // may read what it writes, which is what `DeclaredAccess.CanRead` says.
    public IReadOnlyList<string> ReadSet { get; } = ["security_daily", "price_daily"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new TableWrite("sentiment_daily", WriteOperation.Insert, Columns),
        new TableWrite("sentiment_fetch_attempt", WriteOperation.Insert, AttemptColumns),
    ];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var perCall = (int) await LongAsync(context, "sentiment.tickers_per_call", ct).ConfigureAwait(false);
        var lookback = (int) await LongAsync(context, "sentiment.lookback_days", ct).ConfigureAwait(false);

        var universe = await UniverseAsync(context, ct).ConfigureAwait(false);
        if (universe.Count == 0)
        {
            // Nothing to do rather than nothing to say. C01 has not run, and a stage
            // that writes zero rows halts the sequence, which is the correct answer:
            // a night with no universe has nothing downstream can use.
            return new StageResult(0, "ok", "security is empty, so there is no universe to pull sentiment for");
        }

        var from = context.Date.AddDays(-lookback);
        long written = 0;
        var covered = 0;

        foreach (var batch in Batch(universe, perCall))
        {
            var rows = await FetchAsync(batch, from, context.Date, ct).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                continue;
            }

            covered += rows.Select(r => r.Ticker).Distinct(StringComparer.Ordinal).Count();

            written += await WriteDaysAsync(context, rows, ct).ConfigureAwait(false);
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} of {1:N0} universe names returned a row over {2} days. A name with none is a name " +
            "nobody wrote about, which is not zero attention [D-12]",
            covered, universe.Count, lookback);

        return new StageResult(written, "ok", detail);
    }

    // ------------------------------------------------------- range mode [3.8] ---

    /// <summary>
    /// One pass at window width, <c>from</c> at the window start [D-93].
    ///
    /// **The pool is the live universe plus every in-window delisted name** [D-101],
    /// and the second half is not optional. Without it a delisted name admitted to a
    /// reconstructed 2021 universe has no sentiment rows at all, so `article_count`
    /// zero-fills and its z-score is computed against a baseline of zeros while
    /// `sentiment_score` stays null. That is a degenerate value that ranks rather than
    /// an absence that abstains, and it ranks the same way for every name that later
    /// failed. S3 is one of the two screens §20 names as doing the most to keep this
    /// system off megacaps, and S4's survivorship is irreducible [open item 12], so
    /// leaving this half out puts both of them on survivors alone.
    ///
    /// **The attempt stamps the range start**, which is C02's half of D-99's asymmetry.
    /// The nightly call asks from `context.Date - sentiment.lookback_days` and this
    /// asks from the window start, so the two differ in depth and a ticker the nightly
    /// run touched is not as complete as one this touched. C03 and C05 stamp the range
    /// end because for them the two calls are the same call.
    ///
    /// **Config resolves as of the range end and that is not the case D-93 governs.**
    /// This sweep is ticker-partitioned: no date is being computed and no configured
    /// value reaches a row it writes, the days being the provider's own.
    /// </summary>
    public async Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = await context.ForDateAsync(context.To, ct).ConfigureAwait(false);

        var perCall = (int) await LongAsync(settings, "sentiment.tickers_per_call", ct).ConfigureAwait(false);
        var weight = await LongAsync(settings, "backfill.weight_sentiments_per_ticker", ct).ConfigureAwait(false);
        var reserve = await LongAsync(settings, "backfill.unit_reserve", ct).ConfigureAwait(false);
        var allowance = await LongAsync(settings, "backfill.daily_unit_allowance", ct).ConfigureAwait(false);

        var pool = await RangePoolAsync(settings, context.From, ct).ConfigureAwait(false);
        var already = await AttemptedOnAsync(settings, context.From, ct).ConfigureAwait(false);
        var remaining = pool.Where(t => !already.Contains(t)).ToList();

        long written = 0;
        var dispatched = 0;
        var yielded = 0;
        var halted = false;
        string? haltDetail = null;

        foreach (var batch in Batch(remaining, perCall))
        {
            // **The projection is the batch's own size times the per-ticker weight**,
            // because the endpoint meters flat per ticker whatever the batching
            // [1.6, 3.1]. A batch is the unit of work and a ticker is the unit of
            // billing, so the gate is asked about the first and priced on the second.
            var decision = await context.NextUnitAsync(batch.Count * weight, reserve, allowance, ct)
                .ConfigureAwait(false);

            if (!decision.Fits)
            {
                halted = true;
                haltDetail = decision.Detail;
                break;
            }

            var rows = await FetchAsync(batch, context.From, context.To, ct).ConfigureAwait(false);

            written += await WriteDaysAsync(settings, rows, ct).ConfigureAwait(false);

            var perTicker = rows
                .GroupBy(r => r.Ticker, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (long) g.Count(), StringComparer.Ordinal);

            // **Every member of the batch gains a row, not every member that answered.**
            // A ticker nobody wrote about across five years returns nothing and would
            // otherwise be re-asked on every run for the life of the sweep, and those
            // are exactly the thinly covered names the sentiment screen exists to find
            // [D-12, §20].
            await RecordAttemptsAsync(settings, batch, perTicker, context.From, ct).ConfigureAwait(false);

            dispatched += batch.Count;
            yielded += perTicker.Count;
        }

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} ticker-day(s) over {1:N0} of {2:N0} pool member(s), of which {3:N0} returned at least " +
            "one day and {4:N0} returned none, which is a name nobody wrote about rather than zero " +
            "attention [D-12]. {5:N0} carried an attempt for this range already and were not dispatched " +
            "[D-99]. The pool is the universe plus the in-window delisted names [D-101].",
            written, dispatched, pool.Count, yielded, dispatched - yielded,
            pool.Count - remaining.Count);

        return halted
            ? BackfillResult.Halted(written, context.To, detail + " " + haltDetail)
            : BackfillResult.Completed(written, context.To, detail);
    }

    /// <summary>
    /// The sweep's pool: the live universe, plus every admitted delisted common stock
    /// carrying a bar at or after the window start [D-101].
    /// </summary>
    private async Task<IReadOnlyList<string>> RangePoolAsync(
        StageContext context, DateOnly windowStart, CancellationToken ct)
    {
        // Before the live half is read rather than after it comes back empty, and before
        // the delisted half is fetched, so an unfilled universe costs no provider call
        // [BackfillPool.RequireUniverseCoverageAsync].
        await BackfillPool.RequireUniverseCoverageAsync(context, windowStart, context.Date, ct)
            .ConfigureAwait(false);

        var live = await UniverseAsync(context, ct).ConfigureAwait(false);

        var delisted = await BackfillPool.DelistedWithBarsInWindowAsync(
            _client, context, windowStart, ct).ConfigureAwait(false);

        var pool = new SortedSet<string>(live, StringComparer.Ordinal);
        pool.UnionWith(delisted);

        return pool.ToList();
    }

    /// <summary>
    /// Tickers already carrying an attempt at this range's start, which are the ones
    /// the sweep does not ask for again [D-99].
    /// </summary>
    private static async Task<IReadOnlySet<string>> AttemptedOnAsync(
        StageContext context, DateOnly rangeStart, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "sentiment_fetch_attempt",
            $"""
             SELECT ticker
             FROM sentiment_fetch_attempt
             WHERE last_attempted_date = DATE '{rangeStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'
             ORDER BY ticker;
             """,
            ct).ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// One attempt row per dispatched ticker, upserted on `ticker` so a second run over
    /// the same range writes what the first wrote [D-68].
    /// </summary>
    private static async Task RecordAttemptsAsync(
        StageContext context, IReadOnlyList<string> dispatched,
        IReadOnlyDictionary<string, long> yields, DateOnly rangeStart, CancellationToken ct)
    {
        if (dispatched.Count == 0)
        {
            return;
        }

        var ordered = dispatched.Order(StringComparer.Ordinal).ToList();

        await context.Data.BulkUpsertAsync(
            "sentiment_fetch_attempt", AttemptColumns, AttemptConflictTarget,
            async (w, c) =>
            {
                foreach (var ticker in ordered)
                {
                    var rows = yields.GetValueOrDefault(ticker);

                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(rangeStart, c).ConfigureAwait(false);
                    await w.WriteAsync(rows > 0 ? rangeStart : (DateOnly?) null, c).ConfigureAwait(false);
                    await w.WriteAsync(rows, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
    }

    /// <summary>The day rows write, shared by both entry points.</summary>
    private static async Task<long> WriteDaysAsync(
        StageContext context, IReadOnlyList<Row> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        return await context.Data.BulkUpsertAsync(
            "sentiment_daily", Columns, ConflictTarget,
            async (w, c) =>
            {
                foreach (var r in rows)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(r.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Date, c).ConfigureAwait(false);
                    await w.WriteAsync(r.ArticleCount, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Score, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The universe, whole. No ordering by anything that could become a rank, and no
    /// limit: narrowing here is the failure this component is named in
    /// `ARCHITECTURE.html` §20 for.
    /// </summary>
    private static async Task<IReadOnlyList<string>> UniverseAsync(StageContext context, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "security_daily", Universe.MembersAsOf(context.Date), ct)
            .ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToList();
    }

    /// <summary>
    /// Batched, because the endpoint accepts a comma-separated symbol list and one
    /// call per name over a two-thousand name universe is two thousand round trips.
    ///
    /// **This is a latency knob and not a cost one**, which is the opposite of what
    /// was written here when 1.6 landed. Sentiment is metered flat per ticker:
    /// measured at 1, 10 and 20 tickers it cost 5, 50 and 100 units, so batching
    /// changes how long the stage takes and not what it spends. The size stays
    /// configuration because it is still a real tunable [`CLAUDE.md` §8].
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> Batch(IReadOnlyList<string> all, int size)
    {
        for (var i = 0; i < all.Count; i += size)
        {
            yield return all.Skip(i).Take(size).ToList();
        }
    }

    /// <summary>One ticker-day. Public because the parser is asserted directly, the field names being the least-verified thing in this phase.</summary>
    public readonly record struct Row(string Ticker, DateOnly Date, int? ArticleCount, float? Score);

    private async Task<IReadOnlyList<Row>> FetchAsync(
        IReadOnlyList<string> tickers, DateOnly from, DateOnly to, CancellationToken ct)
    {
        using var doc = await _client.GetAsync(
            "sentiments",
            [
                ("s", string.Join(",", tickers)),
                ("from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ],
            ct).ConfigureAwait(false);

        return Parse(doc.RootElement);
    }

    /// <summary>
    /// The envelope is an object keyed by ticker, each value an array of daily rows.
    /// Shapes are checked rather than assumed, because the field names here are the
    /// least-verified thing in this phase.
    /// </summary>
    public static IReadOnlyList<Row> Parse(JsonElement root)
    {
        var rows = new List<Row>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            return rows;
        }

        foreach (var byTicker in root.EnumerateObject())
        {
            if (byTicker.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var day in byTicker.Value.EnumerateArray())
            {
                if (day.ValueKind != JsonValueKind.Object
                    || !day.TryGetProperty("date", out var d)
                    || d.ValueKind != JsonValueKind.String
                    || !DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    continue;
                }

                rows.Add(new Row(byTicker.Name, date, Count(day), Score(day)));
            }
        }

        // Sorted, because COPY order reaches the table and two runs over the same
        // response must produce identical output [CLAUDE.md section 6].
        rows.Sort(static (a, b) =>
        {
            var t = string.CompareOrdinal(a.Ticker, b.Ticker);
            return t != 0 ? t : a.Date.CompareTo(b.Date);
        });

        return rows;
    }

    /// <summary>Articles that day. Absent stays null: a day with no count is not a day with none.</summary>
    private static int? Count(JsonElement day)
        => day.TryGetProperty("count", out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var n)
            ? n
            : null;

    /// <summary>
    /// The normalised score. A 32-bit float because the column is <c>real</c> and this
    /// is not money [SCHEMA.md, INVARIANT 16].
    /// </summary>
    private static float? Score(JsonElement day)
        => day.TryGetProperty("normalized", out var v) && v.ValueKind == JsonValueKind.Number
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
