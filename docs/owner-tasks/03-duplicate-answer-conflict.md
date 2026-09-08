# Task 3 — Pick between two near-identical answers

**Who:** nutritionist
**Effort:** done
**Blocking:** no
**Tracked as:** open item 7

## Status: closed, 8 September 2026 — split, both kept

Nothing further is needed from you. This file is kept as the record of what was
decided and why.

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
