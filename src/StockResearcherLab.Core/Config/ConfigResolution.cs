namespace StockResearcherLab.Core.Config;

/// <summary>
/// One row of <c>config_rows</c>, reduced to what resolution needs.
/// </summary>
/// <param name="Key">The key string exactly as CONFIG_REFERENCE.md gives it.</param>
/// <param name="Version">Append-only. A change inserts version + 1 rather than updating.</param>
/// <param name="Value">The raw JSON value. Parsing belongs to the consumer, not to resolution.</param>
/// <param name="SetOn">
/// The trading date this version came into force, being <c>set_at</c> reduced to a
/// date. A date rather than an instant because every question asked of config is
/// asked about a trading date [CLAUDE.md section 6].
/// </param>
public sealed record ConfigRow(string Key, int Version, string Value, DateOnly SetOn);

/// <summary>
/// As-of config resolution. Config resolves as of the simulated date, never as of
/// now [D-43, INVARIANT 13].
///
/// This is a pure function over rows so that the rule can be tested without a
/// database. The tuner rewrites slot allocations monthly, so any analysis over
/// history that reads current config is answering a different question than the
/// one asked, and that failure produces no error at all.
/// </summary>
public static class ConfigResolution
{
    /// <summary>
    /// The version of <paramref name="key"/> in force on <paramref name="asOf"/>,
    /// being the highest version whose <see cref="ConfigRow.SetOn"/> is at or
    /// before that date. Null when no version had come into force yet.
    ///
    /// **Null rather than a fallback to the newest row.** A resolver that falls
    /// back when the date filter matches nothing hands today's configuration to a
    /// historical date, which is precisely what INVARIANT 13 exists to stop, and
    /// it passes every test that only exercises dates inside the configured range
    /// [A9].
    /// </summary>
    public static ConfigRow? Resolve(IEnumerable<ConfigRow> rows, string key, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(key);

        ConfigRow? best = null;

        foreach (var row in rows)
        {
            if (!string.Equals(row.Key, key, StringComparison.Ordinal))
            {
                continue;
            }

            if (row.SetOn > asOf)
            {
                continue;
            }

            // Highest version wins among those in force. Compared explicitly
            // rather than taken from enumeration order, which is unspecified for
            // most sources and must never decide an output [CLAUDE.md section 6].
            if (best is null || row.Version > best.Version)
            {
                best = row;
            }
        }

        return best;
    }

    /// <summary>
    /// The store-wide config version in force on <paramref name="asOf"/>, being **one
    /// plus the count of rows whose version is greater than one** and whose
    /// <see cref="ConfigRow.SetOn"/> is at or before that date. Null when there are no
    /// rows at all [D-72].
    ///
    /// **This is the stamp `attribution.config_version` carries**, which is the only
    /// thing that lets history be segmented rather than pooled after a screen
    /// definition changes [`CLAUDE.md` §8, INVARIANT 4]. It therefore has to
    /// distinguish configurations, and that is the whole of why it counts.
    ///
    /// **A maximum over per-key versions does not distinguish them** [D-72]. Keys at
    /// 3, 1, 1 give 3; changing the second key gives 3, 2, 1 and still gives 3. Two
    /// different configurations share a stamp from the second change onward, and the
    /// tuner segmenting on it would pool exactly the results it exists to keep apart.
    /// The failure is silent: every row still carries a number and the query still
    /// groups.
    ///
    /// **Revisions are counted and seeds are not, which is the difference between
    /// counting changes and counting rows** [D-72 as amended]. A key's initial seed
    /// carries the seeder's fixed <c>SeedInstant</c> and is backdated by design, so
    /// counting it would make seeding a new key raise the version for every past date
    /// and a backfill re-run would stamp a different version on identical data. A seed
    /// extends the configuration's schema; only a revision changes the configuration
    /// in force. So it begins at 1 however many keys are seeded, and rises by exactly
    /// one per change.
    ///
    /// **Null rather than 1 when nothing is in force, and that cannot be inferred from
    /// the arithmetic.** One plus zero revisions is 1, which is a real version, so the
    /// sum alone cannot tell a seeded-and-never-revised store from an empty one. The
    /// rows in force are counted separately for exactly that reason. A version nothing
    /// was written under is absent rather than 1, and the caller's answer is to fail
    /// the run rather than stamp it [CLAUDE.md section 6].
    ///
    /// **As of the date, not as of now**, by the same rule as every key: stamping a
    /// backfilled row with today's version would say a night ran under configuration
    /// that did not exist yet, and the tuner would segment on it [D-43, INVARIANT 13].
    /// </summary>
    public static int? ResolveVersion(IEnumerable<ConfigRow> rows, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var inForce = 0;
        var revisions = 0;

        foreach (var row in rows)
        {
            if (row.SetOn > asOf)
            {
                continue;
            }

            inForce++;

            if (row.Version > 1)
            {
                revisions++;
            }
        }

        // Counted rather than accumulated into a maximum, so enumeration order cannot
        // reach the result at all [CLAUDE.md section 6].
        return inForce == 0 ? null : revisions + 1;
    }
}

/// <summary>
/// Thrown when a key has no version in force for the date being processed. Named,
/// because the alternative is a call site reaching for a default, and a default at
/// a call site is the magic number CONFIG_REFERENCE.md exists to prevent
/// [CLAUDE.md section 8].
/// </summary>
public sealed class ConfigNotInForceException : InvalidOperationException
{
    public ConfigNotInForceException(string key, DateOnly asOf)
        : base($"Config key '{key}' has no version in force on " +
               $"{asOf.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}. " +
               "Config resolves as of the simulated date and never as of now, so a key seeded " +
               "later than the date being processed is absent rather than current [D-43, " +
               "INVARIANT 13]. Seed it with a set_at at or before the earliest date the system " +
               "will resolve for.")
    {
        Key = key;
        AsOf = asOf;
    }

    public string Key { get; }

    public DateOnly AsOf { get; }
}

/// <summary>
/// Thrown when no config row at all had come into force for the date being
/// processed, so there is no version to stamp a run with.
///
/// Named and separate from <see cref="ConfigNotInForceException"/> because the two
/// say different things: that one names a key nobody seeded, this one says the whole
/// store post-dates the date being run. Both fail rather than defaulting, since a
/// literal version at a call site is the hardcoded constant checkpoint 1.13 removed.
/// </summary>
public sealed class ConfigVersionNotInForceException : InvalidOperationException
{
    public ConfigVersionNotInForceException(DateOnly asOf)
        : base($"No config row was in force on " +
               $"{asOf.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}, " +
               "so there is no config version to run under. Config resolves as of the simulated date " +
               "and never as of now, and a run stamped with a version that did not exist on its own " +
               "date is what makes history unsegmentable later [D-43, INVARIANT 13]. Seed with a " +
               "set_at at or before the earliest date the system will resolve for.")
        => AsOf = asOf;

    public DateOnly AsOf { get; }
}

/// <summary>
/// Reads configuration as of a date. The only route a stage has to a configured
/// value; nothing reads <c>MAX(version)</c> unconditionally.
/// </summary>
public interface IConfigStore
{
    /// <summary>The value in force on <paramref name="asOf"/>, or null when none is.</summary>
    Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="ResolveAsync"/>, but absence is a failed run rather than a
    /// silent default. A stage completes or it fails [CLAUDE.md section 6].
    /// </summary>
    Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default);

    /// <summary>
    /// The store-wide version in force on <paramref name="asOf"/>, being one plus the
    /// count of revisions set at or before it, or null when no row is [D-72]. See
    /// <see cref="ConfigResolution.ResolveVersion"/> for why it counts changes rather
    /// than rows, and why neither is a maximum.
    /// </summary>
    Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="ResolveVersionAsync"/>, but absence fails the run rather than
    /// falling back to a literal.
    /// </summary>
    Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default);
}
