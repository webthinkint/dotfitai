# Things we need from you

The dotFIT knowledge assistant is built and running. It answers customer
questions about nutrition and supplements using only dotFIT's own material —
approved product copy, the practitioner reference guide, past customer Q&A,
and the podcast — and it cites where every answer came from.

What is left is not mostly code. Six things need a decision or a pass from a
person, and this folder explains each one in plain language: what it is, why it
matters, what we need, and how long it should take.

**You do not need to read the code, and you do not need all six done to keep
going.** Only two of them block anything.

## At a glance

| # | Task | Who | Effort | Blocking? |
|---|---|---|---|---|
| [1](01-golden-set-labeling.md) | Write down the right answer for 250 real questions | Nutritionist + support lead | 2–3 days | **Yes** — this is how we prove the assistant is accurate |
| [2](02-privacy-review-queue.md) | Confirm a privacy check on 222 old emails | Support lead | Half a day | **Yes** — 203 of them are already searchable |
| [3](03-duplicate-answer-conflict.md) | Pick between two near-identical answers | Nutritionist | 15 minutes | No |
| [4](04-product-copy-freshness.md) | Own the monthly product-copy check | Named owner needed | 30 min to set up | No, but risk grows |
| [5](05-azure-account-tasks.md) | Two Azure account chores | Whoever holds the Azure account | 1 hour + waiting | Partly |
| [6](06-decisions-after-testing.md) | Three choices that need a test run first | Project owner | Read a report | No — not ready yet |

## If you only do two things

**Task 2 first** (half a day). It is a privacy confirmation, and 203 of the 222
records involved are already live and searchable. Nothing is known to be wrong;
we need it confirmed rather than assumed. Almost all of it is a bulk sign-off —
only **3 records** genuinely need reading.

**Then task 1** (2–3 days). Until it is done we can measure whether the
assistant *finds* the right material, but not whether its answers are *right*.
That is the difference between "it works" and "we can show it works."

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
  reworded.

## A word on how to read these

Where a number appears — "222 records", "250 questions" — it comes from the real
system, not an estimate. Where something is uncertain, the document says so.
None of these tasks require you to trust a number you cannot check; each one
says where it came from.
