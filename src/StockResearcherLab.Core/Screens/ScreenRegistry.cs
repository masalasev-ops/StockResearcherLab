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

        return new ScreenDefinition(screenId, metrics, minInputs, state, slots);
    }

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
