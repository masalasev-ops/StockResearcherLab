using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// The stage that proves the rails [0.7]. It declares a read set, declares no
/// writes, does nothing meaningful, and returns zero rows written.
///
/// Zero rows is a legitimate result and not an error on its own. It reads
/// run_log because a stage that declares nothing would not exercise the guard at
/// all, and the point of running this one is that the whole path holds:
/// declaration, guard, execution, run_log row, viewer.
/// </summary>
public sealed class NoOpStage : IStage
{
    public string Name => "NoOpStage";

    public IReadOnlyList<string> ReadSet { get; } = ["run_log"];

    public IReadOnlyList<TableWrite> WriteSet { get; } = [];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        // Reads through the declared route. Not because the count is wanted, but
        // because taking the route is what is being proved.
        var rows = await context.Data
            .ReadAsync("run_log", "SELECT count(*) FROM run_log;", ct)
            .ConfigureAwait(false);

        _ = rows;
        return StageResult.None;
    }
}
