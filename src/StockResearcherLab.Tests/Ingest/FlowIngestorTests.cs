using System.Text.Json;
using StockResearcherLab.Core;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// Checkpoint 1.7. The grain is the filing plus the line's position in it, which
/// D-68's reopening clause settled at 1.7 against real data.
///
/// A7 proposed a key made of the transaction's own attributes and left the sweep to
/// confirm it. Over 1,069 real transactions across eight tickers it collided 172
/// times; adding security title, price and shares-owned-after still left 5. Two line
/// items in one filing can be identical on every value the provider sends, and the
/// ordinary case is an option exercise reported as common stock acquired and as
/// restricted stock units disposed.
/// </summary>
public sealed class FlowIngestorTests
{
    /// <summary>
    /// The collision measured against the provider, reduced to a fixture. Same
    /// filing, same owner, same code, same date, same share count, and two genuinely
    /// different lines.
    /// </summary>
    private const string CollidingFiling = """
        [{
          "accession_number": "0001576940-26-000059",
          "filed_at": "2026-08-03",
          "non_derivative": [
            {"reporting_owner_name":"DIXON JOHN SCOTT","reporting_owner_cik":"0001690851",
             "transaction_date":"2026-08-01T00:00:00+00:00","transaction_code":"M",
             "security_title":"Common Stock","shares_amount":1649,"shares_owned_after":14688,
             "acquired_or_disposed":"A"}
          ],
          "derivative": [
            {"reporting_owner_name":"DIXON JOHN SCOTT","reporting_owner_cik":"0001690851",
             "transaction_date":"2026-08-01T00:00:00+00:00","transaction_code":"M",
             "security_title":"Restricted Stock Units","shares_amount":1649,"price_per_share":0,
             "shares_owned_after":1648,"acquired_or_disposed":"D"}
          ]
        }]
        """;

    private static IReadOnlyList<JsonElement> Filings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    // ------------------------------------------------------------- the grain ---

    /// <summary>
    /// The fixture that makes the grain mean something. Both lines survive, and they
    /// differ only by side and ordinal: on A7's tuple they would have collapsed into
    /// one and the count would have looked correct.
    /// </summary>
    [Fact]
    public void TwoLinesIdenticalOnEveryAttributeAreStillTwoRows()
    {
        var rows = FlowIngestor.ParseFilings("CCS.US", Filings(CollidingFiling));

        Assert.Equal(2, rows.Count);

        // A7's proposed key does not separate them.
        var a7 = rows.Select(r => $"{r.Ticker}|{r.Accession}|{r.OwnerName}|{r.Code}|{r.TransactionDate}|{r.Shares}");
        Assert.Single(a7.Distinct());

        // The grain does.
        var grain = rows.Select(r => $"{r.Ticker}|{r.Accession}|{r.Side}|{r.Ordinal}");
        Assert.Equal(2, grain.Distinct().Count());
    }

    [Fact]
    public void EachArrayIsNumberedIndependentlyFromZero()
    {
        var rows = FlowIngestor.ParseFilings("CCS.US", Filings("""
            [{"accession_number":"A1","filed_at":"2026-08-03",
              "non_derivative":[{"transaction_code":"S"},{"transaction_code":"S"}],
              "derivative":[{"transaction_code":"M"}]}]
            """));

        Assert.Equal(3, rows.Count);

        var nonDeriv = rows.Where(r => r.Side == "non_derivative").Select(r => r.Ordinal).ToList();
        var deriv = rows.Where(r => r.Side == "derivative").Select(r => r.Ordinal).ToList();

        Assert.Equal([0, 1], nonDeriv);
        Assert.Equal([0], deriv);
    }

    [Fact]
    public void RowsAreOrderedByTheirOwnKeySoCopyOrderIsStable()
    {
        var rows = FlowIngestor.ParseFilings("CCS.US", Filings("""
            [{"accession_number":"B2","non_derivative":[{"transaction_code":"S"}]},
             {"accession_number":"A1","non_derivative":[{"transaction_code":"P"}]}]
            """));

        Assert.Equal(["A1", "B2"], rows.Select(r => r.Accession));
    }

    // -------------------------------------------------------------- parsing ---

    /// <summary>
    /// The S4 rubric disqualifies option exercises and scheduled plan activity, so a
    /// count that cannot separate an open-market purchase from an award is not the
    /// count the screen needs [D-61]. transaction_code is what does that.
    /// </summary>
    [Fact]
    public void TransactionCodeAndSideSurviveIntact()
    {
        var rows = FlowIngestor.ParseFilings("CCS.US", Filings(CollidingFiling));

        Assert.All(rows, r => Assert.Equal("M", r.Code));
        Assert.Contains(rows, r => r.SecurityTitle == "Common Stock" && r.AcquiredOrDisposed == "A");
        Assert.Contains(rows, r => r.SecurityTitle == "Restricted Stock Units" && r.AcquiredOrDisposed == "D");
    }

    /// <summary>
    /// The provider sends transaction dates as ISO instants and report dates as
    /// plain dates. A trading date is a label the exchange gave a session rather
    /// than a timezone conversion, so the date part is taken as written.
    /// </summary>
    [Fact]
    public void AnIsoInstantAndAPlainDateBothParseToTheDateAsWritten()
    {
        var rows = FlowIngestor.ParseFilings("CCS.US", Filings(CollidingFiling));

        Assert.All(rows, r => Assert.Equal(new DateOnly(2026, 8, 1), r.TransactionDate));
        Assert.All(rows, r => Assert.Equal(new DateOnly(2026, 8, 3), r.FiledAt));
    }

    /// <summary>
    /// Absent is not zero. A missing price on an award is unknown, and a zero would
    /// contribute nothing to a dollar total while looking like a real trade at no
    /// cost. This is one of the five probe patterns this phase must not inherit.
    /// </summary>
    [Fact]
    public void AnAbsentPriceOrShareCountStaysNull()
    {
        var rows = FlowIngestor.ParseFilings("CCS.US", Filings("""
            [{"accession_number":"A1","non_derivative":[{"transaction_code":"A"}]}]
            """));

        var row = Assert.Single(rows);
        Assert.Null(row.Price);
        Assert.Null(row.Shares);
        Assert.Null(row.TotalValue);
        Assert.Null(row.OwnerName);
    }

    [Fact]
    public void AFilingWithNoAccessionNumberIsSkippedRatherThanKeyedOnNothing()
    {
        var rows = FlowIngestor.ParseFilings("CCS.US", Filings("""
            [{"non_derivative":[{"transaction_code":"P"}]},
             {"accession_number":"A1","non_derivative":[{"transaction_code":"P"}]}]
            """));

        Assert.Single(rows);
        Assert.Equal("A1", rows[0].Accession);
    }

    // ------------------------------------------------------------- holders ---

    /// <summary>
    /// Keyed "0", "1", "2" rather than an array, which is why a caller expecting an
    /// array reads nothing rather than failing [1.9].
    /// </summary>
    [Fact]
    public void HoldersAreReadFromAnObjectKeyedByPosition()
    {
        using var doc = JsonDocument.Parse("""
            {"0":{"name":"BlackRock Inc","date":"2026-03-31","currentShares":5136591,
                  "change":-97623,"change_p":-1.8651},
             "1":{"name":"Dimensional Fund Advisors, Inc.","date":"2026-03-31",
                  "currentShares":1961284,"change":-4818,"change_p":-0.2451}}
            """);

        var rows = FlowIngestor.ParseHolders("CCS.US", doc.RootElement);

        Assert.Equal(2, rows.Count);
        Assert.Equal("BlackRock Inc", rows[0].HolderName);
        Assert.Equal(new DateOnly(2026, 3, 31), rows[0].ReportDate);
        Assert.Equal(5136591m, rows[0].Shares);
        Assert.Equal(-97623m, rows[0].Change);
    }

    /// <summary>
    /// Both are key parts at the declared grain, so a row missing either cannot be
    /// written and is dropped rather than given a placeholder date or name.
    /// </summary>
    [Fact]
    public void AHolderWithNoNameOrNoDateIsDroppedRatherThanGivenAPlaceholder()
    {
        using var doc = JsonDocument.Parse("""
            {"0":{"date":"2026-03-31","currentShares":10},
             "1":{"name":"Someone","currentShares":10},
             "2":{"name":"Real Holder","date":"2026-03-31","currentShares":10}}
            """);

        var row = Assert.Single(FlowIngestor.ParseHolders("CCS.US", doc.RootElement));
        Assert.Equal("Real Holder", row.HolderName);
    }

    [Fact]
    public void AnUnexpectedHoldersShapeYieldsNothing()
    {
        foreach (var body in new[] { "[]", "\"NA\"", "null" })
        {
            using var doc = JsonDocument.Parse(body);
            Assert.Empty(FlowIngestor.ParseHolders("CCS.US", doc.RootElement));
        }
    }

    // ------------------------------------------------------------ declared ---

    [Fact]
    public void TheDeclaredWritesAreTheTwoSourceTablesAtTheirOwnGrain()
    {
        var stage = new FlowIngestor(EodhdClientDouble());

        Assert.Equal(2, stage.WriteSet.Count);

        var insider = stage.WriteSet.Single(w => w.Table == "insider_transaction");
        Assert.Equal(FlowIngestor.InsiderColumns, insider.Columns);
        Assert.Contains("transaction_ordinal", insider.Columns);
        Assert.Contains("accession_number", insider.Columns);

        var holding = stage.WriteSet.Single(w => w.Table == "institutional_holding");
        Assert.Equal(FlowIngestor.HoldingColumns, holding.Columns);

        // flow_daily is derived by C34 and is not written here [D-61].
        Assert.DoesNotContain(stage.WriteSet, w => w.Table == "flow_daily");
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
