using System.Globalization;
using System.Net;
using System.Text.Json;
using StockResearcherLab.Core.Config;
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
public sealed class FundamentalsIngestor : IBackfillStage
{
    public static readonly string[] Columns =
    [
        "ticker", "period_end", "period_type", "filing_date", "filing_date_effective",
        "filing_date_unknown_reason", "filing_date_source", "sector",
        "total_assets", "total_liab", "total_stockholder_equity", "cash",
        "cash_and_equivalents", "short_term_investments", "net_debt",
        "short_long_term_debt_total", "long_term_debt", "inventory", "net_receivables",
        "accounts_payable", "total_current_assets", "total_current_liabilities",
        "property_plant_equipment_net", "goodwill", "intangible_assets",
        "total_revenue", "cost_of_revenue", "gross_profit", "operating_income",
        "ebit", "ebitda", "net_income", "income_before_tax", "income_tax_expense",
        "interest_expense", "research_development",
        "cash_from_operating", "cash_from_investing", "cash_from_financing",
        "capital_expenditures", "depreciation", "dividends_paid",
        "sale_purchase_of_stock",
        "shares_outstanding",
    ];

    private static readonly string[] ConflictTarget = ["ticker", "period_end", "period_type"];

    /// <summary>
    /// The attempt record [0006]. Written for every ticker the run selected, whether
    /// or not the fetch yielded rows, which is the distinction the old ordering could
    /// not make.
    /// </summary>
    public static readonly string[] AttemptColumns =
        ["ticker", "last_attempted_date", "last_yield_date", "rows_last_attempt"];

    // ------------------------------------------------------- range mode [3.7] ---

    /// <summary>
    /// One full pool sweep, the rotation cap lifted [D-93].
    ///
    /// **The cap is a rate limit and not a filter** [INVARIANT 1], so lifting it for a
    /// sweep is the cap doing what it is for rather than an exception to it: every name
    /// passes through it eventually and this is the run where they all do at once.
    ///
    /// **The pool is the live candidate pool plus every delisted common stock with a
    /// bar inside the window**, which is the amendment before this checkpoint. C03's
    /// pool derives from current price, liquidity and history, so it holds no name that
    /// has since delisted; a name liquid in 2021 and delisted in 2023 would carry prices
    /// from 3.6 and no fundamental rows, compute zero clean gaps, fail D-62's floor, and
    /// be absent from `security_daily` on every historical date. The reconstruction
    /// would be survivorship-clean on price and not on membership.
    ///
    /// 3.6 is what makes the second half computable: the symbol list carries no
    /// delisting date, so the last bar is the only date there is.
    ///
    /// Config resolves as of the range end for the same reason C02's does: this sweep is
    /// ticker-partitioned, there is no date being computed, and the keys it reads are
    /// operational.
    /// </summary>
    public async Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = await context.ForDateAsync(context.To, ct).ConfigureAwait(false);

        var weight = ConfigValue.Long(await settings.Config
            .RequireAsync("backfill.weight_fundamentals", settings.Date, ct).ConfigureAwait(false));
        var reserve = ConfigValue.Long(await settings.Config
            .RequireAsync("backfill.unit_reserve", settings.Date, ct).ConfigureAwait(false));
        var allowance = ConfigValue.Long(await settings.Config
            .RequireAsync("backfill.daily_unit_allowance", settings.Date, ct).ConfigureAwait(false));
        var windowStart = ConfigValue.Date(await settings.Config
            .RequireAsync("backfill.window_start", settings.Date, ct).ConfigureAwait(false));

        var minPrice = await DecimalAsync(settings, "universe.min_price", ct).ConfigureAwait(false);
        var minAdv = await DecimalAsync(settings, "universe.min_adv_20d", ct).ConfigureAwait(false);
        var minHistory = (int) await LongAsync(settings, "universe.min_history_days", ct).ConfigureAwait(false);

        var pool = await RangePoolAsync(settings, minPrice, minAdv, minHistory, windowStart, ct)
            .ConfigureAwait(false);

        var remaining = context.ResumeFrom is string from
            ? pool.Where(t => string.CompareOrdinal(t, from) >= 0).ToList()
            : pool;

        long rows = 0;
        var attempts = new List<Attempt>();
        var collisions = 0;
        string? haltedAt = null;
        string? haltDetail = null;

        // One at a time. Each call is 10 units against C02's 1, so the gate is asked per
        // ticker here rather than per chunk: the overshoot a chunk would allow is eighty
        // units rather than eight, and this sweep is the one that meets the wall on any
        // day it shares with another [phase 3 plan, 3.7].
        foreach (var ticker in remaining)
        {
            var decision = await context.NextUnitAsync(weight, reserve, allowance, ct).ConfigureAwait(false);

            if (!decision.Fits)
            {
                haltedAt = ticker;
                haltDetail = decision.Detail;
                break;
            }

            var resolved = await LoadAsync(settings, ticker, ct).ConfigureAwait(false);
            var written = resolved?.Written ?? 0;

            rows += written;
            collisions += resolved?.EarningsCollisions ?? 0;

            // Written for every ticker the sweep reached, yield or not, exactly as the
            // nightly path does: a name that returns nothing still has to move down the
            // rotation [0006].
            attempts.Add(new Attempt(ticker, settings.Date, written > 0 ? settings.Date : null, written));
        }

        await RecordAttemptsAsync(settings, attempts, ct).ConfigureAwait(false);

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} row(s) over {1:N0} of {2:N0} pool member(s), the rotation cap lifted. {3:N0} " +
            "earnings entr(ies) dropped as duplicates [D-96]. {4}",
            rows, attempts.Count, pool.Count, collisions,
            context.ResumeFrom is null ? "Full pool." : "Resumed from " + context.ResumeFrom + ".");

        return haltedAt is null
            ? BackfillResult.Completed(rows, context.To, detail)
            : BackfillResult.Halted(rows, context.To, haltedAt, detail + " " + haltDetail);
    }

    /// <summary>
    /// The sweep's pool: the live candidate pool, plus every delisted common stock
    /// carrying a `price_daily` bar at or after the window start.
    ///
    /// The second half is what stops the reconstructed universe being survivorship
    /// filtered. Names that stopped trading before the window are excluded and cost
    /// nothing.
    /// </summary>
    public async Task<IReadOnlyList<string>> RangePoolAsync(
        StageContext context, decimal minPrice, decimal minAdv, int minHistory,
        DateOnly windowStart, CancellationToken ct = default)
    {
        var live = await BootstrapPoolAsync(context, minPrice, minAdv, minHistory, ct).ConfigureAwait(false);

        var delisted = await SymbolList.AdmittedDelistedAsync(_client, ct).ConfigureAwait(false);

        var from = windowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var traded = await context.Data.ReadAsync(
            "price_daily",
            $"""
             SELECT DISTINCT ticker FROM price_daily
             WHERE date >= DATE '{from}'
             ORDER BY ticker;
             """,
            ct).ConfigureAwait(false);

        var inWindow = traded.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);

        var pool = new SortedSet<string>(live, StringComparer.Ordinal);
        pool.UnionWith(delisted.Keys.Where(inWindow.Contains));

        return pool.ToList();
    }

    /// <summary>
    /// Tickers that have reported earnings inside the backward window, which jump the
    /// rotation queue [D-74, the `1 → 3` carried obligation].
    ///
    /// **This closes the one deviation `ReadDeclarationConformanceTests` records.**
    /// §3 has given C03 `events` in its Reads cell since the catalogue was written and
    /// the code did not read it, so earnings never jumped the queue. `events` has been
    /// built since 1.8 and the deviation was carried, failing the moment it stopped
    /// being the only one.
    ///
    /// The window is `events.earnings_backward_days`, which is the same key C06 fills
    /// the table with, so the rotation cannot prefer a name whose event the ingest did
    /// not load.
    /// </summary>
    private static async Task<IReadOnlySet<string>> JustReportedAsync(
        StageContext context, CancellationToken ct)
    {
        var backward = await LongAsync(context, "events.earnings_backward_days", ct).ConfigureAwait(false);

        var from = context.Date.AddDays(-(int) backward).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var asOf = context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var rows = await context.Data.ReadAsync(
            "events",
            $"""
             SELECT DISTINCT ticker FROM events
             WHERE event_type = 'earnings'
               AND event_date >= DATE '{from}' AND event_date <= DATE '{asOf}'
             ORDER BY ticker;
             """,
            ct).ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// `General::Sector` off the unfiltered payload [D-97].
    ///
    /// Null where the block is absent rather than a guess. A ticker with no sector
    /// resolves to the percentile engine's existing bucket-only fallback, which is not
    /// a new case.
    /// </summary>
    private static string? Sector(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("General", out var general)
           && general.ValueKind == JsonValueKind.Object
           && general.TryGetProperty("Sector", out var sector)
           && sector.ValueKind == JsonValueKind.String
            ? sector.GetString()
            : null;

    /// <summary>The earnings history captured on the sweep that pays for it [D-96].</summary>
    public static readonly string[] EarningsColumns =
    [
        "ticker", "period_end", "report_date", "before_after_market",
        "eps_actual", "eps_estimate", "surprise_fraction",
    ];

    private static readonly string[] EarningsConflictTarget = ["ticker", "period_end"];

    private static readonly string[] AttemptConflictTarget = ["ticker"];

    private readonly EodhdClient _client;

    public FundamentalsIngestor(EodhdClient client) => _client = client;

    public string Name => "FundamentalsIngestor";

    // `fundamental_fetch_attempt` is not here and belongs in neither list twice. A
    // stage may read what it writes, which is what `DeclaredAccess.CanRead` says and
    // what `fundamental_snapshot` has always relied on: the rotation reads both back
    // to decide what to fetch next.
    public IReadOnlyList<string> ReadSet { get; } = ["price_daily", "security", "events"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new TableWrite("fundamental_snapshot", WriteOperation.Insert, Columns),
        new TableWrite("fundamental_fetch_attempt", WriteOperation.Insert, AttemptColumns),
        new TableWrite("earnings_history", WriteOperation.Insert, EarningsColumns),
    ];

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

        // Counted across the run rather than per ticker, so a condition currently
        // assumed rare is measured [D-96].
        var earningsCollisions = 0;

        // One entry per selected ticker, written below whether or not the fetch
        // yielded anything. A ticker that returns nothing still has to move down the
        // rotation, or it sits at the head of it for ever [0006].
        var attempts = new List<Attempt>(tickers.Count);

        foreach (var ticker in tickers)
        {
            var resolved = await LoadAsync(context, ticker, ct).ConfigureAwait(false);

            var written = resolved?.Written ?? 0;

            // A run that yields nothing must not erase the date a previous one did,
            // or the two absences collapse back into each other.
            DateOnly? lastYield = written > 0
                ? context.Date
                : selection.PriorYield.TryGetValue(ticker, out var prior) ? prior : null;

            attempts.Add(new Attempt(ticker, context.Date, lastYield, written));

            if (resolved is null)
            {
                continue;
            }

            periods += resolved.Value.Periods.Count;
            substituted += resolved.Value.SubstitutedCount;
            rows += resolved.Value.Written;
            earningsCollisions += resolved.Value.EarningsCollisions;

            if (resolved.Value.WidestCleanGapDays is int w && w > widestAlertDays)
            {
                wideFilers.Add($"{ticker} {w}d");
            }
        }

        await RecordAttemptsAsync(context, attempts, ct).ConfigureAwait(false);

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
        //
        // New against refreshed is what makes a frozen rotation visible in the log
        // rather than in a query someone thought to write [0006]. A run whose
        // selection is entirely refreshed, on a pool with names never attempted left
        // in it, is the defect; a run that is all refreshed on a fully attempted pool
        // is the rotation cycling as it should.
        var coverage = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} row(s) over {1:N0} ticker(s). Candidate pool {2:N0}, of which {3:N0} have never " +
            "been attempted; this run's selection was {4:N0} new and {5:N0} refreshed, the oldest " +
            "attempt in it dated {6}. {7:N0} earnings entr(ies) were dropped as duplicates of a " +
            "period end already seen [D-96]",
            rows, tickers.Count, selection.PoolSize, selection.NeverAttempted,
            selection.NewInSelection, selection.RefreshedInSelection,
            selection.OldestAttemptInSelection is DateOnly d
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "none",
            earningsCollisions);

        notes.Insert(0, coverage);

        return notes.Count > 1
            ? StageResult.Alert(rows, string.Join("; ", notes))
            : new StageResult(rows, "ok", coverage);
    }

    private readonly record struct Selection(
        IReadOnlyList<string> Tickers,
        int PoolSize,
        int NeverAttempted,
        int NewInSelection,
        int RefreshedInSelection,
        DateOnly? OldestAttemptInSelection,
        IReadOnlyDictionary<string, DateOnly> PriorYield);

    /// <summary>One attempt, written whether or not it yielded rows [0006].</summary>
    private readonly record struct Attempt(
        string Ticker, DateOnly AttemptedOn, DateOnly? LastYield, long Rows);

    /// <summary>
    /// The attempt record for every ticker this run selected.
    ///
    /// One upsert on `ticker`, so a second run over the same date writes what the
    /// first wrote [D-68]. Sorted before the copy, because COPY order reaches the
    /// table and an unsorted enumeration is not a deterministic output
    /// [`CLAUDE.md` §6].
    /// </summary>
    private static async Task RecordAttemptsAsync(
        StageContext context, List<Attempt> attempts, CancellationToken ct)
    {
        if (attempts.Count == 0)
        {
            return;
        }

        attempts.Sort((a, b) => string.CompareOrdinal(a.Ticker, b.Ticker));

        await context.Data.BulkUpsertAsync(
            "fundamental_fetch_attempt", AttemptColumns, AttemptConflictTarget,
            async (w, c) =>
            {
                foreach (var a in attempts)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(a.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(a.AttemptedOn, c).ConfigureAwait(false);
                    await w.WriteAsync(a.LastYield, c).ConfigureAwait(false);
                    await w.WriteAsync(a.Rows, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
    }

    private readonly record struct Loaded(
        IReadOnlyList<ResolvedPeriod> Periods, int? WidestCleanGapDays, int SubstitutedCount, long Written,
        int EarningsCollisions);

    private async Task<Loaded?> LoadAsync(StageContext context, string ticker, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            // **Unfiltered, and it costs the same** [3.1, measured]. One call now
            // carries `Financials`, `Earnings::History` and `General::Sector` where the
            // filtered form carried the first alone, so the capture D-96 and D-97 need
            // rides the sweep already being paid for [D-96].
            doc = await _client.GetAsync("fundamentals/" + ticker, [], ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // **A 404 alone** [3.6, open item 18]. A ticker the fundamentals endpoint
            // does not carry is the ordinary case for an index or a fund, and the
            // universe excludes those anyway. Everything else is rethrown: a 402 is the
            // allowance wall reached in flight and it persists for the day, so
            // swallowed it would write nothing for this ticker and nothing for any
            // ticker after it, and the sweep would report a complete pass over a
            // partial load.
            return null;
        }

        using (doc)
        {
            // The payload is whole now, so the statements are one level down where the
            // filtered form put them at the root.
            //
            // **The ValueKind guard is not defensive padding.** This endpoint answers a
            // ticker it carries no financials for with a bare JSON string, and
            // `TryGetProperty` throws on anything that is not an object rather than
            // returning false. The 0006 fixture for that shape is what caught it.
            var financials = doc.RootElement.ValueKind == JsonValueKind.Object
                             && doc.RootElement.TryGetProperty("Financials", out var f)
                ? f
                : doc.RootElement;

            var statements = Statements.Parse(financials);
            if (statements.Count == 0)
            {
                return null;
            }

            // General::Sector off the same call [D-97]. C01's per-member call bought at
            // 10 units what this payload carries for nothing.
            var sector = Sector(doc.RootElement);

            var earnings = EarningsHistory.Parse(doc.RootElement, ticker, out var collisions);
            await WriteEarningsAsync(context, earnings, ct).ConfigureAwait(false);

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
                        await WriteRowAsync(w, ticker, p, s, sector, c).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);

            return new Loaded(
                resolved.Periods, resolved.WidestCleanGapDays, resolved.SubstitutedCount, written,
                collisions);
        }
    }

    /// <summary>
    /// The earnings history for one ticker [D-96].
    ///
    /// Deduped by the parse before it gets here, so the rows carry distinct period ends
    /// and the upsert's conflict target cannot be hit twice in one statement.
    /// </summary>
    private static async Task WriteEarningsAsync(
        StageContext context, IReadOnlyList<EarningsPeriod> periods, CancellationToken ct)
    {
        if (periods.Count == 0)
        {
            return;
        }

        await context.Data.BulkUpsertAsync(
            "earnings_history", EarningsColumns, EarningsConflictTarget,
            async (w, c) =>
            {
                foreach (var p in periods)
                {
                    await w.StartRowAsync(c).ConfigureAwait(false);
                    await w.WriteAsync(p.Ticker, c).ConfigureAwait(false);
                    await w.WriteAsync(p.PeriodEnd, c).ConfigureAwait(false);
                    await w.WriteAsync(p.ReportDate, c).ConfigureAwait(false);
                    await w.WriteAsync(p.BeforeAfterMarket, c).ConfigureAwait(false);
                    await w.WriteAsync(p.EpsActual, c).ConfigureAwait(false);
                    await w.WriteAsync(p.EpsEstimate, c).ConfigureAwait(false);
                    await w.WriteAsync(p.SurpriseFraction, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
    }

    private static async Task WriteRowAsync(
        IBulkWriter w, string ticker, ResolvedPeriod p, Statement s, string? sector, CancellationToken ct)
    {
        await w.StartRowAsync(ct).ConfigureAwait(false);
        await w.WriteAsync(ticker, ct).ConfigureAwait(false);
        await w.WriteAsync(p.PeriodEnd, ct).ConfigureAwait(false);
        await w.WriteAsync("quarterly", ct).ConfigureAwait(false);
        await w.WriteAsync(p.FilingDate, ct).ConfigureAwait(false);
        await w.WriteAsync(p.Effective, ct).ConfigureAwait(false);
        await w.WriteAsync(p.Reason, ct).ConfigureAwait(false);
        await w.WriteAsync(s.FilingDateSource, ct).ConfigureAwait(false);
        await w.WriteAsync(sector, ct).ConfigureAwait(false);

        foreach (var name in Columns.Skip(8))
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
        // Reads rather than one query joining several tables. IStageData checks the
        // table a caller names and cannot see what the SQL actually touches, so a
        // join here would be leaning on that gap rather than being checked by it.
        //
        // **Strictly before the run date** [0006]. Attempts written by this date's
        // own run are invisible here, so a re-run of one date sees what the first run
        // saw and selects the same names: the stage stays a pure function of its date
        // and config version, and the rotation advances between dates rather than
        // between runs [`CLAUDE.md` §6]. It is the same point-in-time discipline
        // every fundamental read applies to `filing_date_effective` [INVARIANT 12].
        var asOf = context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var attemptRows = await context.Data.ReadAsync(
            "fundamental_fetch_attempt",
            $"""
             SELECT ticker, last_attempted_date, last_yield_date
             FROM fundamental_fetch_attempt
             WHERE last_attempted_date < DATE '{asOf}'
             ORDER BY ticker;
             """,
            ct).ConfigureAwait(false);

        var attempted = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        var priorYield = new Dictionary<string, DateOnly>(StringComparer.Ordinal);

        // `date` comes back as DateTime through the generic reader, which is the
        // form every other stage in this project converts from.
        foreach (var r in attemptRows)
        {
            var t = (string) r[0]!;
            attempted[t] = DateOnly.FromDateTime((DateTime) r[1]!);

            if (r[2] is DateTime y)
            {
                priorYield[t] = DateOnly.FromDateTime(y);
            }
        }

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

        // The ordering moved to RotationSelection at 3.5, unchanged, so C05 can share
        // the rule rather than a copy of it [D-95]. It was copied by hand once
        // already and then kept this component's defect after this component was
        // fixed, which is the drift a shared function removes. The reasoning behind
        // each tier is there rather than restated here.
        var justReported = await JustReportedAsync(context, ct).ConfigureAwait(false);

        var rotation = RotationSelection.For(pool, attempted, inUniverse, maxPerRun, justReported);

        return new Selection(
            rotation.Selected,
            rotation.PoolSize,
            rotation.NeverAttempted,
            rotation.NewInSelection,
            rotation.RefreshedInSelection,
            rotation.OldestAttemptInSelection,
            priorYield);
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
