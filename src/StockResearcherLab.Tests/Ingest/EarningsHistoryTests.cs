using System.Text.Json;
using StockResearcherLab.Pipeline.Ingest;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// `Earnings::History` out of the payload C03 already fetches [D-96].
///
/// The fixture is the shape 3.1 read off `fundamentals/CCS.US`, including the
/// forward-dated entry with a null `epsActual`, because that entry is the one the read
/// rule exists for.
/// </summary>
public sealed class EarningsHistoryTests
{
    private const string Payload = """
        {"Earnings":{"History":{
          "2026-09-30":{"reportDate":"2026-10-21","date":"2026-09-30","beforeAfterMarket":"AfterMarket",
                        "currency":null,"epsActual":null,"epsEstimate":1.22,"epsDifference":0,"surprisePercent":null},
          "2026-06-30":{"reportDate":"2026-07-22","date":"2026-06-30","beforeAfterMarket":"AfterMarket",
                        "currency":"USD","epsActual":1.3,"epsEstimate":0.63,"epsDifference":0.67,"surprisePercent":106.3492},
          "2026-03-31":{"reportDate":null,"date":"2026-03-31","beforeAfterMarket":"BeforeMarket",
                        "currency":"USD","epsActual":0.88,"epsEstimate":0.61,"epsDifference":0.27,"surprisePercent":44.2623}
        }}}
        """;

    private static IReadOnlyList<EarningsPeriod> Parse(string json = Payload)
    {
        using var doc = JsonDocument.Parse(json);
        return EarningsHistory.Parse(doc.RootElement, "CCS.US");
    }

    [Fact]
    public void EveryPeriodIsTakenAndSortedByPeriodEnd()
    {
        var periods = Parse();

        Assert.Equal(3, periods.Count);
        Assert.Equal(
            [new DateOnly(2026, 3, 31), new DateOnly(2026, 6, 30), new DateOnly(2026, 9, 30)],
            periods.Select(p => p.PeriodEnd).ToArray());

        Assert.All(periods, p => Assert.Equal("CCS.US", p.Ticker));
    }

    /// <summary>
    /// **The surprise is stored as a fraction, not as the percent the provider sends**
    /// [D-96]. 106.3492 becomes 1.063492, which is what the column is named for and the
    /// rule every ratio in this system follows.
    /// </summary>
    [Fact]
    public void TheProvidersPercentIsStoredAsAFraction()
    {
        var june = Parse().Single(p => p.PeriodEnd == new DateOnly(2026, 6, 30));

        Assert.Equal(1.063492f, june.SurpriseFraction!.Value, 5);
        Assert.Equal(1.3f, june.EpsActual);
        Assert.Equal(0.63f, june.EpsEstimate);
        Assert.Equal("AfterMarket", june.BeforeAfterMarket);
    }

    /// <summary>
    /// A row with no `report_date` is stored and is unreadable, exactly as an undated
    /// fundamental row is [D-96, INVARIANT 12]. The row is kept because the period
    /// happened; what it lacks is a date on which it became public.
    /// </summary>
    [Fact]
    public void ARowWithNoReportDateIsStoredWithANullOne()
    {
        var march = Parse().Single(p => p.PeriodEnd == new DateOnly(2026, 3, 31));

        Assert.Null(march.ReportDate);
        Assert.Equal(0.88f, march.EpsActual);
    }

    /// <summary>
    /// The forward-dated entry, which is the one the read rule exists for: a period that
    /// has not been reported yet carries a scheduled `reportDate` and no actual.
    /// </summary>
    [Fact]
    public void AnUnreportedPeriodCarriesItsScheduledDateAndNoActual()
    {
        var september = Parse().Single(p => p.PeriodEnd == new DateOnly(2026, 9, 30));

        Assert.Equal(new DateOnly(2026, 10, 21), september.ReportDate);
        Assert.Null(september.EpsActual);
        Assert.Null(september.SurpriseFraction);

        // Absent stays null rather than becoming zero. A zero surprise is a real value
        // and a company that reported exactly in line is a different fact from one that
        // has not reported [CLAUDE.md section 6].
        Assert.Equal(1.22f, september.EpsEstimate);
    }

    /// <summary>Handed the `Earnings` block alone rather than the whole payload, which is the same answer.</summary>
    [Fact]
    public void TheBlockAloneParsesAsTheWholePayloadDoes()
    {
        using var whole = JsonDocument.Parse(Payload);
        using var block = JsonDocument.Parse(whole.RootElement.GetProperty("Earnings").GetRawText());

        Assert.Equal(
            EarningsHistory.Parse(whole.RootElement, "CCS.US"),
            EarningsHistory.Parse(block.RootElement, "CCS.US"));
    }

    /// <summary>
    /// A ticker the provider carries no earnings for is an empty result rather than a
    /// fault, which is the ordinary case for a recent listing.
    /// </summary>
    [Theory]
    [InlineData("""{"Financials":{}}""")]
    [InlineData("""{"Earnings":{}}""")]
    [InlineData("""{"Earnings":{"History":{}}}""")]
    public void APayloadWithNoHistoryIsEmptyRatherThanAFault(string json)
        => Assert.Empty(Parse(json));

    /// <summary>
    /// An entry this cannot key on is dropped rather than guessed. A fabricated period
    /// end would collide with a real one under the primary key.
    /// </summary>
    [Fact]
    public void AnEntryWithNoUsableDateIsDropped()
    {
        var periods = Parse("""{"Earnings":{"History":{"not-a-date":{"epsActual":1}}}}""");

        Assert.Empty(periods);
    }

    /// <summary>
    /// The entry's own `date` wins over the object key. They agree on every entry 3.1
    /// read and nothing contracts that they will: the key is what the provider chose to
    /// index by, and `date` is what the row is about.
    /// </summary>
    [Fact]
    public void ThePeriodEndComesFromTheEntryRatherThanTheObjectKey()
    {
        var period = Parse("""{"Earnings":{"History":{"2020-01-01":{"date":"2021-03-31","epsActual":1}}}}""")
            .Single();

        Assert.Equal(new DateOnly(2021, 3, 31), period.PeriodEnd);
    }

    // ------------------------------------------------- duplicate period ends ---
    //
    // Taking the period end from `date` gives up the uniqueness the object key had by
    // construction. Two entries resolving to one (ticker, period_end) reach one COPY as
    // two rows with the same conflict target, and Postgres raises "ON CONFLICT DO
    // UPDATE command cannot affect row a second time": a stage failure on one ticker,
    // mid-sweep, after the units for everything before it are spent.

    private static IReadOnlyList<EarningsPeriod> ParseWith(string json, out int collisions)
    {
        using var doc = JsonDocument.Parse(json);
        return EarningsHistory.Parse(doc.RootElement, "CCS.US", out collisions);
    }

    /// <summary>Two entries for one period end, different report dates. The later one wins.</summary>
    [Fact]
    public void TwoEntriesForOnePeriodEndWriteOneRowAndTheLaterReportDateWins()
    {
        var periods = ParseWith("""
            {"Earnings":{"History":{
              "a":{"date":"2021-03-31","reportDate":"2021-04-20","epsActual":1.0},
              "b":{"date":"2021-03-31","reportDate":"2021-05-14","epsActual":1.1}
            }}}
            """, out var collisions);

        var only = Assert.Single(periods);

        Assert.Equal(new DateOnly(2021, 3, 31), only.PeriodEnd);
        Assert.Equal(new DateOnly(2021, 5, 14), only.ReportDate);
        Assert.Equal(1.1f, only.EpsActual);
        Assert.Equal(1, collisions);
    }

    /// <summary>The order in the payload does not decide it; the report date does.</summary>
    [Fact]
    public void TheLaterReportDateWinsWhicheverOrderTheyArriveIn()
    {
        var periods = ParseWith("""
            {"Earnings":{"History":{
              "a":{"date":"2021-03-31","reportDate":"2021-05-14","epsActual":1.1},
              "b":{"date":"2021-03-31","reportDate":"2021-04-20","epsActual":1.0}
            }}}
            """, out _);

        Assert.Equal(new DateOnly(2021, 5, 14), Assert.Single(periods).ReportDate);
    }

    /// <summary>
    /// Both report dates absent. One row, no throw, and the last in document order
    /// wins, which is a property of the payload rather than of a hash order.
    /// </summary>
    [Fact]
    public void TwoUndatedEntriesForOnePeriodEndWriteOneRowWithoutThrowing()
    {
        var periods = ParseWith("""
            {"Earnings":{"History":{
              "a":{"date":"2021-03-31","reportDate":null,"epsActual":1.0},
              "b":{"date":"2021-03-31","reportDate":null,"epsActual":1.1}
            }}}
            """, out var collisions);

        var only = Assert.Single(periods);

        Assert.Null(only.ReportDate);
        Assert.Equal(1.1f, only.EpsActual);
        Assert.Equal(1, collisions);
    }

    /// <summary>
    /// A dated entry beats an undated one whichever came first. The undated one is
    /// unreadable under the `report_date &lt;= date` rule, so keeping it would discard
    /// the only usable row of the pair.
    /// </summary>
    [Fact]
    public void ADatedEntryBeatsAnUndatedOneWhicheverCameFirst()
    {
        var first = ParseWith("""
            {"Earnings":{"History":{
              "a":{"date":"2021-03-31","reportDate":"2021-04-20","epsActual":1.0},
              "b":{"date":"2021-03-31","reportDate":null,"epsActual":9.9}
            }}}
            """, out _);

        Assert.Equal(new DateOnly(2021, 4, 20), Assert.Single(first).ReportDate);

        var second = ParseWith("""
            {"Earnings":{"History":{
              "a":{"date":"2021-03-31","reportDate":null,"epsActual":9.9},
              "b":{"date":"2021-03-31","reportDate":"2021-04-20","epsActual":1.0}
            }}}
            """, out _);

        Assert.Equal(new DateOnly(2021, 4, 20), Assert.Single(second).ReportDate);
    }

    /// <summary>A payload with no duplicates reports none, so the count means something when it appears.</summary>
    [Fact]
    public void APayloadWithNoDuplicatesReportsNoCollisions()
    {
        ParseWith(Payload, out var collisions);

        Assert.Equal(0, collisions);
    }
}
