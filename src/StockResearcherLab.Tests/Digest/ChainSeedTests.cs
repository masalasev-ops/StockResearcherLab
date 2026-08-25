using System.Text.Json.Nodes;
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
    /// disqualifier, all present at the value the corpus already documents, and
    /// D-143's two, which are the only values in this namespace that a decision
    /// chose rather than a document already carrying.
    /// </summary>
    [Theory]
    [InlineData("digest.chain", "[\"local\",\"haiku\"]")]
    [InlineData("digest.rotation_count", "2")]
    [InlineData("digest.lookback_days", "7")]
    [InlineData("digest.health_timeout_ms", "5000")]
    [InlineData("digest.readiness_check_et", "\"15:30\"")]
    [InlineData("digest.secondary_model_id", "\"claude-haiku-4-5\"")]
    [InlineData("digest.max_input_tokens", "6000")]
    [InlineData("digest.max_output_tokens", "150")]
    [InlineData("s5.no_digest_disqualifier_min_articles_90d", "12")]
    public void TheDecidedKeysAreSeededAtTheirDocumentedValues(string key, string value)
    {
        var seeded = ConfigSeeder.Keys.Where(k => string.Equals(k.Key, key, StringComparison.Ordinal)).ToList();

        Assert.Single(seeded);
        Assert.Equal(value, seeded[0].Value);
    }

    /// <summary>
    /// **The two names D-143 retires are never seeded, and this outlives the
    /// checkpoint that made it true.**
    ///
    /// `digest.max_articles` and `digest.max_tokens` were in `CONFIG_REFERENCE.md` and
    /// are what D-143 supersedes: 5.1 measured the median article at 1,248 real tokens
    /// against `ARCHITECTURE.html` §07's five to eight hundred, so the article cap was
    /// a decision rather than a documented value, and one name covered both the input
    /// and the output length. Neither ever reached the store, so there is no config
    /// version carrying either and no history to segment on their account.
    ///
    /// **The test stays after the second commit rather than being deleted with the
    /// waiting it recorded.** What it now asserts is not "not yet" but "not ever":
    /// these are the two names a later session reading §07, `WORKED_EXAMPLE.md` or
    /// D-133's struck paragraph would reach for, all three of which still contain
    /// them, and seeding one would put a fifth digest key in a namespace with four.
    /// The pair below is asserted present in the same run, so the two assertions
    /// together say which four names moved rather than that four did.
    /// </summary>
    [Theory]
    [InlineData("digest.max_articles")]
    [InlineData("digest.max_tokens")]
    public void TheNamesD143RetiresAreNeverSeeded(string key)
    {
        Assert.DoesNotContain(ConfigSeeder.Keys, k => string.Equals(k.Key, key, StringComparison.Ordinal));
    }

    /// <summary>
    /// The pair D-143 introduces, asserted present by name for the same reason the
    /// pair above is asserted absent by name: `Keys.Count` moving from 102 to 104
    /// says two keys arrived and nothing about which two.
    /// </summary>
    [Theory]
    [InlineData("digest.max_input_tokens")]
    [InlineData("digest.max_output_tokens")]
    public void ThePairD143IntroducesIsSeeded(string key)
    {
        Assert.Single(ConfigSeeder.Keys, k => string.Equals(k.Key, key, StringComparison.Ordinal));
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
            "SELECT provider_order, endpoint, enabled, last_health_check, last_loaded_model, " +
            "request_options FROM local_model_config ORDER BY provider_order;", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

        var rows = new List<(int Order, string Endpoint, bool Enabled, bool HealthNull, bool ModelNull, string? Options)>();
        while (await r.ReadAsync(ct).ConfigureAwait(true))
        {
            rows.Add((
                r.GetInt32(0), r.GetString(1), r.GetBoolean(2),
                r.IsDBNull(3), r.IsDBNull(4),
                r.IsDBNull(5) ? null : r.GetString(5)));
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(ConfigSeeder.ChainLinks.Select(l => l.Order), rows.Select(x => x.Order));
        Assert.Equal(ConfigSeeder.ChainLinks.Select(l => l.Endpoint), rows.Select(x => x.Endpoint));
        Assert.All(rows, x => Assert.True(x.Enabled));

        // **The local link carries D-144's request options and the secondary's column
        // is null, asserted rather than left to the column's default.** A default of
        // `'{}'::jsonb` would make "this link needs no provider-specific parameter"
        // and "this link's request shape is empty" the same row, and 5.5's unhealthy
        // assertion is written against the second [`CLAUDE.md` §6].
        var local = JsonNode.Parse(rows[0].Options!)!.AsObject();
        Assert.Equal("none", (string?)local["reasoning_effort"]);
        Assert.Null(rows[1].Options);

        // **Both stay null and gain no writer in this phase** [D-136]. C32's Writes
        // cell says nothing, via C27, and a health check is emitted through the run log
        // exactly as C07's abort is. Phase 9 inherits whether the UI fills them, and
        // asserting the nulls here is what makes that a decision rather than a drift.
        Assert.All(rows, x => Assert.True(x.HealthNull));
        Assert.All(rows, x => Assert.True(x.ModelNull));
    }

    /// <summary>
    /// **`0023`'s backfill and `ChainLinks` carry the same literal, and the duplication
    /// is deliberate.**
    ///
    /// The seeder inserts `ON CONFLICT DO NOTHING` so that a row the operator has
    /// edited is left as they left it [D-51, D-136], which means it can never reach a
    /// row that already exists. On a database seeded before `0023` the local link's
    /// `request_options` would therefore stay null for ever, so the migration carries
    /// the value for the row that predates the column, guarded on the column still
    /// being null. That is `0013`'s shape and it puts the same JSON in two places.
    ///
    /// A migration whose text is built from code at migration time is a migration whose
    /// recorded hash means nothing, so the copy stays and this test is what stops it
    /// drifting. Exactly `0022`'s provider vocabulary a second time.
    /// </summary>
    [Fact]
    public void TheMigrationsBackfillCarriesTheSameOptionsAsTheSeeder()
    {
        var local = ConfigSeeder.ChainLinks.Single(l => l.Order == 1).RequestOptions;
        Assert.NotNull(local);

        var sql = Migrator.EmbeddedMigrations()
            .Single(m => string.Equals(m.Filename, "0023_request_options.sql", StringComparison.Ordinal))
            .Sql;

        Assert.Contains("SET request_options = '" + local + "'::jsonb", sql, StringComparison.Ordinal);

        // The secondary is backfilled by nothing, its null being the answer rather
        // than a gap. Asserted so that "and only provider_order 1" is a property of
        // this migration rather than a sentence in its header.
        Assert.Null(ConfigSeeder.ChainLinks.Single(l => l.Order == 2).RequestOptions);
        Assert.Contains("WHERE provider_order = 1", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// **`0023`'s backfill fills the local link's null and leaves the secondary's
    /// alone, and a second run of it changes nothing.**
    ///
    /// This is the statement whose absence caused the failure it exists for, so it is
    /// asserted by running it rather than by reading it. It is also the statement no
    /// environment here exercises: `ci.ps1` migrates from an empty server, so the
    /// `UPDATE` runs against zero rows there, and the suite's database is created after
    /// `0023` and gets its value from the insert. The only place it ever fires is a
    /// database seeded before this migration, which is the operator's.
    ///
    /// The statement is read out of the embedded migration rather than retyped, so the
    /// text under test is the text that runs. Nulling the column first is what puts the
    /// row into the state a pre-`0023` database is in.
    /// </summary>
    [Fact]
    public async Task TheMigrationsBackfillFillsOnlyALinkThatHasNone()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedChainAsync(ct).ConfigureAwait(true);

        var sql = Migrator.EmbeddedMigrations()
            .Single(m => string.Equals(m.Filename, "0023_request_options.sql", StringComparison.Ordinal))
            .Sql;

        // Comments are stripped before the statements are split, not after. Two of this
        // migration's comment lines contain a semicolon, so splitting first cuts a
        // comment in half and leaves its tail standing at the front of the next
        // statement. That is not a hypothetical: it is what this test did on its first
        // run, and prose in this corpus is full of semicolons.
        var backfill = string.Join(
                '\n',
                sql.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(part => part.StartsWith("UPDATE", StringComparison.Ordinal));

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        // The state a database seeded before 0023 is in: the column exists and no row
        // has ever carried a value, which is what ON CONFLICT DO NOTHING leaves behind.
        await using (var clear = new NpgsqlCommand(
            "UPDATE local_model_config SET request_options = NULL;", conn))
        {
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
        }

        await using (var run = new NpgsqlCommand(backfill, conn))
        {
            Assert.Equal(1, await run.ExecuteNonQueryAsync(ct).ConfigureAwait(true));
        }

        await using (var again = new NpgsqlCommand(backfill, conn))
        {
            // Guarded on the column still being null, so a re-run is a no-op and a row
            // the operator has since written is not disturbed [0013's shape].
            Assert.Equal(0, await again.ExecuteNonQueryAsync(ct).ConfigureAwait(true));
        }

        await using var read = new NpgsqlCommand(
            "SELECT provider_order, request_options FROM local_model_config ORDER BY provider_order;", conn);
        await using var r = await read.ExecuteReaderAsync(ct).ConfigureAwait(true);

        var options = new List<string?>();
        while (await r.ReadAsync(ct).ConfigureAwait(true))
        {
            options.Add(await r.IsDBNullAsync(1, ct).ConfigureAwait(true) ? null : r.GetString(1));
        }

        Assert.Equal(2, options.Count);
        Assert.Equal("none", (string?)JsonNode.Parse(options[0]!)!["reasoning_effort"]);
        Assert.Null(options[1]);
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
