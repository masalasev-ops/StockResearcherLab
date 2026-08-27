using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Pipeline.Ingest;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// Checkpoint 1.3. Three checks, each failing in isolation, so a passing guard
/// cannot be passing for the wrong reason.
///
/// The rule is a pure function over row counts by date, so these need no database
/// and no provider. That is deliberate: the guard is the component whose false pass
/// is invisible in the output, and a test that could only exercise it end to end
/// would exercise it rarely.
/// </summary>
public sealed class FreshnessGuardTests
{
    private const long AbortBelow = 40_000;
    private const long AlertBelow = 45_000;
    private const decimal Fraction = 0.95m;
    private const int Window = 20;

    /// <summary>A settled trailing population: twenty dates around 50,100.</summary>
    private static List<(DateOnly, long)> Trailing(DateOnly newest, int count = 25, long rows = 50_100)
        => Enumerable.Range(1, count)
            .Select(i => (newest.AddDays(-i), rows + (i % 5) * 20))
            .ToList();

    private static FreshnessVerdict Evaluate(List<(DateOnly, long)> counts, DateOnly session, int window = Window)
        => FreshnessRule.Evaluate(counts, session, AbortBelow, AlertBelow, Fraction, window);

    // -------------------------------------------------------------- the pass ---

    [Fact]
    public void ASettledNewestDatePassesAllThree()
    {
        var today = new DateOnly(2026, 8, 7);
        var counts = new List<(DateOnly, long)> { (today, 50_150) };
        counts.AddRange(Trailing(today));

        var verdict = Evaluate(counts, today);

        Assert.Equal(today, verdict.UsableDate);
        Assert.False(verdict.Alert);
        Assert.False(verdict.SettlednessSkipped);
    }

    // --------------------------------------------------------------- recency ---

    /// <summary>
    /// A provider that has not updated, or a run that was missed. Neither is
    /// something an older date can rescue, so it aborts rather than walking back.
    /// </summary>
    [Fact]
    public void ANewestDateOlderThanTheLastSessionAborts()
    {
        var today = new DateOnly(2026, 8, 7);
        var stale = today.AddDays(-3);

        var counts = new List<(DateOnly, long)> { (stale, 50_150) };
        counts.AddRange(Trailing(stale));

        var verdict = Evaluate(counts, today);

        Assert.Null(verdict.UsableDate);
        Assert.Equal(FreshnessOutcome.Stale, verdict.Steps[0].Outcome);
    }

    [Fact]
    public void AnEmptyPriceTableAborts()
    {
        var verdict = Evaluate([], new DateOnly(2026, 8, 7));

        Assert.Null(verdict.UsableDate);
        Assert.Equal(FreshnessOutcome.NoData, verdict.Steps[0].Outcome);
    }

    // ---------------------------------------------------------- completeness ---

    /// <summary>
    /// Truncated rather than filling. It fails settledness too, and the two mean
    /// different things: this one is a fault to abort on [D-59, D-64].
    /// </summary>
    [Fact]
    public void ACountBelowTheAbortFloorAborts()
    {
        var today = new DateOnly(2026, 8, 7);
        var counts = new List<(DateOnly, long)> { (today, 12_000) };
        counts.AddRange(Trailing(today));

        var verdict = Evaluate(counts, today);

        Assert.Null(verdict.UsableDate);
        Assert.Equal(FreshnessOutcome.Truncated, verdict.Steps[0].Outcome);
    }

    /// <summary>
    /// Above the abort floor, inside the alert band, and settled relative to a
    /// trailing population that is itself low. Passes and is flagged.
    /// </summary>
    [Fact]
    public void ACountInsideTheAlertBandPassesAndIsFlagged()
    {
        var today = new DateOnly(2026, 8, 7);
        var counts = new List<(DateOnly, long)> { (today, 44_000) };
        counts.AddRange(Trailing(today, rows: 44_500));

        var verdict = Evaluate(counts, today);

        Assert.Equal(today, verdict.UsableDate);
        Assert.True(verdict.Alert);
    }

    // ----------------------------------------------------------- settledness ---

    /// <summary>
    /// The measured case. 2026-08-06 read 44,204 against settled days of 50,151 to
    /// 50,244, which is 88 percent of the median and below the 0.95 fraction. Not an
    /// abort: the walk-back takes the date before it [D-70].
    /// </summary>
    [Fact]
    public void AStillFillingDateIsSkippedAndTheDateBeforeItIsUsed()
    {
        var today = new DateOnly(2026, 8, 7);
        var yesterday = today.AddDays(-1);

        var counts = new List<(DateOnly, long)> { (today, 44_204), (yesterday, 50_172) };
        counts.AddRange(Trailing(yesterday));

        var verdict = Evaluate(counts, today);

        Assert.Equal(yesterday, verdict.UsableDate);
        Assert.Equal(FreshnessOutcome.Unsettled, verdict.Steps[0].Outcome);
        Assert.Equal(FreshnessOutcome.Usable, verdict.Steps[1].Outcome);
    }

    [Fact]
    public void TheWalkBackPassesEveryStillFillingDateAndLandsOnTheFirstSettledOne()
    {
        var today = new DateOnly(2026, 8, 7);
        var settled = today.AddDays(-3);

        // Three short dates in front of a settled population, which is what a long
        // weekend of accretion would leave.
        var counts = new List<(DateOnly, long)>
        {
            (today, 44_000), (today.AddDays(-1), 44_100), (today.AddDays(-2), 44_200),
        };
        counts.AddRange(Trailing(today.AddDays(-2)));

        var verdict = Evaluate(counts, today);

        Assert.Equal(settled, verdict.UsableDate);
        Assert.Equal(3, verdict.Steps.Count(s => s.Outcome == FreshnessOutcome.Unsettled));
    }

    /// <summary>
    /// A limit of the relative test, asserted rather than left to be discovered.
    ///
    /// Settledness compares a date against its own trailing population, so a
    /// population that is uniformly short cannot be detected by it: every date is
    /// 100 percent of the median. **Completeness is the check for that**, which is
    /// exactly the division of labour D-64 records when it says the absolute floors
    /// answer "is this file catastrophically short" and were never the tool for
    /// "is this file still filling".
    ///
    /// So a market-wide halving that persisted for a month would pass settledness
    /// and be caught by the abort floor, and a market-wide halving that stayed above
    /// 40,000 would be caught by neither. That is a known gap rather than an
    /// oversight, and it is the reason the two checks both exist.
    /// </summary>
    [Fact]
    public void AUniformlyShortPopulationPassesSettlednessAndIsCompletenessProblem()
    {
        var today = new DateOnly(2026, 8, 7);

        // Every date short by the same amount. Relative to each other they are fine.
        var counts = new List<(DateOnly, long)> { (today, 44_000) };
        counts.AddRange(Trailing(today, rows: 44_000));

        var verdict = Evaluate(counts, today);

        Assert.Equal(today, verdict.UsableDate);
        Assert.True(verdict.Alert, "It sits in the alert band, which is what does report it.");

        // Drop the same population below the abort floor and completeness catches it.
        var truncated = new List<(DateOnly, long)> { (today, 39_000) };
        truncated.AddRange(Trailing(today, rows: 39_000));

        Assert.Null(Evaluate(truncated, today).UsableDate);
    }

    /// <summary>
    /// A26's fourth case. Below a full window there is no median to compare
    /// against. Aborting means the first run ever cannot bootstrap; passing silently
    /// turns the check off when the data is least trustworthy. It is skipped
    /// visibly, recency and completeness still apply, and the run log says so.
    /// </summary>
    [Fact]
    public void TooLittleHistorySkipsSettlednessVisiblyRatherThanAbortingOrPassingSilently()
    {
        var today = new DateOnly(2026, 8, 7);

        // Four dates where the window wants twenty.
        var counts = new List<(DateOnly, long)>
        {
            (today, 44_204), (today.AddDays(-1), 50_100),
            (today.AddDays(-2), 50_120), (today.AddDays(-3), 50_090),
        };

        var verdict = Evaluate(counts, today);

        // The still-filling date is accepted, because nothing can say it is short.
        Assert.Equal(today, verdict.UsableDate);
        Assert.True(verdict.SettlednessSkipped);

        // And it is not silent.
        Assert.Contains("settledness skipped", verdict.Summary(), StringComparison.Ordinal);

        // Completeness still applies during bootstrap, which is the reason skipping
        // is not the same as switching the guard off.
        var truncated = new List<(DateOnly, long)> { (today, 9_000) };
        Assert.Null(Evaluate(truncated, today).UsableDate);
    }

    // -------------------------------------------------------------- calendar ---

    [Fact]
    public void TheCalendarSkipsWeekendsAndHolidays()
    {
        // The live payload's shape, from 1.9's sweep.
        using var doc = JsonDocument.Parse("""
            {
              "TradingHours": { "Open": "09:30:00", "Close": "16:00:00", "WorkingDays": "Mon,Tue,Wed,Thu,Fri" },
              "ExchangeHolidays": {
                "0": { "Holiday": "Independence Day", "Date": "2026-07-03", "Type": "official" }
              }
            }
            """);

        var calendar = ExchangeCalendar.Parse(doc.RootElement);

        // Saturday 2026-08-08 falls back to Friday the 7th.
        Assert.Equal(new DateOnly(2026, 8, 7), calendar.MostRecentCompletedSession(new DateOnly(2026, 8, 8)));

        // A holiday on a Friday falls back to the Thursday.
        Assert.Equal(new DateOnly(2026, 7, 2), calendar.MostRecentCompletedSession(new DateOnly(2026, 7, 3)));

        // A trading day is itself, because this runs after the close.
        Assert.Equal(new DateOnly(2026, 8, 5), calendar.MostRecentCompletedSession(new DateOnly(2026, 8, 5)));
    }

    [Fact]
    public void ACalendarWithNoWorkingDaysIsAFaultRatherThanAPass()
    {
        using var doc = JsonDocument.Parse("""{"TradingHours":{"Open":"09:30:00"}}""");

        Assert.Throws<InvalidOperationException>(() => ExchangeCalendar.Parse(doc.RootElement));
    }

    // ------------------------------------------------------------- the read ---

    /// <summary>
    /// **The guard reads no date after the one it was asked about** [R.1, `CLAUDE.md`
    /// section 5]. Every test above this line exercises the rule, which is a pure
    /// function and was never the problem. The read that feeds it was unbounded, and
    /// an unbounded read makes the stage a function of when it ran rather than of the
    /// date it was given.
    ///
    /// **Asserted on the SQL rather than through a database**, for the reason the class
    /// comment gives: the guard's false pass is invisible in the output, so the check
    /// has to be one that runs every time rather than one that needs a store in a
    /// particular state.
    /// </summary>
    [Fact]
    public void TheReadIsBoundedByTheDateTheStageWasGiven()
    {
        var sql = FreshnessGuard.CountsSql(new DateOnly(2026, 8, 25), Window);

        Assert.Contains("date <= DATE '2026-08-25'", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two live failures the bound closes, stated as the two dates that must not
    /// appear in each other's read [R.1].
    ///
    /// A run for 2026-08-25 must not see 2026-08-26, which is what aborted the evening
    /// of 2026-08-26 on a session still arriving. A run for 2026-08-26 must still see
    /// its own date, because a bound that excluded it would turn every night into a
    /// walk-back and hide a genuinely stale provider.
    /// </summary>
    [Theory]
    [InlineData("2026-08-25", "2026-08-26", false)]
    [InlineData("2026-08-26", "2026-08-26", true)]
    public void ADateIsInsideItsOwnReadAndOutsideAnEarlierOne(string asOf, string other, bool inside)
    {
        var sql = FreshnessGuard.CountsSql(DateOnly.Parse(asOf, CultureInfo.InvariantCulture), Window);
        var bound = DateOnly.Parse(asOf, CultureInfo.InvariantCulture);
        var candidate = DateOnly.Parse(other, CultureInfo.InvariantCulture);

        Assert.Equal(inside, candidate <= bound);
        Assert.Contains("date <= DATE '" + asOf + "'", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole statement, pinned. Shape and date format together, so a rewrite that
    /// kept the bound and lost the ordering, or kept both and rendered the date some
    /// other way, is caught here rather than at 18:33.
    ///
    /// **The locale half of this is closed above the test.** `InvariantGlobalization`
    /// is true for every project here, so a date reaching SQL as `25/08/2026` is not
    /// reachable from a culture setting; the format string is asserted anyway, because
    /// what closes it is a property of the build rather than of this method.
    /// </summary>
    [Fact]
    public void TheStatementIsExactlyThis()
        => Assert.Equal(
            "SELECT date, count(*) FROM price_daily WHERE date <= DATE '2026-08-25' " +
            "GROUP BY date ORDER BY date DESC LIMIT 80;",
            FreshnessGuard.CountsSql(new DateOnly(2026, 8, 25), Window));

    /// <summary>
    /// The window still sets how far back the read reaches, unchanged by the bound.
    /// </summary>
    [Fact]
    public void TheWindowStillSetsTheLimit()
        => Assert.Contains(
            "LIMIT " + ((Window * 2) + 40).ToString(CultureInfo.InvariantCulture),
            FreshnessGuard.CountsSql(new DateOnly(2026, 8, 25), Window),
            StringComparison.Ordinal);
}
