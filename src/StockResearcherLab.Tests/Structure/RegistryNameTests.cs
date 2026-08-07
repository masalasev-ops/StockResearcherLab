using StockResearcherLab.Pipeline;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Structure;

/// <summary>
/// Checkpoint 1.11. Every registered component is one `ARCHITECTURE.html` section 3
/// names, and it is spelled the way the catalogue spells it.
///
/// Names match the architecture is the cheapest traceability available and costs
/// nothing to maintain [CLAUDE.md section 6]. It went unchecked through phase 0,
/// which is how `NoOpStage` came to be a registered component the catalogue does not
/// have, carried forward as an obligation from phase 0 to phase 1 rather than found.
/// </summary>
public sealed class RegistryNameTests
{
    [Fact]
    public void TheCatalogueParses()
    {
        // Guards the parser rather than the registry. A regex that stopped matching
        // would return nothing and the assertion below would pass over an empty
        // set, which is the failure this repository keeps meeting in other forms.
        var components = ArchitectureDocument.Components();

        Assert.True(components.Count >= 30,
            $"ARCHITECTURE.html section 3 parsed to only {components.Count} components, which means " +
            "the parser stopped matching rather than that the catalogue shrank.");

        Assert.Equal("UniverseBuilder", components["C01"]);
        Assert.Equal("PriceIngestor", components["C02"]);
        Assert.Equal("FreshnessGuard", components["C07"]);
        Assert.Equal("RunLog", components["C27"]);
        Assert.Equal("FlowEngine", components["C34"]);
    }

    [Fact]
    public void EveryRegisteredComponentIsNamedInTheCatalogue()
    {
        var declared = ArchitectureDocument.ComponentNames();

        var strangers = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .Select(o => o.Name)
            .Where(n => !declared.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(strangers.Count == 0,
            "Registered component(s) ARCHITECTURE.html section 3 does not name: " +
            string.Join(", ", strangers) +
            ". A component invented in code is a component the architecture does not " +
            "constrain, and the catalogue is what says which thirty-four exist.");
    }

    /// <summary>
    /// The fixture that makes the assertion above mean something. A conformance
    /// test that has never failed has not been tested, and this one would have
    /// passed vacuously if `ComponentNames` ever returned an empty set or the
    /// containment check were inverted.
    ///
    /// `NoOpStage` is used as the stranger deliberately: it is the exact name that
    /// was registered and uncatalogued through all of phase 0.
    /// </summary>
    [Fact]
    public void AComponentTheCatalogueDoesNotNameIsCaught()
    {
        var declared = ArchitectureDocument.ComponentNames();

        string[] registered = ["PriceIngestor", "RunLog", "NoOpStage"];

        var strangers = registered
            .Where(n => !declared.Contains(n))
            .ToList();

        Assert.Equal(["NoOpStage"], strangers);

        // And the two real ones are not false positives.
        Assert.Contains("PriceIngestor", declared);
        Assert.Contains("RunLog", declared);
    }

    [Fact]
    public void NoOpStageIsGone()
    {
        // Named rather than left to the assertion above, because this is the
        // carried obligation phase 0 owed phase 1 and a reader should find it by
        // searching for the name.
        var registered = PipelineComposition
            .AllOwnersForConformance(TestDatabase.ConnectionString)
            .Select(o => o.Name)
            .ToList();

        Assert.DoesNotContain("NoOpStage", registered);
    }
}
