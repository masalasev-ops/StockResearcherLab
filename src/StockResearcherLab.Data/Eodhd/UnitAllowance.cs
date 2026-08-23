using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core.Stages;

namespace StockResearcherLab.Data.Eodhd;

/// <summary>
/// The remaining allowance, read from `/api/user`. The only thing that decides a
/// sweep has run out.
///
/// **The weights in config are a projection and this is the verdict.** They are
/// measurements and can go stale if the provider re-prices, so a sweep uses them to
/// say whether the next unit is expected to fit and never that it did. A drift
/// between the two is recorded rather than smoothed over.
///
/// **The read costs nothing**, confirmed at 3.1 by reading it twice back to back for
/// an unchanged count of 90,518, which is what makes it safe before every unit of
/// work rather than once per sweep.
/// </summary>
public sealed class UnitAllowance : IUnitAllowance
{
    private readonly EodhdClient _client;

    public UnitAllowance(EodhdClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<AllowanceReading> ReadAsync(CancellationToken ct = default)
    {
        using var doc = await _client.GetAsync("user", [], ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    /// <summary>
    /// The three fields the gate needs, from the payload shape measured at phase P,
    /// phase 1 and 3.1.
    ///
    /// Public and static so the parse is asserted against a captured payload rather
    /// than against a live call, which is the same reason `EodhdUrl.Build` is separate
    /// from the client [A19].
    ///
    /// **Every field is required and none defaults.** A missing `apiRequestsDate`
    /// defaulted to today would make a stale reading look current, which is the exact
    /// case this type exists to catch, and a missing `dailyRateLimit` defaulted to
    /// 100,000 would hand a sweep an allowance nobody granted.
    /// </summary>
    public static AllowanceReading Parse(JsonElement root)
    {
        var used = Int(root, "apiRequests");
        var limit = Int(root, "dailyRateLimit");
        var stamped = Text(root, "apiRequestsDate");

        if (!DateOnly.TryParseExact(
                stamped, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new InvalidOperationException(
                $"'/api/user' returned apiRequestsDate '{stamped}', which is not a date in yyyy-MM-dd. " +
                "The allowance gate compares it against the provider's current day, so a value it " +
                "cannot read is a halt rather than a guess.");
        }

        return new AllowanceReading(used, limit, date);
    }

    /// <summary>
    /// A number that may arrive as a JSON number or as a string. Measured both ways
    /// across three probes: `dailyRateLimit` printed as 100000 on 2026-08-05 and as
    /// 100,000 after formatting, and the raw type is not guaranteed by any contract.
    /// </summary>
    private static int Int(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            throw new InvalidOperationException(
                $"'/api/user' returned no '{name}'. The allowance gate reads it before every unit of " +
                "work, so its absence is a shape change and a halt rather than a default.");
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"'/api/user' returned '{name}' as a JSON {element.ValueKind} this cannot read as a whole number.");
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new InvalidOperationException(
                $"'/api/user' returned no string '{name}'. The allowance gate compares it against the " +
                "provider's current day and cannot proceed without it.");
}
