using System.Text.Json;
using StockResearcherLab.Pipeline.Ingest;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// 3.10's per-ticker parse, and the absence that is a decision rather than an omission.
///
/// **The per-ticker feeds differ from the bulk ones in exactly one way that matters**:
/// the ticker is not in the payload. The bulk feeds send `code` and `exchange`
/// separately, which 1.8's fixture exists for; these send neither, because the ticker is
/// what was asked for. A parser reading `code` against these returns nothing and the
/// stage succeeds, which is 1.8's defect in the mirror.
///
/// **`events` holds no earnings row from this pass and that is asserted rather than
/// assumed** [3.10's done-when]. `calendar/earnings` sends no date on which a schedule
/// became public, so a backfilled earnings row is indistinguishable from a
/// live-accumulated one and phase 5 could no longer separate them. The absence is the
/// decision, so it takes a test: an omission and a decision look identical in an empty
/// column.
/// </summary>
public sealed class EventsRangeTests
{
    private const string Ticker = "SRLEVT.US";

    /// <summary>
    /// The shape 3.1 measured on `div/SPY.US`: eight fields of which two reach the
    /// table.
    /// </summary>
    private const string DividendPayload = """
        [
          {"date":"2024-02-08","declarationDate":"2024-01-24","recordDate":"2024-02-09",
           "paymentDate":"2024-03-15","period":"Quarterly","value":0.24,
           "unadjustedValue":0.24,"currency":"USD"}
        ]
        """;

    private const string SplitPayload = """
        [{"date":"2009-06-29","split":"3.000000/1.000000"}]
        """;

    [Fact]
    public void ADividendKeepsItsExDateAndItsDeclarationDateAndDiscardsTheOtherTwo()
    {
        var rows = Parse(DividendPayload, EventsIngestor.DividendEx);

        var row = Assert.Single(rows);

        Assert.Equal(Ticker, row.Ticker);
        Assert.Equal(EventsIngestor.DividendEx, row.EventType);

        // The ex-date is the event. recordDate and paymentDate are the two a reader
        // reaches for by name and neither is it.
        Assert.Equal(new DateOnly(2024, 2, 8), row.EventDate);
        Assert.Equal(new DateOnly(2024, 1, 24), row.AnnouncedDate);
    }

    /// <summary>
    /// A split's payload carries no date on which it became public, so
    /// <c>announced_date</c> stays null. Null means unknown rather than simultaneous
    /// [`CLAUDE.md` §6].
    /// </summary>
    [Fact]
    public void ASplitCarriesItsEffectiveDateAndNoAnnouncement()
    {
        var row = Assert.Single(Parse(SplitPayload, EventsIngestor.Split));

        Assert.Equal(Ticker, row.Ticker);
        Assert.Equal(EventsIngestor.Split, row.EventType);
        Assert.Equal(new DateOnly(2009, 6, 29), row.EventDate);
        Assert.Null(row.AnnouncedDate);
    }

    /// <summary>
    /// **The ticker comes from the request rather than from the payload**, which is the
    /// one structural difference from the bulk form. Neither `code` nor `exchange` is
    /// present here, so a parser reading them would return an empty list while the
    /// stage reported success.
    /// </summary>
    [Fact]
    public void TheTickerComesFromTheRequestBecauseThePayloadDoesNotCarryIt()
    {
        using var doc = JsonDocument.Parse(SplitPayload);

        Assert.DoesNotContain("code", SplitPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exchange", SplitPayload, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            "OTHER.US",
            Assert.Single(EventsIngestor.ParsePerTicker(doc.RootElement, "OTHER.US", EventsIngestor.Split)).Ticker);
    }

    /// <summary>
    /// An empty array is an ordinary name rather than a fault. 3.1 measured
    /// `splits/SPY.US` and `splits/CCS.US` both at zero rows, and SPY is the benchmark.
    /// This is why the sweep resumes on an attempt record rather than on presence in
    /// `events` [0012].
    /// </summary>
    [Fact]
    public void ATickerWithNoDistributionsParsesToNothingRatherThanFailing()
        => Assert.Empty(Parse("[]", EventsIngestor.Split));

    /// <summary>
    /// A row with no date is dropped rather than defaulted. An undated event is not an
    /// event dated today.
    /// </summary>
    [Fact]
    public void ARowWithNoDateIsDroppedRatherThanDefaulted()
        => Assert.Empty(Parse("""[{"split":"2.000000/1.000000"}]""", EventsIngestor.Split));

    /// <summary>
    /// **The absence is the decision** [3.10]. Neither per-ticker feed can produce an
    /// earnings row, so the range pass cannot write one whatever it is served. Asserted
    /// against a payload deliberately carrying an earnings-shaped field.
    /// </summary>
    [Fact]
    public void TheRangePassCannotProduceAnEarningsRowWhateverItIsServed()
    {
        const string Contaminated = """
            [{"date":"2024-02-08","report_date":"2024-02-08","declarationDate":"2024-01-24"}]
            """;

        foreach (var type in new[] { EventsIngestor.Split, EventsIngestor.DividendEx })
        {
            Assert.DoesNotContain(
                Parse(Contaminated, type),
                r => string.Equals(r.EventType, EventsIngestor.Earnings, StringComparison.Ordinal));
        }
    }

    private static IReadOnlyList<EventsIngestor.EventRow> Parse(string json, string eventType)
    {
        using var doc = JsonDocument.Parse(json);
        return EventsIngestor.ParsePerTicker(doc.RootElement, Ticker, eventType);
    }
}
