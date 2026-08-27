using System.Globalization;
using System.Text;

namespace StockResearcherLab.Core.Digest;

/// <summary>
/// Which of the night's candidates go to the chain's second link [D-27, D-138].
///
/// **The hash is written here rather than taken from the runtime, and that is the whole
/// decision.** D-27 says the pair is chosen by the date seed and `CLAUDE.md` §6 says
/// `Random` is seeded from the run date, so a seeded <see cref="System.Random"/> is the
/// reading that suggests itself first. It is rejected because the sequence a seeded
/// `Random` produces is a runtime implementation detail: a framework upgrade that changed
/// it would silently change which pair the rotation compared, splitting the paired sample
/// D-27 exists to build with nothing saying so [D-138].
///
/// **FNV-1a, 64 bit, over the UTF-8 bytes of the invariant date string and the ticker.**
/// It is a published algorithm with published test vectors, one of which is asserted, so
/// this file cannot drift from the thing it says it is either.
/// </summary>
public static class DigestRotation
{
    /// <summary>
    /// The candidates that go to the second link, in the order the hash puts them.
    ///
    /// **Ties break on the ticker ordinally**, so the order is total and two runs of one
    /// night produce the same pair whatever order the candidates arrived in [INVARIANT 6].
    ///
    /// **A candidate set smaller than <paramref name="count"/> rotates whole and nothing is
    /// padded** [D-138]. A count of zero or less selects nothing, which is the same
    /// statement read from the other end.
    /// </summary>
    public static IReadOnlyList<string> Select(
        DateOnly date, IReadOnlyList<string> candidates, int count)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (count <= 0 || candidates.Count == 0)
        {
            return [];
        }

        // The date as `yyyy-MM-dd` and invariant, because the seed is a string and a
        // locale-formatted one would rotate a different pair on a differently configured
        // machine [`CLAUDE.md` §6].
        var seed = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return
        [
            .. candidates
                .OrderBy(t => Hash(seed + t))
                .ThenBy(t => t, StringComparer.Ordinal)
                .Take(count),
        ];
    }

    /// <summary>The FNV-1a 64-bit offset basis.</summary>
    public const ulong OffsetBasis = 14695981039346656037;

    /// <summary>The FNV-1a 64-bit prime.</summary>
    public const ulong Prime = 1099511628211;

    /// <summary>
    /// FNV-1a over the UTF-8 bytes, unchecked because the algorithm is defined on wrapping
    /// multiplication and a checked build would throw on the first long input rather than
    /// hash it.
    /// </summary>
    public static ulong Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var hash = OffsetBasis;

        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            unchecked
            {
                hash ^= b;
                hash *= Prime;
            }
        }

        return hash;
    }
}
