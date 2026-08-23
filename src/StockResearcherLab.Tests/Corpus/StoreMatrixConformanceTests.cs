using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests;

/// <summary>
/// `ARCHITECTURE.html` §16's store list against `SCHEMA.md`, in both directions.
///
/// **This exists because nothing read that section and four stores drifted out of it.**
/// D-76 removed the writer and reader columns from §16 on the grounds that the
/// conformance test holds §3 and `SCHEMA.md` together. That was right about those two
/// columns and it left the store list itself checked by nothing, so
/// `sentiment_derived_daily` from 0004, `fundamental_fetch_attempt` from 0006,
/// `security_daily` from 0007 and `flow_fetch_attempt` from 0008 were all absent from
/// the matrix while every one of them was in a migration and in `SCHEMA.md`. The oldest
/// had been missing since phase 2.
///
/// **Either direction alone passes while the other is broken.** A matrix missing a
/// declared store looks fine to a check that only walks the matrix, and a matrix naming
/// a store nobody declared looks fine to a check that only walks the schema. That is
/// the same argument `SchemaParityTests` makes about the database and it is the same
/// answer.
/// </summary>
public sealed class StoreMatrixConformanceTests
{
    /// <summary>
    /// **The count is stated so the check cannot pass over an empty match set**, which
    /// is what `guards.ps1` does for the same reason: a wrong parse that finds nothing
    /// and a right parse that finds nothing are indistinguishable from a green test.
    ///
    /// A test stating a count is not a spec stating one, so D-83 is not in tension with
    /// it. The number moves when a store is added, deliberately, and moving it is how a
    /// reader learns the matrix changed.
    ///
    /// Thirty-eight at 2026-08-12: thirty-four before the four stores this pass added.
    /// </summary>
    [Fact]
    public void TheStoreMatrixParsesToTheNumberOfStoresItHas()
    {
        var stores = ArchitectureDocument.StoreMatrix();

        // Thirty-nine at 3.7, which added earnings_history [D-96]. Forty at 3.6's
        // second pass, which added price_fetch_attempt [0010]. Forty-one at 3.8, which
        // added sentiment_fetch_attempt [0011]. Forty-two at 3.10, which added
        // event_fetch_attempt [0012].
        //
        // Forty-three at 3.5.1, which added universe_rejection [D-108]. Forty-five at
        // 3.5.2, which added percentile_cell_daily and percentile_cell_coverage [D-107].
        //
        // **This count moves before the rows it counts, and deliberately.** §16 is in
        // `ARCHITECTURE.html`, which is human-edited only [`CLAUDE.md` §13], and the
        // authorisation 3.8 and 3.10 ran under covered the §3 Reads cells alone.
        // Moving the count here leaves exactly two authored lines between this branch
        // and a green gate rather than four, so the rows land and everything passes at
        // once.
        Assert.Equal(45, stores.Count);

        // Distinct, because a store named twice would satisfy a count and a set
        // comparison while saying two different things about one table.
        var duplicates = stores
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(duplicates.Count == 0, "Named more than once in §16: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void EveryStoreInTheMatrixIsDeclaredInSchemaDocument()
    {
        var missing = MatrixNotInSchema(File.ReadAllText(ArchitectureDocument.Path));

        Assert.True(missing.Count == 0,
            "§16 names stores SCHEMA.md does not declare: " + string.Join(", ", missing) +
            ". Either the store is declared there, or the row carries the NOT YET IN SCHEMA marker " +
            "and says so in the document rather than in this test.");
    }

    [Fact]
    public void EveryTableDeclaredInSchemaDocumentIsNamedInTheMatrix()
    {
        var missing = SchemaNotInMatrix(File.ReadAllText(ArchitectureDocument.Path));

        Assert.True(missing.Count == 0,
            "SCHEMA.md declares tables §16 does not name: " + string.Join(", ", missing) +
            ". Each needs a row with a grain and a size, which is what that table is for.");
    }

    /// <summary>
    /// **The marker is checked rather than trusted.** A store marked as not yet
    /// declared, which turns out to be declared, has had the marker outlive the
    /// condition it records, and a marker nobody has to remove is a suppression.
    /// </summary>
    [Fact]
    public void AStoreMarkedNotYetInSchemaIsGenuinelyAbsentFromIt()
    {
        var declared = SchemaDocument.Tables().ToHashSet(StringComparer.Ordinal);

        var stale = ArchitectureDocument.StoreMatrix()
            .Where(s => s.NotYetInSchema && declared.Contains(s.Name))
            .Select(s => s.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "§16 marks these NOT YET IN SCHEMA and SCHEMA.md declares them: " + string.Join(", ", stale) +
            ". The marker comes off at the moment the store lands.");

        // One row carries it today. Asserted so that the two tests above are known to
        // be exercising the marker path rather than passing because nothing uses it.
        var marked = ArchitectureDocument.StoreMatrix().Where(s => s.NotYetInSchema).Select(s => s.Name).ToList();

        Assert.Equal(["screen_evaluation"], marked);
    }

    /// <summary>
    /// A row naming several stores expands to all of them. Assuming one row is one
    /// store would leave `fill`, `position` and `run_log` unchecked while the count
    /// still looked plausible.
    /// </summary>
    [Fact]
    public void ARowNamingSeveralStoresExpandsToEveryOneOfThem()
    {
        var names = ArchitectureDocument.StoreMatrix().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("order", names);
        Assert.Contains("fill", names);
        Assert.Contains("position", names);
        Assert.Contains("cost_ledger", names);
        Assert.Contains("run_log", names);

        // The unsplit forms are what a parser taking the cell whole would produce.
        Assert.DoesNotContain("order / fill / position", names);
        Assert.DoesNotContain("cost_ledger / run_log", names);
    }

    /// <summary>
    /// The summary row is not a store, and it is excluded by structure rather than by
    /// matching the word. It sits in `tfoot` where every store sits in `tbody`, so a
    /// second summary row added later drops out the same way.
    /// </summary>
    [Fact]
    public void TheTotalRowIsNotAStore()
    {
        var names = ArchitectureDocument.StoreMatrix().Select(s => s.Name).ToList();

        Assert.DoesNotContain("Total", names, StringComparer.Ordinal);

        // The word is in the document, so the exclusion is doing something.
        Assert.Contains("<td>Total</td>", File.ReadAllText(ArchitectureDocument.Path), StringComparison.Ordinal);
    }

    // ------------------------------------------------------ shown to fail ---

    /// <summary>
    /// **Demonstrated rather than asserted.** A conformance test that has never failed
    /// has not been tested, which is why every other one in this repository has a
    /// fixture built to break it. The document is mutated in memory; nothing is written.
    /// </summary>
    [Fact]
    public void RemovingAStoreFromTheMatrixFailsTheCheckAndNamesThatStore()
    {
        var html = File.ReadAllText(ArchitectureDocument.Path);

        var row = html.Split('\n').Single(l => l.Contains("<b>security_daily</b>", StringComparison.Ordinal));
        var without = html.Replace(row + "\n", "", StringComparison.Ordinal);

        Assert.NotEqual(html, without);

        var missing = SchemaNotInMatrix(without);

        Assert.Equal(["security_daily"], missing);

        // And restoring it passes, so the failure is the removal rather than the
        // mutation having broken the parse.
        Assert.Empty(SchemaNotInMatrix(html));
        Assert.Equal(ArchitectureDocument.StoreMatrix().Count - 1, ArchitectureDocument.StoreMatrixIn(without).Count);
    }

    /// <summary>
    /// The other direction, shown the same way: a store the matrix names and the schema
    /// does not declare is reported, and the marker is what makes it acceptable.
    /// </summary>
    [Fact]
    public void AStoreTheSchemaDoesNotDeclareIsReportedUnlessItCarriesTheMarker()
    {
        var html = File.ReadAllText(ArchitectureDocument.Path);

        var invented =
            "<tr><td class=\"mono\"><b>srl_no_such_store</b></td><td>invented</td>" +
            "<td class=\"mono\">tiny</td></tr>\n";

        var withInvented = html.Replace("<tbody>\n", "<tbody>\n" + invented, StringComparison.Ordinal);
        Assert.NotEqual(html, withInvented);
        Assert.Equal(["srl_no_such_store"], MatrixNotInSchema(withInvented));

        // The same row carrying the marker is not reported, which is the whole of what
        // the marker buys and is why it is checked separately for staleness.
        var marked = invented.Replace(
            "<b>srl_no_such_store</b>",
            "<b>srl_no_such_store</b> " + ArchitectureDocument.NotYetInSchemaMarker,
            StringComparison.Ordinal);

        Assert.Empty(MatrixNotInSchema(html.Replace("<tbody>\n", "<tbody>\n" + marked, StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- both ---

    /// <summary>Stores §16 names that `SCHEMA.md` does not declare, marker rows aside.</summary>
    private static IReadOnlyList<string> MatrixNotInSchema(string html)
    {
        var declared = SchemaDocument.Tables().ToHashSet(StringComparer.Ordinal);

        return ArchitectureDocument.StoreMatrixIn(html)
            .Where(s => !s.NotYetInSchema && !declared.Contains(s.Name))
            .Select(s => s.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Tables `SCHEMA.md` declares that §16 does not name.</summary>
    private static IReadOnlyList<string> SchemaNotInMatrix(string html)
    {
        var named = ArchitectureDocument.StoreMatrixIn(html)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);

        return SchemaDocument.Tables()
            .Where(t => !named.Contains(t))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}
