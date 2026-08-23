using System.Diagnostics;
using System.Globalization;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Data;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>One pass of the range run, and what it did.</summary>
/// <param name="Pass">`score` or `floor`.</param>
/// <param name="Dates">Sessions processed.</param>
/// <param name="Rows">Rows the pass wrote.</param>
/// <param name="Elapsed">Wall clock, so it can be compared against C11's measured pass.</param>
/// <param name="Detail">One line worth reading.</param>
public sealed record ScreenPassResult(
    string Pass, int Dates, long Rows, TimeSpan Elapsed, string Detail);

/// <summary>
/// Checkpoint 4.13. The two-pass range run over `screen_score_daily`.
///
/// **Pass one loops dates writing scores with no dependency on any other date.** A
/// screen's score on a date is a function of that date's percentiles and the screen's
/// definition, and of nothing else, so the pass is embarrassingly date-partitioned
/// [`CLAUDE.md` §5].
///
/// **Pass two writes floors and ranks, and refuses to run unless pass one is complete.**
/// A floor is the 98th percentile of a screen's own trailing 250-session distribution
/// [D-9, D-115], so a floor computed over a short score table is drawn from a population
/// that does not exist. It would look entirely normal: a number in the right range,
/// against a table with the right columns, on every date. The refusal is the only thing
/// that can tell the difference.
///
/// **Pass two loops dates too, and the plan's "one window function over the complete
/// table" is not available.** <c>percentile_cont</c> is an ordered-set aggregate and
/// Postgres does not admit one as a window function, so a trailing p98 cannot be
/// expressed as a window frame at all. What pass two does instead is the statement 4.5
/// already built and tested, per screen per date, which is the same shape as a night. The
/// divergence from the plan's wording is reported at 4.13 rather than papered over, and
/// the wall clock is recorded so the cost of it is a number rather than an argument.
/// </summary>
public sealed class ScreenRangeRun
{
    private readonly string _connectionString;
    private readonly Action<string> _say;

    /// <summary>
    /// **No clock.** A range run is a pure function of its range and of the config in
    /// force on each date it covers, and a driver that read one would be the ambient clock
    /// arriving through the back door [INVARIANT 11]. The sessions come from
    /// <c>price_daily</c> and the config version from each date.
    /// </summary>
    public ScreenRangeRun(string connectionString, Action<string>? say = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _connectionString = connectionString;
        _say = say ?? (_ => { });
    }

    /// <summary>
    /// Pass one. Every session in the range scored by every registered screen, and
    /// nothing ranked.
    ///
    /// **The config version is resolved per date, never once for the range**
    /// [INVARIANT 13, D-43]. A screen definition resolved as of today against a 2022 date
    /// scores that date under definitions it was not scored under, and looks right doing
    /// it.
    /// </summary>
    public async Task<ScreenPassResult> ScoreAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var stage = new ScreenEngine();
        var config = new ConfigStore(_connectionString);
        var sessions = await SessionsAsync(from, to, ct).ConfigureAwait(false);

        var started = Stopwatch.StartNew();
        long rows = 0;

        for (var i = 0; i < sessions.Count; i++)
        {
            var date = sessions[i];
            var version = await config.RequireVersionAsync(date, ct).ConfigureAwait(false);
            var screens = await ScreenRegistry.LoadScoredAsync(config, date, ct).ConfigureAwait(false);

            var data = new StageData(_connectionString, new DeclaredAccess(stage));

            foreach (var screen in screens.OrderBy(s => s.ScreenId, StringComparer.Ordinal))
            {
                rows += await data.WriteAsync(
                    "screen_score_daily", WriteOperation.Insert,
                    ScreenEngine.ScoreSql(screen, date, version), parameters: null, ct)
                    .ConfigureAwait(false);
            }

            if (i % 100 == 0)
            {
                _say($"  score  {date:yyyy-MM-dd}  {rows:N0} row(s) so far, {started.Elapsed:hh\\:mm\\:ss}");
            }
        }

        return new ScreenPassResult(
            "score", sessions.Count, rows, started.Elapsed,
            $"{sessions.Count:N0} sessions, {rows:N0} rows, nothing ranked");
    }

    /// <summary>
    /// Pass two. Floors and ranks, refusing unless pass one covered the range.
    ///
    /// **The refusal is a count of distinct dates against the calendar's own session
    /// count**, one query and no new table. An interrupted pass one leaves a score table
    /// that is short in a way nothing downstream can see.
    ///
    /// **It fired on its first real use and the cause was not an interruption**, which is
    /// worth recording because it is the more likely cause. Pass one over
    /// 2021-01-04..2026-08-12 covered 1,457 of 1,462 sessions: `security_daily`'s first
    /// evaluation date is 2021-01-10, C01 building weekly on a Sunday, so the five
    /// sessions before it have no universe membership at all and C13 correctly wrote
    /// nothing for them. The range a selection pass can cover starts at the first session
    /// the universe exists on, and the guard is what said so [4.13].
    /// </summary>
    public async Task<ScreenPassResult> FloorAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var stage = new ScreenEngine();
        var config = new ConfigStore(_connectionString);
        var sessions = await SessionsAsync(from, to, ct).ConfigureAwait(false);

        var data = new StageData(_connectionString, new DeclaredAccess(stage));

        await EnsurePassOneCompleteAsync(data, sessions, from, to, ct).ConfigureAwait(false);

        var lookback = (int) ConfigValue.Long(
            await config.RequireAsync("screens.floor_lookback_days", to, ct).ConfigureAwait(false));

        var percentile = ConfigValue.Long(
            await config.RequireAsync("screens.floor_percentile", to, ct).ConfigureAwait(false));

        var started = Stopwatch.StartNew();
        long rows = 0;

        for (var i = 0; i < sessions.Count; i++)
        {
            var date = sessions[i];
            var screens = await ScreenRegistry.LoadScoredAsync(config, date, ct).ConfigureAwait(false);

            foreach (var screen in screens.OrderBy(s => s.ScreenId, StringComparer.Ordinal))
            {
                var trailing = await data.ReadAsync(
                    "screen_score_daily",
                    ScreenEngine.TrailingSql(screen.ScreenId, date, lookback, percentile), ct)
                    .ConfigureAwait(false);

                var observed = Convert.ToInt32(trailing[0][0], CultureInfo.InvariantCulture);

                var p98 = trailing[0][1] is null or DBNull
                    ? (double?) null
                    : Convert.ToDouble(trailing[0][1], CultureInfo.InvariantCulture);

                var floor = observed >= lookback ? p98 : null;

                await data.WriteAsync(
                    "screen_history", WriteOperation.Insert,
                    ScreenEngine.HistorySql(screen.ScreenId, date, floor, p98, observed),
                    parameters: null, ct).ConfigureAwait(false);

                if (floor is not null)
                {
                    rows += await data.WriteAsync(
                        "screen_score_daily", WriteOperation.Update,
                        ScreenEngine.RankSql(screen.ScreenId, date, floor.Value),
                        parameters: null, ct).ConfigureAwait(false);
                }
            }

            if (i % 100 == 0)
            {
                _say($"  floor  {date:yyyy-MM-dd}  {rows:N0} ranked so far, {started.Elapsed:hh\\:mm\\:ss}");
            }
        }

        return new ScreenPassResult(
            "floor", sessions.Count, rows, started.Elapsed,
            $"{sessions.Count:N0} sessions, {rows:N0} rows ranked");
    }

    /// <summary>
    /// **Pass one's distinct dates must equal the calendar's session count for the
    /// range**, and the message says which dates are missing rather than only that some
    /// are.
    /// </summary>
    private static async Task EnsurePassOneCompleteAsync(
        IStageData data, IReadOnlyList<DateOnly> sessions, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await data.ReadAsync(
            "screen_score_daily",
            "SELECT count(DISTINCT date)::int FROM screen_score_daily WHERE date BETWEEN " +
            Literal(from) + " AND " + Literal(to) + ";", ct).ConfigureAwait(false);

        var scored = Convert.ToInt32(rows[0][0], CultureInfo.InvariantCulture);

        if (scored != sessions.Count)
        {
            throw new InvalidOperationException(
                "Pass one covered " + scored.ToString(CultureInfo.InvariantCulture) +
                " of the calendar's " + sessions.Count.ToString(CultureInfo.InvariantCulture) +
                " sessions between " + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                " and " + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                ", so pass two will not run. A floor is the 98th percentile of a screen's own " +
                "trailing distribution, so a floor drawn over a short score table comes from a " +
                "population that does not exist and looks entirely normal doing it: a number in " +
                "the right range, on every date, against a table with the right columns. Re-run " +
                "pass one over the whole range first [D-9, D-115, 4.13].");
        }
    }

    private async Task<IReadOnlyList<DateOnly>> SessionsAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var calendar = new StageData(
            _connectionString,
            new DeclaredAccess("ScreenRangeRun", ["price_daily"], []));

        var sessions = await TradingCalendar.SessionsAsync(calendar, from, to, ct).ConfigureAwait(false);

        if (sessions.Count == 0)
        {
            throw new InvalidOperationException(
                "price_daily holds no session between " +
                from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " and " +
                to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                ". A range run over no date would write nothing and report success.");
        }

        return sessions;
    }

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
}
