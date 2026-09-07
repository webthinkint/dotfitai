# Task 3 — Pick between two near-identical answers

**Who:** nutritionist
**Effort:** 15 minutes
**Blocking:** no
**Tracked as:** open item 7

## What this is

The same question gets asked many times over the years, and we do not want the
assistant retrieving four versions of one answer. So the system groups
near-identical questions and keeps the newest as the canonical one.

It does that automatically — except when it cannot tell whether two answers
actually *agree*. In that case it refuses to choose and asks a person. That has
happened exactly once, on one pair of records.

## The pair

Both are the same question, from 2024, about FirstString protein for muscle
gain:

**Record A — 4 September 2024**

> I plan to use FirstString to gain muscle mass. What else should I take with
> it to help build muscle, and do I have to take the pre-workout serving as
> recommended?

The answer is a full Level 1 performance plan: Active MV, Super Omega-3, Super
Calcium, FirstString, plus Creatine Monohydrate, with dosing for each.

**Record B — 16 August 2024**

> I plan to take FirstString to gain muscle mass while doing calisthenics and
> interval training; what else can I take with it to help, and do I have to
> take the pre-workout serving exactly as recommended?

The answer is about *why* FirstString is the right protein — its macronutrient
makeup, hitting 1 g protein per lb of lean body mass, timing around workouts —
and then points to the baseline programme.

## Why the computer would not decide

It compares the products each answer recommends. Usually one list is a subset of
the other, which means they agree and the fuller one wins. Here they overlap but
neither contains the other: record A lists six products record B does not, and
record B lists one product (part number 1007) that record A does not.

Overlapping-but-not-nested is the one shape the system treats as "these might
genuinely disagree — ask a human."

## What we need

One of two answers:

**Option 1 — Split them.** Treat them as two different questions and keep both
searchable. They read as different questions to us: one asks "what's the plan",
the other asks "why this protein and does timing matter". A customer could
legitimately want either.

**Option 2 — Merge them,** and tell us which is canonical. The other stops being
retrievable.

**Our suggestion is Option 1, split.** The project's own rule for these cases is
that mistakes are not symmetric: wrongly merging deletes a real answer from the
assistant permanently, while wrongly keeping both just leaves a duplicate that
someone might retrieve. The cheap mistake is the better one to make. But this is
your call, not ours — you know whether these are really the same question.

## What happens if this waits

Nothing breaks. Both records stay searchable in the meantime, which is the safe
state. This is the smallest item on the list; it is here because leaving it open
indefinitely means the "unresolved" pile never reaches zero and stops being
meaningful.
