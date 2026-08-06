namespace StockResearcherLab.Core;

/// <summary>
/// The only route to the current time. Nothing reads system time outside an
/// implementation of this [INVARIANT 11], which is what makes a night replayable
/// and what makes an accidental clock read a compile-time visible dependency
/// rather than a silent one.
/// </summary>
public interface IClock
{
    /// <summary>The current instant. Used for run_log timestamps and nothing that affects output.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Today's trading date in US Eastern terms. All market semantics are US
    /// Eastern, and a trading date is the label the exchange gave a session
    /// rather than a timezone conversion of a timestamp [CLAUDE.md section 6].
    /// </summary>
    DateOnly Today { get; }
}

/// <summary>A clock frozen at a given instant. Deterministic by construction.</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset utcNow, DateOnly today)
    {
        UtcNow = utcNow;
        Today = today;
    }

    public DateTimeOffset UtcNow { get; }

    public DateOnly Today { get; }
}
