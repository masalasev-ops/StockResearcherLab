using StockResearcherLab.Core.Stages;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// INVARIANT 10 as amended, checked mechanically rather than by review.
/// Ownership is per operation, so the unit of the assertion is a table and an
/// operation, and at most one component may claim any one of them.
///
/// SCHEMA.md names three splits and no fourth: attribution, proposal, and order
/// with fill and position. Those are the only tables where more than one
/// component may appear at all, and the test permits those and nothing else.
/// </summary>
public sealed class WriteOwnershipConformanceTests
{
    /// <summary>
    /// The tables SCHEMA.md declares as having more than one writing component,
    /// each owning a different operation. Anything else with two writers is a
    /// violation rather than an exception to be added here: the old rule failed
    /// because it counted exceptions, and a list that grows every time the design
    /// is correct is the wrong shape [SCHEMA.md, L.3].
    /// </summary>
    private static readonly string[] PermittedSplits =
        ["attribution", "proposal", "order", "fill", "position"];

    private static StageRegistry RealRegistry()
        => PipelineComposition.BuildRegistry(TestDatabase.ConnectionString);

    [Fact]
    public void NoTwoComponentsClaimTheSameTableAndOperation()
    {
        var conflicts = FindConflicts(RealRegistry());

        Assert.True(conflicts.Count == 0,
            "More than one component claims the same table and operation: " + string.Join("; ", conflicts));
    }

    [Fact]
    public void OnlyTheDeclaredSplitsHaveMoreThanOneWritingComponent()
    {
        var registry = RealRegistry();

        var multi = registry.AllWrites()
            .GroupBy(x => x.Write.Table, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.Component).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .Except(PermittedSplits, StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(multi.Count == 0,
            "Tables with more than one writing component that SCHEMA.md does not declare as a split: " +
            string.Join(", ", multi));
    }

    [Fact]
    public void EveryTableAComponentWritesIsDeclaredInSchemaDocument()
    {
        var declared = SchemaDocument.Tables();

        var undeclared = RealRegistry().AllWrites()
            .Select(x => x.Write.Table)
            .Distinct(StringComparer.Ordinal)
            .Except(declared, StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "Components claim tables SCHEMA.md does not declare: " + string.Join(", ", undeclared));
    }

    [Fact]
    public void TheConformanceTestFailsOnADeliberatelyConflictingRegistry()
    {
        // A conformance test that has never failed has not been tested. This is
        // the fixture that makes the assertion above mean something: two
        // components claiming run_log.Insert, which is exactly the case
        // INVARIANT 10 exists to catch.
        var registry = new StageRegistry(
        [
            new FakeOwner("RunLog", [new TableWrite("run_log", WriteOperation.Insert)]),
            new FakeOwner("ImpostorLog", [new TableWrite("run_log", WriteOperation.Insert)]),
        ]);

        var conflicts = FindConflicts(registry);

        Assert.Single(conflicts);
        Assert.Contains("run_log", conflicts[0], StringComparison.Ordinal);
        Assert.Contains("ImpostorLog", conflicts[0], StringComparison.Ordinal);
        Assert.Contains("RunLog", conflicts[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TwoComponentsOnTheSameTableWithDifferentOperationsIsNotAConflict()
    {
        // attribution is the shape the rule was rewritten for: the allocator
        // inserts, the filler updates, and neither performs the other's
        // operation. Per table this reads as a violation; per operation it is the
        // design.
        var registry = new StageRegistry(
        [
            new FakeOwner("CandidateAllocator", [new TableWrite("attribution", WriteOperation.Insert)]),
            new FakeOwner("ForwardReturnFiller",
                [new TableWrite("attribution", WriteOperation.Update, ["return_5d_raw"])]),
        ]);

        Assert.Empty(FindConflicts(registry));
    }

    private static IReadOnlyList<string> FindConflicts(StageRegistry registry)
        => registry.AllWrites()
            .GroupBy(x => (x.Write.Table, x.Write.Operation))
            .Where(g => g.Select(x => x.Component).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"{g.Key.Table}.{g.Key.Operation} claimed by " +
                         string.Join(" and ", g.Select(x => x.Component)
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(c => c, StringComparer.Ordinal)))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    private sealed class FakeOwner(string name, IReadOnlyList<TableWrite> writes) : IWriteOwner
    {
        public string Name { get; } = name;

        public IReadOnlyList<TableWrite> WriteSet { get; } = writes;
    }
}
