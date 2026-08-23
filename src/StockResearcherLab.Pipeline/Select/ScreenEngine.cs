using System.Globalization;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>
/// C13. Every registered screen scores every active member, on the percentile store
/// alone.
///
/// **Date-partitioned, one set-based statement per screen**, which is
/// <see cref="Compute.PercentileEngine"/>'s shape rather than a second one: a screen's
/// score on a day needs that day's percentiles for that name, and the whole date goes
/// through one statement [`ARCHITECTURE.html` §19, `CLAUDE.md` §5].
///
/// **Every screen scores every active member, and a gated name is scored like any
/// other.** The floor is the 98th percentile of the screen's own trailing distribution
/// and that distribution cannot be known without scoring everyone [D-9, INVARIANT 1].
/// There is a second reason and it is the sharper one: a floor drawn over the ungated
/// subset moves when a position opens or a cooldown expires, which makes a screen's
/// floor a function of the portfolio. **So this stage does not read
/// <c>gate_result</c> and must not.** C14 does, and exclusion happens at allocation
/// [D-117].
///
/// **Screens never read each other** [INVARIANT 2, D-6]. Each definition is loaded
/// through a <see cref="ScreenConfigFacade"/> built with that screen's id, which throws
/// when asked for another screen's key, and each statement names one screen's metric
/// list and nothing else.
///
/// **A null metric is unknown and never scores as a zero or as a bottom rank**
/// [`CLAUDE.md` §6, D-112]. The score is the weighted mean over the non-null members of
/// the metric list, so a name with three of seven is scored on its three rather than
/// punished for the four it does not have. A screen that read absence as the worst
/// value would delete precisely the thinly covered small caps its small slots exist to
/// find, and the megacap tilt this design is arranged against would return through the
/// arithmetic rather than through a ranker.
///
/// <c>rank_within_screen</c> is left null here. Floors and ranks are 4.5's, which is
/// what makes §06's "floors already applied" literally true.
/// </summary>
public sealed class ScreenEngine : IStage
{
    /// <summary>
    /// Which store each ranked metric lives on, derived from
    /// <see cref="Compute.PercentileEngine.Sources"/> rather than restated.
    ///
    /// **Derived rather than copied**, because a second list is what goes silently out
    /// of date the moment a metric moves table, and this one would fail by scoring a
    /// screen on a column that is null everywhere instead of erroring.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> TableByMetric =
        Compute.PercentileEngine.Sources
            .SelectMany(s => s.Metrics.Select(m => (Metric: m, s.Table)))
            .ToDictionary(x => x.Metric, x => x.Table, StringComparer.Ordinal);

    /// <summary>
    /// The one non-percentiled column a screen reads, and where it lives.
    ///
    /// <c>base_breakout_flag</c> is boolean and is the one column with a named reader
    /// that is not percentiled, because a percentile over a two-valued column collapses
    /// to two values and carries nothing the flag does not [`METRICS.md` §6.5, D-114].
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> TableByBoolean =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["base_breakout_flag"] = "indicator_daily",
        };

    /// <summary>Alias per source table, fixed so two runs emit byte-identical SQL.</summary>
    private static readonly IReadOnlyDictionary<string, string> Alias =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["indicator_daily"] = "ind",
            ["valuation_daily"] = "val",
            ["flow_daily"] = "flw",
            ["sentiment_derived_daily"] = "snt",
        };

    /// <summary>
    /// The alias of the derived seven-day article count, which is an aggregate over
    /// <c>sentiment_daily</c> rather than a column on any store [`ARCHITECTURE.html`
    /// §05, `SCHEMA.md` sentiment_daily].
    /// </summary>
    private const string ArticleCountAlias = "art";

    /// <summary>
    /// The raw columns a gate's stabilisation conditions read, and where they live.
    ///
    /// **Raw and not percentiled.** §05 states the three conditions against values
    /// rather than ranks: an article-count z-score of 1.0, a sentiment difference of
    /// zero, and a close above its own 20-day average. A percentile of a z-score is a
    /// different quantity that would compare against the same threshold and produce
    /// entirely plausible numbers.
    ///
    /// <c>dist_20dma</c> is how "close above the 20-day average" is read, that column
    /// being the signed distance as a fraction, so above the average is above zero. It
    /// avoids this stage reading a price series, which it must not [INVARIANT 9].
    /// </summary>
    private static readonly IReadOnlyList<(string Table, string Column)> StabilisationColumns =
    [
        ("indicator_daily", "dist_20dma"),
        ("indicator_daily", "rs_20d_slope"),
        ("sentiment_derived_daily", "article_count_z_own_90d"),
        ("sentiment_derived_daily", "sentiment_delta_7v30"),
    ];

    public string Name => "ScreenEngine";

    /// <summary>
    /// The four percentile stores plus <c>security_daily</c>, which is the one table
    /// <see cref="Universe.AsOf"/> reads and therefore where this stage's population
    /// comes from, plus its own two stores.
    ///
    /// <c>gate_result</c> is deliberately absent and its absence is load-bearing
    /// [D-117]. A floor drawn over the ungated subset would move when a position opens,
    /// which makes a screen's floor a function of the portfolio.
    ///
    /// **<c>config_rows</c> is declared even though <see cref="DeclaredAccess"/> never
    /// sees that read.** Config arrives through <see cref="IConfigStore"/>, which opens
    /// its own connection, so the guard cannot enforce this entry. It is here because
    /// §3's Reads cell names it and the conformance test holds the catalogue and the
    /// code against each other in both directions [D-74]. This component is where a
    /// screen definition comes from, so the cell is right and the declaration follows it.
    /// </summary>
    public IReadOnlyList<string> ReadSet { get; } =
    [
        "indicator_daily", "valuation_daily", "flow_daily", "sentiment_derived_daily",
        "security_daily", "config_rows", "screen_history", "screen_score_daily",
        "sentiment_daily",
    ];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new("screen_score_daily", WriteOperation.Insert, ScoreColumns),
        new("screen_score_daily", WriteOperation.Update, RankColumns),
        new("screen_history", WriteOperation.Insert, HistoryColumns),
    ];

    public static readonly string[] ScoreColumns =
        ["date", "screen_id", "ticker", "score", "rank_within_screen", "config_version"];

    /// <summary>
    /// The rank pass owns this column alone, which is the same per-operation reading of
    /// INVARIANT 10 that lets C11 update the <c>_pctile</c> columns of a row C08
    /// inserted [D-77]. Here both operations are this component's, so there is no
    /// second writer, and declaring the column set is what keeps that visible.
    /// </summary>
    public static readonly string[] RankColumns = ["rank_within_screen"];

    public static readonly string[] HistoryColumns =
        ["screen_id", "date", "floor_score", "p98_trailing", "observation_days"];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var screens = await ScreenRegistry
            .LoadScoredAsync(context.Config, context.Date, ct).ConfigureAwait(false);

        if (screens.Count == 0)
        {
            // Not a silent zero. A store with no registered screen writes no score, and
            // a night that produced no score because no screen exists must not read like
            // a night where nothing cleared a floor [CLAUDE.md section 1].
            return new StageResult(0, "ok", "no screen is registered on this date, so nothing was scored");
        }

        var lookback = (int) ConfigValue.Long(
            await context.Config.RequireAsync("screens.floor_lookback_days", context.Date, ct)
                .ConfigureAwait(false));

        var percentile = ConfigValue.Long(
            await context.Config.RequireAsync("screens.floor_percentile", context.Date, ct)
                .ConfigureAwait(false));

        long written = 0;
        var detail = new List<string>();

        // Ordinal by id rather than in the order config enumerated, so two runs over one
        // date write in the same order and the run log line is byte-identical
        // [CLAUDE.md section 6].
        foreach (var screen in screens)
        {
            var rows = await context.Data.WriteAsync(
                "screen_score_daily", WriteOperation.Insert,
                ScoreSql(screen, context.Date, context.ConfigVersion),
                parameters: null, ct).ConfigureAwait(false);

            written += rows;

            var floor = await ApplyFloorAsync(context, screen, lookback, percentile, ct).ConfigureAwait(false);

            detail.Add(
                $"{screen.ScreenId} {rows.ToString(CultureInfo.InvariantCulture)} scored, " +
                (floor.FloorScore is null
                    ? $"no floor at {floor.ObservationDays.ToString(CultureInfo.InvariantCulture)} of " +
                      $"{lookback.ToString(CultureInfo.InvariantCulture)} days"
                    : $"floor {floor.FloorScore.Value.ToString("0.####", CultureInfo.InvariantCulture)}, " +
                      $"{floor.Ranked.ToString(CultureInfo.InvariantCulture)} ranked"));
        }

        return new StageResult(written, "ok", string.Join("; ", detail));
    }

    /// <summary>
    /// One screen's floor on one date, and the ranks that follow from it.
    ///
    /// **Ranking here rather than in the allocator is what makes §06's "floors already
    /// applied" literally true.** C14 then needs no floor knowledge at all and cannot
    /// apply one differently.
    ///
    /// **The floor is the 98th percentile of the non-null score population** [D-115].
    /// D-112 makes a score null below a screen's minimum input count, so the column is
    /// legitimately sparse, and Postgres counts nulls toward <c>PERCENT_RANK</c>'s
    /// denominator. A floor over 700,000 slots of which 200,000 are null is a different
    /// number from one over 500,000 real scores and the two are indistinguishable on the
    /// page. This is PercentileEngine's own <c>count(metric)</c> argument one level up.
    ///
    /// **Below the lookback there is no floor and nothing is ranked.** That is
    /// `SCREEN_LIFECYCLE.md` §5.1's rule for a newly registered shadow applied
    /// identically to a live screen at the start of the backfill window, because it is
    /// the same condition rather than an analogous one.
    /// </summary>
    private static async Task<FloorOutcome> ApplyFloorAsync(
        StageContext context, ScreenDefinition screen, int lookbackDays, double percentile, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "screen_score_daily",
            TrailingSql(screen.ScreenId, context.Date, lookbackDays, percentile), ct).ConfigureAwait(false);

        var row = rows[0];
        var observationDays = Convert.ToInt32(row[0], CultureInfo.InvariantCulture);
        var p98 = row[1] is null or DBNull ? (double?) null : Convert.ToDouble(row[1], CultureInfo.InvariantCulture);

        // Below the lookback there is no floor, whatever the trailing distribution says.
        // The p98 is still recorded, so a reader can see what the short window held
        // without it being mistaken for a floor in force [D-115].
        var floorScore = observationDays >= lookbackDays ? p98 : null;

        await context.Data.WriteAsync(
            "screen_history", WriteOperation.Insert,
            HistorySql(screen.ScreenId, context.Date, floorScore, p98, observationDays),
            parameters: null, ct).ConfigureAwait(false);

        if (floorScore is null)
        {
            return new FloorOutcome(null, observationDays, 0);
        }

        var ranked = await context.Data.WriteAsync(
            "screen_score_daily", WriteOperation.Update,
            RankSql(screen.ScreenId, context.Date, floorScore.Value), parameters: null, ct)
            .ConfigureAwait(false);

        return new FloorOutcome(floorScore, observationDays, ranked);
    }

    /// <summary>
    /// The trailing window: how many dates this screen has scored inside it, and the
    /// p98 of the non-null scores over it.
    ///
    /// **<c>percentile_cont</c> over <c>score</c> with a <c>WHERE score IS NOT NULL</c>,
    /// not over the column as it stands.** The aggregate already skips nulls, and the
    /// predicate is there so the intent is on the page rather than resting on a property
    /// of the function: D-115's whole point is that the denominator is the real scores
    /// and not the slots [D-115].
    ///
    /// The window is inclusive of the date being scored, so tonight's scores are part of
    /// the distribution tonight's floor is drawn from. That is the trailing distribution
    /// §05 describes rather than a lagged one, and it is stated because the alternative
    /// is invisible in the output.
    ///
    /// **The window is the last <c>lookbackDays</c> dates this screen scored, not a
    /// calendar span.** D-9 says the trailing 250-day distribution and D-115 counts
    /// observations, and a trading date is the label the exchange gave a session rather
    /// than a timezone conversion of a timestamp [`CLAUDE.md` §6]. The first form of this
    /// statement took a calendar interval of twice the lookback, which is about 344
    /// trading dates at a lookback of 250: the gate fired at roughly the right moment and
    /// every floor after it was drawn over a third more history than the decision states.
    /// It passed 4.5's tests because the fixture used consecutive calendar days, where a
    /// span and a count of dates are the same thing.
    /// </summary>
    public static string TrailingSql(string screenId, DateOnly date, int lookbackDays, double percentile)
        => $"""
            WITH dates AS (
                SELECT DISTINCT date
                FROM screen_score_daily
                WHERE screen_id = {Quote(screenId)} AND date <= {Literal(date)}
                ORDER BY date DESC
                LIMIT {Int(lookbackDays)}
            ),
            win AS (
                SELECT s.score
                FROM screen_score_daily s
                JOIN dates d ON d.date = s.date
                WHERE s.screen_id = {Quote(screenId)}
            )
            SELECT
                (SELECT count(*) FROM dates)::int AS observation_days,
                (SELECT percentile_cont({Num(percentile / 100d)}) WITHIN GROUP (ORDER BY score)
                 FROM win WHERE score IS NOT NULL) AS p98;
            """;

    public static string HistorySql(
        string screenId, DateOnly date, double? floorScore, double? p98, int observationDays)
        => $"""
            INSERT INTO screen_history (screen_id, date, floor_score, p98_trailing, observation_days)
            VALUES ({Quote(screenId)}, {Literal(date)}, {Nullable(floorScore)}, {Nullable(p98)},
                    {Int(observationDays)})
            ON CONFLICT (screen_id, date) DO UPDATE SET
                floor_score = EXCLUDED.floor_score,
                p98_trailing = EXCLUDED.p98_trailing,
                observation_days = EXCLUDED.observation_days;
            """;

    /// <summary>
    /// **Dense from 1 at or above the floor and null below it**, so C14 reads a ranked
    /// list with the floor already applied and needs no floor knowledge of its own.
    ///
    /// <c>dense_rank</c> rather than <c>row_number</c>, so two names on the same score
    /// take the same rank rather than being separated by whichever the sort happened to
    /// put first. Ordering is score descending then ticker ascending, which makes the
    /// tie-break explicit and the result reproducible [`CLAUDE.md` §6].
    /// </summary>
    public static string RankSql(string screenId, DateOnly date, double floorScore)
        => $"""
            UPDATE screen_score_daily t
            SET rank_within_screen = r.rk
            FROM (
                SELECT ticker,
                       dense_rank() OVER (ORDER BY score DESC, ticker ASC)::int AS rk
                FROM screen_score_daily
                WHERE screen_id = {Quote(screenId)}
                  AND date = {Literal(date)}
                  AND score IS NOT NULL
                  AND score >= {Num(floorScore)}
            ) r
            WHERE t.screen_id = {Quote(screenId)}
              AND t.date = {Literal(date)}
              AND t.ticker = r.ticker;
            """;

    private static string Nullable(double? value)
        => value is null ? "NULL" : Num(value.Value);

    private readonly record struct FloorOutcome(double? FloorScore, int ObservationDays, long Ranked);

    /// <summary>
    /// One screen, one date, one statement.
    ///
    /// **The population is <see cref="Universe.AsOf"/> and the metric tables are joined
    /// to it rather than the other way round.** That is what makes the row count equal
    /// the membership count exactly, which is INVARIANT 1 checked mechanically rather
    /// than argued: a name with no row in any metric store still gets a row here, with a
    /// null score, because it is a member.
    /// </summary>
    public static string ScoreSql(ScreenDefinition screen, DateOnly date, int configVersion)
    {
        ArgumentNullException.ThrowIfNull(screen);

        var d = Literal(date);
        var ranked = screen.RankedMetrics;

        var joins = string.Join("\n            ", TablesFor(screen)
            .Select(t => $"LEFT JOIN {t} {Alias[t]} ON {Alias[t]}.ticker = u.ticker AND {Alias[t]}.date = {d}"));

        if (screen.Eligibility is not null)
        {
            joins += "\n            " + ArticleCountJoin(d);
        }

        var weighted = string.Join("\n                     + ", ranked.Select(Weighted));
        var weights = string.Join("\n                     + ", ranked.Select(WeightWhenPresent));
        var present = string.Join("\n                     + ", ranked.Select(Present));

        var bonus = screen.Bonuses.Count == 0
            ? "0"
            : string.Join(" + ", screen.Bonuses.Select(BonusTerm));

        // A screen with no gate emits the statement it emitted before gates existed,
        // byte for byte. That is what keeps 4.4's and 4.6's snapshots meaningful, and it
        // is what makes the gate a property of S5's configuration rather than a branch
        // every screen now runs through.
        var eligible = screen.Eligibility is null
            ? string.Empty
            : $"            WHEN ({EligibleSql(screen.Eligibility)}) IS NOT TRUE\n"
              + "                    THEN NULL\n";

        return $"""
            INSERT INTO screen_score_daily (date, screen_id, ticker, score, rank_within_screen, config_version)
            SELECT
                {d} AS date,
                {Quote(screen.ScreenId)} AS screen_id,
                u.ticker,
                CASE
            {eligible}        WHEN ({present}) >= {Int(screen.MinInputs)}
                    THEN ((({weighted})
                           / NULLIF(({weights}), 0)) + ({bonus}))::real
                    ELSE NULL
                END AS score,
                NULL::integer AS rank_within_screen,
                {Int(configVersion)} AS config_version
            FROM (SELECT m.ticker FROM {Universe.AsOf(d)} m WHERE m.is_active) u
            {joins}
            ON CONFLICT (date, screen_id, ticker) DO UPDATE SET
                score = EXCLUDED.score,
                rank_within_screen = EXCLUDED.rank_within_screen,
                config_version = EXCLUDED.config_version;
            """;
    }

    /// <summary>
    /// The gate, as one boolean expression [D-120, `ARCHITECTURE.html` section 05].
    ///
    /// **A name that fails it carries null and not a low score**, which is what the
    /// enclosing <c>CASE</c> arm does: the screen has no opinion about a name it does
    /// not rank, and a low score would put that name into the population the floor is
    /// the 98th percentile of and pull the floor down.
    ///
    /// **Unknown fails the gate as well, and <c>IS NOT TRUE</c> is what makes that so.**
    /// A composite that cannot be computed is not evidence that a name is a good business
    /// beaten down. The arm was first written <c>WHEN NOT (gate)</c>, which is NULL when
    /// the gate is NULL: the arm is then not taken, the statement falls through to the
    /// scoring arm, and a name whose quality composite could not be computed at all is
    /// ranked. It scored 95 in the fixture that found it and nothing about the number
    /// looked wrong. <c>IS NOT TRUE</c> collapses false and unknown into the one arm,
    /// which is what section 05 means by a condition being met.
    /// </summary>
    public static string EligibleSql(ScreenEligibility gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return $"({Composite(gate.Quality)}) >= {Num(gate.QualityMin)}\n"
               + $"                     AND ({Composite(gate.Technical)}) <= {Num(gate.TechnicalMax)}\n"
               + $"                     AND ({PriceStabilising()})\n"
               + $"                     AND ({NewsSettled(gate)})";
    }

    /// <summary>
    /// One composite, which is D-112's weighted mean over a metric list that is not the
    /// screen's ranking list [D-120].
    ///
    /// Null below the composite's own minimum input count, for the reason a score is
    /// null below the screen's: the mean of one input out of seven is not a quality
    /// reading, and a gate that treated it as one would admit names on no evidence.
    /// </summary>
    private static string Composite(ScreenComposite composite)
    {
        var ranked = composite.Metrics.Where(m => !m.IsBonus).ToList();

        var weighted = string.Join(" + ", ranked.Select(Weighted));
        var weights = string.Join(" + ", ranked.Select(WeightWhenPresent));
        var present = string.Join(" + ", ranked.Select(Present));

        return $"CASE WHEN ({present}) >= {Int(composite.MinInputs)} "
               + $"THEN ({weighted}) / NULLIF(({weights}), 0) ELSE NULL END";
    }

    /// <summary>
    /// "Close above the 20-day average OR the relative strength slope positive"
    /// [section 05].
    ///
    /// **This condition does not fail open and the two news conditions do.** Section 05
    /// scopes the fail-open rule to the news tests, and the reason it gives is coverage:
    /// a thinly covered name has no articles, which is ordinary, while every member has
    /// a price history by construction of the universe. So an unknown price condition is
    /// a name this screen cannot evaluate rather than one it should wave through.
    ///
    /// <c>dist_20dma</c> is how "close above the 20-day average" is read, that column
    /// being the signed distance as a fraction, so above the average is above zero. It
    /// is also what keeps this stage off a raw price series, which it must not read
    /// [INVARIANT 9].
    /// </summary>
    private static string PriceStabilising()
        => $"{Alias["indicator_daily"]}.dist_20dma > 0 OR {Alias["indicator_daily"]}.rs_20d_slope > 0";

    /// <summary>
    /// The two news conditions, **failing open below the article threshold**
    /// [section 05].
    ///
    /// Below <c>news_gate_min_articles</c> articles in seven days both conditions are
    /// treated as satisfied. Thinly covered names are exactly what this screen's small
    /// slots exist to find, and failing closed would delete them and reintroduce the
    /// megacap tilt through the arithmetic rather than through a ranker. The cost is a
    /// weaker gate on thinly covered names than on well covered ones, which section 05
    /// records as deliberate.
    ///
    /// **<c>COALESCE(count, 0)</c> is correct here and is not the defaulting
    /// <c>CLAUDE.md</c> section 6 forbids.** `SCHEMA.md` states the rule it rests on: a
    /// day with no <c>sentiment_daily</c> row is a day with no articles rather than a
    /// day nobody looked, because C04 covers the whole universe nightly with no
    /// pre-selection. Absence is a measured zero here, which is the one shape where zero
    /// is the right reading.
    /// </summary>
    private static string NewsSettled(ScreenEligibility gate)
        => $"COALESCE({ArticleCountAlias}.articles_7d, 0) < {Int(gate.NewsGateMinArticles)} "
           + $"OR ({Alias["sentiment_derived_daily"]}.article_count_z_own_90d < {Num(gate.StabilisationZMax)} "
           + $"AND {Alias["sentiment_derived_daily"]}.sentiment_delta_7v30 >= {Num(gate.SentimentDeltaMin)})";

    /// <summary>
    /// The seven-day article count, aggregated from <c>sentiment_daily</c> because no
    /// store carries it as a column.
    ///
    /// **Seven calendar days and not seven sessions**, which is the opposite of the
    /// trailing floor window and is right for the opposite reason. A floor is drawn over
    /// the dates a screen scored, which are sessions. News arrives on days the exchange
    /// is shut, so a seven-session window over a holiday week reaches back nine or ten
    /// days of coverage and reads a different quantity than section 05's seven days.
    /// </summary>
    private static string ArticleCountJoin(string date)
        => "LEFT JOIN (\n"
           + "                            SELECT ticker, sum(article_count)::bigint AS articles_7d\n"
           + "                            FROM sentiment_daily\n"
           + $"                            WHERE date > {date} - 7 AND date <= {date}\n"
           + "                            GROUP BY ticker\n"
           + $"                        ) {ArticleCountAlias} ON {ArticleCountAlias}.ticker = u.ticker";

    /// <summary>
    /// Every store the statement joins, ordinal-ordered so the emitted SQL is
    /// byte-identical across runs.
    ///
    /// A gated screen adds the stores its two composites rank on and the stores its
    /// stabilisation conditions read raw columns from. They fold into one distinct set
    /// rather than being joined twice, S5's technical composite and its own ranking
    /// metric both sitting on <c>indicator_daily</c>.
    /// </summary>
    private static IReadOnlyList<string> TablesFor(ScreenDefinition screen)
        => [.. screen.Metrics
            .Select(m => m.IsBonus ? BooleanTable(m.Metric) : PercentileTable(m.Metric))
            .Concat(GateTables(screen.Eligibility))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)];

    private static IEnumerable<string> GateTables(ScreenEligibility? gate)
        => gate is null
            ? []
            : gate.Quality.Metrics
                .Concat(gate.Technical.Metrics)
                .Where(m => !m.IsBonus)
                .Select(m => PercentileTable(m.Metric))
                .Concat(StabilisationColumns.Select(c => c.Table));

    private static string PercentileTable(string metric)
        => TableByMetric.TryGetValue(metric, out var table)
            ? table
            : throw new InvalidOperationException(
                $"Metric '{metric}' is on no percentile source. A screen ranking on a column nothing " +
                "percentiles would score null for every name on every date and read as a screen that " +
                "found nothing [D-112, METRICS.md section 6].");

    private static string BooleanTable(string metric)
        => TableByBoolean.TryGetValue(metric, out var table)
            ? table
            : throw new InvalidOperationException(
                $"Bonus metric '{metric}' is on no known store. The only non-percentiled column with a " +
                "named reader is base_breakout_flag [METRICS.md section 6.5, D-114].");

    /// <summary>
    /// The direction-adjusted percentile, weighted, contributing nothing when absent.
    ///
    /// <c>COALESCE(..., 0)</c> is safe here only because the denominator drops the same
    /// metric's weight: numerator and denominator both exclude an absent input, which is
    /// what makes this a mean over the present ones rather than a sum punishing absence
    /// [D-112].
    /// </summary>
    private static string Weighted(ScreenMetric m)
        => $"COALESCE({Weight(m)} * {Adjusted(m)}, 0)";

    private static string WeightWhenPresent(ScreenMetric m)
        => $"CASE WHEN {Column(m)} IS NULL THEN 0 ELSE {Weight(m)} END";

    private static string Present(ScreenMetric m)
        => $"({Column(m)} IS NOT NULL)::int";

    /// <summary>
    /// Direction <c>low</c> is 100 minus the stored percentile. Percentiles are ascending
    /// always, so the word says what the screen rewards [D-113, `METRICS.md` §6.3].
    /// </summary>
    private static string Adjusted(ScreenMetric m)
        => m.Direction == MetricDirection.High ? Column(m) : $"(100.0 - {Column(m)})";

    private static string Column(ScreenMetric m)
        => $"{Alias[PercentileTable(m.Metric)]}.{m.Metric}{Compute.PercentileEngine.Suffix}";

    /// <summary>
    /// **A null flag contributes what false contributes** [D-114]. "There is no base"
    /// and "the base is unknown" both mean no evidence of a defined entry level, and the
    /// bonus is evidence-positive only. This is the one place in this system where
    /// unknown and false legitimately coincide, which is why it is written rather than
    /// left to the null rule.
    /// </summary>
    private static string BonusTerm(ScreenMetric m)
        => $"CASE WHEN {Alias[BooleanTable(m.Metric)]}.{m.Metric} THEN {Num(m.BonusPoints!.Value)} ELSE 0 END";

    private static string Weight(ScreenMetric m) => Num(m.Weight);

    private static string Num(double value)
        => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
}
