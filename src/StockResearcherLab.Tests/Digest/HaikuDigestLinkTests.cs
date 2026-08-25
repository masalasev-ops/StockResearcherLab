using System.Net;
using System.Text;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Pipeline.Select;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.6. The chain's secondary link.
///
/// **Every assertion here is made against a stubbed transport and not a paid call.** The
/// SDK's `HttpClient` is settable, which is the same seam C32's tests use, so the mapping
/// from a Messages response onto `DigestAnswer` is asserted without spending. What that
/// cannot cover is whether the wire shape is right, and the phase record says so: one
/// live call is the only thing that proves the request the SDK builds is accepted.
/// </summary>
public sealed class HaikuDigestLinkTests
{
    private const string Key = "sk-ant-not-a-real-key-for-tests";
    private const string ModelId = "claude-haiku-4-5";

    private static HaikuDigestLink Link(StubHandler handler)
        => new(Key, ModelId, new HttpClient(handler));

    private static DigestRequest Request()
        => new("Summarise the articles.", "[2026-08-11] Acme raised guidance.", 150);

    /// <summary>
    /// The provider string is what reaches `news_digest.provider`, closed by a CHECK to
    /// the vocabulary [D-134]. A link cannot invent one and this asserts it does not.
    /// </summary>
    [Fact]
    public void TheProviderIsHaiku()
        => Assert.Equal(DigestProvider.Haiku, Link(new StubHandler()).Provider);

    /// <summary>
    /// **The four token counts map onto the four `cost_ledger` carries** [5.14, D-140].
    /// The two cache counts are new on `DigestAnswer` and exist because this link has a
    /// cache and the primary does not.
    /// </summary>
    [Fact]
    public async Task AllFourTokenCountsAreCarriedOffTheResponse()
    {
        var answer = await Link(new StubHandler
        {
            Text = "Acme raised guidance.",
            InputTokens = 812,
            OutputTokens = 31,
            CacheCreationInputTokens = 400,
            CacheReadInputTokens = 0,
        }).DigestAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("Acme raised guidance.", answer.Text);
        Assert.Equal(812, answer.PromptTokens);
        Assert.Equal(31, answer.CompletionTokens);
        Assert.Equal(400, answer.CacheWriteTokens);

        // Zero rather than null: this link has a cache and read nothing from it, which
        // is a fact about the call [`CLAUDE.md` section 6].
        Assert.Equal(0, answer.CacheReadTokens);
    }

    /// <summary>
    /// The model the provider says answered is what reaches `news_digest.model_name`
    /// [D-29], not the id that was requested. They differ the day an alias resolves.
    /// </summary>
    [Fact]
    public async Task TheModelRecordedIsTheOneTheProviderSaysAnswered()
    {
        var answer = await Link(new StubHandler { Text = "x", Model = "claude-haiku-4-5-20251001" })
            .DigestAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("claude-haiku-4-5-20251001", answer.Model);
    }

    /// <summary>
    /// **An empty answer comes back empty rather than throwing** [INVARIANT 7, D-137].
    /// Malformed is the chain's test and a link that threw on one would take the decision
    /// away from the component that owns it.
    /// </summary>
    [Fact]
    public async Task AnEmptyAnswerIsReturnedRatherThanThrown()
    {
        var answer = await Link(new StubHandler { Text = "" })
            .DigestAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Null(answer.Text);
        Assert.Equal(DigestProvider.Haiku, Link(new StubHandler()).Provider);
    }

    /// <summary>
    /// **A long answer comes back long and is never cut here.** D-137's over-long test is
    /// the chain's, and a truncated digest reaches the researcher as a complete fact.
    /// </summary>
    [Fact]
    public async Task AnOverLongAnswerIsNotTruncated()
    {
        var long_ = new string('x', 4_000);

        var answer = await Link(new StubHandler { Text = long_, OutputTokens = 9_999 })
            .DigestAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(4_000, answer.Text!.Length);
        Assert.Equal(9_999, answer.CompletionTokens);
    }

    /// <summary>
    /// Health is characters of content, as it is on the primary [D-144]. A 200 carrying
    /// no text is unhealthy, which is the case a status-code check would pass.
    /// </summary>
    [Fact]
    public async Task AProbeThatAnswersWithNoContentIsUnhealthy()
    {
        var health = await Link(new StubHandler { Text = "" })
            .HealthAsync(TestContext.Current.CancellationToken);

        Assert.False(health.Healthy);
        Assert.Contains("no content", health.Detail, StringComparison.Ordinal);
    }

    /// <summary>A probe that answers is healthy and says what answered and how long it took.</summary>
    [Fact]
    public async Task AProbeThatAnswersIsHealthyAndNamesTheModel()
    {
        var health = await Link(new StubHandler { Text = "Ready to summarise." })
            .HealthAsync(TestContext.Current.CancellationToken);

        Assert.True(health.Healthy);
        Assert.Equal(ModelId, health.LoadedModel);
        Assert.Contains("character(s)", health.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The provider's own message reaches the detail line.** An expired key, an
    /// exhausted balance and an unreachable model are three different mornings for an
    /// operator, and a category chosen by this class would flatten them into one.
    /// </summary>
    [Fact]
    public async Task ARefusedProbeIsUnhealthyAndCarriesTheProvidersMessage()
    {
        var health = await Link(new StubHandler
        {
            Status = HttpStatusCode.Unauthorized,
            Body = "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\"," +
                   "\"message\":\"invalid x-api-key\"}}",
        }).HealthAsync(TestContext.Current.CancellationToken);

        Assert.False(health.Healthy);
        Assert.Contains("refused the probe", health.Detail, StringComparison.Ordinal);
        Assert.Contains("invalid x-api-key", health.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The model id is the caller's, never a literal in the link [INVARIANT 13]. An empty
    /// one is refused at construction rather than sent as a request the provider rejects.
    /// </summary>
    [Theory]
    [InlineData("", ModelId)]
    [InlineData(Key, "")]
    [InlineData("   ", ModelId)]
    public void AnAbsentKeyOrModelIsRefusedAtConstruction(string key, string model)
        => Assert.ThrowsAny<ArgumentException>(() => new HaikuDigestLink(key, model));

    /// <summary>
    /// **The construction guard, asserted from the composition rather than described in a
    /// comment** [operator direction 2026-08-25].
    ///
    /// The operator's decision is that the digest step never reaches a paid provider: a
    /// cold local model halts the night instead. That decision lives in config, as
    /// `digest.chain` at `["local"]`, and config is the right place for it. What this
    /// asserts is the second half: with the chain naming no paid link, the composition
    /// does not construct one either.
    ///
    /// **The distinction is worth a test rather than a comment.** A link that exists and
    /// is never selected is off by accident of routing, and the next person to touch the
    /// chain can reach it without noticing. A link that is never constructed is off
    /// structurally, and this test is what stops the guard being loosened back to "a key
    /// is present" by someone who reads that as the natural condition.
    /// </summary>
    [Theory]
    [InlineData("[\"local\"]", false)]
    [InlineData("[\"local\",\"haiku\"]", true)]
    public async Task TheChainsOwnMembershipDecidesWhetherAPaidLinkIsBuilt(
        string chain, bool named)
    {
        var links = await DigestChain.NamedLinksAsync(
            new ChainConfig(chain), new DateOnly(2026, 8, 25), TestContext.Current.CancellationToken);

        Assert.Equal(named, links.Contains("haiku", StringComparer.Ordinal));
    }

    /// <summary>`digest.chain` and nothing else; anything unexpected throws.</summary>
    private sealed class ChainConfig(string chain) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => string.Equals(key, "digest.chain", StringComparison.Ordinal)
                ? Task.FromResult<ConfigRow?>(new ConfigRow(key, 2, chain, new DateOnly(2026, 8, 25)))
                : throw new InvalidOperationException($"Unexpected key '{key}'.");

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ConfigRow>> ResolvePrefixAsync(
            string prefix, DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A Messages API double. Returns one text block and a usage object, or an error
    /// status where one is set, and nothing leaves the process.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string Text { get; init; } = "Ready.";

        public string Model { get; init; } = ModelId;

        public int InputTokens { get; init; } = 10;

        public int OutputTokens { get; init; } = 5;

        public int? CacheCreationInputTokens { get; init; }

        public int? CacheReadInputTokens { get; init; }

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public string? Body { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = Body ?? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $$"""
                {
                  "id": "msg_stub",
                  "type": "message",
                  "role": "assistant",
                  "model": "{{Model}}",
                  "content": [{"type": "text", "text": {{System.Text.Json.JsonSerializer.Serialize(Text)}}}],
                  "stop_reason": "end_turn",
                  "stop_sequence": null,
                  "usage": {
                    "input_tokens": {{InputTokens}},
                    "output_tokens": {{OutputTokens}},
                    "cache_creation_input_tokens": {{Json(CacheCreationInputTokens)}},
                    "cache_read_input_tokens": {{Json(CacheReadInputTokens)}}
                  }
                }
                """);

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private static string Json(int? value)
            => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    }
}
