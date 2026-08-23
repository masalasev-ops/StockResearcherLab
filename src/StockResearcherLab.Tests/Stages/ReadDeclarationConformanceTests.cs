using StockResearcherLab.Core.Stages;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// D-74, checked mechanically. A stage's declared `ReadSet` and the Reads cell
/// `ARCHITECTURE.html` section 3 gives it must name the same tables, in both
/// directions.
///
/// **Writes have had this since phase 0 and reads have had nothing.** The registry
/// was asserted against `SCHEMA.md`'s writer declarations from 0.4 and against its
/// own component names from 1.11, while `ReadSet` was asserted against hardcoded
/// literals in four component test files and against nothing at all in the other
/// two. So the catalogue and the running system disagreed about four components from
/// 1.4 until the post phase 1 reconciliation, and no test could have said so.
///
/// **What that cost.** A per-ticker endpoint needs a ticker list and the catalogue
/// named none, so nothing contradicted drawing the fundamentals pool from `security`,
/// which closes the universe over itself: a name needs fundamentals to be admitted,
/// so once `security` was populated only its own members could be fetched. Coverage
/// froze at 679 with no error and entirely plausible output, which is the failure
/// mode `CLAUDE.md` section 1 describes rather than an ordinary bug.
/// </summary>
public sealed class ReadDeclarationConformanceTests
{
    /// <summary>
    /// Stated in advance so the assertions cannot pass over a set that shrank. Nine
    /// components are registered and eight of them are stages: RunLog owns a write
    /// without being runnable, so it has no read set to check.
    ///
    /// It moves deliberately when a phase adds a stage.
    /// </summary>
    private const int ExpectedStages = 13;

    /// <summary>
    /// Section 3 catalogues thirty-four components. Asserted rather than assumed,
    /// because a parser that stopped matching returns an empty map, and an empty map
    /// makes the catalogue-to-code direction below pass over nothing.
    /// </summary>
    private const int CataloguedComponents = 35;

    /// <summary>
    /// Deviations between the catalogue and the code, recorded rather than silently
    /// permitted, and asserted below to still be deviations.
    ///
    /// **Empty since 3.7.** The one entry was C03's Reads cell naming `events` where
    /// `FundamentalsIngestor` did not declare it: §3 says earnings jump the rotation
    /// queue, which needs the earnings calendar, and the rotation was staleness-ordered
    /// only. 3.7 closed it by reading `events` for the names that reported inside
    /// `events.earnings_backward_days` and ranking them above staleness, so the
    /// declaration now means what it says.
    ///
    /// **The closure was found by this test rather than remembered.** It fails when a
    /// recorded deviation stops being one, which is the opposite of how a suppression
    /// list usually behaves, and that is what made the entry safe to record in the
    /// first place. The `1 → 3` carried obligation in `BUILD_PLAN.md` goes with it.
    ///
    /// Declaring a table a stage does not read would have made this green and meant
    /// nothing; removing `events` from the catalogue would have been editing an
    /// authored document to match what was built [`CLAUDE.md` §13]. Neither was done.
    /// </summary>
    private static readonly (string Component, string Table)[] RecordedDeviations = [];

    [Fact]
    public void TheReadsCellParseFindsTablesRatherThanNothing()
    {
        var catalogue = ArchitectureDocument.ReadTablesByComponent();

        Assert.Equal(CataloguedComponents, catalogue.Count);

        // Discrimination, not just a non-empty answer. A parse that returned every
        // component with an empty set would satisfy a count and prove nothing, and a
        // parse that returned every word in the cell would name `order` for C03.
        // `security_daily` joined this cell at 3.12, which is what lets the nightly path
        // measure a departure against the membership in force before it.
        Assert.Equal(
            ["fundamental_snapshot", "price_daily", "security_daily"],
            catalogue["UniverseBuilder"].OrderBy(t => t, StringComparer.Ordinal));

        Assert.Equal(
            ["insider_transaction", "institutional_holding"],
            catalogue["FlowEngine"].OrderBy(t => t, StringComparer.Ordinal));

        // One bulk endpoint and no table at all. This is the row that proves an
        // endpoint name does not become a table.
        Assert.Empty(catalogue["PriceIngestor"]);

        // C03 says "for rotation order" and does not mean the `order` table. The clause
        // it qualifies became `security_daily` at 3.12 and the qualifier is unchanged,
        // which is the point of substituting rather than replacing the cell.
        Assert.DoesNotContain("order", catalogue["FundamentalsIngestor"]);
        Assert.Contains("security_daily", catalogue["FundamentalsIngestor"]);
        Assert.Contains("price_daily", catalogue["FundamentalsIngestor"]);

        // `digest_provider` is configuration rather than a declared table, so the
        // intersection with SCHEMA.md drops it instead of reporting it.
        Assert.DoesNotContain("digest_provider", catalogue["NewsDigester"]);
    }

    [Fact]
    public void EveryRegisteredStageIsUnderTest()
    {
        var stages = Stages();

        Assert.Equal(ExpectedStages, stages.Count);

        foreach (var expected in new[]
                 {
                     "PriceIngestor", "FreshnessGuard", "FundamentalsIngestor", "UniverseBuilder",
                     "SentimentIngestor", "FlowIngestor", "EventsIngestor", "FlowEngine",
                     "IndicatorEngine", "ValuationEngine", "SentimentEngine",
                     "MarketContextEngine", "PercentileEngine",
                 })
        {
            Assert.True(stages.ContainsKey(expected), $"{expected} is registered and is not under test.");
        }
    }

    [Fact]
    public void EveryTableAStageDeclaresIsNamedInItsReadsCell()
    {
        var wrong = Undeclared(Stages(), ArchitectureDocument.ReadTablesByComponent());

        Assert.True(wrong.Count == 0,
            "Stage(s) reading a table ARCHITECTURE.html section 3 does not name in their Reads cell: " +
            string.Join(", ", wrong) +
            ". The code reaches a store the catalogue does not say it reaches, which is the direction " +
            "that hid the fundamentals pool [D-74].");
    }

    [Fact]
    public void EveryTableAReadsCellNamesIsDeclaredByItsStage()
    {
        var wrong = Unread(Stages(), ArchitectureDocument.ReadTablesByComponent())
            .Except(RecordedDeviations.Select(d => $"{d.Component} -> {d.Table}"), StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(wrong.Count == 0,
            "Reads cell(s) naming a table the stage does not declare: " + string.Join(", ", wrong) +
            ". Either the stage stopped reading it, in which case the catalogue is what changes and " +
            "the prior text goes to CHANGELOG.md [D-73], or the stage never read it and the gap is " +
            "recorded rather than declared away.");
    }

    /// <summary>
    /// The exemption cannot go stale. When `FundamentalsIngestor` starts reading
    /// `events` this fails and says to delete the entry, which is the opposite of how
    /// a permitted-exception list usually ages.
    /// </summary>
    [Fact]
    public void EveryRecordedDeviationIsStillADeviation()
    {
        var stages = Stages();
        var catalogue = ArchitectureDocument.ReadTablesByComponent();

        foreach (var (component, table) in RecordedDeviations)
        {
            Assert.True(catalogue.TryGetValue(component, out var named) && named.Contains(table),
                $"{component}'s Reads cell no longer names {table}, so this deviation is recorded " +
                "against nothing. Delete the entry.");

            Assert.True(stages.TryGetValue(component, out var declared) && !declared.Contains(table),
                $"{component} now declares {table}, so the gap is closed. Delete the entry, and check " +
                "that BUILD_PLAN.md's carried obligation goes with it.");
        }
    }

    /// <summary>
    /// A conformance test that has never failed has not been tested. Both directions
    /// are exercised against a fabricated stage, because both would pass vacuously if
    /// either map came back empty and neither failure would look like one.
    /// </summary>
    [Fact]
    public void BothDirectionsFailOnAStageThatDisagreesWithTheCatalogue()
    {
        var catalogue = ArchitectureDocument.ReadTablesByComponent();

        // Reads a table its cell does not name. C02's cell is one bulk endpoint.
        var reachesTooFar = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["PriceIngestor"] = ["price_daily"],
        };

        Assert.Equal(["PriceIngestor -> price_daily"], Undeclared(reachesTooFar, catalogue));
        Assert.Empty(Unread(reachesTooFar, catalogue));

        // Declares none of what its cell names. C34's cell names two.
        var declaresNothing = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["FlowEngine"] = [],
        };

        Assert.Equal(
            ["FlowEngine -> insider_transaction", "FlowEngine -> institutional_holding"],
            Unread(declaresNothing, catalogue));
        Assert.Empty(Undeclared(declaresNothing, catalogue));
    }

    // ---------------------------------------------------------------- helpers ---

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Stages()
        => PipelineComposition.AllOwnersForConformance(TestDatabase.ConnectionString)
            .OfType<IStage>()
            .ToDictionary(s => s.Name, s => s.ReadSet, StringComparer.Ordinal);

    /// <summary>Declared in code, absent from the Reads cell.</summary>
    private static IReadOnlyList<string> Undeclared(
        IReadOnlyDictionary<string, IReadOnlyList<string>> stages,
        IReadOnlyDictionary<string, IReadOnlySet<string>> catalogue)
        => stages
            .SelectMany(s => s.Value.Select(table => (Component: s.Key, Table: table)))
            .Where(x => !catalogue.TryGetValue(x.Component, out var named) || !named.Contains(x.Table))
            .Select(x => $"{x.Component} -> {x.Table}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    /// <summary>Named in the Reads cell, absent from the declaration.</summary>
    private static IReadOnlyList<string> Unread(
        IReadOnlyDictionary<string, IReadOnlyList<string>> stages,
        IReadOnlyDictionary<string, IReadOnlySet<string>> catalogue)
        => stages
            .SelectMany(s => catalogue.TryGetValue(s.Key, out var named)
                ? named.Select(table => (Component: s.Key, Table: table))
                : [])
            .Where(x => !stages[x.Component].Contains(x.Table, StringComparer.Ordinal))
            .Select(x => $"{x.Component} -> {x.Table}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
}
