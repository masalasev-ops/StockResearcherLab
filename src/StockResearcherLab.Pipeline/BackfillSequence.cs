using System.Globalization;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline;

/// <summary>What one source did inside a sequence run.</summary>
/// <param name="Outcome">
/// One of <c>ok</c>, <c>covered</c>, <c>halted</c>, <c>failed</c>, <c>wrote nothing</c>,
/// <c>not registered</c>, <c>no range mode</c>, <c>not reached</c>.
/// </param>
public sealed record BackfillStep(string Stage, string Outcome, long? RowsWritten, string? Detail);

/// <param name="Completed">True when every source in the order ran and none of them stopped it.</param>
/// <param name="HaltedOnAllowance">
/// True when what stopped the sequence was the allowance gate. It is the difference
/// between "run this again after the provider's day rolls over" and "something is
/// wrong", which is why the two do not share an exit code.
/// </param>
public sealed record BackfillSequenceResult(
    DateOnly From,
    DateOnly To,
    bool Completed,
    bool HaltedOnAllowance,
    IReadOnlyList<BackfillStep> Steps)
{
    /// <summary>0 completed, 2 halted on the allowance gate, 1 anything else [3.16].</summary>
    public int ExitCode => Completed ? 0 : HaltedOnAllowance ? 2 : 1;

    public string Summary()
    {
        var head = Completed
            ? "completed"
            : HaltedOnAllowance ? "halted on the allowance gate" : "stopped";

        var rows = Steps.Sum(s => s.RowsWritten ?? 0);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} over {1}..{2}, {3} source(s), {4:N0} row(s)",
            head, Iso(From), Iso(To), Steps.Count, rows);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// The whole backfill: every source over one range, in order [3.16].
///
/// The range counterpart of <see cref="NightlyRun"/> and deliberately its shape. Each
/// source is run through <see cref="BackfillRun"/>, which is the same single-stage
/// entry point <c>Worker backfill &lt;stage&gt;</c> uses, so a sequence run and twelve
/// hand-issued commands do the same work and record the same rows.
///
/// **Parallelism lives inside a stage and never across stages** [`CLAUDE.md` §5]. Each
/// source finishes before the next one reads what it wrote, and there is no
/// cross-source concurrency here to add.
///
/// **Resumption is not this class's** [D-99, 0010]. Every source resumes on its own
/// attempt record, so re-issuing the same command after an interruption walks the same
/// order and each source picks up where it was. What this class contributes to that is
/// falling through the sources that are already done, which is what
/// <see cref="BackfillResult.Covered"/> exists for.
/// </summary>
public sealed class BackfillSequence
{
    /// <summary>
    /// The sources in dependency order. It is `ARCHITECTURE.html` §4's evening order with
    /// two differences, and both are properties of a range rather than preferences.
    ///
    /// **C07 FreshnessGuard is absent.** It decides which stored date tonight may use by
    /// reading the bulk feed, and a range's dates come from the exchange calendar over
    /// the window instead [3.14]. It has no range mode and is not owed one.
    ///
    /// **C01 UniverseBuilder is here, where the night does not run it at all.** It is
    /// weekly and driven separately live; over a range its evaluation dates are inside
    /// the window, so the range form has to run it. It sits after the ingest because
    /// membership is decided from `price_daily` and from the sector C03 now carries
    /// [D-92, 3.7], and before the compute layer because every compute stage reads
    /// `security_daily` [3.12].
    ///
    /// The rest is the evening order unchanged, including C10 after C08 because breadth
    /// reads `indicator_daily`, and C11 last because it ranks what the four above wrote.
    /// </summary>
    public static readonly string[] SourceOrder =
    [
        "PriceIngestor",        // C02, whole history per ticker [3.6]
        "FundamentalsIngestor", // C03, one full pool sweep, carrying sector [3.7]
        "FlowIngestor",         // C05, one universe sweep over form4 [3.9]
        "EventsIngestor",       // C06, splits and dividends, earnings deliberately not [3.10]
        "SentimentIngestor",    // C04, one pass at window width [3.8]
        "UniverseBuilder",      // C01, per evaluation date into security_daily [3.11]
        "FlowEngine",           // C34 [3.14]
        "IndicatorEngine",      // C08 [3.13]
        "ValuationEngine",      // C09 [3.13]
        "SentimentEngine",      // C35 [3.14]
        "MarketContextEngine",  // C10, after C08 because breadth reads indicator_daily [3.14]
        "PercentileEngine",     // C11, last, because it ranks what the four above wrote [3.15]
    ];

    /// <summary>
    /// The selection layer, for a range run over a store the sources have already
    /// filled [4.12, 4.13].
    ///
    /// **A second constant rather than a slice of the first**, and the two orders are
    /// about different things rather than two halves of one. `SourceOrder` sweeps a range
    /// per source, each stage implementing <c>IBackfillStage</c> and covering the whole
    /// window in one pass. These four have no range mode and never will: a screen's floor
    /// is the 98th percentile of its own trailing distribution, so a date has to be scored
    /// before the next date's floor can be drawn, and a range run over them is a loop over
    /// dates rather than a sweep [D-9, D-115].
    ///
    /// It is the tail of <see cref="NightlyRun.EveningOrder"/>, and a test holds the two
    /// against each other so neither can drift.
    /// </summary>
    public static readonly string[] SelectionOrder =
    [
        "GateEngine",           // C12 [4.8]
        "ScreenEngine",         // C13 [4.4, 4.5]
        "CandidateAllocator",   // C14 [4.9, 4.10]
        "ConcentrationMonitor", // C28 [4.11]
    ];

    private readonly StageRegistry _registry;
    private readonly BackfillRun _run;
    private readonly Action<string> _say;

    public BackfillSequence(StageRegistry registry, BackfillRun run, Action<string>? say = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(run);

        _registry = registry;
        _run = run;
        _say = say ?? (_ => { });
    }

    public async Task<BackfillSequenceResult> ExecuteAsync(
        DateOnly from, DateOnly to, IReadOnlyList<string>? order = null, CancellationToken ct = default)
    {
        var sequence = order ?? SourceOrder;
        var steps = new List<BackfillStep>();

        foreach (var name in sequence)
        {
            // **A source the registry does not have stops the sequence, where a night
            // reports it and carries on.** The two orders are not in the same state:
            // `NightlyRun.EveningOrder` was written naming stages later checkpoints
            // would build, so an absence there is expected and saying so is the whole
            // job. Every name below is built and registered, so the only way one is
            // absent is a registry built without a provider token, and then the compute
            // layer would run over an ingest that never called anything and write a
            // store nothing produced [`CLAUDE.md` §1].
            if (_registry.Find(name) is not IStage stage)
            {
                steps.Add(new BackfillStep(name, "not registered", null,
                    "No such stage. Every source in this order is built, so an absence here is a " +
                    "registry with no provider token rather than an unbuilt component [D-55]."));
                _say($"  {name,-22} not registered, stopping");

                return Stopped(from, to, steps, sequence, name);
            }

            if (stage is not IBackfillStage)
            {
                steps.Add(new BackfillStep(name, "no range mode", null,
                    "Registered and does not implement IBackfillStage [D-93]."));
                _say($"  {name,-22} no range mode, stopping");

                return Stopped(from, to, steps, sequence, name);
            }

            BackfillResult result;
            try
            {
                result = await _run.RunAsync(name, from, to, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Fails closed. `BackfillRun` has already recorded the failed row and
                // rethrown; what stops here is every source after this one, because each
                // of them reads what this one writes.
                steps.Add(new BackfillStep(name, "failed", null, ex.Message));
                _say($"  {name,-22} FAILED, stopping: {ex.Message}");

                return Stopped(from, to, steps, sequence, name);
            }

            steps.Add(new BackfillStep(name, result.Status, result.RowsWritten, result.Detail));
            _say($"  {name,-22} {result.Status}, {result.RowsWritten:N0} row(s)");

            // **A halt stops the sequence, and it is not a failure.** The gate stopped
            // this source part way through its pool, so everything after it would derive
            // from a store missing rows the next run will fetch. The operator runs the
            // same command again once the provider's day has rolled over, and every
            // source before this one falls through as covered.
            if (result.WasHalted)
            {
                _say($"  {name,-22} halted on the allowance gate, stopping");
                return new BackfillSequenceResult(from, to, false, true, Pad(steps, sequence, name));
            }

            // The zero-row halt, carried over from `NightlyRun` and keyed on the status
            // rather than on the count. A source that had nothing left to do reports
            // `covered` and is not a short table; a source that had work and wrote no
            // row is one, and the next source cannot tell the difference from the table
            // [`RUNBOOK.md` failure table].
            if (stage.WriteSet.Count > 0 && result.RowsWritten == 0 && !result.WasCovered)
            {
                steps.Add(new BackfillStep(name, "wrote nothing", 0,
                    "A source that writes produced no rows and did not report the range already " +
                    "covered. A partial backfill is worse than none, because every source after " +
                    "this one derives from what it wrote."));
                _say($"  {name,-22} wrote nothing, stopping");

                return Stopped(from, to, steps, sequence, name);
            }
        }

        return new BackfillSequenceResult(from, to, true, false, steps);
    }

    private static BackfillSequenceResult Stopped(
        DateOnly from, DateOnly to, List<BackfillStep> steps, IReadOnlyList<string> sequence, string stoppedAt)
        => new(from, to, false, false, Pad(steps, sequence, stoppedAt));

    /// <summary>
    /// The sources after the one that stopped the run, recorded as not reached.
    ///
    /// **A source missing from the steps and a source that ran and did nothing read the
    /// same way at a glance**, and over twelve sources that is how a short backfill gets
    /// read as a complete one. The row is what the difference costs.
    /// </summary>
    private static IReadOnlyList<BackfillStep> Pad(
        List<BackfillStep> steps, IReadOnlyList<string> sequence, string stoppedAt)
    {
        var after = sequence
            .SkipWhile(n => !string.Equals(n, stoppedAt, StringComparison.Ordinal))
            .Skip(1);

        foreach (var name in after)
        {
            steps.Add(new BackfillStep(name, "not reached", null,
                "The sequence stopped at " + stoppedAt + " before this source ran."));
        }

        return steps;
    }
}
