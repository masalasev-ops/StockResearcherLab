using StockResearcherLab.Api;
using StockResearcherLab.Api.Contracts;
using StockResearcherLab.Data;

// Read-only query API. The single surface the Blazor client talks to, and no
// endpoint here writes anything: keeping it read-only means no UI action can
// alter a run, and the pipeline stays the only writer [D-51].
//
// It references Core and Data and never Pipeline, which is what stops an
// endpoint invoking a stage. 0.5 asserts that.

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Secrets.json", optional: true);

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings:Postgres is empty or absent.");
    Console.Error.WriteLine("Expected appsettings.Secrets.json beside the binary [D-55].");
    return 1;
}

builder.Services.AddSingleton(new RunLog(connectionString));
builder.Services.AddSingleton(new RecordInspector(connectionString));

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// C36's surface [D-109]. One name, one date, and the panels the store can answer.
//
// The inspector holds its own guarded data route rather than taking one from here, so
// the empty write set is its own statement and no composition choice can widen it.
app.MapGet("/api/record/{ticker}/{date}", async (
    RecordInspector inspector, string ticker, DateOnly date, CancellationToken ct) =>
    Results.Ok(await inspector.ReadAsync(ticker, date, ct).ConfigureAwait(false)));

// The bare run viewer's only source [0.6]. Stages, durations, row counts.
app.MapGet("/api/runs", async (RunLog runLog, int? limit, CancellationToken ct) =>
{
    var entries = await runLog.RecentAsync(limit ?? 200, ct).ConfigureAwait(false);
    var contract = entries
        .Select(e => new StageRun(
            e.RunLogId, e.RunDate, e.Stage, e.Status, e.StartedAt, e.DurationMs, e.RowsWritten, e.Error))
        .ToList();

    return Results.Ok(contract);
});

app.Run();
return 0;
