using System.Globalization;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Monitoring;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// C28. The two diversity guarantees measured rather than assumed: the megacap share of
/// recent candidates and the distinct ticker count over sixty days
/// [`ARCHITECTURE.html` §03].
///
/// **It reads <c>security_daily</c> and not <c>security</c>, and that is the whole point
/// of this component existing** [D-92, D-74, §8's B3]. `security` holds one row per
/// ticker carrying today's bucket. A megacap share over a 2022 window computed from it
/// classifies 2022 with 2026 buckets, and every name that has since grown out of mid into
/// large is counted as a megacap it was not at the time. That is the defect D-92 exists
/// to prevent, arriving in the one component whose entire job is to notice a wrong number
/// that looks right.
///
/// **It labels and never narrows, like C12.** An alert is a row, not a halt. The
/// guarantees are design constraints held by the quota at allocation time, and this
/// component is the instrument that says whether they held.
///
/// **It sits at the root of the project rather than in a layer folder**, because §02
/// puts it outside the layers with C26, C27 and C32. The folder names match the layer
/// names in `ARCHITECTURE.html` [`CLAUDE.md` §4], and a component the document places
/// outside them has no folder to be in.
/// </summary>
public sealed class ConcentrationMonitor : IStage
{
    public string Name => "ConcentrationMonitor";

    /// <summary>
    /// §3's cell exactly.
    ///
    /// <c>position</c> is declared and not read. The cell names it and the conformance
    /// test enforces without exemption only the other direction, a table read and not
    /// declared. What this component measures is a property of the candidate set rather
    /// than of the book [`SCREEN_LIFECYCLE.md` §4.5], and a component that read positions
    /// to judge candidate diversity would be answering a different question.
    /// </summary>
    public IReadOnlyList<string> ReadSet { get; } =
    [
        "candidate_set", "position", "security_daily",
    ];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new("alert", WriteOperation.Delete, Columns),
        new("alert", WriteOperation.Insert, Columns),
    ];

    public static readonly string[] Columns = ["date", "alert_type", "detail", "acknowledged"];

    /// <summary>
    /// The two windows, in candidate dates.
    ///
    /// **Both numbers are authored** [`ARCHITECTURE.html` §18]. That failure table
    /// states the megacap condition as "over 20 days" and the distinct-ticker condition
    /// as "over 60 days". 4.11 reported the twenty as unauthored and that was wrong; the
    /// correction is at Q.4 and in D-126, and what remains open is narrower and is
    /// stated on <see cref="AlertTypes.MegacapWindowDates"/>.
    ///
    /// Held on <see cref="AlertTypes"/> beside the vocabulary rather than here, so the
    /// window and the alert type that carries it are one declaration.
    /// </summary>
    public const int MegacapWindowDays = AlertTypes.MegacapWindowDates;

    public const int DistinctWindowDays = AlertTypes.DistinctWindowDates;

    /// <summary>
    /// The bucket the megacap share counts. `large` is `UniverseBuilder`'s name for
    /// $10B and above, which is what §06's quota calls the large bucket and what D-7's
    /// bound of ten of forty is stated over.
    /// </summary>
    public const string MegacapBucket = "large";

    /// <summary>
    /// The two alert types, taken from the closed vocabulary rather than spelled here.
    ///
    /// 4.11 wrote these as two string literals and reported that nothing in the corpus
    /// named them. D-126 names them and migration `0021` holds the same two on the
    /// column, so a third spelling of one of these conditions now fails the insert
    /// instead of being stored [Q.4].
    /// </summary>
    public static string MegacapAlert => AlertTypes.Name(AlertType.MegacapShare);

    public static string DistinctTickersAlert => AlertTypes.Name(AlertType.DistinctTickers60d);

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var megacapMax = ConfigValue.Double(
            await context.Config.RequireAsync("monitor.megacap_share_max", context.Date, ct)
                .ConfigureAwait(false));

        var distinctMin = (int) ConfigValue.Long(
            await context.Config.RequireAsync("monitor.distinct_tickers_60d_min", context.Date, ct)
                .ConfigureAwait(false));

        var measured = await MeasureAsync(context, ct).ConfigureAwait(false);

        await context.Data.WriteAsync(
            "alert", WriteOperation.Delete,
            $"DELETE FROM alert WHERE date = {Literal(context.Date)} AND alert_type IN " +
            $"({Quote(MegacapAlert)}, {Quote(DistinctTickersAlert)});",
            parameters: null, ct).ConfigureAwait(false);

        long raised = 0;

        if (measured.CandidateDates > 0 && measured.MegacapShare > megacapMax)
        {
            raised += await RaiseAsync(
                context, MegacapAlert,
                "megacap share " + Pct(measured.MegacapShare) + " over the last " +
                measured.CandidateDates.ToString(CultureInfo.InvariantCulture) +
                " candidate dates, above " + Pct(megacapMax) +
                ". Buckets are point-in-time from security_daily [D-92].",
                ct).ConfigureAwait(false);
        }

        if (measured.DistinctDates > 0 && measured.DistinctTickers < distinctMin)
        {
            raised += await RaiseAsync(
                context, DistinctTickersAlert,
                measured.DistinctTickers.ToString(CultureInfo.InvariantCulture) +
                " distinct tickers over the last " +
                measured.DistinctDates.ToString(CultureInfo.InvariantCulture) +
                " candidate dates, below " + distinctMin.ToString(CultureInfo.InvariantCulture) + ".",
                ct).ConfigureAwait(false);
        }

        // The measurement is reported whether or not it alerted, so a quiet night says
        // what it measured rather than only that it found nothing [CLAUDE.md section 1].
        var detail =
            "megacap " + Pct(measured.MegacapShare) + " over " +
            measured.CandidateDates.ToString(CultureInfo.InvariantCulture) + " dates, " +
            measured.DistinctTickers.ToString(CultureInfo.InvariantCulture) +
            " distinct over " + measured.DistinctDates.ToString(CultureInfo.InvariantCulture) +
            " dates, " + raised.ToString(CultureInfo.InvariantCulture) + " alert(s)";

        // **Zero alerts is the good night**, and section 18 says so in the two rows that
        // give this component its behaviour: alert when the megacap share is above a
        // third, alert when distinct tickers fall below 250. Neither says halt when
        // neither fired, so the stage says its own zero was expected rather than the
        // runner keeping a list of stages allowed to write nothing.
        return new StageResult(raised, "ok", detail, ZeroRowsExpected: true);
    }

    /// <summary>The two figures, and the window each was measured over.</summary>
    public readonly record struct Concentration(
        double MegacapShare, int CandidateDates, int DistinctTickers, int DistinctDates);

    private static async Task<Concentration> MeasureAsync(StageContext context, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "candidate_set", MeasureSql(context.Date), ct).ConfigureAwait(false);

        var row = rows[0];

        return new Concentration(
            row[0] is null or DBNull ? 0d : Convert.ToDouble(row[0], CultureInfo.InvariantCulture),
            Convert.ToInt32(row[1], CultureInfo.InvariantCulture),
            Convert.ToInt32(row[2], CultureInfo.InvariantCulture),
            Convert.ToInt32(row[3], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Both windows in one statement.
    ///
    /// **The bucket is read from <c>security_daily</c> as of the candidate's own date and
    /// never from <c>security</c>** [D-92, D-74]. `security` holds one row per ticker
    /// carrying today's bucket, so the same window classified from it counts every name
    /// that has since grown out of mid into large as a megacap it was not at the time.
    /// The share it produces is wrong and looks entirely ordinary, which is the failure
    /// this component exists to catch happening inside this component.
    ///
    /// <c>candidate_set.size_bucket</c> carries C14's stamp of the same fact and is
    /// deliberately not used. It is nullable, and a share computed over a column that can
    /// be absent would quietly narrow its own denominator.
    /// </summary>
    public static string MeasureSql(DateOnly date)
    {
        var d = Literal(date);

        return $"""
            WITH recent AS (
                SELECT DISTINCT date FROM candidate_set
                WHERE date <= {d}
                ORDER BY date DESC
                LIMIT {Int(DistinctWindowDays)}
            ),
            megacap_dates AS (
                SELECT date FROM recent ORDER BY date DESC LIMIT {Int(MegacapWindowDays)}
            ),
            megacap AS (
                SELECT
                    count(*) FILTER (WHERE s.size_bucket = {Quote(MegacapBucket)})::numeric
                        / NULLIF(count(*), 0) AS share,
                    (SELECT count(*) FROM megacap_dates)::int AS dates
                FROM candidate_set c
                JOIN megacap_dates m ON m.date = c.date
                LEFT JOIN LATERAL (
                    SELECT sd.size_bucket
                    FROM security_daily sd
                    WHERE sd.ticker = c.ticker AND sd.date <= c.date
                    ORDER BY sd.date DESC
                    LIMIT 1
                ) s ON TRUE
            ),
            distinct_names AS (
                SELECT
                    count(DISTINCT c.ticker)::int AS tickers,
                    (SELECT count(*) FROM recent)::int AS dates
                FROM candidate_set c
                JOIN recent r ON r.date = c.date
            )
            SELECT megacap.share, megacap.dates, distinct_names.tickers, distinct_names.dates
            FROM megacap, distinct_names;
            """;
    }

    private static async Task<long> RaiseAsync(
        StageContext context, string type, string detail, CancellationToken ct)
        => await context.Data.WriteAsync(
            "alert", WriteOperation.Insert,
            "INSERT INTO alert (date, alert_type, detail, acknowledged) VALUES (" +
            Literal(context.Date) + ", " + Quote(type) + ", " + Quote(detail) + ", FALSE);",
            parameters: null, ct).ConfigureAwait(false);

    private static string Pct(double share)
        => (share * 100d).ToString("0.0", CultureInfo.InvariantCulture) + " percent";

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
}
