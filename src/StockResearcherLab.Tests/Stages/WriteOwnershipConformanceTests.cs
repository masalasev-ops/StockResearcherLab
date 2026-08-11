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
///
/// **Which tables those are is read off SCHEMA.md rather than listed here**
/// [1.10, closing the open item carried from 0.4]. So is which component writes
/// which table, which is the assertion the old list could not make at all.
/// </summary>
public sealed class WriteOwnershipConformanceTests
{
    /// <summary>
    /// The tables SCHEMA.md declares as having more than one writing component,
    /// each owning a different operation. Anything else with two writers is a
    /// violation rather than an exception to be added here: the old rule failed
    /// because it counted exceptions, and a list that grows every time the design
    /// is correct is the wrong shape [SCHEMA.md, L.3].
    ///
    /// **Read off the document rather than written here** [1.10]. It was a literal
    /// `["attribution", "proposal", "order", "fill", "position"]` from phase 0
    /// until now, which is the second list `FIXTURES.md` and this file both warn
    /// about: SCHEMA.md declares the splits in prose and this array restated them,
    /// so the document could gain or lose one and the test would keep asserting the
    /// old set while reading green.
    /// </summary>
    private static IReadOnlySet<string> PermittedSplits()
        => SchemaDocument.WritersByTable()
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every registered component, not the subset a missing provider token leaves
    /// behind [sign-off finding A].
    ///
    /// This called `BuildRegistry(connectionString)` until now, whose `apiToken`
    /// defaults to null, and the seven provider-backed stages are added only inside
    /// a `!IsNullOrWhiteSpace(apiToken)` branch. So every assertion in this file ran
    /// over `FlowEngine` and `RunLog` alone, and `price_daily`, `security`,
    /// `fundamental_snapshot`, `sentiment_daily`, `insider_transaction`,
    /// `institutional_holding` and `events` were outside all of them. The tests were
    /// right and were pointed at the wrong registry, which is why they passed and
    /// why nothing about them read as wrong.
    ///
    /// `AllOwnersForConformance` exists for exactly this and was already used by
    /// `RegistryNameTests`, so the name check saw nine components while the write
    /// check saw two.
    /// </summary>
    private static StageRegistry RealRegistry()
        => new(PipelineComposition.AllOwnersForConformance(TestDatabase.ConnectionString));

    /// <summary>
    /// Stated in advance so the assertions below cannot pass over a set that shrank,
    /// which is the same device `guards.ps1` uses for its monetary column count and
    /// for the same reason: five checks finding nothing over two components produce a
    /// line indistinguishable from five checks finding nothing over nine.
    ///
    /// It moves deliberately when a phase adds a component. Phase 2 adds five:
    /// C08, C09, C10, C11 and C35, the last authored at D-78 after this comment
    /// was written and after C34 had already been built early with D-61's ingest.
    /// </summary>
    private const int ExpectedOwners = 14;

    /// <summary>
    /// The assertion that keeps the rest of this file meaningful. A conformance test
    /// is only as wide as the registry it is handed, and nothing said how wide that
    /// was.
    /// </summary>
    [Fact]
    public void EveryRegisteredComponentIsUnderTest()
    {
        var owners = RealRegistry().Owners;

        Assert.Equal(ExpectedOwners, owners.Count);

        // Named rather than left to the count, because the specific failure is a
        // registry built without a provider token: it holds RunLog and FlowEngine,
        // both of which are real, so a count alone would read as a smaller system
        // rather than as a narrower scan.
        //
        // The compute components are named too since 2.11. They are registered
        // unconditionally and so cannot go missing that way, and naming them is what
        // makes a registry that lost one fail by that one's name rather than by a
        // count being off by one.
        foreach (var component in new[]
                 {
                     "PriceIngestor", "FreshnessGuard", "FundamentalsIngestor",
                     "UniverseBuilder", "SentimentIngestor", "FlowIngestor", "EventsIngestor",
                     "FlowEngine", "IndicatorEngine", "ValuationEngine", "SentimentEngine",
                     "MarketContextEngine", "PercentileEngine",
                 })
        {
            Assert.Contains(owners, o => string.Equals(o.Name, component, StringComparison.Ordinal));
        }
    }

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
            .Except(PermittedSplits(), StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(multi.Count == 0,
            "Tables with more than one writing component that SCHEMA.md does not declare as a split: " +
            string.Join(", ", multi));
    }

    /// <summary>
    /// The assertion the hardcoded list could not make [1.10]. Every component the
    /// registry declares as writing a table must be named as a writer of that table
    /// in SCHEMA.md.
    ///
    /// A split list only ever answered "how many components", so a component
    /// writing the wrong table on its own was invisible to it: one writer, no
    /// split, nothing to report. This reads the names.
    ///
    /// **One group is checked coarsely and the limit is worth stating.** `order`,
    /// `fill` and `position` share a heading and a paragraph, so the parse gives
    /// all three tables all three of RiskGate, PaperBroker and PositionManager,
    /// where the prose says RiskGate inserts orders only. Within that group this
    /// asserts membership of the group rather than of the table. Nothing in phase 1
    /// writes any of the three, and phase 7 is where the components exist to check.
    /// </summary>
    [Fact]
    public void EveryWritingComponentIsNamedAsAWriterInSchemaDocument()
    {
        var declared = SchemaDocument.WritersByTable();

        var wrong = RealRegistry().AllWrites()
            .Where(x => !declared.TryGetValue(x.Write.Table, out var names)
                        || !names.Contains(x.Component))
            .Select(x => x.Component + " -> " + x.Write.Table)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(wrong.Count == 0,
            "Components writing a table SCHEMA.md does not name them as a writer of: " +
            string.Join(", ", wrong));
    }

    /// <summary>
    /// The parse is checked against the registry rather than trusted, because a
    /// parser that returned nothing would make the two assertions above vacuous in
    /// opposite directions: no declared writer anywhere makes the name check fail
    /// loudly, but no declared split makes the split check permissive.
    ///
    /// So the floor is that every table this phase's components write has at least
    /// one named writer, which is the same set the name check walks and cannot pass
    /// if the parse is empty.
    /// </summary>
    [Fact]
    public void TheWriterParseFindsANamedWriterForEveryTableTheRegistryWrites()
    {
        var declared = SchemaDocument.WritersByTable();

        var unnamed = RealRegistry().AllWrites()
            .Select(x => x.Write.Table)
            .Distinct(StringComparer.Ordinal)
            .Where(t => !declared.TryGetValue(t, out var names) || names.Count == 0)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(unnamed.Count == 0,
            "SCHEMA.md declares no component as writing these, so the writer parse is " +
            "reading the document wrongly or the document has stopped naming one: " +
            string.Join(", ", unnamed));
    }

    /// <summary>
    /// The parse discriminates rather than answering the same way everywhere. One
    /// table with exactly one writer and one with more than one, both found by
    /// reading the document, is what makes the split set above a reading rather
    /// than a constant.
    /// </summary>
    [Fact]
    public void TheWriterParseSeparatesSingleWriterTablesFromSplitOnes()
    {
        var declared = SchemaDocument.WritersByTable();

        Assert.Contains(declared, kv => kv.Value.Count == 1);
        Assert.Contains(declared, kv => kv.Value.Count > 1);
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
