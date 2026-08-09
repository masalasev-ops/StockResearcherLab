#requires -Version 5.1
<#
    guards.ps1 - the CI greps for the invariants that are grep-checkable.

    Five checks over src/. Each states its pattern beside its count, each
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

    SO STAGE BEFORE RUNNING THIS, if the change adds a file [1.8]. `git ls-files`
    reads the index, so a brand new file is invisible to every check here until
    it is at least `git add`ed, and a pre-commit run over an unstaged new file
    reports a pass it has not earned. That is how 4b53173 was committed with the
    INVARIANT 16 check red over a file created in the same change, having run
    green thirty seconds earlier. ci.ps1 does not have the gap, because it checks
    HEAD out into a worktree where every file is tracked by definition, and it is
    what caught both occurrences.

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
        Exclude    = @(
            'src/StockResearcherLab.Pipeline/Ingest/SentimentIngestor.cs',
            'src/StockResearcherLab.Pipeline/Ingest/FlowIngestor.cs')
        Why        = 'Two files, both for the same reason and neither monetary. SentimentIngestor binds sentiment_score and FlowIngestor binds institutional_holding.change_pct, both declared real in 0001 and SCHEMA.md; binary COPY is strict about types, so the CLR type has to be float. Every monetary column each file touches is numeric and binds as decimal, FlowIngestor''s shares, change, price_per_share and total_value included. THIS EXCLUSION DOES NOT SCALE and the count is now two before phase 2 has started: that phase adds about forty real columns to indicator_daily and would need forty entries, so the general question of how a blunt grep tells a technical float from a monetary one is owed an authored answer before it [PROGRESS, INVARIANT 16 and non-monetary floats]'
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
    },
    @{
        Invariant  = 'CLAUDE.md section 6'
        What       = 'US Eastern is resolved in one place: no TimeZoneInfo outside the clock implementation'
        Pattern    = 'TimeZoneInfo'
        Extensions = @('.cs', '.razor')
        Exclude    = @('src/StockResearcherLab.Data/SystemClock.cs')
        Why        = 'Every US Eastern conversion happens in SQL, where Postgres carries its own tzdata, except SystemClock.cs, which is the single place permitted to read the ambient clock and is therefore the single place permitted to convert it. This exclusion list is that boundary [A23]. It matters because InvariantGlobalization is on, so FindSystemTimeZoneById cannot resolve an IANA id on Windows and throws only on the path that reaches it; SystemClock already tries the IANA id then the Windows one, in that order'
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

# Every extension any check runs over. The union of the per-check scopes must come
# to every tracked file carrying one of these, or the sweep is narrower than it
# reads and a green line says nothing about the files it stopped covering. A path
# move, an ignore rule or a change to how the list is enumerated would all shrink
# it silently, which is the same defect as a count that said four and meant five.
$allExtensions = @($checks | ForEach-Object { $_.Extensions } | Sort-Object -Unique)
$expected = @($files | Where-Object { $allExtensions -contains [System.IO.Path]::GetExtension($_) })
$swept = New-Object System.Collections.Generic.HashSet[string]

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

    foreach ($path in $scope) { [void] $swept.Add($path) }

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

# The scope is asserted rather than printed for a human to eyeball. Five checks
# finding zero over a set that quietly shrank produces a line identical to five
# checks finding zero over everything.
$missed = @($expected | Where-Object { -not $swept.Contains($_) })
if ($missed.Count -gt 0) {
    Write-Host "guards.ps1: FAIL. $($missed.Count) tracked file(s) carry a scanned extension and no check swept them:"
    foreach ($m in ($missed | Sort-Object)) { Write-Host "      $m" }
    Write-Host "            A green line over a set that shrank is indistinguishable from a green line over everything."
    exit 1
}

Write-Host "guards.ps1: ok. $($checks.Count) checks over $($swept.Count) files, every one expecting zero and finding zero."
exit 0
