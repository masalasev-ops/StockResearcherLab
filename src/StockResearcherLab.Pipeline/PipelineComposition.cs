using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// Builds the one registry the whole system uses. There is a single place that
/// says which components exist, so the conformance test at 0.4 and the runner
/// read the same declaration rather than two lists that can drift apart.
/// </summary>
public static class PipelineComposition
{
    public static StageRegistry BuildRegistry(string connectionString)
    {
        var owners = new List<IWriteOwner>
        {
            // Not a stage. Sits outside the layers and owns run_log.
            new RunLog(connectionString),

            // The no-op stage that proves the rails [0.7].
            new NoOpStage(),
        };

        return new StageRegistry(owners);
    }
}
