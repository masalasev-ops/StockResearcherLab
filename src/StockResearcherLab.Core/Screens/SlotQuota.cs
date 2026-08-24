using System.Globalization;

namespace StockResearcherLab.Core.Screens;

/// <summary>
/// How one screen's slots divide across the three size buckets [D-116, D-89,
/// <c>SCREEN_LIFECYCLE.md</c> §8.1].
///
/// **This proportion is the only quota arithmetic there is.** The three
/// <c>screens.quota_*</c> keys were that proportion's output at eight slots and are
/// retired: three keys that must sum to the slot count, can be set so they do not, and
/// nothing would notice [D-116].
///
/// **Two properties are why this proportion rather than another**, and both are checked
/// as tests rather than asserted here. The large share never exceeds a quarter at any
/// count, so D-7's megacap bound of ten of forty holds at every live-screen count rather
/// than only at five screens of eight. And <c>small/slots</c> is at its lowest at exactly
/// eight, so D-7's small-cap floor of fifteen is a floor across the whole reachable space
/// rather than a figure that happens to hold today.
/// </summary>
/// <param name="Large">Names of $10B and above.</param>
/// <param name="Mid">$2B to $10B.</param>
/// <param name="Small">$300M to $2B. The remainder, so the extra seat goes here.</param>
public readonly record struct SlotQuota(int Large, int Mid, int Small)
{
    /// <summary>The slot count this quota divides, which it sums to by construction.</summary>
    public int Total => Large + Mid + Small;

    /// <summary>
    /// D-89's proportion, integral at every count.
    ///
    /// <code>
    /// large = floor(slots / 4)
    /// rest  = slots - large
    /// mid   = floor(rest / 2)
    /// small = rest - mid
    /// </code>
    ///
    /// The remainder falls to small deliberately. Small caps are what this design's
    /// diversity guarantee is about, and rounding the spare seat upward anywhere else
    /// would move the megacap share the bound above is stated in terms of.
    /// </summary>
    public static SlotQuota For(int slots)
    {
        if (slots < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slots), slots, "A screen cannot have a negative slot count.");
        }

        var large = slots / 4;
        var rest = slots - large;
        var mid = rest / 2;

        return new SlotQuota(large, mid, rest - mid);
    }

    /// <summary>
    /// The seats this quota gives one bucket, by the name <c>security_daily.size_bucket</c>
    /// holds.
    ///
    /// **A bucket name outside the three fails rather than returning zero.** Zero seats
    /// and an unrecognised bucket are different things: the first is a quota this screen
    /// genuinely has none of and the second is a name nothing can place, and a screen
    /// silently sending fewer names is precisely what an empty slot is supposed to mean
    /// [D-8].
    /// </summary>
    public int Seats(string bucket)
        => bucket switch
        {
            "large" => Large,
            "mid" => Mid,
            "small" => Small,
            _ => throw new InvalidOperationException(
                $"'{bucket}' is not a size bucket. UniverseBuilder assigns large, mid and small " +
                "and nothing else, so a fourth value here is a name no quota can place, which " +
                "would read as a screen that sent fewer names [D-8]."),
        };

    /// <summary>The three bucket names, in the order a quota is stated in.</summary>
    public static readonly IReadOnlyList<string> Buckets = ["large", "mid", "small"];

    public override string ToString()
        => string.Join("/", new[] { Large, Mid, Small }
            .Select(n => n.ToString(CultureInfo.InvariantCulture)));
}
