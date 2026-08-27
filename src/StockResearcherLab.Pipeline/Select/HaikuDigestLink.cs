using System.Diagnostics;
using System.Globalization;
using Anthropic;
using Anthropic.Models.Messages;
using StockResearcherLab.Core.Digest;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>
/// The chain's secondary link, Haiku 4.5 over the Messages API [D-140, 5.6].
///
/// **It reads no store and is therefore not a registry component.** C32 owns a
/// declaration on `local_model_config` because it has an address to look up; this link's
/// address is the provider's and its model id arrives from the caller, resolved as of the
/// simulated date [INVARIANT 13]. A component with no read set and no write set has
/// nothing for the registry to own.
///
/// **The instruction is the caller's and is byte-identical to the primary's** [D-27]. A
/// difference in prompt between the two links would confound the rotation's paired
/// sample, which is the one measurement this link exists to make possible, so the
/// instruction arrives in the request rather than being read here.
///
/// **It judges nothing** [INVARIANT 7, D-137]. An empty answer comes back empty and a
/// long answer comes back long. Malformed and over-long are the chain's tests.
/// </summary>
public sealed class HaikuDigestLink : IDigestLink
{
    /// <summary>
    /// The probe, identical in shape to C32's so the two links are asked the same
    /// question. Short on purpose: this is the paid link and a health check is not a
    /// place to spend.
    /// </summary>
    public const string ProbeText = LocalModelClient.ProbeText;

    /// <summary>See <see cref="ProbeText"/>. Matches C32 for the same reason.</summary>
    public const int ProbeMaxTokens = LocalModelClient.ProbeMaxTokens;

    private readonly AnthropicClient _client;
    private readonly string _modelId;

    /// <param name="apiKey">From `Anthropic:ApiKey`, never logged and never in a message [D-55].</param>
    /// <param name="modelId">
    /// `digest.secondary_model_id`, resolved by the caller as of the run's date. **Not
    /// read here and not defaulted here**: a literal in this class would be a second
    /// place the model is chosen, and the config version on an attribution row would then
    /// fail to explain a change in what answered [`CLAUDE.md` §8, INVARIANT 13].
    /// </param>
    /// <param name="http">
    /// Optional, and supplied by tests alone. The SDK owns connection handling in
    /// production; injecting a handler is how this link's response mapping is asserted
    /// without a paid call, which is the same seam C32's tests use.
    /// </param>
    public HaikuDigestLink(string apiKey, string modelId, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        _modelId = modelId;
        _client = http is null
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, HttpClient = http };
    }

    public DigestProvider Provider => DigestProvider.Haiku;

    /// <summary>
    /// Whether the provider answers with content, asked the same way C32 asks it.
    ///
    /// **Health is characters of content and not a status code** [D-144]. The reason is
    /// C32's and it applies here for a different cause: a request that is rejected for
    /// credit, for a bad key or for a model id the account cannot reach all fail in ways
    /// an operator resolves differently, and the detail line separates them by carrying
    /// the provider's own message.
    ///
    /// **This costs money, and it is only ever reached when the primary has failed.**
    /// The chain probes in order and stops at the first healthy link [D-136], so on a
    /// night the local model answers, this method is not called.
    /// </summary>
    public async Task<LinkHealth> HealthAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var answer = await CompleteAsync(ProbeText, null, ProbeMaxTokens, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(answer.Text))
            {
                return Unhealthy(stopwatch, $"{answer.Model ?? _modelId} answered with no content");
            }

            return new LinkHealth(
                DigestProvider.Haiku,
                true,
                answer.Model ?? _modelId,
                stopwatch.ElapsedMilliseconds,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{answer.Model ?? _modelId} answered in {stopwatch.ElapsedMilliseconds:N0} ms " +
                    $"with {answer.Text!.Trim().Length:N0} character(s)"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // **The provider's own message reaches the run log** and is not replaced by a
            // category of this class's choosing. An expired key, an exhausted balance and
            // a model the account cannot reach are three different mornings for an
            // operator, and only the provider knows which one happened.
            return Unhealthy(stopwatch, $"the provider refused the probe: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<DigestAnswer> DigestAsync(DigestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CompleteAsync(request.Articles, request.Instruction, request.MaxOutputTokens, ct);
    }

    /// <summary>
    /// One call, and the mapping from the provider's four token counts onto the four
    /// `cost_ledger` carries [5.14]. **The cache counts are reported as sent**, including
    /// zero, because this link has a cache and zero says nothing was written to it, which
    /// is a fact rather than an absence [`CLAUDE.md` §6].
    /// </summary>
    private async Task<DigestAnswer> CompleteAsync(
        string user, string? system, int maxTokens, CancellationToken ct)
    {
        var parameters = system is null
            ? new MessageCreateParams
            {
                Model = _modelId,
                MaxTokens = maxTokens,
                Messages = [new() { Role = Role.User, Content = user }],
            }
            : new MessageCreateParams
            {
                Model = _modelId,
                MaxTokens = maxTokens,
                System = system,
                Messages = [new() { Role = Role.User, Content = user }],
            };

        var response = await _client.Messages.Create(parameters, ct).ConfigureAwait(false);

        var text = string.Concat(
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(b => b.Text));

        return new DigestAnswer(
            string.IsNullOrEmpty(text) ? null : text,
            response.Model,
            (int?)response.Usage?.InputTokens,
            (int?)response.Usage?.OutputTokens,
            (int?)response.Usage?.CacheCreationInputTokens,
            (int?)response.Usage?.CacheReadInputTokens);
    }

    private static LinkHealth Unhealthy(Stopwatch stopwatch, string detail)
        => new(DigestProvider.Haiku, false, null, stopwatch.ElapsedMilliseconds, detail);
}
