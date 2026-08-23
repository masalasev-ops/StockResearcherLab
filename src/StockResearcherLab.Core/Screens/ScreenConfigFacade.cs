using System.Text.RegularExpressions;
using StockResearcherLab.Core.Config;

namespace StockResearcherLab.Core.Screens;

/// <summary>
/// Thrown when a screen asks for a key belonging to another screen.
///
/// **This is what makes INVARIANT 2 structural rather than remembered.** Screens never
/// read each other: each sees only its own config and the percentile store. Sharing a
/// computed value between them looks like removing duplication and destroys the
/// independence the design rests on [INVARIANT 2, D-6, D-120].
/// </summary>
public sealed class ForeignScreenConfigException : InvalidOperationException
{
    public ForeignScreenConfigException(string screenId, string key)
        : base($"Screen '{screenId}' asked for config key '{key}', which belongs to another screen. " +
               "A screen sees only its own config and the percentile store. Sharing a value between " +
               "screens looks like removing duplication and destroys the independence the design " +
               "rests on [INVARIANT 2, D-6]. S5's quality and technical composites duplicate S1's " +
               "metric list deliberately and that copy is the price of this rule [D-120].")
    {
        ScreenId = screenId;
        Key = key;
    }

    public string ScreenId { get; }

    public string Key { get; }
}

/// <summary>
/// One screen's view of configuration. Constructed with a screen id, it refuses any
/// key scoped to a different screen and passes everything else through unchanged.
///
/// **This is <c>DeclaredAccess</c>'s idiom applied to configuration.** That guard makes
/// an undeclared table throw before a connection opens; this one makes another screen's
/// key throw before a value is read. In both cases the point is that the shortcut is
/// unavailable rather than discouraged, which is why D-120's deliberate duplication is
/// safe to write down: nothing can quietly replace the copy with a reference.
/// </summary>
public sealed class ScreenConfigFacade
{
    /// <summary>
    /// A key scoped to a named screen, in either of the two forms this corpus uses:
    /// <c>screens.S5.quality_metrics</c> and the older top-level <c>s5.*</c> that
    /// predates the <c>screens.</c> namespace.
    ///
    /// **Matching both is what stops the older form being a hole in the rule.** The
    /// four <c>s5.*</c> keys in <c>CONFIG_REFERENCE.md</c> are as much S5's as
    /// <c>screens.S5.quality_metrics</c> is, and a guard that only knew the newer
    /// prefix would let any screen read them.
    /// </summary>
    private static readonly Regex ScreenScoped = new(
        @"^(?:screens\.)?(?<id>[Ss]\d+)\.", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IConfigStore _inner;

    public ScreenConfigFacade(string screenId, IConfigStore inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenId);
        ArgumentNullException.ThrowIfNull(inner);

        ScreenId = screenId;
        _inner = inner;
    }

    public string ScreenId { get; }

    /// <summary>
    /// Whether this screen may read the key. Shared keys, meaning anything not scoped
    /// to a named screen, are reachable by every screen: <c>screens.floor_percentile</c>
    /// is one rule for all screens rather than one screen's property.
    /// </summary>
    public bool CanRead(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var match = ScreenScoped.Match(key);

        return !match.Success
            || string.Equals(match.Groups["id"].Value, ScreenId, StringComparison.OrdinalIgnoreCase);
    }

    public void EnsureCanRead(string key)
    {
        if (!CanRead(key))
        {
            throw new ForeignScreenConfigException(ScreenId, key);
        }
    }

    public async Task<ConfigRow> RequireAsync(string key, DateOnly asOf, CancellationToken ct = default)
    {
        EnsureCanRead(key);

        return await _inner.RequireAsync(key, asOf, ct).ConfigureAwait(false);
    }

    public async Task<ConfigRow?> ResolveAsync(string key, DateOnly asOf, CancellationToken ct = default)
    {
        EnsureCanRead(key);

        return await _inner.ResolveAsync(key, asOf, ct).ConfigureAwait(false);
    }

    /// <summary>This screen's own key, built rather than typed, so a call site cannot name another screen's.</summary>
    public string Own(string suffix) => $"screens.{ScreenId}.{suffix}";
}
