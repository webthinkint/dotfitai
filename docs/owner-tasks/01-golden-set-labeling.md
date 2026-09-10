# Task 1 — Write down the right answer for 250 real questions

**Who:** a nutritionist and the support lead, working together
**Effort:** 2–3 days
**Blocking:** yes for a public launch — this is how we prove the assistant is
accurate. It does *not* hold up the stakeholder preview (see task 7).
**Tracked as:** open item 8

## What this is

We picked 250 real customer questions from the last few years of dotFIT support
email. For each one, we need you to write down what a *good* answer must
contain. Not the answer itself — just the points it has to hit.

That gives us a scorecard. Once it exists we can ask the assistant all 250
questions and measure how often it covers what it should. And we can re-run that
every time someone changes the system, so we find out immediately if a change
made answers worse.

Without it we are relying on spot-checks and impressions.

## Why we cannot do this part automatically

Everything else about testing this assistant we have automated. This part we
cannot, and it is worth being clear why.

The assistant is being judged on whether it gives *nutritionally correct,
compliant* advice. A computer can check whether it found the right document and
whether it quoted approved wording. It cannot check whether the advice is
actually right — that is the expertise we are trying to encode. If we let the
model decide what a good answer looks like, we would be asking it to grade its
own homework.

So this is the one place where the work is genuinely yours.

## What we need for each question

The file gives you the question and the original expert answer it came from.
For each, fill in three things:

**1. Points to hit** — 2 to 5 bullets a correct answer must cover.

Write them as statements someone can check off, not topics. "Says creatine can
be taken with any fluid" is checkable. "Discusses creatine timing" is not.

**2. Expected sources** — where a correct answer should get its information.
There are four kinds, in order of authority:

1. Approved product copy (the website text legal has signed off on)
2. The practitioner reference guide
3. Past customer Q&A
4. The podcast

Some likely sources are pre-filled with checkboxes. Tick the ones that apply
and add any that are missing.

**3. Forbidden** — anything the answer must *not* say. A general default is
pre-filled (no claims that a product treats or cures a disease, no medical
advice). Add anything specific to that question.

## An important caveat about the source answer

Each item shows you the original expert answer the question came from. **Treat
it as a starting point, not the target.**

Some of these emails are from 2023 or earlier. Product names have changed,
formulas have changed, and some advice has simply moved on. If the old answer is
outdated, write the points to hit as they should be *today*, and note that the
source is stale. That is useful information, not a problem — it tells us the
old answer should not be driving current responses.

## How to do it

The questions are in:

```
processed/golden/worksheet.md
```

It is a single large document with 250 numbered sections, each laid out the same
way, with blanks to fill in. It opens in any text or markdown editor.

Practical suggestions, since 250 is a lot:

- **Split it.** The items are numbered G-001 to G-250 and are independent. Two
  people can take 125 each and never collide.
- **Do the repeats together.** Many questions are about the same products
  (Omega-3, Calcium Complex, Active MV, WheySmooth all appear often). Sorting
  by product first means you write similar points-to-hit while the topic is
  fresh, which is faster and more consistent.
- **Do not overthink the wording.** Three clear bullets beat five careful ones.
- **Mark items you are unsure about** rather than guessing. A question labelled
  "unsure" is honest data; a wrong label quietly corrupts every future test.

You do not need to touch anything else in the folder.

## One thing to know before you start

Right now the system can *produce* this worksheet but cannot yet *read* your
answers back in. Someone on the engineering side needs to write that step — it
is small, perhaps half a day, but it does not exist today.

This does not change your work. It does mean: tell us when you begin, so the
reading step is ready when you finish, rather than your work sitting in a file
for a fortnight.

## What is already done

You may have heard this task described as "label 250 and write 50 adversarial
questions." The 50 are done — those are deliberately hostile questions (someone
asking whether a supplement cures diabetes, someone who is pregnant, someone
asking where their order is) used to check the assistant refuses properly. They
are written and already being tested against. Ten out of ten medical-escalation
cases behaved correctly in the first run.

Only the 250 remain.

## What happens if this waits

The assistant keeps working. We keep the checks we already have: it finds the
right documents, it cites approved copy for product claims, it refuses medical
questions.

What we cannot do is prove the answers are *good*, or notice if a change makes
them worse. For an internal trial that is tolerable. Before customers use it,
it is not.

That distinction now matters in practice: as of 10 September the assistant goes
to a stakeholder and partner audience on the dotFIT website, deliberately
without waiting for any measurement (task 7). Their reactions are useful, but
they are not a substitute for this — a handful of people saying "that looked
right" is not the same as a scorecard you can re-run after every change. If
anything, the preview makes this more valuable, because it is what would catch a
change that quietly makes the preview worse.
