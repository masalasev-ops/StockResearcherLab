using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Data.Eodhd;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Pipeline.Select;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// Builds the one registry the whole system uses. There is a single place that
/// says which components exist, so the conformance test at 0.4 and the runner
/// read the same declaration rather than two lists that can drift apart.
/// </summary>
public static class PipelineComposition
{
    /// <param name="connectionString">Postgres.</param>
    /// <param name="apiToken">
    /// The data provider token. Null builds a registry whose provider-backed stages
    /// are present and cannot run, which is what the conformance tests want: they
    /// assert over declarations rather than execute anything, and requiring a
    /// secret to enumerate the registry would make them unrunnable in CI.
    /// </param>
    /// <param name="clock">Injected; nothing here reads system time [INVARIANT 11].</param>
    public static StageRegistry BuildRegistry(
        string connectionString, string? apiToken = null, IClock? clock = null)
        => BuildRegistry(
            connectionString,
            string.IsNullOrWhiteSpace(apiToken)
                ? null
                : new EodhdClient(EodhdClient.CreateHttpClient(), apiToken, clock ?? new SystemClock()));

    /// <summary>
    /// The same registry over a client the caller already holds.
    ///
    /// **This exists because the rate limiter is per client and the provider's limit is
    /// not** [`EodhdRateLimiter`, 1,000 requests a minute]. A backfill needs both the
    /// registry and `UnitAllowance`, which reads `/api/user` before every unit of work,
    /// so a driver that let this method build its own client would be running two
    /// independent sliding windows of 1,000 against one limit of 1,000 and would meet it
    /// as a 429 in the middle of a sweep, which is a failed stage after the units before
    /// it are spent. One client, one window.
    /// </summary>
    /// <param name="eodhd">
    /// Null builds a registry whose provider-backed stages are absent, which is the
    /// no-token case above.
    /// </param>
    public static StageRegistry BuildRegistry(string connectionString, EodhdClient? eodhd)
    {
        var owners = new List<IWriteOwner>
        {
            // Not a stage. Sits outside the layers and owns run_log.
            new RunLog(connectionString),

            // Layer 2. Every compute stage derives from tables the ingest wrote and
            // calls no provider, so all of them are registered whether or not a token
            // is present.
            new FlowEngine(),
            new IndicatorEngine(),
            new ValuationEngine(),
            new SentimentEngine(),
            new MarketContextEngine(),
            new PercentileEngine(),

            // Layer 3. Selection derives from the percentile store and calls no
            // provider either, so it registers on the same terms as layer 2.
            //
            // C12 is in this layer too. Section 04 puts it in the Select band at
            // 18:20, before C13 at 18:25, and it reads gate_result in nothing after:
            // C13 does not read that table and must not [D-117].
            new GateEngine(),
            new ScreenEngine(),
        };

        if (eodhd is not null)
        {
            owners.Add(new PriceIngestor(eodhd));
            owners.Add(new FreshnessGuard(eodhd));
            owners.Add(new FundamentalsIngestor(eodhd));
            owners.Add(new UniverseBuilder(eodhd));
            owners.Add(new SentimentIngestor(eodhd));
            owners.Add(new FlowIngestor(eodhd));
            owners.Add(new EventsIngestor(eodhd));
        }

        return new StageRegistry(owners);
    }

    /// <summary>
    /// Every stage this phase registers, whether or not a token is present, so the
    /// conformance tests see the declarations rather than only the runnable subset.
    /// A component the registry cannot see is a component INVARIANT 10 is not
    /// enforced against.
    /// </summary>
    public static IReadOnlyList<IWriteOwner> AllOwnersForConformance(string connectionString)
        => BuildRegistry(connectionString, apiToken: "not-a-real-token-registry-only").Owners;
}
