using System.Diagnostics;
using System.Globalization;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>What the selection range run did.</summary>
/// <param name="Dates">Sessions processed.</param>
/// <param name="Gated">Rows C12 wrote.</param>
/// <param name="Candidates">Rows C14 wrote to <c>candidate_set</c>.</param>
/// <param name="Attributed">Rows C14 wrote to <c>attribution</c>.</param>
/// <param name="Alerts">Rows C28 raised.</param>
/// <param name="Elapsed">Wall clock.</param>
public sealed record SelectionPassResult(
    int Dates, long Gated, long Candidates, long Attributed, long Alerts, TimeSpan Elapsed);

/// <summary>
/// Checkpoint 4.14. C12, C14 and C28 over the backfilled range, and the point the record
/// starts.
///
/// **This is the phase's largest irreversible act.** The `attribution` rows it writes
/// carry scores frozen as they stood on the night, and no later pass may rewrite them
/// [INVARIANT 4, D-40, `RUNBOOK.md`]. Everything that could move a row's contents closed
/// upstream of it: the four readings at Q.1, the shadow registration at Q.7, and the
/// persistence measure at 4.13 that 4.14 waited on.
///
/// **C13 is deliberately not in the sequence.** 4.13 filled `screen_score_daily` over the
/// range in two passes and 4.14 reads what it left. Running C13 again would rewrite thirty
/// million rows to the same values, and pass one's upsert writes `rank_within_screen` null,
/// so a partial re-run would clear ranks that pass two had already drawn. The gate and the
/// allocator are what this checkpoint's own scope names.
///
/// **What that costs, and what is done about it.** §18's data-fault halt lives in C13, so
/// nothing would raise it over a range C13 does not run on. <see cref="RankingLiveScreens"/>
/// is the same condition read out of `screen_history` and `screen_score_daily` instead of
/// computed, and it halts the range run on exactly the dates C13 would have halted a night
/// on. Without it, a date where every live screen with a floor ranked nothing would write
/// an empty candidate set into the frozen record and look identical to the warm-up.
/// </summary>
public sealed class SelectionRangeRun
{
    private readonly string _connectionString;
    private readonly IClock _clock;
    private readonly Action<string> _say;

    /// <summary>
    /// The three components, in §03's order and without C13.
    ///
    /// C12 first because C14 joins `gate_result`, C28 last because it reads the
    /// `candidate_set` C14 has just written.
    /// </summary>
    public static readonly string[] Order = ["GateEngine", "CandidateAllocator", "ConcentrationMonitor"];

    /// <summary>
    /// **The clock is injected and reaches the run log alone** [INVARIANT 11].
    ///
    /// Every stage runs on a date this driver hands it, taken from `price_daily`, and no
    /// stage's date comes from the clock. What the clock supplies is the `started_at` on
    /// the `run_log` row, which is one of the two places in this system where a genuine
    /// instant is meant [`CLAUDE.md` §6].
    ///
    /// **The stages are run through `StageRunner` rather than invoked directly**, which is
    /// what puts a `run_log` row behind each of the 4,371 stage executions this range
    /// makes. A one-off irreversible pass over five years is the last operation that
    /// should be unlogged.
    /// </summary>
    public SelectionRangeRun(string connectionString, IClock clock, Action<string>? say = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(clock);

        _connectionString = connectionString;
        _clock = clock;
        _say = say ?? (_ => { });
    }

    /// <summary>
    /// The range, date by date.
    ///
    /// **It refuses unless `attribution` is empty over the range**, and the refusal is the
    /// whole safety of the checkpoint. A second run over a date that already has rows
    /// would be stopped by `ON CONFLICT (ticker, date) DO NOTHING` per row, which is
    /// INVARIANT 4 holding at the statement level; this stops it a level earlier and says
    /// so, because a run that silently wrote nothing for half its range and rows for the
    /// other half is the hardest state to reason about afterwards.
    ///
    /// The truncate is a separate and explicit act, not something this method does on the
    /// caller's behalf.
    /// </summary>
    public async Task<SelectionPassResult> RunAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var config = new ConfigStore(_connectionString);
        var sessions = await SessionsAsync(from, to, ct).ConfigureAwait(false);

        await EnsureAttributionEmptyAsync(from, to, ct).ConfigureAwait(false);

        var registry = PipelineComposition.BuildRegistry(_connectionString, apiToken: null, _clock);
        var runner = new StageRunner(registry, new RunLog(_connectionString), _clock, _connectionString);

        var started = Stopwatch.StartNew();
        long gated = 0, candidates = 0, attributed = 0, alerts = 0;

        for (var i = 0; i < sessions.Count; i++)
        {
            var date = sessions[i];

            await EnsureNotADataFaultAsync(config, date, ct).ConfigureAwait(false);

            // **Resolved per date and never once for the range** [INVARIANT 13, D-43]. The
            // tuner rewrites slot allocations monthly, so a version resolved as of today
            // and applied to a 2022 date stamps that date with an allocation it was not
            // selected under, and every attribution row it writes would carry it.
            var version = await config.RequireVersionAsync(date, ct).ConfigureAwait(false);

            foreach (var name in Order)
            {
                var result = await runner.RunAsync(name, date, version, ct).ConfigureAwait(false);

                switch (name)
                {
                    case "GateEngine":
                        gated += result.RowsWritten;
                        break;

                    case "CandidateAllocator":
                        candidates += result.RowsWritten;
                        attributed += AttributionRowsFrom(result.Detail);
                        break;

                    case "ConcentrationMonitor":
                        alerts += result.RowsWritten;
                        break;
                }
            }

            if (i % 100 == 0)
            {
                _say($"  select {date:yyyy-MM-dd}  {candidates:N0} candidate(s), " +
                     $"{attributed:N0} attribution row(s), {started.Elapsed:hh\\:mm\\:ss}");
            }
        }

        return new SelectionPassResult(
            sessions.Count, gated, candidates, attributed, alerts, started.Elapsed);
    }

    /// <summary>
    /// **The record must not already have started.**
    ///
    /// `RUNBOOK.md` forbids re-running the attribution write, so a range run over dates
    /// that already carry rows is the operation the prohibition names. Refusing here
    /// rather than relying on the per-row `ON CONFLICT` is what keeps the two states
    /// distinguishable: a refused run wrote nothing anywhere.
    /// </summary>
    private async Task EnsureAttributionEmptyAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var data = new StageData(
            _connectionString, new DeclaredAccess("SelectionRangeRun", ["attribution"], []));

        var rows = await data.ReadAsync(
            "attribution",
            "SELECT count(*)::bigint, min(date), max(date) FROM attribution WHERE date BETWEEN " +
            Literal(from) + " AND " + Literal(to) + ";", ct).ConfigureAwait(false);

        var standing = Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture);

        if (standing > 0)
        {
            throw new InvalidOperationException(
                "attribution already holds " + standing.ToString(CultureInfo.InvariantCulture) +
                " row(s) between " + Literal(from) + " and " + Literal(to) +
                ", first " + rows[0][1] + " and last " + rows[0][2] +
                ". The attribution write is never re-run: those rows carry scores frozen when " +
                "they were first written, and a second pass applies today's screen definitions " +
                "to a past date [INVARIANT 4, D-40, RUNBOOK.md]. If this range is genuinely " +
                "meant to start over, the truncate is an explicit and separate act.");
        }
    }

    /// <summary>
    /// §18's halt, read rather than recomputed.
    ///
    /// **Every live screen with a floor ranking nothing is a data fault; every live screen
    /// having no floor yet is the warm-up.** The two are identical downstream, both being
    /// an empty candidate set, and §18 gives them opposite behaviour. C13 raises this on a
    /// night and does not run here, so the range run raises it instead, on the same
    /// condition and with the same message [4.12, D-84].
    ///
    /// A shadow is not counted either way, since it surfaces no `candidate_set` row
    /// whatever it scores [D-84, D-85].
    /// </summary>
    private async Task EnsureNotADataFaultAsync(IConfigStore config, DateOnly date, CancellationToken ct)
    {
        var live = (await ScreenRegistry.LoadLiveAsync(config, date, ct).ConfigureAwait(false))
            .Select(s => s.ScreenId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        if (live.Count == 0)
        {
            return;
        }

        var (withFloor, ranking) = await RankingLiveScreens(live, date, ct).ConfigureAwait(false);

        if (withFloor > 0 && ranking == 0)
        {
            throw new InvalidOperationException(
                "Every live screen with a floor ranked nothing on " +
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                ". Section 18 halts here rather than continuing, because a floor is the 98th " +
                "percentile of a screen's own trailing distribution and roughly two percent of " +
                "the scored population clears it in the ordinary course, so nothing clearing it " +
                "anywhere indicates a data fault rather than a quiet market [D-84]. A screen " +
                "still below its lookback has no floor and is not counted. " +
                withFloor.ToString(CultureInfo.InvariantCulture) + " of " +
                live.Count.ToString(CultureInfo.InvariantCulture) +
                " live screen(s) had a floor on this date and none of them ranked anything.");
        }
    }

    /// <summary>
    /// How many live screens had a floor on the date, and how many of those ranked
    /// anything.
    ///
    /// **A floor is read off `screen_history` rather than inferred from the presence of a
    /// rank.** Inferring it would make the condition circular: a screen that ranked
    /// nothing would read as a screen with no floor, and the halt could never fire.
    /// </summary>
    private async Task<(int WithFloor, int Ranking)> RankingLiveScreens(
        IReadOnlyList<string> live, DateOnly date, CancellationToken ct)
    {
        var data = new StageData(
            _connectionString,
            new DeclaredAccess("SelectionRangeRun", ["screen_history", "screen_score_daily"], []));

        var ids = string.Join(", ", live.Select(Quote));

        var rows = await data.ReadAsync(
            "screen_history",
            $"""
            SELECT
                count(*) FILTER (WHERE h.floor_score IS NOT NULL)::int AS with_floor,
                count(*) FILTER (WHERE h.floor_score IS NOT NULL AND r.ranked > 0)::int AS ranking
            FROM screen_history h
            LEFT JOIN LATERAL (
                SELECT count(*)::bigint AS ranked
                FROM screen_score_daily s
                WHERE s.screen_id = h.screen_id AND s.date = h.date
                  AND s.rank_within_screen IS NOT NULL
            ) r ON TRUE
            WHERE h.date = {Literal(date)} AND h.screen_id IN ({ids});
            """, ct).ConfigureAwait(false);

        return (Convert.ToInt32(rows[0][0], CultureInfo.InvariantCulture),
                Convert.ToInt32(rows[0][1], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// C14 reports its two write counts in one line and <see cref="StageResult.RowsWritten"/>
    /// carries the `candidate_set` half, so the attribution half is read off the detail.
    ///
    /// **A shape that stops matching returns zero rather than throwing**, because the
    /// total is a reported figure and not a control decision, and a range run that failed
    /// on a changed log line would be worse than one that reported a zero the store can
    /// contradict. The store is what the recorded figure is taken from either way.
    /// </summary>
    private static long AttributionRowsFrom(string? detail)
    {
        if (detail is null)
        {
            return 0;
        }

        const string marker = " attribution rows written";

        var at = detail.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return 0;
        }

        var start = detail.LastIndexOf(' ', at - 1) + 1;

        return long.TryParse(
            detail[start..at], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? count
            : 0;
    }

    private async Task<IReadOnlyList<DateOnly>> SessionsAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var calendar = new StageData(
            _connectionString,
            new DeclaredAccess("SelectionRangeRun", ["price_daily"], []));

        var sessions = await TradingCalendar.SessionsAsync(calendar, from, to, ct).ConfigureAwait(false);

        if (sessions.Count == 0)
        {
            throw new InvalidOperationException(
                "price_daily holds no session between " + Literal(from) + " and " + Literal(to) +
                ". A range run over no date would write nothing and report success.");
        }

        return sessions;
    }

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";

    private static string Quote(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
