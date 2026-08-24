# Close the four owed items, then sign-off

Runs in the build session. Documents and two cell amendments; no component, no migration, no run.

## The two Writes cells

`ARCHITECTURE.html` §3: C04's cell gains `sentiment_fetch_attempt`, C06's gains
`event_fetch_attempt`. Directed, clean edits under D-73, prior wordings verbatim to `CHANGELOG.md`,
citation at the point of change.

Each fails its own conformance test until the cell is corrected, so land each with its test passing
in the same commit.

These are the sixth and seventh instances of that column drifting in four phases. The pattern is
already recorded with its count; add these two to it rather than opening anything new.

## §06's overlap figure

Amend it against the measurement. State what was measured — 0.5 percent of allocated candidates and
2.2 percent over ranked sets, against 186,038 name-dates — and state that the prior figure of 10 to
15 required the five screens to agree more than independence implies, which is the opposite of what
five disjoint metric families and INVARIANT 2 produce.

Name all three readings that were open and which the numbers support, so the amendment reads as a
bound corrected against evidence rather than moved to fit a result. Prior wording to `CHANGELOG.md`.

The phase's done-when line moves with it, in `BUILD_PLAN.md`.

## S3's coverage tilt

`ARCHITECTURE.html` §05 says S3 tilts small and it ranks 57.8 percent large against a universe at
31.3. Correct the sentence to what was measured, and say the tilt has two separable stages: the
inputs compute for 1,067 of 2,819 names and that covered population is already 47 percent large
before any ranking, then the ranking tilts further.

Do not change S3's composition. That is a decision on its own and it is owed forward — file it as a
carried obligation to phase 8, where the tuner and the paired comparison are the things that would
read it, with the two-stage measurement as its evidence.

## S5's fill

Record the measurement in `PROGRESS.md`: 68 of 35,108 live seats, 0.19 percent, against §05's stated
zero to five with a median of three.

Say which of the three candidate causes the store can distinguish and which it cannot — the gate
thresholds, which have never been measured; the composition; or the two news conditions failing open.
If the store can separate them, report the split. If it cannot, say so rather than picking one.

Then correct §05's fill sentence to what was measured, and file the composition question forward with
S3's.

## Then sign-off

`ci.ps1` green after the last commit, then the review in a session that has not committed here.

## Done when

- both Writes cells name their table, each landing with its own test passing, and the drift count is
  updated rather than a new item opened
- §06 carries the measured figure with all three readings named, and `BUILD_PLAN.md`'s done-when line
  moves with it
- §05's S3 sentence states the measured tilt with its two stages, and the composition question is a
  carried obligation to phase 8
- S5's fill is measured and recorded, §05's sentence corrected, and the causes separated or stated as
  inseparable
- `CHANGELOG.md` carries every prior wording
- `git diff` touches no source file and no migration
- `ci.ps1` green
