using StockResearcherLab.Api;
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
    /// It moves deliberately when a phase adds a stage. Fourteen at 4.4, which adds
    /// C13 ScreenEngine.
    /// </summary>
    private const int ExpectedStages = 17;

    /// <summary>
    /// Section 3 catalogues thirty-six components, C36 RecordInspector having joined at
    /// 3.5.1 [D-109]. Asserted rather than assumed,
    /// because a parser that stopped matching returns an empty map, and an empty map
    /// makes the catalogue-to-code direction below pass over nothing.
    /// </summary>
    private const int CataloguedComponents = 36;

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
                     "GateEngine", "ScreenEngine", "CandidateAllocator", "ConcentrationMonitor",
                 })
        {
            Assert.True(stages.ContainsKey(expected), $"{expected} is registered and is not under test.");
        }
    }

    /// <summary>
    /// Read owners that are not stages, which is C36 alone [D-109].
    ///
    /// **Stated for the reason <see cref="ExpectedStages"/> is stated.** A composition
    /// that returned nothing would make both directions pass over an empty set, and
    /// neither pass would look like a failure.
    /// </summary>
    private const int ExpectedReaders = 1;

    [Fact]
    public void EveryRegisteredReaderIsUnderTest()
    {
        var readers = Readers();

        Assert.True(readers.Count == ExpectedReaders,
            $"{readers.Count} reader(s) registered against the {ExpectedReaders} this test states. " +
            "The count moves deliberately when a phase adds one.");

        Assert.True(readers.ContainsKey("RecordInspector"),
            "RecordInspector is the first reader that owns no write, and D-109 exists so that " +
            "such a reader is held to its Reads cell rather than sitting outside the check.");

        // Enumerating the readers opens nothing, exactly as building the registry makes
        // no provider call. Asserted by the connection string being a fiction.
        Assert.NotEmpty(readers["RecordInspector"]);
    }

    [Fact]
    public void EveryTableAStageDeclaresIsNamedInItsReadsCell()
    {
        var wrong = Undeclared(Declared(), ArchitectureDocument.ReadTablesByComponent());

        Assert.True(wrong.Count == 0,
            "Stage(s) reading a table ARCHITECTURE.html section 3 does not name in their Reads cell: " +
            string.Join(", ", wrong) +
            ". The code reaches a store the catalogue does not say it reaches, which is the direction " +
            "that hid the fundamentals pool [D-74].");
    }

    [Fact]
    public void EveryTableAReadsCellNamesIsDeclaredByItsStage()
    {
        var wrong = Unread(Declared(), ArchitectureDocument.ReadTablesByComponent())
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
        var stages = Declared();
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

    /// <summary>
    /// The same fixture for a reader that is not a stage, because the merge is new and
    /// a merged map that silently dropped one source would make C36's declaration
    /// unchecked while every assertion stayed green [D-109].
    ///
    /// `RecordInspector` is used as the name deliberately: it is the component the
    /// mechanism was built for, so the fixture fails on the case it exists to catch
    /// rather than on an invented one.
    /// </summary>
    [Fact]
    public void BothDirectionsFailOnAReaderThatDisagreesWithTheCatalogue()
    {
        var catalogue = ArchitectureDocument.ReadTablesByComponent();

        // Reads a table its cell does not name. The cell grows a checkpoint at a time,
        // so the fixture takes it from the catalogue and adds one rather than restating
        // it: a hardcoded list here would be the second list this repository keeps
        // finding, and it would go stale at 3.5.3.
        var declared = catalogue["RecordInspector"].OrderBy(t => t, StringComparer.Ordinal).ToList();

        var reachesTooFar = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["RecordInspector"] = [.. declared, "proposal"],
        };

        Assert.Equal(["RecordInspector -> proposal"], Undeclared(reachesTooFar, catalogue));
        Assert.Empty(Unread(reachesTooFar, catalogue));

        // Declares none of what its cell names.
        var declaresNothing = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["RecordInspector"] = [],
        };

        Assert.Equal(
            declared.Select(t => "RecordInspector -> " + t),
            Unread(declaresNothing, catalogue));
        Assert.Empty(Undeclared(declaresNothing, catalogue));
    }

    /// <summary>
    /// The merge carries both sources. A `Declared` that returned only the stages would
    /// leave C36 unchecked and every assertion in this class green, which is the exact
    /// failure the two-project split makes easy to write.
    /// </summary>
    [Fact]
    public void TheMergedMapCarriesStagesAndReadersBoth()
    {
        var declared = Declared();

        Assert.Equal(ExpectedStages + ExpectedReaders, declared.Count);
        Assert.Contains("PercentileEngine", declared.Keys);
        Assert.Contains("RecordInspector", declared.Keys);
    }

    // ---------------------------------------------------------------- helpers ---

    /// <summary>
    /// Every declared reader, stages and read-only components together.
    ///
    /// **Two sources rather than one, because the two live in projects that cannot see
    /// each other.** The Api never references Pipeline, which is what structurally stops
    /// a page invoking a stage [`CLAUDE.md` §4], so no single registry can hold both.
    /// The test project references both and is where they meet, which costs no new
    /// reference in either direction [D-109].
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Stages()
        => PipelineComposition.AllOwnersForConformance(TestDatabase.ConnectionString)
            .OfType<IStage>()
            .ToDictionary(s => s.Name, s => s.ReadSet, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Readers()
        => ApiComposition.AllReadOwnersForConformance(TestDatabase.ConnectionString)
            .ToDictionary(r => r.Name, r => r.ReadSet, StringComparer.Ordinal);

    /// <summary>
    /// Every declared reader, stages and read-only components together, which is what
    /// the two directions below are asserted over.
    ///
    /// **Two sources rather than one, because the two live in projects that cannot see
    /// each other.** The Api never references Pipeline, which is what structurally stops
    /// a page invoking a stage [`CLAUDE.md` §4], so no single registry can hold both.
    /// The test project references both and is where they meet, which costs no new
    /// reference in either direction [D-109].
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Declared()
    {
        var merged = new Dictionary<string, IReadOnlyList<string>>(Stages(), StringComparer.Ordinal);

        foreach (var (name, tables) in Readers())
        {
            Assert.False(merged.ContainsKey(name),
                $"{name} is both a registered stage and a registered reader, so one of the two " +
                "declarations is unreachable and the catalogue cannot say which.");

            merged[name] = tables;
        }

        return merged;
    }

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
