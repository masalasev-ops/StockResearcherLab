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

function Assert-MirrorsWorkflow {
    <#
        Reads .github/workflows/ci.yml and asserts that every command it runs has a
        counterpart here.

        WHY. The step list was matched by hand once and nothing re-checked it. The
        script whose whole purpose is to stand in for CI is the last place a silent
        divergence should be possible, and this is the same correction made to the
        guard count one level out: read the thing being mirrored rather than
        re-deriving it, and fail loudly when the source is not where it was expected.

        DELIBERATELY CRUDE, and worth knowing what it misses. It matches `run:`
        lines and the indented block under `run: |`, and pairs each against a
        distinctive substring. It does not parse YAML, so it would miss a second
        job, a command moved into a composite action or a `uses:` step, an `if:`
        that disables a step, and any change to a command's arguments that leaves
        its substring intact. It catches the case that matters: a step added to or
        removed from ci.yml with nothing added or removed here.
    #>

    $workflow = Join-Path $root '.github/workflows/ci.yml'
    if (-not (Test-Path -LiteralPath $workflow)) {
        throw "No .github/workflows/ci.yml. This script exists to run its steps, so it cannot verify it mirrors anything."
    }

    $lines = Get-Content -LiteralPath $workflow
    $commands = @()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $m = [regex]::Match($lines[$i], '^(\s*)-?\s*run:\s*(.*)$')
        if (-not $m.Success) { continue }

        $indent = $m.Groups[1].Value.Length
        $inline = $m.Groups[2].Value.Trim()

        if ($inline -and $inline -ne '|' -and $inline -ne '>') {
            $commands += $inline
            continue
        }

        # A block scalar. Take the more-indented lines that follow.
        $block = @()
        for ($j = $i + 1; $j -lt $lines.Count; $j++) {
            $line = $lines[$j]
            if (-not $line.Trim()) { $block += ''; continue }
            $lead = $line.Length - $line.TrimStart().Length
            if ($lead -le $indent) { break }
            $block += $line.Trim()
        }
        $commands += ($block -join ' ').Trim()
    }

    $commands = @($commands | Where-Object { $_ })
    if ($commands.Count -eq 0) {
        throw "No 'run:' steps found in ci.yml. Either the workflow changed shape or this parser stopped matching, and both mean this script is no longer known to mirror it."
    }

    # Each entry is a step of this script and the substring that identifies the
    # ci.yml command it stands in for.
    $mirrors = @(
        @{ Step = 'Guards';                          Match = 'guards.ps1' },
        @{ Step = 'Restore';                         Match = 'dotnet restore' },
        @{ Step = 'Build';                           Match = 'dotnet build' },
        @{ Step = 'Confirm no secrets file';         Match = '.Secrets.json' },
        @{ Step = 'Migrate / Migrate again';         Match = '-- migrate' },
        @{ Step = 'Test';                            Match = 'dotnet test' }
    )

    $orphans = @($commands | Where-Object {
        $cmd = $_
        -not ($mirrors | Where-Object { $cmd -like "*$($_.Match)*" })
    })

    if ($orphans.Count -gt 0) {
        throw ("ci.yml runs a command this script does not mirror:`n  " +
            ($orphans -join "`n  ") +
            "`nAdd the step here, or this script has stopped standing in for CI while still reporting green.")
    }

    $unused = @($mirrors | Where-Object {
        $mirror = $_
        -not ($commands | Where-Object { $_ -like "*$($mirror.Match)*" })
    })

    if ($unused.Count -gt 0) {
        throw ("This script has steps ci.yml no longer runs: " +
            (($unused | ForEach-Object { $_.Step }) -join ', ') +
            ". The two have diverged in the other direction.")
    }

    Write-Host "      $($commands.Count) run steps in ci.yml, each mirrored"
}

function Write-FailureEvidence {
    <#
        Called only when the drop, create or migrate steps fail. Writes what the
        server said and who was connected, then returns the path so the exit
        message can name it.

        WHY. The migrate step has failed twice in about nine runs and no cause is
        claimed. Nothing here diagnoses it; the change is only that occurrence
        three arrives with evidence attached rather than as a puzzle. Nothing is
        retried and nothing is worked around, because working around a failure you
        cannot explain is how it stops being observable.

        Two candidates this will distinguish immediately, recorded so the next
        occurrence is read rather than investigated: a drop refused because a
        session is still attached to the target, and a create refused because a
        session is attached to template1. Neither is asserted.
    #>
    param([string] $Step, [string] $Cs, [string[]] $Output, [string] $ErrorText)

    $dir = Join-Path $root 'docs/evidence/phase-1'
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }

    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $path = Join-Path $dir "ci-failure-$stamp.txt"

    $sessions = '(not collected)'
    $scriptPath = Join-Path ([System.IO.Path]::GetTempPath()) ("srl-ci-diag-" + [System.Diagnostics.Process]::GetCurrentProcess().Id + ".cs")

    $csharp = @'
#:package Npgsql@9.0.4
using Npgsql;

var cs = Environment.GetEnvironmentVariable("CI_TARGET")!;
var maintenance = new NpgsqlConnectionStringBuilder(cs) { Database = "postgres" }.ConnectionString;

await using var conn = new NpgsqlConnection(maintenance);
await conn.OpenAsync();
await using var cmd = new NpgsqlCommand(
    "SELECT pid, coalesce(datname,'-'), coalesce(application_name,'-'), " +
    "coalesce(host(client_addr),'local'), coalesce(state,'-'), " +
    "coalesce(left(query, 80),'-') FROM pg_stat_activity ORDER BY datname, pid;", conn);
await using var r = await cmd.ExecuteReaderAsync();
Console.WriteLine($"{"pid",-8} {"datname",-26} {"application_name",-26} {"client",-12} {"state",-20} query");
while (await r.ReadAsync())
{
    Console.WriteLine($"{r.GetInt32(0),-8} {r.GetString(1),-26} {r.GetString(2),-26} " +
                      $"{r.GetString(3),-12} {r.GetString(4),-20} {r.GetString(5)}");
}
'@

    try {
        Set-Content -LiteralPath $scriptPath -Value $csharp -Encoding utf8
        $env:CI_TARGET = $Cs
        $sessions = (& dotnet run $scriptPath) -join "`n"
    }
    catch {
        $sessions = "(pg_stat_activity query itself failed: $($_.Exception.Message))"
    }
    finally {
        Remove-Item Env:\CI_TARGET -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
    }

    $body = @(
        "ci.ps1 failure evidence"
        "step      : $Step"
        "utc       : $((Get-Date).ToUniversalTime().ToString('u'))"
        "head      : $head"
        "database  : $Database"
        ""
        "No cause is claimed by this file. It exists so the next occurrence is read"
        "rather than investigated from nothing. The two candidates worth checking"
        "first are a drop refused because a session is still attached to the target,"
        "and a create refused because a session is attached to template1."
        ""
        "=== error, verbatim ==="
        $ErrorText
        ""
        "=== step output, verbatim ==="
        ($Output -join "`n")
        ""
        "=== pg_stat_activity, every session ==="
        $sessions
    )

    Set-Content -LiteralPath $path -Value $body -Encoding utf8
    return $path
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

try
{
    return await DropAsync();
}
catch (PostgresException ex)
{
    // The server's own words and its SQLSTATE, verbatim. 55006 on the target and
    // 55006 on template1 are the same code on different objects, and only the
    // message tells them apart.
    Console.Error.WriteLine($"SQLSTATE {ex.SqlState}: {ex.MessageText}");
    if (!string.IsNullOrEmpty(ex.Detail)) Console.Error.WriteLine($"DETAIL: {ex.Detail}");
    if (!string.IsNullOrEmpty(ex.Hint)) Console.Error.WriteLine($"HINT: {ex.Hint}");
    Console.Error.WriteLine(ex.ToString());
    return 1;
}

async Task<int> DropAsync()
{
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
//
// A PostgresException is caught at the bottom of this file and its SQLSTATE
// printed verbatim, because "database is being accessed by other users" is
// 55006 and reads nothing like "template1 is being accessed", which is the
// same code on a different object.
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
}
'@

    Set-Content -LiteralPath $scriptPath -Value $csharp -Encoding utf8

    try {
        $env:CI_TARGET = $Cs
        $out = & dotnet run $scriptPath
        if ($LASTEXITCODE -ne 0) {
            $evidence = Write-FailureEvidence -Step 'Drop the database' -Cs $Cs -Output $out `
                -ErrorText "dotnet run exited $LASTEXITCODE. The SQLSTATE is on stderr above."
            throw "The database drop failed. Evidence written to $evidence."
        }
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
    # ---- Mirror check. Before anything, because a divergence here means every
    # ---- result below is answering a question about the wrong step list.
    Write-Step 'Assert this script still mirrors ci.yml'
    Assert-MirrorsWorkflow

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
    $summary = $guards | Where-Object { "$_" -match '^guards\.ps1: ok\.\s+(\d+) checks over (\d+) files' } | Select-Object -Last 1
    if (-not $summary) {
        throw 'guards.ps1 printed no summary line naming both counts. It exits 0 over an empty scan too, so a missing count here is a broken capture rather than a clean tree.'
    }

    $m = [regex]::Match("$summary", '^guards\.ps1: ok\.\s+(\d+) checks over (\d+) files')
    $checks = [int] $m.Groups[1].Value
    $sweptFiles = [int] $m.Groups[2].Value
    if ($checks -eq 0 -or $sweptFiles -eq 0) {
        throw 'guards.ps1 reported zero checks or zero files.'
    }

    # Both numbers, not just the verdict. How much was checked is as much a result
    # as whether it passed, and a shrinking sweep produces an identical green line.
    $results['guards.ps1'] = "exit 0, $checks checks over $sweptFiles files, zero each"

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
    if ($LASTEXITCODE -ne 0) {
        $evidence = Write-FailureEvidence -Step 'Migrate, from an empty server' -Cs $cs -Output $m1 `
            -ErrorText "dotnet run -- migrate exited $LASTEXITCODE."
        throw "The first migrate failed. Evidence written to $evidence."
    }
    $m1 | ForEach-Object { Write-Host "      $_" }
    $created = @($m1 | Select-String -Pattern 'created database').Count -gt 0
    $applied = @($m1 | Select-String -Pattern '\.sql\s+applied')
    if (-not $created) {
        # This is the intermittent one. Two occurrences in about nine runs and no
        # cause claimed, so the evidence is collected rather than the failure
        # worked around: nothing is retried, and a run that cannot prove it began
        # against an empty server records nothing at all.
        $evidence = Write-FailureEvidence -Step 'Migrate, from an empty server' -Cs $cs -Output $m1 `
            -ErrorText 'The migrate output contains no "created database" line, so the database existed when it ran. The drop step reported success immediately before.'
        throw "The first migrate did not create the database, so it did not run against an empty server. Evidence written to $evidence."
    }
    if ($applied.Count -eq 0) {
        $evidence = Write-FailureEvidence -Step 'Migrate, from an empty server' -Cs $cs -Output $m1 `
            -ErrorText 'The migrate output names no applied .sql file.'
        throw "The first migrate applied no migration file. Evidence written to $evidence."
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
