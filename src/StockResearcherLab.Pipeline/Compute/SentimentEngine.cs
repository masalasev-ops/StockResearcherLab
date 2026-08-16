using System.Globalization;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Compute;

/// <summary>
/// C35. The three forms the screens rank on, derived from what the provider sends
/// [D-78].
///
/// <c>sentiment_daily</c> holds an article count and a tone. S3 ranks on the three
/// columns here and on nothing else, S5's stabilisation gate reads the first two, and
/// the dossier's fixed core carries two. Ingest grain follows the source; consumption
/// grain follows the screen [D-61].
///
/// **Windows here are calendar days, not trading dates** [`METRICS.md` §1.4].
/// <c>sentiment_daily</c> has a row only on days that carried news, so counting rows
/// would make a ninety-day baseline mean ninety news days, which on a thinly covered
/// name is several years.
///
/// **<c>article_count</c> zero-fills across an absent day and <c>sentiment_score</c>
/// does not.** The two rules are the same rule applied to absences that mean
/// different things. C04 covers the whole universe every night with no pre-selection,
/// so an absent day is no news rather than no lookup and reading the count as zero is
/// reading the fact. There is no tone where there are no articles, and zero-filling
/// the score would say the coverage was exactly neutral, which is a different claim
/// from the coverage not existing. It would also pull every thinly covered name
/// toward zero in proportion to how thinly covered it is, which is a size proxy
/// arriving where D-12 exists to keep one out.
///
/// **Ticker-partitioned**, so this reads once, computes in C# and writes once. Every
/// metric is a function of one ticker's own history, which is D-12 stated as a shape
/// rather than as a rule: a cross-sectional article count measures analyst coverage,
/// which is a size proxy, and against its own baseline it measures change in
/// attention.
/// </summary>
public sealed class SentimentEngine : IStage
{
    /// <summary>
    /// The columns this stage writes. The percentile columns on this table belong to
    /// C11 and are absent here deliberately [D-77].
    /// </summary>
    public static readonly string[] Columns =
    [
        "ticker", "date",
        "article_count_z_own_90d", "sentiment_delta_7v30", "sentiment_7d_level",
    ];

    private static readonly string[] ConflictTarget = ["ticker", "date"];

    /// <summary>
    /// The baseline window, in calendar days. A constant rather than a config key,
    /// because the column is named <c>article_count_z_own_90d</c> and a tunable 90
    /// beside it would be a second place for the number to live
    /// [`FlowEngine.WindowDays` is the precedent].
    /// </summary>
    public const int BaselineDays = 90;

    /// <summary>The short window, named in <c>sentiment_delta_7v30</c> and in <c>sentiment_7d_level</c>.</summary>
    public const int ShortWindowDays = 7;

    /// <summary>The long window, named in <c>sentiment_delta_7v30</c>.</summary>
    public const int LongWindowDays = 30;

    public string Name => "SentimentEngine";

    public IReadOnlyList<string> ReadSet { get; } = ["sentiment_daily", "security_daily"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("sentiment_derived_daily", WriteOperation.Insert, Columns)];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var minBaselineDays = (int) await LongAsync(context, "sentiment.min_baseline_days", ct)
            .ConfigureAwait(false);

        var universe = await UniverseAsync(context, ct).ConfigureAwait(false);
        var history = await HistoryAsync(context, ct).ConfigureAwait(false);

        var rows = new List<Row>(universe.Count);

        foreach (var ticker in universe)
        {
            rows.Add(Compute(ticker, context.Date, history.GetValueOrDefault(ticker, []), minBaselineDays));
        }

        // The universe read is already ordinal; this states the property at the point
        // that relies on it, because write order reaches output [CLAUDE.md section 6].
        rows.Sort(static (a, b) => string.CompareOrdinal(a.Ticker, b.Ticker));

        var written = await context.Data.BulkUpsertAsync(
            "sentiment_derived_daily", Columns, ConflictTarget,
            async (w, c) =>
            {
                foreach (var r in rows)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(r.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Date, c).ConfigureAwait(false);
                    await w.WriteAsync(r.ArticleCountZOwn90D, c).ConfigureAwait(false);
                    await w.WriteAsync(r.SentimentDelta7V30, c).ConfigureAwait(false);
                    await w.WriteAsync(r.Sentiment7DLevel, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);

        return new StageResult(written, "ok", Detail(rows, minBaselineDays));
    }

    /// <summary>
    /// What the run log says about the columns that carry null, read off the rows
    /// rather than inferred [C34's precedent].
    ///
    /// The baseline floor is counted separately from the nulls it causes, because a
    /// name the ingest has not reached and a name whose coverage is genuinely flat
    /// both produce a null z-score and they are different facts.
    /// </summary>
    private static string Detail(IReadOnlyList<Row> rows, int minBaselineDays)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} names over a {1:N0} calendar day baseline. {2:N0} carry fewer than {3:N0} days " +
            "with a row and are null on all three. Null: {4:N0} article_count_z_own_90d, " +
            "{5:N0} sentiment_delta_7v30, {6:N0} sentiment_7d_level",
            rows.Count,
            BaselineDays,
            rows.Count(r => !r.ClearedBaselineFloor),
            minBaselineDays,
            rows.Count(r => r.ArticleCountZOwn90D is null),
            rows.Count(r => r.SentimentDelta7V30 is null),
            rows.Count(r => r.Sentiment7DLevel is null));

    /// <summary>
    /// All three metrics for one ticker. Public so the reference test computes through
    /// the same path the stage does rather than through a copy of it.
    /// </summary>
    /// <param name="history">
    /// The rows <c>sentiment_daily</c> holds for this ticker inside
    /// [<c>date</c> - <see cref="BaselineDays"/>, <c>date</c>], in any order. Days
    /// with no row are absent rather than present with a zero, which is the
    /// distinction the two zero-fill rules turn on.
    /// </param>
    public static Row Compute(
        string ticker, DateOnly date, IReadOnlyList<Day> history, int minBaselineDays)
    {
        var baselineStart = date.AddDays(-BaselineDays);
        var baselineEnd = date.AddDays(-1);

        var byDate = new Dictionary<DateOnly, Day>();

        foreach (var day in history)
        {
            byDate[day.Date] = day;
        }

        // **Before the ingest reached a ticker, an absent day means neither.** The
        // zero-fill above reads an absent day as no news, which is only true once C04
        // has been covering the name, so the count of days that actually carry a row
        // is what separates the two [`METRICS.md` §4.1]. Counted over the baseline
        // window, which is the widest of the three and the one the zero-fill applies
        // to.
        var daysWithARow = byDate.Keys.Count(d => d >= baselineStart && d <= baselineEnd);

        if (daysWithARow < minBaselineDays)
        {
            return new Row(ticker, date, null, null, null, ClearedBaselineFloor: false);
        }

        var shortMean = MeanScore(byDate, date.AddDays(-(ShortWindowDays - 1)), date);
        var longMean = MeanScore(byDate, date.AddDays(-(LongWindowDays - 1)), date);

        return new Row(
            ticker, date,
            ArticleCountZOwn90D: (float?) ArticleCountZ(byDate, baselineStart, baselineEnd, date),
            SentimentDelta7V30: shortMean is { } s && longMean is { } l ? (float) (s - l) : null,
            Sentiment7DLevel: (float?) shortMean,
            ClearedBaselineFloor: true);
    }

    /// <summary>
    /// Today's article count against the ticker's own ninety-day baseline, in standard
    /// deviations.
    ///
    /// **The baseline excludes the date itself**, so today's spike is measured against
    /// a history that does not contain it. Including it damps the very signal the
    /// metric exists to catch, by roughly one part in ninety in the mean and more in
    /// the deviation.
    ///
    /// **Zero-filled**, so a day with no row counts as no articles. A row present with
    /// no count is a different thing and nulls the answer: that is an unknown, where
    /// an absent day is a measurement.
    ///
    /// Null when the deviation is zero, which is a ticker whose count never moves
    /// across the window and therefore has no scale to express a deviation in.
    /// </summary>
    private static double? ArticleCountZ(
        IReadOnlyDictionary<DateOnly, Day> byDate, DateOnly start, DateOnly end, DateOnly date)
    {
        var counts = new List<double>(BaselineDays);

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (!byDate.TryGetValue(d, out var day))
            {
                counts.Add(0);
                continue;
            }

            if (day.ArticleCount is not { } count)
            {
                return null;
            }

            counts.Add(count);
        }

        double? today = byDate.TryGetValue(date, out var onTheDay)
            ? onTheDay.ArticleCount
            : 0;

        if (today is null || counts.Count == 0)
        {
            return null;
        }

        var mean = counts.Average();
        double variance = 0;

        foreach (var c in counts)
        {
            variance += (c - mean) * (c - mean);
        }

        // Population rather than sample. The window is the whole of the baseline
        // being described rather than a draw from something wider.
        var sigma = Math.Sqrt(variance / counts.Count);

        return sigma <= 0 ? null : (today.Value - mean) / sigma;
    }

    /// <summary>
    /// The mean tone over an inclusive calendar range, across the rows that carry one.
    ///
    /// **Not zero-filled**, per the rule this whole component turns on. A day with no
    /// row contributes nothing rather than contributing a neutral reading, and so does
    /// a row whose score is absent: there is no tone in either case, and the two
    /// differ from a measured zero, which would be coverage that really was neutral.
    ///
    /// Null when the range carries no scored row at all.
    /// </summary>
    private static double? MeanScore(
        IReadOnlyDictionary<DateOnly, Day> byDate, DateOnly start, DateOnly end)
    {
        double sum = 0;
        var n = 0;

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (byDate.TryGetValue(d, out var day) && day.SentimentScore is { } score)
            {
                sum += score;
                n++;
            }
        }

        return n == 0 ? null : sum / n;
    }

    // ------------------------------------------------------------------ reads ---

    /// <summary>The active universe, ordinal. C35 iterates it rather than iterating what the ingest happened to reach.</summary>
    private static async Task<IReadOnlyList<string>> UniverseAsync(
        StageContext context, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "security_daily", Universe.MembersAsOf(context.Date), ct)
            .ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToList();
    }

    /// <summary>
    /// Every stored day inside the baseline window for every active universe member,
    /// in one statement rather than a query per ticker.
    ///
    /// The window runs from ninety calendar days before the date through the date
    /// itself, because the baseline ends the day before and the z-score's numerator is
    /// the date's own count.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<Day>>> HistoryAsync(
        StageContext context, CancellationToken ct)
    {
        var sql = $"""
            WITH universe AS (
                SELECT m.ticker FROM {Universe.AsOf(context.Date)} m WHERE m.is_active
            )
            SELECT s.ticker, s.date, s.article_count, s.sentiment_score
            FROM sentiment_daily s
            JOIN universe u ON u.ticker = s.ticker
            WHERE s.date >= {Literal(context.Date.AddDays(-BaselineDays))}
              AND s.date <= {Literal(context.Date)}
            ORDER BY s.ticker, s.date;
            """;

        var rows = await context.Data.ReadAsync("sentiment_daily", sql, ct).ConfigureAwait(false);

        var byTicker = new Dictionary<string, IReadOnlyList<Day>>(StringComparer.Ordinal);
        var current = new List<Day>();
        string? ticker = null;

        foreach (var r in rows)
        {
            var t = (string) r[0]!;

            if (ticker is not null && !string.Equals(t, ticker, StringComparison.Ordinal))
            {
                byTicker[ticker] = current;
                current = [];
            }

            ticker = t;

            current.Add(new Day(
                DateOnly.FromDateTime((DateTime) r[1]!), (int?) r[2], (float?) r[3]));
        }

        if (ticker is not null)
        {
            byTicker[ticker] = current;
        }

        return byTicker;
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

    /// <summary>One stored day, as <c>sentiment_daily</c> holds it. Both measures are nullable and mean different things absent.</summary>
    public readonly record struct Day(DateOnly Date, int? ArticleCount, float? SentimentScore);

    /// <summary>
    /// One <c>sentiment_derived_daily</c> row, in the column order the write uses.
    /// <see cref="ClearedBaselineFloor"/> is not a column: it separates the two
    /// reasons a row is null for the run log.
    /// </summary>
    public readonly record struct Row(
        string Ticker, DateOnly Date,
        float? ArticleCountZOwn90D, float? SentimentDelta7V30, float? Sentiment7DLevel,
        bool ClearedBaselineFloor);
}
