# Task 7 — Decide what the stakeholder preview is allowed to say

**Who:** project owner
**Status:** **DECIDED 2026-09-10 — Option A, in its strongest form.** The
stakeholder preview is *never* blocked on a measurement; it may ship at any
state so the project can be tested continuously. Kept here as the record of
what was decided and what it commits us to.
**Tracked as:** open item 21 (closed); the ruling is in `docs/v1/decisions.md`

The assistant is going onto the dotFIT website behind the website team's own
server. The first version is for **stakeholders and approved partners**, not the
public.

That distinction is the whole question here.

---

## What we said earlier

Two open measurements were written down as "must be settled **before customers
use the assistant**". Both are about *claim wording* — whether the assistant
repeats dotFIT's legally approved product copy word-for-word, or paraphrases it.

Stakeholders and approved partners are not customers. So the rule we wrote does
not automatically apply, and nobody should quietly decide that on their own.

---

## Where those two things actually stand

**Retrieval and safety are fine.** The assistant finds the right source material
99.2% of the time, and it correctly refused all ten medical questions put to it.
Nothing here is about the assistant giving dangerous advice.

**Claim wording is not fine yet.** Two specific findings:

- The automatic claims check **misses things**. In the most recent measured run,
  four answers that a reviewing model judged to contain non-compliant claim
  language were delivered anyway. The check rated all four as clean. We only
  discovered this because we went looking; there was no measurement watching for
  it. There is now.
- The new answering model **paraphrases where the old one quoted**. Approved copy
  says one thing, the assistant says something close but not identical. Close is
  not good enough when the wording is the part that was legally approved.

Neither is a hypothetical. Both come from a real run against 125 questions.

---

## The decision

**Option A — ship the preview now, with the audience told.**

Stakeholders and partners get access. They are told plainly: retrieval and
medical safety are measured and good, claim wording is not yet validated, please
report anything that reads like a product claim. The approved copy is always
right; the assistant is wrong wherever it differs.

*Why we lean this way:* the alternative evidence we have is 125 questions we
wrote ourselves. Real stakeholder questions are better evidence, and a preview
audience that has been told what to look for is the cheapest way to get it. We
have also just added the measurement that was missing, so a second run will tell
us more than the first did.

**Option B — hold the preview until claim wording is fixed and re-measured.**

*Why you might:* a partner who repeats a non-compliant claim to their own
audience is not obviously a smaller problem than a customer reading one. If
anything, partners repeat things. If the preview audience includes anyone who
might quote the assistant publicly, that argues for waiting.

---

## What was decided, and what it commits us to

**Option A, without the conditions attached to it.** The preview is never held
back for a metric. Testing throughout, at any state, is worth more than waiting
for any particular number.

Two things follow, and they are now standing commitments rather than one-time
steps:

1. **The claim-wording caveat travels with every preview release**, not just the
   first. The audience needs to know, on an ongoing basis, that product-claim
   wording is not yet validated and that dotFIT's approved copy is right
   wherever the assistant differs from it. The wording is in
   `docs/v1/website-integration.md`. It still needs your sign-off once — it is a
   claims-compliance statement, not an engineering one.
2. **We log what each release actually decided** (item 20 — **built
   2026-09-11**). If any state can ship, then when someone asks "what did it
   tell my partner in March", this log is the only way to answer. That work was
   optional before this ruling and is not optional now, so it was done.

   Worth knowing what it does and does not hold, because it is a privacy
   question as much as a records one. For every question the assistant is asked,
   we now keep one line recording **what it decided**: whether it refused and on
   what grounds, whether it withheld an answer, whether its safety check passed,
   how many sources it used and cited. We keep **no copy of the question and no
   copy of the answer** — the website's own database already holds those, and a
   second copy of real customer wording is a risk we are not willing to take on.
   The two records are linked by a per-question reference number, so either side
   can be looked up against the other when someone asks.

The one thing still worth your attention: if anyone in the preview audience
might quote the assistant in external material, tell us — that is a different
risk from internal testing and we would want to say so explicitly in the
briefing.

---

## What this does not decide

The assistant still refuses medical questions and hands them to a human, still
withholds any answer that fails its safety check, and still cites every source.
None of that is on the table. This decision is only about the wording of product
claims in answers that are delivered.
