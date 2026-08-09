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

    private async Task<IReadOnlyList<ConfigRow>> ReadAsync(string key, CancellationToken ct)
    {
        // set_at is a timestamptz and every question asked of config is asked
        // about a trading date, so it is reduced to one here. US Eastern rather
        // than UTC or the server's zone, because all market semantics in this
        // system are US Eastern and a trading date is the label the exchange gave
        // a session [CLAUDE.md section 6].
        const string sql = """
            SELECT key, version, value::text, (set_at AT TIME ZONE 'America/New_York')::date
            FROM config_rows
            WHERE key = @key
            ORDER BY version;
            """;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key", key);

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
    /// Seventeen keys. The count is asserted rather than left to be miscounted: it
    /// was recorded as nine, corrected to eleven at A5, twelve at A10, fourteen at A14, fifteen at 1.4
    /// and seventeen at 1.6, and every correction was a key that existed with nothing seeding it.
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
