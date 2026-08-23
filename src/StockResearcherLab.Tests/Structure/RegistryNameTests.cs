using StockResearcherLab.Core;
using StockResearcherLab.Data.Eodhd;
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

    // ------------------------------------------- composition [3.16 driver] ---

    /// <summary>
    /// **The registry can be built over a client the caller already holds**, which is
    /// what the backfill driver needs and why the overload exists. The rate limiter is
    /// per client and the provider's limit is not, so a driver that took the registry
    /// from one client and `UnitAllowance` from another would run two sliding windows
    /// of 1,000 a minute against one limit of 1,000 and meet it as a 429 mid-sweep.
    ///
    /// Asserted as the two routes agreeing on the component set, because that is the
    /// property the refactor could have broken: the token overload now delegates here
    /// rather than constructing its own owners.
    /// </summary>
    [Fact]
    public void BothCompositionRoutesRegisterTheSameComponents()
    {
        var viaToken = PipelineComposition
            .BuildRegistry(TestDatabase.ConnectionString, "not-a-real-token-registry-only", new FixedClock(
                new DateTimeOffset(2026, 8, 12, 3, 0, 0, TimeSpan.Zero), new DateOnly(2026, 8, 12)))
            .Owners.Select(o => o.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        var viaClient = PipelineComposition
            .BuildRegistry(TestDatabase.ConnectionString, EodhdClientDouble())
            .Owners.Select(o => o.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(viaToken, viaClient);
        Assert.Equal(15, viaClient.Count);
        Assert.Contains("PriceIngestor", viaClient);
        Assert.Contains("FundamentalsIngestor", viaClient);
    }

    /// <summary>
    /// No client is the no-token case and it is not the same registry. The seven
    /// provider-backed stages are absent, which is the branch that once made the write
    /// conformance tests run over two components while reading green [sign-off finding
    /// A], so it is asserted rather than assumed to have survived the refactor.
    /// </summary>
    [Fact]
    public void NoClientLeavesTheProviderBackedStagesOut()
    {
        var owners = PipelineComposition
            .BuildRegistry(TestDatabase.ConnectionString, eodhd: null)
            .Owners.Select(o => o.Name).ToList();

        Assert.Equal(8, owners.Count);
        Assert.DoesNotContain("PriceIngestor", owners);
        Assert.DoesNotContain("FundamentalsIngestor", owners);

        // The compute layer and RunLog are registered whether or not a token exists,
        // because none of them calls a provider.
        Assert.Contains("PercentileEngine", owners);
        Assert.Contains("RunLog", owners);
    }

    private static EodhdClient EodhdClientDouble()
        => new(new HttpClient(new NeverCalled()), "fake-token", new FixedClock(
            new DateTimeOffset(2026, 8, 12, 3, 0, 0, TimeSpan.Zero), new DateOnly(2026, 8, 12)));

    /// <summary>Building a registry makes no call, which this proves by failing if one is made.</summary>
    private sealed class NeverCalled : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Composing the registry must not make a call.");
    }
}
