using StockResearcherLab.Api;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Pipeline.Select;
using Xunit;

namespace StockResearcherLab.Tests.Api;

/// <summary>
/// C36's fifth panel: what the digest step read for one name on one date and what came
/// back [D-134, 5.9].
///
/// **The four outcomes are the subject.** D-134 exists because the middle two get
/// collapsed, and a panel that rendered a null `digest_text` and the escape hatch the same
/// way would be the collapse arriving through the viewer instead of through the store. So
/// each of the four is asserted to produce a different answer, from the row alone.
///
/// **The second subject is what the panel declines to show.** Which articles were sent is
/// not recorded, and the failure mode is not an empty panel: it is a page that reconstructs
/// D-143's selection, gets a plausible three of nine, and reads as the record. The
/// assertions here are therefore on the statement as much as on the result.
/// </summary>
public sealed class DigestPanelTests
{
    private static readonly DateOnly Viewed = new(2026, 8, 12);

    private const string Ticker = "AAPL.US";

    /// <summary>
    /// D-134's four states, each from the row alone, each a different answer.
    ///
    /// **The pair that matters is the middle one.** A null `digest_text` is "there was
    /// nothing to read" and D-60's disqualifier bites on it; the escape hatch is a reading
    /// and D-60 does not. They are asserted as different strings rather than as different
    /// booleans, because the panel's answer is the string a reader sees.
    /// </summary>
    [Theory]
    [InlineData(false, null, "no_row")]
    [InlineData(true, null, "no_articles")]
    [InlineData(true, "NO MATERIAL NEWS", "no_material_news")]
    [InlineData(true, "Two filings and a guidance cut.", "prose")]
    public async Task TheFourOutcomesAreFourDifferentAnswers(bool row, string? text, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new Store(digest: row ? new object?[] { text, "local", "qwen3.6:latest", false } : null);

        var panel = await new RecordInspector(data, new Keys()).DigestAsync(Ticker, Viewed, ct)
            .ConfigureAwait(true);

        Assert.Equal(expected, panel.Outcome);

        if (row)
        {
            Assert.NotNull(panel.Digest);
            Assert.Equal(text, panel.Digest.Text);
            Assert.Equal("local", panel.Digest.Provider);
            Assert.Equal("qwen3.6:latest", panel.Digest.ModelName);
        }
        else
        {
            Assert.Null(panel.Digest);
        }
    }

    /// <summary>
    /// The four names are four, and `no_row` is not one of the others by another route.
    /// Asserted over the vocabulary rather than over one call, because an outcome added to
    /// the enum without a name would throw where a reader met it rather than here.
    /// </summary>
    [Fact]
    public void TheOutcomeVocabularyIsFourDistinctNames()
    {
        var names = DigestOutcomes.All.Select(DigestOutcomes.Name).ToList();

        Assert.Equal(4, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// **The string the panel classifies on is the one C33 compares against**, and the two
    /// are one constant rather than two that agree today.
    ///
    /// This test is in the test project because it is the only project that sees both the
    /// Api and the Pipeline, which is `MetricSourceParityTests`' reason and the same shape.
    /// It is cheap and it is what stops the Api's copy drifting if the literal is ever
    /// pushed back down into either side.
    /// </summary>
    [Fact]
    public void TheEscapeHatchThePanelClassifiesOnIsTheOneTheDigesterWrites()
    {
        Assert.Equal(NewsDigester.NoMaterialNews, DigestOutcomes.NoMaterialNewsText);

        Assert.Equal(
            DigestOutcome.NoMaterialNews,
            DigestOutcomes.Classify(true, NewsDigester.NoMaterialNews));

        // Whitespace around the hatch is not the hatch. C33 trims once where the answer
        // arrives, so a stored value carrying it was not treated as the hatch by the
        // writer and must not be by the reader [D-134].
        Assert.Equal(
            DigestOutcome.Prose,
            DigestOutcomes.Classify(true, " " + NewsDigester.NoMaterialNews));
    }

    /// <summary>
    /// **The pool is every stored row and the window is marked rather than applied.**
    ///
    /// Asserted on the statement, because the result cannot show it: a panel that filtered
    /// on `published_at` would return the same rows on a night where every row is inside
    /// the window, which is most nights, and the difference would appear only on the one
    /// re-run under a changed lookback that a reader was trying to understand.
    /// </summary>
    [Fact]
    public async Task ThePoolIsEveryStoredRowAndTheWindowIsMarkedRatherThanApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new Store(digest: null);

        await new RecordInspector(data, new Keys()).DigestAsync(Ticker, Viewed, ct).ConfigureAwait(true);

        var sql = data.Sql.Single(s => s.Contains("FROM headline", StringComparison.Ordinal));

        // The WHERE clause alone, which is the half that could drop a row. The window
        // expression is in the projection and the ordering names the column too, so a
        // slice to the end of the statement would find the string wherever it looked.
        var from = sql.IndexOf("WHERE", StringComparison.Ordinal);
        var to = sql.IndexOf("ORDER BY", StringComparison.Ordinal);
        var where = sql[from..to];

        Assert.DoesNotContain("published_at", where, StringComparison.Ordinal);
        Assert.Contains("published_at IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("content IS NOT NULL", sql, StringComparison.Ordinal);

        // The bound is built in the statement from the viewed date and the lookback, so
        // the page does no date arithmetic of its own [D-109].
        Assert.Contains("(DATE '2026-08-12' - 7)", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row C29 stored outside the window is in the pool and marked, rather than absent.
    /// The in-window answer comes from the statement, so the double supplies it: what is
    /// asserted is that the panel carries it through instead of recomputing one.
    /// </summary>
    [Fact]
    public async Task AnArticleOutsideTheWindowIsShownAndMarkedRatherThanDropped()
    {
        var ct = TestContext.Current.CancellationToken;

        var data = new Store(
            digest: null,
            articles:
            [
                [new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc),
                    "Inside", "Reuters", "u1", "a body", true, true],
                [new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
                    "Outside", "Reuters", "u2", null, false, false],
            ]);

        var panel = await new RecordInspector(data, new Keys()).DigestAsync(Ticker, Viewed, ct)
            .ConfigureAwait(true);

        Assert.Equal(2, panel.Pool.Count);
        Assert.True(panel.Pool[0].InWindow);
        Assert.True(panel.Pool[0].HasBody);
        Assert.Equal("a body", panel.Pool[0].Body);

        Assert.False(panel.Pool[1].InWindow);
        Assert.False(panel.Pool[1].HasBody);
        Assert.Null(panel.Pool[1].Body);

        // **The instant survives the object route**, which it did not until 5.9: the
        // driver returns `timestamptz` as a `DateTime` and the cast this once used asked
        // for a `DateTimeOffset`, so every article read as published at no time at all
        // while the same statement's window expression said otherwise. The doubles above
        // hand back what the driver hands back, which is what makes this assertion mean
        // something.
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero), panel.Pool[0].PublishedAt);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), panel.Pool[1].PublishedAt);
    }

    /// <summary>
    /// **A pool with articles and a null digest is the state the two flags exist to
    /// explain.** Every article carrying a null body produces D-134's second outcome with
    /// a non-empty pool, which reads as a contradiction unless the panel says which rows
    /// had no body [D-131].
    /// </summary>
    [Fact]
    public async Task ABodilessPoolAndANullDigestAreReadableTogether()
    {
        var ct = TestContext.Current.CancellationToken;

        var data = new Store(
            digest: [null, "local", "qwen3.6:latest", false],
            articles:
            [
                [new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc),
                    "Headline only", "Reuters", "u1", null, true, false],
            ]);

        var panel = await new RecordInspector(data, new Keys()).DigestAsync(Ticker, Viewed, ct)
            .ConfigureAwait(true);

        Assert.Equal("no_articles", panel.Outcome);
        Assert.Single(panel.Pool);
        Assert.True(panel.Pool[0].InWindow);
        Assert.False(panel.Pool[0].HasBody);
    }

    /// <summary>
    /// **Both keys resolve as of the viewed date and never as of now** [D-43, INVARIANT
    /// 13]. A panel taking today's lookback would mark the wrong rows in-window against a
    /// digest written under a different one, and it would look right doing it.
    /// </summary>
    [Fact]
    public async Task TheTwoDigestKeysResolveAsOfTheViewedDate()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new Keys();

        await new RecordInspector(new Store(digest: null), config)
            .DigestAsync(Ticker, Viewed, ct).ConfigureAwait(true);

        Assert.Equal(["digest.lookback_days", "digest.max_input_tokens"], config.Asked);
        Assert.All(config.AskedFor, d => Assert.Equal(Viewed, d));
    }

    /// <summary>
    /// The panel reads its two tables and nothing else, on a name with no rows at all. The
    /// declaration is held against the catalogue elsewhere; this is the half saying the
    /// panel does not reach past it to answer.
    /// </summary>
    [Fact]
    public async Task ThePanelReadsTheTwoTablesItIsAbout()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new Store(digest: null);

        await new RecordInspector(data, new Keys()).DigestAsync(Ticker, Viewed, ct).ConfigureAwait(true);

        Assert.Equal(["headline", "news_digest"], data.Touched.Order(StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- doubles ---

    /// <summary>
    /// `headline` rows and at most one `news_digest` row, in the column order the
    /// statements select. Every write route throws, the seam being an
    /// <see cref="IStageData"/>.
    /// </summary>
    private sealed class Store(object?[]? digest, IReadOnlyList<object?[]>? articles = null) : IStageData
    {
        public List<string> Touched { get; } = [];

        public List<string> Sql { get; } = [];

        public Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
            string table, string sql, CancellationToken ct = default, int? commandTimeoutSeconds = null)
        {
            Touched.Add(table);
            Sql.Add(sql);

            IReadOnlyList<IReadOnlyList<object?>> rows = table switch
            {
                "headline" => [.. (articles ?? []).Select(a => (IReadOnlyList<object?>) a)],
                "news_digest" when digest is not null => [digest],
                _ => [],
            };

            return Task.FromResult(rows);
        }

        public Task<long> WriteAsync(
            string table, WriteOperation operation, string sql,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new UndeclaredTableAccessException("RecordInspector", table, operation.ToString(), "none");

        public Task<long> BulkUpsertAsync(
            string table, IReadOnlyList<string> columns, IReadOnlyList<string> conflictTarget,
            Func<IBulkWriter, CancellationToken, Task> write, CancellationToken ct = default)
            => throw new UndeclaredTableAccessException("RecordInspector", table, "Insert", "none");
    }

    /// <summary>
    /// The two digest keys at their seeded values, recording which key was asked for and
    /// as of when.
    /// </summary>
    private sealed class Keys : IConfigStore
    {
        public List<string> Asked { get; } = [];

        public List<DateOnly> AskedFor { get; } = [];

        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
        {
            Asked.Add(key);
            AskedFor.Add(asOf);

            var value = key switch
            {
                "digest.lookback_days" => "7",
                "digest.max_input_tokens" => "6000",
                _ => "1",
            };

            return Task.FromResult<ConfigRow?>(new ConfigRow(key, 1, value, new DateOnly(2020, 1, 1)));
        }

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A reader resolves keys, never the store-wide version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A reader resolves keys, never the store-wide version.");
    }
}
