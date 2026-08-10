using StockResearcherLab.Data;
using Xunit;

namespace StockResearcherLab.Tests.Structure;

/// <summary>
/// The one question 1.13 left open, closed by execution rather than by argument.
///
/// **`InvariantGlobalization` is `true` for every project**
/// [`Directory.Build.props`], and that is what stopped `America/New_York`
/// resolving on Windows: Windows keeps timezone data in the registry and the
/// IANA-to-Windows mapping is the part ICU supplies. `SystemClock.ResolveEastern`
/// tries the IANA id and then the Windows one, so Windows falls through to
/// `Eastern Standard Time` and works. Whether Linux resolves the IANA id from
/// tzdata under the same setting was reasoned about and never run.
///
/// **It stayed unrun even after CI started executing on `ubuntu-latest`.**
/// `Migrator` reads `UtcNow` and never `Today`, every other test uses `FixedClock`
/// or a local double, and the one place that constructs a `SystemClock` without
/// reading it cannot force a `beforefieldinit` static field. So the Linux job
/// exercised guards, build, migrate and the whole suite while the timezone
/// resolution it was supposed to cover never ran. This file is what makes it run.
///
/// A failure here arrives as `TypeInitializationException` wrapping the
/// `InvalidOperationException` from `ResolveEastern`, because `Eastern` is a
/// static field initialiser rather than a call.
///
/// **These two tests read the real clock, which every other test in this project
/// avoids** [INVARIANT 11]. That is the point: the ambient read is the thing under
/// test, and it is reached through `SystemClock`'s own surface rather than through
/// `DateTimeOffset.UtcNow`, so nothing here is a second route to system time and
/// `guards.ps1` has nothing to exclude.
/// </summary>
public sealed class SystemClockTests
{
    [Fact]
    public void TheEasternZoneResolvesOnThisPlatform()
    {
        var thrown = Record.Exception(() => new SystemClock().Today);

        Assert.True(thrown is null,
            "SystemClock could not resolve a US Eastern zone on this platform. Neither " +
            "'America/New_York' nor 'Eastern Standard Time' was found, which under " +
            "InvariantGlobalization=true is the case 1.13 reasoned about and did not run. " +
            thrown);
    }

    /// <summary>
    /// That it resolved something is most of the answer, and this is the rest of it.
    /// US Eastern is UTC-5 or UTC-4 and never ahead of UTC, so today in Eastern terms
    /// is the UTC date or the day before it, never after and never two behind.
    ///
    /// Both bounds are read off the same clock, before and after, so a run crossing
    /// UTC midnight between the two reads widens the window rather than failing.
    /// </summary>
    [Fact]
    public void TodayIsTheUtcDateOrTheDayBefore()
    {
        var clock = new SystemClock();

        var before = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var today = clock.Today;
        var after = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        Assert.True(today <= after,
            $"SystemClock.Today is {today}, ahead of the UTC date {after}. US Eastern is " +
            "never ahead of UTC, so the resolved zone is not US Eastern.");

        Assert.True(today >= before.AddDays(-1),
            $"SystemClock.Today is {today}, more than a day behind the UTC date {before}. " +
            "US Eastern is at most five hours behind UTC.");
    }
}
