using StockResearcherLab.Api;
using StockResearcherLab.Pipeline.Compute;
using Xunit;

namespace StockResearcherLab.Tests.Api;

/// <summary>
/// `RecordInspector.MetricSources` against `PercentileEngine.Sources`, in both
/// directions.
///
/// **This exists because the reader carries a second copy of a list, which this
/// repository normally refuses.** The reason it carries one is structural: the Api may
/// never reference Pipeline, and that reference is what stops a page invoking a stage
/// [`CLAUDE.md` §4, `ApiIsolationTests`]. Importing the real list would cost the
/// guarantee the whole read-only surface rests on, so the copy is the lesser price and
/// this is what keeps it from being paid twice.
///
/// **The test project is the one place that sees both**, which is the same position it
/// holds for the read declaration [D-109]. So the duplication is checked in exactly the
/// place the two halves meet, rather than being noticed when a metrics panel silently
/// stops showing a column C11 added.
///
/// Without this the drift is invisible in the worst way: a metric added to C11 and not
/// here simply does not appear on the page, and a panel that shows twenty-nine of thirty
/// metrics looks exactly like a panel that shows all of them.
/// </summary>
public sealed class MetricSourceParityTests
{
    [Fact]
    public void TheReaderAndTheEngineNameTheSameSourceTables()
    {
        Assert.Equal(
            PercentileEngine.Sources.Select(s => s.Table),
            RecordInspector.MetricSources.Select(s => s.Table));
    }

    [Fact]
    public void TheReaderAndTheEngineNameTheSameMetricsInTheSameOrder()
    {
        foreach (var engine in PercentileEngine.Sources)
        {
            var reader = RecordInspector.MetricSources.Single(s => s.Table == engine.Table);

            Assert.Equal(engine.Metrics, reader.Metrics);
        }
    }

    /// <summary>
    /// Thirty, stated so the pair above cannot agree over a set that shrank on both
    /// sides at once. That is not hypothetical here: the two lists are edited by hand and
    /// the likeliest way to make them agree is to delete from whichever one failed.
    /// </summary>
    [Fact]
    public void BothCarryThirtyRankedMetrics()
    {
        Assert.Equal(30, PercentileEngine.Sources.Sum(s => s.Metrics.Count));
        Assert.Equal(30, RecordInspector.MetricSources.Sum(s => s.Metrics.Length));
    }

    /// <summary>
    /// Every metric name is distinct across the four sources, which is what makes
    /// `metric` sufficient in `percentile_cell_daily`'s key without the table beside it
    /// [D-107]. A collision would be a failed key rather than a silent overwrite, and
    /// this is where it is caught before the migration meets it.
    /// </summary>
    [Fact]
    public void EveryMetricNameIsDistinctAcrossTheFourSources()
    {
        var all = PercentileEngine.Sources.SelectMany(s => s.Metrics).ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }
}
