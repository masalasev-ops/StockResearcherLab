# Phase P — Data probe

    Target:    Claude Code
    Issued:    2026-08-05
    Amended:   2026-08-05, before being run, for D-55 secrets handling and
               CLAUDE.md section renumbering
    Corrected: 2026-08-05, working copy predated the third amendment
    Status:    ISSUED
    Produced:  (fill on completion)

Record of what was asked. Never edited once run.

The reconciliation question in CLAUDE.md section 14 permits amending a prompt that
has not been run yet, which is what happened here: D-55 changed how secrets are
supplied after this prompt was written and before it was issued to a session.

---

Phase P, the data probe. See docs/BUILD_PLAN.md for scope and definition of done.
Read CLAUDE.md first. Section 2 invariants do not apply to this phase because no
pipeline code is being written. Sections 7 and 10 do, being claims about the code
and git and secrets.

Commit per checkpoint, message opening with the checkpoint number, so `P.2 probe
scaffold`. Checkpoints are the five below and match docs/BUILD_PLAN.md.

This phase exists because four assumptions in the architecture were designed around
and never measured. Its output is numbers, not a component.

EODHD publish an OpenAPI 3.1 spec covering their endpoints. Use it rather than
guessing parameter names.

P.1  REPOSITORY BASELINE
     Add a .NET 10 solution in the .slnx format, named StockResearcherLab.slnx,
     with Directory.Build.props and Directory.Packages.props at the root for
     central package management. A single console project at tools/probe.
     Nothing else. No class library, no DI container, no test project, no
     schema.
     Configuration follows the repository convention [D-55]. Copy
     appsettings.Secrets.example.json from the repository root to
     tools/probe/appsettings.Secrets.json, and read the EODHD token from
     Eodhd:ApiToken through the standard configuration builder with that file
     added as an optional source.
     The .gitignore already excludes *.Secrets.json and re-includes the example.
     Do not modify it. Do not add the token to appsettings.json, to any committed
     file, to any log line, or to any sample output.
     DoD: solution builds; `git status --ignored` shows appsettings.Secrets.json
     as ignored rather than untracked; a grep of everything staged for the first
     commit finds no token-shaped string.

P.2  PROBE SCAFFOLD
     One Program.cs. Raw HttpClient and System.Text.Json. Deliberately no
     interfaces, no repository pattern, no retry policy, no resilience, no
     abstraction over the provider, and no attempt to model the responses beyond
     what each measurement needs.
     This is a measuring instrument. It will be read once and deleted. Building
     the real client here means its findings arrive already baked into an
     abstraction chosen before the answers were known.
     If a call fails, print the status code and the first 500 characters of the
     body, then continue to the next measurement. Do not retry and do not throw.
     DoD: runs end to end against a single ticker and prints something for every
     measurement, including failures.

P.3  SAMPLE SELECTION
     Every measurement below runs against six companies with market caps between
     $300M and $2B, spread across at least four different sectors, plus one
     megacap as a control.
     Select them programmatically rather than hardcoding a list, because a name
     that was small last year may not be now. Print the selected set with its
     market caps and sectors before running anything else.
     Everything in this probe looks fine on a megacap. The control exists to
     prove the code works, not to answer any of the four questions.
     DoD: the printed set shows six names in band across four or more sectors.

P.4  THE FOUR MEASUREMENTS
     For each measurement print raw counts per ticker. Do not summarise, do not
     interpret, do not judge whether a result is good. Judgement happens after.

     P.4.1  News archive depth. Request news per ticker with a from-date five
            years back. Print the earliest article date actually returned, the
            total article count, and the count per year.

     P.4.2  Sentiment coverage. Pull the sentiment endpoint per ticker over the
            last 180 days. Print the number of days with a row, the number of
            days with a non-zero article count, and the mean and max article
            count. A series that exists but is empty is the failure case here,
            so report both separately.

     P.4.3  Flow coverage. Per ticker, print insider transaction count over the
            last 90 days, the number of distinct insiders, whether short
            interest is populated at all, and how many short interest
            observations exist over the last 180 days.

     P.4.4  Filing dates. Pull fundamentals per ticker. For the last eight
            quarters print period_end, filing_date, and the gap in days between
            them. Print explicitly when filing_date is absent, null, or equal to
            period_end.

     Also print the row count returned by the bulk end-of-day endpoint for one
     recent US trading day. That number becomes the freshness guard tolerance.

     DoD: all four measurements produce numbers for all seven tickers, or a
     printed reason why not.

P.5  RECORDING
     Write the results to docs/PROGRESS.md by editing the existing "Probe
     findings" table in place. Do not replace the file and do not restructure it.
     Fill the Answer and Measured columns only.
     State uncertainty as uncertainty, per CLAUDE.md section 7. If a number came
     from one ticker rather than six, say so in the cell.
     Do not open DECISIONS.md. If a finding contradicts a decision, report it in
     your summary and stop. Decisions are authored, not build-written, per
     CLAUDE.md section 13.
     DoD: the table is filled, the rest of PROGRESS.md is byte-identical, and
     any contradiction with an existing decision is reported rather than acted
     on.

AFTER
     Do not sign the phase off yourself. Sign-off is the five steps in
     docs/BUILD_PLAN.md and step two is a conformance pass in a session that did
     not build the phase. Report what you found and stop.
