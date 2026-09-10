# Task 6 — Three choices that needed a test run first

**Who:** project owner
**Effort:** reading a report and making the remaining calls
**Blocking:** no
**Tracked as:** open items 5 (closed), 12 and 15

**Updated 10 September 2026.** The full test run happened on 9 September, so
this document is no longer a heads-up — two of the three have numbers now.
**6a is decided and needs nothing from you.** 6b has its first real numbers and
they are more interesting than expected. 6c is unchanged.

---

## 6a — Is the "semantic ranker" worth paying for? — **DECIDED: no**

Azure offers an optional extra step that re-orders search results using a
smarter, slower, chargeable model. We built the assistant so it could be
switched on or off, and deliberately did not guess.

The full run settled it, and not narrowly. Over 125 questions the assistant
found the right source document **99.2% of the time with the ranker off, and
75.2% with it on**. The paid option was substantially *worse*. Its only win
anywhere was a single podcast lookup.

**Decided: leave it off.** No cost, better results, and nothing is needed from
you. The switch still exists if that ever changes.

The early 12-question smoke test pointed the same way, which is reassuring —
but the reason we waited for 125 is that it could just as easily have pointed
the wrong way. It is worth knowing that the small test was right by luck as
much as by design.

---

## 6b — Is the claims safety check too aggressive? (and: is it aggressive enough?)

This is the most important of the three, and the reason it deserves attention.

Before any answer reaches a customer, an automatic check reads it and asks: does
this make a claim about a product that goes beyond the approved copy? If yes,
the answer is withheld entirely and the customer gets a handoff to a human
instead.

That is the right default. But it makes a particular mistake expensive.

If the check wrongly flags a *good* answer — a false alarm — the customer does
not get a slightly-worse answer. They get **no answer at all**, and a handoff to
support. A cautious check that cries wolf turns into a support ticket every
time.

So we need to know how often it is right when it fires. The measurement is built
and runs against the 50 deliberately hostile test questions.

**Where it stands after the full run — it is failing in both directions at
once.** This is the part worth your attention.

*Too aggressive:* the check withheld **51 of 125 answers**. Four out of every
ten customers would have got a handoff instead of an answer we had.

*Not aggressive enough:* separately, of the deliberately-hostile questions where
a reviewing model found genuinely non-compliant wording in the answer, the check
caught **none of them** — and all of those answers were delivered.

Those two findings sound contradictory and are not. We found the reason, and it
was our mistake rather than the model's: the check was only being shown dotFIT's
approved product copy, not the customer Q&A and podcast material the answer had
actually been written from. So it saw answers citing sources it could not read,
and called them unsupported — while having no basis at all to judge the ones
that mattered. One of the withheld answers was a single sentence about how many
carbohydrates are in an apple.

**That has been fixed** (10 September) and is being re-measured. So the numbers
above are the *before* picture, and we expect the false-alarm rate to fall
sharply. What we cannot predict yet is the other direction.

**The question for you, once the new numbers land:** the same one as before, but
now informed. Our engineering view is unchanged — the fix belongs in the check,
not in removing the withholding, because a customer seeing an unapproved claim
is worse than a customer being handed to support.

**This should be settled before the assistant is opened to real customers.** It
does **not** hold up the stakeholder preview — see task 7.

---

## 6c — Should we write test questions for the guide and podcast?

All 250 test questions come from customer email. But the practitioner reference
guide and the podcast make up about **72% of everything the assistant can
search**, and neither has a single test question.

We have partly covered this. There are now 120 automatic checks confirming the
assistant can *find* the right passage in the guide and the podcast. That
catches a search failure.

What it does not check is whether the assistant *answers well* from them,
because those checks have no expected answer to compare against.

**The question for you, later:** is it worth someone writing, say, 30–50 real
questions against the guide and podcast? That is a smaller version of task 1 and
would need the same expertise. It may be worth it, or those sources may be
context rather than primary answers and the current coverage may be enough.

**Two smaller gaps in the same area,** noted so they are not forgotten. Neither
is currently tested:

- the assistant telling a customer it is an AI at the start of a conversation
  (it does this — it is just not covered by an automatic test), and
- resistance to someone trying to talk it out of its instructions.

Both were left out because the test set has a fixed composition agreed earlier,
and we did not want to quietly change what was agreed to fit them in.

**A third gap, new as of 10 September.** The assistant is about to gain
follow-up questions — being able to answer "what about the chocolate one?"
using what was said earlier in the conversation. Every test we have asks a
single question in isolation, including the safety tests. That matters most for
refusals: "I'm 14" in one message and "how much creatine should I take?" three
messages later is currently two unrelated questions, and the second one has
nothing to refuse on.

The engineering side is handling the capability. What may need you is the same
question as above: a handful of realistic *multi-message* conversations that
should end in a refusal, so we can prove it works rather than assert it. If 6c
gets a yes, this belongs in the same sitting.
