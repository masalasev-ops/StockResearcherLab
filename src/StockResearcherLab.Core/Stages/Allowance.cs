namespace StockResearcherLab.Core.Stages;

/// <summary>
/// What the provider says has been spent, and the day it says it about.
///
/// **The date is not decoration.** The counter resets per provider day and the reset
/// was observed landing inside a sweep at 3.1: a read at 02:52 UTC on 2026-08-12
/// returned 90,518 used stamped 2026-08-11, and the next billable call came back
/// against a counter reading 1. A gate that subtracts two readings across that
/// boundary gets a negative number, which is what the sweep's own bracket printed.
/// </summary>
/// <param name="Used">`apiRequests`, the weighted units spent on <paramref name="StampedOn"/>.</param>
/// <param name="Limit">`dailyRateLimit`. 100,000 on this subscription, read at phase P, phase 1 and 3.1.</param>
/// <param name="StampedOn">`apiRequestsDate`. The provider's day, which is not US Eastern [3.1].</param>
public readonly record struct AllowanceReading(int Used, int Limit, DateOnly StampedOn);

/// <summary>
/// What the gate concluded. Three values rather than two, because an exhausted
/// allowance and an unusable reading are different observations and must not collapse
/// into each other.
///
/// That is the same distinction the plan draws between an allowance wall and a D-71
/// short page, one level down: a verdict that says "no" for two different reasons
/// tells an operator nothing about which one to act on.
/// </summary>
public enum AllowanceVerdict
{
    /// <summary>The next unit of work fits above the reserve. Spend it.</summary>
    Fits,

    /// <summary>The reading is current and the next unit does not fit. Halt cleanly and resume tomorrow.</summary>
    Exhausted,

    /// <summary>
    /// The reading is stamped with a day other than the one being run, so its spend
    /// figure belongs to a different day and cannot be subtracted from this one.
    /// Halt cleanly and say so.
    /// </summary>
    Stale,
}

/// <summary>The verdict, what was left, and the line an operator reads in the run log.</summary>
public readonly record struct AllowanceDecision(AllowanceVerdict Verdict, long Remaining, string Detail)
{
    public bool Fits => Verdict == AllowanceVerdict.Fits;
}

/// <summary>
/// The arithmetic, separated from the call that reads it so the rule can be tested
/// without HTTP. The reading is a measurement; this is the only place it becomes a
/// decision.
/// </summary>
public static class AllowanceRule
{
    /// <param name="reading">What `/api/user` last said.</param>
    /// <param name="providerDate">
    /// The date the provider would stamp a call made now. **The UTC date, not
    /// <see cref="IClock.Today"/>**, which is US Eastern: at 02:52 UTC on 2026-08-12
    /// the provider had already moved to 2026-08-12 while New York was still on
    /// 2026-08-11, so the Eastern date would have called a current reading stale
    /// every evening [3.1].
    /// </param>
    /// <param name="reserve">
    /// What a sweep may not eat into, so the nightly run still has an allowance after
    /// a backfill day [`backfill.unit_reserve`].
    /// </param>
    /// <param name="projectedWeight">
    /// The measured weight of the next unit of work. A projection: it says whether the
    /// next unit is expected to fit and never that it did.
    /// </param>
    /// <param name="configuredLimit">
    /// `backfill.daily_unit_allowance`, which is what the allowance was measured to be
    /// rather than what it is now.
    ///
    /// **The reading governs and this is compared against it.** A configured figure
    /// that decided anything would let a provider raising or cutting the allowance go
    /// unnoticed for as long as nobody edited the key, and a sweep would either starve
    /// or spend into a wall while every number looked right. A difference is named in
    /// <see cref="AllowanceDecision.Detail"/> rather than smoothed over, which is the
    /// same rule the six weight keys follow.
    /// </param>
    public static AllowanceDecision Decide(
        AllowanceReading reading, DateOnly providerDate, long reserve, long projectedWeight, long configuredLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reserve);
        ArgumentOutOfRangeException.ThrowIfNegative(projectedWeight);

        if (reading.StampedOn != providerDate)
        {
            // Not assumed to be a reset, in either direction. A reading stamped
            // earlier may mean the counter has rolled and the allowance is whole, or
            // it may mean the provider's day has not turned yet and the spend still
            // stands; 3.1 measured that the reset had happened by 02:52 UTC and could
            // not distinguish a clock boundary from a rollover on the first billable
            // call. Assuming the generous reading spends into a wall, and an
            // allowance error in flight is fatal rather than a clean halt.
            return new AllowanceDecision(
                AllowanceVerdict.Stale,
                Remaining: 0,
                Detail: $"The allowance reading is stamped {Iso(reading.StampedOn)} against a provider " +
                        $"date of {Iso(providerDate)}, so its {reading.Used} spent units belong to a " +
                        "different day and cannot be subtracted from this one. Halted without spending. " +
                        "The counter rolls on the first billable call of a new day, so a nightly run " +
                        "clears this [3.1].");
        }

        var remaining = (long) reading.Limit - reading.Used - reserve;

        // The reading governs. This is the drift between what was measured and what
        // the provider says now, recorded wherever the verdict is.
        var drift = reading.Limit == configuredLimit
            ? ""
            : $" The provider reports a daily limit of {reading.Limit} where " +
              $"backfill.daily_unit_allowance is {configuredLimit}; the reading governs and the " +
              "difference is recorded rather than smoothed over.";

        if (projectedWeight <= remaining)
        {
            return new AllowanceDecision(
                AllowanceVerdict.Fits,
                remaining,
                $"{remaining} units above the reserve of {reserve}, against a projected " +
                $"{projectedWeight}.{drift}");
        }

        return new AllowanceDecision(
            AllowanceVerdict.Exhausted,
            remaining < 0 ? 0 : remaining,
            $"The next unit projects at {projectedWeight} units and {remaining} are left above the " +
            $"reserve of {reserve}, from {reading.Used} of {reading.Limit} spent on " +
            $"{Iso(reading.StampedOn)}. Halted cleanly; D-68's per-grain idempotence is what makes " +
            $"the next run resume rather than restart.{drift}");
    }

    private static string Iso(DateOnly date)
        => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The remaining allowance, read from the provider.
///
/// An interface in Core so a stage can be handed one without Core referencing an HTTP
/// client. The implementation is <c>UnitAllowance</c> in the data layer and is the
/// only thing that decides a sweep has run out.
/// </summary>
public interface IUnitAllowance
{
    /// <summary>
    /// One reading. Costs nothing: `/api/user` is not metered, confirmed at 3.1 by
    /// reading it twice back to back for an unchanged count, which is what makes it
    /// safe to call before every unit of work.
    /// </summary>
    Task<AllowanceReading> ReadAsync(CancellationToken ct = default);
}
