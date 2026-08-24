using System.Globalization;
using StockResearcherLab.Core.Config;

namespace StockResearcherLab.Core.Screens;

/// <summary>
/// The screens registered on a date, read out of config.
///
/// **The set is discovered from config rows and never from a list in code**, which is
/// what <c>CLAUDE.md</c> section 5 means by a screen being a row: a sixth screen is an
/// insert, not a deployment, and 4.6 proves it by seeding one and scoring it with no
/// code change.
///
/// **Every screen is loaded through its own facade**, so a definition cannot be built
/// from another screen's keys even here [INVARIANT 2, D-120].
/// </summary>
public static class ScreenRegistry
{
    public const string Prefix = "screens.";

    private const string StateSuffix = ".state";

    /// <summary>
    /// The registered screen ids on a date, ordinal-ordered.
    ///
    /// A screen exists because it has a <c>state</c> row. That is one key rather than
    /// the presence of any key under its id, so a half-written screen carrying only a
    /// metric list is not silently registered and scored against a missing floor rule.
    /// </summary>
    public static async Task<IReadOnlyList<string>> IdsAsync(
        IConfigStore config, DateOnly asOf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var rows = await config.ResolveByPrefixAsync(Prefix, asOf, ct).ConfigureAwait(false);

        return [.. rows
            .Select(r => r.Key)
            .Where(k => k.EndsWith(StateSuffix, StringComparison.Ordinal))
            .Select(k => k[Prefix.Length..^StateSuffix.Length])
            // A shared key such as screens.floor_percentile has no id segment, and a
            // key with a dot inside the id would name something this registry cannot
            // address. Both are excluded rather than half-read.
            .Where(id => id.Length > 0 && !id.Contains('.', StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)];
    }

    /// <summary>Every registered screen on the date, whatever its state, ordinal-ordered by id.</summary>
    public static async Task<IReadOnlyList<ScreenDefinition>> LoadAsync(
        IConfigStore config, DateOnly asOf, CancellationToken ct = default)
    {
        var ids = await IdsAsync(config, asOf, ct).ConfigureAwait(false);

        var definitions = new List<ScreenDefinition>(ids.Count);

        foreach (var id in ids)
        {
            definitions.Add(await LoadOneAsync(config, id, asOf, ct).ConfigureAwait(false));
        }

        return definitions;
    }

    /// <summary>
    /// The live screens only, which is what the allocator fills slots from. Shadows are
    /// scored like any other screen and allocated nothing [D-84, D-85].
    /// </summary>
    public static async Task<IReadOnlyList<ScreenDefinition>> LoadLiveAsync(
        IConfigStore config, DateOnly asOf, CancellationToken ct = default)
        => [.. (await LoadAsync(config, asOf, ct).ConfigureAwait(false))
            .Where(d => d.State == ScreenState.Live)];

    /// <summary>
    /// The screens that are scored, being live and shadow together. Retired screens are
    /// not scored at all [<c>SCREEN_LIFECYCLE.md</c> section 1.1].
    /// </summary>
    public static async Task<IReadOnlyList<ScreenDefinition>> LoadScoredAsync(
        IConfigStore config, DateOnly asOf, CancellationToken ct = default)
        => [.. (await LoadAsync(config, asOf, ct).ConfigureAwait(false))
            .Where(d => d.State != ScreenState.Retired)];

    public static async Task<ScreenDefinition> LoadOneAsync(
        IConfigStore config, string screenId, DateOnly asOf, CancellationToken ct = default)
    {
        var facade = new ScreenConfigFacade(screenId, config);

        var metrics = ScreenMetricList.Parse(
            await facade.RequireAsync(facade.Own("metrics"), asOf, ct).ConfigureAwait(false));

        var minInputs = (int) ConfigValue.Long(
            await facade.RequireAsync(facade.Own("min_inputs"), asOf, ct).ConfigureAwait(false));

        var state = ParseState(
            await facade.RequireAsync(facade.Own("state"), asOf, ct).ConfigureAwait(false));

        var slots = (int) ConfigValue.Long(
            await facade.RequireAsync(facade.Own("slots"), asOf, ct).ConfigureAwait(false));

        var ranked = metrics.Count(m => !m.IsBonus);

        if (minInputs < 1 || minInputs > ranked)
        {
            throw new InvalidOperationException(
                $"Screen '{screenId}' has min_inputs " +
                minInputs.ToString(CultureInfo.InvariantCulture) +
                " against " + ranked.ToString(CultureInfo.InvariantCulture) +
                " ranked metrics. Below one it admits a name with no inputs at all, and above the " +
                "count it scores nothing on every date while looking like a screen that found " +
                "nothing [D-112].");
        }

        return new ScreenDefinition(
            screenId, metrics, minInputs, state, slots,
            await LoadEligibilityAsync(facade, asOf, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// A screen's gate, or null where it has none [D-120].
    ///
    /// **A screen is gated because its configuration carries the keys, not because code
    /// knows its id.** The presence of <c>quality_metrics</c> is the declaration, so a
    /// second gated screen is a set of config rows and not a class.
    ///
    /// **Half a gate fails the stage rather than being read as no gate.** Once the first
    /// key is there every other is required. The alternative is a screen that silently
    /// stops gating because one row was missed, which ranks the whole universe on
    /// distance below the 200-day average and is the falling-knife screen §05 says the
    /// gate exists to prevent.
    /// </summary>
    private static async Task<ScreenEligibility?> LoadEligibilityAsync(
        ScreenConfigFacade facade, DateOnly asOf, CancellationToken ct)
    {
        var quality = await facade.ResolveAsync(facade.Own(QualityMetrics), asOf, ct).ConfigureAwait(false);

        if (quality is null)
        {
            return null;
        }

        return new ScreenEligibility(
            new ScreenComposite(
                ScreenMetricList.Parse(quality),
                await IntAsync(facade, facade.Own("quality_min_inputs"), asOf, ct).ConfigureAwait(false)),
            await NumberAsync(facade, facade.Own("quality_quintile_min"), asOf, ct).ConfigureAwait(false),
            new ScreenComposite(
                ScreenMetricList.Parse(
                    await facade.RequireAsync(facade.Own("technical_metrics"), asOf, ct).ConfigureAwait(false)),
                await IntAsync(facade, facade.Own("technical_min_inputs"), asOf, ct).ConfigureAwait(false)),
            await NumberAsync(facade, facade.Own("technical_quintile_max"), asOf, ct).ConfigureAwait(false),

            // The three stabilisation thresholds sit in the older top-level namespace,
            // which is where CONFIG_REFERENCE.md has documented them since the first
            // corpus. They are as much this screen's as the composite keys are, and the
            // facade refuses another screen's under either form.
            await NumberAsync(facade, facade.OwnLegacy("stabilisation_z_max"), asOf, ct).ConfigureAwait(false),
            await NumberAsync(facade, facade.OwnLegacy("sentiment_delta_min"), asOf, ct).ConfigureAwait(false),
            await IntAsync(facade, facade.OwnLegacy("news_gate_min_articles"), asOf, ct).ConfigureAwait(false));
    }

    private const string QualityMetrics = "quality_metrics";

    private static async Task<double> NumberAsync(
        ScreenConfigFacade facade, string key, DateOnly asOf, CancellationToken ct)
        => ConfigValue.Double(await facade.RequireAsync(key, asOf, ct).ConfigureAwait(false));

    private static async Task<int> IntAsync(
        ScreenConfigFacade facade, string key, DateOnly asOf, CancellationToken ct)
        => (int) ConfigValue.Long(await facade.RequireAsync(key, asOf, ct).ConfigureAwait(false));

    private static ScreenState ParseState(ConfigRow row)
        => ConfigValue.String(row) switch
        {
            "live" => ScreenState.Live,
            "shadow" => ScreenState.Shadow,
            "retired" => ScreenState.Retired,
            var other => throw new InvalidOperationException(
                $"Config key '{row.Key}' resolved to state '{other}'. A screen has three states and " +
                "an unrecognised one fails the stage rather than being treated as live, which would " +
                "put an unintended screen's names into candidate_set [D-84]."),
        };

    /// <summary>
    /// A raw JSON metric list for a key the screen owns, used by S5 for its two
    /// composites. Routed through the facade so the composite cannot be built from
    /// another screen's list [D-120].
    /// </summary>
    public static async Task<IReadOnlyList<ScreenMetric>> CompositeAsync(
        IConfigStore config, string screenId, string suffix, DateOnly asOf, CancellationToken ct = default)
    {
        var facade = new ScreenConfigFacade(screenId, config);

        return ScreenMetricList.Parse(
            await facade.RequireAsync(facade.Own(suffix), asOf, ct).ConfigureAwait(false));
    }
}
