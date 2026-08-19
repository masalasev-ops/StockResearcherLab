using System.Text.Json;
using StockResearcherLab.Core;
using StockResearcherLab.Data.Eodhd;
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
    /// **The parse stores the date it was sent and never manufactures one** [item 49].
    ///
    /// `insider_transaction` holds ten rows whose `transaction_date` is outside any
    /// plausible range: one at `0024-01-01` and nine across 2027 to 2033, the furthest
    /// being `BEAM.US` at 2033-06-06 against a filing of 2023-06-08. The question that
    /// item asks is whether the provider sent those or this parse made them, and this is
    /// the half of the answer that can be pinned in a fixture.
    ///
    /// **One named field, parsed exactly, or null.** `transaction_date` is read by name
    /// rather than by position, so no other date in the payload can reach the column, and
    /// `TryParseExact` on `yyyy-MM-dd` neither expands a two-digit year nor accepts a
    /// near-miss. **A date this parse cannot read becomes null rather than a guess**,
    /// which is the null-means-unknown rule applied where it costs something: an
    /// unreadable date silently coerced would be indistinguishable from a real one.
    ///
    /// **What this does not decide** is whether the ingest should reject an out-of-range
    /// date rather than store it. That is item 49's authored half, and this fixture is
    /// what would fail when it is answered, which is the point of pinning it now.
    /// </summary>
    [Fact]
    public void TheParseStoresTheDateItWasSentAndNullsWhatItCannotRead()
    {
        var rows = FlowIngestor.ParseFilings("CCS.US", Filings("""
            [{"accession_number":"A1","filed_at":"2023-06-08",
              "derivative":[
                {"transaction_code":"A","transaction_date":"2033-06-06"},
                {"transaction_code":"A","transaction_date":"0024-01-01"},
                {"transaction_code":"A","transaction_date":"24-01-01"},
                {"transaction_code":"A","transaction_date":"2023-13-45"},
                {"transaction_code":"A","transaction_date":"2023-06-08T00:00:00Z"},
                {"transaction_code":"A","transaction_date":""},
                {"transaction_code":"A"}]}]
            """));

        var dates = rows.OrderBy(r => r.Ordinal).Select(r => r.TransactionDate).ToList();

        // Sent in the future, stored in the future. Nothing bounds it today.
        Assert.Equal(new DateOnly(2033, 6, 6), dates[0]);

        // Sent as year 24, stored as year 24. This is the row `BCO.US` carries, and it
        // is the provider's string rather than a two-digit year this parse widened.
        Assert.Equal(new DateOnly(24, 1, 1), dates[1]);

        // **A two-digit year is not silently expanded**, which is what would have made
        // the row above this parse's doing rather than the payload's.
        Assert.Null(dates[2]);

        // An impossible month and day is null rather than rolled forward.
        Assert.Null(dates[3]);

        // A timestamp is taken by its first ten characters, which is the one leniency.
        Assert.Equal(new DateOnly(2023, 6, 8), dates[4]);

        // Empty and absent are both unknown, and neither becomes the filing date.
        Assert.Null(dates[5]);
        Assert.Null(dates[6]);

        // **No row borrowed `filed_at`**, which is the substitution that would turn an
        // unknown transaction date into a plausible one nobody could later question.
        Assert.DoesNotContain(new DateOnly(2023, 6, 8), dates.Skip(5));
    }

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

    // ----------------------------------------------------------- shortfalls ---

    /// <summary>
    /// D-71 asks the run log for two figures per run, the count of tickers that
    /// under-delivered and the total row shortfall, and for the position per
    /// affected ticker. Today's figures are the baseline, so a line that reports
    /// them wrongly makes the baseline wrong rather than merely untidy.
    /// </summary>
    [Fact]
    public void TheRunLogLineCarriesBothCountsAndAPositionPerTicker()
    {
        var line = FlowIngestor.DescribeShortfalls(
        [
            new FlowIngestor.Shortfall("NVDA.US", 1, ShortfallPosition.Final),
            new FlowIngestor.Shortfall("AAON.US", 2, ShortfallPosition.Interior),
            new FlowIngestor.Shortfall("AEIS.US", 12, ShortfallPosition.Both),
        ]);

        // The two counts D-71 names.
        Assert.Contains("3 ticker(s) under-delivered", line, StringComparison.Ordinal);
        Assert.Contains("15 row(s) short in total", line, StringComparison.Ordinal);

        // Both counts as reachable-by-a-trailing-window, since Both is Interior
        // plus a short final page and the interior reading governs.
        Assert.Contains("2 ticker(s) are short inside the history", line, StringComparison.Ordinal);
        Assert.Contains("1 only at the oldest end", line, StringComparison.Ordinal);

        // Every affected ticker named with its position, ordinal so two runs over
        // the same data produce the same line.
        Assert.Contains("AAON.US 2 interior, AEIS.US 12 both, NVDA.US 1 final", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clean run says so rather than saying nothing. A line that is absent when
    /// there is nothing to report reads the same as a line that was never written,
    /// and the baseline is only a baseline if its zero is stated.
    /// </summary>
    [Fact]
    public void ARunWithNoShortfallSaysSoRatherThanStayingSilent()
    {
        Assert.Contains(
            "No ticker under-delivered", FlowIngestor.DescribeShortfalls([]), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ declared ---

    [Fact]
    public void TheDeclaredWritesAreFormFourAtItsOwnGrainAndTheAttemptRecord()
    {
        var stage = new FlowIngestor(EodhdClientDouble());

        // Two at D-98, where it was three. It went one to three across 3.5 and D-95,
        // when the attempt record became this component's own rather than shared with
        // C03's, and back to two when the holdings half moved to C03. The attempt
        // record stays here for the same reason it arrived: two components writing one
        // table is two claims on one triple [INVARIANT 10].
        Assert.Equal(2, stage.WriteSet.Count);

        var insider = stage.WriteSet.Single(w => w.Table == "insider_transaction");
        Assert.Equal(FlowIngestor.InsiderColumns, insider.Columns);
        Assert.Contains("transaction_ordinal", insider.Columns);
        Assert.Contains("accession_number", insider.Columns);

        var attempt = stage.WriteSet.Single(w => w.Table == "flow_fetch_attempt");
        Assert.Equal(FlowIngestor.AttemptColumns, attempt.Columns);

        // **C03's since D-98**, and asserted here rather than only there: the write
        // moved, so the failure this rules out is the old call surviving the move and
        // two components claiming institutional_holding.Insert.
        Assert.DoesNotContain(stage.WriteSet, w => w.Table == "institutional_holding");

        // C03's table is C03's. This one does not touch it.
        Assert.DoesNotContain(stage.WriteSet, w => w.Table == "fundamental_fetch_attempt");

        // flow_daily is derived by C34 and is not written here [D-61].
        Assert.DoesNotContain(stage.WriteSet, w => w.Table == "flow_daily");
    }

    // ------------------------------------------------- the rotation, finding B ---
    //
    // The selection was `ORDER BY ticker LIMIT 250` with no coverage term, so the
    // same 250 names were walked on every pass and the other 2,591 were never
    // reached. A cap every name eventually passes through is a rate limit; a cap no
    // name passes through twice is a filter, and INVARIANT 1 puts filters in the
    // universe definition only.

    private static IReadOnlyList<string> Pool(int n)
        => Enumerable.Range(1, n).Select(i => string.Format(
            System.Globalization.CultureInfo.InvariantCulture, "T{0:D2}.US", i)).ToList();

    /// <summary>
    /// The pool is the universe here, so the tiebreak set is the pool and that tier
    /// never fires. C03 is the component where it does.
    /// </summary>
    private static RotationSelection.Result Select(
        IReadOnlyList<string> pool, IReadOnlyDictionary<string, DateOnly> attempted, int maxPerRun)
        => RotationSelection.For(pool, attempted, pool.ToHashSet(StringComparer.Ordinal), maxPerRun);

    /// <summary>One night's worth: every selected ticker gets an attempt row, yield or not [D-95].</summary>
    private static void RecordAttempts(
        Dictionary<string, DateOnly> attempted, RotationSelection.Result selection, DateOnly on)
    {
        foreach (var t in selection.Selected)
        {
            attempted[t] = on;
        }
    }

    [Fact]
    public void TwoConsecutivePassesOverAnUnchangedUniverseSelectDisjointHeads()
    {
        var pool = Pool(10);
        var attempted = new Dictionary<string, DateOnly>(StringComparer.Ordinal);

        var first = Select(pool, attempted, 4);
        RecordAttempts(attempted, first, new DateOnly(2026, 8, 9));

        var second = Select(pool, attempted, 4);

        Assert.Equal(["T01.US", "T02.US", "T03.US", "T04.US"], first.Selected);
        Assert.Equal(["T05.US", "T06.US", "T07.US", "T08.US"], second.Selected);
        Assert.Empty(first.Selected.Intersect(second.Selected, StringComparer.Ordinal));
    }

    [Fact]
    public void CoverageCompletesRatherThanStoppingAtTheFirstPage()
    {
        var pool = Pool(10);
        var attempted = new Dictionary<string, DateOnly>(StringComparer.Ordinal);

        for (var pass = 0; pass < 3; pass++)
        {
            RecordAttempts(attempted, Select(pool, attempted, 4), new DateOnly(2026, 8, 9).AddDays(pass));
        }

        // Every name reached, which the truncating version could never do.
        Assert.Equal(pool.Count, attempted.Count);
        Assert.Equal(0, Select(pool, attempted, 4).NeverAttempted);
    }

    /// <summary>
    /// **The rotation keeps cycling after coverage completes, which is what D-95
    /// closes** [D-91's counterpart for C05]. Keyed on rows in
    /// `insider_transaction` the never-fetched group empties and the same
    /// alphabetically-first names are selected for ever; keyed on the attempt date it
    /// keeps moving.
    /// </summary>
    [Fact]
    public void TheRotationKeepsCyclingAfterCoverageCompletes()
    {
        var pool = Pool(8);
        var attempted = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        var day = new DateOnly(2026, 8, 9);

        // Two passes of four cover the pool.
        RecordAttempts(attempted, Select(pool, attempted, 4), day);
        RecordAttempts(attempted, Select(pool, attempted, 4), day.AddDays(1));

        var third = Select(pool, attempted, 4);
        RecordAttempts(attempted, third, day.AddDays(2));
        var fourth = Select(pool, attempted, 4);

        // Coverage is complete, so every selection from here is a refresh and the
        // oldest attempt is what says the rotation is moving.
        Assert.Equal(0, third.NeverAttempted);
        Assert.Equal(["T01.US", "T02.US", "T03.US", "T04.US"], third.Selected);
        Assert.Equal(["T05.US", "T06.US", "T07.US", "T08.US"], fourth.Selected);
        Assert.Equal(day, third.OldestAttemptInSelection);
        Assert.Equal(day.AddDays(1), fourth.OldestAttemptInSelection);
    }

    [Fact]
    public void TheCoverageCountsAreReportedTheWayCThreeReportsThem()
    {
        var pool = Pool(10);
        var attempted = new Dictionary<string, DateOnly>(StringComparer.Ordinal)
        {
            ["T01.US"] = new DateOnly(2026, 8, 1),
            ["T02.US"] = new DateOnly(2026, 8, 1),
        };

        var selection = Select(pool, attempted, 4);

        Assert.Equal(10, selection.PoolSize);
        Assert.Equal(8, selection.NeverAttempted);

        // The whole selection is new, because two attempted names sort behind eight
        // unattempted ones rather than ahead of them.
        Assert.Equal(4, selection.NewInSelection);
        Assert.Equal(0, selection.RefreshedInSelection);
        Assert.Null(selection.OldestAttemptInSelection);
        Assert.Equal(["T03.US", "T04.US", "T05.US", "T06.US"], selection.Selected);
    }

    /// <summary>
    /// **D-95's own done-when line.** A name that answers `404 Symbol not found`
    /// writes no rows and is re-offered on a later pass rather than held at the head.
    ///
    /// Under the old ordering it stayed never-fetched for ever, so the 14 of 250 that
    /// answer 404 were re-asked on every single run. 3.1 measured what that costs: a
    /// 404 is billed at 10 units, so the frozen head was spending 140 units a night on
    /// calls that cannot succeed.
    /// </summary>
    [Fact]
    public void A404NameIsReOfferedOnALaterPassRatherThanHeldAtTheHead()
    {
        var pool = Pool(10);
        var attempted = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        var day = new DateOnly(2026, 8, 9);

        var first = Select(pool, attempted, 4);
        Assert.Contains("T01.US", first.Selected);

        // T01 answered 404 and wrote nothing. The attempt is still recorded, which is
        // the whole of what the attempt record is for.
        RecordAttempts(attempted, first, day);

        var second = Select(pool, attempted, 4);
        Assert.DoesNotContain("T01.US", second.Selected);
        RecordAttempts(attempted, second, day.AddDays(1));

        // Re-offered once the never-attempted names are exhausted, at the position its
        // staleness earns rather than at the head.
        var third = Select(pool, attempted, 4);
        Assert.Equal(["T09.US", "T10.US", "T01.US", "T02.US"], third.Selected);
    }

    [Fact]
    public void TheOrderingIsOrdinalRatherThanCultureDependent()
    {
        // Two runs of one stage over one pool must select the same set
        // [CLAUDE.md section 6].
        var pool = new[] { "ZZ.US", "aa.US", "AA.US", "BB.US" };
        var attempted = new Dictionary<string, DateOnly>(StringComparer.Ordinal);

        var selection = Select(pool, attempted, 3);

        Assert.Equal(["AA.US", "BB.US", "ZZ.US"], selection.Selected);
    }

    /// <summary>
    /// The two rotations share the rule rather than a table [D-95, INVARIANT 10]. One
    /// pool through both call sites gives one answer, which is the drift a shared
    /// function removes: C05's ordering was copied from C03's by hand at 1.7 and then
    /// kept the defect after C03's was fixed.
    /// </summary>
    [Fact]
    public void BothRotationsGetTheSameAnswerFromTheSameInputs()
    {
        var pool = Pool(10);
        var attempted = new Dictionary<string, DateOnly>(StringComparer.Ordinal)
        {
            ["T01.US"] = new DateOnly(2026, 8, 1),
            ["T05.US"] = new DateOnly(2026, 7, 20),
        };

        var universe = new HashSet<string>(StringComparer.Ordinal) { "T01.US", "T05.US" };

        var once = RotationSelection.For(pool, attempted, universe, 5);
        var twice = RotationSelection.For(pool, attempted, universe, 5);

        Assert.Equal(once.Selected, twice.Selected);
        Assert.Equal(["T02.US", "T03.US", "T04.US", "T06.US", "T07.US"], once.Selected);
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
