using System.Text.Json;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Ingest;

/// <summary>
/// `Holders::Institutions` out of the payload C03 already fetches [D-98].
///
/// The parse arrived here from `FlowIngestor` unchanged. What is new is that it now
/// has to read the block out of a whole unfiltered payload as well as out of the
/// filtered call's root, which is what the fixture tests below exercise.
/// </summary>
public sealed class InstitutionalHoldersTests
{
    // ---------------------------------------------------------- the shape ---

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

        var rows = InstitutionalHolders.Parse(doc.RootElement, "CCS.US");

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

        var row = Assert.Single(InstitutionalHolders.Parse(doc.RootElement, "CCS.US"));
        Assert.Equal("Real Holder", row.HolderName);
    }

    /// <summary>
    /// A bare JSON string is what the fundamentals endpoint answers a ticker it
    /// carries nothing for with, and `TryGetProperty` throws on it rather than
    /// returning false [0006].
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"NA\"")]
    [InlineData("null")]
    [InlineData("""{"Holders":"NA"}""")]
    [InlineData("""{"Financials":{}}""")]
    public void AnUnexpectedHoldersShapeYieldsNothing(string body)
    {
        using var doc = JsonDocument.Parse(body);
        Assert.Empty(InstitutionalHolders.Parse(doc.RootElement, "CCS.US"));
    }

    // ------------------------------------------- the captured block [D-98] ---
    //
    // WHAT THESE COVER, AND WHAT THEY DO NOT. D-98 rests on two claims. The first is
    // that the provider sends the same twenty rows filtered and unfiltered, and **no
    // test here checks that**: it was measured live over CCS.US and NVDA.US, two calls
    // each, twenty entries both ways agreeing row for row, and the transcript at
    // `docs/evidence/phase-3/holders-filtered-vs-unfiltered-20260812.txt` is the whole
    // of the evidence for it. A fixture cannot re-prove it, because one capture is one
    // response and comparing it against itself proves nothing about the other call.
    //
    // The second claim is ours rather than the provider's: that this parse reads the
    // unfiltered nesting and the filtered root identically and takes the whole block
    // from each. That is what these assert, over the same bytes fed in three shapes.
    //
    // The distinction is the point. A test named for the first claim while checking
    // the second is worse than no test, because a later reader trusts it for something
    // it never looked at.

    /// <summary>
    /// `CCS.US`'s whole `Holders` block as the unfiltered call returned it, 6,547
    /// bytes, captured 2026-08-12. It carries `Institutions` and `Funds` side by side,
    /// which is why the peel has to name the one it wants.
    /// </summary>
    private static string CapturedBlock()
        => File.ReadAllText(Path.Combine(
            SchemaDocument.RepositoryRoot, "docs", "evidence", "phase-3",
            "holders-ccs-unfiltered-20260812.json"));

    /// <summary>
    /// The unfiltered payload nests the block under `Holders`; the filtered call
    /// returns the entries object at its root [1.9, endpoint sweep]. Both shapes, plus
    /// the block between them that was captured, from one set of bytes.
    /// </summary>
    private static IReadOnlyList<HoldingRow> ParseAs(Shape shape)
    {
        var captured = CapturedBlock();

        using var block = JsonDocument.Parse(captured);

        using var doc = shape switch
        {
            // What `fundamentals/{t}` returns whole. A sibling block is included, so
            // the peel is reaching past something rather than into an object of one.
            Shape.UnfilteredPayload => JsonDocument.Parse(
                """{"General":{"Sector":"Consumer Cyclical"},"Holders":""" + captured + "}"),

            // What was captured: the `Holders` block, `Institutions` and `Funds`.
            Shape.HoldersBlock => JsonDocument.Parse(captured),

            // What `filter=Holders::Institutions` returns: the entries at the root.
            _ => JsonDocument.Parse(block.RootElement.GetProperty("Institutions").GetRawText()),
        };

        return InstitutionalHolders.Parse(doc.RootElement, "CCS.US");
    }

    /// <summary>
    /// Public because the theories below take it as a parameter and those must be
    /// public for xUnit to discover them.
    /// </summary>
    public enum Shape { UnfilteredPayload, HoldersBlock, FilteredRoot }

    /// <summary>
    /// Twenty rows out of every shape, which is the "not a truncated head" half stated
    /// as far as a capture can state it: the block carries twenty entries and the parse
    /// returns twenty rows from each shape rather than stopping at the first.
    /// </summary>
    [Theory]
    [InlineData(Shape.UnfilteredPayload)]
    [InlineData(Shape.HoldersBlock)]
    [InlineData(Shape.FilteredRoot)]
    public void EveryShapeOfTheCapturedBlockYieldsAllTwentyEntries(Shape shape)
        => Assert.Equal(20, ParseAs(shape).Count);

    /// <summary>
    /// **The nesting is not read differently from the root.** Row for row, not by
    /// count: a count that matches while the rows differ is the check the probe
    /// deliberately did not make either.
    ///
    /// This is our half of D-98 and the only half a fixture can carry. The provider
    /// half is the live comparison in
    /// `docs/evidence/phase-3/holders-filtered-vs-unfiltered-20260812.txt`.
    /// </summary>
    [Fact]
    public void TheNestedShapeAndTheRootShapeParseToTheSameRowsInTheSameOrder()
    {
        var nested = ParseAs(Shape.UnfilteredPayload);
        var root = ParseAs(Shape.FilteredRoot);

        Assert.Equal(20, nested.Count);
        Assert.Equal(nested, root);

        // The block as captured sits between the two and reads as both.
        Assert.Equal(nested, ParseAs(Shape.HoldersBlock));
    }

    /// <summary>
    /// `Funds` sits beside `Institutions` in the same block and is twenty more names,
    /// none of them institutions. Taking the block whole rather than the named member
    /// would put fund positions under a column that says institutional ownership, which
    /// C34 reads for `inst_ownership_change`.
    /// </summary>
    [Fact]
    public void TheFundsBlockBesideItIsNotRead()
    {
        var names = ParseAs(Shape.HoldersBlock).Select(r => r.HolderName).ToList();

        Assert.Contains("BlackRock Inc", names);
        Assert.DoesNotContain("American Funds SMALLCAP World A", names);
        Assert.DoesNotContain("Avantis US Small Cap Value ETF", names);
    }

    /// <summary>
    /// Ordinal by report date then holder name, because COPY order reaches the table
    /// and object enumeration order is unspecified [`CLAUDE.md` §6]. The block carries
    /// two report dates, so the ordering is exercised rather than asserted over one
    /// value.
    /// </summary>
    [Fact]
    public void RowsAreSortedByReportDateThenHolderName()
    {
        var rows = ParseAs(Shape.HoldersBlock);

        Assert.Equal("American Century Companies Inc", rows[0].HolderName);
        Assert.Equal(new DateOnly(2026, 3, 31), rows[0].ReportDate);
        Assert.Equal(401598m, rows[0].Shares);
        Assert.Equal(316922m, rows[0].Change);
        Assert.Equal(374.2761f, rows[0].ChangePct!.Value, 4);

        // The single 2026-06-30 entry sorts last rather than wherever the provider
        // put it.
        Assert.Equal("Jennison Associates LLC", rows[^1].HolderName);
        Assert.Equal(new DateOnly(2026, 6, 30), rows[^1].ReportDate);

        var keys = rows.Select(r => (r.ReportDate, r.HolderName)).ToList();

        Assert.Equal(
            keys.OrderBy(x => x.ReportDate).ThenBy(x => x.HolderName, StringComparer.Ordinal).ToList(),
            keys);
    }

    /// <summary>
    /// Every row carries the ticker it was asked for. The block itself names no ticker,
    /// so this is the one field the parse supplies rather than reads.
    /// </summary>
    [Fact]
    public void EveryRowCarriesTheTickerItWasParsedFor()
        => Assert.All(ParseAs(Shape.HoldersBlock), r => Assert.Equal("CCS.US", r.Ticker));
}
