using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Data.Eodhd;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Pipeline.Ingest;

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
    {
        var owners = new List<IWriteOwner>
        {
            // Not a stage. Sits outside the layers and owns run_log.
            new RunLog(connectionString),

            // Layer 2. Derives from two tables the ingest wrote and calls nothing,
            // so it is registered whether or not a token is present.
            new FlowEngine(),
        };

        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            var http = EodhdClient.CreateHttpClient();
            var eodhd = new EodhdClient(http, apiToken, clock ?? new SystemClock());

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
