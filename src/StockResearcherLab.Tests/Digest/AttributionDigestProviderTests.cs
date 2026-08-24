using StockResearcherLab.Core.Stages;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Digest;

/// <summary>
/// Checkpoint 5.2. D-135 checked mechanically: `attribution` keeps two writers with
/// one operation each, and nothing in the digest layer becomes a third.
///
/// **The column that invites the third writer is `digest_provider`.** It exists in
/// `0001`, nothing can fill it, and the obvious repair is to let C33 update it after
/// the digest is produced. That is the repair D-135 declines: C14 inserts the row at
/// 18:30 and the digest exists at 18:33, C21 owns the update and only of the nine
/// return columns, and `SCHEMA.md` says no third component writes there at all.
///
/// **This check is built against a registry that has neither C21 nor C33, and that is
/// the point** [4.2's argument unchanged]. C21 is phase 8 and C33 is 5.8, so today the
/// assertion over the real registry passes over a set of one. A check that can only
/// pass is not a check, so the fabricated cases below are the real assertions: they
/// prove the rule has teeth before there is anything for it to bite.
/// </summary>
public sealed class AttributionDigestProviderTests
{
    private const string Table = "attribution";

    private sealed class FabricatedWriter(string name, params TableWrite[] writes) : IWriteOwner
    {
        public string Name { get; } = name;

        public IReadOnlyList<TableWrite> WriteSet { get; } = writes;
    }

    private static IReadOnlyList<(string Component, WriteOperation Op)> AttributionWriters(
        IEnumerable<IWriteOwner> owners)
        => owners
            .SelectMany(o => o.WriteSet
                .Where(w => string.Equals(w.Table, Table, StringComparison.Ordinal))
                .Select(w => (o.Name, w.Operation)))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Operation)
            .ToList();

    /// <summary>
    /// Over the real registry: the allocator inserts, and nothing updates. The update
    /// claim belongs to ForwardReturnFiller and that component does not exist yet, so
    /// the assertion is that the slot is empty rather than that it is correctly filled.
    /// </summary>
    [Fact]
    public void TheOnlyRegisteredAttributionWriterIsTheAllocatorsInsert()
    {
        var writers = AttributionWriters(
            PipelineComposition.AllOwnersForConformance(TestDatabase.ConnectionString));

        Assert.Equal(
            [("CandidateAllocator", WriteOperation.Insert)],
            writers);
    }

    /// <summary>
    /// The digest layer must not claim `attribution`. Asserted against a fabricated
    /// NewsDigester that does, because the real one is 5.8 and a rule stated only in
    /// prose is a rule the next session writes past.
    /// </summary>
    [Fact]
    public void ADigesterClaimingAttributionIsCaught()
    {
        var owners = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .Append(new FabricatedWriter(
                "NewsDigester",
                new TableWrite("news_digest", WriteOperation.Insert),
                new TableWrite(Table, WriteOperation.Update, ["digest_provider"])))
            .ToList();

        var writers = AttributionWriters(owners);

        Assert.Contains(("NewsDigester", WriteOperation.Update), writers);
        Assert.NotEqual(
            [("CandidateAllocator", WriteOperation.Insert)],
            writers);
    }

    /// <summary>
    /// And the collision that the same repair would cause once C21 exists: two
    /// components claiming `attribution.Update` is the case
    /// <see cref="WriteOwnershipConformanceTests"/> exists to catch, and a column list
    /// does not make it two different claims. Both are fabricated, because neither
    /// component is built.
    /// </summary>
    [Fact]
    public void TwoComponentsUpdatingAttributionCollideEvenWithDisjointColumns()
    {
        var owners = new IWriteOwner[]
        {
            new FabricatedWriter("ForwardReturnFiller", new TableWrite(
                Table, WriteOperation.Update,
                ["return_5d_raw", "return_5d_vs_spy", "return_5d_vs_peers"])),
            new FabricatedWriter("NewsDigester", new TableWrite(
                Table, WriteOperation.Update, ["digest_provider"])),
        };

        var claims = AttributionWriters(owners);

        Assert.Equal(2, claims.Count);
        Assert.All(claims, c => Assert.Equal(WriteOperation.Update, c.Op));

        // One table, one operation, two components. The registry test asserts over the
        // table-and-operation pair, and disjoint column sets do not separate them.
        var byPair = claims.GroupBy(c => c.Op).Single();
        Assert.Equal(2, byPair.Count());
    }
}
