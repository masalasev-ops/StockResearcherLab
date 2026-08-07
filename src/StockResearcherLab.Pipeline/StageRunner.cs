using System.Diagnostics;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// Runs one stage and records what happened. The stage is handed an
/// <see cref="IStageData"/> built against its own declared sets, so the guard is
/// applied by whatever runs the stage rather than by the stage agreeing to apply
/// it to itself.
///
/// Fails closed. A stage completes or it fails the run: there is no partial
/// success here, because the next stage cannot tell a short table from a real
/// one [CLAUDE.md section 6].
/// </summary>
public sealed class StageRunner
{
    private readonly StageRegistry _registry;
    private readonly RunLog _runLog;
    private readonly IClock _clock;
    private readonly string _connectionString;

    public StageRunner(StageRegistry registry, RunLog runLog, IClock clock, string connectionString)
    {
        _registry = registry;
        _runLog = runLog;
        _clock = clock;
        _connectionString = connectionString;
    }

    public async Task<StageResult> RunAsync(
        string stageName, DateOnly date, int configVersion, CancellationToken ct = default)
    {
        if (_registry.Find(stageName) is not IStage stage)
        {
            throw new InvalidOperationException(
                $"No stage named '{stageName}' is registered. A stage that is not in the registry does " +
                "not run, deliberately: the registry is the single declaration of who touches what " +
                "[CLAUDE.md section 5].");
        }

        var access = _registry.AccessFor(stage);
        var data = new StageData(_connectionString, access);

        // Config resolves as of the date being processed. Constructed here rather
        // than by the stage, so a stage cannot resolve against a date other than
        // the one it was handed [D-43, INVARIANT 13].
        var config = new ConfigStore(_connectionString);
        var context = new StageContext(date, configVersion, data, _clock, config);

        // started_at comes from the injected clock. The duration comes from a
        // stopwatch, which measures elapsed time rather than reading the time of
        // day, so it is not a second clock [INVARIANT 11].
        var startedAt = _clock.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
            stopwatch.Stop();

            await _runLog.RecordAsync(
                date, stage.Name, "ok", startedAt,
                stopwatch.ElapsedMilliseconds, result.RowsWritten, null, ct).ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // rows_written stays null rather than 0. A stage that threw wrote an
            // unknown number of rows and zero is a real value carrying meaning
            // [CLAUDE.md section 6].
            await _runLog.RecordAsync(
                date, stage.Name, "failed", startedAt,
                stopwatch.ElapsedMilliseconds, null, ex.Message, ct).ConfigureAwait(false);

            throw;
        }
    }
}
