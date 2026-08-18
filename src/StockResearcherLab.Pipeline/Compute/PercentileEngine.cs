using System.Globalization;
using System.Text;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Compute;

/// <summary>
/// C11. Every ranked metric becomes a percentile inside its own size bucket by sector
/// cell.
///
/// **Date-partitioned, so each table is one set-based statement** [`ARCHITECTURE.html`
/// §19, which says of this component "cannot partition by ticker"]. A percentile on a
/// given day needs every name in the cell on that day, so the window functions run
/// over the whole date at once. C08 and C09 are the opposite case and read once,
/// compute per ticker and write once.
///
/// **This is an <c>Update</c> of a disjoint column set on a row another stage
/// inserted** [D-77]. The metric engine owns the metric columns and this stage owns
/// the <c>_pctile</c> columns, which is INVARIANT 10 read per operation rather than
/// per table. The failure that arrangement can have is not a conflict the registry
/// catches: it is a metric engine re-running and blanking what this one wrote. That is
/// safe because the staged write builds its staging table from the written columns
/// alone, and `PercentileSplitTests` is what asserts it rather than leaving it to
/// construction.
///
/// **No narrowing** [INVARIANT 1, D-5]. Every active universe member with a row in the
/// source table is ranked. This stage does not take a top slice, apply a floor or drop
/// a bucket; absolute filters live only in the universe definition, and a percentile
/// engine that skipped part of the universe would be one applied after ranking at
/// ingest had already decided what could be discovered.
///
/// The cell rule, the fallback order and the population being ranked are in
/// `METRICS.md` §6.
/// </summary>
public sealed class PercentileEngine : IStage, IBackfillStage
{
    /// <summary>
    /// One source table and the metrics ranked on it. Thirty columns over four
    /// tables.
    ///
    /// <c>base_breakout_flag</c> is the one column with a named reader that is absent,
    /// because a percentile over a two-valued column collapses to two values and
    /// carries nothing the boolean does not. <c>cash_on_hand</c> and
    /// <c>quarterly_burn_rate</c> are levels in dollars sent to the dossier for
    /// magnitude, and <c>last_two_earnings_surprises</c> is an array [`METRICS.md`
    /// §6.5].
    /// </summary>
    public sealed record MetricTable(string Table, IReadOnlyList<string> Metrics)
    {
        /// <summary>The columns this stage owns on that table, in the order the metrics are declared.</summary>
        public IReadOnlyList<string> PercentileColumns { get; } =
            Metrics.Select(m => m + Suffix).ToList();
    }

    /// <summary>Named <c>&lt;metric&gt;_pctile</c> and stored beside the metric it ranks [`SCHEMA.md`].</summary>
    public const string Suffix = "_pctile";

    /// <summary>
    /// The four metric stores, each with the metrics ranked on it.
    ///
    /// <c>median_dollar_volume_20d</c> is here because §07 says every metric in the
    /// fixed core arrives twice, as the raw value and as its percentile, and the
    /// core's identity group names it. A liquidity percentile inside a size and sector
    /// cell also separates a thinly traded name from a heavily traded one among its
    /// actual peers rather than against megacaps.
    /// </summary>
    public static readonly IReadOnlyList<MetricTable> Sources =
    [
        new("indicator_daily",
        [
            "atr_pct", "adx14", "dist_20dma", "dist_200dma", "dist_52w_high",
            "dist_52w_high_20d_change", "rs_change_21d", "rs_change_63d",
            "rs_21d_63d_change", "rs_20d_slope", "rs_change_vs_sector",
            "volume_vs_50d_avg", "ma50_200_slope", "median_dollar_volume_20d",
        ]),
        new("valuation_daily",
        [
            "fcf_yield", "ev_ebit", "ev_ebit_vs_own_5y", "roic", "roic_4q_change",
            "gross_margin_4q_change", "net_debt_ebitda", "accruals",
            "share_count_change", "revenue_growth_4q_trend",
        ]),
        new("flow_daily",
        [
            "insider_net_90d_usd", "distinct_buyer_count", "inst_ownership_change",
        ]),
        new("sentiment_derived_daily",
        [
            "article_count_z_own_90d", "sentiment_delta_7v30", "sentiment_7d_level",
        ]),
    ];

    public string Name => "PercentileEngine";

    /// <summary>
    /// The four stores and <c>security</c>, which supplies the size bucket and the
    /// sector that define a cell.
    ///
    /// The four are declared here as well as written, which
    /// <see cref="DeclaredAccess.CanRead"/> would not require, because
    /// `ARCHITECTURE.html` §3 names them in this component's Reads cell and the
    /// conformance test reads that cell in both directions [D-74, 2.15].
    /// </summary>
    public IReadOnlyList<string> ReadSet { get; } =
    [
        "indicator_daily", "valuation_daily", "flow_daily", "sentiment_derived_daily",
        "security_daily",
    ];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        Sources
            .Select(s => new TableWrite(s.Table, WriteOperation.Update, s.PercentileColumns))
            .ToList();

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var minMembers = (int) await LongAsync(context, "percentile.cell_min_members", ct)
            .ConfigureAwait(false);

        long written = 0;
        var report = new List<(MetricTable Source, string Metric, Fallback Counts)>();

        foreach (var source in Sources)
        {
            written += await context.Data.WriteAsync(
                source.Table, WriteOperation.Update,
                UpdateSql(source, context.Date, minMembers),
                parameters: null, ct).ConfigureAwait(false);

            var counts = await FallbackCountsAsync(context, source, minMembers, ct).ConfigureAwait(false);

            // Walked in the declared metric order rather than in the order the
            // statement happened to return, so the run log line is byte-identical
            // across two runs of the same date [CLAUDE.md section 6].
            foreach (var metric in source.Metrics)
            {
                report.Add((source, metric, counts.GetValueOrDefault(metric)));
            }
        }

        return new StageResult(written, "ok", Detail(report, minMembers));
    }

    // ------------------------------------------------- range mode [3.15] ---

    /// <summary>
    /// The same work as <see cref="ExecuteAsync"/> over a range, one date at a time.
    ///
    /// **Date-partitioned, and this is the component the partition rule was written
    /// for** [`CLAUDE.md` §5]. A percentile on a day needs every name in the cell on
    /// that day, so there is no ticker to partition by and nothing to hoist out of the
    /// loop: each date's ranking is a closed question over that date's rows.
    ///
    /// **The statement is reissued per date rather than widened across the range, and
    /// here that is not only the arithmetic argument.** <see cref="UpdateSql"/> ranks
    /// within `(size_bucket, sector)` on one date; ranking across a range means adding
    /// the date to every partition clause, which is the same statement only if nothing
    /// else changes, and it is a different statement the first time a cell's membership
    /// moves. The nightly form is also the one 2.10's reference fixtures cover, and this
    /// phase does not reimplement arithmetic for the backfill [D-93].
    ///
    /// **What this costs is stated rather than estimated.** It is one UPDATE per source
    /// table per date, which over this phase's window is four statements times the
    /// trading dates in the range. `BUILD_PLAN.md` calls 3.15 the checkpoint that decides
    /// whether the phase meets its timing line, and the figure that decides it is the
    /// wall clock of a real range run rather than anything assertable here; migration
    /// `0007` already names partitioning as what that finding would recommend.
    ///
    /// **The fallback counts are summed across the range rather than reported per date.**
    /// A per-date line over 1,260 dates is not a run log line anybody reads, and the
    /// question §6.6 asks is which metrics fell back and how often, which sums.
    /// </summary>
    public async Task<BackfillResult> ExecuteRangeAsync(
        BackfillContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Everything that is not the date loop, named [item 43]. Here that is the
        // calendar read alone, the ranking UPDATEs being inside the loop.
        var phases = new PhaseTimer();

        var dates = await context.SessionsAsync(ct).ConfigureAwait(false);
        phases.Mark("calendar");

        if (dates.Count == 0)
        {
            return BackfillResult.Completed(
                0, context.To, "no trading date in the range carries a price_daily bar, so nothing is ranked");
        }

        long written = 0;

        // Keyed on the resolved version, so a range whose config never moved reads the
        // key once and one that moved reads it again at the boundary [C08's precedent].
        var byVersion = new Dictionary<int, int>();

        // Summed across the range, per (table, metric), in the declared order rather than
        // in the order a dictionary enumerates [CLAUDE.md section 6].
        var totals = new Dictionary<(string Table, string Metric), Fallback>();

        var floorSeen = 0;

        // The unit is a date, this loop being date-partitioned, and it covers the ranking
        // UPDATE per source table plus the fallback count that follows it [3.17]. This is
        // the checkpoint `BUILD_PLAN.md` calls the one deciding the phase's timing line,
        // so the distribution rather than the total is what it is measured on [3.15].
        var elapsed = new List<long>();

        foreach (var date in dates)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            var stage = await context.ForDateAsync(date, ct).ConfigureAwait(false);

            if (!byVersion.TryGetValue(stage.ConfigVersion, out var minMembers))
            {
                minMembers = (int) await LongAsync(stage, "percentile.cell_min_members", ct)
                    .ConfigureAwait(false);
                byVersion[stage.ConfigVersion] = minMembers;
            }

            floorSeen = minMembers;

            foreach (var source in Sources)
            {
                written += await stage.Data.WriteAsync(
                    source.Table, WriteOperation.Update,
                    UpdateSql(source, date, minMembers),
                    parameters: null, ct).ConfigureAwait(false);

                var counts = await FallbackCountsAsync(stage, source, minMembers, ct).ConfigureAwait(false);

                foreach (var metric in source.Metrics)
                {
                    var c = counts.GetValueOrDefault(metric);
                    var key = (source.Table, metric);

                    totals[key] = totals.TryGetValue(key, out var running)
                        ? new Fallback(
                            running.InCell + c.InCell,
                            running.InBucket + c.InBucket,
                            running.Unranked + c.Unranked,
                            running.ThinCells + c.ThinCells)
                        : c;
                }
            }

            elapsed.Add((long) System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var report = new List<(MetricTable Source, string Metric, Fallback Counts)>();

        foreach (var source in Sources)
        {
            foreach (var metric in source.Metrics)
            {
                report.Add((source, metric, totals.GetValueOrDefault((source.Table, metric))));
            }
        }

        return BackfillResult.Completed(
            written, context.To,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0:N0} trading date(s). {1} The counts are summed over the range rather than " +
                "per date, so a metric that fell back on one date in twelve hundred is visible " +
                "as a number rather than lost in a line nobody reads [METRICS.md 6.6]. {2} {3}",
                dates.Count, Detail(report, floorSeen), RangeTiming.Describe("date", elapsed),
                phases.Describe()));
    }

    /// <summary>
    /// One table's percentiles, as one statement.
    ///
    /// **The rank is taken over the non-null population, which is what makes the
    /// window expression longer than <c>PERCENT_RANK</c>.** Postgres counts every row
    /// in a partition toward <c>PERCENT_RANK</c>'s denominator, nulls included, so a
    /// cell of twenty carrying five values would produce a percentile computed over
    /// five that looks exactly like one computed over twenty and nothing downstream
    /// could tell the difference. <c>(rank() - 1) / (count(metric) - 1)</c> is
    /// <c>PERCENT_RANK</c> over the values alone: nulls sort last under the default
    /// ordering, so they never take a rank a value wanted, and <c>count</c> of a
    /// column skips them [`METRICS.md` §6.2].
    ///
    /// <c>rank()</c> rather than <c>row_number()</c> gives tied values the same
    /// percentile, which removes the tie-break entirely and with it the determinism
    /// hazard a tie-break would introduce [`METRICS.md` §6.3].
    ///
    /// **The join to <c>security</c> is a LEFT JOIN and that is deliberate.** Every
    /// row for the date is rewritten, so a row outside the active universe is set to
    /// null rather than keeping whatever an earlier run left there. C09 writes a wider
    /// set of tickers than the universe and those rows are the case.
    /// </summary>
    public static string UpdateSql(MetricTable source, DateOnly date, int minMembers)
    {
        ArgumentNullException.ThrowIfNull(source);

        var d = Literal(date);

        var sets = string.Join(",\n            ",
            source.Metrics.Select(m => $"{m}{Suffix} = r.{m}{Suffix}"));

        var projected = string.Join(",\n                   ",
            source.Metrics.Select(m => $"{Case(m, minMembers)} AS {m}{Suffix}"));

        return $"""
            UPDATE {source.Table} AS t
            SET {sets}
            FROM (
                SELECT w.ticker,
                       {projected}
                FROM (
                    {Windowed(source, d)}
                ) w
            ) r
            WHERE t.ticker = r.ticker AND t.date = {d};
            """;
    }

    /// <summary>
    /// The counts §6.6 requires in the run log, per metric, over the same predicates
    /// the update uses.
    ///
    /// Built from <see cref="CellQualifies"/> and <see cref="BucketQualifies"/> rather
    /// than from a second copy of them, so a report that says the fallback did not
    /// fire cannot disagree with an update where it did. A metric that is null for a
    /// reason nobody counted is a metric nobody notices going null.
    /// </summary>
    public static string FallbackReportSql(MetricTable source, DateOnly date, int minMembers)
    {
        ArgumentNullException.ThrowIfNull(source);

        var arms = source.Metrics.Select(m =>
        {
            var valued = $"w.{m}_v IS NOT NULL AND w.size_bucket IS NOT NULL";
            var cell = CellQualifies(m, minMembers);
            var bucket = BucketQualifies(m, minMembers);

            return $"""
                SELECT '{m}' AS metric,
                       count(*) FILTER (WHERE {valued} AND {cell}) AS in_cell,
                       count(*) FILTER (WHERE {valued} AND NOT ({cell}) AND {bucket}) AS in_bucket,
                       count(*) FILTER (WHERE {valued} AND NOT ({cell}) AND NOT ({bucket})) AS unranked,
                       count(DISTINCT (w.size_bucket, w.sector)) FILTER (
                           WHERE w.size_bucket IS NOT NULL AND w.sector IS NOT NULL
                             AND w.{m}_cell_n < {minMembers.ToString(CultureInfo.InvariantCulture)}) AS thin_cells
                FROM w
                """;
        });

        return $"""
            WITH w AS (
                {Windowed(source, Literal(date))}
            )
            {string.Join("\nUNION ALL\n", arms)};
            """;
    }

    /// <summary>
    /// The shared inner query: every row of the source table for the date, with the
    /// cell it belongs to and, per metric, the two populations and the two ranks.
    /// </summary>
    private static string Windowed(MetricTable source, string dateLiteral)
    {
        var terms = string.Join(",\n                       ",
            source.Metrics.SelectMany(m => new[]
            {
                $"m.{m} AS {m}_v",
                $"count(m.{m}) OVER (PARTITION BY s.size_bucket, s.sector) AS {m}_cell_n",
                $"count(m.{m}) OVER (PARTITION BY s.size_bucket) AS {m}_bucket_n",
                $"rank() OVER (PARTITION BY s.size_bucket, s.sector ORDER BY m.{m}) AS {m}_cell_r",
                $"rank() OVER (PARTITION BY s.size_bucket ORDER BY m.{m}) AS {m}_bucket_r",
            }));

        return $"""
            SELECT m.ticker, s.size_bucket, s.sector,
                       {terms}
                    FROM {source.Table} m
                    LEFT JOIN {Universe.AsOf(dateLiteral)} s ON s.ticker = m.ticker AND s.is_active
                    WHERE m.date = {dateLiteral}
            """;
    }

    /// <summary>
    /// The fallback, in the order `METRICS.md` §6.4 gives it, as one expression.
    ///
    /// Size bucket by sector where the non-null population clears the floor, size
    /// bucket alone where it does not, and null where the bucket does not clear it
    /// either. **Never ranked across buckets**: ranking a $1B name against megacaps is
    /// the exact thing D-10 exists to prevent, and a fallback that did it would be
    /// worse than no value.
    /// </summary>
    private static string Case(string metric, int minMembers)
        => $"""
            CASE
                           WHEN w.{metric}_v IS NULL OR w.size_bucket IS NULL THEN NULL
                           WHEN {CellQualifies(metric, minMembers)}
                               THEN {Scaled(metric, "cell")}
                           WHEN {BucketQualifies(metric, minMembers)}
                               THEN {Scaled(metric, "bucket")}
                       END
            """;

    /// <summary>
    /// **A null sector goes straight to the bucket-only fallback and does not form a
    /// cell of its own.** Postgres groups nulls together in a <c>PARTITION BY</c>, so
    /// without this clause every name whose sector lookup failed would be ranked
    /// against the other names whose sector lookup failed, under a column that says
    /// sector. C01 writes null rather than a guess on an HTTP failure and its comment
    /// already anticipates this [`METRICS.md` §6.4].
    /// </summary>
    private static string CellQualifies(string metric, int minMembers)
        => $"w.sector IS NOT NULL AND w.{metric}_cell_n >= {minMembers.ToString(CultureInfo.InvariantCulture)}";

    private static string BucketQualifies(string metric, int minMembers)
        => $"w.{metric}_bucket_n >= {minMembers.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Scaled 0 to 100 and stored as <c>real</c>. `WORKED_EXAMPLE.md` prints 84 and 12
    /// and calls them top and bottom quintile, so the scale is decided and top
    /// quintile is at or above 80.
    ///
    /// A population of one has no spread to place a value inside, and
    /// <c>PERCENT_RANK</c> is zero there by its own definition. It is unreachable
    /// while the floor is above one and is written out rather than left to divide by
    /// zero if the floor ever moves.
    /// </summary>
    private static string Scaled(string metric, string scope)
        => $"(CASE WHEN w.{metric}_{scope}_n > 1 " +
           $"THEN (w.{metric}_{scope}_r - 1) * 100.0 / (w.{metric}_{scope}_n - 1) ELSE 0 END)::real";

    /// <summary>Rows ranked in their sector cell, in their bucket alone, and left null; and cells that fell back.</summary>
    public readonly record struct Fallback(long InCell, long InBucket, long Unranked, long ThinCells);

    private static async Task<IReadOnlyDictionary<string, Fallback>> FallbackCountsAsync(
        StageContext context, MetricTable source, int minMembers, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            source.Table, FallbackReportSql(source, context.Date, minMembers), ct).ConfigureAwait(false);

        var counts = new Dictionary<string, Fallback>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            counts[(string) r[0]!] = new Fallback(
                Convert.ToInt64(r[1], CultureInfo.InvariantCulture),
                Convert.ToInt64(r[2], CultureInfo.InvariantCulture),
                Convert.ToInt64(r[3], CultureInfo.InvariantCulture),
                Convert.ToInt64(r[4], CultureInfo.InvariantCulture));
        }

        return counts;
    }

    /// <summary>
    /// The run log line.
    ///
    /// Totals first, then every metric where the sector cell was not enough or where
    /// nothing was ranked at all. Naming those two cases rather than all thirty keeps
    /// the line readable while making the two that matter impossible to miss: a metric
    /// nothing ranked is a metric no screen can use that night [`METRICS.md` §6.6].
    /// </summary>
    private static string Detail(
        IReadOnlyList<(MetricTable Source, string Metric, Fallback Counts)> report, int minMembers)
    {
        var line = new StringBuilder();

        line.AppendFormat(
            CultureInfo.InvariantCulture,
            "{0:N0} metric(s) over {1:N0} table(s), floor {2:N0} non-null member(s) per metric. " +
            "Ranked in a sector cell {3:N0}, in the size bucket alone {4:N0}, null on a thin bucket {5:N0}",
            report.Count,
            Sources.Count,
            minMembers,
            report.Sum(r => r.Counts.InCell),
            report.Sum(r => r.Counts.InBucket),
            report.Sum(r => r.Counts.Unranked));

        var notable = report
            .Where(r => r.Counts.ThinCells > 0 || r.Counts.Unranked > 0
                        || (r.Counts.InCell == 0 && r.Counts.InBucket == 0))
            .ToList();

        if (notable.Count == 0)
        {
            return line.Append(". Every cell cleared the floor for every metric").ToString();
        }

        line.Append(". By metric, where the sector cell was not enough or nothing was ranked: ");

        var first = true;

        foreach (var (source, metric, counts) in notable)
        {
            if (!first)
            {
                line.Append("; ");
            }

            first = false;

            line.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0}.{1} {2:N0} thin cell(s), {3:N0} in bucket, {4:N0} null",
                source.Table, metric, counts.ThinCells, counts.InBucket, counts.Unranked);
        }

        return line.ToString();
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
}
