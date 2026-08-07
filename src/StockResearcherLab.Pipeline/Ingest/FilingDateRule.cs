namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>Why a provider filing date could not be used, or that it could.</summary>
public static class FilingDateReason
{
    /// <summary>The provider sent no date. A rise here is the provider dropping the field.</summary>
    public const string Null = "null";

    /// <summary>Equal to its own period end. Populated, so nothing errors, and reading it hands you the quarter's numbers on the day the quarter closed.</summary>
    public const string Equal = "equal";

    /// <summary>Earlier than its own period end, which is impossible in fact and so is unknown rather than early.</summary>
    public const string Negative = "negative";

    /// <summary>Usable: at least one day after period end.</summary>
    public const string None = "none";
}

/// <param name="PeriodEnd">The fiscal period's own end.</param>
/// <param name="FilingDate">What the provider sent, which may be absent or impossible.</param>
public readonly record struct RawPeriod(DateOnly PeriodEnd, DateOnly? FilingDate);

/// <param name="Reason">One of <see cref="FilingDateReason"/>'s four.</param>
/// <param name="Effective">
/// The key every read filters on. Null only where the provider's date was unusable
/// and no substitution was derivable, which is a ticker with no clean gap to take a
/// widest from.
/// </param>
/// <param name="CleanGapDays">The gap in days where this period supplied one, else null.</param>
public readonly record struct ResolvedPeriod(
    DateOnly PeriodEnd, DateOnly? FilingDate, string Reason, DateOnly? Effective, int? CleanGapDays);

/// <param name="WidestCleanGapDays">The widest clean gap this ticker showed, or null if it showed none.</param>
/// <param name="CleanGapCount">How many periods supplied a clean gap.</param>
public readonly record struct TickerFilingDates(
    IReadOnlyList<ResolvedPeriod> Periods, int? WidestCleanGapDays, int CleanGapCount)
{
    public int SubstitutedCount => Periods.Count(p => !string.Equals(p.Reason, FilingDateReason.None, StringComparison.Ordinal));
}

/// <summary>
/// D-62, as a pure function over one ticker's periods.
///
/// A filing date is unknown when it is null, equal to its period end, or not at
/// least one day after it. An unknown row becomes readable at period end plus that
/// ticker's own widest clean gap.
///
/// **Per ticker rather than a universal constant.** Set to the observed maximum a
/// constant makes every well-behaved name wait seven months; set anywhere lower it
/// is early on some name, and being early is the one direction point-in-time
/// discipline promises never to go. The distribution clusters: one ticker accounted
/// for 23 of the 38 gaps above 65 days across 454 gaps.
///
/// **The widest is computed from every period in the response**, which is what M.2
/// accepts. A backfill reading a row dated three years ago therefore uses a window
/// derived partly from filing behaviour that had not happened yet. The direction is
/// what makes that acceptable: a widest gap only grows, so a window computed later
/// is at least as wide as the one computed then, and the affected rows become
/// readable later than they truly would have been rather than earlier.
/// </summary>
public static class FilingDateRule
{
    public static TickerFilingDates Resolve(IReadOnlyList<RawPeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);

        // Oldest first, so the sequence is deterministic and reads the way the
        // filing history happened. Ordering by a date rather than by response order,
        // which is not specified anywhere.
        var ordered = periods.OrderBy(p => p.PeriodEnd).ToList();

        // Pass one: classify, and collect the clean gaps. The classification must
        // happen before the widest is taken, because a gap that D-62 calls unknown
        // must not count toward the widest or toward the floor of four. Building the
        // list first and testing afterwards is one of the five probe patterns this
        // phase must not inherit.
        var classified = new List<(RawPeriod Raw, string Reason, int? Gap)>(ordered.Count);

        foreach (var p in ordered)
        {
            if (p.FilingDate is not DateOnly filed)
            {
                classified.Add((p, FilingDateReason.Null, null));
                continue;
            }

            var gap = filed.DayNumber - p.PeriodEnd.DayNumber;

            if (gap == 0)
            {
                classified.Add((p, FilingDateReason.Equal, null));
            }
            else if (gap < 1)
            {
                classified.Add((p, FilingDateReason.Negative, null));
            }
            else
            {
                classified.Add((p, FilingDateReason.None, gap));
            }
        }

        var cleanGaps = classified.Where(c => c.Gap is not null).Select(c => c.Gap!.Value).ToList();
        int? widest = cleanGaps.Count > 0 ? cleanGaps.Max() : null;

        // Pass two: the effective date.
        var resolved = new List<ResolvedPeriod>(classified.Count);

        foreach (var (raw, reason, gap) in classified)
        {
            DateOnly? effective;

            if (string.Equals(reason, FilingDateReason.None, StringComparison.Ordinal))
            {
                effective = raw.FilingDate;
            }
            else if (widest is int w)
            {
                effective = raw.PeriodEnd.AddDays(w);
            }
            else
            {
                // No clean gap anywhere, so nothing to substitute from. Null means
                // no usable filing date and none derivable, and every read filters
                // effective <= date so the row is unreadable by construction. Such
                // a ticker has fewer than the required clean gaps and the universe
                // excludes it anyway [D-62, 1.4].
                effective = null;
            }

            resolved.Add(new ResolvedPeriod(raw.PeriodEnd, raw.FilingDate, reason, effective, gap));
        }

        return new TickerFilingDates(resolved, widest, cleanGaps.Count);
    }
}
