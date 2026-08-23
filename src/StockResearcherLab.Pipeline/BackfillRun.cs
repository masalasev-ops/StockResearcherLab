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

        // **Nothing is read out of the run log to decide where this run starts** [3.6,
        // 0010]. A ticker-partitioned stage resumes on its own attempt record, written
        // as the sweep goes and stamped with the range start, so a clean halt, a
        // command timeout and a killed process all resume identically. The run log is
        // where an operator reads what happened, and it decides nothing.
        //
        // The refusal on a range mismatch went with the position it protected. A range
        // start that differs is simply a different set of attempt rows, and a range end
        // that differs no longer changes anything at all.
        // The trading calendar, declared by this driver rather than by the stages that
        // need it. One read per run, shared, so two compute stages in one backfill cannot
        // evaluate different date sets [3.14].
        var calendar = new StageData(
            _connectionString,
            new DeclaredAccess("BackfillRun", ["price_daily"], []));

        // **The range end is checked against the ingest frontier here, where the driver
        // turns it into a date set, and nowhere else** [item 60]. C07 asks this of the
        // nightly path and the backfill had no equivalent, which is a guard on one path
        // and not the other.
        //
        // **It binds the calendar rather than the run, and that is the whole placement
        // decision.** A range end past the frontier is an operator error for a compute
        // stage and the ordinary case for an ingest one: fetching the dates the store
        // does not hold yet is how the frontier moves at all, so a check at the top of
        // `RunAsync` would make the backfill unable to extend the store. The six stages
        // that resolve the calendar are exactly the six the wrong end harms, so the
        // check sits inside what only they call. An ingest stage never reaches it.
        //
        // It also means the frontier is read after whatever ran before it in a sequence,
        // rather than once at the start against a store the ingest half has since moved.
        var context = new BackfillContext(
            from, to, data, _clock, config, _allowance,
            async token =>
            {
                await PriceFrontier.RequireCoveredAsync(calendar, config, to, token).ConfigureAwait(false);
                return await TradingCalendar.SessionsAsync(calendar, from, to, token).ConfigureAwait(false);
            });

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
                Describe(from, to, result) + Retries(data), ct).ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // rows_written stays null rather than 0. A stage that threw wrote an
            // unknown number of rows and zero is a real value carrying meaning
            // [CLAUDE.md section 6].
            //
            // **run_date is the range START on a throw, not its end** [3.6]. A stage
            // that threw proved it reached its first date and nothing more, so that is
            // what the row records. The range end was the first reading and it is the
            // dangerous one: resumption takes the highest run_date for a stage, so a
            // failed run stamped with its end would resume from after everything the
            // failure skipped, and the skipped dates would never be revisited.
            //
            // The two errors are not symmetric. Resuming too early re-does work that
            // is idempotent per grain and costs time [D-68]; resuming too late leaves
            // a hole no later stage can see. So the row states the date it can prove
            // rather than the one it hoped for, and an unfiltered read is safe without
            // anyone remembering a status filter.
            //
            // **The line records no position, and a failure costs nothing extra for
            // that** [0010]. What the sweep completed is in its attempt record, written
            // as it went, so the next run re-dispatches the tickers with no attempt row
            // for this range and nothing else. A failure and a kill are the same thing
            // to it, which is what the position could never be made to be.
            var line = Range(from, to) + " FAILED. " + ex.Message + Retries(data);

            await _runLog.RecordAsync(
                from, stage.Name, "failed", startedAt,
                stopwatch.ElapsedMilliseconds, null, line, ct).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// What a stage's last range execution did, or null where it has never run one
    /// [3.6].
    ///
    /// **For an operator to read, and nothing branches on it** [0010]. Resumption is
    /// the stage's own attempt record; this is how someone sees whether last night
    /// completed, halted on the gate or failed, before spending another day.
    ///
    /// **Range rows are identified by the line opening with `range `**, which is what
    /// <see cref="Describe"/> guarantees. `run_log.error` is the only free-text column
    /// that table has, so that prefix is the only marker available without a schema
    /// change, and it is one this class controls on both sides.
    /// </summary>
    public async Task<RangeRunReport?> LastRangeRunAsync(string stageName, CancellationToken ct = default)
        => await _runLog.LastRangeRunAsync(stageName, ct).ConfigureAwait(false);

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

        if (result.WasHalted)
        {
            text += ". HALTED on the allowance gate, which is the mechanism working rather than a " +
                    "failure: everything written is kept and the next run picks up the tickers with no " +
                    "attempt row for this range [0010]";
        }

        return result.Detail is null ? text + "." : text + ". " + result.Detail;
    }

    /// <summary>
    /// The connection retry count, always stated, including its zero [D-98's rule one
    /// table over]. A retry firing constantly is a pooling problem still present, and a
    /// count that appears only when it is non-zero is one nobody can baseline.
    ///
    /// Composed here rather than by each stage because this class owns the
    /// <see cref="StageData"/> every stage was handed, so one place reports it for all
    /// of them.
    /// </summary>
    private static string Retries(StageData data)
        => string.Format(
            CultureInfo.InvariantCulture,
            " {0:N0} connection open(s) retried.", data.ConnectionRetries);

    private static string Range(DateOnly from, DateOnly to)
        => "range " + Iso(from) + ".." + Iso(to);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
