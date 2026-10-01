using StockResearcherLab.Core.Gates;
using StockResearcherLab.Core.Screens;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Pipeline.Ingest;
using StockResearcherLab.Pipeline.Select;

namespace StockResearcherLab.Tests.Corpus;

/// <summary>
/// Every statement a registered component writes through, built by the component's own
/// builder, with the component, the table and the operation [5.5.10, 5.5.8].
///
/// **One list for the two tests that read it**, the column check and the money check, so
/// a statement added to one is added to both. There is no marker in the registry saying
/// which route a stage takes, so the list is stated rather than derived, and the column
/// check's reverse fact is what keeps it complete: a declared insert or update with
/// columns that neither this list nor a bulk write covers fails there.
/// </summary>
public static class StatementCatalogue
{
    public static IEnumerable<(string Stage, string Table, WriteOperation Operation, string Sql)> All()
    {
        var date = new DateOnly(2026, 8, 7);
        const WriteOperation insert = WriteOperation.Insert;
        const WriteOperation update = WriteOperation.Update;

        yield return ("FlowEngine", "flow_daily", insert, FlowEngine.Sql);

        foreach (var source in PercentileEngine.Sources)
        {
            yield return ("PercentileEngine", source.Table, update, PercentileEngine.UpdateSql(source, date, 15));
            yield return ("PercentileEngine", "percentile_cell_daily", insert, PercentileEngine.CellSql(source, date, 15));
            yield return ("PercentileEngine", "percentile_cell_coverage", insert, PercentileEngine.CoverageSql(source.Table, date));
        }

        // The six statement writers 5.5.10 adds, and CostLedger, which the plan's count of
        // six did not name. Each statement is built by the component's own builder, or for
        // the three whose rows are assembled at run time, from the fixed head the builder
        // itself uses.
        var screen = new ScreenDefinition(
            "S2", [ScreenMetric.Ranked("adx14", MetricDirection.High)], 1, ScreenState.Live, 8);

        yield return ("ScreenEngine", "screen_score_daily", insert, ScreenEngine.ScoreSql(screen, date, 1));
        yield return ("ScreenEngine", "screen_score_daily", update, ScreenEngine.RankSql("S2", date, 0.5));
        yield return ("ScreenEngine", "screen_score_daily", update, ScreenEngine.ClearRanksSql("S2", date));
        yield return ("ScreenEngine", "screen_history", insert, ScreenEngine.HistorySql("S2", date, 0.9, 0.9, 250));

        yield return ("GateEngine", "gate_result", insert, GateEngine.Sql(
            date, new GateEngine.GateThresholds(8, 5, 2, 30), GateReasons.All.ToHashSet()));

        yield return ("CandidateAllocator", "candidate_set", insert,
            CandidateAllocator.Sql([("S2", SlotQuota.For(8))], date));
        yield return ("CandidateAllocator", "attribution", insert,
            CandidateAllocator.AttributionSql([("S2", SlotQuota.For(8))], [("X-FM", SlotQuota.For(8))], date, 1));

        yield return ("ConcentrationMonitor", "alert", insert, ConcentrationMonitor.AlertInsertHead);
        yield return ("HeadlineIngestor", "headline", insert, HeadlineIngestor.InsertHead);
        yield return ("NewsDigester", "news_digest", insert, NewsDigester.InsertHead);
        yield return ("CostLedger", "cost_ledger", insert, CostLedger.InsertSql);
    }
}