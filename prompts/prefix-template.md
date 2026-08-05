# prefix-template.md

The shared prefix, roughly 4,600 tokens, cached once per night and read by every
candidate call.

**This file is byte-sensitive.** INVARIANT 6 requires the assembled prefix to be
identical across every call within a night. That constrains how it is built:

- No clock reads. The date comes from the run's simulated date, not `DateTime.Now`.
- No iteration over unordered collections. Lessons, calibration rows and sector
  strengths are all sorted explicitly before rendering.
- Number formatting is applied in exactly one place, with an invariant culture.
- No trailing whitespace, and a fixed line ending.
- The assembled string is built once at the start of the run and reused, never
  rebuilt per call.

`prefix_hash` is stored on the dossier row. The snapshot test asserts every call in
a night shares one hash.

The prefix changes between nights, which is expected: market context is daily and
lessons are monthly. It never changes within one.

---

## Assembly order

Order is fixed. Changing it invalidates the cache for that night and, more
importantly, makes two nights' prefixes differ for a reason that is not a content
change.

```
1. ROLE AND TASK          static
2. FIELD KEY              static
3. THE FIVE RUBRICS       from prompts/rubrics.md, config-versioned
4. ACTIVE LESSONS         from researcher_memory, sorted by written_at
5. CALIBRATION            from the last monthly report, sorted by screen then bucket
6. MARKET CONTEXT         from market_context_daily for the run date
7. OUTPUT CONTRACT        static
```

---

## 1. Role and task

Static text. See `prompts/rubrics.md`, "Shared instruction".

## 2. Field key

The candidate block sends bare values with short keys and no labels, because a
candidate token costs about seven times a prefix token. This section is what makes
that readable. It is a static table mapping every key to its meaning, its unit, and
whether the paired number is a percentile.

Example of the form, not the full list:

```
fcfy    free cash flow yield, percent, followed by peer percentile
evebit5 EV/EBIT relative to own 5-year range, ratio, followed by peer percentile
d200    distance from 200-day moving average, percent
atrp    ATR(14) as percent of price
dte     days until next scheduled earnings report
```

The full key list is generated from the same source the candidate block builder
uses, so the two cannot drift apart. That generation is deterministic and ordered.

## 3. The five rubrics

Inserted verbatim from `prompts/rubrics.md` at the config version in force for the
run date. Resolved as-of, never as-now [INVARIANT 13].

## 4. Active lessons

Up to ten. Each rendered as the lesson text, its sample size, and the date it was
written. Requires n of at least 30 and expires after six months unless reconfirmed
[D-44].

Rendered sorted by `written_at` ascending, then by id, so two runs with the same
lesson set produce the same bytes.

## 5. Calibration

From the most recent monthly report. Per model and per screen: stated probability
bucket, realised rate, sample size. Shown so the model can see where it has been
over- or under-confident.

Sorted by screen id then bucket lower bound.

## 6. Market context

One block for the run date: breadth, VIX level and change, regime label, and sector
relative strength sorted by sector name.

This is the only part of the prefix that is genuinely different every night, and it
is the reason the cache is per-night rather than persistent.

## 7. Output contract

Static text. The JSON schema, the field constraints, and the bar. See
`prompts/rubrics.md`, "What to return" and "The bar".

The 60-word thesis and 20-word counter-argument limits are stated here and also
enforced as a hard `max_tokens` on the call. The rubric statement is for quality;
the token cap is what protects the budget, because output is the cost line that
cannot be compressed.

---

## What must never enter the prefix

- Any timestamp other than the simulated run date
- Any candidate-specific value
- Any figure derived from a single ticker
- Anything that varies with which call in the night this is

Each of these breaks the cache silently. The run completes, the proposals look
normal, and the input bill roughly triples. The cache hit rate on the run health
screen is the detector.
