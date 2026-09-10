# Task 3 — Pick between two near-identical answers

**Who:** nutritionist
**Effort:** about 20 minutes
**Blocking:** no — the affected answers stay searchable either way
**Tracked as:** open item 7

## Status: decided 10 September 2026 — split on both pairs

The nutritionist ruled **split** on both pairs below: all four records stay
searchable, and neither member of either pair replaces the other. The ruling
is pinned by exact records in the pipeline, so it survives every future
rebuild, and the conflict pile for this part of the system is back to zero.

For pair A the reasoning matches the August FirstString record below: the
February reply adds guidance the January one does not contain, so it is a
delta, not a replacement. For pair B — where the two answers agreed, so the
newer one could have retired the older — the January 2023 record stays
searchable exactly because it carries the fuller FAQ text; retiring it would
have dropped that wording from search.

## The September reopen (kept as the record)

The August decision below stands. But we re-transcribed every question with
the new, larger AI model that day, and the sharper transcriptions made two
*new* pairs look near-identical to the grouping step — pairs it had kept apart
before. When the system cannot tell whether two answers agree, it refuses to
choose and asks a person, which is what happened here. Both members of each
pair are still searchable while these wait.

### Pair A — LeanMR + creatine, 27 January and 9 February 2023

- **27 Jan** — customer asks whether LeanMR can be mixed with creatine
  monohydrate, and whether the daily creatine drink should go before or after
  a workout. The reply covers creatine timing and usage.
- **9 Feb** — asks whether LeanMR, creatine **and AminoFormula** can all be
  taken together. The reply adds new guidance: don't mix the LeanMR with the
  AminoFormula, because a meal replacement and an amino-acid formula serve
different slots in the day.

Worth knowing: these look like they may be **two turns of the same email
thread** — same subject area, thirteen days apart, the second question a
superset of the first. That is the same shape as the FirstString pair below,
which was ruled *split* for exactly that reason. If you read them the same
way, the ruling is quick.

### Pair B — Lean Pack 90 all-at-once, January 2023 and July 2024

- **Jan 2023** — "can I take all Lean Pack 90 products daily or individually
  over the 90 days?" The reply quotes the FAQ: yes, either way.
- **Jul 2024** — "can I take all three products in Lean Pack 90 at the same
time?" The reply: either way works — all at once on a tight timeline, or as
  directed over the 90 days.

These are the **same question asked a year apart, and the answers agree**.
This pair may genuinely deserve the *supersede* ruling (keep the newer as the
canonical answer, retire the older from search) — but note the 2023 record
carries the fuller FAQ text inside it, so it is worth a glance before
retiring anything.

### What a ruling means

- **Split** — two different questions after all: both stay searchable,
  neither replaces the other. (The August FirstString ruling.)
- **Supersede** — same question, newer answer wins: the older record stops
  appearing in search.

Tell us "A split, B supersede" (or whatever you decide) and the ruling gets
pinned by exact records, so it survives every future rebuild.

## The August decision (kept as the record)

## What this was

The same question gets asked many times over the years, and we do not want the
assistant retrieving four versions of one answer. So the system groups
near-identical questions and keeps the newest as the canonical one.

It does that automatically — except when it cannot tell whether two answers
actually *agree*. In that case it refuses to choose and asks a person. That
happened exactly once, on one pair of records, both from 2024 and both about
FirstString for muscle gain.

## What we found when we looked

The two records are not two separate enquiries. They are **two turns of the
same email thread, from the same customer**:

- **16 August** — the customer writes in through the web form: they plan to
  take FirstString to gain muscle mass while doing calisthenics and interval
  training, what else should they take, and do they have to take the
  pre-workout serving exactly as recommended? The reply explains *why*
  FirstString is the right protein, that they need about 1 g of protein per
  pound of lean body mass, and — importantly — that if they cannot manage the
  pre-workout shake, they should just make sure they hit their daily protein
  total.
- **4 September** — they reply to that same email asking what else to take
  with FirstString. The answer adds Creatine Monohydrate and the Level 1
  Performance/Size plan. The whole August reply is quoted underneath it.

So the September email *is* the fuller document. That was the reason to think
these should be merged.

## Why we still kept both

Because the system does not store the September email the way you read it. When
it splits an email into "the question" and "the expert's answer", the quoted
August reply goes into the question half — it is part of the thread history, not
part of the new answer. So the answer it holds for the September record is only
the new paragraph: creatine, plus the Level 1 plan.

That means dropping the August record would have deleted the only place the
assistant can find the answer to *"do I have to take the pre-workout serving?"*
— while the September record's stored question still ends with that exact
question. We would have been left with one record that asks about pre-workout
timing and an answer that never addresses it. That is worse than having a
duplicate.

The two products lists that triggered the whole thing turned out not to be a
disagreement at all. The August answer lists our multivitamins by who should
take which ("if female under 50 use Women's"), which is why Women's MV appears
in it; the September answer names ActiveMV because that is what the Level 1 plan
specifies. Neither contradicts the other.

## What happens now

Both records stay searchable. Neither replaces the other. The unresolved pile
for this part of the system is now zero.

If the same customer's thread ever needs to read as a single answer, the fix is
on our side, not yours: we would stitch the quoted reply back into the September
record before it is stored. We did not do that here because it changes how every
email in the corpus is split, and this one pair does not justify it.
