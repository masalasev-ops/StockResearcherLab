using System.Text.Json;
using StockResearcherLab.Core;
using StockResearcherLab.Pipeline.Ingest;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// Checkpoint 1.8, the events half. Three sources with three payload shapes, one
/// table, and the narrowing to the universe is what makes a global calendar a US
/// feed.
/// </summary>
public sealed class EventsIngestorTests
{
    private static readonly HashSet<string> Universe =
        new(["CCS.US", "NVDA.US"], StringComparer.Ordinal);

    /// <summary>
    /// The measured shape. `report_date` is the date the company reports and
    /// `date` beside it is the fiscal period end, which is the pair most likely to
    /// be read the wrong way round: the period end is the earlier of the two and
    /// looks like the event.
    /// </summary>
    private const string EarningsBody = """
        {"type":"Earnings","description":"Historical and upcoming Earnings","symbols":"CCS.US",
         "earnings":[
           {"code":"CCS.US","report_date":"2026-07-22","date":"2026-06-30",
            "before_after_market":"AfterMarket","currency":"USD","actual":1.3,"estimate":0.63},
           {"code":"CCS.US","report_date":"2026-10-21","date":"2026-09-30",
            "before_after_market":"AfterMarket","currency":null,"actual":null,"estimate":1.22},
           {"code":"WINSOMBR.BSE","report_date":"2026-08-10","date":"2026-06-30",
            "before_after_market":"BeforeMarket","currency":null,"actual":null,"estimate":null}]}
        """;

    private static JsonElement Parse(string json, out JsonDocument doc)
    {
        doc = JsonDocument.Parse(json);
        return doc.RootElement;
    }

    // ------------------------------------------------------------- earnings ---

    [Fact]
    public void TheEventDateIsTheReportDateAndNotThePeriodEnd()
    {
        var root = Parse(EarningsBody, out var doc);
        using (doc)
        {
            var rows = EventsIngestor.ParseEarnings(root, Universe);

            Assert.All(rows, r => Assert.Equal(EventsIngestor.Earnings, r.EventType));
            Assert.Equal(
                [new DateOnly(2026, 7, 22), new DateOnly(2026, 10, 21)],
                rows.Select(r => r.EventDate));

            // The period ends are 2026-06-30 and 2026-09-30 and neither is here.
            Assert.DoesNotContain(rows, r => r.EventDate == new DateOnly(2026, 6, 30));
        }
    }

    /// <summary>
    /// The calendar is global rather than US: a 90 day window returned 22,286 rows
    /// across every exchange the provider carries [1.8]. Nothing in the request
    /// narrows it, so the universe does.
    /// </summary>
    [Fact]
    public void AnExchangeOutsideTheUniverseIsDropped()
    {
        var root = Parse(EarningsBody, out var doc);
        using (doc)
        {
            var rows = EventsIngestor.ParseEarnings(root, Universe);

            Assert.DoesNotContain(rows, r => r.Ticker == "WINSOMBR.BSE");
            Assert.All(rows, r => Assert.Equal("CCS.US", r.Ticker));
        }
    }

    /// <summary>
    /// The payload carries no date on which the schedule became public, so null
    /// means unknown rather than "announced the same day" [CLAUDE.md section 6].
    /// </summary>
    [Fact]
    public void AnEarningsRowHasNoAnnouncedDateBecauseTheSourceSendsNone()
    {
        var root = Parse(EarningsBody, out var doc);
        using (doc)
        {
            Assert.All(EventsIngestor.ParseEarnings(root, Universe), r => Assert.Null(r.AnnouncedDate));
        }
    }

    // ------------------------------------------------- splits and dividends ---

    /// <summary>
    /// The bulk feeds send `code` and `exchange` separately where the calendar
    /// sends one qualified symbol. A parser that read `code` alone would match
    /// nothing against a universe of `CCS.US` and write no rows while succeeding.
    /// </summary>
    [Fact]
    public void TheBulkFeedsTickerIsAssembledFromCodeAndExchange()
    {
        var root = Parse("""
            [{"code":"NVDA","exchange":"US","date":"2024-06-10","split":"10.000000/1.000000"},
             {"code":"ALHYX","exchange":"US","date":"2024-06-10","split":"177.000000/674.000000"}]
            """, out var doc);

        using (doc)
        {
            var row = Assert.Single(EventsIngestor.ParseBulk(root, Universe, "splits"));

            Assert.Equal("NVDA.US", row.Ticker);
            Assert.Equal(EventsIngestor.Split, row.EventType);
            Assert.Equal(new DateOnly(2024, 6, 10), row.EventDate);
            Assert.Null(row.AnnouncedDate);
        }
    }

    /// <summary>
    /// A dividend payload carries four dates and exactly one reaches `event_date`.
    /// `date` is the ex-date, which is the one that moves the price, and
    /// `declarationDate` is when it became public, which is what `announced_date`
    /// is for. `recordDate` and `paymentDate` are neither.
    /// </summary>
    [Fact]
    public void ADividendKeepsTheExDateAndTheDeclarationDateAndDiscardsTheOtherTwo()
    {
        var root = Parse("""
            [{"code":"CCS","exchange":"US","date":"2026-05-27","dividend":"0.32000",
              "declarationDate":"2026-05-06","recordDate":"2026-05-27","paymentDate":"2026-06-10",
              "period":"Quarterly","currency":"USD"}]
            """, out var doc);

        using (doc)
        {
            var row = Assert.Single(EventsIngestor.ParseBulk(root, Universe, "dividends"));

            Assert.Equal(EventsIngestor.DividendEx, row.EventType);
            Assert.Equal(new DateOnly(2026, 5, 27), row.EventDate);
            Assert.Equal(new DateOnly(2026, 5, 6), row.AnnouncedDate);
        }
    }

    [Fact]
    public void AWeekendReturnsAnEmptyArrayRatherThanAnError()
    {
        var root = Parse("[]", out var doc);
        using (doc)
        {
            Assert.Empty(EventsIngestor.ParseBulk(root, Universe, "splits"));
            Assert.Empty(EventsIngestor.ParseBulk(root, Universe, "dividends"));
        }
    }

    [Fact]
    public void ARowMissingAKeyPartIsDroppedRatherThanGivenAPlaceholder()
    {
        var root = Parse("""
            [{"exchange":"US","date":"2026-05-27"},
             {"code":"CCS","date":"2026-05-27"},
             {"code":"CCS","exchange":"US"},
             {"code":"CCS","exchange":"US","date":"2026-05-27","declarationDate":"2026-05-06"}]
            """, out var doc);

        using (doc)
        {
            Assert.Single(EventsIngestor.ParseBulk(root, Universe, "dividends"));
        }
    }

    // ------------------------------------------------------------ declared ---

    /// <summary>
    /// `announced_date` is deliberately not a key part: it is the attribute most
    /// likely to be revised, and a revision should update the event rather than
    /// insert a second one for the same date [0002].
    /// </summary>
    [Fact]
    public void TheDeclaredWriteIsEventsAndTheKeyExcludesTheAnnouncedDate()
    {
        var stage = new EventsIngestor(EodhdClientDouble());

        // Two writes since 3.10, where this asserted one. `event_fetch_attempt` is what
        // the distributions sweep resumes on [D-99, 0012], and it is named rather than
        // counted: a count would pass on any second write and this test is about which
        // ones there are.
        var write = Assert.Single(stage.WriteSet, w => string.Equals(w.Table, "events", StringComparison.Ordinal));
        Assert.Equal(EventsIngestor.EventColumns, write.Columns);
        Assert.Contains("announced_date", write.Columns);

        var attempt = Assert.Single(
            stage.WriteSet, w => string.Equals(w.Table, "event_fetch_attempt", StringComparison.Ordinal));
        Assert.Equal(EventsIngestor.AttemptColumns, attempt.Columns);

        Assert.Equal(2, stage.WriteSet.Count);

        // `price_daily` since 3.10, read by the range pass alone for the in-window
        // delisted names [D-101]. The nightly path reads no prices.
        Assert.Equal(["security", "price_daily"], stage.ReadSet);
    }

    private static StockResearcherLab.Data.Eodhd.EodhdClient EodhdClientDouble()
        => new(new HttpClient(new NeverCalled()), "fake-token", new FixedClock(
            new DateTimeOffset(2026, 8, 9, 3, 0, 0, TimeSpan.Zero), new DateOnly(2026, 8, 9)));

    private sealed class NeverCalled : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("This test must not make a call.");
    }
}
