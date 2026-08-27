using System.Globalization;
using StockResearcherLab.Api.Contracts;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;

namespace StockResearcherLab.Api;

/// <summary>
/// C36. What the store holds for one ticker on one date, and where each number came
/// from [D-109].
///
/// **A reader, not a stage.** It takes no date-and-config-version, nothing schedules it
/// and it produces nothing to replay. What it shares with a stage is the only thing
/// that matters here: it declares what it reads as data, and `ARCHITECTURE.html` §3
/// carries that declaration where the conformance test can hold it against this class
/// in both directions [D-74]. A reader outside the catalogue is a reader nothing
/// checks, and D-74's record says what that cost the last time.
///
/// **Nothing here computes anything.** Every figure is read from a store; where one
/// would have to be derived, the panel carries the inputs and says so. Selecting,
/// ordering and filtering rows is reading, including taking the most recent row at or
/// before a date, which is the shape every stage already uses. Arithmetic over stored
/// values is computing, and so is a comparison whose answer this then labels.
///
/// **The read-only guarantee is structural rather than promised.** Data arrives through
/// the same <see cref="IStageData"/> route a stage uses, behind a
/// <see cref="DeclaredAccess"/> built from <see cref="ReadSet"/> and an empty write set,
/// so an undeclared table throws before a connection opens and any write at all throws
/// as undeclared. That is the guard `StageRunnerTests` proves with the trespassing-stage
/// fixture, reused rather than rebuilt.
///
/// **It lives in the Api, which never references Pipeline** [`CLAUDE.md` §4], so no page
/// can invoke a stage. `Universe.AsOf` moved to `Core` at 3.5.1 for exactly this: the
/// membership read is one statement with one copy rather than a second one here.
/// </summary>
public sealed class RecordInspector : IReadOwner
{
    private readonly IStageData _data;
    private readonly IConfigStore _config;

    public RecordInspector(string connectionString)
        : this(new StageData(connectionString, Access()), new ConfigStore(connectionString))
    {
    }

    /// <summary>
    /// The declared access the production route runs behind: this read set, and an
    /// empty write set.
    ///
    /// **Public so the test asserts over the object the constructor uses rather than
    /// over a reconstruction of it.** A test that rebuilt the same arguments would pass
    /// while the constructor passed different ones, which is the shape of every
    /// declaration this repository has found disagreeing with its code.
    /// </summary>
    public static DeclaredAccess Access() => new(ComponentName, Tables, []);

    /// <summary>
    /// The seam the tests use. It takes the two routes rather than a connection string
    /// so a test can exercise the panel logic without a store, and so the empty write
    /// set stays this class's own statement rather than something a caller supplies.
    /// </summary>
    public RecordInspector(IStageData data, IConfigStore config)
    {
        _data = data;
        _config = config;
    }

    private const string ComponentName = "RecordInspector";

    /// <summary>
    /// Declared as data and held against §3's Reads cell in both directions.
    ///
    /// It grows one checkpoint at a time rather than being declared whole in advance: a
    /// declaration naming a table nothing reads yet is a claim the conformance test
    /// would pass over, and the point of the check is that the cell and the code say the
    /// same thing at every commit.
    /// </summary>
    private static readonly string[] Tables =
    [
        "security", "security_daily", "universe_rejection",
        "indicator_daily", "valuation_daily", "flow_daily", "sentiment_derived_daily",
        "percentile_cell_daily", "percentile_cell_coverage",
        "price_daily", "fundamental_snapshot", "sentiment_daily", "insider_transaction",
        "market_context_daily",
        "headline", "news_digest",
    ];

    /// <summary>
    /// The four metric stores and the metrics ranked on each, in the order C11 declares
    /// them.
    ///
    /// **Stated here rather than taken from `PercentileEngine.Sources`**, and that is a
    /// second list this repository would normally refuse. It is accepted because the
    /// alternative is worse: the Api may never reference Pipeline, which is what
    /// structurally stops a page invoking a stage [`CLAUDE.md` §4], so importing the real
    /// list would cost the guarantee the whole read-only surface rests on. What keeps the
    /// two in step is a test, `MetricSourceParityTests`, which reads both and fails when they
    /// diverge, and which is in the test project because that is the one place that sees
    /// both.
    /// </summary>
    public static readonly (string Table, string[] Metrics)[] MetricSources =
    [
        ("indicator_daily", [
            "atr_pct", "adx14", "dist_20dma", "dist_200dma", "dist_52w_high",
            "dist_52w_high_20d_change", "rs_change_21d", "rs_change_63d",
            "rs_21d_63d_change", "rs_20d_slope", "rs_change_vs_sector",
            "volume_vs_50d_avg", "ma50_200_slope", "median_dollar_volume_20d",
        ]),
        ("valuation_daily", [
            "fcf_yield", "ev_ebit", "ev_ebit_vs_own_5y", "roic", "roic_4q_change",
            "gross_margin_4q_change", "net_debt_ebitda", "accruals",
            "share_count_change", "revenue_growth_4q_trend",
        ]),
        ("flow_daily", [
            "insider_net_90d_usd", "distinct_buyer_count", "inst_ownership_change",
        ]),
        ("sentiment_derived_daily", [
            "article_count_z_own_90d", "sentiment_delta_7v30", "sentiment_7d_level",
        ]),
    ];

    /// <summary>
    /// D-4's criteria, in C01's own test order, so the panel lists them the way the
    /// evaluation reaches them.
    ///
    /// **Named here and resolved as of the viewed date**, never as of now [D-43,
    /// INVARIANT 13]. `universe.pool_statement_timeout_seconds` is deliberately absent:
    /// it bounds a statement rather than admitting or rejecting a name.
    /// </summary>
    public static readonly string[] CriterionKeys =
    [
        "universe.min_price",
        "universe.min_adv_20d",
        "universe.min_history_days",
        "universe.min_market_cap",
        "fundamentals.min_clean_gaps_for_substitution",
        "universe.bucket_large_floor",
        "universe.bucket_mid_floor",
    ];

    public string Name => ComponentName;

    public IReadOnlyList<string> ReadSet => Tables;

    /// <summary>The whole record for one name on one date. Panels are added a checkpoint at a time.</summary>
    public async Task<RecordView> ReadAsync(string ticker, DateOnly date, CancellationToken ct = default)
    {
        var membership = await MembershipAsync(ticker, date, ct).ConfigureAwait(false);

        return new RecordView(
            ticker, date, membership,
            await MetricsAsync(ticker, date, membership.InForce, ct).ConfigureAwait(false),
            await InputsAsync(ticker, date, ct).ConfigureAwait(false),
            await MarketAsync(date, membership.InForce?.Sector, ct).ConfigureAwait(false),
            await DigestAsync(ticker, date, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// What the digest step read for this name on this date, and what came back [D-134,
    /// 5.9].
    ///
    /// **The pool is unfiltered and the window is marked rather than applied.** C33 reads
    /// `headline` inside `digest.lookback_days` on `published_at`; this reads every row
    /// the table holds for the ticker and date and lets the statement say which of them
    /// the window admits. Applying the filter here would be C33's rule written a second
    /// time, and a row stored outside the window would disappear from the panel with
    /// nothing said about why.
    ///
    /// **Which of the admitted articles were sent is not recorded.** D-143 selects
    /// newest-first up to `digest.max_input_tokens` at run time, against an estimate
    /// rather than a tokenizer, and no column holds the outcome. The two bounds are shown
    /// beside the pool and the selection is left unstated, which is this page's rule
    /// applied to the one figure it cannot honestly show [D-109].
    ///
    /// **Both keys resolve as of the viewed date** [D-43, INVARIANT 13]. A panel reading
    /// today's lookback beside a digest written under a different one would mark the wrong
    /// rows in-window and look right doing it.
    /// </summary>
    public async Task<DigestPanel> DigestAsync(string ticker, DateOnly date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        var lookback = (int) await LongAsync("digest.lookback_days", date, 7, ct).ConfigureAwait(false);
        var maxInput = (int) await LongAsync("digest.max_input_tokens", date, 6_000, ct).ConfigureAwait(false);

        var pool = await PoolAsync(ticker, date, lookback, ct).ConfigureAwait(false);
        var digest = await DigestRowAsync(ticker, date, ct).ConfigureAwait(false);

        return new DigestPanel(
            lookback, maxInput, pool, digest,
            DigestOutcomes.Name(DigestOutcomes.Classify(digest is not null, digest?.Text)));
    }

    /// <summary>
    /// Every article `headline` holds for the ticker and date, newest first.
    ///
    /// **The window bound is built in the statement rather than in C#**, so the panel does
    /// no date arithmetic of its own and the comparison is the one C33 makes: an instant
    /// against midnight UTC on the lookback's first day. It is written `AT TIME ZONE 'UTC'`
    /// rather than by concatenating a date into a literal, because the second reads the
    /// server's `DateStyle` and this server's is `ISO, MDY` by setting rather than by
    /// guarantee. A null `published_at` is inside
    /// no window and sorts last rather than being dropped, because C29 stored it and a
    /// pool that hid it would explain nothing about a null digest [D-131].
    /// </summary>
    private async Task<IReadOnlyList<DigestArticle>> PoolAsync(
        string ticker, DateOnly date, int lookback, CancellationToken ct)
    {
        var days = lookback.ToString(CultureInfo.InvariantCulture);

        var rows = await _data.ReadAsync(
            "headline",
            $"""
             SELECT published_at, title, source, url, content,
                    published_at IS NOT NULL
                      AND published_at >= ((DATE '{Iso(date)}' - {days})::timestamp AT TIME ZONE 'UTC'),
                    content IS NOT NULL
             FROM headline
             WHERE ticker = {Literal(ticker)} AND date = DATE '{Iso(date)}'
             ORDER BY published_at DESC NULLS LAST, title;
             """,
            ct).ConfigureAwait(false);

        return
        [
            // Through `StoredInstant` and not a cast: the driver returns `timestamptz` as
            // a `DateTime`, so `as DateTimeOffset?` is null for every row and the panel
            // would show an absent publication instant beside a window expression that
            // read the same column [5.9].
            .. rows.Select(r => new DigestArticle(
                StoredInstant.From(r[0]),
                r[5] as bool? ?? false,
                r[1] as string,
                r[2] as string,
                r[3] as string,
                r[6] as bool? ?? false,
                r[4] as string)),
        ];
    }

    /// <summary>
    /// The `news_digest` row, or null where the night wrote none for this name.
    ///
    /// The grain is ticker by date and the table has that primary key, so this is a point
    /// read rather than an as-of pick: a digest belongs to the night that made it and
    /// there is no most-recent-at-or-before reading of one.
    /// </summary>
    private async Task<DigestRow?> DigestRowAsync(string ticker, DateOnly date, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "news_digest",
            $"""
             SELECT digest_text, provider, model_name, was_rotation
             FROM news_digest
             WHERE ticker = {Literal(ticker)} AND date = DATE '{Iso(date)}';
             """,
            ct).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return null;
        }

        return new DigestRow(
            rows[0][0] as string,
            rows[0][1] as string ?? string.Empty,
            rows[0][2] as string ?? string.Empty,
            rows[0][3] as bool? ?? false);
    }

    /// <summary>
    /// Breadth, the regime label and the name's own sector composite, on one line.
    ///
    /// **The composite is read out of the stored jsonb by key rather than recomputed
    /// from any price series.** C10 writes `sector_relative_strength` as an object of
    /// sector to trailing relative return with its keys in ordinal order, and taking one
    /// entry out of it is a read. Recomputing it here would be a second implementation of
    /// a composite whose member floor, window and benchmark all live in C10.
    ///
    /// **`vix` is null and the panel says absent rather than blank** [D-80]. The bulk
    /// end-of-day feed carries equities and the index is not among them, so the column is
    /// written null explicitly and a blank cell would read as a rendering gap.
    /// </summary>
    public async Task<MarketPanel> MarketAsync(DateOnly date, string? sector, CancellationToken ct = default)
    {
        var rows = await _data.ReadAsync(
            "market_context_daily",
            $"""
             SELECT breadth, vix, regime_label,
                    sector_relative_strength ->> {Literal(sector ?? string.Empty)}
             FROM market_context_daily
             WHERE date = DATE '{Iso(date)}';
             """,
            ct).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return new MarketPanel(false, null, null, null, sector, null);
        }

        return new MarketPanel(
            true, Real(rows[0][0]), Real(rows[0][1]), rows[0][2] as string, sector,
            Composite(rows[0][3]));
    }

    /// <summary>
    /// One entry of the sector composite object.
    ///
    /// **Read with `->>` rather than `->`**, so Postgres returns text and the value
    /// arrives as a string whatever the driver would have mapped a `jsonb` scalar to.
    /// `->` would leave the CLR type to the provider, and a mapping change would turn
    /// every composite on the page into an absence without erroring.
    ///
    /// A sector the object does not carry yields SQL null and reads as absent, which is
    /// the state a sector below `market.sector_composite_min_members` produces: one name's
    /// noise is not a sector, so C10 writes no entry rather than a number [2.1].
    /// </summary>
    private static double? Composite(object? value)
        => value is string text
           && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : Real(value);

    /// <summary>
    /// What the compute layer read, rather than what it produced.
    ///
    /// **Every window here belongs to a component and none belongs to this page.**
    /// Sentiment takes `sentiment.lookback_days` resolved as of the viewed date, insider
    /// takes the ninety days `insider_net_90d_usd` carries in its own name, and filings
    /// take no window at all: everything readable on the date, which is what
    /// `filing_date_effective &lt;= date` means [INVARIANT 12]. The bar count is the one
    /// bound no component owns and it is `inspector.recent_bars`.
    /// </summary>
    public async Task<InputsPanel> InputsAsync(string ticker, DateOnly date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        var bars = (int) await LongAsync("inspector.recent_bars", date, 20, ct).ConfigureAwait(false);
        var sentimentDays = (int) await LongAsync("sentiment.lookback_days", date, 30, ct).ConfigureAwait(false);

        return new InputsPanel(
            bars,
            await BarsAsync(ticker, date, bars, ct).ConfigureAwait(false),
            await FilingsAsync(ticker, date, ct).ConfigureAwait(false),
            sentimentDays,
            await SentimentAsync(ticker, date, sentimentDays, ct).ConfigureAwait(false),
            InsiderWindowDays,
            await InsiderAsync(ticker, date, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The ninety-day insider window, as a constant and deliberately not a key.
    ///
    /// `FlowEngine.WindowDays` gives the reason and it holds identically here: the column
    /// is named <c>insider_net_90d_usd</c> in `ARCHITECTURE.html` §3 and §5 and in D-61,
    /// so a tunable ninety would be a second place for the number to live and the column
    /// name would be wrong the first time they disagreed. Copying the constant rather
    /// than the key is therefore copying the same decision, not weakening it.
    /// </summary>
    public const int InsiderWindowDays = 90;

    private async Task<IReadOnlyList<PriceBar>> BarsAsync(
        string ticker, DateOnly date, int bars, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "price_daily",
            $"""
             SELECT date, close, adj_close, volume
             FROM price_daily
             WHERE ticker = {Literal(ticker)} AND date <= DATE '{Iso(date)}'
             ORDER BY date DESC
             LIMIT {bars.ToString(CultureInfo.InvariantCulture)};
             """,
            ct).ConfigureAwait(false);

        return rows
            .Select(r => new PriceBar(Date(r[0])!.Value, r[1] as decimal?, r[2] as decimal?, r[3] as long?))
            .ToList();
    }

    /// <summary>
    /// Every filing readable on the date, newest effective date first.
    ///
    /// **Filtered and ordered on `filing_date_effective` and never on `period_end`**
    /// [INVARIANT 12, D-46, D-62]. Period end is the natural-looking key and hands a
    /// reader quarterly numbers weeks before they were public; a viewer ordering on it
    /// would teach that reading to everyone who opens the page.
    ///
    /// The newest row is marked, being the one a valuation input would have resolved
    /// from. **Which column came from which filing is not recorded**, so the panel shows
    /// the rows and says that rather than implying a provenance the store does not hold.
    /// </summary>
    private async Task<IReadOnlyList<Filing>> FilingsAsync(string ticker, DateOnly date, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "fundamental_snapshot",
            $"""
             SELECT period_end, filing_date, filing_date_effective, filing_date_unknown_reason, period_type
             FROM fundamental_snapshot
             WHERE ticker = {Literal(ticker)}
               AND filing_date_effective IS NOT NULL
               AND filing_date_effective <= DATE '{Iso(date)}'
             ORDER BY filing_date_effective DESC, period_end DESC;
             """,
            ct).ConfigureAwait(false);

        return rows
            .Select((r, i) => new Filing(
                Date(r[0])!.Value, Date(r[1]), Date(r[2]), r[3] as string, r[4] as string,
                MostRecentReadable: i == 0))
            .ToList();
    }

    /// <summary>
    /// The days inside the window that carry a row, and only those.
    ///
    /// **Absent days are not filled.** The series is sparse by construction, rows
    /// appearing only on days that carry news, and a zero here would say attention was
    /// measured at nothing rather than not measured [D-12, D-78].
    /// </summary>
    private async Task<IReadOnlyList<SentimentDay>> SentimentAsync(
        string ticker, DateOnly date, int windowDays, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "sentiment_daily",
            $"""
             SELECT date, article_count, sentiment_score
             FROM sentiment_daily
             WHERE ticker = {Literal(ticker)}
               AND date <= DATE '{Iso(date)}'
               AND date > DATE '{Iso(date.AddDays(-windowDays))}'
             ORDER BY date DESC;
             """,
            ct).ConfigureAwait(false);

        return rows
            .Select(r => new SentimentDay(Date(r[0])!.Value, r[1] as int?, Real(r[2])))
            .ToList();
    }

    /// <summary>
    /// Insider filings inside the ninety-day window, keyed on the date the filing became
    /// public rather than on the transaction date.
    ///
    /// `FlowEngine` filters `filed_at` for the same reason: a transaction is not evidence
    /// until it is filed, and reading on `transaction_date` alone is INVARIANT 12's
    /// mistake arriving through a table with a different shape. Both dates are shown so
    /// the lag is inspectable.
    /// </summary>
    private async Task<IReadOnlyList<InsiderFiling>> InsiderAsync(
        string ticker, DateOnly date, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "insider_transaction",
            $"""
             SELECT filed_at, transaction_date, reporting_owner_name, transaction_code,
                    shares_amount, price_per_share
             FROM insider_transaction
             WHERE ticker = {Literal(ticker)}
               AND filed_at <= DATE '{Iso(date)}'
               AND filed_at > DATE '{Iso(date.AddDays(-InsiderWindowDays))}'
             ORDER BY filed_at DESC, transaction_date DESC;
             """,
            ct).ConfigureAwait(false);

        return rows
            .Select(r => new InsiderFiling(
                Date(r[0]), Date(r[1]), r[2] as string, r[3] as string,
                r[4] as decimal?, r[5] as decimal?))
            .ToList();
    }

    /// <summary>
    /// A whole-number key as of the viewed date, falling back to the stated default where
    /// no version was in force yet.
    ///
    /// **A reader falls back where a stage fails.** A stage resolving nothing is a run
    /// that must not continue [D-72]; a viewer resolving nothing is looking at a date
    /// before the key existed, which is a fact about that date rather than a fault, and
    /// refusing to render the panel would hide the three other windows that do resolve.
    /// </summary>
    private async Task<long> LongAsync(string key, DateOnly date, long fallback, CancellationToken ct)
    {
        var row = await _config.ResolveAsync(key, date, ct).ConfigureAwait(false);

        return row is not null
               && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
    }

    /// <summary>
    /// Every ranked metric with its raw value, its percentile, and the size of the cell
    /// it was ranked in [D-107].
    ///
    /// **Four reads for the values, one per source, and one read for the cells.** The
    /// cell figures are joined on the name's own `(size_bucket, sector)` taken from the
    /// membership row already read, which is why this takes it rather than reading
    /// `security_daily` a second time.
    ///
    /// **A name with no membership row belongs to no cell**, so every metric reads as
    /// unranked rather than as a cell of zero. The panel says which of the two it is.
    /// </summary>
    public async Task<MetricsPanel> MetricsAsync(
        string ticker, DateOnly date, MembershipRow? inForce, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        var coverage = await CoverageAsync(date, ct).ConfigureAwait(false);
        var cells = await CellsAsync(date, inForce, ct).ConfigureAwait(false);
        var rows = new List<MetricRow>();

        foreach (var (table, metrics) in MetricSources)
        {
            var values = await ValuesAsync(table, metrics, ticker, date, ct).ConfigureAwait(false);

            foreach (var metric in metrics)
            {
                var cell = cells.GetValueOrDefault(metric);
                var v = values.GetValueOrDefault(metric);

                rows.Add(new MetricRow(
                    table, metric, v.Value, v.Percentile,
                    cell?.CellMembers, cell?.BucketMembers, cell?.MinMembers, cell?.RankedScope));
            }
        }

        var cellOf = inForce is null ? null : new MetricCell(inForce.SizeBucket, inForce.Sector);

        return new MetricsPanel(
            cellOf, coverage.Populated, coverage.From, coverage.To, rows);
    }

    /// <summary>
    /// One source table's raw values and percentiles for one name on one date.
    ///
    /// The column list is built from the declared metric names, so a metric added to
    /// <see cref="MetricSources"/> and absent from the table fails loudly at the
    /// statement rather than reading as null.
    /// </summary>
    private async Task<Dictionary<string, (decimal? Value, double? Percentile)>> ValuesAsync(
        string table, IReadOnlyList<string> metrics, string ticker, DateOnly date, CancellationToken ct)
    {
        var columns = string.Join(", ", metrics.SelectMany(m => new[] { m, m + "_pctile" }));

        var rows = await _data.ReadAsync(
            table,
            $"SELECT {columns} FROM {table} WHERE ticker = {Literal(ticker)} AND date = DATE '{Iso(date)}';",
            ct).ConfigureAwait(false);

        var map = new Dictionary<string, (decimal?, double?)>(StringComparer.Ordinal);

        if (rows.Count == 0)
        {
            return map;
        }

        for (var i = 0; i < metrics.Count; i++)
        {
            map[metrics[i]] = (Number(rows[0][i * 2]), Real(rows[0][(i * 2) + 1]));
        }

        return map;
    }

    /// <summary>
    /// The cell rows for this name's own cell, one per metric.
    ///
    /// **`IS NOT DISTINCT FROM` rather than an equality**, because `NULL = NULL` matches
    /// nothing and the row that carries no sector is the one a name with no sector needs.
    /// That is the same comparison the unique index makes under `NULLS NOT DISTINCT`, so
    /// the read and the key agree [0016]. An equality with `coalesce` would agree with
    /// neither: it merges the null cell with the empty-string one, which C11 ranks
    /// separately.
    /// </summary>
    private async Task<Dictionary<string, CellFigures>> CellsAsync(
        DateOnly date, MembershipRow? inForce, CancellationToken ct)
    {
        var map = new Dictionary<string, CellFigures>(StringComparer.Ordinal);

        if (inForce?.SizeBucket is not { } bucket)
        {
            return map;
        }

        var rows = await _data.ReadAsync(
            "percentile_cell_daily",
            $"""
             SELECT metric, cell_members, bucket_members, min_members, ranked_scope
             FROM percentile_cell_daily
             WHERE date = DATE '{Iso(date)}'
               AND size_bucket = {Literal(bucket)}
               AND sector IS NOT DISTINCT FROM {SectorLiteral(inForce.Sector)};
             """,
            ct).ConfigureAwait(false);

        foreach (var r in rows)
        {
            map[(string) r[0]!] = new CellFigures(
                r[1] as int?, (int) r[2]!, (int) r[3]!, (string) r[4]!);
        }

        return map;
    }

    /// <summary>
    /// Whether the populating pass has reached this date, read as coverage and never as
    /// equality [D-106].
    ///
    /// **A date inside the covered range of every source is populated.** Taking the
    /// narrowest of the four is what makes a halt between the four statements of one date
    /// read as not populated rather than as populated with a source missing.
    ///
    /// **The span is checked against the date's own rows rather than trusted on its
    /// own**, because a span only means what it looks like while the covered set is
    /// contiguous, and the nightly path can break that. C11 writes cells for the date it
    /// runs on, so a night that runs after a gap widens `covered_to` past dates nothing
    /// ever wrote, and every one of them would then read as populated with its cells
    /// absent. That is precisely the confusion this marker exists to prevent, arriving
    /// through the writer rather than through the reader. Two reads, and the second is
    /// one indexed existence check.
    /// </summary>
    private async Task<(bool Populated, DateOnly? From, DateOnly? To)> CoverageAsync(
        DateOnly date, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "percentile_cell_coverage",
            "SELECT max(covered_from), min(covered_to), count(*) FROM percentile_cell_coverage;",
            ct).ConfigureAwait(false);

        var from = rows.Count == 0 ? null : Date(rows[0][0]);
        var to = rows.Count == 0 ? null : Date(rows[0][1]);

        if (rows.Count == 0 || rows[0][2] is not long sources || sources < MetricSources.Length
            || from is null || to is null || date < from || date > to)
        {
            return (false, from, to);
        }

        var present = await _data.ReadAsync(
            "percentile_cell_daily",
            $"SELECT 1 FROM percentile_cell_daily WHERE date = DATE '{Iso(date)}' LIMIT 1;",
            ct).ConfigureAwait(false);

        return (present.Count > 0, from, to);
    }

    private sealed record CellFigures(int? CellMembers, int BucketMembers, int MinMembers, string RankedScope);

    private static decimal? Number(object? value) => value switch
    {
        null => null,
        decimal d => d,
        float f => (decimal) f,
        double d => (decimal) d,
        int i => i,
        long l => l,
        _ => null,
    };

    private static double? Real(object? value) => value switch
    {
        null => null,
        float f => f,
        double d => d,
        decimal m => (double) m,
        _ => null,
    };

    /// <summary>
    /// Membership, and for a name that is not a member the criterion C01 stopped on.
    ///
    /// **Four reads and no arithmetic.** Identity, the `security_daily` row in force,
    /// the `universe_rejection` row in force, and the criteria as they stood. The panel
    /// does not decide whether the name is a member: presence of an active row says
    /// admitted, presence of a rejection says rejected, and absence of both says the
    /// name was not evaluated on that date, which is a third state and not a blank
    /// [`SCHEMA.md`, `universe_rejection`].
    /// </summary>
    public async Task<MembershipPanel> MembershipAsync(
        string ticker, DateOnly date, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        var identity = await IdentityAsync(ticker, ct).ConfigureAwait(false);
        var inForce = await InForceAsync(ticker, date, ct).ConfigureAwait(false);
        var rejection = await RejectionAsync(ticker, date, inForce?.EvaluationDate, ct).ConfigureAwait(false);
        var thresholds = await ThresholdsAsync(date, ct).ConfigureAwait(false);

        return new MembershipPanel(identity, inForce, rejection, thresholds);
    }

    private async Task<SecurityIdentity?> IdentityAsync(string ticker, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "security",
            $"""
             SELECT name, first_seen, last_seen, delisted_date
             FROM security
             WHERE ticker = {Literal(ticker)};
             """,
            ct).ConfigureAwait(false);

        return rows.Count == 0
            ? null
            : new SecurityIdentity(
                rows[0][0] as string, Date(rows[0][1]), Date(rows[0][2]), Date(rows[0][3]));
    }

    /// <summary>
    /// The row in force, and the evaluation date it was written on.
    ///
    /// **The date the row was written on is carried rather than dropped**, because C01
    /// evaluates weekly and a Wednesday reads the Sunday before it. A panel showing the
    /// bucket without the date it was decided on invites a reader to take it for a daily
    /// fact, which is the reading `security_daily` exists to make impossible.
    ///
    /// `is_active` is not filtered, for the reason <see cref="Universe.AsOf"/> gives:
    /// filtering inside the pick resurrects a name whose latest row is the departure
    /// that ended its membership.
    /// </summary>
    private async Task<MembershipRow?> InForceAsync(string ticker, DateOnly date, CancellationToken ct)
    {
        var rows = await _data.ReadAsync(
            "security_daily",
            $"""
             SELECT s.date, s.sector, s.size_bucket, s.market_cap, s.is_active
             FROM security_daily s
             WHERE s.ticker = {Literal(ticker)} AND s.date <= DATE '{Iso(date)}'
             ORDER BY s.date DESC
             LIMIT 1;
             """,
            ct).ConfigureAwait(false);

        return rows.Count == 0
            ? null
            : new MembershipRow(
                Date(rows[0][0])!.Value,
                rows[0][1] as string,
                rows[0][2] as string,
                rows[0][3] as decimal?,
                (bool) rows[0][4]!);
    }

    /// <summary>
    /// The rejection in force, read the same as-of way as membership and for the same
    /// reason: C01 writes both on its weekly cadence, so a date between two evaluations
    /// reads the one before it.
    /// </summary>
    /// <param name="inForceOn">
    /// The evaluation date of the `security_daily` row in force, where there is one.
    ///
    /// **The as-of pick spans both tables, because one evaluation writes to one of them
    /// and the answer is whichever C01 reached last** [`SCHEMA.md`, `universe_rejection`].
    /// Read without this bound the two picks are independent, and a name rejected at one
    /// evaluation and admitted at a later one carries both: the panel then says member
    /// and rejected at once, which is the state the two tables partition the population
    /// precisely to prevent. Measured at the 3.5 sign-off rather than reasoned about:
    /// 205,940 active rows over 2,228 tickers carry a rejection at an earlier date, so it
    /// is the ordinary case and not an edge.
    ///
    /// **The bound is inclusive and that is the half a `&gt;` would get wrong.** A name
    /// leaving the universe is written both rows on the one evaluation date, the
    /// departure into `security_daily` and the criterion here, and the criterion is
    /// exactly the answer for it. Sharing a date is that case; an earlier date is the
    /// superseded one.
    ///
    /// Null for a name with no membership row at all, which leaves the rejection
    /// unbounded, that being a name C01 has never admitted.
    /// </param>
    private async Task<RejectionRow?> RejectionAsync(
        string ticker, DateOnly date, DateOnly? inForceOn, CancellationToken ct)
    {
        var superseded = inForceOn is { } on ? $"\n               AND r.date >= DATE '{Iso(on)}'" : string.Empty;

        var rows = await _data.ReadAsync(
            "universe_rejection",
            $"""
             SELECT r.date, r.criterion
             FROM universe_rejection r
             WHERE r.ticker = {Literal(ticker)} AND r.date <= DATE '{Iso(date)}'{superseded}
             ORDER BY r.date DESC
             LIMIT 1;
             """,
            ct).ConfigureAwait(false);

        return rows.Count == 0
            ? null
            : new RejectionRow(Date(rows[0][0])!.Value, (string) rows[0][1]!);
    }

    /// <summary>
    /// Every criterion's value as it stood on the viewed date.
    ///
    /// `ResolveAsync` rather than `RequireAsync`: a date before a key came into force
    /// has no value, and that is a fact to show rather than a run to fail. A viewer is
    /// the one reader for which an absent config row is information [D-72].
    /// </summary>
    private async Task<IReadOnlyList<ThresholdInForce>> ThresholdsAsync(DateOnly date, CancellationToken ct)
    {
        var resolved = new List<ThresholdInForce>(CriterionKeys.Length);

        foreach (var key in CriterionKeys)
        {
            var row = await _config.ResolveAsync(key, date, ct).ConfigureAwait(false);
            resolved.Add(new ThresholdInForce(key, row?.Value, row?.Version, row?.SetOn));
        }

        return resolved;
    }

    private static DateOnly? Date(object? value)
        => value is DateTime d ? DateOnly.FromDateTime(d) : null;

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// A ticker as a SQL literal, with quotes doubled.
    ///
    /// The statement is interpolated because every statement in this repository is, and
    /// the one value that arrives from outside is escaped here rather than trusted. A
    /// ticker reaches this class from a route parameter, which is the only place in the
    /// system where a string a user typed reaches SQL at all.
    /// </summary>
    private static string Literal(string ticker)
        => "'" + ticker.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// A sector as a SQL literal, or the keyword NULL.
    ///
    /// Separate from <see cref="Literal(string)"/> because a null sector is a value the
    /// key holds rather than an absent argument, and rendering it as <c>''</c> would ask
    /// for the empty-string cell instead [0016].
    /// </summary>
    private static string SectorLiteral(string? sector)
        => sector is null ? "NULL" : Literal(sector);
}
