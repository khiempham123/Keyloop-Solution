# AI Collaboration — Prompt Record

The prompts that actually shaped this submission, in order, with what came back and what
was done with it. Prompts were issued in Vietnamese in a Claude Code session; the intent
of each is given here in English.

The pattern these follow is deliberate and is the strategy described in the README:
**never ask for code first.** Each phase asks the model to extract, to score, to attack,
or to justify — and only then to produce. Work that is generated before the problem is
pinned down is work that has to be thrown away.

---

## Phase 1 — Extract, do not interpret

> Read the assessment brief and pull out the requirements. Do not start designing.

Produced a requirement inventory and, importantly, separated the *explicit* acceptance
criteria of each scenario from the *cross-cutting* requirements in "The Challenge
Structure" and "Deliverables" — the second group is where the persistent-database
requirement hides. Keeping extraction separate from design is what made finding it
possible.

## Phase 2 — Score the options against a rubric, with a recommendation

> Analyse all four scenarios and assess which suits my level. Give reasoning, not a verdict.

Produced the comparison that led to Scenario D: implementation complexity, risk of
scope overrun, and how much material each scenario leaves for the system design
document. See review log **D-1**.

## Phase 3 — Attack the choice

> Re-read Part 2 adversarially. What does Scenario D fail to satisfy by construction?

The highest-value prompt in the whole process. Produced the finding that a persistent
database is mandatory for a backend submission while Scenario D stores nothing of its
own. See review log **D-2**.

## Phase 4 — Plan against a real constraint

> I have six days, and these are the three deliverables. What is the order of work, and what gets cut first if I run late?

Produced the day-by-day plan, the "must not cut" list (parallel fan-out, partial
failure, tests, database, design document, AI narrative, video — each maps to a line in
the evaluation framework), and the cut-order for everything else. The cut list became
§9 Non-Goals in the design document: a choice stated with its reason reads as
judgement, whereas the same gap left silent reads as an oversight.

## Phase 5 — Find the failure mode in my own design

> Here is the fan-out design. Find the bug.

Produced the `Task.WhenAll` finding. See review log **D-3** — this changed the core
type (`UpstreamResult<T>`) rather than a line of code.

## Phase 6 — Justify every technology, including the rejects

> For each technology, give the alternative you rejected and why.

Produced the technology table in §6 of the design document. Forcing a rejected
alternative for every row is what surfaced the FluentAssertions licence problem
(**D-4**) and the question of where integration tests should point (**D-6**) — neither
would have come up from a prompt that only asked what to use.

## Phase 7 — Design document, then plan, then code

> Write the system design document. Then an implementation plan with the tests spelled out per task.

Deliberate sequencing. Writing the design before the plan, and the plan before the code,
means the model implements against a specification I own instead of inventing one — and
it is what lets each task in the plan be verified independently rather than reviewed as
a single opaque pile of generated code.

---

## Phase 8 — Implementation

Prompts added as Tasks 1–8 are executed, each paired with the verification step that
checked its output.
