using System.Globalization;
using Npgsql;
using StockResearcherLab.Core.Config;

namespace StockResearcherLab.Data;

/// <summary>
/// <c>config_rows</c> behind <see cref="IConfigStore"/>.
///
/// The date filter is applied in SQL and the version choice in
/// <see cref="ConfigResolution"/>, so the rule that decides which version wins is
/// one pure function with tests rather than a fragment of a query nobody can
/// exercise without a database.
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    private readonly string _connectionString;

    public ConfigStore(string connectionString) => _connectionString = connectionString;

    public async Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
    {
        var rows = await ReadAsync(key, ct).ConfigureAwait(false);
        return ConfigResolution.Resolve(rows, key, asOf);
    }

    public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
        => await ResolveAsync(key, asOf, ct).ConfigureAwait(false)
           ?? throw new ConfigNotInForceException(key, asOf);

    public async Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var rows = await ReadAsync(key: null, ct).ConfigureAwait(false);
        return ConfigResolution.ResolveVersion(rows, asOf);
    }

    public async Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
        => await ResolveVersionAsync(asOf, ct).ConfigureAwait(false)
           ?? throw new ConfigVersionNotInForceException(asOf);

    /// <param name="key">
    /// One key, or null for every row in the store. The store-wide version spans
    /// keys by definition, so it cannot be answered from one key's rows.
    /// </param>
    private async Task<IReadOnlyList<ConfigRow>> ReadAsync(string? key, CancellationToken ct)
    {
        // set_at is a timestamptz and every question asked of config is asked
        // about a trading date, so it is reduced to one here. US Eastern rather
        // than UTC or the server's zone, because all market semantics in this
        // system are US Eastern and a trading date is the label the exchange gave
        // a session [CLAUDE.md section 6].
        // @key IS NULL selects every row, which is what the store-wide version
        // needs. Written as one statement rather than two so both paths reduce
        // set_at to a US Eastern date the same way; two statements is how the
        // conversion drifts between them.
        const string sql = """
            SELECT key, version, value::text, (set_at AT TIME ZONE 'America/New_York')::date
            FROM config_rows
            WHERE @key::text IS NULL OR key = @key::text
            ORDER BY key, version;
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key", (object?) key ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new List<ConfigRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new ConfigRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                DateOnly.FromDateTime(reader.GetDateTime(3))));
        }

        return rows;
    }

    /// <summary>
    /// Every key beginning with <paramref name="prefix"/>, at the version in force on
    /// <paramref name="asOf"/>, ordered by key.
    ///
    /// **The whole store is read and filtered here rather than filtered in SQL**, which
    /// is the same shape <see cref="ResolveVersionAsync"/> already uses. Resolution is
    /// <see cref="ConfigResolution.Resolve"/>'s and not a second `MAX(version)` written
    /// in a `WHERE` clause: one implementation of "in force on this date" is what makes
    /// INVARIANT 13 checkable, and a prefix query with its own version pick would be a
    /// second one that can disagree.
    ///
    /// Ordinal ordering, so two runs over one date enumerate screens identically
    /// [`CLAUDE.md` §6].
    /// </summary>
    public async Task<IReadOnlyList<ConfigRow>> ResolveByPrefixAsync(
        string prefix, DateOnly asOf, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var all = await ReadAsync(null, ct).ConfigureAwait(false);

        return [.. all
            .Select(r => r.Key)
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => ConfigResolution.Resolve(all, k, asOf))
            .Where(r => r is not null)
            .Select(r => r!)];
    }
}

/// <summary>
/// The configuration this phase consumes, seeded as version 1.
///
/// Values and their reasons are in CONFIG_REFERENCE.md and in the decisions it
/// cites. They are here as well because seeding is code, and the document records
/// the verified consumer rather than being read at runtime. A value appearing here
/// and not there is a defect in the same way round as the reverse.
/// </summary>
public sealed class ConfigSeeder
{
    /// <summary>
    /// The instant every seeded row is stamped with.
    ///
    /// At or before the earliest date the system will ever resolve config for,
    /// which is the start of the five-year backfill window rather than the moment
    /// the script ran [A9]. A wall-clock stamp would put every backfill date
    /// before every row, and every historical resolution would return nothing
    /// while today's resolution looked correct.
    ///
    /// A fixed literal rather than a computation from the current date, because
    /// two runs of the seeder must produce identical rows [CLAUDE.md section 6].
    ///
    /// **Midday UTC, not midnight, and that is the whole point of the choice.**
    /// Resolution reduces set_at to a US Eastern date, so a midnight-UTC stamp
    /// lands at 19:00 the previous day in New York and the row comes into force on
    /// 2019-12-31 while every line of prose about it says 2020-01-01. Midday is far
    /// enough from both boundaries that the Eastern date equals the written date in
    /// either offset, standard or daylight. This is the off-by-one CLAUDE.md
    /// section 6 warns about, where a trading date is treated as a timezone
    /// conversion of an instant, and it was caught by the test asserting that a
    /// date before the seed resolves to nothing.
    /// </summary>
    public static readonly DateTimeOffset SeedInstant =
        new(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The count is asserted rather than left to be miscounted: it
    /// was recorded as nine, corrected to eleven at A5, twelve at A10, fourteen at A14, fifteen at 1.4,
    /// seventeen at 1.6, nineteen at 1.7 and twenty-two at 1.8, and every correction was a key that existed with nothing seeding it.
    /// Thirty at 2.4, which is phase 2's seven plus percentile.cell_min_members:
    /// that one had been in CONFIG_REFERENCE.md since the corpus was written with
    /// nothing seeding it, which is the same defect as every correction above.
    /// Thirty-two at 2.9, D-80's two breadth thresholds once the rule existed.
    /// Forty-two at 3.3: the window start, the concurrency, the allowance and its
    /// reserve, and one weight for each of the six endpoints a sweep calls.
    ///
    /// **The number in the first line of this comment went stale and is now removed
    /// rather than corrected** [5.3]. It read "forty-two" while the asserted count had
    /// reached ninety-five, four phases having added keys without touching the word.
    /// A count stated in two places is a count that can disagree with itself, and the
    /// one that is asserted is the one in `ConfigResolutionTests`.
    ///
    /// **102 at 5.3's first commit**: six `digest.*` keys and D-60's disqualifier.
    /// Two further keys are deliberately not here and arrive in the second commit,
    /// D-143 being drafted and unauthored. The count therefore moves twice in one
    /// checkpoint and its test moves with it both times, which is the visible form of
    /// a checkpoint that landed in two commits rather than one.
    ///
    /// **104 at 5.3's second commit**, D-143 having been authored: the two keys it
    /// introduces arrive and the two it retires were never here to remove. That is
    /// what the first commit's absence assertions bought, and it is why this count
    /// moved by two rather than staying still while two names changed underneath it.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> Keys { get; } =
    [
        // Universe, D-4. Absolute filters, and the only place anything is filtered out.
        ("universe.min_market_cap", "300000000"),
        ("universe.min_price", "5"),
        ("universe.min_adv_20d", "2000000"),
        ("universe.min_history_days", "250"),
        ("universe.bucket_large_floor", "10000000000"),
        ("universe.bucket_mid_floor", "2000000000"),

        // The bound the two pool statements run under, which is not the connection
        // string's [D-102]. Both derive a candidate set from `price_daily` on D-4's
        // price-side criteria, both make one whole-heap pass, and both are on a nightly
        // path. Raising `Command Timeout` to 1800 to unblock a pool build also removed
        // the bound the upsert path ran under, and that bound is what caught item 27's
        // bloat failure at 300 seconds rather than letting it grind. The expensive
        // statement carries its own limit so the global can go back to being the value
        // every other statement is judged against.
        //
        // 1800 is where the global stood before this key existed, and the reason it is
        // not lower is measured rather than assumed. The pool build has been observed
        // at 46.4s warm, 474.7s cold on a quiet machine, and **over 900s on a machine
        // that had just run a vacuum, several whole-table scans and a five-hour
        // query**. A bound set from the quiet reading failed the sweep twice. The
        // spread is the OS page cache, this server having `shared_buffers` at the
        // 128MB default against an 18 GB table, so the statement's cost is a property
        // of what else has run rather than of the statement.
        //
        // **What this buys is not a smaller number, it is a scoped one.** The global
        // goes back to 300, which is the bound the upsert path runs under and the one
        // that caught item 27's bloat failure rather than letting it grind. Only the
        // two statements known to be slow carry 1800.
        ("universe.pool_statement_timeout_seconds", "1800"),

        // Fundamentals. The substitution floor is D-62; the two alerts are D-57
        // and D-62 and both emit through the run log, never to alert [A3].
        ("fundamentals.min_clean_gaps_for_substitution", "4"),
        ("fundamentals.substitution_rate_alert", "0.25"),
        ("fundamentals.widest_gap_alert_days", "180"),

        // How many tickers C03 fetches in one run. The fundamentals endpoint is per
        // ticker, so the whole candidate set is thousands of calls and a night
        // should not spend all of them on one stage. Rolling rather than complete,
        // which is what ARCHITECTURE.html section 3 already asks of C03.
        ("fundamentals.max_tickers_per_run", "500"),

        // Sentiment covers the whole universe [D-23], so the endpoint's
        // comma-separated symbol list is what keeps that from being one round trip
        // per name. The batch size is a latency knob and not a cost one: sentiment
        // is metered flat at 5 units per ticker whatever the batch, measured at 1,
        // 10 and 20 tickers [PROGRESS, endpoint weights].
        ("sentiment.tickers_per_call", "50"),
        ("sentiment.lookback_days", "30"),

        // C05's per-run bound and its page size. form4 is 10 units a page, so a
        // full universe pass is the most expensive thing in a week and is bounded
        // the same way C03 is [PROGRESS, endpoint weights].
        ("flow.max_tickers_per_run", "250"),
        ("flow.form4_page_size", "50"),

        // How long after a period end an institutional holding is treated as
        // public. A 13F is due within forty-five days of the quarter it reports,
        // and institutional_holding.report_date is that period end rather than a
        // filing date. Reading on report_date alone is the mistake INVARIANT 12
        // names for fundamentals, arriving through the other table that has the
        // same shape [C34, 1.8].
        ("flow.institutional_report_lag_days", "45"),

        // C06's earnings window, forward and back. Two readers want different
        // halves: C12's earnings blackout needs the next report date, and C03's
        // rotation lets a name that has just reported jump the queue. The calendar
        // endpoint is metered at 1 unit whatever the range, so the width costs
        // nothing and the bound is about what belongs in the table.
        ("events.earnings_forward_days", "90"),
        ("events.earnings_backward_days", "7"),

        // Freshness. The two row-count floors are D-59 and stand unrevised at
        // D-64's closure. The two settled_* values are D-70's and their reasoning
        // is in D-64, including why 0.95 sits near the measured part-settled
        // ceiling rather than midway between the populations.
        ("freshness.row_count_abort_below", "40000"),
        ("freshness.row_count_alert_below", "45000"),
        ("freshness.settled_fraction", "0.95"),
        ("freshness.settled_window_days", "20"),

        // C02's trailing re-load window [A10]. Not the guard's: a day loaded short
        // is topped up by re-loading it, and D-68's idempotent upsert is what makes
        // that safe. Without it a part-settled day stays part-settled for ever.
        ("price.reload_window_days", "20"),

        // The cell floor, D-10. Below this many members carrying a value for the
        // metric being ranked, the cell falls back to size bucket alone. It counts
        // the non-null population per metric rather than the cell, because a cell of
        // twenty where five carry a value otherwise yields a percentile computed
        // over five that is indistinguishable from one computed over twenty.
        ("percentile.cell_min_members", "15"),

        // C08's Wilder seed [2.1]. ATR and ADX are recursive rather than window
        // aggregates, so the recursion needs a start. Without a fixed warm-up the
        // answer depends on how much history the database happens to hold, two
        // databases with the same 250 bars and different amounts before them
        // disagree, neither is wrong, and the reference test cannot be written.
        // 250 rather than 14 because the 13/14 decay puts the seed's influence
        // under one part in ten thousand after 125 steps, which makes the choice
        // immaterial to the answer while keeping it exactly specified.
        ("indicator.wilder_warmup_bars", "250"),

        // base_breakout_flag's two conditions [2.1]. The lookback is the window a
        // breakout is measured against; the range cap is what makes that window a
        // base rather than a trend. Without the second, a name in a steady advance
        // sets a new window high most days and the flag is on permanently, which
        // carries no information and puts every strong trend into S2 twice.
        ("indicator.base_lookback_days", "60"),
        ("indicator.base_max_range_pct", "0.25"),

        // How many sampled points ev_ebit_vs_own_5y needs before it is computed
        // rather than null [2.1]. Month-ends over five years is sixty; twenty-four
        // is two years. Below it the column is null rather than a comparison
        // against a history too short to be one, which on a cold database is every
        // name until phase 3 has backfilled.
        ("valuation.own_history_min_points", "24"),

        // Breadth's window [2.1]. It matches dist_200dma's 200 and is a key rather
        // than a constant because, unlike the indicator windows, it is not named in
        // any column.
        ("market.breadth_ma_days", "200"),

        // Below this many members a sector composite is one name's noise rather
        // than a sector [2.1]. Read by C08 for rs_change_vs_sector and by C10 for
        // sector_relative_strength, which are the same construction at two grains.
        ("market.sector_composite_min_members", "5"),

        // How many days inside the window must carry a sentiment_daily row before
        // any derived sentiment metric is computed [2.1]. An absent day is zero
        // articles, but only once the ingest has reached the ticker at all, and
        // this is what separates those two cases.
        ("sentiment.min_baseline_days", "20"),

        // D-80's two breadth thresholds. Held back at 2.4 because the rule they
        // threshold was unauthored and a value with no rule behind it is a number
        // nothing can be read against. The benchmark half of the same rule has no key,
        // because a series is above or below its own average and zero is already
        // meaningful there.
        ("market.regime_breadth_high", "0.60"),
        ("market.regime_breadth_low", "0.40"),

        // ------------------------------------------------------- phase 3 [3.3] ---

        // The backfill window start, as a stored date [D-94]. A count of years
        // resolved against the run date moves the window on every re-run, so two
        // backfills over one store would compute different date sets and the phase's
        // fourth done-when line could not be tested at all.
        //
        // The first session of 2021, which is after SeedInstant and so resolvable
        // [D-72], and which opens the window before the 2021 advance rather than
        // inside it. That matters for one reason the plan states: the window has to
        // contain a real drawdown, and a drawdown needs the peak it fell from
        // [ARCHITECTURE.html section 05]. It is also the first string-valued key in
        // this store, so it is quoted here and read through ConfigValue.Date.
        ("backfill.window_start", "\"2021-01-04\""),

        // How many tickers a ticker-partitioned sweep works on at once. Not a
        // provider bound: EodhdRateLimiter already holds the 1,000-a-minute limit and
        // a sweep of 50,785 names at one unit each is nowhere near it. This bounds
        // how many Npgsql binary COPY streams and sockets are open at once, which is
        // the resource that actually runs out. Measured and revised at 3.6 rather
        // than guessed twice.
        ("backfill.ticker_concurrency", "8"),

        // ----------------------------------------------------- phase 3.5 [3.5.3] ---

        // How many of a ticker's most recent bars the record inspector shows on the
        // inputs panel [D-109].
        //
        // **A display bound rather than a metric window, and it is the one thing the
        // panel bounds that no component already owns.** The sentiment window is
        // sentiment.lookback_days and the insider window is FlowEngine.WindowDays,
        // which is a constant precisely because insider_net_90d_usd carries the number
        // in its name. Bars have neither, so the alternative here is a literal at the
        // call site, which is what CLAUDE.md section 8 rules out.
        //
        // Twenty, being the liquidity window D-4's dollar volume criterion is computed
        // over, so the panel shows the bars the membership panel's verdict rests on.
        ("inspector.recent_bars", "20"),

        // The allowance and what is held back from it. dailyRateLimit read 100,000
        // at 3.1, which is the same figure phase P and phase 1 read.
        //
        // The reserve is what a sweep may not eat into, so the nightly run still has
        // an allowance after a backfill day. A measured night is 45,518 units
        // [PROGRESS, 2026-08-09] and the rest is headroom for C04's universe-sized
        // pass moving with the universe. C01's weekly rebuild at 28,401 is NOT in it,
        // because 3.7 moves the sector call to C03 and takes that number to about
        // one; until 3.7 lands, a sweep sharing a Sunday with a C01 rebuild has less
        // margin than this number says, and that is recorded rather than padded,
        // since padding it would shrink every ordinary day's sweep for a case that
        // stops existing.
        ("backfill.daily_unit_allowance", "100000"),
        ("backfill.unit_reserve", "50000"),

        // One weight per endpoint a sweep calls, so the gate can project whether the
        // next unit of work fits before spending it. Seeded from the weights table
        // measured 2026-08-09 and confirmed against 3.1's own brackets, every one
        // exact. A weight at a call site is the magic number CLAUDE.md section 8
        // rules out, and these are measurements rather than constants: they project
        // whether the next unit fits and never decide that it did, which is what the
        // /api/user reading does.
        ("backfill.weight_eod", "1"),
        ("backfill.weight_fundamentals", "10"),
        ("backfill.weight_sentiments_per_ticker", "5"),
        ("backfill.weight_form4_page", "10"),
        ("backfill.weight_splits", "1"),
        ("backfill.weight_dividends", "1"),

        // The shared screen rules, which are one rule for every screen rather than any
        // screen's property. **These were documented in CONFIG_REFERENCE.md from the
        // first corpus and seeded by nothing until 4.5**, which found it by failing to
        // resolve the lookback: keys with values, decisions and a Consumer column behind
        // them, and no row in the store [D-7, D-9].
        //
        // screens.slot_ceiling was seeded here from 4.5 until Q.5 and is retired [D-127].
        // It never had a reader: screens.<id>.slots is what a screen has and what the
        // tuner moves, and a ceiling of eight cannot also cap that without contradicting
        // D-43's cap of twelve. The rows already inserted stay, config being append-only,
        // so this is a removal from the seeder and from the reference rather than a
        // delete. Same shape as D-116's retirement of the three quota keys.
        ("screens.floor_percentile", "98"),
        ("screens.floor_lookback_days", "250"),

        // Screens, as config rows. The set of screens is discovered from these keys
        // and never from a list in code, which is what CLAUDE.md section 5 means by a
        // screen being a row: a sixth screen is an insert, not a deployment.
        //
        // Direction lives here and no _inv column exists. Percentiles are ascending
        // always, so the word says what the screen rewards rather than how anything
        // sorts, and section 05's three _inv names are seeded as "low" [D-113].
        //
        // min_inputs was chosen against measured coverage rather than picked; the
        // table and both dates it was read on are in CONFIG_REFERENCE.md [D-112].
        // S1 Quality at a fair price. Seven ranked inputs, three of them read low.
        ("screens.S1.metrics", """[{"metric":"fcf_yield","direction":"high","weight":1},{"metric":"ev_ebit_vs_own_5y","direction":"low","weight":1},{"metric":"roic_4q_change","direction":"high","weight":1},{"metric":"gross_margin_4q_change","direction":"high","weight":1},{"metric":"net_debt_ebitda","direction":"low","weight":1},{"metric":"accruals","direction":"low","weight":1},{"metric":"share_count_change","direction":"low","weight":1}]"""),
        ("screens.S1.min_inputs", "5"),
        ("screens.S1.state", "\"live\""),
        ("screens.S1.slots", "8"),
        // S2 Trend. Five ranked inputs plus base_breakout_flag as a bonus outside the mean [D-114].
        ("screens.S2.metrics", """[{"metric":"rs_21d_63d_change","direction":"high","weight":1},{"metric":"dist_200dma","direction":"high","weight":1},{"metric":"adx14","direction":"high","weight":1},{"metric":"ma50_200_slope","direction":"high","weight":1},{"metric":"dist_52w_high_20d_change","direction":"high","weight":1},{"metric":"base_breakout_flag","kind":"bonus","points":10}]"""),
        ("screens.S2.min_inputs", "4"),
        ("screens.S2.state", "\"live\""),
        ("screens.S2.slots", "8"),
        // S3 Sentiment inflection.
        ("screens.S3.metrics", """[{"metric":"article_count_z_own_90d","direction":"high","weight":1},{"metric":"sentiment_delta_7v30","direction":"high","weight":1},{"metric":"sentiment_7d_level","direction":"high","weight":1}]"""),
        ("screens.S3.min_inputs", "3"),
        ("screens.S3.state", "\"live\""),
        ("screens.S3.slots", "8"),
        // S4 Flow. Two inputs: inst_ownership_change is not a ranking input [D-118].
        ("screens.S4.metrics", """[{"metric":"insider_net_90d_usd","direction":"high","weight":1},{"metric":"distinct_buyer_count","direction":"high","weight":1}]"""),
        ("screens.S4.min_inputs", "2"),
        ("screens.S4.state", "\"live\""),
        ("screens.S4.slots", "8"),
        // S5 Mean reversion. Ranks on distance below the 200-day average [D-120].
        ("screens.S5.metrics", """[{"metric":"dist_200dma","direction":"low","weight":1}]"""),
        ("screens.S5.min_inputs", "1"),
        ("screens.S5.state", "\"live\""),
        ("screens.S5.slots", "8"),

        // ------------------------------------------------ the shadow family ---
        //
        // Three shadows, registered at Q.7 and before 4.14 [D-129]. C13 scores them
        // like any other screen and C14 writes their attribution rows and no
        // candidate_set row, which is D-84 and D-85 and needs no code here: the
        // mechanism was built at 4.9 and 4.10 against a fixture screen and this is
        // the first registration that means anything.
        //
        // **They are registered before 4.14 and not at sign-off, and the ordering is
        // the whole reason.** attribution's key is (ticker, date) with score_per_screen
        // one jsonb object across screens, so a screen added after 4.14 could only gain
        // a backfilled row by updating a frozen one, which RUNBOOK.md forbids outright.
        // D-119 put the registration decision at sign-off, which falls after 4.14, and
        // that sequence would have removed the option without anyone choosing to give it
        // up [D-129].
        //
        // **X-PEAD is not here and that is D-90's open fork, not an omission.** Its
        // input has no backfillable history: events.earnings_backward_days is 7 and
        // announced_date is null for earnings, so its backfilled distribution would come
        // from a different population than its live one, which is what D-58 removed
        // short interest for [SCREEN_LIFECYCLE.md section 7.5].
        //
        // Each ranks on one valuation_daily column that C09 already writes and C11
        // already percentiles, so the whole cost is these rows and their share of
        // section 9.1's storage.
        //
        // X-NSI, an extraction from S1. Net share issuance is a management action rather
        // than an accounting outcome, and as one of seven S1 inputs it can be outvoted by
        // six valuation and quality measures on exactly the names where dilution is the
        // whole story [section 7.2]. Direction low: a company retiring shares is the good
        // case, which is why S1's cell names the input share_count_change_inv [D-113].
        ("screens.X-NSI.metrics", """[{"metric":"share_count_change","direction":"low","weight":1}]"""),
        ("screens.X-NSI.min_inputs", "1"),
        ("screens.X-NSI.state", "\"shadow\""),
        ("screens.X-NSI.slots", "12"),

        // X-ACC, an extraction from S1. Accruals measure the divergence between
        // accounting earnings and cash, and S1's own rubric names accruals as its
        // characteristic failure while six other inputs can outvote it [section 7.2].
        // Direction low, as S1's accruals_inv names.
        ("screens.X-ACC.metrics", """[{"metric":"accruals","direction":"low","weight":1}]"""),
        ("screens.X-ACC.min_inputs", "1"),
        ("screens.X-ACC.state", "\"shadow\""),
        ("screens.X-ACC.slots", "12"),

        // X-FM, an addition. Fundamental momentum is the trajectory of the business
        // rather than its level, which is D-11's principle applied to revenue rather than
        // to returns. It drops gross_margin_4q_change deliberately: that column is an S1
        // ranking input and including it would make one screen an extraction and an
        // addition at once, which is two statistical treatments on one screen
        // [section 7.3].
        ("screens.X-FM.metrics", """[{"metric":"revenue_growth_4q_trend","direction":"high","weight":1}]"""),
        ("screens.X-FM.min_inputs", "1"),
        ("screens.X-FM.state", "\"shadow\""),
        ("screens.X-FM.slots", "12"),

        // The slot count is twelve because that is tuner.slot_cap, which is the depth
        // section 2.1 records a shadow at: it holds no slots, so what is recorded is what
        // it would have surfaced at the largest count a promotion could ever give it. C14
        // reads the cap directly for a shadow and never this key, and Validated() applies
        // only to live screens, so the value is what a promotion would start from rather
        // than something the allocator acts on today.

        // S5's two composites, held as metric lists of S5's own. The quality list is
        // the same content as S1's, copied, and the copy is deliberate: removing it is
        // what INVARIANT 2 forbids. What holds it is the per-screen facade, which
        // throws when asked for another screen's key [D-120].
        ("screens.S5.quality_metrics", """[{"metric":"fcf_yield","direction":"high","weight":1},{"metric":"ev_ebit_vs_own_5y","direction":"low","weight":1},{"metric":"roic_4q_change","direction":"high","weight":1},{"metric":"gross_margin_4q_change","direction":"high","weight":1},{"metric":"net_debt_ebitda","direction":"low","weight":1},{"metric":"accruals","direction":"low","weight":1},{"metric":"share_count_change","direction":"low","weight":1}]"""),
        ("screens.S5.technical_metrics", """[{"metric":"dist_200dma","direction":"high","weight":1},{"metric":"dist_52w_high","direction":"high","weight":1},{"metric":"rs_change_63d","direction":"high","weight":1}]"""),
        ("screens.S5.quality_min_inputs", "5"),
        ("screens.S5.technical_min_inputs", "3"),
        ("screens.S5.quality_quintile_min", "80"),
        ("screens.S5.technical_quintile_max", "20"),

        // S5's three stabilisation thresholds, in the older top-level namespace where
        // CONFIG_REFERENCE.md has documented them since the first corpus [D-13, D-14].
        // **Documented and seeded by nothing until 4.7**, which is the third time this
        // has been found: the same held for the three shared screens.* keys at 4.5.
        // Kept at their documented names rather than moved under screens.S5, a rename
        // being an authored change to a spec document for no gain.
        //
        // news_gate_min_articles is the fail-open threshold and is the one rule here
        // that must not be tidied into a fail-closed one. Below three articles in seven
        // days both news conditions are treated as satisfied, because thinly covered
        // names are what this screen's small slots exist to find [ARCHITECTURE.html
        // section 05].
        ("s5.stabilisation_z_max", "1.0"),
        ("s5.sentiment_delta_min", "0"),
        ("s5.news_gate_min_articles", "3"),

        // Gates. Every threshold is a key and no literal sits at a call site
        // [D-117, CLAUDE.md section 8]. All four values are unconstrained by anything
        // in the corpus and CONFIG_REFERENCE.md records that; only gap_pct fires over
        // the backfill window, the other three reasons being structurally unevaluable
        // until phase 7. Halt and already-held carry no key, being conditions.
        ("gates.gap_pct", "8"),
        ("gates.earnings_blackout_days_before", "5"),
        ("gates.earnings_blackout_days_after", "2"),
        ("gates.cooldown_days", "30"),

        // The tuner's slot range, which C14 reads before C22 exists. D-43 gives the
        // tuner a floor of four and a cap of twelve and D-89's proportion holds its
        // megacap bound and its small-cap floor over exactly that range, so a screen's
        // slot count is validated against these two rather than clamped [D-43, D-116].
        //
        // **Documented in CONFIG_REFERENCE.md since the first corpus and seeded by
        // nothing until 4.9**, which is the fourth time this gap has been found and the
        // fourth time it was found by a resolve failing rather than by a count. The
        // other two tuner keys stay unseeded: nothing reads them before phase 8.
        ("tuner.slot_floor", "4"),
        ("tuner.slot_cap", "12"),

        // C28's two bounds, which are the two diversity guarantees stated as numbers.
        // megacap_share_max is D-7's third and distinct_tickers_60d_min is the phase's
        // own done-when. **Documented and unseeded until 4.11**, the fifth instance and
        // the fifth found by a resolve failing. The other three monitor and cost keys
        // stay unseeded: nothing reads them before phase 6.
        ("monitor.megacap_share_max", "0.333"),
        ("monitor.distinct_tickers_60d_min", "250"),

        // The digest chain's decided keys [5.3, first commit]. Every value here is the
        // one `CONFIG_REFERENCE.md` already documents or one an authored decision
        // states, so nothing is chosen at this call site.
        //
        // **Two keys this namespace is documented with were deliberately absent from
        // the first commit and that was its whole shape.** `digest.max_articles` and
        // `digest.max_tokens` were never seeded: 5.1 measured the median article at
        // 1,248 real tokens against `ARCHITECTURE.html` section 07's five to eight
        // hundred, which made the article cap a decision rather than a documented
        // value, and D-143 was then drafted and unauthored. Config is append-only, so
        // a provisional value is a config version and a history that must be segmented
        // rather than a placeholder [CLAUDE.md section 8, section 12]. **D-143 is now
        // authored and neither name is ever seeded**: the two below replace them, and
        // `ChainSeedTests` asserts all four names in the state they now stand in.
        //
        // `digest.chain` is seeded at its documented value and its consumer is not
        // bound here. D-136 gives the chain's order to `local_model_config.provider_order`
        // filtered on `enabled`, so what this key is for is a question 5.7 answers;
        // seeding a documented key at its documented value chooses nothing, and
        // `CONFIG_REFERENCE.md`'s Consumer column stays honest about it [5.3 finding].
        ("digest.chain", "[\"local\",\"haiku\"]"),
        ("digest.rotation_count", "2"),
        ("digest.lookback_days", "7"),
        ("digest.health_timeout_ms", "5000"),

        // **A second bound on a second question** [5.13]. `digest.health_timeout_ms`
        // asks whether the link is ready now and is deliberately short.
        // `digest.warm_timeout_ms` bounds a probe whose job is to make the model
        // resident: LM Studio loads on request, so a probe generous enough to cover a
        // load causes one. 120,000 against a cold load measured at 41 to 52 seconds,
        // which is headroom rather than a measurement of its own. Widening the health
        // timeout instead would hide a cold model behind the check that exists to find
        // one, which is why these are two keys and not one.
        ("digest.warm_timeout_ms", "120000"),
        ("digest.readiness_check_et", "\"15:30\""),
        ("digest.secondary_model_id", "\"claude-haiku-4-5\""),

        // D-143's two, which are one retired key split into the two quantities it was
        // standing for. `digest.max_input_tokens` caps what is sent and is measured
        // rather than derived: it admits three median articles with room, one at p95,
        // and it bounds a full secondary year at $47.63 against section 07's $18,
        // which a count cap could not promise at any number.
        // `digest.max_output_tokens` is 150 and is the digest's own length, which is
        // what section 07 and `CONFIG_REFERENCE.md` have always meant by that number.
        // One name for both is what let them be conflated at 5.1, where the API
        // parameter capped the completion and the design meant the digest.
        ("digest.max_input_tokens", "6000"),
        ("digest.max_output_tokens", "150"),

        // D-60's no-digest disqualifier, documented since the first corpus and seeded
        // by nothing until now, which `CONFIG_REFERENCE.md` says in terms: "the fourth
        // is the rubric's and is not a screen threshold, so it stays unseeded until
        // phase 5". Its consumer is the rubric prefix and is phase 6's to verify. It is
        // seeded here rather than there because the fact it gates on, whether a digest
        // was available, is this phase's to produce [D-60, D-134].
        ("s5.no_digest_disqualifier_min_articles_90d", "12"),
    ];

    /// <summary>
    /// The digest chain's rows, which are not config keys and are seeded here anyway
    /// [D-136].
    ///
    /// **`local_model_config` is a table whose declared writer is the UI** [D-51,
    /// `SCHEMA.md`], and the UI is phase 9. So until then the two rows arrive through
    /// the seeder alongside the config keys, which is what `seed.ps1`'s own header
    /// anticipates: the portfolio registry from D-36 and the digest provider chain from
    /// D-25 are still to come, in the phases that build them.
    ///
    /// **This adds no writer to the registry.** `SCHEMA.md` already carries the
    /// precedent that a table's writer may be configuration rather than a stage, which
    /// is what `portfolio` is. A seeder is not a component and declares no write set.
    ///
    /// **The local endpoint is 5.1's measurement rather than a default.** The machine
    /// runs an OpenAI-compatible server on `localhost:11434`; `ARCHITECTURE.html` §07
    /// names LM Studio, which listens on 1234, and there is nothing there. That
    /// discrepancy is reported in `PROGRESS.md` and is not this row's to settle: the
    /// endpoint is a row precisely so the server can differ.
    ///
    /// **The secondary's endpoint is the vendor's base and the client does not read
    /// it.** The SDK knows its own address. The column is NOT NULL and one row per link
    /// is the declared grain, so the honest value is the address the link actually
    /// reaches rather than an empty string standing for "not applicable".
    ///
    /// **`request_options` is D-144's and is null for the secondary.** The local link
    /// carries `reasoning_effort` at `none`, measured at 5.1: without it that server
    /// returns HTTP 200, a normal usage block and zero characters of content, so the
    /// chain would fall through to the paid link every night with the local server up,
    /// answering in under two seconds, and reporting healthy. The secondary's null is
    /// a link that needs no provider-specific parameter rather than one whose options
    /// are unknown, and it is asserted rather than left to the column's default.
    /// </summary>
    public static IReadOnlyList<(int Order, string Endpoint, string? RequestOptions)> ChainLinks { get; } =
    [
        (1, "http://localhost:11434/v1", "{\"reasoning_effort\": \"none\"}"),
        (2, "https://api.anthropic.com", null),
    ];

    private readonly string _connectionString;

    public ConfigSeeder(string connectionString) => _connectionString = connectionString;

    /// <summary>
    /// Inserts version 1 of every key. A second run changes nothing and says so,
    /// which is the property migrate.ps1 is already tested for and the same reason:
    /// "idempotent" stays true until it quietly does not [A9].
    /// </summary>
    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        // ON CONFLICT DO NOTHING rather than an upsert. A real change arrives as
        // version + 1 through the ordinary append-only path, so a seeder that
        // overwrote version 1 would rewrite history rather than extend it.
        const string sql = """
            INSERT INTO config_rows (key, version, value, set_at, set_by)
            VALUES (@key, 1, @value::jsonb, @set_at, 'seed.ps1')
            ON CONFLICT (key, version) DO NOTHING;
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var inserted = 0;
        foreach (var (key, value) in Keys)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("key", key);
            cmd.Parameters.AddWithValue("value", value);
            cmd.Parameters.AddWithValue("set_at", SeedInstant);
            inserted += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return inserted;
    }

    /// <summary>
    /// The chain's two rows [D-136]. Separate from <see cref="SeedAsync"/> and
    /// reported separately, because they are not config keys: they carry no version,
    /// resolve as of nothing, and a count of them mixed into the key count would make
    /// both numbers say less than either does alone.
    ///
    /// `ON CONFLICT DO NOTHING` on `provider_order`, so a second run changes nothing,
    /// and so a row the UI has since edited is left exactly as the operator left it
    /// [D-51].
    /// </summary>
    public async Task<int> SeedChainAsync(CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO local_model_config (provider_order, endpoint, enabled, request_options)
            VALUES (@order, @endpoint, TRUE, @options::jsonb)
            ON CONFLICT (provider_order) DO NOTHING;
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var inserted = 0;
        foreach (var (order, endpoint, options) in ChainLinks)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("order", order);
            cmd.Parameters.AddWithValue("endpoint", endpoint);

            // Null rather than an empty object [D-144]. A link that needs no
            // provider-specific parameter and a link whose parameters are an empty
            // request shape are different facts, and 5.5's unhealthy assertion is
            // written against the second.
            cmd.Parameters.AddWithValue("options", (object?)options ?? DBNull.Value);
            inserted += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return inserted;
    }

    public static string Describe(int inserted, int total)
        => inserted == 0
            ? string.Create(CultureInfo.InvariantCulture, $"  nothing to seed, {total} keys already present")
            : string.Create(CultureInfo.InvariantCulture, $"  {inserted} of {total} keys inserted at version 1");

    /// <summary>The chain's own line, kept apart from the key count for the reason <see cref="SeedChainAsync"/> gives.</summary>
    public static string DescribeChain(int inserted, int total)
        => inserted == 0
            ? string.Create(CultureInfo.InvariantCulture, $"  nothing to seed, {total} chain link(s) already present")
            : string.Create(CultureInfo.InvariantCulture, $"  {inserted} of {total} chain link(s) inserted");
}
