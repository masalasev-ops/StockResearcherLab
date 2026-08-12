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

        // Resumed only from a halt [3.6]. A completed execution has nothing left, and a
        // failed one records no position because it cannot prove one: both start over,
        // which is safe because every write is idempotent on its own grain [D-68].
        var last = await _runLog.LastRangeRunAsync(stage.Name, ct).ConfigureAwait(false);

        // **A resume point belongs to the range that produced it** [item 22]. The
        // position is a ticker and the pool it indexes into is decided by the range, so
        // a position taken from a narrower range resumes into a wider pool and skips
        // every name the narrow one did not contain. Found as a test fixture's halt
        // standing in front of a real sweep, which would have completed and reported a
        // plausible count over a partial load.
        //
        // **A mismatch refuses rather than starting over**, which is the half that is
        // not obvious. `to` defaults to today, so a sweep halted on day one and
        // re-invoked on day two carries a different range: falling through to a fresh
        // start is idempotent and therefore not corrupt, and it burns a day of
        // allowance re-doing finished work and never reaches the end. Refusing makes
        // both the fixture row and the midnight rollover loud, and the operator either
        // passes the recorded range explicitly or clears the row deliberately.
        if (last is { } row && row.WasHalted && !row.CoversRange(from, to))
        {
            throw new InvalidOperationException(
                $"'{stage.Name}' has a halted range run recorded over {row.RecordedRange} and this run " +
                $"asks for {Range(from, to)[6..]}. A resume point belongs to the range that produced it: " +
                "the position is a ticker and which pool it indexes into is decided by the range, so " +
                "resuming a wider sweep from a narrower one's position skips every name the narrow one " +
                $"did not contain. run_log row {row.RunLogId.ToString(CultureInfo.InvariantCulture)}" +
                (row.Position is null ? "" : $", halted at {row.Position}") +
                ". Either pass the recorded range explicitly and resume it, or delete that row " +
                "deliberately and start over. Starting over is not done for you, because `to` defaults " +
                "to today and a sweep re-invoked the next day would silently restart and never finish.");
        }

        var resumeFrom = last is { } point && point.WasHalted ? point.Position : null;

        var context = new BackfillContext(from, to, data, _clock, config, _allowance, resumeFrom);

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
            // a hole no later stage can see. So the row states the position it can
            // prove rather than the one it hoped for, and an unfiltered read is safe
            // without anyone remembering a status filter.
            await _runLog.RecordAsync(
                from, stage.Name, "failed", startedAt,
                stopwatch.ElapsedMilliseconds, null,
                Range(from, to) + " " + ex.Message, ct).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Where a stage's next range execution should pick up, or null where it has never
    /// run one [3.6].
    ///
    /// **The highest `run_date` among that stage's range rows, unfiltered by status.**
    /// A halted row carries the date it reached and a failed row carries its range
    /// start, so the maximum is the furthest point any execution can prove it got to.
    /// Filtering on status would be a second rule the caller has to remember, and the
    /// row already says what it can prove.
    ///
    /// **Range rows are identified by the line opening with `range `**, which is what
    /// <see cref="Describe"/> guarantees. `run_log.error` is the only free-text column
    /// that table has, so that prefix is the only marker available without a schema
    /// change, and it is one this class controls on both sides.
    /// </summary>
    public async Task<ResumePoint?> ResumeFromAsync(string stageName, CancellationToken ct = default)
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
