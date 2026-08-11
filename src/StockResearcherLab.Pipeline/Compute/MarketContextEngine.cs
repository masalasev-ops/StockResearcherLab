using System.Globalization;
using System.Text;
using System.Text.Json;
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
public sealed class MarketContextEngine : IStage
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

    public IReadOnlyList<string> ReadSet { get; } = ["price_daily", "security", "indicator_daily"];

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

        var written = await context.Data.BulkUpsertAsync(
            "market_context_daily", Columns, ConflictTarget,
            async (w, c) =>
            {
                await w.StartRowAsync(c).ConfigureAwait(false);
                await w.WriteAsync(context.Date, c).ConfigureAwait(false);
                await w.WriteAsync(breadth is null ? null : (float?) breadth.Value, c).ConfigureAwait(false);

                // No series in this feed. Null rather than a substitute wearing the
                // column's name [METRICS.md section 5].
                await w.WriteAsync<float?>(null, c).ConfigureAwait(false);

                await w.WriteAsync(label, c).ConfigureAwait(false);
                await w.WriteAsync(sectors, c).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

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
            JOIN security s ON s.ticker = i.ticker AND s.is_active
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
    /// </summary>
    private static async Task<string> SectorRelativeStrengthAsync(
        StageContext context, int minMembers, CancellationToken ct)
    {
        var sql = $"""
            WITH universe AS (
                SELECT ticker, sector FROM security WHERE is_active AND sector IS NOT NULL
            ),
            windowed AS (
                SELECT u.sector, p.ticker, p.date, p.adj_close,
                       row_number() OVER (PARTITION BY p.ticker ORDER BY p.date DESC) AS rn
                FROM price_daily p
                JOIN universe u ON u.ticker = p.ticker
                WHERE p.date <= {Literal(context.Date)} AND p.adj_close IS NOT NULL AND p.adj_close > 0
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
            WHERE r IS NOT NULL
            GROUP BY sector, date
            HAVING count(*) >= {minMembers.ToString(CultureInfo.InvariantCulture)}
            ORDER BY sector, date;
            """;

        var rows = await context.Data.ReadAsync("price_daily", sql, ct).ConfigureAwait(false);

        var chained = new Dictionary<string, (double Level, int Days)>(StringComparer.Ordinal);
        var universeLevel = 1.0;
        var universeDays = 0;
        var byDate = new Dictionary<DateOnly, List<double>>();

        foreach (var r in rows)
        {
            var sector = (string) r[0]!;
            var date = DateOnly.FromDateTime((DateTime) r[1]!);
            var mean = (double) (decimal) r[2]!;

            var prior = chained.GetValueOrDefault(sector, (Level: 1.0, Days: 0));
            chained[sector] = (prior.Level * (1 + mean), prior.Days + 1);

            if (!byDate.TryGetValue(date, out var list))
            {
                byDate[date] = list = [];
            }

            list.Add(mean);
        }

        // The universe composite is the same construction over every sector present,
        // so the two are comparable by construction rather than by assumption.
        foreach (var date in byDate.Keys.OrderBy(d => d))
        {
            universeLevel *= 1 + byDate[date].Average();
            universeDays++;
        }

        var buffer = new StringBuilder("{");
        var first = true;

        foreach (var sector in chained.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var (level, days) = chained[sector];

            if (days < RelativeStrengthWindow || universeDays < RelativeStrengthWindow || universeLevel <= 0)
            {
                continue;
            }

            if (!first)
            {
                buffer.Append(',');
            }

            first = false;

            buffer.Append(JsonSerializer.Serialize(sector))
                .Append(':')
                .Append(((level / universeLevel) - 1).ToString("F6", CultureInfo.InvariantCulture));
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
