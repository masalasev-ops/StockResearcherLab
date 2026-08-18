using System.Globalization;
using StockResearcherLab.Core.Config;

namespace StockResearcherLab.Core.Stages;

/// <summary>
/// A second entry point on a stage that already exists, never a second component
/// [D-93].
///
/// A `HistoricalPriceIngestor` would be a second claim on `price_daily`'s
/// component-table-operation triple, which the conformance test catches, and
/// `ARCHITECTURE.html` §3 would not name it, which `RegistryNameTests` catches.
/// Neither is an accident of the machinery: a loader with its own arithmetic is a
/// second implementation of every formula, and the reference fixtures at 2.5 to 2.8
/// would then cover half of what runs.
///
/// So <see cref="WriteSet"/>, <see cref="IStage.ReadSet"/>, the registry and §3 are
/// all unchanged by a stage gaining this.
/// </summary>
public interface IBackfillStage : IStage
{
    /// <summary>
    /// The same work as <see cref="IStage.ExecuteAsync"/>, over a range.
    ///
    /// Idempotent per grain rather than transactional, which is what makes an
    /// interrupted backfill resumable: one transaction spanning twelve million rows
    /// is its own failure mode [D-68, D-93].
    /// </summary>
    Task<BackfillResult> ExecuteRangeAsync(BackfillContext context, CancellationToken ct = default);
}

/// <summary>
/// What a range execution did.
///
/// **It records no position and nothing resumes from it** [3.6, 0010]. A
/// ticker-partitioned sweep resumes on its own attempt record, so a clean halt, a
/// command timeout and a killed process all resume identically. A clean halt is still
/// not a failure and still does not look like one: the sweep has spent what it could
/// and everything written is kept.
/// </summary>
/// <param name="RowsWritten">Rows written across the whole range.</param>
/// <param name="Status">
/// `ok` for a range that completed, `halted` for one the allowance gate stopped.
/// The run log carries it, so an operator sees the difference without reading detail.
/// </param>
/// <param name="Detail">The line worth reading. Lands in `run_log.error`, the only free-text column that table has.</param>
/// <param name="LastDateCovered">
/// The last date this execution actually finished. For a completed range it is the
/// range end; for a halted one it is where it reached, which is why `run_log.run_date`
/// takes it rather than the range end.
/// </param>
public readonly record struct BackfillResult(
    long RowsWritten,
    string Status,
    string? Detail,
    DateOnly LastDateCovered)
{
    public static BackfillResult Completed(long rowsWritten, DateOnly lastDateCovered, string? detail = null)
        => new(rowsWritten, "ok", detail, lastDateCovered);

    /// <summary>Stopped by the allowance gate, with everything written so far kept.</summary>
    public static BackfillResult Halted(long rowsWritten, DateOnly lastDateCovered, string detail)
        => new(rowsWritten, "halted", detail, lastDateCovered);

    public bool WasHalted => string.Equals(Status, "halted", StringComparison.Ordinal);
}

/// <summary>
/// Everything a range execution is allowed to reach. The range equivalent of
/// <see cref="StageContext"/>, and deliberately not a superset of it.
///
/// **There is no Date and no ConfigVersion here, and that is the point.** D-93 puts
/// config resolution per date being computed rather than once for the range, and a
/// context carrying one version for a whole range would make the wrong thing the easy
/// thing. The only route to a <see cref="StageContext"/> inside a range is
/// <see cref="ForDateAsync"/>, which resolves that date's version [D-43, INVARIANT 13].
/// </summary>
public sealed class BackfillContext
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<DateOnly>>>? _sessions;
    private IReadOnlyList<DateOnly>? _resolved;

    public BackfillContext(
        DateOnly from,
        DateOnly to,
        IStageData data,
        IClock clock,
        IConfigStore config,
        IUnitAllowance allowance,
        Func<CancellationToken, Task<IReadOnlyList<DateOnly>>>? sessions = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(allowance);

        if (to < from)
        {
            throw new ArgumentException(
                $"The backfill range ends before it starts: {Iso(from)} to {Iso(to)}.", nameof(to));
        }

        From = from;
        To = to;
        Data = data;
        Clock = clock;
        Config = config;
        Allowance = allowance;
        _sessions = sessions;
    }

    /// <summary>
    /// The trading dates this range covers, resolved once by the driver and shared by
    /// every stage in it.
    ///
    /// **Resolved here rather than by each stage, for two reasons and the second is the
    /// one that forced it** [3.14]. A calendar read per stage is a calendar read that can
    /// disagree between stages inside one run, and a backfill whose compute layers
    /// evaluate different date sets writes a store nothing produced. And the session list
    /// lives in `price_daily`, which C34 and C35 do not declare and cannot be given
    /// without an authored `ARCHITECTURE.html` §3 edit; the driver declares it for itself
    /// instead, so the stage contract is enforced rather than widened.
    ///
    /// Memoised, so a range with four stages in it reads the calendar once.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> SessionsAsync(CancellationToken ct = default)
    {
        if (_resolved is { } already)
        {
            return already;
        }

        if (_sessions is null)
        {
            throw new InvalidOperationException(
                "This BackfillContext was constructed without a session source, so the trading " +
                "calendar cannot be resolved. The driver supplies it; a context built by hand has " +
                "to supply one too rather than falling back to a stage's own read, which would put " +
                "`price_daily` inside the declared set of every stage that walks a range.");
        }

        _resolved = await _sessions(ct).ConfigureAwait(false);
        return _resolved;
    }

    /// <summary>First date of the range, inclusive.</summary>
    public DateOnly From { get; }

    /// <summary>Last date of the range, inclusive.</summary>
    public DateOnly To { get; }

    /// <summary>The only data access a range execution has, and it enforces the declared sets.</summary>
    public IStageData Data { get; }

    /// <summary>Injected. Nothing reads system time outside the clock implementation [INVARIANT 11].</summary>
    public IClock Clock { get; }

    /// <summary>Configuration. Resolved per date through <see cref="ForDateAsync"/>, never once for the range.</summary>
    public IConfigStore Config { get; }

    /// <summary>The remaining provider allowance, read before each unit of work.</summary>
    public IUnitAllowance Allowance { get; }

    /// <summary>
    /// The date the provider would stamp a call made now, being the UTC date.
    ///
    /// **Not <see cref="IClock.Today"/>**, which is US Eastern. At 02:52 UTC on
    /// 2026-08-12 the provider's counter had already moved to 2026-08-12 while New
    /// York was still on 2026-08-11, so an Eastern comparison would call a current
    /// reading stale every evening [3.1].
    /// </summary>
    public DateOnly ProviderDate => DateOnly.FromDateTime(Clock.UtcNow.UtcDateTime);

    /// <summary>
    /// A <see cref="StageContext"/> for one date inside the range, with that date's
    /// config version resolved.
    ///
    /// Fails rather than defaulting where no version is in force, which is the case a
    /// window start before `ConfigSeeder.SeedInstant` produces: every stage would
    /// otherwise compute against a configuration that resolved to nothing while
    /// today's resolution still looked correct [D-72, D-94].
    /// </summary>
    public async Task<StageContext> ForDateAsync(DateOnly date, CancellationToken ct = default)
    {
        if (date < From || date > To)
        {
            throw new ArgumentOutOfRangeException(
                nameof(date),
                $"{Iso(date)} is outside the range {Iso(From)} to {Iso(To)}. A range execution " +
                "computing a date it was not given is the failure the range bound exists to prevent.");
        }

        var version = await Config.RequireVersionAsync(date, ct).ConfigureAwait(false);
        return new StageContext(date, version, Data, Clock, Config);
    }

    /// <summary>
    /// Every date in the range, ascending. Ordered by construction rather than by an
    /// enumeration whose order is unspecified [`CLAUDE.md` §6].
    /// </summary>
    public IEnumerable<DateOnly> Dates()
    {
        for (var date = From; date <= To; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    /// <summary>
    /// Whether the next unit of work fits, read fresh from the provider.
    ///
    /// **Stated once, here, and named in the done-when of every sweep that carries
    /// it.** Writing this out per sweep is the duplication D-76, D-77 and D-83 each
    /// removed.
    /// </summary>
    /// <param name="projectedWeight">From the sweep's own `backfill.weight_*` key.</param>
    /// <param name="reserve">From `backfill.unit_reserve`.</param>
    /// <param name="configuredLimit">
    /// From `backfill.daily_unit_allowance`. Compared against what the provider
    /// reports, never used in place of it.
    /// </param>
    public async Task<AllowanceDecision> NextUnitAsync(
        long projectedWeight, long reserve, long configuredLimit, CancellationToken ct = default)
    {
        var reading = await Allowance.ReadAsync(ct).ConfigureAwait(false);
        return AllowanceRule.Decide(reading, ProviderDate, reserve, projectedWeight, configuredLimit);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
