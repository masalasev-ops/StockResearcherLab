namespace StockResearcherLab.Core.Digest;

/// <summary>
/// Which of D-134's four states one candidate's digest is in on one date.
///
/// **The four exist because the middle two are the pair that gets collapsed** [D-134].
/// A null <c>digest_text</c> is "there was nothing to read", which is what D-60's
/// no-digest disqualifier bites on; the escape hatch is "a link read the articles and
/// found nothing material", which is a reading rather than an absence and which D-60
/// does not bite on. Collapsing them makes S5's disqualifier fire on thinly covered
/// small caps, which is the population this design exists to reach.
///
/// **It lives in Core because two projects classify on it and neither may reference the
/// other.** C33 writes the states in the Pipeline and C36's digest panel names them in
/// the Api, which never references Pipeline [`CLAUDE.md` §4]. A copy on each side is the
/// second list this repository keeps finding out of step, so the vocabulary and the rule
/// that reads it sit here once.
/// </summary>
public enum DigestOutcome
{
    /// <summary>
    /// No row at all: the ticker was not a candidate that night, or the run halted
    /// before the digest step [D-134, D-139].
    ///
    /// **Which of the two is not readable from `news_digest`**, and a reader holding
    /// only that table says no row rather than choosing between them.
    /// </summary>
    NoRow,

    /// <summary>
    /// A row whose <c>digest_text</c> is null: a link was selected and there was nothing
    /// to send, meaning no article inside the window or every article carrying a null
    /// body [D-131]. This is "no digest available" in D-60's sense.
    /// </summary>
    NoArticles,

    /// <summary>
    /// A row carrying the escape hatch exactly: a link read the articles and returned
    /// what `prompts/digest-instruction.md` defines for material it judges immaterial.
    /// </summary>
    NoMaterialNews,

    /// <summary>An ordinary digest.</summary>
    Prose,
}

/// <summary>
/// The outcome vocabulary and the one rule that reads it out of a stored row.
///
/// **One classifier, not one per reader.** The states are distinguished by a null, by an
/// exact string and by neither, and a second implementation of that comparison is how the
/// middle two stop being separable.
/// </summary>
public static class DigestOutcomes
{
    /// <summary>
    /// The escape hatch, exactly as `prompts/digest-instruction.md` defines it and as
    /// C33 compares against [D-134].
    ///
    /// **Compared ordinally and never trimmed here.** C33 trims the link's answer once,
    /// where the answer arrives, so a row already holds the trimmed text and a second
    /// trim in a reader would let a stored value with whitespace read as the hatch when
    /// the writer did not treat it as one.
    /// </summary>
    public const string NoMaterialNewsText = "NO MATERIAL NEWS";

    /// <summary>Every outcome, in declaration order, which is absence to reading.</summary>
    public static readonly IReadOnlyList<DigestOutcome> All = [.. Enum.GetValues<DigestOutcome>()];

    /// <summary>
    /// The stored form of one outcome, as the panel names it.
    ///
    /// **A switch with no fallthrough rather than a table.** These four names are not the
    /// enum's own text lower-cased, so they cannot be built from it, and the default arm
    /// throws so an outcome added to the enum arrives with its name or not at all. That is
    /// <see cref="DigestProviders"/>' rule reached by the other route.
    /// </summary>
    public static string Name(DigestOutcome outcome) => outcome switch
    {
        DigestOutcome.NoRow => "no_row",
        DigestOutcome.NoArticles => "no_articles",
        DigestOutcome.NoMaterialNews => "no_material_news",
        DigestOutcome.Prose => "prose",
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome, "This outcome has no stored name [D-134]."),
    };

    /// <summary>
    /// Which state a row is in, from the row alone.
    /// </summary>
    /// <param name="rowExists">
    /// Whether `news_digest` holds a row for the ticker and date. False is D-134's first
    /// state and the caller says which of its two causes it can see, if either.
    /// </param>
    /// <param name="digestText">
    /// The stored <c>digest_text</c>. Null is the second state, and it is a real value
    /// rather than a missing one [`CLAUDE.md` §6].
    /// </param>
    public static DigestOutcome Classify(bool rowExists, string? digestText)
    {
        if (!rowExists)
        {
            return DigestOutcome.NoRow;
        }

        if (digestText is null)
        {
            return DigestOutcome.NoArticles;
        }

        return string.Equals(digestText, NoMaterialNewsText, StringComparison.Ordinal)
            ? DigestOutcome.NoMaterialNews
            : DigestOutcome.Prose;
    }
}
