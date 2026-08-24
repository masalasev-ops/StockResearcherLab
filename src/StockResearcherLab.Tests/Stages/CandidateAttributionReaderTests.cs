using StockResearcherLab.Api;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// Checkpoint 4.2. D-110's last clause checked mechanically: every reader meaning
/// "candidate" declares <c>candidate_attribution</c> rather than <c>attribution</c>,
/// so reading the table becomes a deliberate act rather than the default.
///
/// **This check is built against no registered reader at all, and that is the point.**
/// Every reader on the list below is a phase 8 or phase 9 component and none exists.
/// A check over an empty list passes vacuously and would keep passing until phase 9,
/// by which time each of those components would have been written by a session that
/// had not read <c>SCREEN_LIFECYCLE.md</c> section 4.5. Writing it now means each of
/// them is written under a check instead.
///
/// So the real assertions here are the fabricated ones. They prove the rule has teeth
/// before there is anything for it to bite.
/// </summary>
public sealed class CandidateAttributionReaderTests
{
    private const string Table = "attribution";
    private const string View = "candidate_attribution";

    /// <summary>
    /// The readers <c>SCREEN_LIFECYCLE.md</c> section 4.5 marks as meaning "candidate",
    /// restricted to the ones that are components with a declared read set.
    ///
    /// **The enumeration is that document's and is cited rather than restated** [D-85,
    /// section 4.5, section 4.7]. Its reasoning for each entry is not copied here: a
    /// second copy of a rule is a rule that can disagree with itself.
    ///
    /// **Three of section 4.5's candidate-meaning entries are deliberately absent, and
    /// naming them is the point.** <c>VALIDITY.md</c> section 3's counts and
    /// <c>WORKED_EXAMPLE.md</c> are documents rather than components, so no read set can
    /// hold them. U2, U7 and the abstention analysis are screens rendered from read
    /// models rather than readers of the store, so the declaration that binds them is
    /// C31 ReadModelBuilder's, which is on the list. A component-level conformance test
    /// cannot reach a document or a screen, and pretending otherwise would put names on
    /// this list that nothing could ever satisfy.
    /// </summary>
    private static readonly string[] CandidateMeaningReaders =
    [
        "LessonWriter",        // C23, phase 8
        "CalibrationReporter", // C24, phase 8
        "ReadModelBuilder",    // C31, phase 9, candidate half
    ];

    private sealed class FabricatedReader(string name, params string[] reads) : IReadOwner
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> ReadSet { get; } = reads;
    }

    /// <summary>
    /// The rule itself, factored so the registered half and the fabricated half run the
    /// identical check rather than two checks that can drift apart.
    /// </summary>
    private static void AssertReadsTheViewNotTheTable(IReadOwner reader)
    {
        if (!CandidateMeaningReaders.Contains(reader.Name, StringComparer.Ordinal))
        {
            return;
        }

        Assert.False(reader.ReadSet.Contains(Table, StringComparer.Ordinal),
            $"{reader.Name} means \"candidate\" per SCREEN_LIFECYCLE.md section 4.5 and declares " +
            $"`{Table}`. It must declare `{View}` instead. The table carries shadow rows, and a " +
            "reader meaning candidate that reads it is wrong in a way that produces a plausible " +
            "number rather than an error [D-85, D-110].");

        Assert.True(reader.ReadSet.Contains(View, StringComparer.Ordinal),
            $"{reader.Name} means \"candidate\" and declares neither `{Table}` nor `{View}`. " +
            $"It must declare `{View}` [D-110].");
    }

    /// <summary>
    /// The list is non-empty, which is what stops every assertion below passing over
    /// nothing if the list were ever emptied.
    /// </summary>
    [Fact]
    public void TheDeclaredListIsNonEmpty()
    {
        Assert.NotEmpty(CandidateMeaningReaders);
    }

    /// <summary>
    /// The registered half. Vacuous today by construction, and it stops being vacuous
    /// the moment phase 8 registers C23.
    /// </summary>
    [Fact]
    public void EveryRegisteredCandidateMeaningReaderDeclaresTheView()
    {
        foreach (var reader in AllReadOwners())
        {
            AssertReadsTheViewNotTheTable(reader);
        }
    }

    /// <summary>A reader on the list declaring the table fails, and the failure names the view.</summary>
    [Fact]
    public void AListedReaderDeclaringTheTableFailsAndTheFailureNamesTheView()
    {
        var reader = new FabricatedReader("LessonWriter", Table, "trade_outcome");

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertReadsTheViewNotTheTable(reader));

        Assert.Contains(View, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A reader on the list declaring the view passes.</summary>
    [Fact]
    public void AListedReaderDeclaringTheViewPasses()
    {
        var reader = new FabricatedReader("LessonWriter", View, "trade_outcome");

        AssertReadsTheViewNotTheTable(reader);
    }

    /// <summary>
    /// **A reader not on the list declaring the table passes**, which is the
    /// everything-surfaced case and would otherwise be caught wrongly.
    ///
    /// C21 ForwardReturnFiller is the reason the design uses <c>attribution</c> at all:
    /// one filler filling live and shadow rows in the same pass. A rule that forced
    /// every reader onto the view would break exactly the reader the table exists for
    /// [section 4.5].
    /// </summary>
    [Fact]
    public void AnUnlistedReaderDeclaringTheTablePasses()
    {
        var reader = new FabricatedReader("ForwardReturnFiller", Table, "price_daily");

        AssertReadsTheViewNotTheTable(reader);
    }

    /// <summary>
    /// A listed reader declaring neither fails too, so the rule is not satisfied by
    /// simply not mentioning the store.
    /// </summary>
    [Fact]
    public void AListedReaderDeclaringNeitherFails()
    {
        var reader = new FabricatedReader("CalibrationReporter", "proposal");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertReadsTheViewNotTheTable(reader));
    }

    /// <summary>
    /// <c>DeclaredAccess</c> admits the view through the same guard a table goes
    /// through, so nothing special-cases it [D-110].
    ///
    /// The guard holds a set of declared names and never consults a table list, which
    /// is why a view needs no accommodation. Asserted rather than assumed, because "it
    /// happens to work" and "it is guaranteed to work" read identically until someone
    /// adds a table-existence check.
    /// </summary>
    [Fact]
    public void DeclaredAccessAdmitsTheViewLikeAnyTable()
    {
        var access = new DeclaredAccess("LessonWriter", [View], []);

        Assert.True(access.CanRead(View));
        Assert.False(access.CanRead(Table));

        access.EnsureCanRead(View);

        var ex = Assert.Throws<UndeclaredTableAccessException>(() => access.EnsureCanRead(Table));
        Assert.Equal(Table, ex.Table);
    }

    private static IReadOnlyList<IReadOwner> AllReadOwners()
    {
        var owners = new List<IReadOwner>();

        owners.AddRange(PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .OfType<IReadOwner>());

        owners.AddRange(ApiComposition.AllReadOwnersForConformance(TestDatabase.ConnectionString));

        return owners;
    }
}
