namespace StockResearcherLab.Core.Gates;

/// <summary>
/// Why a name was gated out of tonight's allocation. The vocabulary is closed
/// [`ARCHITECTURE.html` §03, D-117].
///
/// **The order is the declaration order and it is the order rows are written in.** A
/// name failing three reasons carries three entries in this order rather than in
/// whatever order the statement happened to evaluate, so two runs over one date write
/// byte-identical arrays and a reader comparing two nights is comparing the same thing
/// [`CLAUDE.md` §6].
/// </summary>
public enum GateReason
{
    /// <summary>
    /// Earnings inside the blackout window, which is
    /// <c>gates.earnings_blackout_days_before</c> ahead and
    /// <c>gates.earnings_blackout_days_after</c> behind.
    /// </summary>
    EarningsBlackout,

    /// <summary>An overnight move above <c>gates.gap_pct</c>, in either direction.</summary>
    Gap,

    /// <summary>The name did not trade this session.</summary>
    Halt,

    /// <summary>An open position in any portfolio on this date.</summary>
    AlreadyHeld,

    /// <summary>Exited inside the last <c>gates.cooldown_days</c>.</summary>
    Cooldown,
}

/// <summary>
/// The reason vocabulary, as the strings <c>gate_result.reasons</c> holds.
///
/// **One list, in one place, and the database holds the same one.** Migration `0018`
/// puts a CHECK on the column built from these names, so a reason outside the
/// vocabulary fails the insert rather than being stored and read back as a reason
/// nobody recognises. Enumerating it in code alone would leave the store able to hold
/// a value no reader can interpret, which is the shape this corpus keeps removing.
/// </summary>
public static class GateReasons
{
    /// <summary>Every reason, in declaration order, which is the order a row lists them in.</summary>
    public static readonly IReadOnlyList<GateReason> All =
        [.. Enum.GetValues<GateReason>()];

    /// <summary>
    /// The stored form of one reason: lower snake case, so <c>EarningsBlackout</c> is
    /// <c>earnings_blackout</c>. Built rather than tabulated, so a reason added to the
    /// enum cannot arrive without its string.
    /// </summary>
    public static string Name(GateReason reason)
    {
        var source = reason.ToString();
        var built = new System.Text.StringBuilder(source.Length + 2);

        foreach (var c in source)
        {
            if (char.IsAsciiLetterUpper(c) && built.Length > 0)
            {
                built.Append('_');
            }

            built.Append(char.ToLowerInvariant(c));
        }

        return built.ToString();
    }

    /// <summary>Every reason's stored form, in declaration order.</summary>
    public static IReadOnlyList<string> Names => [.. All.Select(Name)];

    /// <summary>
    /// A stored reason, read back.
    ///
    /// **Fails closed on anything outside the vocabulary** rather than returning a
    /// nullable or a default. A row carrying a reason no reader recognises is a row
    /// whose meaning is unknown, and treating it as one of the five would be a wrong
    /// answer that looks like a right one.
    /// </summary>
    public static GateReason Parse(string name)
    {
        foreach (var reason in All)
        {
            if (string.Equals(Name(reason), name, StringComparison.Ordinal))
            {
                return reason;
            }
        }

        throw new InvalidOperationException(
            $"'{name}' is not a gate reason. The vocabulary is closed and is " +
            string.Join(", ", Names) +
            " [ARCHITECTURE.html section 03, D-117]. A row carrying anything else was written " +
            "by something that bypassed both GateEngine and migration 0018's CHECK.");
    }
}
