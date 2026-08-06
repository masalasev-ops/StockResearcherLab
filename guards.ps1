#requires -Version 5.1
<#
    guards.ps1 - the CI greps for the invariants that are grep-checkable.

    STUB. Exits zero. Phase 0 checkpoint 0.1 puts the file in place and names
    what it will run; the greps land when there is pipeline code for them to
    have an opinion about. A guard that greps an empty src/ and passes is not
    evidence of anything, and a green stub that says so is better than a green
    stub that looks like a pass.

    What it will run, and the invariant each one stands for:

      INVARIANT 16  money is decimal, never float or double, in any monetary
                    path. Grep for float/double/Single/Double on identifiers
                    matching price, cost, pnl, equity, value, amount, usd.

      INVARIANT 11  no ambient clock. Grep for DateTime.Now and DateTime.UtcNow
                    anywhere outside the IClock implementation, and for
                    DateTimeOffset.Now and .UtcNow with it.

      INVARIANT 6   the prefix is byte-identical within a night. Grep for
                    Guid.NewGuid and Random without a seed anywhere reachable
                    from dossier construction.

      CLAUDE.md 6   determinism. Grep for ToString and string interpolation of
                    numbers and dates without CultureInfo.InvariantCulture, and
                    for Dictionary or HashSet enumeration reaching a write, a
                    render or a hash.

    Each grep states its pattern alongside its result when it runs, and each is
    whitespace-tolerant, because prose and code here are both hard-wrapped and a
    line-anchored pattern misses silently [CLAUDE.md section 7].
#>

Write-Host "guards.ps1: stub, no greps wired yet [0.1]. Exiting 0."
exit 0
