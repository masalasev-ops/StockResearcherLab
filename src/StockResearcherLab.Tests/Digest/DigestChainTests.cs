using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Pipeline.Select;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.7. The chain [D-136, D-137].
///
/// **Every test here uses fabricated links and that is the point rather than a
/// convenience.** The chain's whole claim is that nothing in it knows which link is
/// preferred, so the assertion that proves it is the same chain run with the order
/// reversed and the same code choosing the other one. A test against the two real links
/// would prove the two real links work and say nothing about the claim.
///
/// **The secondary is unbuilt** and 5.6 waits on a credential, so the fabricated pair is
/// also what makes 5.7 buildable now. A chain whose fall-through cannot be exercised is a
/// chain whose fall-through is untested, and D-137's rule is the reason this checkpoint
/// exists.
/// </summary>
public sealed class DigestChainTests
{
    private static readonly DigestRequest Request = new("instruction", "articles", 150);

    // ------------------------------------------------------------- the order ---

    [Fact]
    public async Task TheChainIsOrderedByProviderOrderAndNamedByTheKey()
    {
        var chain = await BuildAsync(["local", "haiku"], Answering(DigestProvider.Local), Answering(DigestProvider.Haiku));

        Assert.Equal([DigestProvider.Local, DigestProvider.Haiku], chain.Order);
    }

    [Fact]
    public async Task TheFirstLinkAnswersAndTheSecondIsNotCalled()
    {
        var first = Answering(DigestProvider.Local);
        var second = Answering(DigestProvider.Haiku);

        var chain = await BuildAsync(["local", "haiku"], first, second);
        var answer = await chain.DigestAsync(Request, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(answer);
        Assert.Equal(DigestProvider.Local, answer!.Provider);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
        Assert.Empty(answer.PassedOver);
    }

    /// <summary>
    /// **The assertion that no code path names a link.** The same two implementations in
    /// the opposite order, and the chain answers from the other one. A branch that knew
    /// which link was preferred would answer from the same one both times.
    /// </summary>
    [Fact]
    public async Task ReversingTheOrderReversesWhichLinkAnswers()
    {
        var local = Answering(DigestProvider.Local);
        var haiku = Answering(DigestProvider.Haiku);

        var forward = await BuildAsync(["local", "haiku"], local, haiku);
        Assert.Equal(DigestProvider.Local, (await forward.DigestAsync(Request, ct: Ct))!.Provider);

        var reversed = await BuildAsync(["haiku", "local"], haiku, local);
        Assert.Equal(DigestProvider.Haiku, (await reversed.DigestAsync(Request, ct: Ct))!.Provider);
    }

    /// <summary>
    /// **Falling through works in both directions**, which is why §07 asks for a chain
    /// rather than a primary with a fallback. A dead second link with a healthy first is
    /// the case a fallback branch never exercises.
    /// </summary>
    [Fact]
    public async Task TheFallThroughWorksFromEitherEnd()
    {
        var forward = await BuildAsync(["local", "haiku"], Silent(DigestProvider.Local), Answering(DigestProvider.Haiku));
        Assert.Equal(DigestProvider.Haiku, (await forward.DigestAsync(Request, ct: Ct))!.Provider);

        var reversed = await BuildAsync(["haiku", "local"], Silent(DigestProvider.Haiku), Answering(DigestProvider.Local));
        Assert.Equal(DigestProvider.Local, (await reversed.DigestAsync(Request, ct: Ct))!.Provider);
    }

    /// <summary>
    /// **The rotation's target is a position and not a name** [D-138, 5.10]. Asked for the
    /// link at the chain's second position, the same code answers from the other one when
    /// the chain is built in the opposite order, which is `ReversingTheOrderReverses...`
    /// applied to the preference rather than to the default.
    /// </summary>
    [Fact]
    public async Task ThePreferredLinkIsTheSecondPositionFromEitherEnd()
    {
        var local = Answering(DigestProvider.Local);
        var haiku = Answering(DigestProvider.Haiku);

        var forward = await BuildAsync(["local", "haiku"], local, haiku);
        Assert.Equal(DigestProvider.Haiku, forward.RotationTarget);
        Assert.Equal(
            DigestProvider.Haiku,
            (await forward.DigestAsync(Request, forward.RotationTarget, Ct))!.Provider);

        var reversed = await BuildAsync(["haiku", "local"], haiku, local);
        Assert.Equal(DigestProvider.Local, reversed.RotationTarget);
        Assert.Equal(
            DigestProvider.Local,
            (await reversed.DigestAsync(Request, reversed.RotationTarget, Ct))!.Provider);
    }

    /// <summary>
    /// **A preferred link that answers with nothing falls through to the rest of the chain
    /// in its own order**, so a rotated candidate on a night the secondary is dead reaches
    /// exactly the link an ordinary candidate would have [D-137, D-138]. The preference
    /// moves where the walk starts and changes nothing else.
    /// </summary>
    [Fact]
    public async Task APreferredLinkThatCannotAnswerFallsThroughToTheRest()
    {
        var local = Answering(DigestProvider.Local);
        var haiku = Silent(DigestProvider.Haiku);

        var chain = await BuildAsync(["local", "haiku"], local, haiku);

        var answer = await chain.DigestAsync(Request, chain.RotationTarget, Ct);

        Assert.NotNull(answer);
        Assert.Equal(DigestProvider.Local, answer.Provider);
        Assert.Single(answer.PassedOver);
    }

    /// <summary>
    /// A chain of one has no second position, so it has nowhere to rotate to and says null
    /// rather than pointing at itself. A caller then marks the candidate and sends it down
    /// the ordinary path, which is what D-138's "the whole set rotates" needs to stay
    /// meaningful on a one-link chain.
    /// </summary>
    [Fact]
    public async Task AChainOfOneHasNoRotationTarget()
    {
        var chain = await BuildAsync(["local"], Answering(DigestProvider.Local));

        Assert.Null(chain.RotationTarget);
        Assert.Equal(DigestProvider.Local, (await chain.DigestAsync(Request, null, Ct))!.Provider);
    }

    // ------------------------------------------------------------- D-137 ---

    /// <summary>
    /// **Retried once on the same link, then passed over.** Two calls to the first link
    /// and one to the second, which is the count D-137 states rather than an implication
    /// of it.
    /// </summary>
    [Fact]
    public async Task AnEmptyAnswerIsRetriedOnceAndThenFallsThrough()
    {
        var first = Silent(DigestProvider.Local);
        var second = Answering(DigestProvider.Haiku);

        var chain = await BuildAsync(["local", "haiku"], first, second);
        var answer = await chain.DigestAsync(Request, ct: Ct);

        Assert.Equal(2, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Equal(DigestProvider.Haiku, answer!.Provider);
    }

    /// <summary>
    /// **Both records, which is D-137's done-when.** The answer carries the second link's
    /// provider and model, and a line names the first link and why it was passed over.
    /// One without the other says a fall-through happened and not why, or why and not what
    /// answered.
    /// </summary>
    [Fact]
    public async Task TheFallThroughIsVisibleInBothPlaces()
    {
        var chain = await BuildAsync(
            ["local", "haiku"], Silent(DigestProvider.Local), Answering(DigestProvider.Haiku, model: "claude-haiku-4-5"));

        var answer = await chain.DigestAsync(Request, ct: Ct);

        Assert.Equal(DigestProvider.Haiku, answer!.Provider);
        Assert.Equal("claude-haiku-4-5", answer.Model);

        var line = Assert.Single(answer.PassedOver);
        Assert.Contains("local", line, StringComparison.Ordinal);
        Assert.Contains("no content", line, StringComparison.Ordinal);
        Assert.Contains("unhealthy for the rest of this run", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A link that failed twice is not asked again this run, which is what stops a dead
    /// link costing two calls per candidate rather than two calls.
    /// </summary>
    [Fact]
    public async Task ALinkThatFailedTwiceIsNotAskedAgainThisRun()
    {
        var first = Silent(DigestProvider.Local);
        var second = Answering(DigestProvider.Haiku);

        var chain = await BuildAsync(["local", "haiku"], first, second);

        for (var candidate = 0; candidate < 5; candidate++)
        {
            Assert.Equal(DigestProvider.Haiku, (await chain.DigestAsync(Request, ct: Ct))!.Provider);
        }

        Assert.Equal(2, first.Calls);
        Assert.Equal(5, second.Calls);
        Assert.Equal([DigestProvider.Local], chain.Failed);

        // And the reason is recorded once rather than on every candidate after it.
        Assert.Single((await chain.DigestAsync(Request, ct: Ct))!.PassedOver);
    }

    /// <summary>
    /// **Over-long is asserted on the response and nothing is truncated.** 200 completion
    /// tokens against a cap of 150, twice, and the digest that reaches the caller is the
    /// other link's whole answer rather than the first link's cut to fit.
    /// </summary>
    [Fact]
    public async Task AnOverLongAnswerIsRefusedRatherThanCut()
    {
        var first = new StubLink(DigestProvider.Local)
        {
            Answer = new DigestAnswer(new string('x', 4_000), "qwen3.6:latest", 3_000, 200),
        };
        var second = Answering(DigestProvider.Haiku, text: "A short digest.");

        var chain = await BuildAsync(["local", "haiku"], first, second);
        var answer = await chain.DigestAsync(Request, ct: Ct);

        Assert.Equal(2, first.Calls);
        Assert.Equal("A short digest.", answer!.Text);
        Assert.Contains("200 completion token(s) against a cap of 150", answer.PassedOver[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerExactlyAtTheCapIsAccepted()
    {
        var link = new StubLink(DigestProvider.Local)
        {
            Answer = new DigestAnswer("At the cap.", "qwen3.6:latest", 3_000, 150),
        };

        var chain = await BuildAsync(["local", "haiku"], link, Answering(DigestProvider.Haiku));
        var answer = await chain.DigestAsync(Request, ct: Ct);

        Assert.Equal(DigestProvider.Local, answer!.Provider);
        Assert.Equal(1, link.Calls);
    }

    /// <summary>
    /// **A link that reports no completion count is refused, which is stricter than D-137
    /// says and is the fail-closed reading of it.** The over-long test is an assertion on
    /// the response; a link that cannot say how long its answer was puts the one case
    /// D-137 exists for outside the net. Both real links report usage, so this refuses
    /// nothing that currently exists.
    /// </summary>
    [Fact]
    public async Task AnAnswerWithNoReportedLengthIsRefused()
    {
        var first = new StubLink(DigestProvider.Local)
        {
            Answer = new DigestAnswer("A digest of unknown length.", "qwen3.6:latest", null, null),
        };

        var chain = await BuildAsync(["local", "haiku"], first, Answering(DigestProvider.Haiku));
        var answer = await chain.DigestAsync(Request, ct: Ct);

        Assert.Equal(DigestProvider.Haiku, answer!.Provider);
        Assert.Contains("cap cannot be asserted", answer.PassedOver[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Whitespace is not content, one component over from where 5.5 asserts it. A model
    /// that answers with a newline satisfies every non-null check and carries no digest.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n\t")]
    public async Task AnAnswerThatIsOnlyWhitespaceIsMalformed(string text)
    {
        var first = new StubLink(DigestProvider.Local)
        {
            Answer = new DigestAnswer(text, "qwen3.6:latest", 3_000, 0),
        };

        var chain = await BuildAsync(["local", "haiku"], first, Answering(DigestProvider.Haiku));

        Assert.Equal(DigestProvider.Haiku, (await chain.DigestAsync(Request, ct: Ct))!.Provider);
    }

    /// <summary>
    /// A transport failure is malformed [D-137] and is named as itself. "Unreachable" and
    /// "answered with nothing" call for opposite responses from whoever reads the line.
    /// </summary>
    [Fact]
    public async Task ATransportFailureIsMalformedAndSaysSoDifferently()
    {
        var first = new StubLink(DigestProvider.Local) { Throw = true };

        var chain = await BuildAsync(["local", "haiku"], first, Answering(DigestProvider.Haiku));
        var answer = await chain.DigestAsync(Request, ct: Ct);

        Assert.Equal(2, first.Calls);
        Assert.Contains("could not be reached", answer!.PassedOver[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// **No link answers and the chain says so rather than inventing one.** This is the
    /// state INVARIANT 15's gate halts on, and 5.11 is where the halt is built; here it is
    /// only that the chain returns nothing and every link is recorded as having failed.
    /// </summary>
    [Fact]
    public async Task NoHealthyLinkReturnsNothingAndRecordsBoth()
    {
        var chain = await BuildAsync(
            ["local", "haiku"], Silent(DigestProvider.Local), Silent(DigestProvider.Haiku));

        Assert.Null(await chain.DigestAsync(Request, ct: Ct));
        Assert.Equal([DigestProvider.Local, DigestProvider.Haiku], chain.Failed);
    }

    // ------------------------------------------------------- what fails to build ---

    /// <summary>
    /// **A length mismatch fails the run rather than being reconciled.** `digest.chain` is
    /// versioned config and `local_model_config` is not, so the two can disagree, and a
    /// chain with an address nothing can name is not something to repair silently at
    /// 18:33.
    /// </summary>
    [Fact]
    public async Task AKeyAndATableOfDifferentLengthsFailToBuild()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAsync(["local"], Answering(DigestProvider.Local), Answering(DigestProvider.Haiku)));

        Assert.Contains("digest.chain names 1 link(s)", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("2 enabled row(s)", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The vocabulary is closed and `DigestProviders.Parse` fails closed [D-134, 5.2], so
    /// a chain naming a link the CHECK would refuse cannot be built. The two lists are the
    /// same list and this is where the config half of it is held to that.
    /// </summary>
    [Fact]
    public async Task ANameOutsideTheVocabularyFailsToBuild()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAsync(["local", "gemini"], Answering(DigestProvider.Local), Answering(DigestProvider.Haiku)));

        Assert.Contains("is not a digest provider", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name in config with nothing implementing it is a chain one shorter than the
    /// record says, and the fall-through it describes would be invisible.
    /// </summary>
    [Fact]
    public async Task ANamedLinkWithNoImplementationFailsToBuild()
    {
        // Two enabled rows and two names, so the length check passes and the failure is
        // the one this test is about rather than the one before it.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAsync(["local", "haiku"], rows: 2, Answering(DigestProvider.Local)));

        Assert.Contains("no link implements it", thrown.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- the seams ---

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<DigestChain> BuildAsync(string[] names, params StubLink[] links)
        => BuildAsync(names, links.Length, links);

    /// <summary>
    /// The row count stated apart from the link count, so a test can put a name in config
    /// that nothing implements without tripping the length check first.
    /// </summary>
    private static Task<DigestChain> BuildAsync(string[] names, int rows, params StubLink[] links)
        => DigestChain.BuildAsync(
            new ChainData(rows),
            new StubConfig(names),
            new DateOnly(2026, 8, 24),
            links.ToDictionary(l => l.Provider, IDigestLink (l) => l),
            Ct);

    private static StubLink Answering(DigestProvider provider, string? model = null, string text = "A digest.")
        => new(provider) { Answer = new DigestAnswer(text, model ?? "a-model", 3_000, 40) };

    private static StubLink Silent(DigestProvider provider)
        => new(provider) { Answer = new DigestAnswer(string.Empty, "a-model", 3_000, 0) };

    private sealed class StubLink(DigestProvider provider) : IDigestLink
    {
        public DigestProvider Provider { get; } = provider;

        public DigestAnswer Answer { get; set; } = new("A digest.", "a-model", 3_000, 40);

        public bool Throw { get; set; }

        public int Calls { get; private set; }

        public Task<LinkHealth> HealthAsync(CancellationToken ct = default)
            => Task.FromResult(new LinkHealth(Provider, true, "a-model", 1, "stub"));

        public Task<DigestAnswer> DigestAsync(DigestRequest request, CancellationToken ct = default)
        {
            Calls++;

            return Throw
                ? throw new HttpRequestException("connection refused")
                : Task.FromResult(Answer);
        }
    }

    /// <summary>`enabled` rows at `provider_order` 1..n, without a store.</summary>
    private sealed class ChainData(int rows) : IStageData
    {
        public Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
            string table, string sql, CancellationToken ct = default, int? commandTimeoutSeconds = null)
        {
            Assert.Equal("local_model_config", table);

            IReadOnlyList<IReadOnlyList<object?>> result =
                [.. Enumerable.Range(1, rows).Select(i => new object?[] { i })];

            return Task.FromResult(result);
        }

        public Task<long> WriteAsync(
            string table, WriteOperation operation, string sql,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("The chain writes nothing.");

        public Task<long> BulkUpsertAsync(
            string table, IReadOnlyList<string> columns, IReadOnlyList<string> conflictTarget,
            Func<IBulkWriter, CancellationToken, Task> write, CancellationToken ct = default)
            => throw new NotSupportedException("The chain writes nothing.");
    }

    /// <summary>`digest.chain` and nothing else; anything unexpected throws.</summary>
    private sealed class StubConfig(string[] names) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => string.Equals(key, "digest.chain", StringComparison.Ordinal)
                ? Task.FromResult<ConfigRow?>(new ConfigRow(
                    key, 1,
                    "[" + string.Join(",", names.Select(n => "\"" + n + "\"")) + "]",
                    new DateOnly(2020, 1, 1)))
                : throw new InvalidOperationException(
                    $"The chain resolved '{key}', which 5.7 does not expect it to read.");

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("The chain does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("The chain does not resolve the store-wide config version.");
    }
}
