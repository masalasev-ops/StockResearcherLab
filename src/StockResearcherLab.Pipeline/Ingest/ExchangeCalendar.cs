using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Data.Eodhd;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>
/// The US trading calendar, from <c>exchange-details/US</c>.
///
/// This is C07's one provider call [A4, A10]. Recency needs the most recent
/// completed session and cannot take it from <c>price_daily</c>: comparing the
/// newest stored date against itself is circular and cannot detect a whole missing
/// session, which is half of what the check exists for.
/// </summary>
public sealed class ExchangeCalendar
{
    private readonly IReadOnlySet<DayOfWeek> _workingDays;
    private readonly IReadOnlySet<DateOnly> _holidays;

    public ExchangeCalendar(IReadOnlySet<DayOfWeek> workingDays, IReadOnlySet<DateOnly> holidays)
    {
        _workingDays = workingDays;
        _holidays = holidays;
    }

    /// <summary>
    /// The most recent date at or before <paramref name="asOf"/> that the exchange
    /// held a session on.
    ///
    /// At or before, not before: this runs after the close, so the run date's own
    /// session has completed when there was one. The half-day early closes the
    /// endpoint also reports are not consulted, because a shortened session is still
    /// a session and its row count is completeness's problem rather than recency's.
    /// </summary>
    public DateOnly MostRecentCompletedSession(DateOnly asOf)
    {
        // Bounded rather than open, so a malformed calendar fails loudly instead of
        // looping. Ten days clears any run of holidays the US market has.
        for (var back = 0; back <= 10; back++)
        {
            var d = asOf.AddDays(-back);
            if (IsSession(d))
            {
                return d;
            }
        }

        throw new InvalidOperationException(
            $"No trading session found in the ten days to {asOf:yyyy-MM-dd}. The exchange calendar is " +
            "either empty or malformed, and recency cannot be evaluated against it.");
    }

    public bool IsSession(DateOnly d) => _workingDays.Contains(d.DayOfWeek) && !_holidays.Contains(d);

    /// <summary>Fetches and parses. One call, and the only one this stage makes.</summary>
    public static async Task<ExchangeCalendar> FetchAsync(EodhdClient client, CancellationToken ct = default)
    {
        using var doc = await client.GetAsync("exchange-details/US", [], ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    public static ExchangeCalendar Parse(JsonElement root)
    {
        if (!root.TryGetProperty("TradingHours", out var hours)
            || !hours.TryGetProperty("WorkingDays", out var working)
            || working.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "exchange-details/US carries no TradingHours.WorkingDays. Recency has no calendar to " +
                "read and the guard cannot be evaluated, which is a fault rather than a pass.");
        }

        var days = new HashSet<DayOfWeek>();
        foreach (var token in working.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            days.Add(ParseDay(token.Trim()));
        }

        if (days.Count == 0)
        {
            throw new InvalidOperationException(
                "exchange-details/US reports no working days at all.");
        }

        var holidays = new HashSet<DateOnly>();
        if (root.TryGetProperty("ExchangeHolidays", out var hol) && hol.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in hol.EnumerateObject())
            {
                if (entry.Value.TryGetProperty("Date", out var d)
                    && d.ValueKind == JsonValueKind.String
                    && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    holidays.Add(parsed);
                }
            }
        }

        return new ExchangeCalendar(days, holidays);
    }

    private static DayOfWeek ParseDay(string token) => token switch
    {
        "Mon" => DayOfWeek.Monday,
        "Tue" => DayOfWeek.Tuesday,
        "Wed" => DayOfWeek.Wednesday,
        "Thu" => DayOfWeek.Thursday,
        "Fri" => DayOfWeek.Friday,
        "Sat" => DayOfWeek.Saturday,
        "Sun" => DayOfWeek.Sunday,
        _ => throw new InvalidOperationException(
            $"exchange-details/US reports a working day '{token}' that is not a three-letter English " +
            "day name. The calendar's shape has changed and recency would silently read fewer days."),
    };
}
