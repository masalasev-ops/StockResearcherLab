using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.8. C33 NewsDigester and the write [D-133, D-134, D-136, D-143].
///
/// **The four outcomes are what this class is mostly about**, because the middle pair is
/// the one that gets collapsed and collapsing it makes S5's no-digest disqualifier fire on
/// thinly covered small caps, which is the population the design exists to reach [D-60].
/// </summary>
[Collection("database")]
public sealed class NewsDigesterTests : IAsyncLifetime
{
    private const string Marker = "ZDIG";

    private static readonly DateOnly Date = new(2026, 8, 12);

    /// <summary>
    /// **The chain's two rows, seeded here rather than relied on.**
    ///
    /// `TestDatabase` seeds the config keys and not `local_model_config` [D-136], so on a
    /// fresh database that table is empty until some test seeds it. These tests passed
    /// only because another class had already run, and `ci.ps1` drops both databases
    /// before every run, so the first ordering that put this class first failed six tests
    /// at once. Found there rather than here.
    ///
    /// `SeedChainAsync` is `ON CONFLICT DO NOTHING`, so calling it per test costs nothing
    /// and leaves an operator-edited row alone.
    /// </summary>
    public async ValueTask InitializeAsync()
        => await new ConfigSeeder(TestDatabase.ConnectionString)
            .SeedChainAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs after every test here, passing or failing, which clearing first cannot do:
    /// the last run's rows would otherwise outlive the class.
    ///
    /// **That is not tidiness and it was found by a failure in another class.**
    /// `SelectionShapeTests` asserts the six selection tables still hold zero rows and
    /// `HeadlineIngestorTests` asserts a night with no candidate, both over the whole
    /// shared database. A `candidate_set` row left behind here fails both.
    /// </summary>
    public async ValueTask DisposeAsync()
        => await ClearAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

    // -------------------------------------------------------- the declaration ---

    /// <summary>
    /// **`screen_score_daily` and `attribution` are absent, asserted, and that is
    /// INVARIANT 7 made structural at the component the invariant is about.** C29 carries
    /// the same absence one stage earlier; this is the one that matters, because this is
    /// the component that decides how much evidence each name gets.
    /// </summary>
    [Fact]
    public void TheDeclaredSetsNameNothingAboutAScore()
    {
        var stage = Stage(new StubLink(DigestProvider.Local));

        Assert.Equal(["candidate_set", "headline"], stage.ReadSet);
        Assert.DoesNotContain("screen_score_daily", stage.ReadSet);
        Assert.DoesNotContain("attribution", stage.ReadSet);

        // And `local_model_config` is not here either. The chain reads it through the
        // declaration C32 owns, so this stage's read set says what this stage reads
        // [D-109].
        Assert.DoesNotContain("local_model_config", stage.ReadSet);

        Assert.Equal(
            [
                new TableWrite("news_digest", WriteOperation.Insert, NewsDigester.Columns),
                new TableWrite("news_digest", WriteOperation.Delete, NewsDigester.Columns),
            ],
            stage.WriteSet.OrderBy(w => w.Operation));

        // No Update, so a later "just refresh the empty ones" throws before a connection
        // opens rather than leaving some rows tonight's and some last week's.
        Assert.DoesNotContain(stage.WriteSet, w => w.Operation == WriteOperation.Update);
    }

    // ----------------------------------------------------- D-134's four outcomes ---

    /// <summary>
    /// **Three of the four in one run, written and read back apart.** The fourth, no row
    /// at all, is the ticker that was not a candidate, and it is asserted by its absence
    /// in the same read.
    /// </summary>
    [Fact]
    public async Task TheFourOutcomesAreWrittenAsFourDistinctStates()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        // Prose, the escape hatch, and a candidate with no article at all.
        await CandidatesAsync([Prose, Hatch, Bare], ct);
        await ArticleAsync(Prose, "Something happened.", ct);
        await ArticleAsync(Hatch, "A promotional wire release.", ct);

        var link = new StubLink(DigestProvider.Local)
        {
            Answer = t => t.Contains("promotional", StringComparison.Ordinal)
                ? NewsDigester.NoMaterialNews
                : "Reported second-quarter results on 2026-08-10.",
        };

        var result = await RunAsync(link, ct);
        Assert.Equal(3, result.RowsWritten);

        var rows = await ReadAsync(ct);

        Assert.Equal("Reported second-quarter results on 2026-08-10.", rows[Prose].Text);
        Assert.Equal(NewsDigester.NoMaterialNews, rows[Hatch].Text);

        // **The state D-60 bites on**, and it is not the escape hatch beside it. A link
        // was selected and there was nothing to send.
        Assert.Null(rows[Bare].Text);

        // And the row exists, carrying the link that would have answered, so "no digest
        // available" and "the run halted" stay different facts.
        Assert.Equal("local", rows[Bare].Provider);
        Assert.Equal("qwen3.6:latest", rows[Bare].Model);

        // The fourth outcome: a ticker that was not a candidate has no row.
        Assert.DoesNotContain(Marker + "NONE", rows.Keys);
    }

    /// <summary>
    /// An article whose body the provider did not send is not an article. Every article
    /// null-bodied is the null-digest state, not a request carrying empty documents
    /// [D-131].
    /// </summary>
    [Fact]
    public async Task ACandidateWhoseArticlesAllCarryNoBodyIsTheNullState()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        await CandidatesAsync([Bare], ct);
        await ArticleAsync(Bare, null, ct);
        await ArticleAsync(Bare, "   ", ct);

        var link = new StubLink(DigestProvider.Local);
        await RunAsync(link, ct);

        Assert.Null((await ReadAsync(ct))[Bare].Text);
        Assert.Equal(0, link.Calls);
    }

    // --------------------------------------------------- D-143's selection ---

    /// <summary>
    /// **The minimum-of-one case, asserted directly.** One article, alone longer than the
    /// cap, and it is still sent. Without this the candidate produces no digest, which
    /// under D-134 is the null state and under D-60 disqualifies the name for having long
    /// news, making a long article and an absent article the same fact.
    ///
    /// **It is also the assertion a later reader would remove as a special case**, which
    /// is why it is here alone rather than as a clause of another test.
    /// </summary>
    [Fact]
    public async Task ASingleArticleLongerThanTheCapIsStillSent()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        await CandidatesAsync([Prose], ct);

        // 6,000 tokens is the cap and 3.5 characters a token is the estimate, so this
        // body alone estimates at roughly 17,000 tokens. 5.1 measured a real body of
        // 14,099, so the case is an earnings-call transcript rather than a contrivance.
        await ArticleAsync(Prose, new string('x', 60_000), ct);

        var link = new StubLink(DigestProvider.Local);
        await RunAsync(link, ct);

        Assert.Equal(1, link.Calls);
        Assert.NotNull((await ReadAsync(ct))[Prose].Text);
    }

    /// <summary>
    /// Whole articles, most recent first, while the next still fits, and the loop does not
    /// reach past one that does not fit for a smaller one behind it [D-143].
    /// </summary>
    [Fact]
    public void TheSelectionTakesWholeArticlesInOrderAndDoesNotReachPastOne()
    {
        var articles = new[]
        {
            Article(1, new string('a', 3_500)),   // most recent, 1,000 tokens
            Article(2, new string('b', 21_000)),  // 6,000 tokens, so 7,000 crosses the cap
            Article(3, new string('c', 350)),     // 100 tokens, and would fit
        };

        var selected = DigestSelection.Select(articles, 6_000);

        // The first fits. The second would cross the cap, so the selection ends there and
        // the third is not reached for.
        Assert.Single(selected);
        Assert.Equal(new string('a', 3_500), selected[0].Content);
    }

    /// <summary>
    /// **The articles reach the selection carrying their publication instant, read through
    /// the store rather than handed to it.**
    ///
    /// Every other test of D-143's order builds `Article` records by hand, so all of them
    /// passed while `ArticlesAsync` was reading the column as an absence: the driver
    /// returns `timestamptz` as a `DateTime` and the cast that read it asked for a
    /// `DateTimeOffset`, which is null for every row and never throws. The order then fell
    /// through to the tie-breaks and the rendered input said "date unknown" on articles
    /// whose date the store held.
    ///
    /// **It asserts on what the link was sent**, which is the only place the two effects
    /// are both visible: the dates appear, and the newer article appears before the older
    /// one rather than in url order.
    /// </summary>
    [Fact]
    public async Task TheArticlesReachTheLinkWithTheirDatesAndInDateOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        await CandidatesAsync([Prose], ct);

        // The older article sorts first on the url tie-break and second on the date, so
        // one order is the rule and the other is the rule having nothing to order on.
        await ArticleAsync(Prose, "older body", new DateTimeOffset(2026, 8, 7, 9, 0, 0, TimeSpan.Zero), "http://example.invalid/a", ct);
        await ArticleAsync(Prose, "newer body", new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero), "http://example.invalid/b", ct);

        var link = new StubLink(DigestProvider.Local);
        string? sent = null;
        link.Answer = input => { sent = input; return "A digest."; };

        await RunAsync(link, ct);

        Assert.NotNull(sent);
        Assert.DoesNotContain("date unknown", sent, StringComparison.Ordinal);
        Assert.Contains("[2026-08-11]", sent, StringComparison.Ordinal);
        Assert.Contains("[2026-08-07]", sent, StringComparison.Ordinal);

        Assert.True(
            sent.IndexOf("newer body", StringComparison.Ordinal)
                < sent.IndexOf("older body", StringComparison.Ordinal),
            "The newer article is sent first [D-143].");
    }

    /// <summary>
    /// **The tie-break is total, which D-133's source-only rule is not on this provider.**
    /// 5.1 measured no source at all and 5.4's fixture is two articles sharing a
    /// publication instant, a title and a link and differing only in body. An undecided
    /// order is a different prompt on two runs of one night [INVARIANT 6].
    /// </summary>
    [Fact]
    public void ArticlesIdenticalButForTheirBodyAreOrderedDeterministically()
    {
        var at = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        var first = new Article(at, "One title", null, "http://example.invalid/a", "bbb");
        var second = new Article(at, "One title", null, "http://example.invalid/a", "aaa");

        Assert.Equal(
            DigestSelection.Select([first, second], 6_000).Select(a => a.Content),
            DigestSelection.Select([second, first], 6_000).Select(a => a.Content));

        Assert.Equal("aaa", DigestSelection.Select([first, second], 6_000)[0].Content);
    }

    /// <summary>
    /// The estimate over-counts against both ratios 5.1 measured, which is what keeps
    /// D-143's ceiling a ceiling. Asserted on the constant rather than on a length, so a
    /// later session cannot raise it toward 4.30 without this failing.
    /// </summary>
    [Fact]
    public void TheTokenEstimateIsConservativeAgainstBothMeasuredRatios()
    {
        Assert.True(DigestSelection.CharactersPerToken < 3.80m,
            "5.1 measured 3.80 characters per token over a real three-article prompt. An estimate " +
            "at or above that under-counts the input and D-143's $47.63 ceiling stops being one.");

        Assert.Equal(2, DigestSelection.EstimateTokens("abcdefg"));
        Assert.Equal(0, DigestSelection.EstimateTokens(string.Empty));
    }

    // ------------------------------------------------------------ the write ---

    /// <summary>
    /// A re-run over one date replaces the night rather than merging with it, and the
    /// second run's rows are the first run's [D-132's shape one table over].
    /// </summary>
    [Fact]
    public async Task ARerunOverOneDateReplacesRatherThanAppends()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        await CandidatesAsync([Prose], ct);
        await ArticleAsync(Prose, "Something happened.", ct);

        var link = new StubLink(DigestProvider.Local);

        await RunAsync(link, ct);
        var first = await ReadAsync(ct);

        await RunAsync(link, ct);
        var second = await ReadAsync(ct);

        Assert.Single(second);
        Assert.Equal(first[Prose].Text, second[Prose].Text);
    }

    /// <summary>
    /// A night with no candidate writes nothing and says the zero is expected, so the
    /// zero-row halt does not fire. C14's warm-up case two stages later.
    /// </summary>
    [Fact]
    public async Task ANightWithNoCandidateWritesNothingAndSaysTheZeroIsExpected()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        var link = new StubLink(DigestProvider.Local);
        var result = await RunAsync(link, ct);

        Assert.Equal(0, result.RowsWritten);
        Assert.True(result.ZeroRowsExpected);
        Assert.Equal(0, link.Calls);
    }

    /// <summary>
    /// **The gate, in its narrow observable form.** No healthy link fails the stage rather
    /// than writing rows with something in them. 5.11 asserts the rest of INVARIANT 15,
    /// that no stage after C33 runs.
    /// </summary>
    [Fact]
    public async Task NoHealthyLinkFailsTheStageRatherThanWritingAnything()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        await CandidatesAsync([Prose], ct);
        await ArticleAsync(Prose, "Something happened.", ct);

        // Both links unhealthy, which is the only state INVARIANT 15's gate fires on. One
        // unhealthy link is a fall-through and 5.7 asserts that.
        var link = new StubLink(DigestProvider.Local) { Healthy = false };
        var secondary = new StubLink(DigestProvider.Haiku) { Healthy = false };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(link, ct, secondary));

        Assert.Contains("INVARIANT 15", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(await ReadAsync(ct));
    }

    /// <summary>
    /// A link that answered without naming a model fails the write rather than producing a
    /// row that reads as attributed. `model_name` is what separates a shift in results
    /// from a change in the evidence [D-29], and a blank there records nothing.
    /// </summary>
    [Fact]
    public async Task ALinkThatNamesNoModelFailsRatherThanWritingABlank()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        await CandidatesAsync([Prose], ct);
        await ArticleAsync(Prose, "Something happened.", ct);

        var link = new StubLink(DigestProvider.Local) { Model = null };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(link, ct));

        Assert.Contains("named no model", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(await ReadAsync(ct));
    }

    // ------------------------------------------------------- the instruction ---

    /// <summary>
    /// **The instruction sent is the instruction section and not the file.** The file is
    /// prose for a person as well as for a model, and the section below the rule explains
    /// what the model is likely to get wrong, which is not an instruction.
    /// </summary>
    [Fact]
    public void TheInstructionIsTheSectionAndNotTheWholeFile()
    {
        var instruction = DigestInstruction.Read(PipelineComposition.DigestInstructionPath);

        Assert.Contains(NewsDigester.NoMaterialNews, instruction.Text, StringComparison.Ordinal);
        Assert.Contains("You are not judging it", instruction.Text, StringComparison.Ordinal);

        // The explanation is below the rule and is not sent.
        Assert.DoesNotContain("## Why this shape", instruction.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("# digest-instruction.md", instruction.Text, StringComparison.Ordinal);

        Assert.Equal(64, instruction.Sha256.Length);
    }

    /// <summary>
    /// The hash is over the extracted text with line endings normalised, so a checkout on
    /// another machine produces the same version string for the same instruction.
    /// </summary>
    [Fact]
    public void TheInstructionHashIsIndependentOfLineEndings()
    {
        const string file = "# h\n\n---\n\n## The instruction\n\nDo the thing.\n\n---\n\n## Why\n";

        Assert.Equal(
            DigestInstruction.Parse(file).Sha256,
            DigestInstruction.Parse(file.Replace("\n", "\r\n", StringComparison.Ordinal)).Sha256);
    }

    [Fact]
    public void AnInstructionFileWithNoClosingRuleFailsRatherThanSendingTheExplanation()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => DigestInstruction.Parse("## The instruction\n\nDo the thing.\n\n## Why\nBecause.\n"));

        Assert.Contains("not closed by a horizontal rule", thrown.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- the seams ---

    // ------------------------------------------------- D-138's rotation ---

    /// <summary>
    /// **Exactly `digest.rotation_count` rows carry `was_rotation`, and they are the rows
    /// the hash names** [D-138].
    ///
    /// The pair itself is pinned in `DigestRotationTests` against literal names. What is
    /// asserted here is the half that lives in C33: the rows the rule selects are the rows
    /// that go to the chain's second position and the rows the column marks.
    /// </summary>
    [Fact]
    public async Task ExactlyTheRotationCountRowsAreMarkedAndTheyAreTheOnesTheHashNames()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        await CandidatesAsync(Five, ct);

        foreach (var ticker in Five)
        {
            await ArticleAsync(ticker, "Something happened at " + ticker, ct);
        }

        var local = new StubLink(DigestProvider.Local);
        var secondary = new StubLink(DigestProvider.Haiku) { Model = "claude-haiku-4-5" };

        await RunAsync(local, ct, secondary, rotation: 2);

        var rows = await ReadAsync(ct);
        var marked = rows.Where(r => r.Value.WasRotation).Select(r => r.Key).Order(StringComparer.Ordinal);

        Assert.Equal(5, rows.Count);
        Assert.Equal(
            DigestRotation.Select(Date, Five, 2).Order(StringComparer.Ordinal),
            marked);

        // The marked rows went to the second position and the rest to the first, which is
        // what makes D-27's pair a pair rather than a label.
        foreach (var (ticker, row) in rows)
        {
            Assert.Equal(row.WasRotation ? "haiku" : "local", row.Provider);
            Assert.Equal(row.WasRotation ? "claude-haiku-4-5" : "qwen3.6:latest", row.Model);
            Assert.NotNull(ticker);
        }

        Assert.Equal(2, secondary.Calls);
        Assert.Equal(3, local.Calls);
    }

    /// <summary>
    /// **The rotation is present on a night the primary is down**, which is the case D-138
    /// says `was_rotation` exists for: everything went to the secondary, and without the
    /// column the rotation's rows would be indistinguishable from the fall-through.
    /// </summary>
    [Fact]
    public async Task TheRotationIsPresentOnANightThePrimaryIsDown()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        await CandidatesAsync(Five, ct);

        foreach (var ticker in Five)
        {
            await ArticleAsync(ticker, "Something happened at " + ticker, ct);
        }

        var local = new StubLink(DigestProvider.Local) { Healthy = false };
        var secondary = new StubLink(DigestProvider.Haiku) { Model = "claude-haiku-4-5" };

        await RunAsync(local, ct, secondary, rotation: 2);

        var rows = await ReadAsync(ct);

        Assert.All(rows.Values, r => Assert.Equal("haiku", r.Provider));
        Assert.Equal(2, rows.Count(r => r.Value.WasRotation));
        Assert.Equal(
            DigestRotation.Select(Date, Five, 2).Order(StringComparer.Ordinal),
            rows.Where(r => r.Value.WasRotation).Select(r => r.Key).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// **A rotated candidate whose target is dead falls through and is still marked.**
    ///
    /// This is the reading of D-138 this build takes and it is the one worth asserting: the
    /// flag records that the candidate was selected, and `provider` records which link
    /// answered. The alternative, marking only the rows the second link answered, makes the
    /// count vary with an outage, and the checkpoint asks for exactly
    /// `digest.rotation_count` rows on a night the primary is healthy and on a night it is
    /// not. D-27's pair is therefore the marked rows whose provider is not the first link's.
    /// </summary>
    [Fact]
    public async Task ARotatedCandidateWhoseTargetIsDeadFallsThroughAndIsStillMarked()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        await CandidatesAsync(Five, ct);

        foreach (var ticker in Five)
        {
            await ArticleAsync(ticker, "Something happened at " + ticker, ct);
        }

        var local = new StubLink(DigestProvider.Local);

        // **Dead for a digest, not merely unhealthy at the probe.** `ReadyAsync` stops at
        // the first healthy link, so a secondary that fails only its health check is never
        // probed on a night the primary is up and would answer the rotation normally. The
        // link that is actually dead is the one that answers with nothing [D-137].
        var secondary = new StubLink(DigestProvider.Haiku)
        {
            Model = "claude-haiku-4-5",
            Answer = _ => string.Empty,
        };

        await RunAsync(local, ct, secondary, rotation: 2);

        var rows = await ReadAsync(ct);

        Assert.All(rows.Values, r => Assert.Equal("local", r.Provider));
        Assert.Equal(2, rows.Count(r => r.Value.WasRotation));

        // Two calls to the dead link and not two per rotated candidate: it failed twice on
        // the first of them and was unhealthy for the rest of the run, so the second went
        // straight past it [D-137].
        Assert.Equal(2, secondary.Calls);
        Assert.Equal(5, local.Calls);
    }

    /// <summary>
    /// A candidate set smaller than the count rotates whole and nothing is padded [D-138],
    /// asserted through the stage rather than only over the rule, because the padding a
    /// naive implementation would do happens where the rows are built.
    /// </summary>
    [Fact]
    public async Task ACandidateSetSmallerThanTheRotationCountRotatesWhole()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        await CandidatesAsync([Prose], ct);
        await ArticleAsync(Prose, "Something happened.", ct);

        await RunAsync(new StubLink(DigestProvider.Local), ct, rotation: 2);

        var rows = await ReadAsync(ct);

        Assert.Single(rows);
        Assert.True(rows[Prose].WasRotation);
    }

    /// <summary>The five candidates the rotation tests run over.</summary>
    private static readonly string[] Five =
    [
        Marker + "R1.US", Marker + "R2.US", Marker + "R3.US", Marker + "R4.US", Marker + "R5.US",
    ];

    private static string Prose => Marker + "PROSE.US";

    private static string Hatch => Marker + "HATCH.US";

    private static string Bare => Marker + "BARE.US";

    private static Article Article(int daysAgo, string content)
        => new(
            new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero).AddDays(-daysAgo),
            "A title", null, "http://example.invalid/" + daysAgo.ToString(CultureInfo.InvariantCulture), content);

    /// <summary>
    /// **Both links, always, because `local_model_config` holds two enabled rows and the
    /// chain refuses to build against a `digest.chain` of a different length.** That
    /// refusal is 5.7's and it is load-bearing here: a class that named one link would be
    /// testing C33 against a chain the store does not describe.
    /// </summary>
    private static NewsDigester Stage(StubLink local, StubLink secondary, int rotation = 0)
        => new(
            (context, ct) => DigestChain.BuildAsync(
                new StageData(TestDatabase.ConnectionString, LocalModelClient.Access()),
                new StubConfig(rotation),
                context.Date,
                new Dictionary<DigestProvider, IDigestLink>
                {
                    [local.Provider] = local,
                    [secondary.Provider] = secondary,
                },
                ct),
            () => new DigestInstruction.Instruction("Summarise.", "hash"));

    private static NewsDigester Stage(StubLink local, int rotation = 0)
        => Stage(local, new StubLink(DigestProvider.Haiku) { Model = "claude-haiku-4-5" }, rotation);

    private static async Task<StageResult> RunAsync(
        StubLink link, CancellationToken ct, StubLink? secondary = null, int rotation = 0)
    {
        var stage = secondary is null ? Stage(link, rotation) : Stage(link, secondary, rotation);

        var context = new StageContext(
            Date, 1,
            new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage)),
            new FixedClock(new DateTimeOffset(2026, 8, 12, 22, 33, 0, TimeSpan.Zero), Date),
            new StubConfig(rotation));

        return await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
    }

    private sealed record Stored(string? Text, string Provider, string Model, bool WasRotation);

    private static async Task<IReadOnlyDictionary<string, Stored>> ReadAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT ticker, digest_text, provider, model_name, was_rotation FROM news_digest " +
            "WHERE ticker LIKE @m ORDER BY ticker COLLATE \"C\";", conn);
        cmd.Parameters.AddWithValue("m", Marker + "%");

        var rows = new Dictionary<string, Stored>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            rows[r.GetString(0)] = new Stored(
                await r.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : r.GetString(1),
                r.GetString(2), r.GetString(3), r.GetBoolean(4));
        }

        return rows;
    }

    private static async Task CandidatesAsync(string[] tickers, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);

        foreach (var ticker in tickers)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO candidate_set (ticker, date, screens_surfacing, size_bucket, slot_filled) " +
                "VALUES (@t, @d, ARRAY['S1'], 'small', TRUE) ON CONFLICT DO NOTHING;", conn);
            cmd.Parameters.AddWithValue("d", Date);
            cmd.Parameters.AddWithValue("t", ticker);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task ArticleAsync(string ticker, string? content, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO headline (ticker, date, published_at, title, source, url, content) " +
            "VALUES (@t, @d, @p, 'A title', NULL, @u, @c);", conn);

        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", Date);
        cmd.Parameters.AddWithValue("p", new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        cmd.Parameters.AddWithValue("u", "http://example.invalid/" + (content?.Length ?? 0).ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("c", (object?)content ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The same, with the instant and the link stated, for the one test whose subject is
    /// the order the instants put the articles in.
    /// </summary>
    private static async Task ArticleAsync(
        string ticker, string? content, DateTimeOffset at, string url, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO headline (ticker, date, published_at, title, source, url, content) " +
            "VALUES (@t, @d, @p, 'A title', NULL, @u, @c);", conn);

        cmd.Parameters.AddWithValue("t", ticker);
        cmd.Parameters.AddWithValue("d", Date);
        cmd.Parameters.AddWithValue("p", at);
        cmd.Parameters.AddWithValue("u", url);
        cmd.Parameters.AddWithValue("c", (object?)content ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM news_digest WHERE ticker LIKE @m; " +
            "DELETE FROM headline WHERE ticker LIKE @m; " +
            "DELETE FROM candidate_set WHERE ticker LIKE @m;", conn);
        cmd.Parameters.AddWithValue("m", Marker + "%");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed class StubLink(DigestProvider provider) : IDigestLink
    {
        public DigestProvider Provider { get; } = provider;

        public bool Healthy { get; set; } = true;

        public string? Model { get; set; } = "qwen3.6:latest";

        public Func<string, string> Answer { get; set; } = _ => "A digest.";

        public int Calls { get; private set; }

        public Task<LinkHealth> HealthAsync(CancellationToken ct = default)
            => Task.FromResult(new LinkHealth(
                Provider, Healthy, Healthy ? Model : null, 1,
                Healthy ? "stub answered" : "stub answered with no content"));

        public Task<DigestAnswer> DigestAsync(DigestRequest request, CancellationToken ct = default)
        {
            Calls++;
            var text = Answer(request.Articles);
            return Task.FromResult(new DigestAnswer(text, Model, 3_000, 40));
        }
    }

    /// <summary>
    /// The five keys C33 reads, and nothing else.
    /// </summary>
    /// <param name="rotation">
    /// **`digest.rotation_count`, and it is zero here where production seeds two** [5.10].
    /// The rotation routes its candidates to the chain's second link, and every test in
    /// this class that asserts which link answered, what it was sent or what it named its
    /// model would be asserting about the secondary stub instead on a candidate set of one
    /// to three names. The rotation's own tests pass two and assert on the rows.
    /// </param>
    private sealed class StubConfig(int rotation = 0) : IConfigStore
    {
        private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
        {
            ["digest.lookback_days"] = "7",
            ["digest.max_input_tokens"] = "6000",
            ["digest.max_output_tokens"] = "150",
            ["digest.chain"] = "[\"local\",\"haiku\"]",
        };

        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
        {
            var value = string.Equals(key, "digest.rotation_count", StringComparison.Ordinal)
                ? rotation.ToString(CultureInfo.InvariantCulture)
                : Values.TryGetValue(key, out var seeded)
                    ? seeded
                    : throw new InvalidOperationException(
                        $"C33 resolved '{key}', which this class does not expect it to read.");

            return Task.FromResult<ConfigRow?>(new ConfigRow(key, 1, value, new DateOnly(2020, 1, 1)));
        }

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A stage does not resolve the store-wide config version.");
    }
}
