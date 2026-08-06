using Microsoft.Extensions.Configuration;
using StockResearcherLab.Data;

// The host that runs the nightly pipeline and the backfill. At 0.2 it does one
// thing: apply the schema. migrate.ps1 is a wrapper over this command, because
// the connection string lives beside the project that needs it rather than in a
// script [D-55].

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.Secrets.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var command = args.Length > 0 ? args[0] : "help";

switch (command)
{
    case "migrate":
        return await MigrateAsync().ConfigureAwait(false);

    default:
        Console.WriteLine("StockResearcherLab.Worker");
        Console.WriteLine("  migrate    apply the schema, snapshot-first. Idempotent.");
        return 0;
}

async Task<int> MigrateAsync()
{
    var connectionString = config.GetConnectionString("Postgres");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.Error.WriteLine("ConnectionStrings:Postgres is empty or absent.");
        Console.Error.WriteLine("Expected appsettings.Secrets.json beside the binary, copied from");
        Console.Error.WriteLine("appsettings.Secrets.example.json at the repository root [D-55].");
        return 1;
    }

    Console.WriteLine("migrate");
    var migrator = new Migrator(connectionString, Console.WriteLine);
    var applied = await migrator.ApplyAsync().ConfigureAwait(false);

    Console.WriteLine(applied.Count == 0
        ? "  nothing to apply, schema already current"
        : $"  {applied.Count} migration(s) applied");
    return 0;
}
