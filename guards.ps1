#requires -Version 5.1
<#
    guards.ps1 - the CI greps for the invariants that are grep-checkable.

    Five checks. Four are greps over src/ and the fifth is INVARIANT 16, which
    asserts over the schema instead. Each states what it ran against beside its
    result, and any failure fails the run.

    THE FOUR GREPS each expect zero. A zero expectation is self-validating: a
    wrong pattern that finds nothing and a right pattern that finds nothing are
    indistinguishable from the number alone, so the file set is asserted
    non-empty first and the pattern is printed for reading.

    INVARIANT 16 ASSERTS RATHER THAN EXCLUDES [1.8]. It used to grep for
    `float` and `double` with a list of files to skip, and the list had reached
    two before phase 2 had started, which adds about forty technical `real`
    columns. A list like that gets extended until the guard is suppressed rather
    than satisfied. Two positive checks replace it, both reading the migrations
    and `SCHEMA.md`:

      1. Every column whose name matches the monetary pattern is `numeric`,
         unless `SCHEMA.md` declares it as not money. The expected count is
         stated so the check cannot pass over an empty match set.
      2. Every `real` and `double precision` column in the migrations is
         declared in `SCHEMA.md` as not money.

    So adding a `real` column means declaring it in the schema document, which
    is where a reader would look, rather than in this script, which is where
    nobody does.

    IT READS THE MIGRATIONS RATHER THAN THE DATABASE, because `ci.yml` runs this
    script before the migrate step and there is no schema to read at that point.
    The migrations are the schema's definition and they are tracked, so the two
    agree by construction. `SchemaParityTests` makes the same assertion against
    the live database, where one exists.

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
        Invariant  = 'INVARIANT 16'
        Kind       = 'schema'
        What       = 'money is numeric: asserted from SCHEMA.md, not excluded per file'
        Pattern    = '(_usd|price|value|cap|cost|equity|pnl|dollar|amount)'
        Extensions = @('.sql')
        Exclude    = @()

        # Stated in advance so the check cannot pass over an empty match set, and
        # exact rather than a floor so a column added without a matching thought
        # about its type is caught in both directions. Fewer means something
        # stopped being read; more means a monetary column arrived and this number
        # moves with it, deliberately.
        # Moved 17 to 18 at 2.2: fundamental_snapshot.capital_expenditures, which
        # matches through "cap" and is money [D-79].
        ExpectedMonetary = 18

        Why        = 'The previous mechanism was a grep for float and double with a list of files to skip. The list had reached two before phase 2, which adds about forty technical real columns, and a list like that gets extended until the guard is suppressed rather than satisfied. This asserts instead: every monetary-named column is numeric unless SCHEMA.md declares it as not money, and every real column is declared there. Adding a real column therefore means declaring it in the document a reader would look at'
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

# ------------------------------------------------------------ INVARIANT 16 ---
#
# The migrations parsed to a column-to-type map. Quoted identifiers are accepted,
# and that is not a nicety: `order` and `position` are reserved words and are
# quoted in 0001, and the first version of this parser skipped both tables and
# reported on six fewer monetary columns than exist while printing a pass.

function Get-MigrationColumns {
    $dir = Join-Path $root 'src/StockResearcherLab.Data/Migrations'
    $files = @(Get-ChildItem -Path $dir -Filter *.sql | Sort-Object Name)

    if ($files.Count -eq 0) {
        throw "No migration found under $dir. This check reads the schema from them and has nothing to read."
    }

    $columns = @{}
    $script:MigrationFilesRead = @($files | ForEach-Object {
        'src/StockResearcherLab.Data/Migrations/' + $_.Name
    })

    foreach ($f in $files) {
        $text = [regex]::Replace((Get-Content $f.FullName -Raw), '--[^\r\n]*', '')

        foreach ($m in [regex]::Matches($text, '(?is)CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?"?([a-z_0-9]+)"?\s*\((.*?)\r?\n\)\s*;')) {
            $table = $m.Groups[1].Value
            foreach ($line in $m.Groups[2].Value -split "`n") {
                $t = $line.Trim().TrimEnd(',')
                if ($t -match '^(PRIMARY|UNIQUE|FOREIGN|CONSTRAINT|CHECK)') { continue }
                if ($t -match '^([a-z_0-9]+)\s+(.+)$') {
                    $columns["$table.$($matches[1])"] = Get-ColumnType $matches[2]
                }
            }
        }

        foreach ($m in [regex]::Matches($text, '(?is)ALTER\s+TABLE\s+"?([a-z_0-9]+)"?(.*?);')) {
            $table = $m.Groups[1].Value
            foreach ($a in [regex]::Matches($m.Groups[2].Value, '(?is)ADD\s+COLUMN\s+(?:IF\s+NOT\s+EXISTS\s+)?([a-z_0-9]+)\s+([^,\r\n]+)')) {
                $columns["$table.$($a.Groups[1].Value)"] = Get-ColumnType $a.Groups[2].Value
            }

            # A rename moves the entry, so the map holds current names. Without
            # this, 0002's flow_daily rename would leave the old name in the set
            # and the new one absent.
            foreach ($r in [regex]::Matches($m.Groups[2].Value, '(?is)RENAME\s+COLUMN\s+([a-z_0-9]+)\s+TO\s+([a-z_0-9]+)')) {
                $from = "$table.$($r.Groups[1].Value)"
                $to = "$table.$($r.Groups[2].Value)"
                if ($columns.ContainsKey($from)) { $columns[$to] = $columns[$from]; $columns.Remove($from) }
            }
        }
    }

    return $columns
}

function Get-ColumnType {
    param([string] $Rest)

    $t = [regex]::Replace($Rest.Trim(), '(?i)\s+(NOT\s+NULL|NULL|GENERATED|DEFAULT|PRIMARY|UNIQUE|REFERENCES|CHECK).*$', '')
    return $t.Trim().ToLowerInvariant()
}

# Every `table.column` SCHEMA.md declares as not money, read from the tables under
# its "Columns that are not money" heading.
function Get-DeclaredNonMonetary {
    $path = Join-Path $root 'docs/SCHEMA.md'
    $text = Get-Content $path -Raw

    $section = [regex]::Match($text, '(?s)###\s+Columns that are not money(.*?)\r?\n---')
    if (-not $section.Success) {
        throw "docs/SCHEMA.md has no 'Columns that are not money' section. INVARIANT 16 asserts against that list and cannot assert against nothing."
    }

    $declared = @{}
    foreach ($m in [regex]::Matches($section.Groups[1].Value, '(?m)^\|\s*`([a-z_0-9]+\.[a-z_0-9]+)`')) {
        $declared[$m.Groups[1].Value] = $true
    }

    return $declared
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

    if ($check.Kind -eq 'schema') {
        $problems = @()

        $columns = Get-MigrationColumns
        $declared = Get-DeclaredNonMonetary

        # The set read and the set tracked must be the same set, in both
        # directions. A tracked .sql outside Migrations is a file this check does
        # not read while its scope line implies it did. An untracked .sql inside
        # Migrations is the opposite: read here and absent from a CI checkout, so
        # the guard would reach a different verdict locally than in CI, which is
        # the failure ci.ps1 exists to catch and this one should not create.
        $read = @($script:MigrationFilesRead)

        foreach ($o in @($scope | Where-Object { $_ -notlike 'src/StockResearcherLab.Data/Migrations/*' })) {
            $problems += "      $o is a tracked .sql file this check does not read"
        }

        foreach ($u in @($read | Where-Object { $scope -notcontains $_ })) {
            $problems += "      $u is read by this check and is not tracked, so CI would not see it"
        }

        # 1. Monetary name, not numeric, not declared as not money.
        $monetaryNumeric = 0
        foreach ($key in ($columns.Keys | Sort-Object)) {
            $column = $key.Substring($key.IndexOf('.') + 1)
            if ($column -notmatch $check.Pattern) { continue }

            if ($columns[$key] -like 'numeric*') { $monetaryNumeric++; continue }
            if ($declared.ContainsKey($key)) { continue }

            $problems += "      $key is $($columns[$key]) and its name says money [INVARIANT 16]"
        }

        if ($monetaryNumeric -ne $check.ExpectedMonetary) {
            $problems += "      expected $($check.ExpectedMonetary) monetary numeric column(s) and found $monetaryNumeric. Fewer means part of the schema stopped being read, which is how quoted identifiers hid six of them once. More means a monetary column arrived and this number moves with it"
        }

        # 2. Every real or double precision column declared in SCHEMA.md.
        $realCount = 0
        foreach ($key in ($columns.Keys | Sort-Object)) {
            if ($columns[$key] -notmatch '^(real|double precision)(\[\])?$') { continue }

            $realCount++
            if ($declared.ContainsKey($key)) { continue }

            $problems += "      $key is $($columns[$key]) and SCHEMA.md does not declare it as not money [INVARIANT 16]"
        }

        # 3. Nothing declared that no longer exists, so the list cannot rot into a
        #    set of names that stopped meaning anything.
        foreach ($key in ($declared.Keys | Sort-Object)) {
            if (-not $columns.ContainsKey($key)) {
                $problems += "      SCHEMA.md declares $key as not money and no migration defines it"
            }
        }

        $verdict = 'ok'
        if ($problems.Count -gt 0) { $verdict = 'FAIL'; $failed++ }

        Write-Host "  $($check.Invariant)  $verdict"
        Write-Host "    $($check.What)"
        Write-Host "    pattern : $($check.Pattern) against the column name, never the table's"
        Write-Host "    scope   : $($columns.Count) columns over $(@($script:MigrationFilesRead).Count) migration file(s) read and $($scope.Count) tracked, against $($declared.Count) declared in SCHEMA.md"
        Write-Host "    why     : $($check.Why)"
        Write-Host "    expected: $($check.ExpectedMonetary) monetary numeric, every real declared    found: $monetaryNumeric monetary numeric, $realCount real, $($problems.Count) problem(s)"
        foreach ($problem in $problems) { Write-Host $problem }
        Write-Host ""
        continue
    }

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
    Write-Host "guards.ps1: FAIL. $failed of $($checks.Count) checks did not hold."
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

Write-Host "guards.ps1: ok. $($checks.Count) checks over $($swept.Count) files, four greps finding none of what they look for and one schema assertion over the migrations."
exit 0
