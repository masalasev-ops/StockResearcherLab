using System.Diagnostics;
using System.Globalization;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// Runs one registered stage over a range and records what happened. The range
/// counterpart of <see cref="StageRunner"/>, and deliberately its shape: the stage is
/// handed an <see cref="IStageData"/> built against its own declared sets, so the
/// guard is applied by whatever runs the stage rather than by the stage agreeing to
/// apply it to itself.
///
/// **Resolved through the registry, exactly as a nightly stage is.** A stage that is
/// not registered does not run, and there is no instance overload here that would let
/// one: the registry is the single declaration of who touches what, and a second
/// entry point that skipped it would be a second declaration [`CLAUDE.md` §5].
///
/// **Fails closed on an exception and halts cleanly on the allowance.** Those are
/// different outcomes and the run log tells them apart: a halt is the gate working,
/// keeps everything written so far, and records where it reached; a throw is a failed
/// run.
/// </summary>
public sealed class BackfillRun
{
    private readonly StageRegistry _registry;
    private readonly RunLog _runLog;
    private readonly IClock _clock;
    private readonly string _connectionString;
    private readonly IUnitAllowance _allowance;

    public BackfillRun(
        StageRegistry registry,
        RunLog runLog,
        IClock clock,
        string connectionString,
        IUnitAllowance allowance)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runLog);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(allowance);

        _registry = registry;
        _runLog = runLog;
        _clock = clock;
        _connectionString = connectionString;
        _allowance = allowance;
    }

    public async Task<BackfillResult> RunAsync(
        string stageName, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (_registry.Find(stageName) is not IStage stage)
        {
            throw new InvalidOperationException(
                $"No stage named '{stageName}' is registered. A stage that is not in the registry does " +
                "not run, deliberately: the registry is the single declaration of who touches what " +
                "[CLAUDE.md section 5].");
        }

        if (stage is not IBackfillStage backfill)
        {
            throw new InvalidOperationException(
                $"'{stageName}' is registered and does not implement IBackfillStage, so it has no range " +
                "mode. A backfill is the registered components executed over a range and no component " +
                "exists that a night does not run, so the answer is a second entry point on this class " +
                "rather than a second class [D-93].");
        }

        var access = _registry.AccessFor(stage);
        var data = new StageData(_connectionString, access);

        // Constructed here rather than by the stage, so a stage cannot resolve
        // against a range other than the one it was handed. There is no version on
        // this context at all: the only route to one is ForDateAsync, which resolves
        // the date being computed [D-43, D-93, INVARIANT 13].
        var config = new ConfigStore(_connectionString);
        var context = new BackfillContext(from, to, data, _clock, config, _allowance);

        var startedAt = _clock.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await backfill.ExecuteRangeAsync(context, ct).ConfigureAwait(false);
            stopwatch.Stop();

            // run_date is the last date the execution actually finished rather than
            // the range end, so the row answers "where did it get to" directly, which
            // is what resumption reads. For a completed range the two are the same.
            await _runLog.RecordAsync(
                result.LastDateCovered, stage.Name, result.Status, startedAt,
                stopwatch.ElapsedMilliseconds, result.RowsWritten,
                Describe(from, to, result), ct).ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // rows_written stays null rather than 0. A stage that threw wrote an
            // unknown number of rows and zero is a real value carrying meaning
            // [CLAUDE.md section 6]. run_date falls back to the range end, there
            // being no reached date to record.
            await _runLog.RecordAsync(
                to, stage.Name, "failed", startedAt,
                stopwatch.ElapsedMilliseconds, null,
                Range(from, to) + " " + ex.Message, ct).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// The line that lands in `run_log.error`, which is the only free-text column that
    /// table has and therefore where the range goes [D-93].
    ///
    /// It always opens with the range, so one pattern finds every range execution in
    /// the log whatever else the stage had to say.
    /// </summary>
    public static string Describe(DateOnly from, DateOnly to, BackfillResult result)
    {
        var text = Range(from, to) + ", reached " + Iso(result.LastDateCovered);

        if (result.Position is not null)
        {
            text += " at " + result.Position;
        }

        if (result.WasHalted)
        {
            text += ". HALTED on the allowance gate, which is the mechanism working rather than a " +
                    "failure: everything written is kept and the next run resumes from here on D-68's " +
                    "per-grain idempotence";
        }

        return result.Detail is null ? text + "." : text + ". " + result.Detail;
    }

    private static string Range(DateOnly from, DateOnly to)
        => "range " + Iso(from) + ".." + Iso(to);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
