using System.Globalization;
using System.Text.Json;
using StockResearcherLab.Core.Config;

namespace StockResearcherLab.Core.Screens;

/// <summary>
/// The direction a screen reads a metric in. Percentiles are ascending always, so the
/// word says what the screen rewards rather than how anything sorts [D-113,
/// <c>METRICS.md</c> section 6.3].
/// </summary>
public enum MetricDirection
{
    /// <summary>A higher raw value is better. The stored percentile is used as it stands.</summary>
    High,

    /// <summary>A lower raw value is better. The stored percentile is used as 100 minus itself.</summary>
    Low,
}

/// <summary>
/// One entry in a screen's metric list: either a ranked percentile carrying a
/// direction and a weight, or a fixed bonus in score points applied outside the mean.
///
/// **A bonus is not a ranked metric and the two are one type on purpose.** They come
/// from one config array, so a reader that iterated the array and assumed every entry
/// was rankable would be reading a shape the config does not promise [D-113, D-114].
/// </summary>
public sealed record ScreenMetric
{
    private ScreenMetric(string metric, MetricDirection direction, double weight, double? bonusPoints)
    {
        Metric = metric;
        Direction = direction;
        Weight = weight;
        BonusPoints = bonusPoints;
    }

    /// <summary>The column this entry names, without the <c>_pctile</c> suffix.</summary>
    public string Metric { get; }

    /// <summary>Meaningless for a bonus, which is why <see cref="IsBonus"/> gates its use.</summary>
    public MetricDirection Direction { get; }

    /// <summary>Defaults to 1 when the config omits it [D-112].</summary>
    public double Weight { get; }

    /// <summary>Score points added after the weighted mean, or null for a ranked metric [D-114].</summary>
    public double? BonusPoints { get; }

    public bool IsBonus => BonusPoints is not null;

    public static ScreenMetric Ranked(string metric, MetricDirection direction, double weight = 1d)
        => new(metric, direction, weight, null);

    public static ScreenMetric Bonus(string metric, double points)
        => new(metric, MetricDirection.High, 0d, points);
}

/// <summary>
/// A screen, as it stands in config on one date. **The set of screens is discovered
/// from config and never from a list in code**, which is what <c>CLAUDE.md</c> section
/// 5 means by a screen being a row: a sixth screen is an insert, not a deployment.
/// </summary>
public sealed record ScreenDefinition(
    string ScreenId,
    IReadOnlyList<ScreenMetric> Metrics,
    int MinInputs,
    ScreenState State,
    int Slots)
{
    /// <summary>The ranked half of the metric list, which is what the weighted mean runs over.</summary>
    public IReadOnlyList<ScreenMetric> RankedMetrics
        => [.. Metrics.Where(m => !m.IsBonus)];

    /// <summary>The bonus half, applied after the mean [D-114].</summary>
    public IReadOnlyList<ScreenMetric> Bonuses
        => [.. Metrics.Where(m => m.IsBonus)];
}

/// <summary>A screen's lifecycle state [D-84, <c>SCREEN_LIFECYCLE.md</c> section 1.1].</summary>
public enum ScreenState
{
    /// <summary>Scored, and allocated slots.</summary>
    Live,

    /// <summary>Scored, writes attribution, allocated no slots [D-85].</summary>
    Shadow,

    /// <summary>Not scored.</summary>
    Retired,
}

/// <summary>
/// Reads a screen's metric list out of a config value. Separate from the record so the
/// parse can be exercised without a store.
///
/// **Every failure here fails the stage closed** rather than defaulting. An
/// unrecognised direction defaulting to <c>high</c> would invert three of S1's seven
/// inputs and produce an entirely plausible score [D-113, <c>CLAUDE.md</c> section 6].
/// </summary>
public static class ScreenMetricList
{
    public static IReadOnlyList<ScreenMetric> Parse(ConfigRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        using var doc = JsonDocument.Parse(row.Value);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Config key '{row.Key}' resolved to a JSON {doc.RootElement.ValueKind}, not an array. " +
                "A metric list is an array of objects, each carrying the metric, the direction and " +
                "the weight [D-113].");
        }

        var metrics = new List<ScreenMetric>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            metrics.Add(ParseOne(row.Key, element));
        }

        if (metrics.Count == 0)
        {
            throw new InvalidOperationException(
                $"Config key '{row.Key}' resolved to an empty metric list. A screen that ranks on " +
                "nothing scores every name identically and its floor admits two percent of the " +
                "universe at random.");
        }

        return metrics;
    }

    private static ScreenMetric ParseOne(string key, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Config key '{key}' has a metric list entry that is a JSON {element.ValueKind} " +
                "rather than an object.");
        }

        if (!element.TryGetProperty("metric", out var metricElement)
            || metricElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"Config key '{key}' has a metric list entry with no string 'metric' property.");
        }

        var metric = metricElement.GetString()!;

        if (element.TryGetProperty("kind", out var kindElement))
        {
            var kind = kindElement.GetString();

            if (!string.Equals(kind, "bonus", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Config key '{key}' entry '{metric}' carries kind '{kind}', and 'bonus' is the " +
                    "only kind there is. An unrecognised kind fails the stage rather than being " +
                    "treated as a ranked metric [D-114].");
            }

            if (!element.TryGetProperty("points", out var pointsElement)
                || pointsElement.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidOperationException(
                    $"Config key '{key}' entry '{metric}' is a bonus with no numeric 'points'. The " +
                    "magnitude of a bonus is stated in configuration and nowhere else [D-114].");
            }

            return ScreenMetric.Bonus(metric, pointsElement.GetDouble());
        }

        if (!element.TryGetProperty("direction", out var directionElement)
            || directionElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"Config key '{key}' entry '{metric}' has no string 'direction'. Percentiles are " +
                "ascending always, so a screen has to say which way it reads one [D-113].");
        }

        var direction = directionElement.GetString() switch
        {
            "high" => MetricDirection.High,
            "low" => MetricDirection.Low,
            var other => throw new InvalidOperationException(
                $"Config key '{key}' entry '{metric}' carries direction '{other}'. The vocabulary is " +
                "'high' and 'low' and an unrecognised value fails the stage closed rather than " +
                "defaulting to high, which would invert the metric and score plausibly [D-113]."),
        };

        var weight = element.TryGetProperty("weight", out var weightElement)
            ? weightElement.ValueKind == JsonValueKind.Number
                ? weightElement.GetDouble()
                : throw new InvalidOperationException(
                    $"Config key '{key}' entry '{metric}' has a non-numeric 'weight'.")
            : 1d;

        if (weight <= 0d)
        {
            throw new InvalidOperationException(
                $"Config key '{key}' entry '{metric}' has weight " +
                weight.ToString(CultureInfo.InvariantCulture) +
                ". A weight at or below zero either removes the metric from the list while leaving " +
                "it visible there, or inverts it behind the direction's back.");
        }

        return ScreenMetric.Ranked(metric, direction, weight);
    }
}
