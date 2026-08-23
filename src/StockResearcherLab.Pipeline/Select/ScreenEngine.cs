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
        "security_daily", "config_rows", "screen_history",
    ];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new("screen_score_daily", WriteOperation.Insert, ScoreColumns),
    ];

    public static readonly string[] ScoreColumns =
        ["date", "screen_id", "ticker", "score", "rank_within_screen", "config_version"];

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
            detail.Add($"{screen.ScreenId} {rows.ToString(CultureInfo.InvariantCulture)}");
        }

        return new StageResult(written, "ok", string.Join(", ", detail));
    }

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

        var weighted = string.Join("\n                     + ", ranked.Select(Weighted));
        var weights = string.Join("\n                     + ", ranked.Select(WeightWhenPresent));
        var present = string.Join("\n                     + ", ranked.Select(Present));

        var bonus = screen.Bonuses.Count == 0
            ? "0"
            : string.Join(" + ", screen.Bonuses.Select(BonusTerm));

        return $"""
            INSERT INTO screen_score_daily (date, screen_id, ticker, score, rank_within_screen, config_version)
            SELECT
                {d} AS date,
                {Quote(screen.ScreenId)} AS screen_id,
                u.ticker,
                CASE
                    WHEN ({present}) >= {Int(screen.MinInputs)}
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

    /// <summary>Ordinal-ordered so the emitted SQL is byte-identical across runs.</summary>
    private static IReadOnlyList<string> TablesFor(ScreenDefinition screen)
        => [.. screen.Metrics
            .Select(m => m.IsBonus ? BooleanTable(m.Metric) : PercentileTable(m.Metric))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)];

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
