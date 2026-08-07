#requires -Version 5.1
<#
    seed.ps1 - insert the configuration rows the pipeline reads.

    ~~STUB at 0.1.~~ Wired at 1.13. A wrapper over the Worker's seed command, for
    the same reason migrate.ps1 wraps its migrate command: the connection string
    lives beside the project that needs it rather than in a script [D-55].

    WHAT IT SEEDS. Version 1 of every key in CONFIG_REFERENCE.md that phase 1
    consumes, fourteen of them. The portfolio registry from D-36 and the digest
    provider chain from D-25 are still to come, in the phases that build them.

    THE set_at CONVENTION, which is the part worth reading [A9]. Seeded rows are
    stamped at or before the earliest date the system will ever resolve config
    for, which is the start of the five-year backfill window rather than the
    moment this script ran. Config resolves as of the simulated date and never as
    of now [D-43, INVARIANT 13], so a wall-clock stamp would put every backfill
    date before every row: every historical resolution would return nothing while
    today's looked correct. The instant is a fixed literal in ConfigSeeder, not a
    computation, because two runs must produce identical rows.

    IDEMPOTENT. Version 1 inserts ON CONFLICT DO NOTHING, so a second run changes
    nothing and says so. A real change arrives as version + 1 through the ordinary
    append-only path; a seeder that overwrote version 1 would rewrite history
    rather than extend it.
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& dotnet run --project (Join-Path $root 'src/StockResearcherLab.Worker') --no-launch-profile -- seed
exit $LASTEXITCODE
