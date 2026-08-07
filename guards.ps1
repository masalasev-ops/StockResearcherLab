#requires -Version 5.1
<#
    guards.ps1 - the CI greps for the invariants that are grep-checkable.

    Four checks over src/. Each states its pattern beside its count, each
    expects zero, and any hit fails the run. A zero expectation is
    self-validating: a wrong pattern that finds nothing and a right pattern that
    finds nothing are indistinguishable from the number alone, so the file set
    is asserted non-empty first and the pattern is printed for reading.

    Nothing is checked here that cannot currently return zero. A guard that
    ships red is a guard everyone learns to ignore [O.1].

    THE FILE SET is the git-tracked files under src/. That excludes bin/, obj/
    and the untracked appsettings.Secrets.json files by construction, and it is
    exactly what CI checks out. A file that exists and is not tracked is not
    scanned, which is the one way this set can be short, and it cannot happen on
    a CI checkout.

    COMMENTS ARE STRIPPED before matching. // to end of line, over every
    extension scanned. -- to end of line, over .sql ONLY. Two of the four
    patterns otherwise match the prose that states the invariant, in
    0001_snapshot.sql and in a test comment, and a comment naming a rule is not
    a breach of it. The cost is one blind spot: a breach to the right of a //
    inside a string literal on the same line would be missed.

    THE -- STRIP IS SCOPED TO .sql because in C# that sequence is the decrement
    operator [pass Q, Q.4]. Applied to .cs it deletes the rest of the line, so a
    real breach to the right of `i--` is never matched and the check reports
    zero, which reads as a pass. That is the failure class this file exists to
    catch, arriving through the file written to catch it. It was inert only
    because no C# in the tree decremented anything, and phase 1 writes the first
    paging loops.

    PATTERNS ARE WHITESPACE-TOLERANT and matched over the whole file rather than
    line by line, so a token broken across a line still matches [CLAUDE.md
    section 7].
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Each check names the invariant it stands for, the pattern it runs, the
# extensions it runs over, and any path it excludes with the reason.
$checks = @(
    @{
        Invariant  = 'INVARIANT 11'
        What       = 'no ambient clock: DateTime and DateTimeOffset are read only in the clock implementation'
        Pattern    = 'DateTime(Offset)?\s*\.\s*(Utc)?Now'
        Extensions = @('.cs', '.razor')
        Exclude    = @('src/StockResearcherLab.Data/SystemClock.cs')
        Why        = 'SystemClock.cs is the implementation and is the one place this is allowed'
    },
    @{
        Invariant  = 'INVARIANT 16'
        What       = 'money is decimal: no float or double in any monetary path'
        Pattern    = '\b(float|double)\b'
        Extensions = @('.cs', '.razor', '.sql')
        Exclude    = @()
        Why        = ''
    },
    @{
        Invariant  = 'INVARIANT 6'
        What       = 'the prefix is byte-identical within a night: no Guid.NewGuid'
        Pattern    = 'Guid\s*\.\s*NewGuid'
        Extensions = @('.cs', '.razor')
        Exclude    = @('src/StockResearcherLab.Tests/')
        Why        = 'CLAUDE.md section 6 bans it in anything affecting output or ordering, and the three uses in tests name a run_log row so repeat runs against one database do not collide. Tightens to dossier-reachable code when C15 exists'
    },
    @{
        Invariant  = 'INVARIANT 6'
        What       = 'the prefix is byte-identical within a night: Random is seeded from the run date'
        Pattern    = 'new\s+Random\s*\(\s*\)'
        Extensions = @('.cs', '.razor')
        Exclude    = @()
        Why        = ''
    }
)

function Get-TrackedSourceFiles {
    $listed = & git -C $root ls-files src/
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files failed. This script scans the tracked file set and has nothing to scan without it."
    }

    return @($listed | Where-Object { $_ -and $_.Trim().Length -gt 0 })
}

function Remove-Comments {
    param([string] $Text, [string] $Extension)

    # // to end of line, over every extension scanned.
    $stripped = [regex]::Replace($Text, '//[^\r\n]*', '')

    # -- to end of line, over .sql only. Branched rather than applied to
    # everything, because in C# -- is the decrement operator and stripping it
    # there blinds every check to the rest of the line [pass Q, Q.4].
    if ($Extension -eq '.sql') {
        $stripped = [regex]::Replace($stripped, '--[^\r\n]*', '')
    }

    return $stripped
}

$files = Get-TrackedSourceFiles
if ($files.Count -eq 0) {
    Write-Host "guards.ps1: FAIL. The tracked file set under src/ is empty, so every check below"
    Write-Host "            would pass over nothing. That is a broken scan, not a clean tree."
    exit 1
}

Write-Host "guards.ps1: $($files.Count) tracked files under src/"
Write-Host ""

$failed = 0

foreach ($check in $checks) {
    $scope = @($files | Where-Object {
        $path = $_
        $ext = [System.IO.Path]::GetExtension($path)
        if ($check.Extensions -notcontains $ext) { return $false }
        foreach ($skip in $check.Exclude) {
            if ($path.StartsWith($skip, [System.StringComparison]::Ordinal)) { return $false }
        }
        return $true
    })

    $hits = @()
    foreach ($path in $scope) {
        $full = Join-Path $root $path
        if (-not (Test-Path -LiteralPath $full)) { continue }

        $text = Remove-Comments `
            -Text (Get-Content -LiteralPath $full -Raw) `
            -Extension ([System.IO.Path]::GetExtension($path))
        if ($null -eq $text) { continue }

        foreach ($m in [regex]::Matches($text, $check.Pattern)) {
            $line = ($text.Substring(0, $m.Index) -split "`n").Count
            $hits += "      $path`:$line  $($m.Value)"
        }
    }

    $verdict = 'ok'
    if ($hits.Count -gt 0) { $verdict = 'FAIL'; $failed++ }

    Write-Host "  $($check.Invariant)  $verdict"
    Write-Host "    $($check.What)"
    Write-Host "    pattern : $($check.Pattern)"
    Write-Host "    scope   : $($scope.Count) files, $($check.Extensions -join ' ')$(if ($check.Exclude.Count) { ', excluding ' + ($check.Exclude -join ' ') })"
    if ($check.Why) { Write-Host "    why     : $($check.Why)" }
    Write-Host "    expected: 0    found: $($hits.Count)"
    foreach ($hit in $hits) { Write-Host $hit }
    Write-Host ""
}

if ($failed -gt 0) {
    Write-Host "guards.ps1: FAIL. $failed of $($checks.Count) checks found what they expect none of."
    exit 1
}

Write-Host "guards.ps1: ok. $($checks.Count) checks, every one expecting zero and finding zero."
exit 0
