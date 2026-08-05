# GLOSSARY.md

Terms with a specific meaning in this lab. Ordinary finance vocabulary is not
repeated here.

**Attribution row.** One row per candidate per night, written at shortlist time with
screen scores frozen, holding forward returns filled later. Follows the candidate,
not the trade, so a name that was surfaced but never bought still has one.

**Bucket cap.** At most four of eight open positions in the large-and-above bucket.
The portfolio-level counterpart to the candidate-level size quota.

**Candidate block.** The per-candidate portion of the dossier, roughly 800 tokens of
bare values. Fresh input on every call, which is why it carries no explanatory text.

**Candidate set.** The deduplicated union of what all five screens surfaced tonight.
Around 27 names. All four portfolios see the same set in full.

**Cell.** A size bucket crossed with a sector, the group within which percentiles are
computed. Falls back to size bucket alone below fifteen members.

**Digest chain.** The ordered list of summariser providers. Local model primary,
Haiku secondary. First healthy link answers.

**Dossier.** What a researcher is given: the shared prefix plus one candidate block.
Persisted before any call, which is what makes citation checking possible.

**Exposure matching.** Screens and Random enter only when the primary research
portfolio enters, and take the same count, so no portfolio wins on market timing.

**Floor.** A screen's admission threshold, being the 98th percentile of that screen's
own trailing 250-day score distribution. Self-referential, so it adapts to dispersion.

**Peer-relative return.** Forward return measured against the equal-weighted median
of the name's own cell. The column every learning loop reads.

**Prefix.** The shared, cached portion of the dossier. Byte-identical across every
call within a night.

**Primary research portfolio.** The one carrying `is_primary`. Screens and Random
match their entry count to it. Exactly one at a time.

**Probability.** The researcher's stated chance that a trade reaches its target
before its stop. Replaced an integer conviction scale.

**Rotation.** Two candidates a night routed to the secondary digest provider
regardless of primary health, so that path stays exercised and a paired sample
accumulates.

**Screen.** One of five independent candidate generators, each ranking on its own
metrics and blind to the others. Referred to by name rather than by code in
conversation.

**Size quota.** Two large, three mid, three small per screen. Unfilled slots stay
empty and are never backfilled from a larger bucket.

**Slot.** One of eight places a screen may contribute, subject to the size quota. A
ceiling, never a target.

**Stage.** A pipeline component that declares its read and write sets, takes a date
and a config version, and is a pure function of those inputs.

**Stabilisation gate.** Three conditions a mean reversion candidate must pass before
it is eligible. Two of them fail open below three articles in seven days.
