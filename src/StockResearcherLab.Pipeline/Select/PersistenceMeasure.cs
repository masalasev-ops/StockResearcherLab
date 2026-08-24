using System.Globalization;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>One screen's persistence figures over the backfilled range.</summary>
/// <param name="ScreenId">The screen.</param>
/// <param name="Pairs">
/// Consecutive-session pairs where both sides carried a ranked set, which is what the
/// D-1 statistic averaged over. A screen that ranks on few dates has few pairs, and the
/// count is beside the overlap so a figure taken over a handful of dates cannot be read
/// as one taken over the window.
/// </param>
/// <param name="DatesRanked">
/// Sessions on which the screen ranked anything at all, out of the sessions it scored.
/// **A screen that ranks on few dates cannot be read off its overlap alone**, and the two
/// numbers are reported together for that reason.
/// </param>
/// <param name="DatesScored">Sessions the screen scored, which is every session it ran on.</param>
/// <param name="MeanRankedSize">The mean size of the screen's ranked set on a date it ranked.</param>
/// <param name="MeanScoredSize">The mean size of the population it ranked out of.</param>
/// <param name="Chance">
/// The overlap two independent draws of the ranked set's own size from the scored
/// population would give, computed per screen from its own sizes [§5].
/// </param>
/// <param name="Lag1">Mean Jaccard overlap between D and D-1.</param>
/// <param name="Lag5">The same at D and D-5.</param>
/// <param name="Lag21">The same at D and D-21.</param>
/// <param name="LargeShare">The ranked set's large-bucket share, for the size-proxy reading.</param>
public sealed record ScreenPersistence(
    string ScreenId,
    long Pairs,
    long DatesRanked,
    long DatesScored,
    double MeanRankedSize,
    double MeanScoredSize,
    double Chance,
    double Lag1,
    double Lag5,
    double Lag21,
    double LargeShare);

/// <summary>
/// §5's pre-registered persistence measure, computed at 4.13 and recorded before 4.14
/// freezes anything.
///
/// **Why this exists at all.** The phase's done-when is a diversity test and is nearly
/// blind to a screen's composition being wrong. Replace every score with a date-seeded
/// random number and four of the five distribution lines pass outright: the floor is
/// self-referential so it admits two percent whatever the score is, the ceiling cuts each
/// screen to eight, and the 2/3/3 split holds by construction because the allocator
/// enforces the quota rather than the screen earning it. So the measure that would
/// distinguish the cases was stated in full before any score existed to look at, and this
/// is that measure rather than one chosen after the numbers were seen.
///
/// **It reads no forward return and no `attribution` row**, so it does not touch
/// `CLAUDE.md` §11's prohibition on tuning screens on forward returns before anything has
/// judged them. It is a statement about a ranked set's stability and nothing else.
///
/// **The chance baseline is per screen, computed from the screen's own sizes.** Two
/// independent draws of k names from a population of n overlap by about
/// <c>k / (2n - k)</c> in Jaccard terms, and the five screens rank different numbers of
/// names out of different populations, so a single baseline would flatter the narrow
/// ones.
/// </summary>
public static class PersistenceMeasure
{
    /// <summary>
    /// The three lags §5 names, in order. Sessions rather than calendar days, because a
    /// ranked set exists per session [`CLAUDE.md` §6].
    /// </summary>
    public static readonly IReadOnlyList<int> Lags = [1, 5, 21];

    public static async Task<IReadOnlyList<ScreenPersistence>> MeasureAsync(
        string connectionString, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var data = new StageData(
            connectionString,
            new DeclaredAccess(
                "PersistenceMeasure", ["screen_score_daily", "security_daily"], []));

        var rows = await data.ReadAsync("screen_score_daily", Sql(from, to), ct).ConfigureAwait(false);

        var found = new List<ScreenPersistence>();

        foreach (var row in rows)
        {
            var ranked = Number(row[2]);
            var scored = Number(row[3]);

            found.Add(new ScreenPersistence(
                (string) row[0]!,
                Convert.ToInt64(row[1], CultureInfo.InvariantCulture),
                Convert.ToInt64(row[8], CultureInfo.InvariantCulture),
                Convert.ToInt64(row[9], CultureInfo.InvariantCulture),
                ranked,
                scored,
                ChanceOverlap(ranked, scored),
                Number(row[4]),
                Number(row[5]),
                Number(row[6]),
                Number(row[7])));
        }

        return found;
    }

    /// <summary>
    /// The Jaccard overlap two independent uniform draws of <paramref name="ranked"/>
    /// names from <paramref name="scored"/> would give, in expectation.
    ///
    /// The expected intersection is <c>k * k / n</c> and the expected union is
    /// <c>2k - k*k/n</c>, so the ratio is <c>k / (2n - k)</c>. Stated as the closed form
    /// rather than simulated, so the baseline is reproducible and carries no seed.
    /// </summary>
    public static double ChanceOverlap(double ranked, double scored)
        => scored <= 0d || ranked <= 0d ? 0d : ranked / ((2d * scored) - ranked);

    /// <summary>
    /// One statement, per screen, over the whole range.
    ///
    /// **The ranked set is the names carrying a non-null <c>rank_within_screen</c>**,
    /// which is §5's own definition and is what a floor leaves behind. A set is compared
    /// against the set the same screen ranked <c>lag</c> sessions earlier, where "sessions
    /// earlier" is a position in the screen's own list of scored dates rather than a
    /// calendar offset: the exchange is shut at weekends and a calendar lag of one would
    /// compare Monday against Sunday and find nothing on both sides.
    ///
    /// **The lag is taken over every scored date and the pairs are filtered afterwards,
    /// not before.** `array_agg` with a FILTER yields null on a date that ranked nothing,
    /// <c>jaccard</c> is STRICT so a null on either side gives null, and <c>avg</c> skips
    /// it. So a date with no ranked set is excluded from the average without being
    /// excluded from the ordering, which is what keeps D-1 meaning one session.
    ///
    /// **Filtering first was the first form of this and it was wrong.** Lagging over the
    /// ranked dates alone makes D-1 mean "the previous date this screen ranked anything",
    /// which for a screen ranking on a twentieth of the window is weeks rather than a
    /// session, and it reports that gap as high one-day persistence. It changed S5's
    /// figure from 0.7536 to what is recorded, and it would have flattered exactly the
    /// screen with the least to say.
    ///
    /// The exclusion still matters and is now done in the right place: the first 250
    /// sessions have no floor and therefore no ranked set [D-115], and counting them as
    /// overlaps of zero would drive every screen toward zero and read as near-chance
    /// persistence on all five.
    /// </summary>
    public static string Sql(DateOnly from, DateOnly to)
    {
        var f = Literal(from);
        var t = Literal(to);

        var lagColumns = string.Join(",\n                    ", Lags.Select(l =>
            $"lag(ranked, {Int(l)}) OVER (PARTITION BY screen_id ORDER BY date) AS prior_{Int(l)}"));

        var overlaps = string.Join(",\n                ", Lags.Select(l =>
            $"avg(jaccard(ranked, prior_{Int(l)})) FILTER (WHERE prior_{Int(l)} IS NOT NULL) AS lag_{Int(l)}"));

        return $"""
            WITH sets AS (
                SELECT
                    s.screen_id,
                    s.date,
                    array_agg(s.ticker ORDER BY s.ticker COLLATE "C")
                        FILTER (WHERE s.rank_within_screen IS NOT NULL) AS ranked,
                    count(s.score)::numeric AS scored
                FROM screen_score_daily s
                WHERE s.date BETWEEN {f} AND {t}
                GROUP BY s.screen_id, s.date
            ),
            ranked_only AS (
                SELECT * FROM sets WHERE ranked IS NOT NULL AND cardinality(ranked) > 0
            ),
            lagged AS (
                SELECT
                    screen_id, date, ranked, scored,
                    {lagColumns}
                FROM sets
            ),
            sizes AS (
                SELECT
                    r.screen_id,
                    avg(cardinality(r.ranked))::numeric AS mean_ranked,
                    avg(r.scored)::numeric AS mean_scored,
                    count(*)::bigint AS dates_ranked
                FROM ranked_only r
                GROUP BY r.screen_id
            ),
            scored_dates AS (
                SELECT screen_id, count(*)::bigint AS dates_scored FROM sets GROUP BY screen_id
            ),
            buckets AS (
                SELECT
                    r.screen_id,
                    count(*) FILTER (WHERE b.size_bucket = 'large')::numeric
                        / NULLIF(count(*), 0) AS large_share
                FROM ranked_only r
                CROSS JOIN LATERAL unnest(r.ranked) AS u(ticker)
                LEFT JOIN LATERAL (
                    SELECT sd.size_bucket FROM security_daily sd
                    WHERE sd.ticker = u.ticker AND sd.date <= r.date
                    ORDER BY sd.date DESC LIMIT 1
                ) b ON TRUE
                GROUP BY r.screen_id
            ),
            overlap AS (
                SELECT
                    screen_id,
                    count(*) FILTER (WHERE prior_1 IS NOT NULL AND ranked IS NOT NULL)::bigint
                        AS pairs,
                    {overlaps}
                FROM lagged
                GROUP BY screen_id
            )
            SELECT
                overlap.screen_id,
                overlap.pairs,
                sizes.mean_ranked,
                sizes.mean_scored,
                overlap.lag_1,
                overlap.lag_5,
                overlap.lag_21,
                buckets.large_share,
                sizes.dates_ranked,
                scored_dates.dates_scored
            FROM overlap
            JOIN sizes ON sizes.screen_id = overlap.screen_id
            JOIN scored_dates ON scored_dates.screen_id = overlap.screen_id
            LEFT JOIN buckets ON buckets.screen_id = overlap.screen_id
            ORDER BY overlap.screen_id COLLATE "C";
            """;
    }

    /// <summary>
    /// The Jaccard function the statement above calls, created once by migration `0020`.
    ///
    /// A function rather than an inline expression because the intersection and the union
    /// of two arrays are two subqueries each, repeated three times for the three lags, and
    /// six copies of one expression is where one of them quietly differs.
    /// </summary>
    public const string JaccardFunction = "jaccard";

    private static double Number(object? value)
        => value is null or DBNull ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
}
