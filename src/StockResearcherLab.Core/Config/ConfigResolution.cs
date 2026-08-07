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
}
