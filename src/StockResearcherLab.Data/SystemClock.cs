using StockResearcherLab.Core;

namespace StockResearcherLab.Data;

/// <summary>
/// The one place system time is read [INVARIANT 11]. It lives here rather than
/// in Core because Core takes no dependency on anything that needs a machine,
/// and a clock implementation is exactly that [CLAUDE.md section 4].
/// </summary>
public sealed class SystemClock : IClock
{
    private static readonly TimeZoneInfo Eastern = ResolveEastern();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <summary>
    /// Today in US Eastern terms. All market semantics are US Eastern, and a
    /// trading date is the label the exchange gave a session rather than a
    /// timezone conversion of a timestamp [CLAUDE.md section 6]. This is the
    /// closest a wall clock gets to that, and anything that needs the real
    /// trading calendar takes it from the exchange rather than from here.
    /// </summary>
    public DateOnly Today
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Eastern).DateTime);

    private static TimeZoneInfo ResolveEastern()
    {
        // The IANA id on Linux and CI, the Windows id locally. Tried in that
        // order rather than branching on the platform, because the platform is
        // not the thing that matters and a missing zone should fail loudly.
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the next.
            }
            catch (InvalidTimeZoneException)
            {
                // Try the next.
            }
        }

        throw new InvalidOperationException(
            "Neither 'America/New_York' nor 'Eastern Standard Time' resolves on this machine. " +
            "All market semantics here are US Eastern, so there is no sensible fallback.");
    }
}
