namespace StockResearcherLab.Core.Digest;

/// <summary>
/// A link in the digest provider chain. The vocabulary is closed [D-25, D-134].
///
/// **The order is the declaration order and it is the chain's order**, local first
/// and the secondary behind it [`ARCHITECTURE.html` §07]. It is stated here so a
/// reader of this file sees the chain, and it is not what the chain runs on: the
/// order in force on a date comes from <c>local_model_config.provider_order</c>
/// [D-136], because a chain reordered in code is a chain no config version records.
///
/// **Two, because the design has two implementations and not because two is a
/// limit.** `CONFIG_REFERENCE.md` says adding a third link is an insert. A phase that
/// adds one adds its name here and extends migration `0022` in the same checkpoint,
/// which is the rule `AlertType` already follows.
/// </summary>
public enum DigestProvider
{
    /// <summary>
    /// The local inference server over its OpenAI-compatible endpoint. Primary by
    /// choice rather than by cost: nothing leaves the machine [D-25].
    /// </summary>
    Local,

    /// <summary>
    /// Haiku 4.5, at <c>digest.secondary_model_id</c> [D-140]. Deliberately a
    /// different provider from the second research portfolio, so one vendor outage
    /// cannot take out both the digest fallback and a portfolio [D-25].
    /// </summary>
    Haiku,
}

/// <summary>
/// The provider vocabulary, as the strings <c>news_digest.provider</c> holds.
///
/// **One list, in one place, and the database holds the same one.** Migration
/// `0022` puts a CHECK on the column carrying these names, so a provider outside the
/// vocabulary fails the insert rather than being stored and read back as something no
/// reader recognises. This is `GateReasons`' and `AlertTypes`' shape and it is
/// deliberately the same shape: three vocabularies closed three different ways is how
/// one of them stops being closed [D-126].
///
/// **Why the column matters more than it looks.** `provider` and `model_name` are NOT
/// NULL because which model produced each digest is what separates a later shift in
/// results from a change in the evidence [D-29, INVARIANT 7's second boundary]. A
/// column that could hold an unrecognised string could hold a night nobody can
/// attribute.
/// </summary>
public static class DigestProviders
{
    /// <summary>Every provider, in declaration order.</summary>
    public static readonly IReadOnlyList<DigestProvider> All = [.. Enum.GetValues<DigestProvider>()];

    /// <summary>
    /// The stored form of one provider: lower case, so <c>Local</c> is <c>local</c>.
    /// Built rather than tabulated, so a provider added to the enum cannot arrive
    /// without its string.
    ///
    /// **No snake case here, and that is deliberate rather than an omission.** Both
    /// names are single words and `digest.chain` already carries them as
    /// <c>local, haiku</c> [`CONFIG_REFERENCE.md`]. `AlertTypes.Name` builds snake
    /// case because its names are compound and one of them carries digits, which is
    /// where that builder had a defect worth a comment. A single-word name has no
    /// break to get wrong.
    /// </summary>
    public static string Name(DigestProvider provider)
        => provider.ToString().ToLowerInvariant();

    /// <summary>Every provider's stored form, in declaration order.</summary>
    public static IReadOnlyList<string> Names => [.. All.Select(Name)];

    /// <summary>
    /// Reading a stored provider back. **Fails closed**: a string outside the
    /// vocabulary throws rather than resolving to a default, because a default here
    /// would turn an unattributable digest into an attributed one and the row would
    /// read as ordinary.
    /// </summary>
    public static DigestProvider Parse(string name)
    {
        foreach (var provider in All)
        {
            if (string.Equals(Name(provider), name, StringComparison.Ordinal))
            {
                return provider;
            }
        }

        throw new InvalidOperationException(
            "'" + name + "' is not a digest provider. The vocabulary is closed to " +
            string.Join(", ", Names) + " and migration `0022` holds the same list on the " +
            "column, so a row carrying this could not have been written by this system [D-134].");
    }
}
