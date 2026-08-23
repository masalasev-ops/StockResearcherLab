using System.Globalization;
using Microsoft.Extensions.Configuration;
using StockResearcherLab.Core.Config;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Data.Eodhd;
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

    case "run-selection":
        return await RunNightAsync(BackfillSequence.SelectionOrder).ConfigureAwait(false);

    case "backfill":
        return await BackfillAsync().ConfigureAwait(false);

    case "stages":
        return ListStages();

    default:
        Console.WriteLine("StockResearcherLab.Worker");
        Console.WriteLine("  migrate               apply the schema, snapshot-first. Idempotent.");
        Console.WriteLine("  seed                  insert version 1 of every config key. Idempotent.");
        Console.WriteLine("  stages                list the registered components and what each writes.");
        Console.WriteLine("  run <stage> [date]    run one stage. Date defaults to today, US Eastern.");
        Console.WriteLine("  run-night [date]      run the evening sequence in order, halting on the first failure.");
        Console.WriteLine("  run-selection [date]  run the selection half of that sequence over the store as it");
        Console.WriteLine("                        stands: C12, C13, C14 and C28, and no ingest. This is the");
        Console.WriteLine("                        night a backfilled date gets, the sources having already");
        Console.WriteLine("                        filled it, and it calls no provider [4.12].");
        Console.WriteLine("  backfill <from> <to>  run every source over a range, in order, each finishing");
        Console.WriteLine("                        before the next begins. Sources already swept report");
        Console.WriteLine("                        `covered` and fall through, so re-issuing the identical");
        Console.WriteLine("                        command is how an interrupted rebuild is resumed.");
        Console.WriteLine("                        BOTH DATES ARE REQUIRED HERE, because a rebuild spans");
        Console.WriteLine("                        days and a defaulted `to` moves at midnight.");
        Console.WriteLine("  backfill <stage> [from] [to]");
        Console.WriteLine("                        run one stage over a range. From defaults to");
        Console.WriteLine("                        backfill.window_start and to defaults to today.");
        Console.WriteLine("                        PASS BOTH DATES FOR A MULTI-DAY SWEEP. C03 and C05");
        Console.WriteLine("                        stamp their attempt rows with the range end, and the");
        Console.WriteLine("                        default `to` moves at midnight, so a sweep resumed the");
        Console.WriteLine("                        next day on defaults re-sweeps its pool whole [D-99].");
        Console.WriteLine("                        Exit 0 completed, 2 halted on the allowance, 1 failed.");
        Console.WriteLine("                        A halt is the gate working: run it again, with the same");
        Console.WriteLine("                        two dates, after the provider's day rolls over.");
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

async Task<int> RunNightAsync(IReadOnlyList<string>? order = null)
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

    var label = order is null ? "run-night" : "run-selection";

    Console.WriteLine(
        $"{label}  {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}  config v{configVersion}");

    var night = NightlyRun.For(
        connectionString, config["Eodhd:ApiToken"], clock, Console.WriteLine);

    var result = await night.ExecuteAsync(date, configVersion, order).ConfigureAwait(false);

    Console.WriteLine($"  {result.Summary()}");

    // Non-zero when the night halted, so an unattended run is visible as a failure
    // rather than as a quiet short night.
    return result.Completed ? 0 : 1;
}

/// <summary>
/// A range, either one named stage or every source in order. 3.16's driver; the
/// single-stage half was pulled forward to 3.6, which needed it, and the
/// sources-in-order half lands here.
///
/// **Which form is being asked for is read off the first argument** rather than off a
/// flag. `backfill PriceIngestor 2021-01-04 2026-08-17` names a stage;
/// `backfill 2021-01-04 2026-08-17` does not, and a date is not a stage name in any
/// registry this system can have.
///
/// **This command spends real allowance** and is the only one in this file that can.
/// It is therefore deliberately explicit about what it is about to do before it does
/// it: the stage, the range, where it is resuming from, and the fact that the gate is
/// what stops it.
/// </summary>
async Task<int> BackfillAsync()
{
    // The sequence form is the no-stage-name one, so the argument positions shift by
    // one and the parse says which. A stage name that parsed as a date would be the
    // ambiguity here and there is no such name.
    var named = args.Length > 1 && !IsIsoDate(args[1]);
    var stageName = named ? args[1] : null;
    var dateArg = named ? 2 : 1;

    // **The sequence form states both dates or it does not run**, and the reason is a
    // bill rather than tidiness [D-99]. `to` defaults to today and today moves at
    // midnight. C02, C04 and C06 stamp their attempt rows with the range start and do
    // not care; **C03 and C05 stamp the range end**, because that column is also what
    // the nightly rotation orders on, so a rebuild resumed the next morning on defaults
    // presents those two with an empty attempt set and re-sweeps both pools whole. A
    // full rebuild spans days by construction, so this is the ordinary case rather than
    // an edge, and there is no single-day rebuild the refusal costs anything.
    //
    // The single-stage form keeps its defaults. It is issued for one compute stage at a
    // time as well as for a sweep, and the warning in the help text is what it has.
    if (!named && args.Length < 3)
    {
        Console.Error.WriteLine(
            "backfill over every source needs both dates: backfill <from> <to>. `to` would otherwise " +
            "default to today, and today moves at midnight; C03 and C05 stamp their attempt rows with " +
            "the range end, so a rebuild resumed the next morning on defaults would re-sweep both " +
            "pools whole [D-99]. A full rebuild spans days, so this is the ordinary case.");
        return 1;
    }

    var connectionString = RequireConnectionString();
    var clock = new SystemClock();

    var token = config["Eodhd:ApiToken"];
    if (string.IsNullOrWhiteSpace(token))
    {
        // Without a token the registry omits every provider-backed stage, so the
        // failure would otherwise arrive as "no stage named PriceIngestor", which
        // reads as a typo rather than as a missing secret.
        Console.Error.WriteLine(
            "Eodhd:ApiToken is empty or absent, so the registry holds no provider-backed stage and " +
            "there is nothing here to run over a range [D-55].");
        return 1;
    }

    // **One client for the registry and the allowance**, because the rate limiter is
    // per client and the provider's limit is not. The gate reads `/api/user` before
    // every unit of work, so two clients would run two sliding windows of 1,000 a
    // minute against one limit of 1,000 [PipelineComposition].
    var eodhd = new EodhdClient(EodhdClient.CreateHttpClient(), token, clock);

    var registry = PipelineComposition.BuildRegistry(connectionString, eodhd);
    var runLog = new RunLog(connectionString);

    var run = new BackfillRun(
        registry, runLog, clock, connectionString, new UnitAllowance(eodhd));

    // The range end first, because the window start is config and config resolves as
    // of the date being asked about rather than as of now [INVARIANT 13, D-43]. It is
    // the same date the range stages resolve their own keys against.
    var to = args.Length > dateArg + 1
        ? DateOnly.ParseExact(args[dateArg + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture)
        : clock.Today;

    var from = args.Length > dateArg
        ? DateOnly.ParseExact(args[dateArg], "yyyy-MM-dd", CultureInfo.InvariantCulture)
        : ConfigValue.Date(await new ConfigStore(connectionString)
            .RequireAsync("backfill.window_start", to).ConfigureAwait(false));

    var label = stageName ?? "all sources";

    if (from > to)
    {
        Console.Error.WriteLine(
            $"backfill {label}  from {Iso(from)} is after to {Iso(to)}. An empty range is a typo " +
            "rather than a no-op, so it is refused before anything is spent.");
        return 1;
    }

    if (stageName is null)
    {
        return await BackfillEverythingAsync(registry, run, from, to).ConfigureAwait(false);
    }

    Console.WriteLine($"backfill {stageName}  range {Iso(from)}..{Iso(to)}");

    // Stated before the run rather than inferred from the result, because a sweep
    // resuming and a sweep starting over look identical from a row count and the
    // second spends the whole range again.
    //
    // **It reports and decides nothing** [0010]. Where the run picks up is the stage's
    // attempt record, so this line cannot disagree with what then happens the way its
    // predecessor did: that one branched on whether the last row was a halt, and went
    // on saying "this starts over" after a failed row's position began resuming.
    var last = await run.LastRangeRunAsync(stageName).ConfigureAwait(false);
    Console.WriteLine(last is null
        ? "  no previous range run for this stage recorded"
        : $"  last range run {last.Status}, reached {Iso(last.LastDateCovered)}, run_log row " +
          last.RunLogId.ToString(CultureInfo.InvariantCulture) +
          ". Where this run picks up is its attempt record rather than that row [0010].");

    BackfillResult result;
    try
    {
        result = await run.RunAsync(stageName, from, to).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        // **Caught for the exit code, not to soften the failure.** `BackfillRun` has
        // already recorded the failed row and rethrown, and this command is the one
        // driven unattended across days, so an unhandled exception here surfaces as a
        // .NET crash code that says nothing about which of the three outcomes happened.
        // The whole detail including the stack still goes to stderr, because a throw is
        // a defect rather than an expected state and the operator needs it.
        Console.Error.WriteLine(ex.ToString());
        Console.Error.WriteLine(
            $"backfill {stageName} FAILED. The run is recorded as failed against its range start, " +
            "which is the only position it can prove [3.6].");
        return 1;
    }

    Console.WriteLine(
        $"  {result.Status}, {result.RowsWritten.ToString("N0", CultureInfo.InvariantCulture)} row(s), " +
        $"reached {Iso(result.LastDateCovered)}");

    if (result.Detail is not null)
    {
        Console.WriteLine($"  {result.Detail}");
    }

    // **Three outcomes, three exit codes**, because the design insists a halt and a
    // failure are different observations and a caller reading one number is where they
    // would collapse. 0 completed, 2 halted on the allowance gate, 1 threw. A halt is
    // the mechanism working: a multi-day sweep halts in the ordinary course.
    return result.WasHalted ? 2 : 0;
}

/// <summary>
/// Every source over one range, in order [3.16].
///
/// **It is the same twelve commands the single-stage form issues one at a time**, run
/// through the same <see cref="BackfillRun"/> against the same range, so a sequence run
/// and a hand-issued sweep are the same work and leave the same `run_log` rows.
///
/// **A halt is expected here rather than exceptional.** The ingest sources spend days,
/// so the ordinary course of a full rebuild is: run it, it halts at exit 2 somewhere in
/// the ingest, run the identical command again after the provider's day rolls over, and
/// the sources already finished report `covered` and fall through in seconds.
/// </summary>
async Task<int> BackfillEverythingAsync(StageRegistry registry, BackfillRun run, DateOnly from, DateOnly to)
{
    Console.WriteLine($"backfill all sources  range {Iso(from)}..{Iso(to)}");
    Console.WriteLine($"  {BackfillSequence.SourceOrder.Length} source(s), in order, each finishing before the next begins");

    // The same pre-run line the single-stage form prints, one per source. It reports and
    // decides nothing [0010]: where each source picks up is its own attempt record. What
    // it is for is seeing, before another day is spent, which sources have already run
    // and how they ended.
    foreach (var name in BackfillSequence.SourceOrder)
    {
        var last = await run.LastRangeRunAsync(name).ConfigureAwait(false);
        Console.WriteLine(last is null
            ? $"  {name,-22} no previous range run recorded"
            : $"  {name,-22} last {last.Status}, reached {Iso(last.LastDateCovered)}, run_log row " +
              last.RunLogId.ToString(CultureInfo.InvariantCulture));
    }

    var result = await new BackfillSequence(registry, run, Console.WriteLine)
        .ExecuteAsync(from, to).ConfigureAwait(false);

    Console.WriteLine($"  {result.Summary()}");

    // Printed after the summary rather than instead of it. A stopped sequence is read
    // for which source stopped it and what the ones after it did not do, and that is
    // exactly what a row count cannot say.
    foreach (var step in result.Steps.Where(s => s.Detail is not null))
    {
        Console.WriteLine($"  {step.Stage,-22} {step.Outcome}: {step.Detail}");
    }

    return result.ExitCode;
}

/// <summary>
/// Whether an argument is a date rather than a stage name, which is how the two forms
/// of `backfill` are told apart [3.16].
///
/// Invariant culture, exact, so a machine whose locale reads `04/01/2021` the other way
/// round cannot make this answer differ between two developers [`CLAUDE.md` §6].
/// </summary>
static bool IsIsoDate(string arg)
    => DateOnly.TryParseExact(arg, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
