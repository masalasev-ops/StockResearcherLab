using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Pipeline;

/// <summary>
/// The single declaration of who touches what. Every stage in the pipeline is
/// registered here and nowhere else, which is what lets one test assert that
/// write ownership matches SCHEMA.md rather than a reviewer checking it by hand
/// [CLAUDE.md section 5, INVARIANT 10].
///
/// A stage that is not registered does not run. That is deliberate: the
/// alternative is a stage discoverable by reflection, which would let one exist
/// without ever appearing in the declaration the conformance test reads.
/// </summary>
public sealed class StageRegistry
{
    private readonly List<IStage> _stages;

    public StageRegistry(IEnumerable<IStage> stages)
    {
        // Ordinal sort by name. Registry order must not depend on the order the
        // caller happened to construct things in, or on a locale
        // [CLAUDE.md section 6].
        _stages = stages.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        var duplicate = _stages.GroupBy(s => s.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Two stages are registered as '{duplicate.Key}'. A component name is how the " +
                "registry, SCHEMA.md and ARCHITECTURE.html section 3 refer to the same thing, so " +
                "it has to be unique here.");
        }
    }

    /// <summary>Every registered stage, ordinal by name.</summary>
    public IReadOnlyList<IStage> Stages => _stages;

    /// <summary>Every declared write across every stage, as component, table, operation triples.</summary>
    public IReadOnlyList<(string Component, TableWrite Write)> AllWrites()
        => _stages.SelectMany(s => s.WriteSet.Select(w => (s.Name, w)))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.w.Table, StringComparer.Ordinal)
            .ThenBy(x => x.w.Operation)
            .ToList();

    public IStage? Find(string name)
        => _stages.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>The declared sets for a stage, which is what <see cref="IStageData"/> is constructed against.</summary>
    public DeclaredAccess AccessFor(IStage stage) => new(stage);
}
