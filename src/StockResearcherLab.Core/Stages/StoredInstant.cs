namespace StockResearcherLab.Core.Stages;

/// <summary>
/// A `timestamp with time zone` as it arrives through <see cref="IStageData"/>'s object
/// route.
///
/// **This exists because `as DateTimeOffset?` on that route is silently always null.**
/// The driver hands a `timestamptz` back as a <see cref="DateTime"/> with
/// <see cref="DateTimeKind.Utc"/>, so the cast that reads correctly is a cast the compiler
/// accepts, produces no error, and yields an absence for every row. What follows is the
/// failure this system is built around: nothing throws, the run completes, and a rule
/// stated in terms of the instant quietly stops being applied [`CLAUDE.md` §1].
///
/// **It was found at 5.9 and it had already changed what a model was sent.** C33 read
/// `headline.published_at` this way, so D-143's most-recent-first selection ordered on
/// null for every article and fell through to its tie-breaks, and the rendered input told
/// the model "date unknown" on articles whose date the store held.
///
/// **An unexpected type throws rather than reading as absent**, which is the whole point:
/// the defect above was a conversion that failed by producing nothing. A driver mapping
/// change is a thing to fail on, not to absorb [`CLAUDE.md` §6].
/// </summary>
public static class StoredInstant
{
    /// <summary>
    /// The instant, or null where the column is null. Null means the store holds no
    /// instant and never means the conversion did not work.
    /// </summary>
    public static DateTimeOffset? From(object? value) => value switch
    {
        null => null,
        DBNull => null,
        DateTimeOffset at => at,

        // Kind is Utc on this route and is asserted rather than assumed: an unspecified
        // kind would be read as local time by the constructor, which is an offset error
        // rather than an absence and is worse.
        DateTime utc when utc.Kind == DateTimeKind.Utc => new DateTimeOffset(utc),

        _ => throw new InvalidOperationException(
            $"A stored instant arrived as {value.GetType().FullName} and was not converted. " +
            "Reading it as an absence is what this type exists to stop [5.9]."),
    };
}
