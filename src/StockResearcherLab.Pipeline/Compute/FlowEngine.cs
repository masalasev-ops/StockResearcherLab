using System.Globalization;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Compute;

/// <summary>
/// C34. Two source tables at their own natural grain become three daily metrics at
/// ticker by day. Ingest grain follows the source, consumption grain follows the
/// screen [D-61].
///
/// One statement over the whole date rather than a loop over tickers. The partition
/// key here is the date, not the ticker, because every ticker's row for one date is
/// computed from the same two windows [CLAUDE.md section 5].
///
/// **A row is written only where the ticker's flow history is actually in the
/// store**, because a ticker that has never been through the rotation and a ticker
/// with genuinely no insider buying are different facts and a zero cannot tell them
/// apart. Absence of a row means unknown; a zero in a row means measured and zero
/// [CLAUDE.md section 6].
/// </summary>
public sealed class FlowEngine : IStage, IBackfillStage
{
    public static readonly string[] FlowColumns =
        ["ticker", "date", "insider_net_90d_usd", "distinct_buyer_count", "inst_ownership_change"];

    /// <summary>
    /// The trailing window, in days. A constant rather than a config key: the
    /// column is named <c>insider_net_90d_usd</c> in <c>ARCHITECTURE.html</c>
    /// sections 3 and 5 and in D-61, so a tunable 90 would be a second place for
    /// the number to live and the column name would be wrong the first time they
    /// disagreed.
    /// </summary>
    public const int WindowDays = 90;

    public string Name => "FlowEngine";

    public IReadOnlyList<string> ReadSet { get; } = ["insider_transaction", "institutional_holding"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("flow_daily", WriteOperation.Insert, FlowColumns)];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var lagDays = (int) await LongAsync(context, "flow.institutional_report_lag_days", ct).ConfigureAwait(false);

        var written = await context.Data.WriteAsync(
            "flow_daily", WriteOperation.Insert, Sql,
            new Dictionary<string, object?>
            {
                ["d"] = context.Date,
                ["window_days"] = WindowDays,
                ["lag_days"] = lagDays,
            },
            ct).ConfigureAwait(false);

        var unknown = await UnknownCountsAsync(context, lagDays, ct).ConfigureAwait(false);

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} flow_daily row(s) for {1:yyyy-MM-dd} over a {2:N0} day insider window. " +
            "{3:N0} ticker(s) have an unpriced or unattributed open-market row and carry null rather than " +
            "an understated total; {4:N0} have a second institutional report date and therefore a change " +
            "to compute, the rest null because the source is a snapshot rather than a series [D-69]",
            written, context.Date, WindowDays, unknown.Unpriced, unknown.WithTwoReports);

        return new StageResult(written, "ok", detail);
    }

    // ------------------------------------------------- range mode [3.14] ---

    /// <summary>
    /// The same work as <see cref="ExecuteAsync"/> over a range, one date at a time.
    ///
    /// **The statement is reissued per date rather than generalised across the range,
    /// and that is a decision rather than the easy path.** <see cref="Sql"/> is one
    /// derivation with two point-in-time filters, a ninety-day trailing window and a
    /// two-report lookback, and every one of those is expressed relative to `@d`. Widening
    /// it to a range means cross joining each of those CTEs against a date set, which is a
    /// rewrite of the arithmetic rather than a rewrite of its bounds. This phase's whole
    /// position is that the arithmetic is not reimplemented for the backfill, because that
    /// is what keeps 2.6's reference fixtures covering both paths rather than one
    /// [D-93, C08 and C09's precedent].
    ///
    /// **What that costs is one statement per date instead of one per range**, and the
    /// per-date statement is the one already indexed for the nightly path. The checkpoint
    /// asks for the two shapes measured against each other over one month and the faster
    /// taken; that measurement needs a populated `insider_transaction`, which 3.9's sweep
    /// has not yet produced, so it is recorded as owed rather than guessed at.
    ///
    /// **Config resolves per date and not once**, because `flow.institutional_report_lag_days`
    /// decides which 13F is readable on a backfilled date and is therefore parametric
    /// rather than operational [INVARIANT 13, D-43].
    /// </summary>
    public async Task<BackfillResult> ExecuteRangeAsync(
        BackfillContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var atEnd = await context.ForDateAsync(context.To, ct).ConfigureAwait(false);

        var dates = await context.SessionsAsync(ct).ConfigureAwait(false);

        if (dates.Count == 0)
        {
            return BackfillResult.Completed(
                0, context.To, "no trading date in the range carries a price_daily bar, so nothing is computed");
        }

        long written = 0;
        var datesWithRows = 0;

        // Keyed on the resolved version, so a range whose config never moved reads the
        // key once and one that moved reads it again at the boundary.
        var byVersion = new Dictionary<int, int>();

        // The unit is a date, this loop being date-partitioned, and it covers the write
        // as well as the compute: the statement is one INSERT per date [3.17].
        var elapsed = new List<long>();

        foreach (var date in dates)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            var stage = await context.ForDateAsync(date, ct).ConfigureAwait(false);

            if (!byVersion.TryGetValue(stage.ConfigVersion, out var lagDays))
            {
                lagDays = (int) await LongAsync(stage, "flow.institutional_report_lag_days", ct)
                    .ConfigureAwait(false);
                byVersion[stage.ConfigVersion] = lagDays;
            }

            var rows = await stage.Data.WriteAsync(
                "flow_daily", WriteOperation.Insert, Sql,
                new Dictionary<string, object?>
                {
                    ["d"] = date,
                    ["window_days"] = WindowDays,
                    ["lag_days"] = lagDays,
                },
                ct).ConfigureAwait(false);

            written += rows;

            if (rows > 0)
            {
                datesWithRows++;
            }

            elapsed.Add((long) System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return BackfillResult.Completed(
            written, context.To,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0:N0} flow_daily row(s) over {1:N0} trading date(s), of which {2:N0} carried at least " +
                "one row. A date with none is a date no ticker had a visible filing for, which is an " +
                "ordinary early-window fact rather than a gap. The {3:N0} day insider window and the " +
                "institutional lag are applied per date, so a backfilled row reads only what was public " +
                "on it [INVARIANT 12's shape]. {4}",
                written, dates.Count, datesWithRows, WindowDays,
                RangeTiming.Describe("date", elapsed)));
    }

    /// <summary>
    /// The whole derivation, as one statement.
    ///
    /// **Point in time is two separate filters and not one** [INVARIANT 12's shape,
    /// applied to flow]. <c>transaction_date</c> is when the insider traded and is
    /// what the ninety-day window means; <c>filed_at</c> is when the filing became
    /// public and is what decides whether this system could have known. Windowing
    /// on <c>filed_at</c> alone would misdate the signal and windowing on
    /// <c>transaction_date</c> alone would read filings up to two business days
    /// before they existed. A null <c>filed_at</c> is excluded rather than assumed
    /// public, which is the conservative direction.
    ///
    /// <c>institutional_holding.report_date</c> is a period end, not a filing date,
    /// and a 13F is due within forty-five days of it. Reading on
    /// <c>report_date &lt;= date</c> would be the exact mistake INVARIANT 12 names
    /// for fundamentals, so the lag is subtracted and it is configuration rather
    /// than a literal.
    /// </summary>
    public const string Sql = """
        INSERT INTO flow_daily (ticker, date, insider_net_90d_usd, distinct_buyer_count, inst_ownership_change)
        WITH visible AS (
            SELECT ticker,
                   transaction_code,
                   acquired_or_disposed,
                   transaction_date,
                   COALESCE(total_value, shares_amount * price_per_share) AS usd,
                   COALESCE(reporting_owner_cik, reporting_owner_name)    AS owner
            FROM insider_transaction
            WHERE filed_at IS NOT NULL
              AND filed_at <= @d
        ),
        in_window AS (
            -- P is an open-market purchase and S an open-market sale, both a
            -- person deciding to trade at the market price. Everything else is
            -- excluded by what it is rather than by what it is worth: A is an
            -- award, M an option exercise, F shares withheld for tax, G a gift.
            -- The S4 rubric disqualifies option exercises and scheduled plan
            -- activity, so a net dollar figure pooling awards with purchases is
            -- not the figure the screen reads [D-61, SCHEMA.md].
            SELECT * FROM visible
            WHERE transaction_code IN ('P','S')
              AND transaction_date IS NOT NULL
              AND transaction_date >  @d - @window_days
              AND transaction_date <= @d
        ),
        insider AS (
            SELECT c.ticker,
                   -- Null rather than an understated total. A P or S row with no
                   -- dollar value cannot be added, and dropping it from the sum is
                   -- adding zero, which is a value that means something else.
                   CASE WHEN COUNT(w.ticker) FILTER (WHERE w.usd IS NULL) > 0 THEN NULL
                        ELSE COALESCE(SUM(CASE WHEN w.acquired_or_disposed = 'A' THEN w.usd
                                               WHEN w.acquired_or_disposed = 'D' THEN -w.usd
                                          END), 0)
                   END AS net_usd,
                   -- Same rule for an unattributable buyer: COUNT DISTINCT skips
                   -- nulls silently, so a filing with neither a CIK nor a name
                   -- would lower the count without saying so.
                   CASE WHEN COUNT(w.ticker) FILTER (WHERE w.transaction_code = 'P'
                                                       AND w.owner IS NULL) > 0 THEN NULL
                        ELSE COUNT(DISTINCT w.owner) FILTER (WHERE w.transaction_code = 'P')
                   END AS buyers
            FROM (SELECT DISTINCT ticker FROM visible) c
            LEFT JOIN in_window w ON w.ticker = c.ticker
            GROUP BY c.ticker
        ),
        holdings AS (
            SELECT ticker,
                   report_date,
                   CASE WHEN COUNT(*) FILTER (WHERE shares IS NULL) > 0 THEN NULL
                        ELSE SUM(shares)
                   END AS total
            FROM institutional_holding
            WHERE report_date <= @d - @lag_days
            GROUP BY ticker, report_date
        ),
        ranked AS (
            SELECT ticker, report_date, total,
                   ROW_NUMBER() OVER (PARTITION BY ticker ORDER BY report_date DESC) AS rn
            FROM holdings
            WHERE total IS NOT NULL
        ),
        inst AS (
            SELECT a.ticker,
                   CASE WHEN b.total > 0
                        THEN ((a.total - b.total) / b.total)::real
                   END AS change
            FROM ranked a
            JOIN ranked b ON b.ticker = a.ticker AND b.rn = 2
            WHERE a.rn = 1
        ),
        tickers AS (
            SELECT ticker FROM insider
            UNION
            SELECT ticker FROM ranked
        )
        SELECT t.ticker, @d, i.net_usd, i.buyers, n.change
        FROM tickers t
        LEFT JOIN insider i ON i.ticker = t.ticker
        LEFT JOIN inst    n ON n.ticker = t.ticker
        ORDER BY t.ticker
        ON CONFLICT (ticker, date) DO UPDATE SET
            insider_net_90d_usd  = EXCLUDED.insider_net_90d_usd,
            distinct_buyer_count = EXCLUDED.distinct_buyer_count,
            inst_ownership_change = EXCLUDED.inst_ownership_change;
        """;

    /// <summary>
    /// What the run log says about the two populations that carry null. Read back
    /// rather than inferred, because a metric that is null for a reason nobody
    /// counted is a metric nobody notices going null.
    /// </summary>
    private static async Task<(long Unpriced, long WithTwoReports)> UnknownCountsAsync(
        StageContext context, int lagDays, CancellationToken ct)
    {
        var d = Literal(context.Date);
        var window = WindowDays.ToString(CultureInfo.InvariantCulture);
        var lag = lagDays.ToString(CultureInfo.InvariantCulture);

        var unpriced = await context.Data.ReadAsync(
            "insider_transaction",
            $"""
            SELECT count(DISTINCT ticker)
            FROM insider_transaction
            WHERE filed_at IS NOT NULL AND filed_at <= {d}
              AND transaction_code IN ('P','S')
              AND transaction_date IS NOT NULL
              AND transaction_date > {d} - {window}
              AND transaction_date <= {d}
              AND (COALESCE(total_value, shares_amount * price_per_share) IS NULL
                   OR (transaction_code = 'P'
                       AND COALESCE(reporting_owner_cik, reporting_owner_name) IS NULL));
            """,
            ct).ConfigureAwait(false);

        var reports = await context.Data.ReadAsync(
            "institutional_holding",
            $"""
            SELECT count(*) FROM (
                SELECT ticker
                FROM institutional_holding
                WHERE report_date <= {d} - {lag}
                GROUP BY ticker
                HAVING count(DISTINCT report_date) >= 2
            ) s;
            """,
            ct).ConfigureAwait(false);

        return (Convert.ToInt64(unpriced[0][0], CultureInfo.InvariantCulture),
                Convert.ToInt64(reports[0][0], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A date as a SQL literal. <see cref="IStageData.ReadAsync"/> takes no
    /// parameters, and a date formatted invariantly from a <see cref="DateOnly"/>
    /// cannot carry anything but digits and hyphens [CLAUDE.md section 6].
    /// </summary>
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
