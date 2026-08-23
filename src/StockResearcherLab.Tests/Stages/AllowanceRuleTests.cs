using System.Text.Json;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data.Eodhd;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// The allowance gate's arithmetic, and the parse it reads from [3.4].
///
/// **Separated from the call so the rule can be exercised without HTTP.** The reading
/// is a measurement and this is the only place it becomes a decision, which is the
/// same split `EodhdUrl.Build` has from the client and for the same reason: a test
/// that needs a live call asserts what the endpoint happened to do today.
/// </summary>
public sealed class AllowanceRuleTests
{
    private static readonly DateOnly ProviderDate = new(2026, 8, 12);

    [Fact]
    public void TheNextUnitFitsWhenItIsWithinWhatIsLeftAboveTheReserve()
    {
        var reading = new AllowanceReading(Used: 30_000, Limit: 100_000, StampedOn: ProviderDate);

        var decision = AllowanceRule.Decide(reading, ProviderDate, reserve: 50_000, projectedWeight: 10, configuredLimit: 100_000);

        Assert.Equal(AllowanceVerdict.Fits, decision.Verdict);
        Assert.True(decision.Fits);
        Assert.Equal(20_000, decision.Remaining);
    }

    /// <summary>
    /// The reserve is what a sweep may not eat into, so a night still has an
    /// allowance after a backfill day. A gate that only compared against the limit
    /// would spend the night's units and the failure would be tomorrow's run rather
    /// than today's sweep.
    /// </summary>
    [Fact]
    public void TheReserveIsHeldBackRatherThanSpent()
    {
        var reading = new AllowanceReading(Used: 49_995, Limit: 100_000, StampedOn: ProviderDate);

        // 5 units are left above the reserve, and 45,000 below it. A gate reading the
        // limit alone would say a 10-unit call fits.
        Assert.Equal(
            AllowanceVerdict.Exhausted,
            AllowanceRule.Decide(reading, ProviderDate, reserve: 50_000, projectedWeight: 10, configuredLimit: 100_000).Verdict);

        Assert.Equal(
            AllowanceVerdict.Fits,
            AllowanceRule.Decide(reading, ProviderDate, reserve: 50_000, projectedWeight: 5, configuredLimit: 100_000).Verdict);
    }

    /// <summary>
    /// The boundary, which is where an off-by-one would live. A unit whose weight
    /// exactly equals what is left fits: the reserve is a floor to stay above, not one
    /// to stay clear of.
    /// </summary>
    [Fact]
    public void AUnitWhoseWeightExactlyEqualsWhatIsLeftFits()
    {
        var reading = new AllowanceReading(Used: 49_990, Limit: 100_000, StampedOn: ProviderDate);

        var decision = AllowanceRule.Decide(reading, ProviderDate, reserve: 50_000, projectedWeight: 10, configuredLimit: 100_000);

        Assert.Equal(AllowanceVerdict.Fits, decision.Verdict);
        Assert.Equal(10, decision.Remaining);
    }

    /// <summary>
    /// **The finding this whole type exists for** [3.1]. The counter resets per
    /// provider day and the reset landed inside the sweep: 90,518 used stamped
    /// 2026-08-11 read at 02:52 UTC on 2026-08-12, then the next billable call came
    /// back against a counter reading 1.
    ///
    /// A gate that subtracted across that boundary would have concluded it had spent
    /// negative units, which is what the sweep's own bracket printed.
    /// </summary>
    [Fact]
    public void AReadingStampedWithADifferentDayIsStaleRatherThanSpentOrClear()
    {
        var yesterday = new AllowanceReading(Used: 90_518, Limit: 100_000, StampedOn: new DateOnly(2026, 8, 11));

        var decision = AllowanceRule.Decide(yesterday, ProviderDate, reserve: 50_000, projectedWeight: 1, configuredLimit: 100_000);

        Assert.Equal(AllowanceVerdict.Stale, decision.Verdict);
        Assert.False(decision.Fits);
        Assert.Equal(0, decision.Remaining);

        // Named in the line an operator reads, because the action differs: an
        // exhausted allowance waits for tomorrow, a stale reading clears the moment
        // any billable call rolls the counter.
        Assert.Contains("2026-08-11", decision.Detail, StringComparison.Ordinal);
        Assert.Contains("2026-08-12", decision.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stale and exhausted are one absorbed observation apart, which is the same
    /// structural distinction D-71 draws between a short page and a client that
    /// stopped asking. A gate collapsing them tells an operator nothing about which to
    /// act on.
    /// </summary>
    [Fact]
    public void StaleAndExhaustedAreDifferentObservationsAndSayDifferentThings()
    {
        var stale = AllowanceRule.Decide(
            new AllowanceReading(90_518, 100_000, new DateOnly(2026, 8, 11)),
            ProviderDate, reserve: 50_000, projectedWeight: 1, configuredLimit: 100_000);

        var exhausted = AllowanceRule.Decide(
            new AllowanceReading(90_518, 100_000, ProviderDate),
            ProviderDate, reserve: 50_000, projectedWeight: 1, configuredLimit: 100_000);

        Assert.NotEqual(stale.Verdict, exhausted.Verdict);
        Assert.NotEqual(stale.Detail, exhausted.Detail);

        // Both refuse, and refusing is the safe direction in each case: a wasted day
        // rather than an allowance error in flight, which is fatal.
        Assert.False(stale.Fits);
        Assert.False(exhausted.Fits);
    }

    /// <summary>
    /// A reading stamped ahead of the provider date is unusable too. It cannot be a
    /// reset and it is not a spend figure for this day, so it takes the same verdict
    /// rather than a fourth one.
    /// </summary>
    [Fact]
    public void AReadingStampedAheadOfTheProviderDateIsAlsoStale()
    {
        var ahead = new AllowanceReading(10, 100_000, new DateOnly(2026, 8, 13));

        Assert.Equal(
            AllowanceVerdict.Stale,
            AllowanceRule.Decide(ahead, ProviderDate, reserve: 0, projectedWeight: 1, configuredLimit: 100_000).Verdict);
    }

    /// <summary>
    /// `backfill.daily_unit_allowance` is compared against what the provider reports
    /// and never used in place of it.
    ///
    /// **The reading governs**, so a provider that raised the allowance is spent
    /// against immediately rather than after someone edits a key, and one that cut it
    /// halts the sweep rather than letting it spend into a wall. The difference is
    /// named where the verdict is, which is the same rule the six weight keys follow.
    /// </summary>
    [Fact]
    public void TheProviderReadingGovernsTheConfiguredAllowanceAndTheDriftIsNamed()
    {
        // The provider says 120,000 where config says 100,000. 70,000 above the
        // reserve rather than 50,000, and the extra is spendable.
        var raised = new AllowanceReading(Used: 0, Limit: 120_000, StampedOn: ProviderDate);

        var decision = AllowanceRule.Decide(
            raised, ProviderDate, reserve: 50_000, projectedWeight: 60_000, configuredLimit: 100_000);

        Assert.Equal(AllowanceVerdict.Fits, decision.Verdict);
        Assert.Equal(70_000, decision.Remaining);
        Assert.Contains("120000", decision.Detail, StringComparison.Ordinal);
        Assert.Contains("100000", decision.Detail, StringComparison.Ordinal);
        Assert.Contains("the reading governs", decision.Detail, StringComparison.Ordinal);
    }

    /// <summary>An agreeing reading says nothing extra, so the drift line means something when it appears.</summary>
    [Fact]
    public void NoDriftIsReportedWhenTheReadingAndTheConfiguredAllowanceAgree()
    {
        var decision = AllowanceRule.Decide(
            new AllowanceReading(0, 100_000, ProviderDate),
            ProviderDate, reserve: 50_000, projectedWeight: 10, configuredLimit: 100_000);

        Assert.DoesNotContain("the reading governs", decision.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- the parse ---

    /// <summary>
    /// The payload shape as `/api/user` returned it at 3.1, keys and all, so the parse
    /// is asserted against something the provider actually sent rather than against
    /// what it was assumed to send.
    /// </summary>
    private const string UserPayload = """
        {"name":"redacted","email":"redacted","subscriptionType":"eod-fundamentals",
         "paymentMethod":"card","apiRequests":90518,"apiRequestsDate":"2026-08-11",
         "dailyRateLimit":100000,"extraLimit":0,"inviteToken":"redacted",
         "inviteTokenClicked":0,"subscriptionMode":"paid","canManageOrganizations":false}
        """;

    [Fact]
    public void TheUserPayloadParsesToTheThreeFieldsTheGateNeeds()
    {
        using var doc = JsonDocument.Parse(UserPayload);

        var reading = UnitAllowance.Parse(doc.RootElement);

        Assert.Equal(90_518, reading.Used);
        Assert.Equal(100_000, reading.Limit);
        Assert.Equal(new DateOnly(2026, 8, 11), reading.StampedOn);
    }

    /// <summary>
    /// `extraLimit` read 500 at phase P and 0 at 3.1, so a field this payload carries
    /// can move without notice. The parse takes the three it needs and ignores the
    /// rest rather than binding a shape.
    /// </summary>
    [Fact]
    public void AnUnexpectedFieldDoesNotBreakTheParse()
    {
        using var doc = JsonDocument.Parse(
            """{"apiRequests":1,"apiRequestsDate":"2026-08-12","dailyRateLimit":100000,"somethingNew":[1,2]}""");

        Assert.Equal(1, UnitAllowance.Parse(doc.RootElement).Used);
    }

    /// <summary>
    /// Every field is required and none defaults. A missing `apiRequestsDate` defaulted
    /// to today would make a stale reading look current, which is the one case this
    /// type exists to catch, and a missing `dailyRateLimit` defaulted to 100,000 would
    /// hand a sweep an allowance nobody granted.
    /// </summary>
    [Theory]
    [InlineData("""{"apiRequestsDate":"2026-08-12","dailyRateLimit":100000}""")]
    [InlineData("""{"apiRequests":1,"dailyRateLimit":100000}""")]
    [InlineData("""{"apiRequests":1,"apiRequestsDate":"2026-08-12"}""")]
    [InlineData("""{"apiRequests":1,"apiRequestsDate":"12 August","dailyRateLimit":100000}""")]
    public void AMissingOrUnreadableFieldIsAHaltRatherThanADefault(string payload)
    {
        using var doc = JsonDocument.Parse(payload);

        Assert.Throws<InvalidOperationException>(() => UnitAllowance.Parse(doc.RootElement));
    }

    /// <summary>
    /// The numbers have arrived as JSON numbers on every read so far and nothing
    /// contracts that they will. A string form parses rather than throwing.
    /// </summary>
    [Fact]
    public void ANumberSentAsAStringStillParses()
    {
        using var doc = JsonDocument.Parse(
            """{"apiRequests":"90518","apiRequestsDate":"2026-08-11","dailyRateLimit":"100000"}""");

        var reading = UnitAllowance.Parse(doc.RootElement);

        Assert.Equal(90_518, reading.Used);
        Assert.Equal(100_000, reading.Limit);
    }
}
