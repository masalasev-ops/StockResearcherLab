# Phase 5.5 — The corrections, the prompt that started it

**Target** phase 5.5, the corrective pass between phase 5's sign-off and phase 6.

**Issued** 2026-10-01 by the operator to the build session, against `main` at `2349a7f`.

**Status** `ISSUED`. **Produced** the phase 5.5 block in `BUILD_PLAN.md` and D-158 to D-160
in `DECISIONS.md`, drafted at its step 0 and adopted at `d8109f9`, and the checkpoints that
follow it on branch `phase-5.5-corrections`.

**This is the text as issued and nothing below this header is summarised, reordered or
corrected** [D-63]. Two exchanges followed it before any code and they are recorded after
it, because they are what the authored text was adopted on: the operator's direction to
proceed, and the operator's answers to four questions the build session put. The questions
are given as they were put, with the option chosen.

---

## The prompt

Start phase 5.5, the corrective pass between phase 5's sign-off and phase 6.

The plan is prompts/BuildPlans/phase-5.5-the-corrections.md, on main at 2349a7f.
Read it in full before anything else, then CLAUDE.md, then the carried obligations
table at the foot of BUILD_PLAN.md.

BRANCH FIRST. Do not commit to main. Cut phase-5.5-corrections from main before the
first write, and open a PR for it.

STEP 0, AND IT IS A GATE. BUILD_PLAN.md carries no phase 5.5 block, so the phase does
not formally exist yet. Draft one for me to author from: the scope paragraph, the
fifteen-row checkpoint table and the seven done-when lines, in BUILD_PLAN.md's own
style, written to a scratch file rather than into BUILD_PLAN.md. Draft D-158, D-159
and D-160 the same way, in DECISIONS.md's entry format, stating the options and
choosing none. Then stop and show me all four. Authored content is not yours to
write [CLAUDE.md sections 3 and 13]. Nothing below runs until I have authored them.

ONE KNOWN DEFECT IN THE PLAN, FIX IT IN THE DRAFT. The plan's done-when line 5 (line
317) and its Verification section (line 482) both say four checkpoints add a check
that must be shown red before green, and name "5.5.8 to 5.5.10 and 5.5.13". Eight
checkpoint rows actually state a fail-at-HEAD observable: 5.5.2, 5.5.4, 5.5.6, 5.5.9,
5.5.10, 5.5.12, 5.5.13 and 5.5.14. Verify that against the rows yourself rather than
taking it from here, then carry the corrected count into the done-when line you draft.

THEN, once I have authored the block and the decisions, work checkpoints in the
plan's order. 5.5.3 is blocked on D-158 alone; if that decision is still open when
you reach it, skip it, carry on with 5.5.4 onward, and note the out-of-order landing
in PROGRESS.md.

THE RULES THAT BITE ON THIS PASS, stated because they are what it is about:

- Commit per checkpoint, never per phase, message "Phase 5.5 / 5.5.n - what it did".
- Run ci.ps1 per checkpoint, green before the push. 895 tests is the number to beat,
  not to match.
- RED BEFORE GREEN, on all eight checkpoints named above. For each, show the check
  failing against the mutation its row names before you show it passing, and record
  both results. A conformance test that has never failed has not been tested.
- Section 7 of the plan lists six findings you must not touch. ARCHITECTURE.html,
  CLAUDE.md and METRICS.md's definitions are human-edited only. If a checkpoint seems
  to need one of them changed, that is a finding to report, not a task to take on.
- Do not re-run the attribution write, and do not enable the secondary digest link or
  make any paid model call.
- Do not add a measurement to your own scope. The three queries the plan names are
  queries against tables that already exist.

REPORT AT THE END: what landed, what is owed, and anything else the plan got wrong.
The plan was drafted from an audit rather than from building the code, so treat its
line numbers as claims to verify rather than facts. One has already been found wrong
by this route; assume there are others.

---

## The direction to proceed, after the four drafts were shown

so  proceed with implementation. What do you need from me

---

## The four questions put, and the options chosen

**How should the phase block and D-158 to D-160 get into BUILD_PLAN.md and DECISIONS.md?**
Chosen: "Enter drafts + 5.5.15 red". I enter the drafts as you read them, with your answers
below filled in. 5.5.15's new check must first be shown failing against an evening order
with one nightly stage removed, so done-when line 5 counts ten checks. Recorded as drafted
by me and read and authorised by you on 2026-10-01.

**D-158, adx14: METRICS.md says null when +DI + -DI is zero; the code writes 0. What
stands, and what happens to history?** Chosen: "Null; recompute to C13". Code corrected to
null. C08, C11 and both C13 passes re-run over the backfill; attribution and candidate_set
stay frozen (C14 cannot re-run without re-running the attribution write). Boundary date
recorded. S2's floors then come from the corrected distribution.

**D-158, sector_relative_strength: METRICS.md says a member-weighted universe composite and
a difference; the code uses the mean of sector means and a ratio. What stands?** Chosen:
"Fix code; recompute C10". Corrected to METRICS.md and C10 re-run over the backfill, about
10 minutes. Nothing is percentiled from it, no screen reads it and no attribution row
carries it, so the change stays in market_context_daily.

**D-159: where does the one list of tolerated failures live?** Chosen: "RUNBOOK table is the
list". RUNBOOK's failure table gains the 404 and short-page rows at 5.5.14; you edit
CLAUDE.md §6 to delegate to it instead of counting two.
