using System.Globalization;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;

namespace StockResearcherLab.Pipeline;

/// <summary>What one stage did inside a night.</summary>
/// <param name="Stage">Component name.</param>
/// <param name="Outcome">One of <c>ok</c>, <c>alert</c>, <c>not registered</c>, <c>halted</c>, <c>failed</c>, <c>skipped</c>.</param>
public sealed record NightlyStep(string Stage, string Outcome, long? RowsWritten, string? Detail);

/// <param name="TradingDate">What the guard blessed, or null when it found nothing usable.</param>
/// <param name="Completed">True when every registered stage in the order ran.</param>
public sealed record NightlyRunResult(
    DateOnly RunDate, DateOnly? TradingDate, bool Completed, IReadOnlyList<NightlyStep> Steps)
{
    public string Summary()
    {
        var head = Completed ? "completed" : "halted";
        var date = TradingDate is DateOnly d
            ? " on " + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : " with no usable trading date";

        return head + date + ", " + Steps.Count.ToString(CultureInfo.InvariantCulture) + " step(s)";
    }
}

/// <summary>
/// The nightly sequence. Stages in declared order, each finishing before the next
/// reads what it wrote.
///
/// **Parallelism lives inside a stage and never across stages** [CLAUDE.md section
/// 5]. The freshness guard and the zero-row halt both depend on a stage being
/// finished before the next one begins, and a partial run is worse than no run.
///
/// **The order is declared once, in full, including stages later checkpoints
/// build.** A stage the registry does not have is reported as not registered rather
/// than skipped silently, so a night that ran short says so. At sign-off nothing in
/// the order should be absent.
/// </summary>
public sealed class NightlyRun
{
    /// <summary>
    /// The evening order from `ARCHITECTURE.html` section 4 and `RUNBOOK.md`.
    ///
    /// C01 UniverseBuilder is not here: it runs weekly on Sunday rather than nightly
    /// and is driven separately.
    /// </summary>
    public static readonly string[] EveningOrder =
    [
        "PriceIngestor",        // C02 17:30, the bulk feed
        "FreshnessGuard",       // C07 17:40, decides the date everything after uses
        "FundamentalsIngestor", // C03 17:45
        "FlowIngestor",         // C05 17:45
        "EventsIngestor",       // C06 17:45
        "SentimentIngestor",    // C04 18:00
        "FlowEngine",           // C34 18:05
        "IndicatorEngine",      // C08 18:05
        "ValuationEngine",      // C09 18:05
        "SentimentEngine",      // C35 18:05
        "MarketContextEngine",  // C10 18:05, after C08 because breadth reads indicator_daily
        "PercentileEngine",     // C11 18:15, last, because it ranks what the four above wrote

        // Selection. C12 before C13 because section 04 puts it there, and C13 does not
        // read gate_result in any case and must not [D-117]. C14 after both. C28 last,
        // at 19:00, outside the layers.
        "GateEngine",           // C12 18:20
        "ScreenEngine",         // C13 18:25
        "CandidateAllocator",   // C14 18:30
        "ConcentrationMonitor", // C28 19:00
    ];

    /// <summary>
    /// The selection half alone, for a range run that has already computed everything
    /// upstream of it. It lives on <see cref="BackfillSequence"/>, beside the source
    /// order it is the counterpart of, and is named here so a reader of the evening order
    /// finds it [4.12, 4.13].
    /// </summary>
    public static IReadOnlyList<string> SelectionOrder => BackfillSequence.SelectionOrder;

    private readonly StageRegistry _registry;
    private readonly StageRunner _runner;
    private readonly Action<string> _say;

    public NightlyRun(StageRegistry registry, StageRunner runner, Action<string>? say = null)
    {
        _registry = registry;
        _runner = runner;
        _say = say ?? (_ => { });
    }

    public async Task<NightlyRunResult> ExecuteAsync(
        DateOnly runDate, int configVersion, IReadOnlyList<string>? order = null,
        CancellationToken ct = default)
    {
        var sequence = order ?? EveningOrder;
        var steps = new List<NightlyStep>();

        // Until the guard has spoken, stages run on the date the run was started
        // for. After it, they run on the date it blessed.
        var date = runDate;
        DateOnly? trading = null;

        foreach (var name in sequence)
        {
            if (_registry.Find(name) is not IStage stage)
            {
                steps.Add(new NightlyStep(name, "not registered", null,
                    "No such stage. Later checkpoints build it."));
                _say($"  {name,-22} not registered");
                continue;
            }

            StageResult result;
            try
            {
                result = await _runner.RunAsync(name, date, configVersion, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Fails closed. The runner has already recorded the failure against
                // this stage; what stops here is everything after it.
                steps.Add(new NightlyStep(name, "failed", null, ex.Message));
                _say($"  {name,-22} FAILED, halting: {ex.Message}");

                return new NightlyRunResult(runDate, trading, false, steps);
            }

            if (result.TradingDate is DateOnly blessed)
            {
                trading = blessed;
                date = blessed;
                _say($"  {name,-22} {result.Status}, trading date {blessed:yyyy-MM-dd}");
            }
            else
            {
                _say($"  {name,-22} {result.Status}, {result.RowsWritten:N0} row(s)");
            }

            steps.Add(new NightlyStep(name, result.Status, result.RowsWritten, result.Detail));

            // The zero-row halt. It keys on a stage that writes having written
            // nothing, not on the count alone: zero rows is a legitimate result for
            // a stage that declares no writes, which is what the guard is
            // [RUNBOOK failure table].
            //
            // **And on the stage not having said its own zero was expected.** Section
            // 18 gives C14 and C28 an exception to this rule in the same table that
            // states it: an unfillable slot is left empty, and a night where both
            // diversity guarantees held raises no alert. The exception comes from the
            // stage, which is the only thing that can say why its zero was correct,
            // rather than from a list of stage names here that would go stale [4.12].
            if (stage.WriteSet.Count > 0 && result.RowsWritten == 0 && !result.ZeroRowsExpected)
            {
                steps.Add(new NightlyStep(name, "halted", 0,
                    "A stage that writes produced no rows. A partial run is worse than none, because " +
                    "the next stage cannot tell a short table from a real one."));
                _say($"  {name,-22} wrote nothing, halting");

                return new NightlyRunResult(runDate, trading, false, steps);
            }
        }

        return new NightlyRunResult(runDate, trading, true, steps);
    }

    /// <summary>Builds the sequence against a real database and registry.</summary>
    public static NightlyRun For(
        string connectionString, string? apiToken, IClock clock, Action<string>? say = null)
    {
        var registry = PipelineComposition.BuildRegistry(connectionString, apiToken, clock);
        var runLog = new RunLog(connectionString);
        var runner = new StageRunner(registry, runLog, clock, connectionString);

        return new NightlyRun(registry, runner, say);
    }
}
