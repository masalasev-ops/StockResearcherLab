# Post phase 1 reconciliation — the architecture document left clean

    Target:    Claude Code
    Issued:    2026-08-10
    Status:    SPENT
    Archived:  before any file was changed, per `CLAUDE.md` §3.

**This is not a lettered correction pass.** The session opened by inventing the
letter Q, having read the commit convention as requiring one, and was told mid-run
to drop it and file the work as post phase 1 reconciliation instead. Commit
messages carry that phrase where a phase would go. Recorded because a letter
invented by a session is exactly the kind of thing that later reads as authored.

---

## Architecture reconciliation, with the document left clean

Human-directed corrections. The sign-off review settled every reading here, so the
session transcribes rather than judges.

**Do not remove "earnings jump the queue".** The review confirmed the architecture
is right and the code is incomplete. Removing it to match what was built is the
move CLAUDE.md §3 forbids, and under the new convention that removal would leave
no trace at all. Same for anything else in §3 describing behaviour phase 1 did not
build.

Confirm D-72 is the highest authored entry, then take the next four. Author all
four before touching HTML.

## D-73 Specs are clean, records keep their strikes

`ACTIVE`. Amends the strike-in-place convention in `CLAUDE.md` §13.

A struck line states a third time what `DECISIONS.md` and `CHANGELOG.md` already
hold. That duplication is the one open item 6 exists to close, and applying it to
the source of truth makes the document that constrains everything else the hardest
one to read. A reader parsing live text from dead text on every visit is paying a
cost the register was built to remove, and a session skimming can take struck text
for live.

In documents read to know the current state, a removal is a clean edit:
`ARCHITECTURE.html`, `SCHEMA.md`, `CONFIG_REFERENCE.md`, `RUNBOOK.md`. Delete the
superseded text, keep the decision citation at the point of change, and record the
prior value in `CHANGELOG.md`.

In documents that are the record, strikes stay. `DECISIONS.md`, because a
superseded entry must keep the reasoning its successor replaced. `PROGRESS.md`,
because a log's corrections are its content. Runtime prompt files remain clean
deletions, unchanged from before.

Existing strikes in the four spec documents are cleaned under this decision, one
condition: no strike is removed until its decision names what it removed. Where a
decision does not, `CHANGELOG.md` records the text before the deletion.

## D-74 The Reads column names where a stage gets its ticker list

`ACTIVE`

§3 gave four ingest components a Reads column containing only their endpoints.
`FundamentalsIngestor` declares `["price_daily", "security"]`; `SentimentIngestor`,
`FlowIngestor` and `EventsIngestor` each declare `["security"]`. `DeclaredAccess`
enforces those sets at runtime, so the catalogue and the running system have
disagreed since 1.4.

The omission is not cosmetic. A per-ticker endpoint needs a ticker list and the
catalogue never said where any of them gets one. That silence is what allowed the
fundamentals pool to be drawn from `security`, closing the universe over itself: a
name needs fundamentals to be admitted, so once `security` was populated only its
own members could be fetched, and coverage froze at 679 with no error and entirely
plausible output.

A pool source and an ordering source are different reads and are named as
different reads. Standing: any per-ticker component states in its Reads cell where
its ticker list comes from and what that read is for.

No code changes. The document begins describing what the code already does.

## D-75 FlowIngestor runs nightly

`ACTIVE`. Supersedes the Weekly cadence in §3, which read `Weekly`.

`RUNBOOK.md` line 17 puts flow at 17:45 with fundamentals and events. The two
documents have contradicted each other since the corpus was written and phase 1's
nightly sequence followed RUNBOOK without recording a choice.

Weekly was set when this component also carried short interest and wrote
`flow_daily`. D-58 removed the first, D-61 moved the second, and the cadence was
never revisited.

The binding constraint is coverage latency, not filing latency. The stage rotates
`flow.max_tickers_per_run` names of 2,841 per run, so a universe pass takes about
twelve runs. Nightly gives complete coverage in roughly twelve days against the
flow screen's trailing ninety-day window. Weekly gives it in about three months,
which is the window itself, so a name's insider figures would be stale by the full
length of the period they are computed over.

Recorded because the earlier argument was wrong: it reasoned from Form 4 arriving
within two business days, which was never binding, and it was made while the stage
had a fixed head and no cadence could have mattered.

## D-76 Writes are stated in §3 and in SCHEMA.md, and nowhere else

`ACTIVE`. Closes open item 6.

§16's store matrix carries Written-by and Read-by columns repeating §3's Writes
and Reads, which repeat `SCHEMA.md`'s writer declarations. Every write-column
defect in passes K through N came from that duplication, and its stated trigger
fired at phase 0.

Both columns are dropped. Two statements are acceptable where three were not, and
the reason is mechanical: the 1.10 conformance test asserts §3's registry against
`SCHEMA.md`'s writer declarations in both directions, so those two are held
consistent by a test. The store matrix's columns were checked by nothing, which is
why they drifted.

## The HTML

`docs/ARCHITECTURE.html` only.

**C03 Reads**, currently `Fundamentals endpoint, <code>events</code>` — append:

    , <code>price_daily</code> for the candidate pool,
    <code>security</code> for rotation order <span class="rmv">D-74</span>

**C04 Reads**, currently `Sentiment endpoint` — append:

    , <code>security</code> for the universe it iterates <span class="rmv">D-74</span>

**C05 Reads**, currently `Insider, <s>short interest,</s> <span class="rmv">D-58</span> ownership`
— becomes:

    Insider, ownership <span class="rmv">D-58</span>,
    <code>security</code> for the universe it iterates <span class="rmv">D-74</span>

**C06 Reads** — append the same `security` phrase and citation.

**C05 Runs**, currently `<td>Weekly</td>`:

    <td>Daily 17:45 <span class="rmv">D-75</span></td>

**C05 Writes**, currently carrying `<s>flow_daily</s> <span class="rmv">D-61</span>`
— becomes `insider_transaction, institutional_holding <span class="rmv">D-61</span>`.

**C05 description**, currently the struck short-interest sentence with its D-58
citation — the sentence goes and the cell keeps a `muted` span naming what the
component does now, cited to D-58.

**§16 store matrix** — remove the Written-by and Read-by columns from the header
and every row, and add a line under the table pointing at §3 and `SCHEMA.md`
`[D-76]`.

**Every remaining `<s>` in the file** — sweep with
`grep -o "<s>[^<]*</s>"`, and for each: confirm the cited decision names what was
removed, record the prior text in `CHANGELOG.md` if it does not, then delete the
struck text and keep the citation. Report any whose decision does not name it
rather than deleting on assumption.

Do not alter any Writes cell beyond C05's, any other Runs cell, or the description
of any component this pass does not name.

## CHANGELOG.md

One entry per decision, plus one entry recording the prior text of every strike
removed by the sweep. With the strikes gone the changelog is the only legible
account of what the document used to say.

## A separate commit: reads get a conformance path

Not part of the reconciliation and it is why the reconciliation was needed. Nothing
compares a declared `ReadSet` against the catalogue in either direction.
`ArchitectureDocument` parses component id and name only, and `ReadSet` is asserted
against hardcoded literals in four component test files while two of the deviating
components assert nothing at all.

Extend `ArchitectureDocument` to parse the Reads cell, and add a conformance test
asserting both directions: every table a component declares is named in its Reads
cell, and every table named in its Reads cell is declared. Endpoint names in that
column are not tables and are excluded by not matching any table in `SCHEMA.md`.

Own commit, after the reconciliation, so it runs against a corrected document.

## Done when

- D-73 to D-76 are ACTIVE; D-75 records `Weekly` as the text it replaced; D-76
  names open item 6 as closed and that row is struck in `PROGRESS.md`, which keeps
  its strikes.
- `CLAUDE.md` §13 states the specs-clean, records-keep rule.
- `grep -c "<s>" docs/ARCHITECTURE.html` returns 0, and every removal has a
  `CHANGELOG.md` line naming its prior text.
- Four Reads cells name `security`, C03's also names `price_daily`, each cited.
- C05's Runs cell reads `Daily 17:45` with a D-75 citation, and no other Runs cell
  changed.
- §16 has no Written-by or Read-by column and carries the pointer line.
- A whitespace-tolerant grep for "earnings jump the queue" returns one hit.
- `ci.ps1` green at 149 before the conformance commit, and green with it after.
