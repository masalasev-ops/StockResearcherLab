using System.Globalization;
using System.Net;
using System.Text.Json;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// C05. Form 4, at its own natural grain, which the compute layer derives
/// <c>flow_daily</c> from together with the holdings C03 ingests [D-61].
///
/// **The institutional half left at D-98** and <see cref="InstitutionalHolders"/> now
/// carries it. It was a second call to <c>fundamentals/{t}</c>, filtered, and the
/// filter is a projection of the document C03 receives unfiltered in a call already
/// paid for. Form 4 stays here because <c>sec-filings/{t}/form4</c> is a different
/// endpoint and the only one of the two that pages: <c>meta.total</c> matched the
/// filings index on every ticker checked, so it is fully backfillable where the
/// holdings block has no series at all [D-69, 1.9].
///
/// **Never the legacy `insider-transactions` endpoint.** It returned zero over 90
/// days for all seven probe names including the control, and market-wide it is stale
/// by about three months and carries US Congress member trades, which are not Form 4
/// insider filings.
/// </summary>
public sealed class FlowIngestor : IStage, IBackfillStage
{
    public static readonly string[] InsiderColumns =
    [
        "ticker", "accession_number", "transaction_side", "transaction_ordinal",
        "filed_at", "transaction_date", "reporting_owner_cik", "reporting_owner_name",
        "transaction_code", "security_title", "shares_amount", "price_per_share",
        "total_value", "shares_owned_after", "acquired_or_disposed",
    ];

    /// <summary>
    /// The attempt record [D-95, 0008]. Written for every ticker the run selected,
    /// whether or not the fetch yielded rows, which is the distinction the old
    /// ordering could not make. Same four columns as C03's, because it is the same
    /// record of the same kind of act.
    /// </summary>
    public static readonly string[] AttemptColumns =
        ["ticker", "last_attempted_date", "last_yield_date", "rows_last_attempt"];

    private static readonly string[] InsiderKey =
        ["ticker", "accession_number", "transaction_side", "transaction_ordinal"];

    private static readonly string[] AttemptConflictTarget = ["ticker"];

    private readonly EodhdClient _client;

    public FlowIngestor(EodhdClient client) => _client = client;

    public string Name => "FlowIngestor";

    // `flow_fetch_attempt` is not here and belongs in neither list twice. A stage may
    // read what it writes, which is what `DeclaredAccess.CanRead` says: the rotation
    // reads it back to decide what to fetch next [0008].
    public IReadOnlyList<string> ReadSet { get; } = ["security"];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new TableWrite("insider_transaction", WriteOperation.Insert, InsiderColumns),
        new TableWrite("flow_fetch_attempt", WriteOperation.Insert, AttemptColumns),
    ];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        var maxPerRun = (int) await LongAsync(context, "flow.max_tickers_per_run", ct).ConfigureAwait(false);
        var pageSize = (int) await LongAsync(context, "flow.form4_page_size", ct).ConfigureAwait(false);

        var selection = await UniverseAsync(context, maxPerRun, ct).ConfigureAwait(false);
        var tickers = selection.Tickers;

        long insiderRows = 0;

        // Per affected ticker, because a count alone cannot say whether the missing
        // rows can reach a trailing window [D-71]. Sorted before rendering, since
        // the tickers are walked in a fixed order but the list still reaches output.
        var shortfalls = new List<Shortfall>();

        // One entry per selected ticker, written below whether or not the fetch
        // yielded anything. A ticker that returns nothing still has to move down the
        // rotation, or it sits at the head of it for ever [D-95]. The 14 of 250 that
        // answer 404 are exactly that case, and 3.1 measured a 404 at 10 units.
        var attempts = new List<Attempt>(tickers.Count);

        foreach (var ticker in tickers)
        {
            // One call a ticker since D-98, where it was two. The second was a filtered
            // read of the payload C03 fetches whole, so what it bought at 10 units a
            // ticker was a projection rather than a source.
            var written = await LoadInsiderAsync(context, ticker, pageSize, shortfalls, ct)
                .ConfigureAwait(false);

            insiderRows += written;

            // A run that yields nothing must not erase the date a previous one did,
            // or the two absences collapse back into each other.
            DateOnly? lastYield = written > 0
                ? context.Date
                : selection.PriorYield.TryGetValue(ticker, out var prior) ? prior : null;

            attempts.Add(new Attempt(ticker, context.Date, lastYield, written));
        }

        await RecordAttemptsAsync(context, attempts, ct).ConfigureAwait(false);

        // Coverage first, exactly as C03 reports it. A run that re-walks the same
        // 250 names and one that reaches 250 new ones look identical from a row
        // count, which is how the truncation below went unseen until sign-off.
        //
        // The oldest attempt in the selection is the number that says the rotation is
        // still moving once coverage completes: it advances run by run, where every
        // count above it stops moving [D-95].
        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} insider transaction row(s) over {1:N0} ticker(s). Candidate pool {2:N0}, of which " +
            "{3:N0} have never been attempted; {4:N0} of this run's selection were new and {5:N0} were " +
            "refreshed, the oldest attempt among them dated {6}. Institutional holdings are C03's " +
            "since D-98 and are reported there. {7}",
            insiderRows, tickers.Count,
            selection.PoolSize, selection.NeverAttempted, selection.NewInSelection,
            selection.RefreshedInSelection,
            selection.OldestAttemptInSelection?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none",
            DescribeShortfalls(shortfalls));

        return new StageResult(insiderRows, "ok", detail);
    }

    // ------------------------------------------------------- range mode [3.9] ---

    /// <summary>
    /// One universe pass over <c>form4</c>, whole history per ticker [D-93].
    ///
    /// **The pool is the live universe and it does not widen, which is the one place
    /// D-101 does not reach.** Every other ingest pool in this phase gained the
    /// in-window delisted names; this one cannot, because `sec-filings` answers 404
    /// for a delisted ticker against the same string `eod/{t}` and `fundamentals/{t}`
    /// return series for [open item 12]. The bias is therefore a fact about the
    /// provider rather than a choice made here, and it is recorded against S4 rather
    /// than worked around.
    ///
    /// **The attempt stamps the range end, which is C03's half of D-99's asymmetry.**
    /// The nightly call and the sweep call are the same call: both walk `form4` to the
    /// end and neither takes a depth parameter, so a ticker the rotation covered is as
    /// complete as one this sweep covered and skipping it is correct. That is the
    /// condition D-99 names, and C02's range-start stamp exists only because its two
    /// calls differ in depth. The column carries the rotation's freshness ordering as
    /// well, which is the same one-column-two-purposes D-99 flagged on C03 and did not
    /// split.
    ///
    /// **This is the sweep the allowance gate exists for** [3.9]. About 258,000 units
    /// across three days, so it halts twice in the ordinary course and a halt is the
    /// mechanism working rather than a failure.
    /// </summary>
    public async Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = await context.ForDateAsync(context.To, ct).ConfigureAwait(false);

        var pageSize = (int) await LongAsync(settings, "flow.form4_page_size", ct).ConfigureAwait(false);
        var weight = await LongAsync(settings, "backfill.weight_form4_page", ct).ConfigureAwait(false);
        var reserve = await LongAsync(settings, "backfill.unit_reserve", ct).ConfigureAwait(false);
        var allowance = await LongAsync(settings, "backfill.daily_unit_allowance", ct).ConfigureAwait(false);

        var pool = await RangePoolAsync(settings, ct).ConfigureAwait(false);
        var already = await AttemptedOnAsync(settings, context.To, ct).ConfigureAwait(false);
        var remaining = pool.Where(t => !already.Contains(t)).ToList();

        long insiderRows = 0;
        var walked = 0;
        var shortfalls = new List<Shortfall>();
        var attempts = new List<Attempt>(remaining.Count);

        string? haltedOn = null;

        // **Serial rather than parallel, and that is the paging.** C02 and C03 fan out
        // because one ticker is one call; here one ticker is a walk whose length is
        // discovered as it runs, so a bounded worker pool would have several walks
        // asking the gate about the same remaining allowance and each answer would be
        // stale by however many pages the others fetched meanwhile.
        foreach (var ticker in remaining)
        {
            // **The gate is asked per page, not per ticker** [3.9]. A ticker's walk is
            // ten units a page over an unknown page count, so a per-ticker projection
            // would either overstate and stop early or understate and overshoot. The
            // cost is a `/api/user` read per page, which spends no units.
            var read = await WalkAsync(
                ticker, pageSize,
                async token => (await context.NextUnitAsync(weight, reserve, allowance, token)
                    .ConfigureAwait(false)).Fits,
                ct).ConfigureAwait(false);

            if (read is null)
            {
                // 404, being a ticker the filings index does not carry. An ordinary
                // fact rather than a fault, and it still takes an attempt row so the
                // sweep does not offer it again.
                attempts.Add(new Attempt(ticker, context.To, null, 0));
                walked++;
                continue;
            }

            var written = await WriteFilingsAsync(settings, ticker, read.Value.Rows, ct).ConfigureAwait(false);
            insiderRows += written;

            if (read.Value.StoppedByGate)
            {
                // **No attempt row, deliberately.** The rows collected are kept, every
                // write being idempotent per grain [D-68], and the ticker stays in the
                // remaining set so the next run walks it whole. Stamping it here would
                // freeze a partial history behind a record saying it was covered, which
                // is the one outcome the attempt record exists to prevent.
                haltedOn = ticker;
                break;
            }

            if (read.Value.Shortfall > 0)
            {
                shortfalls.Add(new Shortfall(ticker, read.Value.Shortfall, read.Value.Position));
            }

            attempts.Add(new Attempt(ticker, context.To, written > 0 ? context.To : null, written));
            walked++;
        }

        await RecordAttemptsAsync(settings, attempts, ct).ConfigureAwait(false);

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} insider transaction row(s) over {1:N0} of {2:N0} universe member(s). {3:N0} carried " +
            "an attempt for this sweep already and were not walked [D-99]. The pool is live names only " +
            "and cannot widen [open item 12]. {4}",
            insiderRows, walked, pool.Count, pool.Count - remaining.Count,
            DescribeShortfalls(shortfalls));

        if (haltedOn is null)
        {
            return BackfillResult.Completed(insiderRows, context.To, detail);
        }

        return BackfillResult.Halted(insiderRows, context.To, detail + " " + DescribeGatedHalt(haltedOn));
    }

    /// <summary>
    /// The gated halt's own sentence, kept out of the shortfall line [3.9].
    ///
    /// **A gated stop and a D-71 short page are two different observations and the run
    /// log has to be readable as two.** One is this system declining to spend and the
    /// other is the provider withholding rows it claimed to have; they are one absorbed
    /// observation apart, and a sweep that rendered the first as the second would put a
    /// spending decision into the record as a provider defect while leaving a short
    /// history unremarked.
    ///
    /// Public so the distinction is asserted directly. C05's sweep pool is `security`,
    /// which on a developer database is the live universe, so an end-to-end range test
    /// would stamp an attempt row for every real ticker [open items 10 and 26].
    /// </summary>
    public static string DescribeGatedHalt(string ticker) => string.Format(
        CultureInfo.InvariantCulture,
        "HALTED on the allowance gate at {0}, mid-walk. That ticker carries no attempt row and is " +
        "walked again from its first page by the next run, so no partial history is recorded as " +
        "complete. This is not a D-71 shortfall and is not counted as one.",
        ticker);

    /// <summary>
    /// The sweep's pool, which is the live universe in ticker order.
    ///
    /// No rotation and no cap: the sweep's job is one pass over everything, where the
    /// nightly stage's job is to move a bounded number of names along [D-95].
    /// </summary>
    private static async Task<IReadOnlyList<string>> RangePoolAsync(
        StageContext context, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "security", "SELECT ticker FROM security WHERE is_active ORDER BY ticker;", ct)
            .ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToList();
    }

    /// <summary>
    /// Tickers already carrying an attempt at this sweep's range end, which are the
    /// ones it does not walk again [D-99].
    /// </summary>
    private static async Task<IReadOnlySet<string>> AttemptedOnAsync(
        StageContext context, DateOnly asOf, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "flow_fetch_attempt",
            $"""
             SELECT ticker
             FROM flow_fetch_attempt
             WHERE last_attempted_date = DATE '{asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'
             ORDER BY ticker;
             """,
            ct).ConfigureAwait(false);

        return rows.Select(r => (string) r[0]!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// One ticker's <c>form4</c> walk, or null where the filings index does not carry
    /// the ticker.
    /// </summary>
    private async Task<PagedRead?> WalkAsync(
        string ticker, int pageSize, Func<CancellationToken, Task<bool>>? gate, CancellationToken ct)
    {
        try
        {
            return await _client.GetAllPagesAsync(
                "sec-filings/" + ticker + "/form4", [], pageSize, ct, gate).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>The parse and the write, shared by both entry points.</summary>
    private static async Task<long> WriteFilingsAsync(
        StageContext context, string ticker, IReadOnlyList<JsonElement> raw, CancellationToken ct)
    {
        var rows = ParseFilings(ticker, raw);
        if (rows.Count == 0)
        {
            return 0;
        }

        return await context.Data.BulkUpsertAsync(
            "insider_transaction", InsiderColumns, InsiderKey,
            async (w, c) =>
            {
                foreach (var r in rows)
                {
                    await WriteInsiderRowAsync(w, r, c).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
    }

    /// <param name="PoolSize">The whole active universe, which is the pool here.</param>
    /// <param name="NeverAttempted">Pool members with no row in <c>flow_fetch_attempt</c> yet.</param>
    /// <param name="NewInSelection">How many of this run's selection were among them.</param>
    /// <param name="RefreshedInSelection">The rest, which have been attempted before.</param>
    /// <param name="OldestAttemptInSelection">The oldest attempt date among those, or null.</param>
    /// <param name="PriorYield">
    /// The yield date each selected ticker already had, so a run that yields nothing
    /// does not erase it.
    /// </param>
    public readonly record struct Selection(
        IReadOnlyList<string> Tickers,
        int PoolSize,
        int NeverAttempted,
        int NewInSelection,
        int RefreshedInSelection,
        DateOnly? OldestAttemptInSelection,
        IReadOnlyDictionary<string, DateOnly> PriorYield);

    /// <summary>One attempt, written whether or not it yielded rows [D-95].</summary>
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
            "flow_fetch_attempt", AttemptColumns, AttemptConflictTarget,
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

    /// <param name="Position">
    /// `interior` means at least one page before the last came back short, so the
    /// missing rows sit inside the history and a trailing-90-day metric can be
    /// affected. `final` means they sit at the oldest end, outside every trailing
    /// window [D-71].
    /// </param>
    public readonly record struct Shortfall(string Ticker, int Rows, ShortfallPosition Position);

    /// <summary>
    /// The two counts D-71 requires in the run log, and the per-ticker positions
    /// behind them.
    ///
    /// **Uncapped**, unlike the wide-filer list C03 renders. The decision asks for
    /// the position per affected ticker and a truncated list answers it for a
    /// sample, which is the difference between a record and an impression. At the
    /// measured rate of 43 tickers in 250 a full universe pass renders a few
    /// hundred entries into one text column, which is the evidence file rather than
    /// a summary of it.
    /// </summary>
    public static string DescribeShortfalls(IReadOnlyList<Shortfall> shortfalls)
    {
        if (shortfalls.Count == 0)
        {
            return "No ticker under-delivered against meta.total [D-71]";
        }

        var ordered = shortfalls
            .OrderBy(x => x.Ticker, StringComparer.Ordinal)
            .ToList();

        var interior = ordered.Count(x => x.Position is ShortfallPosition.Interior or ShortfallPosition.Both);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:N0} ticker(s) under-delivered against meta.total, {1:N0} row(s) short in total, of which " +
            "{2:N0} ticker(s) are short inside the history where a trailing window can reach them and " +
            "{3:N0} only at the oldest end [D-71]: {4}",
            ordered.Count,
            ordered.Sum(x => x.Rows),
            interior,
            ordered.Count - interior,
            string.Join(", ", ordered.Select(x => string.Format(
                CultureInfo.InvariantCulture, "{0} {1} {2}",
                x.Ticker, x.Rows, x.Position.ToString().ToLowerInvariant()))));
    }

    /// <summary>
    /// Tickers to walk this run, ordered so coverage advances rather than repeating.
    ///
    /// **This was `ORDER BY ticker LIMIT 250` and that made the cap a filter rather
    /// than a rate limit** [sign-off finding B]. The same 250 names were selected on
    /// every pass, so `insider_transaction` held 226 tickers of a 2,841 name universe
    /// with every one of them inside ordinal ranks 1 to 250 and none outside, and
    /// which names the flow screen could ever see was decided by ticker spelling.
    /// INVARIANT 1 puts absolute filters in the universe definition and nowhere else;
    /// a cap every name eventually passes through is a bound on a night's spend,
    /// which is what this now is.
    ///
    /// **The ordering is `RotationSelection`, shared with C03 rather than copied from
    /// it** [D-95]. It was copied by hand at 1.7 and then kept this component's
    /// defect after C03's was fixed at D-91, which is the drift a shared function
    /// removes. C03's universe tier is a tiebreak here too and simply never fires,
    /// because this pool is the universe.
    /// </summary>
    private static async Task<Selection> UniverseAsync(
        StageContext context, int maxPerRun, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "security", "SELECT ticker FROM security WHERE is_active ORDER BY ticker;", ct)
            .ConfigureAwait(false);

        var pool = rows.Select(r => (string) r[0]!).ToList();

        // **Strictly before the run date** [D-95]. A re-run of one date therefore sees
        // the state the first run saw and selects the same names, so the stage stays a
        // pure function of its date and config version; the rotation advances between
        // dates rather than between runs [`CLAUDE.md` §6, INVARIANT 13].
        //
        // Reading what it writes, which DeclaredAccess permits without a second
        // declaration: the read set would otherwise say this stage reads a table it
        // owns, which is not what a read set means.
        var asOf = context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var attemptRows = await context.Data.ReadAsync(
            "flow_fetch_attempt",
            $"""
             SELECT ticker, last_attempted_date, last_yield_date
             FROM flow_fetch_attempt
             WHERE last_attempted_date < DATE '{asOf}'
             ORDER BY ticker;
             """,
            ct).ConfigureAwait(false);

        var attempted = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        var priorYield = new Dictionary<string, DateOnly>(StringComparer.Ordinal);

        // `date` comes back as DateTime through the generic reader, which is the form
        // every other stage in this project converts from.
        foreach (var r in attemptRows)
        {
            var t = (string) r[0]!;
            attempted[t] = DateOnly.FromDateTime((DateTime) r[1]!);

            if (r[2] is DateTime y)
            {
                priorYield[t] = DateOnly.FromDateTime(y);
            }
        }

        var inUniverse = pool.ToHashSet(StringComparer.Ordinal);
        var rotation = RotationSelection.For(pool, attempted, inUniverse, maxPerRun);

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
    /// Form 4, walked to the end on <c>page[offset]</c> and <c>page[limit]</c>.
    ///
    /// `limit` and `offset` are accepted and silently ignored by this endpoint, so a
    /// call using them returns one page and looks complete [1.9].
    ///
    /// The client compares the collected count against <c>meta.total</c> and the two
    /// outcomes are not the same failure [D-71]. Stopping while a next link is still
    /// offered fails the stage, because what was missed is unknown and asking again
    /// would fix it. Running the server out of pages and still coming up short is
    /// the provider disagreeing with itself, and is recorded here rather than
    /// halting the night: at the measured rate a universe pass would never complete.
    /// </summary>
    private async Task<long> LoadInsiderAsync(
        StageContext context, string ticker, int pageSize, List<Shortfall> shortfalls,
        CancellationToken ct)
    {
        // **A 404 alone, narrowed at 3.9** [open item 18]. This caught
        // `HttpRequestException` whole, so a 402 or a 429 reaching it was recorded as a
        // ticker with no filings index: a night that hit the allowance wall in flight
        // wrote nothing for that name and nothing for any name after it, and returned
        // having completed over a partial load. The walk shares its catch with the
        // sweep's, so there is one rule rather than two [D-100's reasoning, one layer
        // up].
        var read = await WalkAsync(ticker, pageSize, gate: null, ct).ConfigureAwait(false);
        if (read is null)
        {
            return 0;
        }

        // The server ran out of pages with rows still unaccounted for. Recorded and
        // continued, because asking again cannot produce them and halting means a
        // universe pass never completes [D-71]. The client throws instead where the
        // loop stopped while a next link was still on offer.
        if (read.Value.Shortfall > 0)
        {
            shortfalls.Add(new Shortfall(ticker, read.Value.Shortfall, read.Value.Position));
        }

        return await WriteFilingsAsync(context, ticker, read.Value.Rows, ct).ConfigureAwait(false);
    }

    /// <summary>One insider row into an open COPY stream. Column order is <see cref="InsiderColumns"/>.</summary>
    private static async Task WriteInsiderRowAsync(IBulkWriter w, InsiderRow r, CancellationToken c)
    {
        await w.StartRowAsync(c).ConfigureAwait(false);
        await w.WriteAsync(r.Ticker, c).ConfigureAwait(false);
        await w.WriteAsync(r.Accession, c).ConfigureAwait(false);
        await w.WriteAsync(r.Side, c).ConfigureAwait(false);
        await w.WriteAsync(r.Ordinal, c).ConfigureAwait(false);
        await w.WriteAsync(r.FiledAt, c).ConfigureAwait(false);
        await w.WriteAsync(r.TransactionDate, c).ConfigureAwait(false);
        await w.WriteAsync(r.OwnerCik, c).ConfigureAwait(false);
        await w.WriteAsync(r.OwnerName, c).ConfigureAwait(false);
        await w.WriteAsync(r.Code, c).ConfigureAwait(false);
        await w.WriteAsync(r.SecurityTitle, c).ConfigureAwait(false);
        await w.WriteAsync(r.Shares, c).ConfigureAwait(false);
        await w.WriteAsync(r.Price, c).ConfigureAwait(false);
        await w.WriteAsync(r.TotalValue, c).ConfigureAwait(false);
        await w.WriteAsync(r.SharesOwnedAfter, c).ConfigureAwait(false);
        await w.WriteAsync(r.AcquiredOrDisposed, c).ConfigureAwait(false);
    }

    /// <param name="Ordinal">
    /// Position within its array, as the provider ordered it. **This is a key part
    /// and not a detail** [D-68 reopened at 1.7]: over 1,069 real transactions no
    /// combination of a transaction's own attributes was unique, because two line
    /// items in one filing can be identical on every value the provider sends.
    /// </param>
    public readonly record struct InsiderRow(
        string Ticker, string Accession, string Side, int Ordinal,
        DateOnly? FiledAt, DateOnly? TransactionDate, string? OwnerCik, string? OwnerName,
        string? Code, string? SecurityTitle, decimal? Shares, decimal? Price,
        decimal? TotalValue, decimal? SharesOwnedAfter, string? AcquiredOrDisposed);

    public static IReadOnlyList<InsiderRow> ParseFilings(string ticker, IReadOnlyList<JsonElement> filings)
    {
        var rows = new List<InsiderRow>();

        foreach (var filing in filings)
        {
            if (filing.ValueKind != JsonValueKind.Object
                || !filing.TryGetProperty("accession_number", out var acc)
                || acc.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var accession = acc.GetString()!;
            var filedAt = Date(filing, "filed_at");

            // Both arrays, numbered independently, because a Form 4 reports
            // non-derivative and derivative holdings separately.
            foreach (var side in new[] { "non_derivative", "derivative" })
            {
                if (!filing.TryGetProperty(side, out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var ordinal = 0;
                foreach (var tx in arr.EnumerateArray())
                {
                    if (tx.ValueKind != JsonValueKind.Object)
                    {
                        ordinal++;
                        continue;
                    }

                    rows.Add(new InsiderRow(
                        ticker, accession, side, ordinal++,
                        filedAt,
                        Date(tx, "transaction_date"),
                        Text(tx, "reporting_owner_cik"),
                        Text(tx, "reporting_owner_name"),
                        Text(tx, "transaction_code"),
                        Text(tx, "security_title"),
                        Money(tx, "shares_amount"),
                        Money(tx, "price_per_share"),
                        Money(tx, "total_value"),
                        Money(tx, "shares_owned_after"),
                        Text(tx, "acquired_or_disposed")));
                }
            }
        }

        // Ordinal by the key, so COPY order is stable across runs.
        rows.Sort(static (a, b) =>
        {
            var t = string.CompareOrdinal(a.Accession, b.Accession);
            if (t != 0) return t;
            t = string.CompareOrdinal(a.Side, b.Side);
            return t != 0 ? t : a.Ordinal.CompareTo(b.Ordinal);
        });

        return rows;
    }

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && !string.IsNullOrEmpty(v.GetString())
            ? v.GetString()
            : null;

    /// <summary>
    /// The provider sends transaction dates as ISO instants and filing dates as
    /// plain dates, so both forms are accepted. A trading date is a label rather
    /// than a timezone conversion, so the date part is taken as written.
    /// </summary>
    private static DateOnly? Date(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var s = v.GetString();
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        return s.Length >= 10
               && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)
            ? iso
            : null;
    }

    /// <summary>Money and share counts as decimal [INVARIANT 16]. Absent stays null: zero shares is a real value.</summary>
    private static decimal? Money(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(
                v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null,
            _ => null,
        };
    }

    private static async Task<long> LongAsync(StageContext context, string key, CancellationToken ct)
    {
        var row = await context.Config.RequireAsync(key, context.Date, ct).ConfigureAwait(false);
        return long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new InvalidOperationException($"{key} resolved to '{row.Value}', which is not a whole number.");
    }
}
