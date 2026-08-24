using System.Globalization;
using System.Text.Json;

namespace StockResearcherLab.Core.Config;

/// <summary>
/// A resolved row's value, read as the type its key is stored as.
///
/// <c>config_rows.value</c> is <c>jsonb</c> and <see cref="ConfigRow.Value"/> is that
/// column rendered as text, so a number arrives as <c>100000</c> and a string arrives
/// as <c>"2021-01-04"</c> **with its quotes**. Every key seeded before phase 3 was a
/// number, where the quotes never appear and <c>decimal.TryParse</c> on the raw text
/// is correct. `backfill.window_start` is the first string-valued key [D-94], and a
/// parse that trimmed quotes by hand would be the place a JSON escape later arrives
/// unnoticed.
///
/// So the value is parsed as JSON rather than as text. That is the rule stated once,
/// in code, where the consumer reads it.
///
/// **The existing per-stage <c>LongAsync</c> and <c>DecimalAsync</c> helpers are left
/// alone.** They are correct for number-valued keys and rewriting eleven call sites
/// to route through here would be churn against a phase that is not about them
/// [`CLAUDE.md` §10]. New readers use this.
/// </summary>
public static class ConfigValue
{
    /// <summary>
    /// A date-valued key, stored as a JSON string in <c>yyyy-MM-dd</c>.
    ///
    /// Exact format rather than a permissive parse, and invariant culture, because a
    /// date that parses under one culture and not another is the failure
    /// <c>CultureInfo.InvariantCulture</c> is mandated against [`CLAUDE.md` §6].
    /// </summary>
    public static DateOnly Date(ConfigRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var text = String(row);

        return DateOnly.TryParseExact(
            text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new InvalidOperationException(
                $"Config key '{row.Key}' resolved to '{text}', which is not a date in yyyy-MM-dd. " +
                "A date-valued key is stored as a JSON string in that format so that its ordering " +
                "is its ordering as text and no locale reaches the parse.");
    }

    /// <summary>A string-valued key, with the JSON quoting removed by the JSON reader.</summary>
    public static string String(ConfigRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        using var doc = Parse(row);

        return doc.RootElement.ValueKind == JsonValueKind.String
            ? doc.RootElement.GetString()!
            : throw new InvalidOperationException(
                $"Config key '{row.Key}' resolved to a JSON {doc.RootElement.ValueKind}, not a string. " +
                "The stored value is what decides this, not the caller.");
    }

    /// <summary>A whole-number key, such as an allowance or an endpoint weight.</summary>
    public static long Long(ConfigRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        using var doc = Parse(row);

        return doc.RootElement.ValueKind == JsonValueKind.Number
               && doc.RootElement.TryGetInt64(out var value)
            ? value
            : throw new InvalidOperationException(
                $"Config key '{row.Key}' resolved to '{row.Value}', which is not a whole number.");
    }

    /// <summary>
    /// A key that may carry a fraction, such as a percentile threshold or a z-score
    /// bound.
    ///
    /// **A whole number is accepted here and a fraction is not accepted by
    /// <see cref="Long"/>**, which is the asymmetry the two keys need: `1.0` and `1` are
    /// the same threshold written two ways, while `2.5` articles is not a count. So a
    /// threshold seeded as `80` reads here and a count seeded as `3.5` fails there.
    /// </summary>
    public static double Double(ConfigRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        using var doc = Parse(row);

        return doc.RootElement.ValueKind == JsonValueKind.Number
               && doc.RootElement.TryGetDouble(out var value)
            ? value
            : throw new InvalidOperationException(
                $"Config key '{row.Key}' resolved to '{row.Value}', which is not a number.");
    }

    private static JsonDocument Parse(ConfigRow row)
    {
        try
        {
            return JsonDocument.Parse(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Config key '{row.Key}' holds '{row.Value}', which is not JSON. The column is " +
                "jsonb, so this means the row was written by something that bypassed the seeder.",
                ex);
        }
    }
}
