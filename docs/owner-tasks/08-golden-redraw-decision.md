# Task 8 — Decide whether to refresh the 250 test questions

**Who:** project owner — one decision, about 15 minutes of reading
**Effort:** none beyond the decision; the alternatives are already prepared
**Blocking:** it gates the *start* of task 1 (the 2–3 day labeling pass), not
the preview and not engineering
**Tracked as:** open item 8 (the golden set)

## What this is

Task 1 will have a nutritionist and the support lead write down, for each of
250 real customer questions, what a good answer must contain. That work
becomes the permanent scorecard we re-run after every change to the assistant.

Those 250 were picked on 8 September from the corpus as it stood then. On
9 September the whole corpus was re-processed with the better answering model
(the capacity upgrade, task 5), and the pool of usable questions moved from
650 to 653 — and, more importantly, the re-processing cleaned up and re-tagged
the questions themselves. So there is a choice:

- **Keep** the 250 exactly as they are, or
- **Re-draw** them from the corpus as it stands today.

This is your call because it moves the target of 2–3 days of two people's
work. Once labeling starts, a re-draw throws that work away, which is why the
decision comes first. **Task 1 should not start until this is decided** —
either answer unblocks it.

## The decision, with the real numbers

We regenerated the full re-draw as a preview, so nothing below is a projection
— it is exactly what a re-draw would produce today. (It was produced by the
same deterministic command that builds the real set; re-running it always
gives these same numbers.)

**What stays the same:**

- **208 of the 250 questions stay** (83%).
- The spread of years barely moves (2023: 74 → 73 questions; 2026: 69 → 70;
  every other year unchanged).
- All 29 product families that are represented today are represented after —
  none drop out, none are added.
- The 42 incoming questions are genuinely different questions — none is a
  re-worded version of one of the 42 that leave.

**What changes:**

- **42 of the 250 questions (17%) are swapped** for different ones.
- The product mix shifts, and the direction is consistent: questions the old
  processing could not tie to any product ("general guidance") fall from
  79 to 63, while properly tagged ones rise — Active MV 53 → 65, Alln1
  SuperBlend 28 → 34, AminoFormula 25 → 28, Brain Health 1 → 3. The one
  family that loses ground beyond the untagged pile is Calcium Complex
  (12 → 5). This is the better model noticing product mentions the old one
  read past — the same effect that grew the read-each pile in task 2.
- Two questions in the **current** 250 (G-012, a 2023 gelatin-source question,
  and G-032, a 2023 celiac-disease question) now point at answers the
  re-processing marked as retired. A scorecard item aimed at a retired answer
  is a small flaw — 2 of 250 — but it is the kind that never repairs itself.

## What does not change either way

The 50 hostile "adversarial" questions, the 20 multi-turn conversation tests
and the 120 retrieval probes are hand-written or built by fixed rules — a
re-draw of the 250 does not touch them, and their recent results (safety
refusals 10 of 10, multi-turn 10 of 10) carry over as they are.

## The three options

**A. Keep the current 250.** Zero work, labeling can start immediately on the
existing worksheet. The scorecard stays a snapshot of the older processing:
slightly more untagged general questions than the corpus now warrants, and
the two retired-answer items above. Everything already measured on this set
(including yesterday's full test run) stays valid without re-running anything.

**B. Re-draw now.** The worksheet regenerates with the 42 new questions and
labeling starts on that file. Engineering re-measures the sample tier of the
test run — about ten minutes of machine time, already prepared. The scorecard
then matches the corpus as it exists today, with the better product tagging.

**C. Rule on task 3 first, then re-draw.** Task 3 (about 20 minutes of the
nutritionist's time, already written up) settles two pairs of near-identical
answers that are still waiting a ruling. Neither pair appears in either draw,
so the ruling cannot change the 250 directly — but a "same question" ruling
removes records from the pool, which can ripple into the draw. Concretely:
if both pairs are ruled *different questions*, the pool stands and the
preview above is exactly what you would get. If either is ruled *the same
question*, the preview regenerates (a minute) before labeling starts.

## What we would point out

Both A and B are defensible; nothing about the current 250 is broken. The
case for re-drawing is that the scorecard is permanent — it will be re-run
after every change for the life of the assistant — so it is worth the one-time
cost of having it reflect the corpus and the tagging quality you actually
have. The case for keeping is momentum: the set is valid, the measurement
pipeline is already running on it, and 83% of the work is identical either
way.

If you do re-draw, **option C costs 20 extra minutes and removes the one way
this decision can come back** (a later pool change forcing a second re-draw
after labeling has started). That is the sequencing we would suggest: task 3,
then this ruling, then task 1.

## Where the numbers came from

The preview set, including the re-drawn worksheet you could hand to the
labelers, sits in `pipeline/out/golden-preview/` on the build machine
(`worksheet.md` opens in any editor). It is a scratch copy — deciding
"keep" deletes nothing, deciding "re-draw" promotes a regenerated copy to
the real location. The comparison counts in this document were computed from
that preview against the current committed set on 10 September 2026; the
draw command is deterministic, so the same comparison will reproduce.
