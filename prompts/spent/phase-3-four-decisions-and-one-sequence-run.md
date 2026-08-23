# Phase 3 — the four decisions, then one sequence run

    Target:    Claude Code
    Issued:    2026-08-22
    Status:    SPENT
    Archived:  after the work, per `CLAUDE.md` §3. The session did not archive it
               before writing code and this header says so rather than implying
               otherwise.

**What this prompt settled, and why it is worth keeping.** It carries four authored
decisions the code cannot be read back from: that the driver refuses a range end past
the frontier rather than clamping it, that the frontier is a real bar count rather than
the newest date present, that C08's two window bounds are deliberately not harmonised to
satisfy a done-when, and that `first_seen`'s changed meaning is corrected in `SCHEMA.md`
as a clean edit. Without it, the next reader meets a guard that refuses instead of
narrowing and has no way to know that was chosen rather than convenient.

**Two things went differently from what the prompt anticipated, and both are recorded
in `PROGRESS.md` rather than corrected here** [D-63: a spent prompt records what was
asked, not what should have been asked].

The sequence run did not happen as written. At the corrected range end C03 and C05
re-dispatch their whole pools for about 440,000 provider units, so the run was cut to
its compute half on the operator's decision mid-session. Open item 62.

Item 60's row-removal half did not close. Its rows stand on 2026-08-13, which carries
one bar rather than none, so item 57's predicate could not reach them.

---

## Item 57: delete the phantom rows

Authorised. Single predicate: zero bars in `price_daily` on that date, across
`indicator_daily`, `sentiment_derived_daily`, `valuation_daily` and `market_context_daily`.

A date with no bar cannot have produced the row standing on it, so this is not a judgement about
which dates deserve to survive. That is why one predicate covers four tables and no date list is
needed.

Record per-table counts before and after against the captured 1,823,849 + 5,929 + 122, so the
delete is auditable rather than asserted.

Say in the item why it mattered: phase 4 counts forward returns in trading days, and a row on a
non-session date makes that count wrong while erroring on nothing. That sentence is what stops a
later session recreating them.

## Item 60: the driver refuses a range end past the frontier

Refuse, do not clamp. A range end the store cannot cover is an operator error, and silently
narrowing a three-year range is the class of thing this system keeps finding.

The frontier is the newest date carrying a real bar count, not the newest date present. That
distinction is the entire finding, since 2026-08-13 was present and empty. Write the definition
where the driver reads it.

C07 does this nightly and the backfill has no equivalent, which is the same shape as the pool
precondition and the seeding race: a guard that exists on one path and not the other.

## Item 52: the byte-identical line takes the exception

The two paths bound SPY differently — nightly by row count ending at the run date, range by
calendar span plus 400 days — so for a ticker with a long hole the windows can have zero overlap.
That is a real counterexample and it is explained.

Do not harmonise the windows to save the line. That is a code change made to satisfy a done-when
rather than because the behaviour is wrong, and which bound is correct is a separate question
nobody has asked.

Amend the done-when to state what replay asserts and what it does not: byte-identical for a
ticker whose window both paths derive identically, and the divergence recorded with its mechanism
for one that does not. Both halves in the line, so a later session testing the other reading
knows it was expected.

## Item 51: security.first_seen

`first_seen` is derived from `price_daily` and the prune truncates it on the next C01 run.

The column now means "the earliest bar the store holds", not "the earliest bar that existed". Say
that in `SCHEMA.md` as a clean edit, since it is a spec describing a column whose meaning changed
under it.

Then say whether anything reads it. If nothing does, that is the answer and it costs nothing. If
something does, name it and what the truncation does to it — do not fix that here.

## Then one sequence-driver run

`Worker backfill 2021-01-04 <the frontier>` after the three code changes land.

The ingest half falls through as `covered`; the compute half re-runs whole at roughly 203 minutes
and lays the rows down against the corrected driver rather than the old one. That closes 3.16 and
3.17 in one pass, which is why it goes last.

Record the total against the timing done-when as a measurement. **Do not tune to reach "minutes".**
203.55 moved further from the target because C34 entered the sum, not because anything slowed, and
whether "minutes" was a requirement with a reason or an aspiration written before anything was
measured is a human's call with the figure in front of them.

## Done when

- the phantom delete reconciles against the captured figures, per table, before and after
- the driver refuses a range end past the frontier, with the frontier defined as a real bar count,
  asserted both directions
- the replay done-when states both what it asserts and what it does not
- `first_seen`'s meaning is corrected in `SCHEMA.md` and its readers are named or their absence is
  stated
- one sequence run completes and its total is recorded as a measurement, untuned
- items 51, 52, 57 and 60 close, and 61 stays open
- `ci.ps1` green, run after each commit
