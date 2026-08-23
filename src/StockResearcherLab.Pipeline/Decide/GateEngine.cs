using System.Globalization;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Gates;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Decide;

/// <summary>
/// C12. Every active member is labelled with every reason it is unavailable tonight, or
/// with none [`ARCHITECTURE.html` §03, D-117].
///
/// **The gate labels and never narrows.** Every active member gets exactly one row,
/// which makes INVARIANT 1 checkable here as well as at C13. Nothing downstream of the
/// universe definition narrows by rank, score or count, and a gate that wrote rows only
/// for the names it rejected would be a filter wearing a label's clothes.
///
/// **Every failing reason is recorded rather than the first** [`SCHEMA.md`]. A name
/// blocked by earnings and by a gap is blocked for two reasons, and a row carrying only
/// the first makes the second invisible to any later question about why names are
/// rejected. <c>passed</c> is the empty reason array and not a separately maintained
/// flag, so the two cannot disagree.
///
/// **This stage does not read <c>screen_score_daily</c> and C13 does not read
/// <c>gate_result</c>.** Exclusion happens at allocation, in C14. A floor drawn over the
/// ungated subset would move when a position opens or a cooldown expires, which makes a
/// screen's floor a function of the portfolio [D-117, INVARIANT 1, INVARIANT 2].
///
/// **Three of the five reasons are structurally unevaluable over the backfill window
/// rather than passing over it** [D-117]. <c>position</c> and <c>trade_outcome</c> hold
/// no rows until phase 7, so already-held and cooldown can never fire, and earnings are
/// deliberately not backfilled, so the blackout has no calendar to read on a historical
/// date. That is what <c>attribution.gate_state</c> records as <c>passed_partial</c> at
/// 4.10, and it is why all five are exercised here against fabricated rows rather than
/// left untested because the tables are empty.
/// </summary>
public sealed class GateEngine : IStage
{
    public string Name => "GateEngine";

    /// <summary>
    /// §3's four stores plus <c>security_daily</c>, which is where the membership comes
    /// from and which the cell does not name.
    ///
    /// **The declaration follows the code and the cell is reported.** This is the same
    /// class as B1 and B2 in `prompts/BuildPlans/phase-4-screens-and-selection.md` §8: a
    /// Reads cell that cannot be satisfied as written, because "every active member gets
    /// a row" requires the table the membership is read from and the cell names four
    /// others. Narrowing the declaration to fit the markup is the direction that hid the
    /// fundamentals pool closing over itself [D-74].
    ///
    /// <c>config_rows</c> is deliberately absent, unlike C13's declaration. Config
    /// arrives through <see cref="IConfigStore"/> on its own connection, so
    /// <see cref="DeclaredAccess"/> never sees that read, and C13 declares it only
    /// because §3's cell for C13 names it. §3's cell for C12 does not, so declaring it
    /// here would be the conformance test's other direction failing.
    /// </summary>
    public IReadOnlyList<string> ReadSet { get; } =
    [
        "events", "price_daily", "position", "trade_outcome", "security_daily",
    ];

    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new("gate_result", WriteOperation.Insert, Columns),
    ];

    public static readonly string[] Columns = ["ticker", "date", "passed", "reasons"];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var thresholds = new GateThresholds(
            GapPct: await NumberAsync(context, "gates.gap_pct", ct).ConfigureAwait(false),
            BlackoutBefore: await IntAsync(context, "gates.earnings_blackout_days_before", ct).ConfigureAwait(false),
            BlackoutAfter: await IntAsync(context, "gates.earnings_blackout_days_after", ct).ConfigureAwait(false),
            CooldownDays: await IntAsync(context, "gates.cooldown_days", ct).ConfigureAwait(false));

        var written = await context.Data.WriteAsync(
            "gate_result", WriteOperation.Insert,
            Sql(context.Date, thresholds), parameters: null, ct).ConfigureAwait(false);

        return new StageResult(written, "ok",
            written.ToString(CultureInfo.InvariantCulture) + " members labelled");
    }

    /// <summary>The four thresholds, resolved as of the simulated date [INVARIANT 13].</summary>
    public readonly record struct GateThresholds(
        double GapPct, int BlackoutBefore, int BlackoutAfter, int CooldownDays);

    /// <summary>
    /// One date, one statement, one row per active member.
    ///
    /// **The reason array is built from <see cref="GateReasons.All"/> in declaration
    /// order and stripped of its nulls**, so the surviving entries keep that order.
    /// <c>array_remove</c> preserves order, which is what makes "a name failing three
    /// reasons carries three entries in a fixed order" a property of the statement
    /// rather than of how Postgres happened to evaluate it [`CLAUDE.md` §6].
    /// </summary>
    public static string Sql(DateOnly date, GateThresholds thresholds)
    {
        var d = Literal(date);

        var arms = string.Join(",\n                    ",
            GateReasons.All.Select(r =>
                $"CASE WHEN {Condition(r, d, thresholds)} THEN '{GateReasons.Name(r)}' END"));

        return $"""
            INSERT INTO gate_result (ticker, date, passed, reasons)
            SELECT
                u.ticker,
                {d} AS date,
                cardinality(g.reasons) = 0 AS passed,
                g.reasons
            FROM (SELECT m.ticker FROM {Universe.AsOf(d)} m WHERE m.is_active) u
            LEFT JOIN price_daily p ON p.ticker = u.ticker AND p.date = {d}
            LEFT JOIN LATERAL (
                SELECT q.close
                FROM price_daily q
                WHERE q.ticker = u.ticker AND q.date < {d}
                ORDER BY q.date DESC
                LIMIT 1
            ) prev ON TRUE
            CROSS JOIN LATERAL (
                SELECT array_remove(ARRAY[
                    {arms}
                ], NULL) AS reasons
            ) g
            ON CONFLICT (ticker, date) DO UPDATE SET
                passed = EXCLUDED.passed,
                reasons = EXCLUDED.reasons;
            """;
    }

    private static string Condition(GateReason reason, string d, GateThresholds t)
        => reason switch
        {
            GateReason.EarningsBlackout => EarningsBlackout(d, t),
            GateReason.Gap => Gap(t),
            GateReason.Halt => Halt(),
            GateReason.AlreadyHeld => AlreadyHeld(d),
            GateReason.Cooldown => Cooldown(d, t),
            _ => throw new InvalidOperationException(
                $"Gate reason '{reason}' has no condition. The vocabulary is closed and every " +
                "member of it is evaluated here, so a reason added to the enum without a " +
                "condition fails the stage rather than silently never firing [D-117]."),
        };

    /// <summary>
    /// Earnings inside the window, which reaches <c>before</c> days forward and
    /// <c>after</c> days back. The name is what the window is measured on: five days
    /// **before** earnings is an earnings date up to five days ahead of tonight.
    ///
    /// <c>events</c> is keyed on <c>event_date</c> for the earnings type, and the type
    /// string is the one C06 writes [`EventsIngestor`].
    /// </summary>
    private static string EarningsBlackout(string d, GateThresholds t)
        => "EXISTS (SELECT 1 FROM events e WHERE e.ticker = u.ticker AND e.event_type = 'earnings' " +
           $"AND e.event_date BETWEEN {d} - {Int(t.BlackoutAfter)} AND {d} + {Int(t.BlackoutBefore)})";

    /// <summary>
    /// An overnight move above the threshold, **in either direction**. A name that gapped
    /// down eleven percent is as unenterable at tomorrow's open as one that gapped up,
    /// and §03 names the reason "gap" without a sign.
    ///
    /// Measured against the previous session's close rather than a fixed calendar
    /// yesterday, so a long weekend or a holiday is the same comparison.
    /// </summary>
    private static string Gap(GateThresholds t)
        => "prev.close IS NOT NULL AND prev.close <> 0 AND p.open IS NOT NULL " +
           $"AND abs((p.open - prev.close) / prev.close) * 100 > {Num(t.GapPct)}";

    /// <summary>
    /// The name did not trade this session.
    ///
    /// **UNAUTHORED, and stated here rather than hidden.** Nothing in this corpus defines
    /// the halt condition operationally. `CONFIG_REFERENCE.md` says only that it carries
    /// no threshold key because "a name is halted or it is not", and §3 gives C12 four
    /// stores of which <c>price_daily</c> is the only price source. This is the one
    /// reading that store supports: an active member with no bar for the session, or a
    /// bar with no volume, did not trade. Reported at 4.8 rather than decided.
    /// </summary>
    private static string Halt()
        => "p.ticker IS NULL OR p.volume IS NULL OR p.volume = 0";

    /// <summary>
    /// An open position in any portfolio on this date.
    ///
    /// **Read from the dates and not from <c>is_open</c>**, which is a flag standing for
    /// today. A backfill asking "was this name held on 2022-06-03" off <c>is_open</c>
    /// gets an answer about 2026, which is D-92's defect arriving in a second component:
    /// it would look right on every live night and be wrong on every historical one.
    /// </summary>
    private static string AlreadyHeld(string d)
        => "EXISTS (SELECT 1 FROM \"position\" pos WHERE pos.ticker = u.ticker " +
           $"AND pos.opened_date <= {d} AND (pos.closed_date IS NULL OR pos.closed_date > {d}))";

    /// <summary>
    /// Exited inside the cooldown, counted in calendar days from the exit.
    ///
    /// **The cooldown at 30 calendar days is shorter than D-34's 40-trading-day time
    /// stop**, so a name can be re-surfaced before a position that ran its full stop
    /// would have closed. That was seen and accepted when the value was set, and it is
    /// recorded here beside the code rather than only in `CONFIG_REFERENCE.md`.
    /// </summary>
    private static string Cooldown(string d, GateThresholds t)
        => "EXISTS (SELECT 1 FROM trade_outcome tout WHERE tout.ticker = u.ticker " +
           $"AND tout.exit_date <= {d} AND tout.exit_date > {d} - {Int(t.CooldownDays)})";

    private static async Task<double> NumberAsync(StageContext c, string key, CancellationToken ct)
        => ConfigValue.Double(await c.Config.RequireAsync(key, c.Date, ct).ConfigureAwait(false));

    private static async Task<int> IntAsync(StageContext c, string key, CancellationToken ct)
        => (int) ConfigValue.Long(await c.Config.RequireAsync(key, c.Date, ct).ConfigureAwait(false));

    private static string Num(double value)
        => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
}
