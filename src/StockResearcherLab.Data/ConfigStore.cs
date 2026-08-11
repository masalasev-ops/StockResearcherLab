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
    /// Thirty keys. The count is asserted rather than left to be miscounted: it
    /// was recorded as nine, corrected to eleven at A5, twelve at A10, fourteen at A14, fifteen at 1.4,
    /// seventeen at 1.6, nineteen at 1.7 and twenty-two at 1.8, and every correction was a key that existed with nothing seeding it.
    /// Thirty at 2.4, which is phase 2's seven plus percentile.cell_min_members:
    /// that one had been in CONFIG_REFERENCE.md since the corpus was written with
    /// nothing seeding it, which is the same defect as every correction above.
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

    public static string Describe(int inserted, int total)
        => inserted == 0
            ? string.Create(CultureInfo.InvariantCulture, $"  nothing to seed, {total} keys already present")
            : string.Create(CultureInfo.InvariantCulture, $"  {inserted} of {total} keys inserted at version 1");
}
