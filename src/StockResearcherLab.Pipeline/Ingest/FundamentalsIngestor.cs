using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// C03. Fundamentals keyed on <c>filing_date_effective</c>, never period end and
/// never the raw filing date [D-46, D-62, INVARIANT 12].
///
/// **The candidate list bootstraps from `price_daily`, and that is a deviation from
/// `ARCHITECTURE.html` section 3**, which gives this component's reads as the
/// fundamentals endpoint and `events`. It cannot be only those: C01 UniverseBuilder
/// needs shares outstanding and clean gaps to build `security`, and both come from
/// here, so a component reading only `security` could never run first. The bootstrap
/// is the criteria that need no fundamentals, being price and liquidity from
/// `price_daily`, and `security` is preferred the moment it is populated. Reported
/// rather than closed.
///
/// **Earnings do not jump the queue yet.** The catalogue says they should, and
/// `events` is built at 1.8. Until then the rotation is staleness-ordered only, and
/// that is a gap rather than a decision.
/// </summary>
public sealed class FundamentalsIngestor : IStage
{
    public static readonly string[] Columns =
    [
        "ticker", "period_end", "period_type", "filing_date", "filing_date_effective",
        "filing_date_unknown_reason", "filing_date_source",
        "total_assets", "total_liab", "total_stockholder_equity", "cash",
        "cash_and_equivalents", "short_term_investments", "net_debt",
        "short_long_term_debt_total", "long_term_debt", "inventory", "net_receivables",
        "accounts_payable", "total_current_assets", "total_current_liabilities",
        "property_plant_equipment_net", "goodwill", "intangible_assets",
        "total_revenue", "cost_of_revenue", "gross_profit", "operating_income",
        "ebit", "ebitda", "net_income", "income_before_tax", "income_tax_expense",
        "interest_expense", "research_development",
        "cash_from_operating", "cash_from_investing", "cash_from_financing",
        "depreciation", "dividends_paid", "sale_purchase_of_stock",
        "shares_outstanding",
    ];

    private static readonly string[] ConflictTarget = ["ticker", "period_end", "period_type"];

    private readonly EodhdClient _client;

    public FundamentalsIngestor(EodhdClient client) => _client = client;

    public string Name => "FundamentalsIngestor";

    public IReadOnlyList<string> ReadSet { get; } = ["price_daily", "security"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
        [new TableWrite("fundamental_snapshot", WriteOperation.Insert, Columns)];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var maxPerRun = (int) await LongAsync(context, "fundamentals.max_tickers_per_run", ct).ConfigureAwait(false);
        var substitutionAlert = await DecimalAsync(context, "fundamentals.substitution_rate_alert", ct).ConfigureAwait(false);
        var widestAlertDays = await LongAsync(context, "fundamentals.widest_gap_alert_days", ct).ConfigureAwait(false);
        var minPrice = await DecimalAsync(context, "universe.min_price", ct).ConfigureAwait(false);
        var minAdv = await DecimalAsync(context, "universe.min_adv_20d", ct).ConfigureAwait(false);
        var minHistory = (int) await LongAsync(context, "universe.min_history_days", ct).ConfigureAwait(false);

        var selection = await CandidatesAsync(
            context, minPrice, minAdv, minHistory, maxPerRun, ct).ConfigureAwait(false);
        var tickers = selection.Tickers;

        long rows = 0;
        long periods = 0;
        long substituted = 0;
        var wideFilers = new List<string>();

        foreach (var ticker in tickers)
        {
            var resolved = await LoadAsync(context, ticker, ct).ConfigureAwait(false);
            if (resolved is null)
            {
                continue;
            }

            periods += resolved.Value.Periods.Count;
            substituted += resolved.Value.SubstitutedCount;
            rows += resolved.Value.Written;

            if (resolved.Value.WidestCleanGapDays is int w && w > widestAlertDays)
            {
                wideFilers.Add($"{ticker} {w}d");
            }
        }

        // Both alerts emit through the run log. Neither may write `alert`, which has
        // ConcentrationMonitor as its only writer [A3, INVARIANT 10].
        var notes = new List<string>();

        var rate = periods == 0 ? 0m : (decimal) substituted / periods;
        if (rate > substitutionAlert)
        {
            notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "substitution rate {0:P1} over {1:N0} periods, above {2:P0}. The provider's date " +
                "handling may have changed, and every substituted row reads late by that ticker's " +
                "own widest gap [D-62, RUNBOOK]",
                rate, periods, substitutionAlert));
        }

        if (wideFilers.Count > 0)
        {
            // Sorted, so two runs over the same data produce the same line.
            wideFilers.Sort(StringComparer.Ordinal);

            notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0:N0} ticker(s) with a widest clean gap above {1} days: {2}{3}. Their fundamentals " +
                "reach a screen too late to be worth much [D-62]",
                wideFilers.Count,
                widestAlertDays,
                string.Join(", ", wideFilers.Take(20)),
                wideFilers.Count > 20 ? ", ..." : ""));
        }

        // Coverage before anything else, because the rotation not advancing is
        // invisible from a row count: three runs each wrote 46,376 rows and covered
        // the same 500 tickers, and nothing in the log said so [1.8].
        var coverage = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} row(s) over {1:N0} ticker(s). Candidate pool {2:N0}, of which {3:N0} have never " +
            "been fetched; {4:N0} of this run's selection were new",
            rows, tickers.Count, selection.PoolSize, selection.NeverFetched, selection.NewInSelection);

        notes.Insert(0, coverage);

        return notes.Count > 1
            ? StageResult.Alert(rows, string.Join("; ", notes))
            : new StageResult(rows, "ok", coverage);
    }

    private readonly record struct Selection(
        IReadOnlyList<string> Tickers, int PoolSize, int NeverFetched, int NewInSelection);

    private readonly record struct Loaded(
        IReadOnlyList<ResolvedPeriod> Periods, int? WidestCleanGapDays, int SubstitutedCount, long Written);

    private async Task<Loaded?> LoadAsync(StageContext context, string ticker, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await _client.GetAsync(
                "fundamentals/" + ticker,
                [("filter", "Financials")],
                ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // A ticker the fundamentals endpoint does not carry is the ordinary
            // case for an index or a fund, and the universe excludes those anyway.
            // It is not a reason to fail the night.
            return null;
        }

        using (doc)
        {
            var statements = Statements.Parse(doc.RootElement);
            if (statements.Count == 0)
            {
                return null;
            }

            var resolved = FilingDateRule.Resolve(
                statements.Select(s => new RawPeriod(s.PeriodEnd, s.FilingDate)).ToList());

            var byPeriod = statements.ToDictionary(s => s.PeriodEnd);

            var written = await context.Data.BulkUpsertAsync(
                "fundamental_snapshot", Columns, ConflictTarget,
                async (w, c) =>
                {
                    foreach (var p in resolved.Periods)
                    {
                        var s = byPeriod[p.PeriodEnd];
                        await WriteRowAsync(w, ticker, p, s, c).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);

            return new Loaded(
                resolved.Periods, resolved.WidestCleanGapDays, resolved.SubstitutedCount, written);
        }
    }

    private static async Task WriteRowAsync(
        IBulkWriter w, string ticker, ResolvedPeriod p, Statement s, CancellationToken ct)
    {
        await w.StartRowAsync(ct).ConfigureAwait(false);
        await w.WriteAsync(ticker, ct).ConfigureAwait(false);
        await w.WriteAsync(p.PeriodEnd, ct).ConfigureAwait(false);
        await w.WriteAsync("quarterly", ct).ConfigureAwait(false);
        await w.WriteAsync(p.FilingDate, ct).ConfigureAwait(false);
        await w.WriteAsync(p.Effective, ct).ConfigureAwait(false);
        await w.WriteAsync(p.Reason, ct).ConfigureAwait(false);
        await w.WriteAsync(s.FilingDateSource, ct).ConfigureAwait(false);

        foreach (var name in Columns.Skip(7))
        {
            await w.WriteAsync(s.Value(name), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tickers to fetch this run, ordered so the same run over the same data picks
    /// the same set.
    ///
    /// `security` when it is populated, and `price_daily` before that. The
    /// bootstrap applies only the criteria that need no fundamentals, since the
    /// ones that do are what this stage exists to supply.
    /// </summary>
    private async Task<Selection> CandidatesAsync(
        StageContext context, decimal minPrice, decimal minAdv, int minHistory,
        int maxPerRun, CancellationToken ct)
    {
        // Two reads rather than one query joining both tables. IStageData checks the
        // table a caller names and cannot see what the SQL actually touches, so a
        // join here would be leaning on that gap rather than being checked by it.
        var already = await context.Data.ReadAsync(
            "fundamental_snapshot",
            "SELECT DISTINCT ticker FROM fundamental_snapshot;",
            ct).ConfigureAwait(false);

        var fetched = already.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);

        var fromSecurity = await context.Data.ReadAsync(
            "security", "SELECT ticker FROM security WHERE is_active ORDER BY ticker;", ct)
            .ConfigureAwait(false);

        var inUniverse = fromSecurity.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);

        // **The pool is the candidate set, never the universe** [1.8, measured].
        // Drawing it from `security` once `security` was populated closed the
        // universe permanently rather than merely ordering it differently: a name
        // needs fundamentals to be admitted, so a name outside `security` could
        // never be fetched and could never be admitted. Three consecutive runs
        // wrote an identical 46,376 rows and the distinct ticker count did not
        // move, and the universe stood at 679 against a design estimate of about
        // 2,000 with 2,479 candidates rejected for clean gaps most of which had
        // never been fetched at all.
        //
        // `security` still decides order and no longer decides membership.
        var pool = await BootstrapPoolAsync(context, minPrice, minAdv, minHistory, ct).ConfigureAwait(false);

        // Never fetched first, then universe members, then everything else, each
        // group ordinal. That is what makes this a rotation: ordering by ticker
        // alone re-selects the same head every run and coverage never advances past
        // the first page. Coverage before freshness while coverage is incomplete,
        // because a name absent from the store cannot be screened at all, where a
        // name whose figures are a few days old still can.
        var selected = pool
            .OrderBy(t => fetched.Contains(t) ? (inUniverse.Contains(t) ? 1 : 2) : 0)
            .ThenBy(t => t, StringComparer.Ordinal)
            .Take(maxPerRun)
            .ToList();

        return new Selection(
            selected,
            pool.Count,
            pool.Count(t => !fetched.Contains(t)),
            selected.Count(t => !fetched.Contains(t)));
    }

    /// <summary>
    /// The bootstrap pool: everything priced above the floor on the date being
    /// built, excluding the index tickers the bulk feed carries, which begin with a
    /// caret and are not common stock [D-2].
    ///
    /// Only the criteria that need no fundamentals, because the ones that do are
    /// what this stage exists to supply. C01 applies the rest.
    /// </summary>
    private async Task<List<string>> BootstrapPoolAsync(
        StageContext context, decimal minPrice, decimal minAdv, int minHistory, CancellationToken ct)
    {
        var asOf = context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // All three of D-4's price-side criteria, not just the price floor [1.8].
        // The price floor alone left 8,423 names in the pool where 4,808 clear
        // liquidity and history, so about 3,600 of them could never be admitted
        // whatever their filings say, and each one costs 10 units to find that out.
        //
        // The same argument the type filter below already makes: this is D-4's own
        // criteria applied sooner rather than a second filter, and INVARIANT 1
        // still has absolute filters living only in the universe definition. C01
        // applies every one of these again and remains the only component that
        // decides membership [D-5, INVARIANT 1].
        var sql = $"""
            WITH bars AS (
                SELECT ticker, date, close, volume,
                       row_number() OVER (PARTITION BY ticker ORDER BY date DESC) AS rn
                FROM price_daily
                WHERE date <= DATE '{asOf}'
            ),
            latest AS (SELECT ticker, close FROM bars WHERE rn = 1),
            mdv AS (
                SELECT ticker,
                       percentile_cont(0.5) WITHIN GROUP (ORDER BY close * volume) AS median_dollar_volume
                FROM bars WHERE rn <= 20 AND close IS NOT NULL AND volume IS NOT NULL
                GROUP BY ticker
            ),
            span AS (
                SELECT ticker, count(*) AS days
                FROM price_daily WHERE date <= DATE '{asOf}' GROUP BY ticker
            )
            SELECT l.ticker
            FROM latest l
            JOIN mdv m ON m.ticker = l.ticker
            JOIN span s ON s.ticker = l.ticker
            WHERE l.close >= {minPrice.ToString(CultureInfo.InvariantCulture)}
              AND m.median_dollar_volume >= {minAdv.ToString(CultureInfo.InvariantCulture)}
              AND s.days >= {minHistory.ToString(CultureInfo.InvariantCulture)}
              AND l.ticker NOT LIKE '^%'
            ORDER BY l.ticker;
            """;

        var rows = await context.Data.ReadAsync("price_daily", sql, ct).ConfigureAwait(false);

        // Restricted to the instruments D-4 admits before a single per-ticker call
        // is spent. Without it the pool is 39,711 names at the price floor, almost
        // all of them funds and OTC listings the universe rejects anyway, and four
        // runs of 500 landed financials for 133 tickers. This is the same universe
        // criterion applied sooner, not a second one [D-5, INVARIANT 1].
        var admitted = await SymbolList.AdmittedAsync(_client, ct).ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).Where(admitted.ContainsKey).ToList();
    }

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
