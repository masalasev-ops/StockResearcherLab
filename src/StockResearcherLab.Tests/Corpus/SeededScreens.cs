using StockResearcherLab.Data;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// The screen ids `ConfigSeeder` registers, read off the seeder rather than listed.
///
/// **Why this exists.** Four fixtures retire the seeded screens so that their own screen
/// is the only one scored, and all four carried the literal `["S1", "S2", "S3", "S4",
/// "S5"]`. Q.7 registered three shadows and every one of those fixtures then scored eight
/// screens where it asserts over one. That is the second-list defect this repository keeps
/// finding, in the place it is least visible: a fixture's own setup.
///
/// **The teardown was the half that actually broke.** Its `LIKE 'screens.S%.state'`
/// matched the five live ids and not `X-NSI`, `X-ACC` or `X-FM`, so the retire rows for
/// the three shadows survived the fixture and every later test in the same database read
/// them as retired. A shared list fixes the setup; <see cref="AnyStateVersionTwo"/> is
/// what fixes the other half.
/// </summary>
public static class SeededScreens
{
    /// <summary>
    /// Every screen id the seeder registers, whatever its state, ordinal-ordered.
    ///
    /// A screen exists because it has a `state` row, which is `ScreenRegistry`'s own rule
    /// applied to the seeded list rather than restated [`CLAUDE.md` §5].
    /// </summary>
    public static IReadOnlyList<string> Ids()
        => [.. ConfigSeeder.Keys
            .Select(k => k.Key)
            .Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)
                        && k.EndsWith(StateSuffix, StringComparison.Ordinal))
            .Select(k => k[Prefix.Length..^StateSuffix.Length])
            .Where(id => !id.Contains('.', StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)];

    /// <summary>
    /// The `LIKE` pattern a fixture's teardown uses to remove its own retire rows.
    ///
    /// **Every screen id and not the ones beginning with S.** The four fixtures are the
    /// only writers of a version 2 `state` row, so the pattern can be this wide, and it
    /// has to be: a pattern narrower than the set the setup writes leaves rows behind
    /// that the next test reads as configuration.
    /// </summary>
    public const string AnyStateVersionTwo = "screens.%.state";

    private const string Prefix = "screens.";

    private const string StateSuffix = ".state";
}
