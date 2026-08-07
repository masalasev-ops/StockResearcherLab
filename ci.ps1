#requires -Version 5.1
<#
    ci.ps1 - the steps of .github/workflows/ci.yml, run locally, as a run.

    WHY THIS EXISTS. No hosted runner has ever picked up a job on this account.
    One run was queued and cancelled unassigned; the repository's total run count
    is 1. Phase 0 was signed off by running every CI step by hand and recording
    the seven results in PROGRESS.md. Sign-off step 1 asks that nothing be
    recorded by hand that a run can record, so this makes the local path a run
    [A6, BUILD_PLAN sign-off].

    A SELF-HOSTED RUNNER DOES NOT CLOSE IT. ci.yml is `runs-on: ubuntu-latest`
    with a `services: postgres:18` container, which needs a Linux runner with
    Docker. A Windows runner would need the workflow rewritten against a local
    database, which loses the empty-server property the two migrate steps prove.
    So ci.yml is left untouched and works the moment runners are allocated, and
    this script stands in until then.

    WHAT IT RUNS AGAINST. A git worktree at HEAD, not this working tree. That is
    what CI checks out: tracked files only, no ignored files, so no
    appsettings.Secrets.json and no bin/ or obj/. It is what makes the "no
    secrets file present" step mean something, and it is the same tracked-file
    reasoning guards.ps1 already uses on its own scan.

    THE CONSEQUENCE, STATED RATHER THAN HIDDEN: this checks HEAD, so uncommitted
    work is not tested. Commit the checkpoint, then run this. That matches the
    commit-per-checkpoint convention rather than fighting it.

    THE DATABASE. Dropped first, so the first migrate genuinely runs against an
    empty server. It is a dedicated database, not the one you develop against:
    dropping that on every CI run would be hostile, and the property being proved
    is identical either way. Override with -Database or CI_DATABASE.

    Nothing here writes to the repository and nothing is left behind. The
    worktree is removed in the finally block whether the run passes or fails.
#>

[CmdletBinding()]
param(
    # The database this run creates and drops. Never the development one.
    [string] $Database = $(if ($env:CI_DATABASE) { $env:CI_DATABASE } else { 'stockresearcherlab_ci' }),

    # Full connection string. Read from the developer's own secrets file when
    # absent, with the database name replaced by $Database. Never printed.
    [string] $ConnectionString = $env:CI_CONNECTION_STRING
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Seven results, in ci.yml's order, filled as the run goes and printed at the
# end in the same shape PROGRESS.md recorded them by hand.
$results = New-Object System.Collections.Specialized.OrderedDictionary
$stepNo = 0

function Write-Step {
    param([string] $Name)
    $script:stepNo++
    Write-Host ""
    Write-Host ("  [{0}] {1}" -f $script:stepNo, $Name) -ForegroundColor Cyan
}

function Assert-ExitZero {
    param([string] $What)
    if ($LASTEXITCODE -ne 0) {
        throw "$What exited $LASTEXITCODE."
    }
}

function Resolve-ConnectionString {
    if ($ConnectionString) { return $ConnectionString }

    # The developer's own secrets file, which sits outside the worktree by
    # construction because it is gitignored [D-55].
    $secrets = Join-Path $root 'src/StockResearcherLab.Worker/appsettings.Secrets.json'
    if (-not (Test-Path -LiteralPath $secrets)) {
        throw @"
No connection string. Pass -ConnectionString, set CI_CONNECTION_STRING, or put one in
  $secrets
CI supplies this as the ConnectionStrings__Postgres environment variable and needs no file.
"@
    }

    $cs = (Get-Content -LiteralPath $secrets -Raw | ConvertFrom-Json).ConnectionStrings.Postgres
    if (-not $cs) { throw "ConnectionStrings.Postgres is empty in $secrets." }

    # Swap the database name. The rest of the string, host, user and password,
    # is the developer's and is never echoed.
    if ($cs -match 'Database\s*=') {
        return ($cs -replace 'Database\s*=[^;]*', "Database=$Database")
    }

    return ($cs.TrimEnd(';') + ";Database=$Database")
}

function Remove-CiDatabase {
    param([string] $Cs, [string] $WorkDir)

    # Dropped through a .NET 10 file-based app rather than psql, which is not
    # installed, or Npgsql loaded into this process, which Windows PowerShell 5.1
    # cannot do because it is .NET Framework and Npgsql targets .NET 10.
    #
    # Written to a temp path outside the worktree and deleted after, so no scratch
    # source survives in the repository and the file set CI scans is untouched.
    $scriptPath = Join-Path ([System.IO.Path]::GetTempPath()) ("srl-ci-drop-" + [System.Diagnostics.Process]::GetCurrentProcess().Id + ".cs")

    $csharp = @'
#:package Npgsql@9.0.4
using Npgsql;

var cs = Environment.GetEnvironmentVariable("CI_TARGET")
    ?? throw new InvalidOperationException("CI_TARGET unset.");

var db = new NpgsqlConnectionStringBuilder(cs).Database
    ?? throw new InvalidOperationException("The connection string names no database.");

var maintenance = new NpgsqlConnectionStringBuilder(cs) { Database = "postgres" }.ConnectionString;

await using var conn = new NpgsqlConnection(maintenance);
await conn.OpenAsync();

// Sessions left open by an earlier run would make DROP DATABASE fail, and the
// failure would read as a broken script rather than a stale connection.
await using (var kill = new NpgsqlCommand(
    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
    "WHERE datname = @d AND pid <> pg_backend_pid();", conn))
{
    kill.Parameters.AddWithValue("d", db);
    await kill.ExecuteNonQueryAsync();
}

// Quoted rather than bound: DROP DATABASE takes no parameter.
await using (var drop = new NpgsqlCommand(
    $"DROP DATABASE IF EXISTS \"{db.Replace("\"", "\"\"")}\";", conn))
{
    await drop.ExecuteNonQueryAsync();
}

// Read it back. A drop that reported success and left the database standing
// would send the next step against a populated schema, and "migrate ran clean
// from empty" would be recorded off a database that was never empty.
await using (var check = new NpgsqlCommand(
    "SELECT 1 FROM pg_database WHERE datname = @d;", conn))
{
    check.Parameters.AddWithValue("d", db);
    if (await check.ExecuteScalarAsync() is not null)
    {
        Console.Error.WriteLine($"still present after DROP: {db}");
        return 1;
    }
}

Console.WriteLine($"dropped {db}, confirmed absent");
return 0;
'@

    Set-Content -LiteralPath $scriptPath -Value $csharp -Encoding utf8

    try {
        $env:CI_TARGET = $Cs
        $out = & dotnet run $scriptPath
        Assert-ExitZero 'the database drop'
        Write-Host ("      " + ($out | Select-Object -Last 1))
    }
    finally {
        Remove-Item Env:\CI_TARGET -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- the run ---

$cs = Resolve-ConnectionString
# Named from the process id rather than a new guid. Nothing here reaches output,
# so it is not an INVARIANT 6 question, but the repository's habit is that a
# fresh guid never appears where a stable name will do [CLAUDE.md section 6].
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("srl-ci-" + [System.Diagnostics.Process]::GetCurrentProcess().Id)
$head = (& git -C $root rev-parse --short HEAD)
Assert-ExitZero 'git rev-parse'

Write-Host ""
Write-Host "ci.ps1: .github/workflows/ci.yml, run locally" -ForegroundColor White
Write-Host "  HEAD      $head"
Write-Host "  database  $Database  (dropped first)"
Write-Host "  worktree  $work"

$previousConnection = $env:ConnectionStrings__Postgres

try {
    # ---- Check out. CI's actions/checkout@v4. -----------------------------
    Write-Step 'Check out'
    & git -C $root worktree add --detach --quiet $work HEAD
    Assert-ExitZero 'git worktree add'
    Write-Host "      tracked files at $head, no ignored files"

    # The configuration builder reads appsettings.Secrets.json first and
    # environment variables last, so this wins over any file, exactly as the
    # env: block in ci.yml does [D-55].
    $env:ConnectionStrings__Postgres = $cs
    $env:DOTNET_NOLOGO = 'true'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'

    # ---- Guards ----------------------------------------------------------
    # First rather than last. It needs only the checkout, it takes a second, and
    # a guard reporting after a three-minute suite arrives too late to be the
    # first thing anyone reads.
    Write-Step 'Guards'
    # 6>&1 redirects the information stream. guards.ps1 reports through
    # Write-Host, which does not reach the pipeline in Windows PowerShell, so
    # without this the run captures nothing and the check count reads 0 while
    # the output still appears on the console. That is the shape of failure this
    # repository keeps finding: a number that looks like a measurement and was
    # produced by a scan that saw nothing.
    $guards = & (Join-Path $work 'guards.ps1') 6>&1
    Assert-ExitZero 'guards.ps1'
    $guards | Select-Object -Last 1 | ForEach-Object { Write-Host "      $_" }

    # Read the count off guards.ps1's own summary line rather than counting the
    # per-check headings. Counting them meant matching the label, and the labels
    # are not all "INVARIANT n": the fifth check stands for a CLAUDE.md section
    # rather than a numbered invariant, so it was silently not counted and this
    # block reported four where guards.ps1 reported five.
    $summary = $guards | Where-Object { "$_" -match '^guards\.ps1: ok\.\s+(\d+) checks' } | Select-Object -Last 1
    if (-not $summary) {
        throw 'guards.ps1 printed no summary line. It exits 0 over an empty scan too, so a missing count here is a broken capture rather than a clean tree.'
    }

    $checks = [int]([regex]::Match("$summary", '^guards\.ps1: ok\.\s+(\d+) checks').Groups[1].Value)
    if ($checks -eq 0) {
        throw 'guards.ps1 reported zero checks.'
    }
    $results['guards.ps1'] = "exit 0, $checks checks, zero each"

    # ---- Set up .NET 10. CI's actions/setup-dotnet@v4. --------------------
    Write-Step 'Set up .NET 10'
    $sdk = & dotnet --version
    Assert-ExitZero 'dotnet --version'
    if (-not $sdk.StartsWith('10.')) {
        throw "ci.yml pins dotnet-version 10.0.x and this machine has $sdk."
    }
    Write-Host "      SDK $sdk"

    # ---- Restore ---------------------------------------------------------
    Write-Step 'Restore'
    & dotnet restore (Join-Path $work 'StockResearcherLab.slnx') | Out-Null
    Assert-ExitZero 'dotnet restore'
    $results['dotnet restore'] = 'exit 0'
    Write-Host '      ok'

    # ---- Build -----------------------------------------------------------
    # TreatWarningsAsErrors is on in Directory.Build.props, so a warning fails
    # here rather than accumulating.
    Write-Step 'Build'
    $build = & dotnet build (Join-Path $work 'StockResearcherLab.slnx') --no-restore --configuration Debug
    Assert-ExitZero 'dotnet build'
    $warn = ($build | Select-String -Pattern '^\s*(\d+) Warning\(s\)').Matches.Groups[1].Value
    $err = ($build | Select-String -Pattern '^\s*(\d+) Error\(s\)').Matches.Groups[1].Value
    $results['dotnet build --no-restore'] = "$warn warnings, $err errors"
    Write-Host "      $warn warnings, $err errors"

    # ---- Confirm no secrets file is present ------------------------------
    # The tests must pass on the environment variable alone. If a secrets file
    # were ever committed this fails before anything reads it, and the run below
    # would otherwise have proved nothing about the CI path.
    Write-Step 'Confirm no secrets file is present'
    $found = @(Get-ChildItem -LiteralPath $work -Recurse -File -Filter '*.Secrets.json' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*.Secrets.example.json' } |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
    if ($found.Count -gt 0) {
        $found | ForEach-Object { Write-Host "      $($_.FullName)" }
        throw 'A secrets file is present in the checkout.'
    }
    $results['no secrets file present'] = 'none found, as expected'
    Write-Host '      none, as expected'

    # ---- Drop the database. CI's fresh service container. -----------------
    Write-Step 'Drop the database (CI gets a fresh container; this is the local equivalent)'
    Remove-CiDatabase -Cs $cs -WorkDir $work

    # ---- Migrate, from an empty server -----------------------------------
    Write-Step 'Migrate, from an empty server'
    $m1 = & dotnet run --project (Join-Path $work 'src/StockResearcherLab.Worker') --no-build --no-launch-profile -- migrate
    Assert-ExitZero 'the first migrate'
    $m1 | ForEach-Object { Write-Host "      $_" }
    $created = @($m1 | Select-String -Pattern 'created database').Count -gt 0
    $applied = @($m1 | Select-String -Pattern '\.sql\s+applied')
    if (-not $created) {
        throw 'The first migrate did not create the database, so it did not run against an empty server.'
    }
    if ($applied.Count -eq 0) {
        throw 'The first migrate applied no migration file.'
    }
    $files = ($applied | ForEach-Object { ($_ -replace '^\s*', '') -replace '\s+applied.*$', '' }) -join ', '
    $results['migrate, from an empty server'] = "created database, $files applied"

    # ---- Migrate again, asserting idempotence ----------------------------
    # A second run must change nothing. Checked rather than trusted, because
    # "idempotent" is the kind of claim that stays true until it quietly does not.
    Write-Step 'Migrate again, asserting idempotence'
    $m2 = & dotnet run --project (Join-Path $work 'src/StockResearcherLab.Worker') --no-build --no-launch-profile -- migrate
    Assert-ExitZero 'the second migrate'
    if (@($m2 | Select-String -Pattern 'nothing to apply').Count -eq 0) {
        $m2 | ForEach-Object { Write-Host "      $_" }
        throw 'The second migrate applied something. It is not idempotent.'
    }
    $results['migrate again'] = 'nothing to apply, gate passed'
    Write-Host '      nothing to apply, gate passed'

    # ---- Test ------------------------------------------------------------
    Write-Step 'Test'
    $test = & dotnet test (Join-Path $work 'StockResearcherLab.slnx') --no-build --verbosity quiet
    Assert-ExitZero 'dotnet test'
    $summary = $test | Select-String -Pattern 'Passed!.*Passed:\s*(\d+)' | Select-Object -First 1
    $passed = if ($summary) { $summary.Matches.Groups[1].Value } else { '?' }
    $results['dotnet test --no-build'] = "Passed $passed, Failed 0"
    Write-Host "      Passed $passed, Failed 0"
}
finally {
    if ($null -eq $previousConnection) {
        Remove-Item Env:\ConnectionStrings__Postgres -ErrorAction SilentlyContinue
    }
    else {
        $env:ConnectionStrings__Postgres = $previousConnection
    }

    if (Test-Path -LiteralPath $work) {
        & git -C $root worktree remove --force $work 2>$null
        if (Test-Path -LiteralPath $work) {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    & git -C $root worktree prune 2>$null
}

# ------------------------------------------------------------- the record ---
# The same seven lines PROGRESS.md recorded by hand at phase 0 sign-off, now
# produced by a run. Paste this block rather than retyping it.

Write-Host ""
Write-Host "ci.ps1: ok. $($results.Count) results at $head" -ForegroundColor Green
Write-Host ""
foreach ($key in $results.Keys) {
    Write-Host ("  {0,-30}{1}" -f $key, $results[$key])
}
Write-Host ""
exit 0
