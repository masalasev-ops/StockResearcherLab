namespace StockResearcherLab.Api.Contracts;

/// <summary>
/// One stage of one run, as the viewer needs it. This is the Api's contract
/// surface: the Ui references this project for these types and reaches data only
/// by calling the endpoints that return them [CLAUDE.md section 4].
/// </summary>
public sealed record StageRun(
    long RunLogId,
    DateOnly RunDate,
    string Stage,
    string Status,
    DateTimeOffset StartedAt,
    long? DurationMs,
    long? RowsWritten,
    string? Error);
