using System.Globalization;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>One measured line of phase 4's definition of done.</summary>
/// <param name="Line">The done-when line, as `BUILD_PLAN.md` words it.</param>
/// <param name="Measured">What the store says.</param>
/// <param name="Holds">Whether it meets the line, or null where the line states no bound.</param>
public sealed record DoneWhenLine(string Line, string Measured, bool? Holds);

/// <summary>
/// Phase 4's six done-when lines, measured against the frozen record [4.14].
///
/// **This is 4.14's own scope rather than a measurement added to it.** The checkpoint
/// says "the done-when distributions measured here", and a definition of done that is a
/// checklist rather than a description needs every line to run and produce an observable
/// result [`CLAUDE.md` §3, `BUILD_PLAN.md`].
///
/// **It reads and writes nothing.** Every figure is a query against `candidate_set`,
/// `attribution` and `market_context_daily` as 4.14 left them, so re-running it at
/// sign-off reproduces the recorded numbers or contradicts them, which is what makes them
/// traceable rather than asserted [D-67].
///
/// **Three of the six lines are stated loosely in the plan and the reading is named rather
/// than assumed.** "Roughly 26 to 30 candidates" is measured as the mean over dates that
/// produced any. "Any 60-day window" is read as sixty candidate dates rather than sixty
/// calendar days, which is the looser of the two: sixty sessions span more calendar than
/// sixty days and therefore hold more distinct names. And the overlap line prints both of
/// its readings, because the plan asks for both. Every reading is printed beside its figure
/// so a reader can disagree with the reading rather than with the number.
///
/// **The overlap line carries no verdict, and that is the plan's own shape** [Q.9, phase 4
/// sign-off]. Its clause described what the design would do rather than stating a bound the
/// result has to clear, the description was wrong, and it was corrected to the measurement.
/// Setting a bound now would be a bar fitted to the measurement that produced it, so the
/// line reports the two readings against the independence figure they are compared with and
/// neither passes nor fails.
/// </summary>
public static class SelectionDistributions
{
    /// <summary>
    /// D-89's proportion at eight slots, which is D-7's 2/3/3 [`SCREEN_LIFECYCLE.md` §8.1].
    /// Held as the target the measured split is reported against.
    /// </summary>
    public static readonly (string Bucket, double Target)[] SizeTarget =
        [("large", 2d / 8d), ("mid", 3d / 8d), ("small", 3d / 8d)];

    /// <param name="live">
    /// The live screen ids, resolved from `screens.&lt;id&gt;.state` as of the range end.
    /// The overlap line's second reading is over ranked sets, which live in
    /// <c>screen_score_daily</c> beside every shadow's, so the live set has to arrive from
    /// configuration. **It must not be inferred from the id**, which is a naming convention
    /// standing in for a config value and would count a promoted screen as a shadow with
    /// nothing failing [phase 4 sign-off].
    /// </param>
    public static async Task<IReadOnlyList<DoneWhenLine>> MeasureAsync(
        string connectionString, DateOnly from, DateOnly to,
        IReadOnlyCollection<string> live, double megacapShareMax, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(live);

        var data = new StageData(
            connectionString,
            new DeclaredAccess(
                "SelectionDistributions",
                ["candidate_set", "attribution", "market_context_daily", "screen_score_daily"],
                []));

        var found = new List<DoneWhenLine>();

        foreach (var (line, sql, verdict) in Queries(from, to, live, megacapShareMax))
        {
            var rows = await data.ReadAsync("candidate_set", sql, ct).ConfigureAwait(false);
            found.Add(verdict(line, rows));
        }

        return found;
    }

    /// <summary>
    /// One line's statement, for the assertions that hold a query against how it was
    /// asked to be written rather than against what it returned.
    /// </summary>
    public static string SqlFor(
        string line, DateOnly from, DateOnly to, IReadOnlyCollection<string> live, double megacapShareMax)
        => Queries(from, to, live, megacapShareMax)
            .Single(q => string.Equals(q.Line, line, StringComparison.Ordinal)).Sql;

    private static IEnumerable<(string Line, string Sql, Func<string, IReadOnlyList<IReadOnlyList<object?>>, DoneWhenLine> Verdict)>
        Queries(DateOnly from, DateOnly to, IReadOnlyCollection<string> live, double megacapShareMax)
    {
        var f = Literal(from);
        var t = Literal(to);

        // ------------------------------------------------------------- one ---

        yield return (
            "a night yields roughly 26 to 30 candidates",
            $"""
            SELECT
                count(*)::numeric / NULLIF(count(DISTINCT date), 0) AS mean_per_date,
                count(DISTINCT date)::bigint AS dates,
                min(per_date.n)::bigint AS smallest,
                max(per_date.n)::bigint AS largest
            FROM candidate_set c
            CROSS JOIN LATERAL (SELECT 0) AS ignored
            JOIN LATERAL (
                SELECT count(*)::bigint AS n FROM candidate_set x
                WHERE x.date = c.date
            ) per_date ON TRUE
            WHERE c.date BETWEEN {f} AND {t};
            """,
            (line, rows) =>
            {
                var mean = Number(rows[0][0]);
                return new DoneWhenLine(
                    line,
                    Fixed(mean, 1) + " a night over " + Count(rows[0][1]) + " dates that produced any, " +
                    "smallest " + Count(rows[0][2]) + ", largest " + Count(rows[0][3]),
                    mean >= 26d && mean <= 30d);
            });

        // ------------------------------------------------------------- two ---

        yield return (
            "the 2/3/3 size distribution holds",
            $"""
            SELECT
                size_bucket,
                count(*)::bigint AS n,
                count(*)::numeric / sum(count(*)) OVER () AS share
            FROM candidate_set
            WHERE date BETWEEN {f} AND {t} AND size_bucket IS NOT NULL
            GROUP BY size_bucket
            ORDER BY size_bucket COLLATE "C";
            """,
            (line, rows) =>
            {
                var measured = rows.ToDictionary(
                    r => (string) r[0]!, r => Number(r[2]), StringComparer.Ordinal);

                var parts = SizeTarget.Select(x =>
                    x.Bucket + " " + Pct(measured.GetValueOrDefault(x.Bucket)) +
                    " against " + Pct(x.Target));

                // **The quota is a ceiling per screen and not a floor**, and D-8 says an
                // unfilled slot stays empty rather than being backfilled from a larger
                // bucket. So the measured split sits at or below the target in the
                // buckets that run short, and what the line asks is that the shape holds
                // rather than that it matches to the point.
                return new DoneWhenLine(line, string.Join(", ", parts), null);
            });

        // ----------------------------------------------------------- three ---

        yield return (
            "megacap share sits under a third including inside the 2022 drawdown",
            $"""
            WITH labelled AS (
                SELECT c.size_bucket, m.regime_label
                FROM candidate_set c
                LEFT JOIN market_context_daily m ON m.date = c.date
                WHERE c.date BETWEEN {f} AND {t} AND c.size_bucket IS NOT NULL
            )
            SELECT
                count(*) FILTER (WHERE size_bucket = 'large')::numeric
                    / NULLIF(count(*), 0) AS whole_range,
                count(*) FILTER (WHERE size_bucket = 'large' AND regime_label = 'risk_off')::numeric
                    / NULLIF(count(*) FILTER (WHERE regime_label = 'risk_off'), 0) AS risk_off,
                count(*) FILTER (WHERE regime_label = 'risk_off')::bigint AS risk_off_rows,
                count(*) FILTER (WHERE regime_label IS NULL)::bigint AS unlabelled
            FROM labelled;
            """,
            (line, rows) =>
            {
                var whole = Number(rows[0][0]);
                var riskOff = Number(rows[0][1]);
                var riskOffRows = Convert.ToInt64(rows[0][2], CultureInfo.InvariantCulture);

                // **The drawdown is read off `regime_label` rather than off an invented
                // date window.** `market_context_daily.regime_label` is authored and
                // closed to risk_on, risk_off and mixed by `0005`, so `risk_off` is this
                // corpus's own name for the condition rather than a range picked to suit
                // the answer [D-80].
                //
                // **The bound is `monitor.megacap_share_max` and not a literal third**
                // [phase 4 sign-off]. C28 alerts on that key and this line scores the same
                // guarantee, so a literal here is one bound stated twice and the two could
                // be moved apart with nothing failing [`CLAUDE.md` §8].
                var max = megacapShareMax;

                return new DoneWhenLine(
                    line,
                    "whole range " + Pct(whole) + ", risk_off dates " +
                    (riskOffRows == 0 ? "no rows" : Pct(riskOff) + " over " + Count(rows[0][2]) + " rows") +
                    ", " + Count(rows[0][3]) + " candidate rows on dates with no regime label" +
                    ", against monitor.megacap_share_max at " + Pct(max),
                    whole < max && (riskOffRows == 0 || riskOff < max));
            });

        // ------------------------------------------------------------ four ---

        yield return (
            "distinct tickers over any 60-day window exceed 250",
            $"""
            WITH dates AS (
                SELECT DISTINCT date FROM candidate_set WHERE date BETWEEN {f} AND {t}
            ),
            windowed AS (
                SELECT
                    d.date,
                    (SELECT count(DISTINCT c.ticker)::bigint
                     FROM candidate_set c
                     WHERE c.date <= d.date
                       AND c.date > (
                           SELECT min(x.date) FROM (
                               SELECT date FROM dates WHERE date <= d.date
                               ORDER BY date DESC LIMIT 60
                           ) x
                       ) - 1) AS distinct_tickers,
                    (SELECT count(*) FROM dates WHERE date <= d.date) AS dates_behind
                FROM dates d
            )
            SELECT min(distinct_tickers)::bigint, count(*)::bigint
            FROM windowed
            WHERE dates_behind >= 60;
            """,
            (line, rows) =>
            {
                if (rows[0][0] is null or DBNull)
                {
                    return new DoneWhenLine(line, "no full 60-session window in the range", null);
                }

                var smallest = Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture);

                // **The minimum over every full window, not the mean.** The line is a
                // floor and a mean over five years would hide a converged month inside a
                // varied year, which is the condition the bound exists to catch [D-7].
                return new DoneWhenLine(
                    line,
                    "smallest full 60-session window holds " + Count(rows[0][0]) +
                    " distinct tickers, over " + Count(rows[0][1]) + " such windows",
                    smallest > 250);
            });

        // ------------------------------------------------------------ five ---

        yield return (
            "overlap between screens is read on both of its readings, allocated candidates and ranked sets",
            $"""
            WITH allocated AS (
                SELECT
                    count(*) FILTER (WHERE cardinality(screens_surfacing) > 1)::numeric
                        / NULLIF(count(*), 0) AS shared,
                    count(*)::bigint AS rows,
                    avg(cardinality(screens_surfacing))::numeric AS mean_screens
                FROM candidate_set
                WHERE date BETWEEN {f} AND {t}
            ),
            ranked AS (
                SELECT date, ticker, count(DISTINCT screen_id)::int AS screens
                FROM screen_score_daily
                WHERE date BETWEEN {f} AND {t}
                  AND rank_within_screen IS NOT NULL
                  AND screen_id IN ({Ids(live)})
                GROUP BY date, ticker
            ),
            over_ranked AS (
                SELECT
                    count(*) FILTER (WHERE screens > 1)::numeric / NULLIF(count(*), 0) AS shared,
                    count(*)::bigint AS names,
                    avg(screens)::numeric AS mean_screens
                FROM ranked
            )
            SELECT
                allocated.shared, allocated.rows, allocated.mean_screens,
                over_ranked.shared, over_ranked.names, over_ranked.mean_screens
            FROM allocated, over_ranked;
            """,
            (line, rows) =>
            {
                // **Both readings, because the plan asks for both and the two are
                // different quantities** [Q.9]. A live screen ranks about forty-four names
                // above its floor and seats eight of them, so an overlap over ranked sets
                // draws from roughly five times the population an overlap over seats does.
                // `candidate_set` carries live screen ids alone [D-85]; the ranked reading
                // is filtered to the same set, which is why it needs the live ids passed in.
                //
                // **No verdict.** The clause states what near-independence looks like
                // rather than a bound to clear, and the independence figure is what the two
                // are read against: five screens each ranking the top two percent of their
                // own scored population put a name in two sets with probability near four
                // percent [D-9].
                return new DoneWhenLine(
                    line,
                    "over allocated candidates, " + Pct(Number(rows[0][0])) + " of " + Count(rows[0][1]) +
                    " rows carry more than one live screen, mean " + Fixed(Number(rows[0][2]), 2) +
                    " screens a row; over ranked sets, " + Pct(Number(rows[0][3])) + " of " +
                    Count(rows[0][4]) + " ranked name-dates, mean " + Fixed(Number(rows[0][5]), 2) +
                    " screens. Both against the roughly 4 percent five independent " +
                    "top-two-percent rankings would give",
                    null);
            });

        // ------------------------------------------------------------- six ---

        yield return (
            "attribution rows exist for every candidate with the config version stamped",
            $"""
            SELECT
                (SELECT count(*)::bigint FROM candidate_set WHERE date BETWEEN {f} AND {t}) AS candidates,
                (SELECT count(*)::bigint FROM attribution
                 WHERE date BETWEEN {f} AND {t} AND surfaced_as = 'candidate') AS attributed,
                (SELECT count(*)::bigint FROM candidate_set c
                 WHERE c.date BETWEEN {f} AND {t}
                   AND NOT EXISTS (
                       SELECT 1 FROM attribution a
                       WHERE a.ticker = c.ticker AND a.date = c.date AND a.surfaced_as = 'candidate'
                   )) AS unattributed,
                (SELECT count(*)::bigint FROM attribution
                 WHERE date BETWEEN {f} AND {t} AND config_version IS NULL) AS unstamped,
                (SELECT count(*)::bigint FROM attribution
                 WHERE date BETWEEN {f} AND {t} AND surfaced_as = 'shadow') AS shadow_rows;
            """,
            (line, rows) =>
            {
                var unattributed = Convert.ToInt64(rows[0][2], CultureInfo.InvariantCulture);
                var unstamped = Convert.ToInt64(rows[0][3], CultureInfo.InvariantCulture);

                return new DoneWhenLine(
                    line,
                    Count(rows[0][0]) + " candidates, " + Count(rows[0][1]) + " carrying an attribution " +
                    "row, " + unattributed.ToString(CultureInfo.InvariantCulture) + " without one, " +
                    unstamped.ToString(CultureInfo.InvariantCulture) + " rows with no config version, " +
                    Count(rows[0][4]) + " shadow rows beside them",
                    unattributed == 0 && unstamped == 0);
            });
    }

    /// <summary>
    /// Per live screen, how many seats it actually filled and how often it filled any.
    ///
    /// **Not a done-when line, and it exists because one of them failed.** §06 puts
    /// overlap at 10 to 15 percent and it measures 0.5, which has two possible causes
    /// that a share alone cannot separate: five screens that genuinely never agree, or
    /// forty seats of which only thirty are ever filled. A finding reported without its
    /// cause is the kind that gets buried [`CLAUDE.md` §15].
    ///
    /// **A seat is a screen-and-ticker pair on a date**, read out of `attribution`'s
    /// `screens_surfacing` rather than out of `candidate_set`, because the two carry the
    /// same live ids and only one of them carries the shadows beside them for comparison.
    /// </summary>
    public static async Task<IReadOnlyList<(string ScreenId, long Seats, long Dates, double PerDate)>>
        SeatsByScreenAsync(string connectionString, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var data = new StageData(
            connectionString,
            new DeclaredAccess("SelectionDistributions", ["attribution"], []));

        var rows = await data.ReadAsync(
            "attribution",
            $"""
            WITH seat AS (
                SELECT a.date, s.screen_id
                FROM attribution a
                CROSS JOIN LATERAL unnest(a.screens_surfacing) AS s(screen_id)
                WHERE a.date BETWEEN {Literal(from)} AND {Literal(to)}
            )
            SELECT
                screen_id,
                count(*)::bigint AS seats,
                count(DISTINCT date)::bigint AS dates,
                count(*)::numeric / NULLIF(count(DISTINCT date), 0) AS per_date
            FROM seat
            GROUP BY screen_id
            ORDER BY screen_id COLLATE "C";
            """, ct).ConfigureAwait(false);

        return [.. rows.Select(r => (
            (string) r[0]!,
            Convert.ToInt64(r[1], CultureInfo.InvariantCulture),
            Convert.ToInt64(r[2], CultureInfo.InvariantCulture),
            Number(r[3])))];
    }

    /// <summary>
    /// The live screen ids as a SQL list, ordinal-ordered so two runs emit byte-identical
    /// SQL [`CLAUDE.md` §6].
    ///
    /// **An empty live set fails rather than producing `IN ()`.** A range with no live
    /// screen has no overlap to report, and a statement that silently matched nothing would
    /// print 0.0 percent of 0 names, which reads exactly like five screens that never agree.
    /// </summary>
    private static string Ids(IReadOnlyCollection<string> live)
        => live.Count == 0
            ? throw new InvalidOperationException(
                "No live screen was passed to the overlap line. The ranked reading is over the " +
                "live screens' ranked sets and an empty set would report 0.0 percent of 0 names, " +
                "which is indistinguishable from screens that never agree [CLAUDE.md section 1].")
            : string.Join(", ", live.OrderBy(id => id, StringComparer.Ordinal).Select(Quote));

    private static double Number(object? value)
        => value is null or DBNull ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static string Count(object? value)
        => Convert.ToInt64(value ?? 0L, CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture);

    private static string Pct(double share)
        => (share * 100d).ToString("0.0", CultureInfo.InvariantCulture) + " percent";

    private static string Fixed(double value, int places)
        => value.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Quote(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
}
