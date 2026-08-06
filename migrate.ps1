#requires -Version 5.1
<#
    migrate.ps1 - apply the Postgres schema, snapshot-first.

    STUB at 0.1. Checkpoint 0.2 makes it real.

    It will be a thin wrapper over the Worker, because the connection string
    lives beside the project that needs it and not in a script [D-55]:

        dotnet run --project src/StockResearcherLab.Worker -- migrate
#>

Write-Host "migrate.ps1: stub, schema lands at 0.2 [0.1]. Exiting 0."
exit 0
