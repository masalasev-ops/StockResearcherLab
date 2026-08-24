using StockResearcherLab.Core.Stages;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// A component's declared `WriteSet` and the Writes cell `ARCHITECTURE.html` section 3
/// gives it must name the same tables, in both directions [Q.3].
///
/// **This is the check the Reads column has had since D-74 and the Writes column has
/// never had.** Write ownership was asserted against `SCHEMA.md` from 0.4 and against
/// the registry's own component names from 1.11, and section 3's Writes cells were read
/// by nothing. So the two documents could disagree about which component writes what and
/// nothing would fail.
///
/// **The carried obligation raised at code review `0006` says this and names where it
/// bites**: C03 was the first component to gain a second write and its cell drifted
/// immediately, D-85 gives C14 a second write in phase 4 and D-87 gives C22 one in phase
/// 8. C14's landed at 4.10, so the predicted case has arrived and this closes the
/// obligation rather than carrying it further.
///
/// **Why the Reads column got one first and this did not.** Reads drifted on twelve
/// recorded occasions across phases 2, 3 and 4, and each drift was found by the code
/// needing a store the document did not name, which fails loudly. A Writes drift is the
/// opposite: the component writes what it writes and the cell is prose nobody executes,
/// so the disagreement produces no error at all. That is the failure mode `CLAUDE.md`
/// section 1 describes, and it is a reason for checking the quieter column rather than a
/// reason for leaving it.
/// </summary>
public sealed class WriteDeclarationConformanceTests
{
    /// <summary>
    /// Stated in advance for the reason `ReadDeclarationConformanceTests` states its
    /// own: a parser that stopped matching returns an empty map, and an empty map makes
    /// the catalogue-to-code direction pass over nothing.
    /// </summary>
    private const int CataloguedComponents = 36;

    /// <summary>
    /// Cells naming a table the component does not write, recorded rather than silently
    /// permitted, and asserted below to still be deviations.
    ///
    /// **Empty at Q.3.** No Writes cell names a store its component has stopped writing.
    /// </summary>
    private static readonly (string Component, string Table)[] RecordedDeviations = [];

    /// <summary>
    /// The other direction: a component writes a table its Writes cell does not name.
    ///
    /// **Empty at Q.8, and it held two entries for the five checkpoints before it.** C04
    /// SentimentIngestor writing `sentiment_fetch_attempt` and C06 EventsIngestor writing
    /// `event_fetch_attempt` were what this check found on the first run it ever had
    /// [Q.3]. Both were recorded here rather than corrected, `ARCHITECTURE.html` being
    /// human-edited, and both cells were amended on the operator's direction at Q.8 with
    /// the prior wordings in `CHANGELOG.md` [D-73]. The entries are deleted because the
    /// assertion below had started failing on them, which is the closure working.
    ///
    /// **They were the `0006` carried obligation's own prediction arriving**, that item
    /// saying the Writes column has no check and that C03 drifted the moment it gained a
    /// second write. They were also the sixth and seventh drifts of that column in four
    /// phases, after D-91, D-95, D-92, D-96 and D-98.
    ///
    /// **A separate list from <see cref="RecordedDeviations"/> and deliberately so.**
    /// The Reads check gives this direction no exemption at all, because a stage reading
    /// an undeclared store is what hid the fundamentals pool closing over itself. Writes
    /// are not the same case: `DeclaredAccess` refuses a write to an undeclared table
    /// before the connection opens, and `WriteOwnershipConformanceTests` holds the
    /// registry against `SCHEMA.md`, so an undeclared write cannot reach a store. What
    /// is left is a narrative that describes a different system, which is worth
    /// recording and is not worth failing the suite over. The asymmetry is stated here
    /// rather than inherited by accident.
    ///
    /// An entry fails the moment its cell is amended, which is what closes it.
    /// </summary>
    private static readonly (string Component, string Table)[] CellsShortOfTheCode = [];

    [Fact]
    public void TheWritesCellParseFindsTablesRatherThanNothing()
    {
        var catalogue = ArchitectureDocument.WriteTablesByComponent();

        Assert.Equal(CataloguedComponents, catalogue.Count);

        // Discrimination rather than a non-empty answer. A parse returning every
        // component with an empty set would satisfy a count and prove nothing.
        Assert.Equal(
            ["security", "security_daily", "universe_rejection"],
            catalogue["UniverseBuilder"].OrderBy(t => t, StringComparer.Ordinal));

        Assert.Equal(
            ["attribution", "candidate_set"],
            catalogue["CandidateAllocator"].OrderBy(t => t, StringComparer.Ordinal));

        // "via C27" is not a table and neither is "nothing". Both drop out on the
        // intersection with SCHEMA.md rather than being reported.
        Assert.Empty(catalogue["FreshnessGuard"]);
        Assert.Empty(catalogue["RecordInspector"]);

        // The one limit the parser's own summary states, asserted so that it is a known
        // gap rather than a surprise when phase 6 registers C17. The cell reads
        // `proposal.status`, which is a column and matches no table.
        Assert.Empty(catalogue["ProposalValidator"]);
    }

    [Fact]
    public void EveryTableAComponentWritesIsNamedInItsWritesCell()
    {
        var wrong = Undeclared(Declared(), ArchitectureDocument.WriteTablesByComponent())
            .Except(CellsShortOfTheCode.Select(d => d.Component + " -> " + d.Table), StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(wrong.Count == 0,
            "Component(s) writing a table ARCHITECTURE.html section 3 does not name in their " +
            "Writes cell: " + string.Join(", ", wrong) +
            ". The code writes a store the catalogue does not say it writes, and nothing else " +
            "reports it: one-writer-per-table still holds against SCHEMA.md and the narrative " +
            "simply describes a different system [INVARIANT 10].");
    }

    [Fact]
    public void EveryTableAWritesCellNamesIsDeclaredByItsComponent()
    {
        var wrong = Unread(Declared(), ArchitectureDocument.WriteTablesByComponent())
            .Except(RecordedDeviations.Select(d => d.Component + " -> " + d.Table), StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(wrong.Count == 0,
            "Writes cell(s) naming a table the component does not declare: " +
            string.Join(", ", wrong) +
            ". Either the component stopped writing it, in which case the catalogue is what " +
            "changes and the prior text goes to CHANGELOG.md [D-73], or it never wrote it and " +
            "the gap is recorded rather than declared away.");
    }

    /// <summary>
    /// The exemption cannot go stale, which is what made an entry safe to record in the
    /// Reads check and makes it safe here.
    /// </summary>
    [Fact]
    public void EveryRecordedDeviationIsStillADeviation()
    {
        var components = Declared();
        var catalogue = ArchitectureDocument.WriteTablesByComponent();

        foreach (var (component, table) in RecordedDeviations)
        {
            Assert.True(catalogue.TryGetValue(component, out var named) && named.Contains(table),
                component + "'s Writes cell no longer names " + table + ", so this deviation is " +
                "recorded against nothing. Delete the entry.");

            Assert.True(components.TryGetValue(component, out var declared) && !declared.Contains(table),
                component + " now writes " + table + ", so the gap is closed. Delete the entry, and " +
                "check that BUILD_PLAN.md's carried obligation goes with it.");
        }

        foreach (var (component, table) in CellsShortOfTheCode)
        {
            Assert.True(components.TryGetValue(component, out var declared) && declared.Contains(table),
                component + " no longer writes " + table + ", so this entry records nothing. Delete " +
                "it, and check whether the store still has a writer at all.");

            Assert.True(catalogue.TryGetValue(component, out var named) && !named.Contains(table),
                component + "'s Writes cell now names " + table + ", so the amendment has been made " +
                "and the gap is closed. Delete the entry, and check that the reported item in " +
                "PROGRESS.md goes with it.");
        }
    }

    /// <summary>
    /// A conformance test that has never failed has not been tested. Both directions are
    /// exercised against a fabricated component, because both pass vacuously if either
    /// map comes back empty and neither failure looks like one.
    /// </summary>
    [Fact]
    public void BothDirectionsFailOnAComponentThatDisagreesWithTheCatalogue()
    {
        var catalogue = ArchitectureDocument.WriteTablesByComponent();

        // Writes a table its cell does not name. C07's cell is "via C27".
        var writesTooMuch = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["FreshnessGuard"] = ["run_log"],
        };

        Assert.Equal(["FreshnessGuard -> run_log"], Undeclared(writesTooMuch, catalogue));
        Assert.Empty(Unread(writesTooMuch, catalogue));

        // Declares none of what its cell names. C14's cell names two.
        var writesNothing = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["CandidateAllocator"] = [],
        };

        Assert.Equal(
            ["CandidateAllocator -> attribution", "CandidateAllocator -> candidate_set"],
            Unread(writesNothing, catalogue));
        Assert.Empty(Undeclared(writesNothing, catalogue));
    }

    /// <summary>
    /// **A second write is what this check exists for**, so the components that have one
    /// are asserted to still have one. C14 is the case the `0006` obligation named in
    /// advance, and a change that moved either write elsewhere would leave every
    /// assertion above green while removing the thing being checked.
    /// </summary>
    [Fact]
    public void TheComponentsWithTwoWritesStillHaveTwo()
    {
        var declared = Declared();

        Assert.Equal(
            ["attribution", "candidate_set"],
            declared["CandidateAllocator"].OrderBy(t => t, StringComparer.Ordinal));

        Assert.Equal(
            ["screen_history", "screen_score_daily"],
            declared["ScreenEngine"].OrderBy(t => t, StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- helpers ---

    /// <summary>
    /// Every registered write owner and the distinct tables it writes.
    ///
    /// **Distinct tables and not table-and-operation pairs**, because section 3's Writes
    /// cell names stores rather than operations. C14 declares a delete and an insert on
    /// `candidate_set` and the cell names the table once, so the operation split is
    /// `WriteOwnershipConformanceTests`' business against `SCHEMA.md` and not this
    /// check's [INVARIANT 10 as amended].
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Declared()
        => PipelineComposition.AllOwnersForConformance(TestDatabase.ConnectionString)
            .ToDictionary(
                o => o.Name,
                o => (IReadOnlyList<string>) [.. o.WriteSet
                    .Select(w => w.Table)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(t => t, StringComparer.Ordinal)],
                StringComparer.Ordinal);

    /// <summary>Declared in code, absent from the Writes cell.</summary>
    private static IReadOnlyList<string> Undeclared(
        IReadOnlyDictionary<string, IReadOnlyList<string>> components,
        IReadOnlyDictionary<string, IReadOnlySet<string>> catalogue)
        => components
            .SelectMany(s => s.Value.Select(table => (Component: s.Key, Table: table)))
            .Where(x => !catalogue.TryGetValue(x.Component, out var named) || !named.Contains(x.Table))
            .Select(x => x.Component + " -> " + x.Table)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    /// <summary>Named in the Writes cell, absent from the declaration.</summary>
    private static IReadOnlyList<string> Unread(
        IReadOnlyDictionary<string, IReadOnlyList<string>> components,
        IReadOnlyDictionary<string, IReadOnlySet<string>> catalogue)
        => components
            .SelectMany(s => catalogue.TryGetValue(s.Key, out var named)
                ? named.Select(table => (Component: s.Key, Table: table))
                : [])
            .Where(x => !components[x.Component].Contains(x.Table, StringComparer.Ordinal))
            .Select(x => x.Component + " -> " + x.Table)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
}
