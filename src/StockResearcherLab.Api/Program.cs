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

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

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
