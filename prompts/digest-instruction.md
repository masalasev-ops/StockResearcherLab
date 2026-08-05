# digest-instruction.md

The instruction sent to the digest provider, whichever link in the chain is
answering.

**Identical text for every provider.** The local model and the secondary receive the
same instruction, because the nightly rotation compares them and a difference in
prompt would confound that comparison.

---

## The instruction

You are summarising recent news about one company so that a separate analyst can
judge it. You are not judging it. Do not express a view on whether the company is
attractive, do not recommend anything, and do not describe the stock as cheap,
expensive, promising or troubled.

Read the articles below and produce at most 150 tokens covering:

1. What happened, factually, with dates.
2. If the price has moved sharply, the apparent cause according to these articles.
3. Any of the following if present: accounting investigation, auditor change, going
   concern language, restatement, executive departure, regulatory action,
   litigation, dilutive offering, reverse merger, delisting notice.

Attribute each fact to a dated article. Do not infer, do not connect events the
articles do not connect, and do not fill space.

**If the articles are thin, promotional, or contain nothing material, return exactly
`NO MATERIAL NEWS` and stop.** Padding thin coverage into a plausible narrative is
the single worst outcome here, and it is the specific failure small models exhibit
when asked to summarise very little.

If the coverage appears to be stock promotion rather than reporting, say so
explicitly. That is a fact about the coverage and is useful.

---

## Why this shape

The digest is the only part of the candidate block that cannot be verified. Every
number the researcher cites is checked back against the dossier by the validator.
Prose cannot be. If the summariser misreads an article or manufactures a narrative
from three wire releases, the researcher receives it as fact and the validator
cannot help.

That is why the instruction is extractive rather than interpretive, why attribution
to dated articles is required, and why there is an explicit escape hatch for thin
material. The failure this guards against is not a wrong summary. It is a confident
summary of nothing.

---

## Boundary

INVARIANT 7: the local model transforms evidence and never judges. This instruction
is the enforcement point. Any future change that asks the digest step to score,
rank, filter or opine crosses that line, and crossing it means the research
portfolio is no longer measuring the research model.

---

## Rotation

Two candidates a night are routed to the secondary provider regardless of primary
health, chosen by the date seed [D-27]. `was_rotation` is set on those rows so the
paired sample stays separable from genuine fallthroughs.

The comparison this enables: same night, same articles, same instruction, two
summarisers. After a quarter, whether digest source affects downstream outcomes is
answerable from data rather than argument.
