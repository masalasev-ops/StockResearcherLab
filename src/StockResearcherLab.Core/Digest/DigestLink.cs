namespace StockResearcherLab.Core.Digest;

/// <summary>
/// One link in the digest chain [D-136].
///
/// **The chain is a chain rather than a primary with a fallback branch**, which is what
/// `ARCHITECTURE.html` §07 asks for and what makes failover work in both directions with
/// no rarely-executed code path. That costs one interface with two operations and buys a
/// caller in which nothing knows which link is preferred.
///
/// **Two operations and not three.** A link answers whether it is currently able to
/// answer, and it digests. It does not decide whether it should be used, which is the
/// chain's, and it does not judge what came back, which is nobody's [INVARIANT 7].
/// </summary>
public interface IDigestLink
{
    /// <summary>
    /// Which link this is, and the string that reaches `news_digest.provider`. Closed to
    /// the vocabulary by a CHECK, so a link cannot invent one [D-134].
    /// </summary>
    DigestProvider Provider { get; }

    /// <summary>
    /// Whether this link can answer now, and what it answered with.
    ///
    /// **Health is a probe that came back with something, not a status code** [D-144].
    /// A reasoning model returns HTTP 200, a normal usage block and zero characters of
    /// content, so a check reading the status would report healthy on the one link that
    /// cannot produce a digest, and every candidate would go to the paid link with the
    /// local server up and answering in under two seconds.
    /// </summary>
    Task<LinkHealth> HealthAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends one digest request and returns what came back, uninterpreted.
    ///
    /// **A link does not decide whether its own answer was good enough.** Malformed and
    /// over-long are D-137's and are the chain's to apply, so this returns an empty
    /// answer rather than throwing on one, and returns a long answer rather than cutting
    /// it. Nothing is ever truncated here or anywhere: a half-sentence digest reaches the
    /// researcher as a complete fact and the validator cannot check prose.
    /// </summary>
    Task<DigestAnswer> DigestAsync(DigestRequest request, CancellationToken ct = default);
}

/// <summary>
/// One digest call. The instruction and the articles arrive already assembled, because
/// selecting which articles to send is C33's [D-143] and the instruction is one file read
/// once [`prompts/digest-instruction.md`].
/// </summary>
/// <param name="Instruction">
/// The instruction text, identical for every link. A difference in prompt between the two
/// would confound the rotation's paired sample [D-27].
/// </param>
/// <param name="Articles">The selected article text, already inside the input cap.</param>
/// <param name="MaxOutputTokens">
/// `digest.max_output_tokens`. Requested on the call and asserted on the response by the
/// chain, because a provider that ignores it returns a long answer with a normal status
/// [D-137, D-143].
/// </param>
public sealed record DigestRequest(string Instruction, string Articles, int MaxOutputTokens);

/// <summary>
/// What a link answered with. Every field may be absent, and absent is reported rather
/// than defaulted: a completion count of zero is a real value and says the model produced
/// nothing, where null says the link did not report one [`CLAUDE.md` §6].
/// </summary>
/// <param name="Text">The digest text, or null where the link returned none.</param>
/// <param name="Model">
/// The model the provider says answered, which is what reaches `news_digest.model_name`
/// [D-29]. Null where the provider did not say.
/// </param>
/// <param name="PromptTokens">Input tokens the provider reports, for C26 [D-140].</param>
/// <param name="CompletionTokens">
/// Output tokens the provider reports. This is what D-137's over-long test is made
/// against, so a link that reports none cannot be checked.
/// </param>
/// <param name="CacheWriteTokens">
/// Tokens written to the provider's prompt cache, for `cost_ledger.cache_write_tokens`
/// [5.14]. **Null on a link with no cache rather than zero**, because zero is a link
/// that has one and wrote nothing to it, and the two price differently
/// [`CLAUDE.md` §6].
/// </param>
/// <param name="CacheReadTokens">
/// Tokens read from the provider's prompt cache, for `cost_ledger.cache_read_tokens`.
/// Null carries the same meaning as above.
/// </param>
public sealed record DigestAnswer(
    string? Text,
    string? Model,
    int? PromptTokens,
    int? CompletionTokens,
    int? CacheWriteTokens = null,
    int? CacheReadTokens = null);

/// <summary>
/// What a health probe found. Reported rather than written: `run_log` keeps one writer
/// and C32's Writes cell says nothing, so this travels back to a caller that records it
/// through C27 [D-136, INVARIANT 10].
/// </summary>
/// <param name="Provider">The link probed.</param>
/// <param name="Healthy">Whether it answered with a non-empty completion inside the timeout.</param>
/// <param name="LoadedModel">
/// The model the endpoint says answered, or null where it did not say. Null is the
/// server not naming one, never a stand-in for an unknown name [`CLAUDE.md` §6].
/// </param>
/// <param name="LatencyMs">Wall time for the probe, including a timeout that expired.</param>
/// <param name="Detail">
/// One line for the run log. On an unhealthy link it says which of the three ways it
/// failed, because "unhealthy" alone cannot separate a server that is down from one that
/// is up and returning nothing, and those call for opposite responses.
/// </param>
public sealed record LinkHealth(
    DigestProvider Provider,
    bool Healthy,
    string? LoadedModel,
    long LatencyMs,
    string Detail);
