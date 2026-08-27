using Npgsql;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.2. Migration `0022`, asserted against the database rather than read
/// off the file.
///
/// **Everything here is about a column that can hold a value no reader can
/// interpret.** `news_digest.provider` and `model_name` are how a fall-through is
/// visible in the record and how a later shift in results is separated from a change
/// in the evidence [D-29, INVARIANT 7's second boundary]. `headline.content` is what
/// the digest is a digest of. All three were open in a way that fails quietly rather
/// than loudly.
/// </summary>
[Collection("database")]
public sealed class DigestSchemaTests
{
    // ------------------------------------------------- headline.content [D-131] ---

    /// <summary>
    /// The column exists, holds an article, and holds null.
    ///
    /// **Null and empty are asserted to be different**, because that distinction is
    /// what D-134's four outcomes rest on one table over: a row with no body is the
    /// no-article case and a row with an empty body would be a digest over nothing.
    /// A `NOT NULL DEFAULT ''` column, which is the shape that suggests itself, makes
    /// them the same value.
    /// </summary>
    [Fact]
    public async Task HeadlineCarriesAnArticleBodyAndDistinguishesAbsentFromEmpty()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO headline (ticker, date, published_at, title, source, url, content) VALUES " +
            "('AAA.US', DATE '2026-08-12', TIMESTAMPTZ '2026-08-11 13:00+00', 'a title', NULL, 'https://x/1', 'a body'), " +
            "('AAA.US', DATE '2026-08-12', TIMESTAMPTZ '2026-08-10 13:00+00', 'another',  NULL, 'https://x/2', NULL), " +
            "('AAA.US', DATE '2026-08-12', TIMESTAMPTZ '2026-08-09 13:00+00', 'a third',  NULL, 'https://x/3', '');",
            conn, tx))
        {
            Assert.Equal(3, await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(true));
        }

        await using (var read = new NpgsqlCommand(
            "SELECT count(*) FILTER (WHERE content IS NULL), " +
            "       count(*) FILTER (WHERE content = ''), " +
            "       count(*) FILTER (WHERE content IS NOT NULL AND content <> '') " +
            "FROM headline WHERE ticker = 'AAA.US';", conn, tx))
        await using (var r = await read.ExecuteReaderAsync(ct).ConfigureAwait(true))
        {
            Assert.True(await r.ReadAsync(ct).ConfigureAwait(true));
            Assert.Equal(1, r.GetInt64(0));
            Assert.Equal(1, r.GetInt64(1));
            Assert.Equal(1, r.GetInt64(2));
        }

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The digest orders a ticker's articles by `published_at` and takes the most
    /// recent [D-133], and 0001's only index is on the run's date rather than the
    /// article's. This asserts the index `0022` adds is there, by name, because an
    /// absent index is a correct answer computed slowly and nothing else would say so.
    /// </summary>
    [Fact]
    public async Task TheOrderingColumnHasAnIndex()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes " +
            "WHERE tablename = 'headline' AND indexname = 'headline_ticker_published_ix';", conn);

        var definition = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        Assert.NotNull(definition);
        Assert.Contains("published_at DESC", definition, StringComparison.Ordinal);
    }

    // ------------------------------------- news_digest.provider vocabulary [D-134] ---

    /// <summary>
    /// **The vocabulary is closed by the database and not only by the enum** [D-134].
    /// A constant closes what this system writes and leaves the column able to hold a
    /// string no reader can interpret, which is the shape `0018` removed for the gate
    /// reasons, `0021` for the alert types, and `0022` here.
    /// </summary>
    [Fact]
    public async Task AProviderOutsideTheVocabularyFailsTheInsert()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO news_digest (ticker, date, digest_text, provider, model_name, was_rotation) " +
            "VALUES ('AAA.US', DATE '2026-08-12', 'a digest', 'not_a_provider', 'some-model', FALSE);",
            conn, tx);

        var thrown = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteNonQueryAsync(ct));

        Assert.Equal("news_digest_provider_vocabulary", thrown.ConstraintName);

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The constraint and the enum hold the same two names, read out of the catalogue
    /// rather than trusted. The list is duplicated deliberately, a migration built
    /// from a list in code being a migration whose recorded hash changes with a
    /// rebuild [0021's argument unchanged].
    /// </summary>
    [Fact]
    public async Task TheConstraintAndTheEnumHoldTheSameVocabulary()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint " +
            "WHERE conname = 'news_digest_provider_vocabulary';", conn);

        var definition = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        Assert.NotNull(definition);

        foreach (var name in DigestProviders.Names)
        {
            Assert.Contains("'" + name + "'", definition, StringComparison.Ordinal);
        }

        // And nothing else: two quoted strings in the constraint, two in the enum.
        Assert.Equal(DigestProviders.Names.Count, definition.Count(c => c == '\'') / 2);
    }

    /// <summary>Both stored forms, asserted against the literals rather than against the builder.</summary>
    [Fact]
    public void TheStoredFormsAreTheTwoStringsTheColumnHolds()
    {
        Assert.Equal("local", DigestProviders.Name(DigestProvider.Local));
        Assert.Equal("haiku", DigestProviders.Name(DigestProvider.Haiku));
        Assert.Equal(2, DigestProviders.All.Count);
    }

    /// <summary>
    /// Reading back fails closed. A default here would turn an unattributable digest
    /// into an attributed one and the row would read as ordinary.
    /// </summary>
    [Fact]
    public void AStringOutsideTheVocabularyDoesNotParse()
    {
        Assert.Equal(DigestProvider.Local, DigestProviders.Parse("local"));
        Assert.Equal(DigestProvider.Haiku, DigestProviders.Parse("haiku"));

        var thrown = Assert.Throws<InvalidOperationException>(() => DigestProviders.Parse("Local"));
        Assert.Contains("is not a digest provider", thrown.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------ news_digest.model_name [D-134] ------

    /// <summary>
    /// **NOT NULL was already there and it is not enough.** An empty string satisfies
    /// it and records nothing, so a row could carry a provider and no model and read
    /// as a complete attribution. The blank case is asserted alongside the empty one
    /// because `&lt;&gt; ''` passes a single space.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task ABlankModelNameFailsTheInsert(string modelName)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO news_digest (ticker, date, digest_text, provider, model_name, was_rotation) " +
            "VALUES ('AAA.US', DATE '2026-08-12', 'a digest', 'local', @m, FALSE);", conn, tx);
        cmd.Parameters.AddWithValue("m", modelName);

        var thrown = await Assert.ThrowsAsync<PostgresException>(
            () => cmd.ExecuteNonQueryAsync(ct));

        Assert.Equal("news_digest_model_name_not_blank", thrown.ConstraintName);

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// The four outcomes D-134 separates, inserted and read back as four distinct
    /// states. **The assertion that matters is the middle pair**: a null
    /// `digest_text` and the literal `NO MATERIAL NEWS` are different rows, and
    /// collapsing them is what makes S5's no-digest disqualifier fire on thinly
    /// covered small caps [D-60].
    /// </summary>
    [Fact]
    public async Task TheFourOutcomesAreFourDistinctStates()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(true);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO news_digest (ticker, date, digest_text, provider, model_name, was_rotation) VALUES " +
            "('NOARTICLE.US', DATE '2026-08-12', NULL,               'local', 'qwen', FALSE), " +
            "('NOMATERIAL.US', DATE '2026-08-12', 'NO MATERIAL NEWS', 'local', 'qwen', FALSE), " +
            "('PROSE.US',      DATE '2026-08-12', 'Acme reported.',   'haiku', 'claude-haiku-4-5', TRUE);",
            conn, tx))
        {
            Assert.Equal(3, await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(true));
        }

        // The fourth state is the absent row, and it is asserted by its absence.
        await using (var read = new NpgsqlCommand(
            "SELECT count(*) FILTER (WHERE digest_text IS NULL), " +
            "       count(*) FILTER (WHERE digest_text = 'NO MATERIAL NEWS'), " +
            "       count(*) FILTER (WHERE digest_text IS NOT NULL AND digest_text <> 'NO MATERIAL NEWS'), " +
            "       count(*) FILTER (WHERE ticker = 'NEVERACANDIDATE.US') " +
            "FROM news_digest WHERE date = DATE '2026-08-12';", conn, tx))
        await using (var r = await read.ExecuteReaderAsync(ct).ConfigureAwait(true))
        {
            Assert.True(await r.ReadAsync(ct).ConfigureAwait(true));
            Assert.Equal(1, r.GetInt64(0));
            Assert.Equal(1, r.GetInt64(1));
            Assert.Equal(1, r.GetInt64(2));
            Assert.Equal(0, r.GetInt64(3));
        }

        await tx.RollbackAsync(ct).ConfigureAwait(true);
    }
}
