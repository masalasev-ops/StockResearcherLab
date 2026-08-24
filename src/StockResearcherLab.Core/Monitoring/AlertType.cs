namespace StockResearcherLab.Core.Monitoring;

/// <summary>
/// What an `alert` row is about. The vocabulary is closed [D-126].
///
/// **Two, because `ARCHITECTURE.html` §03 gives `alert` one writer and §18 gives that
/// writer two conditions.** Megacap share above a third over 20 days, and distinct
/// tickers over 60 days below 250. §18's table names four further conditions whose row
/// reads "Alert" and whose owner is C07, C03, C13 or C26; none of those components
/// declares a write to `alert` and no document says how their alerts are recorded, so
/// they are deliberately absent rather than guessed at. A phase that gives one of them a
/// writer adds its name here and extends migration `0021` in the same checkpoint.
/// </summary>
public enum AlertType
{
    /// <summary>
    /// The candidate set's large-bucket share over the last
    /// <see cref="AlertTypes.MegacapWindowDates"/> candidate dates, against
    /// <c>monitor.megacap_share_max</c>.
    /// </summary>
    MegacapShare,

    /// <summary>
    /// Distinct tickers over the last <see cref="AlertTypes.DistinctWindowDates"/>
    /// candidate dates, against <c>monitor.distinct_tickers_60d_min</c>.
    /// </summary>
    DistinctTickers60d,
}

/// <summary>
/// The alert vocabulary, as the strings <c>alert.alert_type</c> holds.
///
/// **One list, in one place, and the database holds the same one.** Migration `0021`
/// puts a CHECK on the column carrying these names, so a type outside the vocabulary
/// fails the insert rather than being stored and read back as something no reader
/// recognises. This is `GateReasons`' shape and it is deliberately the same shape: two
/// vocabularies closed two different ways is how one of them stops being closed.
/// </summary>
public static class AlertTypes
{
    /// <summary>Every type, in declaration order.</summary>
    public static readonly IReadOnlyList<AlertType> All = [.. Enum.GetValues<AlertType>()];

    /// <summary>
    /// **The two windows are authored and are not keys** [`ARCHITECTURE.html` §18].
    /// That table states the megacap condition as "over 20 days" and the distinct-ticker
    /// condition as "over 60 days", so both numbers come from the document.
    ///
    /// `monitor.distinct_tickers_60d_min` carries its window inside its own name, and a
    /// separate `monitor.distinct_tickers_window` would be one fact in two places that
    /// can disagree. The megacap key does not carry its window, which is an asymmetry in
    /// the naming rather than a gap in the design.
    ///
    /// **Candidate dates and not calendar days, which is the one part §18 does not
    /// settle.** Candidates exist per session, so a window of sessions is what the
    /// guarantee is about, and a calendar span over a holiday week reaches back a
    /// different number of nights. §18 says "days" for both conditions and the choice
    /// between the two readings is the build's. It is the distinction the trailing floor
    /// window got wrong at 4.5, which is why it is stated rather than assumed [D-126].
    /// </summary>
    public const int MegacapWindowDates = 20;

    /// <summary>The distinct-ticker window, in candidate dates. See above.</summary>
    public const int DistinctWindowDates = 60;

    /// <summary>
    /// The stored form of one type: lower snake case, so <c>MegacapShare</c> is
    /// <c>megacap_share</c> and <c>DistinctTickers60d</c> is
    /// <c>distinct_tickers_60d</c>. Built rather than tabulated, so a type added to the
    /// enum cannot arrive without its string.
    ///
    /// **A run of digits opens a segment and does not close one**, which is the rule
    /// `GateReasons` never needed because no reason has a number in it. So
    /// <c>Tickers60d</c> is <c>tickers_60d</c>: the break falls before the <c>6</c>
    /// because a letter precedes it, the <c>0</c> continues the run, and the trailing
    /// <c>d</c> follows its digits rather than starting a segment of its own. Without
    /// the digit clause the same builder yields <c>tickers60d</c>, which is a plausible
    /// string that no constraint would hold and no key would match.
    /// </summary>
    public static string Name(AlertType type)
    {
        var source = type.ToString();
        var built = new System.Text.StringBuilder(source.Length + 2);

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];

            var opensAWord = char.IsAsciiLetterUpper(c)
                             || (char.IsAsciiDigit(c) && i > 0 && char.IsAsciiLetter(source[i - 1]));

            if (opensAWord && built.Length > 0)
            {
                built.Append('_');
            }

            built.Append(char.ToLowerInvariant(c));
        }

        return built.ToString();
    }

    /// <summary>Every type's stored form, in declaration order.</summary>
    public static IReadOnlyList<string> Names => [.. All.Select(Name)];

    /// <summary>
    /// Reading a stored type back. **Fails closed**: a string outside the vocabulary
    /// throws rather than resolving to a default, because a default here would turn an
    /// unrecognised alert into a recognised one and the row would read as ordinary.
    /// </summary>
    public static AlertType Parse(string name)
    {
        foreach (var type in All)
        {
            if (string.Equals(Name(type), name, StringComparison.Ordinal))
            {
                return type;
            }
        }

        throw new InvalidOperationException(
            "'" + name + "' is not an alert type. The vocabulary is closed to " +
            string.Join(", ", Names) + " and migration `0021` holds the same list on the " +
            "column, so a row carrying this could not have been written by this system [D-126].");
    }
}
