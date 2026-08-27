using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Digest;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.5. C32 LocalModelClient [D-136, D-144].
///
/// **The assertion this class exists for is that a link answering 200 with no content
/// is unhealthy.** Everything else here is ordinary. That one is the finding 5.1
/// produced and the reason D-144 exists: a reasoning model returns a normal status, a
/// normal usage block and zero characters, so a check reading the status reports healthy
/// on the one link that cannot produce a digest, the chain digests every candidate on
/// the paid link, and it does that every night with the local server up.
///
/// **Fabricated responses rather than the live server, and D-144 says why.** The local
/// server can be made to produce the empty-content state today and cannot be relied on
/// to keep producing it: it is a property of the loaded model, and the model is
/// operator-editable by design. A test whose subject is a state that may stop existing
/// is a test that goes green for the wrong reason.
///
/// **The live server is exercised at 5.5 as well, out of band**, both directions, and
/// the transcript is at `docs/evidence/phase-5/`. That is 5.1's arrangement: a
/// measurement against a real provider is evidence, and the suite stays runnable in CI
/// on a machine with no inference server.
/// </summary>
[Collection("database")]
public sealed class LocalModelClientTests
{
    private const string Endpoint = "http://localhost:11434/v1";

    private const string LocalOptions = "{\"reasoning_effort\": \"none\"}";

    // ------------------------------------------------------- the declaration ---

    /// <summary>
    /// The write set is empty and that is the read-only guarantee made structural
    /// [D-109]. Asserted over <see cref="LocalModelClient.Access"/> itself rather than
    /// over a rebuild of the same arguments, which would pass while the constructor
    /// passed different ones.
    /// </summary>
    [Fact]
    public void ItDeclaresOneTableAndNoWriteAtAll()
    {
        var access = LocalModelClient.Access();

        Assert.Equal("LocalModelClient", access.StageName);
        Assert.Equal(["local_model_config"], access.ReadSet);
        Assert.Empty(access.WriteSet);
    }

    /// <summary>
    /// **The two columns C32 would be the obvious component to fill, and does not**
    /// [D-136]. `local_model_config.last_health_check` and `last_loaded_model` belong to
    /// the UI at phase 9, and this component reports health rather than recording it.
    /// A write attempt throws as undeclared before a connection opens, so that stays
    /// true structurally rather than by nobody having tried yet.
    /// </summary>
    [Fact]
    public async Task AnyWriteToItsOwnTableThrowsBeforeAConnectionOpens()
    {
        var ct = TestContext.Current.CancellationToken;

        // A fictional host. Reaching the store at all would fail differently, which is
        // what says the throw happened before a connection was opened.
        var data = new StageData("Host=nowhere.invalid;Database=none", LocalModelClient.Access());

        var thrown = await Assert.ThrowsAsync<UndeclaredTableAccessException>(
            () => data.WriteAsync(
                "local_model_config", WriteOperation.Update,
                "UPDATE local_model_config SET last_loaded_model = 'x';", parameters: null, ct));

        Assert.Contains("local_model_config", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsTheLocalLinkAndSaysSo()
    {
        var client = new LocalModelClient(
            new StageData("Host=nowhere.invalid;Database=none", LocalModelClient.Access()),
            new StubConfig(),
            new HttpClient(new StubHandler()));

        Assert.Equal(DigestProvider.Local, client.Provider);
        Assert.Equal("LocalModelClient", client.Name);
        Assert.Equal(["local_model_config"], client.ReadSet);
    }

    // ------------------------------------------------------------- the probe ---

    /// <summary>
    /// **200, a finish reason, a usage block, and no content. Unhealthy.**
    ///
    /// This is D-144's assertion and the one that would have caught the finding. The
    /// fabricated response is the shape 5.1 measured coming back from `qwen3.6:latest`
    /// with no `reasoning_effort`: nothing about it is an error and every field a status
    /// check would read says the link is fine.
    /// </summary>
    [Fact]
    public async Task ALinkAnsweringTwoHundredWithNoContentIsUnhealthy()
    {
        var handler = new StubHandler { Content = string.Empty };

        var health = await ProbeAsync(handler, LocalOptions);

        Assert.False(health.Healthy);
        Assert.Equal(DigestProvider.Local, health.Provider);

        // The detail line names the cause rather than saying "unhealthy", because a
        // server that is down and a server that is up and silent call for opposite
        // responses from whoever reads the run log.
        Assert.Contains("no content", health.Detail, StringComparison.Ordinal);
        Assert.Contains("request_options", health.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whitespace is not content either. A model that answers with a newline satisfies
    /// every non-null check and carries no digest, which is the same failure one
    /// character further on. `news_digest.model_name`'s CHECK was widened at 5.2 for
    /// exactly this reason and a tab is what caught it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\t")]
    public async Task ContentThatIsOnlyWhitespaceIsNotContent(string content)
    {
        var health = await ProbeAsync(new StubHandler { Content = content }, LocalOptions);

        Assert.False(health.Healthy);
    }

    [Fact]
    public async Task ALinkAnsweringWithProseIsHealthyAndNamesWhatAnswered()
    {
        var handler = new StubHandler { Content = "I am ready to summarise news articles." };

        var health = await ProbeAsync(handler, LocalOptions);

        Assert.True(health.Healthy);
        Assert.Equal("qwen3.6:latest", health.LoadedModel);
        Assert.True(health.LatencyMs >= 0);
        Assert.Contains("qwen3.6:latest", health.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The name recorded is the one the completion says answered, not the one the
    /// model list offered.** They agree today and the completion's is authoritative:
    /// §20's "loaded local model changed since last run" is about what produced tonight's
    /// digests, and an endpoint that served a different model than it listed would
    /// otherwise be recorded under the wrong name with nothing saying so.
    /// </summary>
    [Fact]
    public async Task TheModelRecordedIsTheOneThatAnsweredRatherThanTheOneListed()
    {
        var handler = new StubHandler { Content = "Ready.", AnsweringModel = "qwen3.6:q4" };

        var health = await ProbeAsync(handler, LocalOptions);

        Assert.True(health.Healthy);
        Assert.Equal("qwen3.6:q4", health.LoadedModel);
    }

    [Fact]
    public async Task AnEndpointListingNoModelIsUnhealthy()
    {
        var health = await ProbeAsync(new StubHandler { Models = [] }, LocalOptions);

        Assert.False(health.Healthy);
        Assert.Null(health.LoadedModel);
        Assert.Contains("nothing is loaded", health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndpointThatCannotBeReachedIsUnhealthyAndSaysSoDifferently()
    {
        var health = await ProbeAsync(new StubHandler { Throw = true }, LocalOptions);

        Assert.False(health.Healthy);
        Assert.Contains("could not be reached", health.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The timeout is `digest.health_timeout_ms` and it bounds the whole probe rather
    /// than one request, so a link cannot spend twice its budget by being slow twice.
    /// </summary>
    [Fact]
    public async Task ALinkThatDoesNotAnswerInsideTheTimeoutIsUnhealthy()
    {
        var health = await ProbeAsync(
            new StubHandler { Delay = TimeSpan.FromSeconds(30) }, LocalOptions, timeoutMs: 120);

        Assert.False(health.Healthy);
        Assert.Contains("digest.health_timeout_ms", health.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The same probe under the warm bound names the warm key** [5.13]. Two questions
    /// get two bounds, and a detail line saying `digest.health_timeout_ms` after a probe
    /// that ran under the other one would send an operator to change the wrong value.
    /// </summary>
    [Fact]
    public async Task TheWarmBoundNamesItsOwnKeyRatherThanTheHealthOne()
    {
        var health = await ProbeAsync(
            new StubHandler { Delay = TimeSpan.FromSeconds(30) }, LocalOptions, timeoutMs: 120,
            timeoutKey: LocalModelClient.WarmTimeoutKey);

        Assert.False(health.Healthy);
        Assert.Contains("digest.warm_timeout_ms", health.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("digest.health_timeout_ms", health.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// **No enabled row is not a sick server**, and the line says which it is. The chain
    /// is built from this table [D-136], so an operator who disabled the local link gets
    /// a report saying that rather than one implying the machine is broken.
    /// </summary>
    [Fact]
    public async Task ADisabledOrAbsentLinkIsUnhealthyWithoutAnyRequestBeingMade()
    {
        var handler = new StubHandler { Content = "Ready." };

        var client = new LocalModelClient(new NoLinkData(), new StubConfig(), new HttpClient(handler));
        var health = await client.HealthAsync(TestContext.Current.CancellationToken);

        Assert.False(health.Healthy);
        Assert.Contains("no address to probe", health.Detail, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // -------------------------------------------------- D-144's request shape ---

    /// <summary>
    /// **The column reaches the request and the client knows nothing about the key.**
    /// A client that named `reasoning_effort` would be the literal D-144 refuses, so the
    /// assertion is that whatever the column holds is what the body carries.
    /// </summary>
    [Fact]
    public async Task WhateverTheColumnHoldsIsWhatTheRequestCarries()
    {
        var handler = new StubHandler { Content = "Ready." };

        await ProbeAsync(handler, "{\"reasoning_effort\": \"none\", \"seed\": 7}");

        var body = handler.LastCompletionBody!;

        Assert.Equal("none", (string?)body["reasoning_effort"]);
        Assert.Equal(7, (int?)body["seed"]);

        // And the parameters every link takes are still there. A merge that replaced the
        // request rather than adding to it would send a body with no messages in it.
        Assert.Equal("qwen3.6:latest", (string?)body["model"]);
        Assert.Equal(LocalModelClient.ProbeMaxTokens, (int?)body["max_tokens"]);
        Assert.Equal(
            LocalModelClient.ProbeText,
            (string?)(body["messages"] as JsonArray)![0]!["content"]);
    }

    /// <summary>
    /// **An empty object is a request shape somebody chose, and it produces the failure.**
    /// This is the second half of D-144's assertion: the same server, the same model, the
    /// options removed, and the link reports unhealthy rather than healthy-with-nothing.
    /// A default of `'{}'::jsonb` on the column would have made this state arrive on
    /// every row, which is why `0023` has no default.
    /// </summary>
    [Fact]
    public async Task WithTheOptionsRemovedTheSameLinkReportsUnhealthy()
    {
        // The stub reproduces the measured behaviour rather than being told the answer:
        // it returns content when the request carries reasoning_effort and returns none
        // when it does not, which is what 5.1 measured.
        var handler = new StubHandler { ContentOnlyWithoutReasoning = true };

        var withOptions = await ProbeAsync(handler, LocalOptions);
        Assert.True(withOptions.Healthy);

        var withEmptyObject = await ProbeAsync(handler, "{}");
        Assert.False(withEmptyObject.Healthy);

        var withNull = await ProbeAsync(handler, null);
        Assert.False(withNull.Healthy);
    }

    /// <summary>
    /// A `request_options` that is not an object fails the run rather than being ignored.
    /// The column has no CHECK on its shape, deliberately, because the keys are a
    /// provider's; that leaves the client as the place a wrong shape is refused, and
    /// silently skipping it is how the link goes quietly unconfigured.
    /// </summary>
    [Fact]
    public async Task RequestOptionsThatAreNotAnObjectFailRatherThanBeingIgnored()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProbeAsync(new StubHandler { Content = "Ready." }, "[1, 2]"));

        Assert.Contains("not a JSON object", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two probes build a byte-identical request body. The merge orders the column's
    /// properties ordinally rather than taking the JSON object's own order, which is the
    /// determinism rule one endpoint over [`CLAUDE.md` §6].
    /// </summary>
    [Fact]
    public async Task TwoProbesBuildTheSameRequestByte()
    {
        var handler = new StubHandler { Content = "Ready." };

        await ProbeAsync(handler, "{\"z\": 1, \"a\": 2, \"m\": 3}");
        var first = handler.LastCompletionText;

        await ProbeAsync(handler, "{\"m\": 3, \"z\": 1, \"a\": 2}");

        Assert.Equal(first, handler.LastCompletionText);
    }

    // ----------------------------------------------------- against the store ---

    /// <summary>
    /// The row this reads is the row the seeder wrote, read through the declared route.
    /// Every other test here fabricates the link; this one asserts the statement finds
    /// the real one, which is the half a stub cannot cover.
    /// </summary>
    [Fact]
    public async Task ItReadsTheSeededLocalLinkThroughItsDeclaredAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedChainAsync(ct).ConfigureAwait(true);

        var client = new LocalModelClient(TestDatabase.ConnectionString, new HttpClient(new StubHandler()));
        var link = await client.LinkAsync(ct);

        Assert.NotNull(link);
        Assert.Equal(1, link!.Order);
        Assert.Equal(ConfigSeeder.ChainLinks.Single(l => l.Order == 1).Endpoint, link.Endpoint);
        Assert.Equal("none", (string?)JsonNode.Parse(link.RequestOptions!)!["reasoning_effort"]);
    }

    /// <summary>
    /// A disabled row is not a link. The chain is `enabled`-filtered [D-136] and this is
    /// where that filter is asserted rather than assumed, restoring the row afterwards
    /// so the class leaves the table as it found it.
    /// </summary>
    [Fact]
    public async Task ADisabledRowIsNotALink()
    {
        var ct = TestContext.Current.CancellationToken;
        await new ConfigSeeder(TestDatabase.ConnectionString).SeedChainAsync(ct).ConfigureAwait(true);

        var client = new LocalModelClient(TestDatabase.ConnectionString, new HttpClient(new StubHandler()));

        await SetEnabledAsync(false, ct);
        try
        {
            Assert.Null(await client.LinkAsync(ct));
        }
        finally
        {
            await SetEnabledAsync(true, ct);
        }

        Assert.NotNull(await client.LinkAsync(ct));
    }

    private static async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "UPDATE local_model_config SET enabled = @e WHERE provider_order = 1;", conn);
        cmd.Parameters.AddWithValue("e", enabled);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- the seams ---

    private static async Task<LinkHealth> ProbeAsync(
        StubHandler handler, string? requestOptions, long timeoutMs = 5000,
        string timeoutKey = LocalModelClient.HealthTimeoutKey)
    {
        var client = new LocalModelClient(
            new OneLinkData(requestOptions),
            new StubConfig(timeoutMs),
            new HttpClient(handler));

        return await client.HealthAsync(timeoutKey, TestContext.Current.CancellationToken);
    }

    /// <summary>One enabled row at `provider_order` 1, without a store.</summary>
    private sealed class OneLinkData(string? requestOptions) : IStageData
    {
        public Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
            string table, string sql, CancellationToken ct = default, int? commandTimeoutSeconds = null)
        {
            Assert.Equal("local_model_config", table);

            IReadOnlyList<IReadOnlyList<object?>> rows =
                [new object?[] { 1, Endpoint, requestOptions }];

            return Task.FromResult(rows);
        }

        public Task<long> WriteAsync(
            string table, WriteOperation operation, string sql,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("C32 writes nothing [D-136].");

        public Task<long> BulkUpsertAsync(
            string table, IReadOnlyList<string> columns, IReadOnlyList<string> conflictTarget,
            Func<IBulkWriter, CancellationToken, Task> write, CancellationToken ct = default)
            => throw new NotSupportedException("C32 writes nothing [D-136].");
    }

    /// <summary>No enabled row at all.</summary>
    private sealed class NoLinkData : IStageData
    {
        public Task<IReadOnlyList<IReadOnlyList<object?>>> ReadAsync(
            string table, string sql, CancellationToken ct = default, int? commandTimeoutSeconds = null)
            => Task.FromResult<IReadOnlyList<IReadOnlyList<object?>>>([]);

        public Task<long> WriteAsync(
            string table, WriteOperation operation, string sql,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("C32 writes nothing [D-136].");

        public Task<long> BulkUpsertAsync(
            string table, IReadOnlyList<string> columns, IReadOnlyList<string> conflictTarget,
            Func<IBulkWriter, CancellationToken, Task> write, CancellationToken ct = default)
            => throw new NotSupportedException("C32 writes nothing [D-136].");
    }

    /// <summary>
    /// The two timeout keys and nothing else; anything unexpected throws. Both answer the
    /// same stub value, because what these tests separate is which key the client names
    /// in its own detail line, not what the two are set to [5.13].
    /// </summary>
    private sealed class StubConfig(long timeoutMs = 5000) : IConfigStore
    {
        public Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => string.Equals(key, LocalModelClient.HealthTimeoutKey, StringComparison.Ordinal)
               || string.Equals(key, LocalModelClient.WarmTimeoutKey, StringComparison.Ordinal)
                ? Task.FromResult<ConfigRow?>(new ConfigRow(
                    key, 1, timeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new DateOnly(2020, 1, 1)))
                : throw new InvalidOperationException(
                    $"C32 resolved '{key}', which 5.5 does not expect it to read.");

        public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
            => (await ResolveAsync(key, asOf, ct).ConfigureAwait(false))!;

        public Task<int?> ResolveVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A link does not resolve the store-wide config version.");

        public Task<int> RequireVersionAsync(DateOnly asOf, CancellationToken ct = default)
            => throw new NotSupportedException("A link does not resolve the store-wide config version.");
    }

    /// <summary>
    /// An OpenAI-compatible endpoint, fabricated. It answers the model list and the
    /// completion, and it records what it was sent so the request shape can be asserted
    /// on the body rather than on the call having succeeded, which is `EodhdClientTests`'
    /// rule one provider over.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string Content { get; set; } = "Ready.";

        public string? AnsweringModel { get; set; }

        public string[] Models { get; set; } = ["qwen3.6:latest"];

        public bool Throw { get; set; }

        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        /// <summary>5.1's measured behaviour: content only when the request asks for no reasoning.</summary>
        public bool ContentOnlyWithoutReasoning { get; set; }

        public List<string> Requests { get; } = [];

        public string? LastCompletionText { get; private set; }

        public JsonObject? LastCompletionBody
            => LastCompletionText is null ? null : JsonNode.Parse(LastCompletionText) as JsonObject;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);

            if (Throw)
            {
                throw new HttpRequestException("connection refused");
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct).ConfigureAwait(false);
            }

            if (path.EndsWith("/models", StringComparison.Ordinal))
            {
                var data = new JsonArray();
                foreach (var id in Models)
                {
                    data.Add(new JsonObject { ["id"] = id, ["object"] = "model" });
                }

                return Json(new JsonObject { ["object"] = "list", ["data"] = data });
            }

            LastCompletionText = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);

            var content = ContentOnlyWithoutReasoning
                ? (LastCompletionBody?["reasoning_effort"] is null ? string.Empty : "Ready.")
                : Content;

            // Everything a status check would read says this link is fine, whatever the
            // content is. That is the shape 5.1 measured and the reason D-144 exists.
            return Json(new JsonObject
            {
                ["model"] = AnsweringModel ?? Models.FirstOrDefault() ?? "none",
                ["choices"] = new JsonArray(
                    new JsonObject
                    {
                        ["finish_reason"] = "stop",
                        ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                    }),
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 24,
                    ["completion_tokens"] = content.Length == 0 ? 0 : 8,
                    ["total_tokens"] = content.Length == 0 ? 24 : 32,
                },
            });
        }

        private static HttpResponseMessage Json(JsonObject body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
    }
}
