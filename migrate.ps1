#requires -Version 5.1
<#
    migrate.ps1 - apply the Postgres schema, snapshot-first.

    A thin wrapper over the Worker. The connection string lives beside the
    project that needs it and never in a script or on a command line [D-55], so
    this passes no credentials and takes none.

    Idempotent. Every statement in the snapshot is IF NOT EXISTS and the ledger
    in meta.schema_migration skips a file whose hash it has already recorded, so
    a second run changes nothing and says so.
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "migrate.ps1: applying schema via StockResearcherLab.Worker"
& dotnet run --project (Join-Path $root 'src/StockResearcherLab.Worker') --no-launch-profile -- migrate
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host "migrate.ps1: FAILED with exit code $code"
    exit $code
}

Write-Host "migrate.ps1: ok"
exit 0
