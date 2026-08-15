using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// C01. D-4's six absolute criteria, and the only place in this system anything is
/// filtered out.
///
/// **No component downstream narrows by rank, score or count** [D-5, INVARIANT 1].
/// Ranking at ingest decides what can be discovered before anything has had a chance
/// to be discovered, and the sentiment screen was once made structurally unable to
/// find what it exists to find by exactly that.
///
/// **The clean filing-gap criterion is computed for the date being built, never read
/// from a stored total** [D-62, M.1]. A ticker has more clean gaps now than it had
/// three years ago, so a stored value read during backfill would admit names a live
/// system on that date would have excluded, and backfilled screen scores would sit
/// on a different population than live ones.
/// </summary>
public sealed class UniverseBuilder : IStage
{
    public static readonly string[] Columns =
    [
        "ticker", "name", "sector", "size_bucket", "market_cap",
        "first_seen", "last_seen", "is_active",
    ];

    private static readonly string[] ConflictTarget = ["ticker"];

    private readonly EodhdClient _client;

    public UniverseBuilder(EodhdClient client) => _client = client;

    public string Name => "UniverseBuilder";

    public IReadOnlyList<string> ReadSet { get; } = ["price_daily", "fundamental_snapshot"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("security", WriteOperation.Insert, Columns)];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var minPrice = await DecimalAsync(context, "universe.min_price", ct).ConfigureAwait(false);
        var minAdv = await DecimalAsync(context, "universe.min_adv_20d", ct).ConfigureAwait(false);
        var minHistory = (int) await LongAsync(context, "universe.min_history_days", ct).ConfigureAwait(false);
        var minMarketCap = await DecimalAsync(context, "universe.min_market_cap", ct).ConfigureAwait(false);
        var largeFloor = await DecimalAsync(context, "universe.bucket_large_floor", ct).ConfigureAwait(false);
        var midFloor = await DecimalAsync(context, "universe.bucket_mid_floor", ct).ConfigureAwait(false);
        var minCleanGaps = (int) await LongAsync(context, "fundamentals.min_clean_gaps_for_substitution", ct).ConfigureAwait(false);
        var statementTimeout = (int) await LongAsync(context, "universe.pool_statement_timeout_seconds", ct).ConfigureAwait(false);

        var admitted = await SymbolList.AdmittedAsync(_client, ct).ConfigureAwait(false);
        var liquid = await LiquidAsync(context, minPrice, minAdv, minHistory, statementTimeout, ct).ConfigureAwait(false);
        var fundamentals = await FundamentalsAsync(context, ct).ConfigureAwait(false);

        // Every rejection counted, because "the universe builds to roughly 2,000
        // names" is not an answer on its own: what was excluded and by which
        // criterion is what makes the number readable [phase 1 done-when].
        var rejectedType = 0;
        var rejectedNoFundamentals = 0;
        var rejectedCleanGaps = 0;
        var rejectedNoShares = 0;
        var rejectedMarketCap = 0;

        var members = new List<Member>();

        foreach (var p in liquid)
        {
            if (!admitted.TryGetValue(p.Ticker, out var listing))
            {
                rejectedType++;
                continue;
            }

            // Counted apart from the thin ones, because they are not the same
            // fact and the pooled number is unreadable [1.8]. A ticker C03 has
            // never fetched has zero clean gaps by absence; a ticker it has
            // fetched has however many its filings actually carry. Both fail the
            // floor and only one of them is about the data.
            if (!fundamentals.TryGetValue(p.Ticker, out var f))
            {
                rejectedNoFundamentals++;
                continue;
            }

            if (f.CleanGaps < minCleanGaps)
            {
                rejectedCleanGaps++;
                continue;
            }

            if (f.SharesOutstanding is not decimal shares || shares <= 0)
            {
                // No readable share count means no market capitalisation, and D-4's
                // floor cannot be applied. Absent is not zero and not a pass.
                rejectedNoShares++;
                continue;
            }

            var marketCap = shares * p.Close;
            if (marketCap < minMarketCap)
            {
                rejectedMarketCap++;
                continue;
            }

            members.Add(new Member(
                p.Ticker, listing, marketCap, Bucket(marketCap, largeFloor, midFloor),
                p.FirstSeen, p.LastSeen));
        }

        // Ordinal, so two runs over the same data write in the same order.
        members.Sort(static (a, b) => string.CompareOrdinal(a.Ticker, b.Ticker));

        var sectors = await SectorsAsync(context, ct).ConfigureAwait(false);

        var written = await context.Data.BulkUpsertAsync(
            "security", Columns, ConflictTarget,
            async (w, c) =>
            {
                foreach (var m in members)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(m.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(m.Name, c).ConfigureAwait(false);
                    await w.WriteAsync(sectors.GetValueOrDefault(m.Ticker), c).ConfigureAwait(false);
                    await w.WriteAsync(m.Bucket, c).ConfigureAwait(false);
                    await w.WriteAsync(m.MarketCap, c).ConfigureAwait(false);
                    await w.WriteAsync(m.FirstSeen, c).ConfigureAwait(false);
                    await w.WriteAsync(m.LastSeen, c).ConfigureAwait(false);
                    await w.WriteAsync(true, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} names. Rejected: {1:N0} not common stock, {2:N0} with no fundamentals fetched yet, " +
            "{3:N0} fetched but below {4} clean filing gaps, {5:N0} with no readable share count, " +
            "{6:N0} below the market cap floor. Candidates passing price, liquidity and history: {7:N0}",
            members.Count, rejectedType, rejectedNoFundamentals, rejectedCleanGaps, minCleanGaps,
            rejectedNoShares, rejectedMarketCap, liquid.Count);

        return new StageResult(written, "ok", detail);
    }

    /// <summary>
    /// The size bucket, which is total: every market capitalisation takes one of three
    /// values and none takes null.
    ///
    /// Public so the test that C11's fallback rests on reads this rule rather than a
    /// copy of it [2.10]. `METRICS.md` §6.4 says a null <c>size_bucket</c> cannot
    /// arise, and that claim is this function being total plus a member having cleared
    /// D-4's market capitalisation floor to be here at all. Without it a reader cannot
    /// tell whether the bucket hole was reasoned about or missed, and the percentile
    /// fallback would need a third step for a case that does not exist.
    /// </summary>
    public static string Bucket(decimal marketCap, decimal largeFloor, decimal midFloor)
        => marketCap >= largeFloor ? "large" : marketCap >= midFloor ? "mid" : "small";

    private readonly record struct Member(
        string Ticker, string Name, decimal MarketCap, string Bucket, DateOnly FirstSeen, DateOnly LastSeen);

    private readonly record struct Liquid(string Ticker, decimal Close, DateOnly FirstSeen, DateOnly LastSeen);

    private readonly record struct Fund(int CleanGaps, decimal? SharesOutstanding);

    /// <summary>
    /// Price, liquidity and history, as of the date being built.
    ///
    /// **The 20-day median dollar volume is computed here from `price_daily`, never
    /// taken from a provider average** [1.5]. The probe's own sample selection used
    /// `avgvol_50d * adjusted_close` and understated for any name that split inside
    /// the window, because average volume is unadjusted while adjusted close is
    /// adjusted. A median rather than a mean, and computed from the same bars every
    /// other stage reads.
    ///
    /// **Rewritten at D-102 and D-4's criteria are unchanged.** The set is the same;
    /// what changed is how it is derived. Measured against the 109,787,541 row store:
    /// the old statement took 871.1s and this one is proved to return the identical
    /// ticker set, compared set against set rather than by count [`PROGRESS.md`].
    ///
    /// **The old shape built a hundred and nine million rows to keep fifty-eight
    /// thousand.** `row_number() OVER (PARTITION BY ticker ORDER BY date DESC)` across
    /// every row matching the date bound materialised a 5.4 GB on-disk CTE and sorted
    /// it externally at about 4.5 GB across three workers, and every node above it
    /// re-read that spill: 19 GB of temporary I/O for 8,610 rows. The whole table was
    /// then scanned a second time for `span`.
    ///
    /// **What replaces it is a seek per ticker rather than a scan**, which is the same
    /// change item 24 made to C03's pool and is why D-102 covers both. The distinct
    /// tickers come off the `(ticker, date)` primary key, a LATERAL takes each ticker's
    /// twenty most recent bars by index however far back they are, which is exactly
    /// what `rn <= 20` meant, and the history test is asked last of the few thousand
    /// names that already cleared price and liquidity rather than of all 88,341.
    ///
    /// **The history test is an `EXISTS ... OFFSET` and not a `count(*)`, and that is
    /// worth 2.2x on its own.** A first attempt used `count(*) >= 250` inside a LATERAL,
    /// which reads every bar a ticker has, and measured 948.7s against the old shape's
    /// 2,090.8s. `EXISTS ... OFFSET 249 LIMIT 1` stops at the 250th index entry, which
    /// is the same question asked in a way that can stop early, and is exactly what C03
    /// already does. `first_seen` and `last_seen` come from `min` and `max` on the same
    /// index, each of which Postgres answers as a one-row seek rather than an aggregate.
    ///
    /// `count(*) >= min_history_days` is preserved as an offset rather than replaced by
    /// a calendar test: "has at least N bars" and "has a bar N days ago" are different
    /// questions and the second admits a ticker with ten bars spread over a year.
    ///
    /// **It stays date-parametric and that is load-bearing for 3.11**, which runs this
    /// per weekly evaluation date across the window. Every bound is `<= asOf`, so the
    /// statement answers for a historical date exactly as it answers for today, which
    /// is the property a summary of current state could not have given it [D-102].
    /// </summary>
    private static async Task<IReadOnlyList<Liquid>> LiquidAsync(
        StageContext context, decimal minPrice, decimal minAdv, int minHistory,
        int statementTimeout, CancellationToken ct)
    {
        var asOf = context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var sql = $"""
            WITH tickers AS (
                SELECT DISTINCT ticker FROM price_daily WHERE date <= DATE '{asOf}'
            ),
            recent AS (
                SELECT t.ticker, b.close, b.volume, b.rn
                FROM tickers t
                CROSS JOIN LATERAL (
                    SELECT close, volume, row_number() OVER (ORDER BY date DESC) AS rn
                    FROM price_daily p
                    WHERE p.ticker = t.ticker AND p.date <= DATE '{asOf}'
                    ORDER BY p.date DESC
                    LIMIT {DollarVolume.WindowBars.ToString(CultureInfo.InvariantCulture)}
                ) b
            ),
            latest AS (SELECT ticker, close FROM recent WHERE rn = 1),
            mdv AS (
                SELECT ticker,
                       {DollarVolume.MedianExpression} AS median_dollar_volume
                FROM recent
                WHERE {DollarVolume.RowFilter}
                GROUP BY ticker
            ),
            liquid AS (
                SELECT l.ticker, l.close
                FROM latest l
                JOIN mdv m ON m.ticker = l.ticker
                WHERE l.close >= {minPrice.ToString(CultureInfo.InvariantCulture)}
                  AND m.median_dollar_volume >= {minAdv.ToString(CultureInfo.InvariantCulture)}
            )
            SELECT q.ticker, q.close,
                   (SELECT min(f.date) FROM price_daily f
                     WHERE f.ticker = q.ticker AND f.date <= DATE '{asOf}') AS first_seen,
                   (SELECT max(g.date) FROM price_daily g
                     WHERE g.ticker = q.ticker AND g.date <= DATE '{asOf}') AS last_seen
            FROM liquid q
            WHERE EXISTS (
                SELECT 1 FROM price_daily h
                WHERE h.ticker = q.ticker AND h.date <= DATE '{asOf}'
                OFFSET {(minHistory - 1).ToString(CultureInfo.InvariantCulture)} LIMIT 1
            )
            ORDER BY q.ticker;
            """;

        var rows = await context.Data
            .ReadAsync("price_daily", sql, ct, statementTimeout).ConfigureAwait(false);

        return rows.Select(r => new Liquid(
            (string) r[0]!,
            (decimal) r[1]!,
            DateOnly.FromDateTime((DateTime) r[2]!),
            DateOnly.FromDateTime((DateTime) r[3]!))).ToList();
    }

    /// <summary>
    /// The clean gap count and the newest readable share count, both as of the date
    /// being built.
    ///
    /// The gap count filters on `filing_date_effective <= date`, so it is what a
    /// live system on that date would have seen rather than what is known now
    /// [M.1]. The share count filters the same way, because a market capitalisation
    /// built from a filing nobody had yet is the lookahead INVARIANT 12 exists to
    /// prevent arriving through a different door.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, Fund>> FundamentalsAsync(
        StageContext context, CancellationToken ct)
    {
        var asOf = context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var sql = $"""
            WITH readable AS (
                SELECT * FROM fundamental_snapshot
                WHERE filing_date_effective IS NOT NULL AND filing_date_effective <= DATE '{asOf}'
            ),
            gaps AS (
                SELECT ticker, count(*) AS clean_gaps FROM readable
                WHERE filing_date_unknown_reason = 'none' GROUP BY ticker
            ),
            shares AS (
                SELECT DISTINCT ON (ticker) ticker, shares_outstanding
                FROM readable WHERE shares_outstanding IS NOT NULL
                ORDER BY ticker, period_end DESC
            )
            SELECT coalesce(g.ticker, s.ticker), coalesce(g.clean_gaps, 0), s.shares_outstanding
            FROM gaps g FULL OUTER JOIN shares s ON s.ticker = g.ticker;
            """;

        var rows = await context.Data.ReadAsync("fundamental_snapshot", sql, ct).ConfigureAwait(false);

        var map = new Dictionary<string, Fund>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            map[(string) r[0]!] = new Fund(
                Convert.ToInt32(r[1], CultureInfo.InvariantCulture),
                r[2] is null ? null : (decimal) r[2]!);
        }

        return map;
    }

    /// <summary>
    /// Sector, read from the most recent filing at or before the date being built
    /// [D-97].
    ///
    /// **This was a call per member and is now a read.** At 10 units a name over 2,840
    /// members that was 28,401 units a week, buying what `fundamentals/{t}` already
    /// carries for nothing in a call C03 makes anyway. Removing it is what makes a
    /// per-date C01 affordable at all.
    ///
    /// **As of the date being built, not as of now**, which the call could never be.
    /// A backfilled 2021 date reads the sector on the newest filing readable then,
    /// where the call read today's. Sector as of a filing is still not sector as of a
    /// date, and that limitation is recorded rather than proxied [D-97].
    ///
    /// Null where the ticker has no readable filing carrying one, which resolves to the
    /// percentile engine's existing bucket-only fallback rather than to a new case.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string?>> SectorsAsync(
        StageContext context, CancellationToken ct)
    {
        var asOf = context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var sql = $"""
            SELECT DISTINCT ON (ticker) ticker, sector
            FROM fundamental_snapshot
            WHERE sector IS NOT NULL
              AND filing_date_effective IS NOT NULL
              AND filing_date_effective <= DATE '{asOf}'
            ORDER BY ticker, filing_date_effective DESC, period_end DESC;
            """;

        var rows = await context.Data.ReadAsync("fundamental_snapshot", sql, ct).ConfigureAwait(false);

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            map[(string) r[0]!] = r[1] as string;
        }

        return map;
    }

    private static async Task<long> LongAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);
        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a whole number.");
    }

    private static async Task<decimal> DecimalAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);
        return decimal.TryParse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a number.");
    }
}
