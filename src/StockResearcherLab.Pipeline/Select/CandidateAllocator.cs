using System.Globalization;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline.Select;

/// <summary>
/// C14. Each live screen's ranked list is cut to a size quota, the gated names dropped,
/// and the union written once per name [`ARCHITECTURE.html` §06].
///
/// **The floors are already applied.** C13 leaves <c>rank_within_screen</c> null below a
/// screen's floor, so this component needs no floor knowledge at all and cannot apply one
/// differently [D-115, §06].
///
/// **The slot count is a ceiling and never a target.** If only three names clear a
/// screen's floor, that screen returns three.
///
/// **An unfillable size slot stays empty and is never backfilled from a larger bucket**
/// [INVARIANT 3, D-8]. The quota is applied inside each bucket, so a screen with no small
/// name clearing its floor sends fewer names rather than promoting a mid one. The empty
/// slot is the diversity guarantee working.
///
/// **A gated name's slot passes to the next name of its own size, and that is a
/// different case** [D-117]. D-8 forbids backfilling from a larger bucket, which is the
/// case where nothing of that size cleared the floor. A name that cleared the floor and
/// is unavailable tonight is not that case, and conflating the two either leaves a hole
/// D-8 does not ask for or leaks the guarantee D-8 exists to hold. Gated names are
/// dropped before the seats are counted, so the seat goes to the next eligible name of
/// the same size.
///
/// **Shadows are scored and allocated nothing** [D-84, D-85]. Only live screens reach
/// this component's quota. The shadow half is 4.10's, where a shadow's names are written
/// to <c>attribution</c> and to no <c>candidate_set</c> row.
/// </summary>
public sealed class CandidateAllocator : IStage
{
    public string Name => "CandidateAllocator";

    /// <summary>
    /// §3's cell exactly, after the amendment that closed §8's B2.
    ///
    /// <c>market_context_daily</c> is declared and not yet read: the regime it supplies
    /// is stamped on an <c>attribution</c> row, which is 4.10's. Declaring the cell's
    /// full set is safe in the direction the conformance test enforces without exemption,
    /// which is a table read and not declared.
    ///
    /// <c>config_rows</c> is deliberately absent, §3's cell for this component not naming
    /// it. Config arrives through <see cref="IConfigStore"/> on its own connection, so
    /// <see cref="DeclaredAccess"/> never sees that read.
    /// </summary>
    public IReadOnlyList<string> ReadSet { get; } =
    [
        "screen_score_daily", "gate_result", "security_daily", "market_context_daily",
    ];

    /// <summary>
    /// Insert and Delete, both this component's [INVARIANT 10 read per operation].
    ///
    /// **The delete is what makes a re-run of one date reproduce that date.** An insert
    /// with <c>ON CONFLICT</c> alone updates the names the new run surfaces and leaves
    /// behind the ones it no longer does, so a night re-run after a name was gated would
    /// keep that name in the candidate set. Found by the gated-slot fixture at 4.9, where
    /// the gated name stayed a candidate through the second run.
    ///
    /// Two runs of a stage over one date and config version must produce identical
    /// output, and with the stale row surviving they did not [`CLAUDE.md` §6].
    /// </summary>
    public IReadOnlyList<TableWrite> WriteSet { get; } =
    [
        new("candidate_set", WriteOperation.Delete, Columns),
        new("candidate_set", WriteOperation.Insert, Columns),
    ];

    public static readonly string[] Columns =
        ["ticker", "date", "screens_surfacing", "size_bucket", "slot_filled"];

    public async Task<StageResult> ExecuteAsync(StageContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var live = await ScreenRegistry
            .LoadLiveAsync(context.Config, context.Date, ct).ConfigureAwait(false);

        var floor = (int) ConfigValue.Long(
            await context.Config.RequireAsync("tuner.slot_floor", context.Date, ct).ConfigureAwait(false));

        var cap = (int) ConfigValue.Long(
            await context.Config.RequireAsync("tuner.slot_cap", context.Date, ct).ConfigureAwait(false));

        var quotas = live
            .Select(s => (s.ScreenId, Quota: SlotQuota.For(Validated(s, floor, cap))))
            .OrderBy(x => x.ScreenId, StringComparer.Ordinal)
            .ToList();

        if (quotas.Count == 0)
        {
            // Not a silent zero. A night with no live screen produces no candidate, and
            // that must not read like a night where nothing cleared a floor
            // [CLAUDE.md section 1].
            return new StageResult(0, "ok", "no live screen is registered on this date");
        }

        await EnsureGateHasRunAsync(context, ct).ConfigureAwait(false);

        // The date is cleared before it is written, so the run replaces the night rather
        // than merging with whatever a previous run of it left.
        await context.Data.WriteAsync(
            "candidate_set", WriteOperation.Delete,
            ClearSql(context.Date), parameters: null, ct).ConfigureAwait(false);

        var written = await context.Data.WriteAsync(
            "candidate_set", WriteOperation.Insert,
            Sql(quotas, context.Date), parameters: null, ct).ConfigureAwait(false);

        var detail = string.Join(", ", quotas.Select(q => $"{q.ScreenId} {q.Quota}"))
            + "; " + written.ToString(CultureInfo.InvariantCulture) + " candidates";

        return new StageResult(written, "ok", detail);
    }

    /// <summary>
    /// A screen's slot count, held inside the tuner's own floor and cap [D-43].
    ///
    /// **Fails the stage closed rather than clamping.** A count outside the range is a
    /// configuration error, and clamping it would run the night under an allocation
    /// nobody wrote while every row it produced looked ordinary. Above the cap it also
    /// breaks the two properties §8.1's proportion is chosen for: the megacap bound and
    /// the small-cap floor are stated over counts from four to twelve.
    /// </summary>
    private static int Validated(ScreenDefinition screen, int floor, int cap)
        => screen.Slots >= floor && screen.Slots <= cap
            ? screen.Slots
            : throw new InvalidOperationException(
                $"Screen '{screen.ScreenId}' has " + screen.Slots.ToString(CultureInfo.InvariantCulture) +
                " slots, outside the tuner's floor of " + floor.ToString(CultureInfo.InvariantCulture) +
                " and cap of " + cap.ToString(CultureInfo.InvariantCulture) +
                ". D-89's proportion holds its megacap bound and its small-cap floor over that " +
                "range and is not stated outside it, so this fails rather than clamping [D-43, D-116].");

    /// <summary>
    /// C12 must have run for this date.
    ///
    /// **Without this the missing gate is a silent zero.** The allocation joins
    /// <c>gate_result</c>, so a night where C12 did not run drops every name and writes
    /// no candidate, which is indistinguishable on the page from a night where nothing
    /// cleared a floor. That is the failure `CLAUDE.md` §1 describes and the reason the
    /// run fails closed here [§6, RUNBOOK].
    /// </summary>
    private static async Task EnsureGateHasRunAsync(StageContext context, CancellationToken ct)
    {
        var rows = await context.Data.ReadAsync(
            "gate_result",
            $"SELECT count(*)::bigint FROM gate_result WHERE date = {Literal(context.Date)};",
            ct).ConfigureAwait(false);

        if (Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture) == 0)
        {
            throw new InvalidOperationException(
                $"gate_result holds no row for {context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}. " +
                "C12 labels every active member and this component joins that label, so an absent " +
                "gate would drop every name and write no candidate, which reads exactly like a " +
                "night where nothing cleared a floor [CLAUDE.md section 1].");
        }
    }

    /// <summary>
    /// One date, one statement.
    ///
    /// **The gate is applied before the seats are counted**, which is what makes a gated
    /// name's slot pass to the next name of its own size rather than leaving a hole
    /// [D-117].
    ///
    /// **The seat number is per screen and per bucket**, which is what makes an unfilled
    /// bucket stay unfilled: there is no expression anywhere here that could move a seat
    /// between buckets [INVARIANT 3, D-8].
    ///
    /// Ordering is the screen's own rank then ticker ascending, so a tie inside a bucket
    /// resolves the same way twice and the whole statement is reproducible
    /// [`CLAUDE.md` §6].
    /// </summary>
    public static string Sql(IReadOnlyList<(string ScreenId, SlotQuota Quota)> quotas, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(quotas);

        var d = Literal(date);

        // Ordinal by screen then by the fixed bucket order, so two runs emit byte-identical
        // SQL and the VALUES list cannot depend on how config enumerated.
        var seats = string.Join(",\n                    ", quotas
            .SelectMany(q => SlotQuota.Buckets.Select(b => (q.ScreenId, Bucket: b, Seats: q.Quota.Seats(b))))
            .Select(x => $"({Quote(x.ScreenId)}, {Quote(x.Bucket)}, {Int(x.Seats)})"));

        return $"""
            INSERT INTO candidate_set (ticker, date, screens_surfacing, size_bucket, slot_filled)
            WITH quota (screen_id, size_bucket, seats) AS (
                VALUES
                    {seats}
            ),
            member AS (
                SELECT m.ticker, m.size_bucket
                FROM {Universe.AsOf(d)} m
                WHERE m.is_active AND m.size_bucket IS NOT NULL
            ),
            eligible AS (
                SELECT s.screen_id, s.ticker, s.rank_within_screen, member.size_bucket
                FROM screen_score_daily s
                JOIN member ON member.ticker = s.ticker
                JOIN gate_result g ON g.ticker = s.ticker AND g.date = {d} AND g.passed
                WHERE s.date = {d} AND s.rank_within_screen IS NOT NULL
            ),
            seated AS (
                SELECT e.screen_id, e.ticker, e.size_bucket,
                       row_number() OVER (
                           PARTITION BY e.screen_id, e.size_bucket
                           ORDER BY e.rank_within_screen ASC, e.ticker ASC
                       ) AS seat
                FROM eligible e
            ),
            taken AS (
                SELECT seated.ticker, seated.screen_id, seated.size_bucket
                FROM seated
                JOIN quota q
                  ON q.screen_id = seated.screen_id AND q.size_bucket = seated.size_bucket
                WHERE seated.seat <= q.seats
            )
            SELECT
                taken.ticker,
                {d} AS date,
                array_agg(DISTINCT taken.screen_id COLLATE "C") AS screens_surfacing,
                min(taken.size_bucket) AS size_bucket,
                NULL::boolean AS slot_filled
            FROM taken
            GROUP BY taken.ticker
            ON CONFLICT (ticker, date) DO UPDATE SET
                screens_surfacing = EXCLUDED.screens_surfacing,
                size_bucket = EXCLUDED.size_bucket,
                slot_filled = EXCLUDED.slot_filled;
            """;
    }

    /// <summary>The date's own rows, removed before it is rebuilt. See <see cref="WriteSet"/>.</summary>
    public static string ClearSql(DateOnly date)
        => $"DELETE FROM candidate_set WHERE date = {Literal(date)};";

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Literal(DateOnly d)
        => "DATE '" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
}
