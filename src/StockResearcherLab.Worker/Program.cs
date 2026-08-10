using System.Globalization;
using Microsoft.Extensions.Configuration;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline;

// The host that runs the nightly pipeline and the backfill. At phase 0 it does
// two things: apply the schema, and run one stage. migrate.ps1 is a wrapper over
// the first, because the connection string lives beside the project that needs
// it rather than in a script [D-55].

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

    case "seed":
        return await SeedAsync().ConfigureAwait(false);

    case "run":
        return await RunStageAsync().ConfigureAwait(false);

    case "run-night":
        return await RunNightAsync().ConfigureAwait(false);

    case "stages":
        return ListStages();

    default:
        Console.WriteLine("StockResearcherLab.Worker");
        Console.WriteLine("  migrate               apply the schema, snapshot-first. Idempotent.");
        Console.WriteLine("  seed                  insert version 1 of every config key. Idempotent.");
        Console.WriteLine("  stages                list the registered components and what each writes.");
        Console.WriteLine("  run <stage> [date]    run one stage. Date defaults to today, US Eastern.");
        Console.WriteLine("  run-night [date]      run the evening sequence in order, halting on the first failure.");
        return 0;
}

string RequireConnectionString()
    => config.GetConnectionString("Postgres") is { Length: > 0 } cs
        ? cs
        : throw new InvalidOperationException(
            "ConnectionStrings:Postgres is empty or absent. Expected appsettings.Secrets.json beside " +
            "the binary, copied from appsettings.Secrets.example.json at the repository root [D-55].");

async Task<int> MigrateAsync()
{
    var connectionString = RequireConnectionString();

    // The database is named before anything is applied [1.8]. ci.ps1 drops a
    // database, confirms it absent, and then asserts that this command created
    // it; twice that assertion has failed with every migration reported as
    // already applied, which can only happen against a database that was never
    // dropped. Nothing in the output said which one it was, so two occurrences
    // produced two evidence files and no answer. It says now.
    var database = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Database;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"migrate  database {database}"));

    var applied = await new Migrator(connectionString, new SystemClock(), Console.WriteLine)
        .ApplyAsync().ConfigureAwait(false);

    Console.WriteLine(applied.Count == 0
        ? "  nothing to apply, schema already current"
        : $"  {applied.Count} migration(s) applied");
    return 0;
}

async Task<int> RunNightAsync()
{
    var clock = new SystemClock();
    var date = args.Length > 1
        ? DateOnly.ParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture)
        : clock.Today;

    var connectionString = RequireConnectionString();

    // Resolved as of the date being run, never assumed [checkpoint 1.13, D-43,
    // INVARIANT 13]. It was the literal 1 until this pass, which would have
    // stamped every attribution row with version 1 whatever the tuner had done,
    // and the tuner cannot segment history it cannot tell apart [CLAUDE.md
    // section 8]. Resolved here rather than inside a stage, so a stage cannot
    // resolve against a date other than the one it was handed.
    var configVersion = await new ConfigStore(connectionString)
        .RequireVersionAsync(date).ConfigureAwait(false);

    Console.WriteLine($"run-night  {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}  config v{configVersion}");

    var night = NightlyRun.For(
        connectionString, config["Eodhd:ApiToken"], clock, Console.WriteLine);

    var result = await night.ExecuteAsync(date, configVersion).ConfigureAwait(false);

    Console.WriteLine($"  {result.Summary()}");

    // Non-zero when the night halted, so an unattended run is visible as a failure
    // rather than as a quiet short night.
    return result.Completed ? 0 : 1;
}

async Task<int> SeedAsync()
{
    Console.WriteLine("seed");
    var seeder = new ConfigSeeder(RequireConnectionString());
    var inserted = await seeder.SeedAsync().ConfigureAwait(false);

    Console.WriteLine(ConfigSeeder.Describe(inserted, ConfigSeeder.Keys.Count));
    Console.WriteLine($"  {ConfigSeeder.Keys.Count} keys");
    return 0;
}

int ListStages()
{
    var registry = PipelineComposition.BuildRegistry(RequireConnectionString(), config["Eodhd:ApiToken"], new SystemClock());
    Console.WriteLine("registered components");

    foreach (var owner in registry.Owners)
    {
        var runnable = owner is StockResearcherLab.Core.Stages.IStage ? "stage" : "writer";
        var writes = owner.WriteSet.Count == 0
            ? "(writes nothing)"
            : string.Join(", ", owner.WriteSet.Select(w => w.ToString()));
        Console.WriteLine($"  {owner.Name,-18} {runnable,-7} {writes}");
    }

    return 0;
}

async Task<int> RunStageAsync()
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("run needs a stage name. 'stages' lists them.");
        return 1;
    }

    var connectionString = RequireConnectionString();
    var clock = new SystemClock();
    var registry = PipelineComposition.BuildRegistry(
        connectionString, config["Eodhd:ApiToken"], clock);
    var runLog = new RunLog(connectionString);
    var runner = new StageRunner(registry, runLog, clock, connectionString);

    var date = args.Length > 2
        ? DateOnly.ParseExact(args[2], "yyyy-MM-dd", CultureInfo.InvariantCulture)
        : clock.Today;

    // As in run-night: resolved as of the date, never a literal [checkpoint 1.13].
    var configVersion = await new ConfigStore(connectionString)
        .RequireVersionAsync(date).ConfigureAwait(false);

    Console.WriteLine($"run {args[1]}  date {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}  config v{configVersion}");

    var result = await runner.RunAsync(args[1], date, configVersion).ConfigureAwait(false);

    Console.WriteLine($"  ok, {result.RowsWritten.ToString("N0", CultureInfo.InvariantCulture)} row(s) written");
    return 0;
}
