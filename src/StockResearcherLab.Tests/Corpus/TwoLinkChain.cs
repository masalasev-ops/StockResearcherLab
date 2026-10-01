using StockResearcherLab.Core.Config;
using StockResearcherLab.Data;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// The two-link chain the digest tests exercise, set up by the class that needs it rather
/// than inherited from the seed [5.5.11].
///
/// **The seed is one link from 5.5.11.** `digest.chain` is seeded at `["local"]` and the
/// secondary's `local_model_config` row seeded disabled, the operator's standing direction
/// of 2026-08-25. A class testing the fall-through, the rotation or the gate needs both
/// links, and it said so in a comment while taking them from the seed: "Both links,
/// always, because `local_model_config` holds two enabled rows". Each such class now asks
/// for that state here, and `ChainSeedTests` resets the table before asserting the seed's
/// own shape, so the order classes run in decides nothing.
/// </summary>
public static class TwoLinkChain
{
    public const string Names = "[\"local\",\"haiku\"]";

    /// <summary>Both rows present and both enabled.</summary>
    public static async Task EnableAsync(CancellationToken ct)
    {
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedChainAsync(ct).ConfigureAwait(false);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "UPDATE local_model_config SET enabled = TRUE WHERE NOT enabled;", conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The store's config with <c>digest.chain</c> read as both links, every other key
    /// passed through, so a class testing a two-link chain against the real store writes
    /// no config row to get one.
    /// </summary>
    public sealed class Config(IConfigStore inner) : IConfigStore
    {
        public async Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => string.Equals(key, "digest.chain", StringComparison.Ordinal)
                ? new ConfigRow(key, 1, Names, new DateOnly(2020, 1, 1))
                : await inner.ResolveAsync(key, asOf, ct).ConfigureAwait(false);

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => string.Equals(key, "digest.chain", StringComparison.Ordinal)
                ? new ConfigRow(key, 1, Names, new DateOnly(2020, 1, 1))
                : await inner.RequireAsync(key, asOf, ct).ConfigureAwait(false);

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => inner.ResolveVersionAsync(asOf, ct);

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => inner.RequireVersionAsync(asOf, ct);
    }
}