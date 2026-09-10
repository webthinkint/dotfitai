# Things we need from you

The dotFIT knowledge assistant is built and running. It answers customer
questions about nutrition and supplements using only dotFIT's own material —
approved product copy, the practitioner reference guide, past customer Q&A,
and the podcast — and it cites where every answer came from.

**As of 10 September 2026 it is going onto the dotFIT website** for stakeholders
and approved partners — not the public. By your own ruling that preview is never
held back waiting for a measurement, so it can be tested continuously at
whatever state it is in (task 7). Nothing in this folder blocks it. What is
below is what stands between the preview and real customers.

What is left is not mostly code. Eight things have needed a decision or a pass
from a person, and this folder explains each one in plain language: what it is,
why it matters, what we need, and how long it should take.

**Five are now done** (3, 4, 5, 7, and half of 6) and are kept as the record
rather than as work. Of the rest, **two block a public launch** and none
block the stakeholder preview — that is the point of the task 7 ruling.

## At a glance

| # | Task | Who | Effort | Blocking? |
|---|---|---|---|---|
| [1](01-golden-set-labeling.md) | Write down the right answer for 250 real questions | Nutritionist + support lead | 2–3 days | **Public launch** — this is how we prove the assistant is accurate |
| [2](02-privacy-review-queue.md) | Confirm a privacy check on 234 old emails | Support lead | ~half a day | **Yes** — 211 of them are already searchable |
| [3](03-duplicate-answer-conflict.md) | ~~Rule on two pairs of near-identical answers~~ | — | **Decided 2026-09-10 — split on both** | No |
| [4](04-product-copy-freshness.md) | ~~Own the monthly product-copy check~~ | — | **Decided 2026-09-10** — drops are ad hoc, chain runs same day | No |
| [5](05-azure-account-tasks.md) | ~~Two Azure account chores~~ | — | — | **Done** 2026-09-09 |
| [6](06-decisions-after-testing.md) | Choices that needed a test run first | Project owner | Read a report | 6a **decided**; 6b before public launch |
| [7](07-stakeholder-release-scope.md) | What the stakeholder preview may say | Project owner | **Decided 2026-09-10** | No — preview ships at any state |
| [8](08-golden-redraw-decision.md) | Keep or refresh the 250 test questions | Project owner | ~15 min, before task 1 starts | Gates task 1's start |

## If you only do two things

**Task 2 first** (about half a day). It is a privacy confirmation, and 211 of
the 234 records involved are already live and searchable. **20 records** need
reading and **one ruling** covers another 153 at once. Re-scoped twice, most
recently on 2026-09-10: the corpus was re-transcribed with a larger model that
notices names the smaller one read past, so the read-each pile grew from 2 to
20. The task document explains what changed and why that is not bad news.

**Then task 1** (2–3 days). Until it is done we can measure whether the
assistant *finds* the right material, but not whether its answers are *right*.
That is the difference between "it works" and "we can show it works."

One small gate first: **task 8** is a ~15-minute decision (keep or refresh the
250 questions task 1 will label) that must be taken before task 1 starts —
labeling first and refreshing after would throw the work away. Everything
needed to decide it, including a ready-made preview of the alternative, is in
its document.

## What is already handled

So you know where the line is, these came up during the build and are done —
no action needed:

- Every podcast citation now links to the exact moment in the episode. The
  mapping we thought was unverifiable turned out to be exact.
- The assistant refuses medical questions and hands them to a human. Ten out of
  ten test cases behaved correctly.
- Answers are held back until an automatic safety check passes. A customer never
  sees a partial or unchecked answer.
- Product claims are quoted word-for-word from the approved copy, never
  reworded. (One caveat, honestly: the newer answering model paraphrases more
  than the old one did, and tightening that is live engineering work — task 6b.)
- The optional paid search re-ranker was tested properly and turned **off**: it
  made results worse, not better. No cost, better answers.
- Every Azure account chore is closed — the capacity increase came through and
  the stuck search index is gone.

## A word on how to read these

Where a number appears — "234 records", "250 questions" — it comes from the real
system, not an estimate. When one changes, it is because the system was re-run,
and the document says so rather than quietly swapping the figure. Where something is uncertain, the document says so.
None of these tasks require you to trust a number you cannot check; each one
says where it came from.
