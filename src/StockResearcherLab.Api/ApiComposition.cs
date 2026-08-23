using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Api;

/// <summary>
/// The read owners this project hosts, in one place, so the conformance test sees the
/// declarations rather than a list of its own [D-109].
///
/// **This is `PipelineComposition.AllOwnersForConformance`'s counterpart and exists for
/// its reason.** A component the registry cannot see is a component the invariant is not
/// enforced against; a reader nothing enumerates is a reader D-74 is not enforced
/// against. The pipeline registry cannot hold these, because the Api never references
/// Pipeline and the reference exists in the other direction only from the test project.
///
/// It takes a connection string and opens nothing. Enumerating the readers must make no
/// connection, exactly as building the registry makes no provider call.
/// </summary>
public static class ApiComposition
{
    public static IReadOnlyList<IReadOwner> AllReadOwnersForConformance(string connectionString)
        => [new RecordInspector(connectionString)];
}
