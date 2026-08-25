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
}

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
