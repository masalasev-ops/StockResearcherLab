using System.Globalization;
using System.Text;
using System.Text.Json;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Compute;

/// <summary>
/// C10. One row a day: breadth, the regime label, sector relative strength, and a VIX
/// column this provider has no series for.
///
/// **Runs after C08 within the 18:05 slot**, because breadth is counted off
/// <c>indicator_daily.dist_200dma</c> rather than recomputed from prices. One
/// definition of "above its own 200-day average", in the column that already carries
/// it.
///
/// **The denominator excludes names with too little history rather than counting them
/// as below their average.** Counting them would put every recent listing on the
/// bearish side of the measure permanently, which is a slow drift nothing would
/// report.
///
/// **`vix` is written null and that is recorded rather than proxied** [`METRICS.md`
/// §5]. The bulk end-of-day feed carries equities and the index is not among them. A
/// realised-volatility substitute would carry the column's name without its meaning,
/// which is worse than an absence a reader can see.
/// </summary>
public sealed class MarketContextEngine : IStage, IBackfillStage
{
    public static readonly string[] Columns =
        ["date", "breadth", "vix", "regime_label", "sector_relative_strength"];

    private static readonly string[] ConflictTarget = ["date"];

    /// <summary>D-80's three, and the database holds the same list as a CHECK [0005].</summary>
    public const string RiskOn = "risk_on";
    public const string RiskOff = "risk_off";
    public const string Mixed = "mixed";

    /// <summary>Trading dates the sector and universe composites are chained over.</summary>
    public const int CompositeWindow = 64;

    private const int RelativeStrengthWindow = 63;

    public string Name => "MarketContextEngine";

    public IReadOnlyList<string> ReadSet { get; } = ["price_daily", "security_daily", "indicator_daily"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("market_context_daily", WriteOperation.Insert, Columns)];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var breadthMaDays = (int) await LongAsync(context, "market.breadth_ma_days", ct).ConfigureAwait(false);
        var minMembers = (int) await LongAsync(context, "market.sector_composite_min_members", ct).ConfigureAwait(false);
        var high = (double) await DecimalAsync(context, "market.regime_breadth_high", ct).ConfigureAwait(false);
        var low = (double) await DecimalAsync(context, "market.regime_breadth_low", ct).ConfigureAwait(false);

        var breadth = await BreadthAsync(context, ct).ConfigureAwait(false);
        var benchmarkAbove = await BenchmarkAboveItsAverageAsync(context, breadthMaDays, ct).ConfigureAwait(false);
        var sectors = await SectorRelativeStrengthAsync(context, minMembers, ct).ConfigureAwait(false);

        var label = Regime(breadth, benchmarkAbove, high, low);

        var written = await WriteAsync(
            context, [new Row(context.Date, breadth, label, sectors)], ct).ConfigureAwait(false);

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "regime {0}, breadth {1}, benchmark {2} its {3}-day average, {4} sector(s), vix null",
            label,
            breadth is null ? "unknown" : breadth.Value.ToString("F3", CultureInfo.InvariantCulture),
            benchmarkAbove is null ? "unknown against" : benchmarkAbove.Value ? "above" : "below",
            breadthMaDays.ToString(CultureInfo.InvariantCulture),
            sectors.Count(ch => ch == ':'));

        return new StageResult(written, "ok", detail);
    }

    /// <summary>One date's context row, so the write is shared by the nightly and range paths.</summary>
    private readonly record struct Row(DateOnly Date, double? Breadth, string Label, string Sectors);

    /// <summary>
    /// The write, shared by both paths.
    ///
    /// **Extracted rather than copied at 3.14** for the reason C08's and C35's were: a
    /// five-column write stated twice drifts, and the `jsonb` column is the one where the
    /// drift would be silent rather than loud. Binary COPY carries no type name and the
    /// two formats differ by one leading version byte, which is why the json goes through
    /// `WriteJsonAsync` and not through a string [2.9, found at 2.12].
    /// </summary>
    private static Task<long> WriteAsync(
        StageContext context, IReadOnlyList<Row> rows, CancellationToken ct)
        => context.Data.BulkUpsertAsync(
            "market_context_daily", Columns, ConflictTarget,
            async (w, c) =>
            {
                foreach (var r in rows)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(r.Date, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Breadth is null ? null : (float?) r.Breadth.Value, c).ConfigureAwait(false);

                    // No series in this feed. Null rather than a substitute wearing the
                    // column's name [METRICS.md section 5].
                    await w.WriteAsync<float?>(null, c).ConfigureAwait(false);

                    await w.WriteAsync(r.Label, c).ConfigureAwait(false);
                    await w.WriteJsonAsync(r.Sectors, c).ConfigureAwait(false);
                }
            }, ct);

    // ------------------------------------------------- range mode [3.14] ---

    /// <summary>
    /// The same work as <see cref="ExecuteAsync"/> over a range, one date at a time and
    /// one write at the end.
    ///
    /// **Date-partitioned, so there is no ticker loop to hoist.** Each date's breadth is a
    /// count over that date's universe, each benchmark test is a moving average ending on
    /// it, and each sector block is a ratio of that date's members. Nothing carries across
    /// a date except the config version, so the range is the nightly work repeated with
    /// its bounds moved and the write batched.
    ///
    /// **The three reads are reissued per date rather than generalised across the range.**
    /// Widening them means cross joining each against a date set, which is a rewrite of
    /// what they compute rather than of what they are bounded by, and this phase does not
    /// reimplement arithmetic for the backfill [D-93]. The checkpoint asks for the two
    /// shapes measured over one month and the faster taken; that measurement reads
    /// `indicator_daily`, which no backfill has yet written, so it is recorded as owed
    /// rather than answered from a guess.
    ///
    /// **What is batched is the write and not the reads**, which is where the saving
    /// actually is: 1,260 single-row upserts become one COPY per range.
    /// </summary>
    public async Task<BackfillResult> ExecuteRangeAsync(
        BackfillContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Everything that is not the date loop, named [item 43]. Here that is a calendar
        // read before it and one batched COPY after it, which the per-date figure has
        // never covered [3.14].
        var phases = new PhaseTimer();

        var atEnd = await context.ForDateAsync(context.To, ct).ConfigureAwait(false);

        var dates = await context.SessionsAsync(ct).ConfigureAwait(false);
        phases.Mark("calendar");

        if (dates.Count == 0)
        {
            return BackfillResult.Completed(
                0, context.To, "no trading date in the range carries a price_daily bar, so nothing is computed");
        }

        var byVersion = new Dictionary<int, Settings>();
        var rows = new List<Row>(dates.Count);

        var unknownBreadth = 0;

        // The unit is a date, this loop being date-partitioned, and it times the compute
        // alone: the write is one batched COPY after the loop, so it is in the total in
        // `run_log.duration_ms` and not in any per-date figure [3.14, 3.17].
        var elapsed = new List<long>();

        foreach (var date in dates)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            var stage = await context.ForDateAsync(date, ct).ConfigureAwait(false);

            if (!byVersion.TryGetValue(stage.ConfigVersion, out var settings))
            {
                settings = new Settings(
                    (int) await LongAsync(stage, "market.breadth_ma_days", ct).ConfigureAwait(false),
                    (int) await LongAsync(stage, "market.sector_composite_min_members", ct).ConfigureAwait(false),
                    (double) await DecimalAsync(stage, "market.regime_breadth_high", ct).ConfigureAwait(false),
                    (double) await DecimalAsync(stage, "market.regime_breadth_low", ct).ConfigureAwait(false));

                byVersion[stage.ConfigVersion] = settings;
            }

            var breadth = await BreadthAsync(stage, ct).ConfigureAwait(false);
            var above = await BenchmarkAboveItsAverageAsync(stage, settings.BreadthMaDays, ct).ConfigureAwait(false);
            var sectors = await SectorRelativeStrengthAsync(stage, settings.MinMembers, ct).ConfigureAwait(false);

            if (breadth is null)
            {
                unknownBreadth++;
            }

            rows.Add(new Row(
                date, breadth, Regime(breadth, above, settings.High, settings.Low), sectors));

            elapsed.Add((long) System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        // The date loop's span, which `elapsed` already holds. Dropped rather than left
        // to fall into the write phase below.
        phases.Skip();

        // Ascending by date, which the calendar already is. Stated at the point that
        // relies on it, because write order reaches output [CLAUDE.md section 6].
        rows.Sort(static (a, b) => a.Date.CompareTo(b.Date));

        var written = await WriteAsync(atEnd, rows, ct).ConfigureAwait(false);
        phases.Mark("write");

        return BackfillResult.Completed(
            written, context.To,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0:N0} market_context_daily row(s) over {1:N0} trading date(s). {2:N0} carry an unknown " +
                "breadth and are therefore labelled {3}, which is a date `indicator_daily` has no " +
                "`dist_200dma` for rather than a date with no trend. {4:N0} risk_on, {5:N0} risk_off, " +
                "{6:N0} mixed. vix is null on every one of them, the bulk feed carrying equities and not " +
                "the index [D-80, open item 30]. {7} {8}",
                written, dates.Count, unknownBreadth, Mixed,
                rows.Count(static r => r.Label == RiskOn),
                rows.Count(static r => r.Label == RiskOff),
                rows.Count(static r => r.Label == Mixed),
                RangeTiming.Describe("date, compute only", elapsed),
                phases.Describe()));
    }

    /// <summary>
    /// The four configured values one date is composed under, held per resolved version so
    /// a range whose config never moved reads them once [C08's precedent].
    /// </summary>
    private readonly record struct Settings(int BreadthMaDays, int MinMembers, double High, double Low);

    /// <summary>
    /// D-80. Three values, and the benchmark contributes a sign test rather than a
    /// threshold because zero is already meaningful there.
    ///
    /// A single crossing on either input moves the label to <c>mixed</c> rather than
    /// flipping it to the opposite, which is why no minimum run length is needed to
    /// stop it alternating.
    ///
    /// Public so the reference test reads the same rule the stage does.
    /// </summary>
    public static string Regime(double? breadth, bool? benchmarkAbove, double high, double low)
    {
        if (breadth is not { } b || benchmarkAbove is not { } above)
        {
            return Mixed;
        }

        if (b >= high && above)
        {
            return RiskOn;
        }

        return b <= low && !above ? RiskOff : Mixed;
    }

    /// <summary>
    /// The share of the universe above its own 200-day average, counted off the column
    /// that already carries that distance.
    /// </summary>
    private static async Task<double?> BreadthAsync(StageContext context, CancellationToken ct)
    {
        var sql = $"""
            SELECT count(*) FILTER (WHERE i.dist_200dma > 0) AS above,
                   count(*) FILTER (WHERE i.dist_200dma IS NOT NULL) AS measurable
            FROM indicator_daily i
            JOIN {Universe.AsOf(context.Date)} s ON s.ticker = i.ticker AND s.is_active
            WHERE i.date = {Literal(context.Date)};
            """;

        var rows = await context.Data.ReadAsync("indicator_daily", sql, ct).ConfigureAwait(false);

        if (rows.Count == 0 || (long) rows[0][1]! == 0)
        {
            return null;
        }

        return (double) (long) rows[0][0]! / (long) rows[0][1]!;
    }

    /// <summary>
    /// Whether the benchmark closed above its own n-day average. A sign test, so it
    /// carries no threshold of its own [D-80].
    ///
    /// **The window is bounded below rather than filtered after the fact** [3.17]. The
    /// row number over every bar at or before the date read the benchmark's whole series
    /// to keep `maDays` of it, which was 265 rows while nothing had fetched the series
    /// and is 8,444 now that something has [D-104]. Same rows, since `PRIMARY KEY
    /// (ticker, date)` admits no tie in date, and an index scan of two hundred rather
    /// than a scan of every bar the series ever had.
    /// </summary>
    private static async Task<bool?> BenchmarkAboveItsAverageAsync(
        StageContext context, int maDays, CancellationToken ct)
    {
        var sql = $"""
            WITH w AS (
                SELECT date, adj_close,
                       row_number() OVER (ORDER BY date DESC) AS rn
                FROM price_daily
                WHERE ticker = '{IndicatorEngine.Benchmark}'
                  AND date <= {Literal(context.Date)}
                  AND adj_close IS NOT NULL
                ORDER BY date DESC
                LIMIT {maDays.ToString(CultureInfo.InvariantCulture)}
            )
            SELECT count(*) AS bars,
                   max(adj_close) FILTER (WHERE rn = 1) AS last_close,
                   avg(adj_close) AS moving_average
            FROM w
            WHERE rn <= {maDays.ToString(CultureInfo.InvariantCulture)};
            """;

        var rows = await context.Data.ReadAsync("price_daily", sql, ct).ConfigureAwait(false);

        // A shorter window is not the average this names, so it is unknown rather than
        // computed over what happens to be stored [CLAUDE.md section 6].
        if (rows.Count == 0 || (long) rows[0][0]! < maDays)
        {
            return null;
        }

        return (decimal) rows[0][1]! > (decimal) rows[0][2]!;
    }

    /// <summary>
    /// Each sector's trailing return against the universe's, as a jsonb object.
    ///
    /// **Keys are written in ordinal sort order.** An object built from a dictionary
    /// carries that dictionary's enumeration order, which is unspecified and can differ
    /// between runs of the same binary, and this column reaches the cached prefix where
    /// a byte difference breaks the cache and roughly triples the input bill silently
    /// [INVARIANT 6].
    ///
    /// **The composite window is bounded per member rather than filtered after the fact,
    /// and this was the larger half of C10's 391.8 minutes** [3.17]. The row number over
    /// every bar at or before the date read 22,034,626 rows belonging to universe
    /// members, some of them reaching back to 1962, sorted 16.8 million of them to disk
    /// at 236 MB a date, and kept 64 a ticker. **The cost was the whole store per date
    /// rather than the window per date**, which is why the per-date figure barely moved
    /// across the range: 14,641 ms at the median against 15,912 on the last date, the
    /// 248 ms first date being the one before the first membership epoch, where the
    /// universe is empty and there is nothing to join to. The lateral asks each member
    /// for its own last 64 bars through `price_daily_pkey`: 2,849 index scans of 64 rows,
    /// measured warm at 814 ms a date against about 15 seconds.
    ///
    /// **The same rows, not a narrower window.** `row_number() OVER (PARTITION BY ticker
    /// ORDER BY date DESC) &lt;= 64` and `ORDER BY date DESC LIMIT 64` per ticker select
    /// the same bars, `PRIMARY KEY (ticker, date)` admitting no tie in date to break. A
    /// lower bound in calendar days would not have been the same rows: a member with a
    /// gap inside its last 64 sessions would contribute fewer, and the count is what the
    /// `HAVING` and the 63-day chain both test.
    /// </summary>
    private static async Task<string> SectorRelativeStrengthAsync(
        StageContext context, int minMembers, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "price_daily", SectorCompositeSql(context.Date, minMembers), ct).ConfigureAwait(false);

        return SectorRelativeStrength(
            [.. rows.Select(static r => new CompositeReturn(
                r[0] as string,
                DateOnly.FromDateTime((DateTime) r[1]!),
                (double) (decimal) r[2]!))]);
    }

    /// <summary>
    /// One composite's mean member return on one date. <see cref="Sector"/> is null for
    /// the universe composite.
    /// </summary>
    public readonly record struct CompositeReturn(string? Sector, DateOnly Date, double MeanReturn);

    /// <summary>
    /// The statement behind <see cref="SectorRelativeStrengthAsync"/>, public so a test can
    /// run it against a fixture whose answer is worked by hand [5.5.3].
    ///
    /// **Two kinds of row, and the second is what 5.5.3 added** [D-158]. Each sector's mean
    /// member return per date, as before, and the universe's, carried with a null sector:
    /// the mean over **every active member**, as `METRICS.md` defines it, including a name
    /// with no sector and a name in a sector too small to carry a composite of its own.
    /// Until 5.5.3 the universe composite was the mean of the sector means, which weighed
    /// a sector of eight as heavily as a sector of four hundred and left out every member
    /// in no qualifying sector.
    ///
    /// **The member floor applies to the universe's dates as well as the sectors'.**
    /// `market.sector_composite_min_members` is below which a composite is one name's
    /// noise, and a member whose last 64 bars straddle a hole contributes returns on dates
    /// no other member is reading. The sector rows have always had the floor; without it
    /// the universe chain would take those dates at one or two names each.
    /// </summary>
    public static string SectorCompositeSql(DateOnly date, int minMembers)
        => $"""
            WITH universe AS (
                SELECT m.ticker, m.sector FROM {Universe.AsOf(date)} m
                 WHERE m.is_active
            ),
            windowed AS (
                SELECT u.sector, b.ticker, b.date, b.adj_close, b.rn
                FROM universe u
                CROSS JOIN LATERAL (
                    SELECT p.ticker, p.date, p.adj_close,
                           row_number() OVER (ORDER BY p.date DESC) AS rn
                    FROM price_daily p
                    WHERE p.ticker = u.ticker AND p.date <= {Literal(date)}
                      AND p.adj_close IS NOT NULL AND p.adj_close > 0
                    ORDER BY p.date DESC
                    LIMIT {CompositeWindow.ToString(CultureInfo.InvariantCulture)}
                ) b
            ),
            kept AS (
                SELECT sector, ticker, date, adj_close FROM windowed
                WHERE rn <= {CompositeWindow.ToString(CultureInfo.InvariantCulture)}
            ),
            rets AS (
                SELECT sector, date,
                       (adj_close / lag(adj_close) OVER (PARTITION BY ticker ORDER BY date)) - 1 AS r
                FROM kept
            )
            SELECT sector, date, avg(r) AS mean_return
            FROM rets
            WHERE r IS NOT NULL AND sector IS NOT NULL
            GROUP BY sector, date
            HAVING count(*) >= {minMembers.ToString(CultureInfo.InvariantCulture)}
            UNION ALL
            SELECT NULL::text, date, avg(r)
            FROM rets
            WHERE r IS NOT NULL
            GROUP BY date
            HAVING count(*) >= {minMembers.ToString(CultureInfo.InvariantCulture)}
            ORDER BY 1 NULLS FIRST, 2;
            """;

    /// <summary>
    /// The chain and the comparison, over the rows <see cref="SectorCompositeSql"/>
    /// returns. Public and free of any store so the arithmetic can be worked by hand [5.5.3].
    ///
    /// **Each sector's value is its trailing return minus the universe's** [`METRICS.md`,
    /// D-158]: (sector level - 1) - (universe level - 1), which is the difference of the
    /// two levels. Until 5.5.3 it was their ratio less one, a quantity with the same sign
    /// and a different size that no definition names.
    ///
    /// **Each chain is multiplied in date order whatever order the rows arrive in**, so the
    /// floating-point result is the same for any caller rather than for the one statement
    /// that happens to sort them [`CLAUDE.md` section 6].
    /// </summary>
    public static string SectorRelativeStrength(IReadOnlyList<CompositeReturn> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // **One window per comparison: the sector's own 63 most recent dates, ending on the
        // newest date the universe carries, with the universe chained over exactly those**
        // [`METRICS.md`, "over the same window", D-130, 5.5.3]. Chaining every date given
        // compared a 63-session sector return with a longer universe one: a member whose last
        // 64 bars straddle a hole returns a row older than every other member's, and five such
        // members anywhere carry the universe onto that date. Found by review after the first
        // recompute.
        //
        // **The window is the sector's dates and not the universe's, because `price_daily`
        // holds bars on days the exchange was shut** [D-130]. Five of a holiday's 1 to 18 carry
        // the universe onto it while no sector reaches its floor, so a window taken from the
        // universe's dates held a non-session every sector lacked, and the recompute wrote `{}`
        // on 431 of 1,465 dates. Found by the recompute's comparison, corrected before the record.
        //
        // **What this costs until D-130's calendar reaches this stage**: the store cannot tell
        // a non-session from a session on which a sector fell below its floor, so a sector thin
        // on a session inside its window reaches one date further back rather than carrying no
        // value as `METRICS.md` §2 has it. Carried with D-130's obligation [`BUILD_PLAN.md`].
        var universe = new Dictionary<DateOnly, double>();
        var bySector = new Dictionary<string, List<CompositeReturn>>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            if (r.Sector is null)
            {
                universe[r.Date] = r.MeanReturn;
                continue;
            }

            if (!bySector.TryGetValue(r.Sector, out var series))
            {
                bySector[r.Sector] = series = [];
            }

            series.Add(r);
        }

        if (universe.Count == 0)
        {
            return "{}";
        }

        var newest = universe.Keys.Max();

        var buffer = new StringBuilder("{");
        var first = true;

        foreach (var sector in bySector.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var window = bySector[sector]
                .Where(r => r.Date <= newest)
                .OrderByDescending(static r => r.Date)
                .Take(RelativeStrengthWindow)
                .ToList();

            // A sector with no row on the newest date would be compared as of an older one,
            // and one with fewer than 63 dates has no 63-date return. The universe carries
            // every date a sector does, its floor being met by the sector's own members; a
            // hand-built row set that breaks that gets no value rather than a shorter chain.
            if (window.Count < RelativeStrengthWindow
                || window[0].Date != newest
                || window.Exists(r => !universe.ContainsKey(r.Date)))
            {
                continue;
            }

            var level = 1.0;
            var universeLevel = 1.0;

            for (var i = window.Count - 1; i >= 0; i--)
            {
                level *= 1 + window[i].MeanReturn;
                universeLevel *= 1 + universe[window[i].Date];
            }

            if (!first)
            {
                buffer.Append(',');
            }

            first = false;

            buffer.Append(JsonSerializer.Serialize(sector))
                .Append(':')
                .Append((level - universeLevel).ToString("F6", CultureInfo.InvariantCulture));
        }

        return buffer.Append('}').ToString();
    }

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";

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
