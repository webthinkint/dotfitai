# Task 6 — Three choices that need a test run first

**Who:** project owner
**Effort:** reading a report and making three calls
**Blocking:** no — and not ready for you yet
**Tracked as:** open items 5, 12 and 15

This one is here so it is not a surprise later. **There is nothing to decide
today.** Each of these needs a full test run first, which is engineering work,
not yours. When those numbers exist, three questions come to you.

---

## 6a — Is the "semantic ranker" worth paying for?

Azure offers an optional extra step that re-orders search results using a
smarter, slower, chargeable model. We built the assistant so it can be switched
on or off, and deliberately did not guess which is better.

**Early signal, and please treat it as weak:** in a first small test of 12
questions, the assistant found the right source document **100% of the time
with the ranker off, and 83% with it on**. That points against paying for it.

But 12 questions is a smoke test, not evidence. It needs the full run before
anyone acts on it. Mentioned here only so the eventual result is not surprising.

**The question for you, later:** turn it on, or leave it off? A cost question
with a quality trade-off, once we know what the trade-off actually is.

---

## 6b — Is the claims safety check too aggressive?

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

**Where it stands:** in the first run, the check did not fire at all. So we have
no false alarms — and also no evidence it works. Zero out of zero is not a
reassuring result, it is an absent one. This needs the full run.

**The question for you, later:** if the check turns out to be trigger-happy, do
we soften it? Our engineering view is that the fix should be the wording of the
check itself, not removing the withholding — a customer seeing an unapproved
claim is worse than a customer being handed to support. But how much
false-alarm rate is acceptable is a business judgement.

**This one should be settled before customers use the assistant.**

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
