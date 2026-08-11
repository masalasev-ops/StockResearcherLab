using System.Globalization;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Compute;

/// <summary>
/// C08. The technical columns of <c>indicator_daily</c>, computed locally from
/// <c>price_daily</c> and never bought [`SCHEMA.md`].
///
/// **Ticker-partitioned, which is why this reads once and computes in C# rather than
/// expressing itself as one statement the way C34 does** [`ARCHITECTURE.html` §19].
/// Every indicator here is a function of one ticker's own series, so no worker needs to
/// see another's data. C34 is date-partitioned and is one statement for the opposite
/// reason: a percentile on a day needs every name in the cell on that day. C01 is the
/// precedent for this shape, two set-based reads then a per-ticker loop then one bulk
/// upsert, and it is not a query per ticker.
///
/// **ATR and ADX are Wilder-smoothed, which is recursive and is not a window
/// aggregate.** That is the concrete reason the arithmetic is here rather than in SQL.
/// The recursion is seeded at a fixed warm-up so it is a function of the date and the
/// config version alone: without one, two databases holding the same 250 bars and
/// different amounts before them disagree, neither is wrong, and the reference test
/// cannot be written [`METRICS.md` §2].
///
/// **Prices compared across dates use the adjusted series and volume is adjusted
/// through the same factor.** A two for one split halves the raw close, and a moving
/// average or a 52-week high computed on the raw series reads that as a fifty percent
/// decline while every screen downstream sees a name that has collapsed. Nothing
/// errors [`METRICS.md` §1.1].
///
/// **Insufficient history is null, never a shorter window.** A name with 120 bars has
/// no 200-day average, and substituting the 120 it has produces a number that is not
/// what the column says it is, on exactly the names whose history is thinnest
/// [`CLAUDE.md` §6].
///
/// Formulas, windows, warm-ups and null rules for every column are in `METRICS.md`.
/// </summary>
public sealed class IndicatorEngine : IStage
{
    /// <summary>
    /// The columns this stage writes, declared so the staged write is checked against
    /// them before a connection opens [A27]. The percentile columns on this table
    /// belong to C11 and are absent here deliberately [D-77].
    /// </summary>
    public static readonly string[] Columns =
    [
        "ticker", "date",
        "atr_pct", "adx14", "dist_20dma", "dist_200dma", "dist_52w_high",
        "dist_52w_high_20d_change", "volume_vs_50d_avg", "ma50_200_slope",
        "base_breakout_flag", "median_dollar_volume_20d",
        "rs_change_21d", "rs_change_63d", "rs_21d_63d_change", "rs_20d_slope",
        "rs_change_vs_sector",
    ];

    /// <summary>
    /// The benchmark, read from <c>price_daily</c> rather than from a store of its own.
    ///
    /// C02 writes every row the bulk feed returns with no universe filter, so the
    /// series is already there among roughly fifty thousand tickers a day. D-2 puts
    /// ETFs out of scope as candidates, which is a statement about <c>security</c> and
    /// not about what may be read as a benchmark.
    /// </summary>
    public const string Benchmark = "SPY.US";

    private static readonly string[] ConflictTarget = ["ticker", "date"];

    /// <summary>
    /// Bars this stage needs behind the date being computed, before the Wilder warm-up
    /// is taken into account. The binding one is `dist_52w_high_20d_change`, which
    /// needs a 52-week high at D and another 20 trading dates earlier, so 252 + 20.
    /// `ma50_200_slope` needs 220 and everything else less.
    /// </summary>
    public const int RequiredBars = 272;

    /// <summary>Wilder's period, in the name of no column, but fixed by the column that carries it.</summary>
    public const int WilderPeriod = 14;

    private const int Ma20 = 20;
    private const int Ma50 = 50;
    private const int Ma200 = 200;
    private const int Volume50 = 50;
    private const int Weeks52 = 252;
    private const int SlopeWindow = 20;

    public string Name => "IndicatorEngine";

    public IReadOnlyList<string> ReadSet { get; } = ["price_daily", "security"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("indicator_daily", WriteOperation.Insert, Columns)];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var warmup = (int) await LongAsync(context, "indicator.wilder_warmup_bars", ct).ConfigureAwait(false);
        var baseLookback = (int) await LongAsync(context, "indicator.base_lookback_days", ct).ConfigureAwait(false);
        var baseMaxRange = (double) await DecimalAsync(context, "indicator.base_max_range_pct", ct).ConfigureAwait(false);

        // One extra bar because the first true range needs a previous close.
        var bars = Math.Max(RequiredBars, warmup + 1);

        var minSectorMembers = (int) await LongAsync(context, "market.sector_composite_min_members", ct).ConfigureAwait(false);

        var series = await SeriesAsync(context, bars, ct).ConfigureAwait(false);
        var medians = await MedianDollarVolumeAsync(context, ct).ConfigureAwait(false);
        var benchmark = await BenchmarkAsync(context, bars, ct).ConfigureAwait(false);
        var sectors = await SectorsAsync(context, ct).ConfigureAwait(false);
        var composites = await SectorCompositesAsync(context, bars, minSectorMembers, ct).ConfigureAwait(false);

        var rows = new List<Row>(series.Count);

        foreach (var (ticker, history) in series)
        {
            var sector = sectors.GetValueOrDefault(ticker);

            rows.Add(Compute(
                ticker, context.Date, history, warmup, baseLookback, baseMaxRange,
                medians.GetValueOrDefault(ticker), benchmark,
                sector is null ? null : composites.GetValueOrDefault(sector)));
        }

        // Ordinal, so two runs over the same date write in the same order and the
        // output is byte-identical [CLAUDE.md section 6].
        rows.Sort(static (a, b) => string.CompareOrdinal(a.Ticker, b.Ticker));

        var written = await context.Data.BulkUpsertAsync(
            "indicator_daily", Columns, ConflictTarget,
            async (w, c) =>
            {
                foreach (var r in rows)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(r.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Date, c).ConfigureAwait(false);
                    await w.WriteAsync(r.AtrPct, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Adx14, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Dist20Dma, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Dist200Dma, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Dist52WHigh, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Dist52WHigh20DChange, c).ConfigureAwait(false);
                    await w.WriteAsync(r.VolumeVs50DAvg, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Ma50200Slope, c).ConfigureAwait(false);
                    await w.WriteAsync(r.BaseBreakoutFlag, c).ConfigureAwait(false);
                    await w.WriteAsync(r.MedianDollarVolume20D, c).ConfigureAwait(false);
                    await w.WriteAsync(r.RsChange21D, c).ConfigureAwait(false);
                    await w.WriteAsync(r.RsChange63D, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Rs21D63DChange, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Rs20DSlope, c).ConfigureAwait(false);
                    await w.WriteAsync(r.RsChangeVsSector, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);

        return new StageResult(written, "ok", Detail(rows));
    }

    /// <summary>
    /// What the run log says about the columns that carry null, read off the rows
    /// rather than inferred. A metric that is null for a reason nobody counted is a
    /// metric nobody notices going null [C34's precedent].
    /// </summary>
    private static string Detail(IReadOnlyList<Row> rows)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} names. Null: {1:N0} atr_pct, {2:N0} adx14, {3:N0} dist_200dma, " +
            "{4:N0} dist_52w_high, {5:N0} dist_52w_high_20d_change, {6:N0} ma50_200_slope, " +
            "{7:N0} volume_vs_50d_avg, {8:N0} median_dollar_volume_20d",
            rows.Count,
            rows.Count(r => r.AtrPct is null),
            rows.Count(r => r.Adx14 is null),
            rows.Count(r => r.Dist200Dma is null),
            rows.Count(r => r.Dist52WHigh is null),
            rows.Count(r => r.Dist52WHigh20DChange is null),
            rows.Count(r => r.Ma50200Slope is null),
            rows.Count(r => r.VolumeVs50DAvg is null),
            rows.Count(r => r.MedianDollarVolume20D is null));

    /// <summary>
    /// Every column for one ticker. Public so the reference test computes through the
    /// same path the stage does rather than through a copy of it.
    /// </summary>
    public static Row Compute(
        string ticker, DateOnly date, IReadOnlyList<Bar> history,
        int warmup, int baseLookback, double baseMaxRange, decimal? medianDollarVolume,
        IReadOnlyDictionary<DateOnly, double>? benchmark = null,
        IReadOnlyDictionary<DateOnly, double>? sectorComposite = null)
    {
        var n = history.Count;

        // The adjusted series. A bar missing either close or adjusted close has no
        // adjustment factor, so it has no adjusted high, low or volume either.
        var cp = new double?[n];
        var hp = new double?[n];
        var lp = new double?[n];
        var vp = new double?[n];

        for (var i = 0; i < n; i++)
        {
            var b = history[i];
            cp[i] = (double?) b.AdjClose;

            if (b.Close is not > 0 || b.AdjClose is null)
            {
                continue;
            }

            var f = (double) b.AdjClose.Value / (double) b.Close.Value;
            hp[i] = b.High is null ? null : (double) b.High.Value * f;
            lp[i] = b.Low is null ? null : (double) b.Low.Value * f;

            // Adjusted volume divides by the same factor, so a split leaves a ratio of
            // two of them unchanged. Any constant normalisation cancels in that ratio,
            // which is the only place this is read [METRICS.md section 1.1].
            vp[i] = b.Volume is null ? null : b.Volume.Value / f;
        }

        var (atr, adx) = Wilder(hp, lp, cp, warmup);
        var last = cp[n - 1];

        // Relative strength is the ticker's adjusted close over the benchmark's, so
        // the change in it is the ticker's return over the benchmark's. Against the
        // sector composite it is the same construction with a different denominator,
        // and the composite's arbitrary seed cancels because only a change is read.
        var rsBenchmark = Relative(history, cp, benchmark);
        var rsSector = Relative(history, cp, sectorComposite);

        var rs21 = Change(rsBenchmark, 21);
        var rs63 = Change(rsBenchmark, 63);

        return new Row(
            ticker, date,
            AtrPct: atr is null || last is not > 0 ? null : (float) (atr.Value / last.Value),
            Adx14: (float?) adx,
            Dist20Dma: (float?) DistanceFromAverage(cp, Ma20),
            Dist200Dma: (float?) DistanceFromAverage(cp, Ma200),
            Dist52WHigh: (float?) DistanceFrom52WeekHigh(cp, hp, n - 1),
            Dist52WHigh20DChange: (float?) Dist52WHighChange(cp, hp, n - 1),
            VolumeVs50DAvg: (float?) RatioToAverage(vp, Volume50),
            Ma50200Slope: (float?) MaSlope(cp),
            BaseBreakoutFlag: BaseBreakout(cp, hp, lp, baseLookback, baseMaxRange),
            MedianDollarVolume20D: medianDollarVolume,
            RsChange21D: (float?) rs21,
            RsChange63D: (float?) rs63,
            Rs21D63DChange: rs21 is null || rs63 is null ? null : (float?) (rs21.Value - rs63.Value),
            Rs20DSlope: (float?) SlopeOf(rsBenchmark, SlopeWindow),
            RsChangeVsSector: (float?) Change(rsSector, 63));
    }

    /// <summary>
    /// The ratio of the ticker's adjusted close to a reference series, aligned on date.
    ///
    /// A date the reference does not carry breaks the ratio there rather than being
    /// filled from a neighbour, because a benchmark that did not trade is not a
    /// benchmark that was flat.
    /// </summary>
    private static double?[] Relative(
        IReadOnlyList<Bar> history, double?[] cp, IReadOnlyDictionary<DateOnly, double>? reference)
    {
        var rs = new double?[cp.Length];

        if (reference is null)
        {
            return rs;
        }

        for (var i = 0; i < cp.Length; i++)
        {
            if (cp[i] is { } c && reference.TryGetValue(history[i].Date, out var r) && r > 0)
            {
                rs[i] = c / r;
            }
        }

        return rs;
    }

    /// <summary>Change in a series over n trading dates, as a fraction. Null if either endpoint is absent.</summary>
    private static double? Change(double?[] v, int n)
    {
        var at = v.Length - 1;

        return at - n < 0 || v[at] is not { } now || v[at - n] is not { } then || then <= 0
            ? null
            : (now / then) - 1;
    }

    /// <summary>Normalised slope of the last n values of a series, or null if any is absent.</summary>
    private static double? SlopeOf(double?[] v, int n)
    {
        if (v.Length < n)
        {
            return null;
        }

        var y = new double[n];

        for (var k = 0; k < n; k++)
        {
            if (v[v.Length - n + k] is not { } x)
            {
                return null;
            }

            y[k] = x;
        }

        return NormalisedSlope(y);
    }

    /// <summary>
    /// ATR(14) and ADX(14), both Wilder-smoothed over a warm-up window ending at the
    /// date being computed.
    ///
    /// The seed is the simple mean of the first fourteen values in that window and the
    /// recursion runs forward from there. 250 bars rather than 14 because the 13/14
    /// decay puts the seed's influence under one part in ten thousand after 125 steps,
    /// which makes the choice immaterial to the answer while keeping it exactly
    /// specified [METRICS.md section 2].
    /// </summary>
    private static (double? Atr, double? Adx) Wilder(
        double?[] hp, double?[] lp, double?[] cp, int warmup)
    {
        var n = hp.Length;

        // The window of true ranges ends at the last bar and each needs its
        // predecessor, so it starts one bar later than the price window.
        var start = Math.Max(1, n - warmup);
        var len = n - start;

        if (len < (2 * WilderPeriod) + 1)
        {
            return (null, null);
        }

        var tr = new double[len];
        var plusDm = new double[len];
        var minusDm = new double[len];

        for (var t = 0; t < len; t++)
        {
            var i = start + t;

            if (hp[i] is not { } h || lp[i] is not { } l ||
                cp[i - 1] is not { } prevClose || hp[i - 1] is not { } prevHigh || lp[i - 1] is not { } prevLow)
            {
                // A gap anywhere in the window breaks the recursion, and continuing
                // over it would smooth across a hole rather than report one.
                return (null, null);
            }

            tr[t] = Math.Max(h - l, Math.Max(Math.Abs(h - prevClose), Math.Abs(l - prevClose)));

            var up = h - prevHigh;
            var down = prevLow - l;

            plusDm[t] = up > down && up > 0 ? up : 0;
            minusDm[t] = down > up && down > 0 ? down : 0;
        }

        var smoothedTr = WilderSeries(tr);
        var smoothedPlus = WilderSeries(plusDm);
        var smoothedMinus = WilderSeries(minusDm);

        var dx = new List<double>(len);

        for (var t = WilderPeriod - 1; t < len; t++)
        {
            if (smoothedTr[t] <= 0)
            {
                // A name that has not moved at all across the window has no
                // directional index rather than one of zero.
                return (smoothedTr[^1], null);
            }

            var plusDi = 100 * smoothedPlus[t] / smoothedTr[t];
            var minusDi = 100 * smoothedMinus[t] / smoothedTr[t];
            var sum = plusDi + minusDi;

            dx.Add(sum <= 0 ? 0 : 100 * Math.Abs(plusDi - minusDi) / sum);
        }

        return dx.Count < WilderPeriod
            ? (smoothedTr[^1], null)
            : (smoothedTr[^1], WilderSeries([.. dx])[^1]);
    }

    /// <summary>
    /// Wilder's smoothing across a whole series, seeded on the simple mean of the first
    /// period. Values before the seed are not defined and are never read.
    /// </summary>
    private static double[] WilderSeries(double[] x)
    {
        var outp = new double[x.Length];
        double seed = 0;

        for (var i = 0; i < WilderPeriod; i++)
        {
            seed += x[i];
        }

        outp[WilderPeriod - 1] = seed / WilderPeriod;

        for (var t = WilderPeriod; t < x.Length; t++)
        {
            outp[t] = ((outp[t - 1] * (WilderPeriod - 1)) + x[t]) / WilderPeriod;
        }

        return outp;
    }

    /// <summary>Signed distance of the last close from its own n-day average, as a fraction.</summary>
    private static double? DistanceFromAverage(double?[] cp, int window)
    {
        var ma = Average(cp, cp.Length - 1, window);

        return ma is not > 0 || cp[^1] is not { } last ? null : (last - ma.Value) / ma.Value;
    }

    /// <summary>Ratio of the last value to its own n-day average. One is participation exactly at its average.</summary>
    private static double? RatioToAverage(double?[] v, int window)
    {
        var mean = Average(v, v.Length - 1, window);

        return mean is not > 0 || v[^1] is not { } last ? null : last / mean.Value;
    }

    /// <summary>
    /// Signed distance of the close at <paramref name="at"/> from the highest adjusted
    /// high of the 252 bars ending there. At or below zero always.
    ///
    /// The adjusted high rather than the adjusted close, because a 52-week high means
    /// the high. The provider adjusts only the close, so the high is derived through
    /// the same factor [METRICS.md section 2].
    /// </summary>
    private static double? DistanceFrom52WeekHigh(double?[] cp, double?[] hp, int at)
    {
        if (at + 1 < Weeks52 || cp[at] is not { } close)
        {
            return null;
        }

        double high = 0;

        for (var i = at - Weeks52 + 1; i <= at; i++)
        {
            if (hp[i] is not { } h)
            {
                return null;
            }

            high = Math.Max(high, h);
        }

        return high <= 0 ? null : (close - high) / high;
    }

    /// <summary>
    /// The change in that distance over 20 trading dates. Positive means the name has
    /// closed the gap to its own high over the last month, which is D-11's change over
    /// level applied to a distance.
    /// </summary>
    private static double? Dist52WHighChange(double?[] cp, double?[] hp, int at)
    {
        var now = DistanceFrom52WeekHigh(cp, hp, at);
        var then = DistanceFrom52WeekHigh(cp, hp, at - SlopeWindow);

        return now is null || then is null ? null : now.Value - then.Value;
    }

    /// <summary>
    /// The slope of the 50 over 200 average ratio across the last 20 dates, normalised
    /// by that ratio's own mean so the value is scale free and comparable across names.
    ///
    /// Positive when the faster average is pulling away from the slower one, which is
    /// the trend confirmation S2 ranks on. The reading is stated in `METRICS.md` as a
    /// proposal: the name gives two windows and the word slope and no document defines
    /// the combination.
    /// </summary>
    private static double? MaSlope(double?[] cp)
    {
        var ratios = new double[SlopeWindow];

        for (var k = 0; k < SlopeWindow; k++)
        {
            var at = cp.Length - 1 - (SlopeWindow - 1 - k);
            var fast = Average(cp, at, Ma50);
            var slow = Average(cp, at, Ma200);

            if (fast is null || slow is not > 0)
            {
                return null;
            }

            ratios[k] = fast.Value / slow.Value;
        }

        return NormalisedSlope(ratios);
    }

    /// <summary>
    /// Ordinary least squares slope against the day index, divided by the series' own
    /// mean. Null when the mean is not positive, which no price ratio is.
    /// </summary>
    internal static double? NormalisedSlope(double[] y)
    {
        var n = y.Length;
        var meanIndex = (n - 1) / 2.0;
        double meanY = 0;

        foreach (var v in y)
        {
            meanY += v;
        }

        meanY /= n;

        double num = 0;
        double den = 0;

        for (var i = 0; i < n; i++)
        {
            var di = i - meanIndex;
            num += di * (y[i] - meanY);
            den += di * di;
        }

        return den <= 0 || meanY <= 0 ? null : num / den / meanY;
    }

    /// <summary>
    /// A breakout out of a base, which is two conditions rather than one.
    ///
    /// The range condition is what makes the window a base rather than a trend. Without
    /// it a name in a steady advance sets a new window high most days and the flag is
    /// on permanently, which carries no information and would put every strong trend
    /// into S2 twice. The definition is a proposal in `METRICS.md`: "base breakout"
    /// appears in `ARCHITECTURE.html` as an S2 ranking input and nowhere with a
    /// definition.
    /// </summary>
    private static bool? BaseBreakout(
        double?[] cp, double?[] hp, double?[] lp, int lookback, double maxRange)
    {
        var n = cp.Length;

        if (n < lookback + 1 || cp[^1] is not { } close)
        {
            return null;
        }

        double high = double.MinValue;
        double low = double.MaxValue;

        // The window ends one bar before the date being computed, so the breakout is
        // measured against what came before it rather than against itself.
        for (var i = n - 1 - lookback; i <= n - 2; i++)
        {
            if (hp[i] is not { } h || lp[i] is not { } l)
            {
                return null;
            }

            high = Math.Max(high, h);
            low = Math.Min(low, l);
        }

        if (low <= 0)
        {
            return null;
        }

        return (high - low) / low <= maxRange && close > high;
    }

    /// <summary>Mean of the window of <paramref name="window"/> values ending at <paramref name="at"/>, or null if any is absent.</summary>
    private static double? Average(double?[] v, int at, int window)
    {
        if (at + 1 < window || at < 0)
        {
            return null;
        }

        double sum = 0;

        for (var i = at - window + 1; i <= at; i++)
        {
            if (v[i] is not { } x)
            {
                return null;
            }

            sum += x;
        }

        return sum / window;
    }

    /// <summary>
    /// The trailing bars for every active universe member, in one statement over the
    /// whole table rather than a query per ticker.
    ///
    /// The universe comes from `security` rather than from `price_daily`, which carries
    /// every row the bulk feed returns and is roughly fifty thousand tickers a day
    /// against the universe's two and a half thousand.
    /// </summary>
    private static async Task<IReadOnlyList<(string Ticker, IReadOnlyList<Bar> History)>> SeriesAsync(
        StageContext context, int bars, CancellationToken ct)
    {
        var sql = $"""
            WITH universe AS (
                SELECT ticker FROM security WHERE is_active
            ),
            windowed AS (
                SELECT p.ticker, p.date, p.high, p.low, p.close, p.adj_close, p.volume,
                       row_number() OVER (PARTITION BY p.ticker ORDER BY p.date DESC) AS rn
                FROM price_daily p
                JOIN universe u ON u.ticker = p.ticker
                WHERE p.date <= {Literal(context.Date)}
            )
            SELECT ticker, date, high, low, close, adj_close, volume
            FROM windowed
            WHERE rn <= {bars.ToString(CultureInfo.InvariantCulture)}
            ORDER BY ticker, date;
            """;

        var rows = await context.Data.ReadAsync("price_daily", sql, ct).ConfigureAwait(false);

        var series = new List<(string, IReadOnlyList<Bar>)>();
        var current = new List<Bar>();
        string? ticker = null;

        foreach (var r in rows)
        {
            var t = (string) r[0]!;

            if (ticker is not null && !string.Equals(t, ticker, StringComparison.Ordinal))
            {
                series.Add((ticker, current));
                current = [];
            }

            ticker = t;

            current.Add(new Bar(
                DateOnly.FromDateTime((DateTime) r[1]!),
                (decimal?) r[2], (decimal?) r[3], (decimal?) r[4], (decimal?) r[5], (long?) r[6]));
        }

        if (ticker is not null)
        {
            series.Add((ticker, current));
        }

        return series;
    }

    /// <summary>
    /// The 20-day median dollar volume per ticker, through the definition C01 also
    /// reads so the universe and this column cannot disagree [<see cref="DollarVolume"/>].
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, decimal?>> MedianDollarVolumeAsync(
        StageContext context, CancellationToken ct)
    {
        var sql = $"""
            WITH universe AS (
                SELECT ticker FROM security WHERE is_active
            ),
            bars AS (
                SELECT p.ticker, p.close, p.volume,
                       row_number() OVER (PARTITION BY p.ticker ORDER BY p.date DESC) AS rn
                FROM price_daily p
                JOIN universe u ON u.ticker = p.ticker
                WHERE p.date <= {Literal(context.Date)}
            )
            SELECT ticker, {DollarVolume.MedianExpression} AS median_dollar_volume
            FROM bars
            WHERE rn <= {DollarVolume.WindowBars.ToString(CultureInfo.InvariantCulture)}
              AND {DollarVolume.RowFilter}
            GROUP BY ticker
            ORDER BY ticker;
            """;

        var rows = await context.Data.ReadAsync("price_daily", sql, ct).ConfigureAwait(false);
        var map = new Dictionary<string, decimal?>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            map[(string) r[0]!] = (decimal?) r[1];
        }

        return map;
    }

    /// <summary>One bar as stored, unadjusted except for <c>AdjClose</c>.</summary>
    public readonly record struct Bar(
        DateOnly Date, decimal? High, decimal? Low, decimal? Close, decimal? AdjClose, long? Volume);

    /// <summary>One <c>indicator_daily</c> row, in the column order the write uses.</summary>
    public readonly record struct Row(
        string Ticker, DateOnly Date,
        float? AtrPct, float? Adx14, float? Dist20Dma, float? Dist200Dma, float? Dist52WHigh,
        float? Dist52WHigh20DChange, float? VolumeVs50DAvg, float? Ma50200Slope,
        bool? BaseBreakoutFlag, decimal? MedianDollarVolume20D,
        float? RsChange21D = null, float? RsChange63D = null, float? Rs21D63DChange = null,
        float? Rs20DSlope = null, float? RsChangeVsSector = null);

    /// <summary>The benchmark's adjusted closes by date, read from `price_daily` like any other series.</summary>
    private static async Task<IReadOnlyDictionary<DateOnly, double>> BenchmarkAsync(
        StageContext context, int bars, CancellationToken ct)
    {
        var sql = $"""
            SELECT date, adj_close
            FROM (
                SELECT date, adj_close,
                       row_number() OVER (ORDER BY date DESC) AS rn
                FROM price_daily
                WHERE ticker = '{Benchmark}' AND date <= {Literal(context.Date)} AND adj_close IS NOT NULL
            ) w
            WHERE rn <= {bars.ToString(CultureInfo.InvariantCulture)}
            ORDER BY date;
            """;

        var rows = await context.Data.ReadAsync("price_daily", sql, ct).ConfigureAwait(false);
        var map = new Dictionary<DateOnly, double>();

        foreach (var r in rows)
        {
            map[DateOnly.FromDateTime((DateTime) r[0]!)] = (double) (decimal) r[1]!;
        }

        return map;
    }

    /// <summary>Sector per active member, which is nullable and is null rather than guessed [C01].</summary>
    private static async Task<IReadOnlyDictionary<string, string?>> SectorsAsync(
        StageContext context, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "security", "SELECT ticker, sector FROM security WHERE is_active ORDER BY ticker;", ct)
            .ConfigureAwait(false);

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            map[(string) r[0]!] = r[1] as string;
        }

        return map;
    }

    /// <summary>
    /// One index per sector, equal-weighted across that sector's own universe members
    /// and rebalanced daily, chained from the mean of their daily returns.
    ///
    /// **Not a sector ETF, and the reason is the constituents rather than the
    /// convenience.** The sector SPDRs are S&amp;P 500 sector slices and this universe is
    /// mostly outside that index, so measuring a $500M industrial against XLI measures
    /// it against Honeywell and Caterpillar. That imports a large-cap benchmark into a
    /// small-cap universe and tilts the trend screen by regime, which is the megacap
    /// drift `ARCHITECTURE.html` §20 says this design exists to prevent, arriving
    /// through a column nobody would look at. Needing no mapping is true and is not
    /// why [D-77's sibling reasoning, `METRICS.md` §2].
    ///
    /// A date on which the sector has fewer than the configured minimum members is left
    /// out of the index entirely, so a ratio spanning it is null rather than computed
    /// against one name's noise.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<DateOnly, double>>>
        SectorCompositesAsync(StageContext context, int bars, int minMembers, CancellationToken ct)
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
                SELECT sector, ticker, date, adj_close
                FROM windowed
                WHERE rn <= {bars.ToString(CultureInfo.InvariantCulture)}
            ),
            rets AS (
                SELECT sector, date,
                       (adj_close / lag(adj_close) OVER (PARTITION BY ticker ORDER BY date)) - 1 AS r
                FROM kept
            )
            SELECT sector, date, avg(r) AS mean_return, count(*) AS members
            FROM rets
            WHERE r IS NOT NULL
            GROUP BY sector, date
            HAVING count(*) >= {minMembers.ToString(CultureInfo.InvariantCulture)}
            ORDER BY sector, date;
            """;

        var rows = await context.Data.ReadAsync("price_daily", sql, ct).ConfigureAwait(false);

        var composites = new Dictionary<string, IReadOnlyDictionary<DateOnly, double>>(StringComparer.Ordinal);
        var current = new Dictionary<DateOnly, double>();
        string? sector = null;
        var level = 1.0;

        foreach (var r in rows)
        {
            var s = (string) r[0]!;

            if (sector is not null && !string.Equals(s, sector, StringComparison.Ordinal))
            {
                composites[sector] = current;
                current = [];
                level = 1.0;
            }

            sector = s;
            level *= 1 + (double) (decimal) r[2]!;
            current[DateOnly.FromDateTime((DateTime) r[1]!)] = level;
        }

        if (sector is not null)
        {
            composites[sector] = current;
        }

        return composites;
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
