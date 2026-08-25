using Npgsql;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Data;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.3, first commit. The decided keys and the chain's two rows.
///
/// **The assertions that matter here are about what is absent.** Seeding a namespace
/// is ordinary; seeding six of eight keys because two of them are a decision nobody
/// has taken is the thing that gets tidied away by the next session, who sees a gap
/// and fills it. Config is append-only [`CLAUDE.md` §8], so a provisional value is a
/// config version and a history that must be segmented rather than a placeholder that
/// can be corrected.
/// </summary>
[Collection("database")]
public sealed class ChainSeedTests
{
    /// <summary>
    /// The six `digest.*` keys D-25, D-27, D-133 and D-140 decide, plus D-60's
    /// disqualifier, all present at the value the corpus already documents.
    /// </summary>
    [Theory]
    [InlineData("digest.chain", "[\"local\",\"haiku\"]")]
    [InlineData("digest.rotation_count", "2")]
    [InlineData("digest.lookback_days", "7")]
    [InlineData("digest.health_timeout_ms", "5000")]
    [InlineData("digest.readiness_check_et", "\"15:30\"")]
    [InlineData("digest.secondary_model_id", "\"claude-haiku-4-5\"")]
    [InlineData("s5.no_digest_disqualifier_min_articles_90d", "12")]
    public void TheDecidedKeysAreSeededAtTheirDocumentedValues(string key, string value)
    {
        var seeded = ConfigSeeder.Keys.Where(k => string.Equals(k.Key, key, StringComparison.Ordinal)).ToList();

        Assert.Single(seeded);
        Assert.Equal(value, seeded[0].Value);
    }

    /// <summary>
    /// **The two keys D-143 introduces are not seeded, and the two it retires are not
    /// seeded either.**
    ///
    /// `digest.max_articles` and `digest.max_tokens` are in `CONFIG_REFERENCE.md` and
    /// are the two D-143 supersedes: 5.1 measured the median article at 1,248 real
    /// tokens against `ARCHITECTURE.html` §07's five to eight hundred, so the article
    /// cap is a decision rather than a documented value, and one name covered both the
    /// input and the output length. `digest.max_input_tokens` and
    /// `digest.max_output_tokens` replace them and arrive in this checkpoint's second
    /// commit once D-143 is authored.
    ///
    /// Asserted rather than left to the count, because a count that moved would not
    /// say which four keys it was about.
    /// </summary>
    [Theory]
    [InlineData("digest.max_articles")]
    [InlineData("digest.max_tokens")]
    [InlineData("digest.max_input_tokens")]
    [InlineData("digest.max_output_tokens")]
    public void TheKeysD143DecidesAreNotSeededYet(string key)
    {
        Assert.DoesNotContain(ConfigSeeder.Keys, k => string.Equals(k.Key, key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Every seeded `digest.*` key resolves as of a date inside the backfill window,
    /// which is the property `SeedInstant` exists for and the one a wall-clock stamp
    /// would break silently [INVARIANT 13, D-43].
    /// </summary>
    [Fact]
    public async Task EverySeededDigestKeyResolvesAsOfADateInsideTheWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new ConfigStore(TestDatabase.ConnectionString);
        var asOf = new DateOnly(2021, 1, 4);

        foreach (var (key, _) in ConfigSeeder.Keys.Where(k => k.Key.StartsWith("digest.", StringComparison.Ordinal)))
        {
            var value = await store.ResolveAsync(key, asOf, ct).ConfigureAwait(true);
            Assert.True(value is not null, $"'{key}' did not resolve as of {asOf:yyyy-MM-dd}.");
        }
    }

    /// <summary>
    /// The chain's two rows exist, in order, and re-seeding changes nothing.
    ///
    /// **Idempotence is asserted rather than assumed** for the reason `SeedAsync`
    /// already states about the keys: a row the operator has since edited through the
    /// one permitted write endpoint [D-51] must be left exactly as they left it, and
    /// `ON CONFLICT DO NOTHING` is what makes a second seed harmless rather than a
    /// silent revert.
    /// </summary>
    [Fact]
    public async Task TheChainHasTwoRowsAndReSeedingChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeder = new ConfigSeeder(TestDatabase.ConnectionString);

        await seeder.SeedChainAsync(ct).ConfigureAwait(true);
        var second = await seeder.SeedChainAsync(ct).ConfigureAwait(true);

        Assert.Equal(0, second);

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT provider_order, endpoint, enabled, last_health_check, last_loaded_model " +
            "FROM local_model_config ORDER BY provider_order;", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

        var rows = new List<(int Order, string Endpoint, bool Enabled, bool HealthNull, bool ModelNull)>();
        while (await r.ReadAsync(ct).ConfigureAwait(true))
        {
            rows.Add((r.GetInt32(0), r.GetString(1), r.GetBoolean(2), r.IsDBNull(3), r.IsDBNull(4)));
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(ConfigSeeder.ChainLinks.Select(l => l.Order), rows.Select(x => x.Order));
        Assert.Equal(ConfigSeeder.ChainLinks.Select(l => l.Endpoint), rows.Select(x => x.Endpoint));
        Assert.All(rows, x => Assert.True(x.Enabled));

        // **Both stay null and gain no writer in this phase** [D-136]. C32's Writes
        // cell says nothing, via C27, and a health check is emitted through the run log
        // exactly as C07's abort is. Phase 9 inherits whether the UI fills them, and
        // asserting the nulls here is what makes that a decision rather than a drift.
        Assert.All(rows, x => Assert.True(x.HealthNull));
        Assert.All(rows, x => Assert.True(x.ModelNull));
    }

    /// <summary>
    /// The chain's order and the vocabulary are the same length and are meant to stay
    /// so. A link added to one and not the other is a chain with an address nothing can
    /// name, or a name nothing can reach.
    /// </summary>
    [Fact]
    public void TheChainHasOneRowPerProviderInTheVocabulary()
    {
        Assert.Equal(DigestProviders.All.Count, ConfigSeeder.ChainLinks.Count);
        Assert.Equal(
            Enumerable.Range(1, DigestProviders.All.Count),
            ConfigSeeder.ChainLinks.Select(l => l.Order));
    }
}
