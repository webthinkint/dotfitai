# Program guide audit against the deck

Every claim in `assistant/references/program-guide.md` checked against the deck
extraction in `pipeline-output/summaries/md/`, using the slides the guide cites
and the product slides for each dose. Out of scope: the screening rules
(section 2) and the overlap-check nutrient amounts (section 9), which the guide
says come from product labels, not the deck. Every slide number the guide cites
exists in the extraction.

The items below need a decision from the team. Items in A are places where the
guide contradicts itself; items in B are places where the deck contradicts
itself and the guide follows one side; items in C are places where the guide
says more than the deck.

Deck wording is paraphrased, not pasted.

## Already fixed in the guide

These followed directly from the deck and are applied:

- **First String calcium.** The guide said 2 servings have about 400 mg; the
  product slides (141–142, 161) give about 390–400 mg per serving. The guide now
  says per serving. The youth slide that says 2 servings supply 400 mg is B-1
  below.
- **Vitamin D.** Rule 4 said vitamin D depends on a blood test and is not
  automatic. The deck recommends it for nearly everyone (slides 67–74), so rule
  4 now says to give the daily dose and adjust it to a blood test when there is
  one.
- **Omega-3.** Rule 4 framed omega-3 as conditional and not automatic. The deck
  says most people fall short and the product is for nearly everyone who doesn't
  eat fatty fish regularly (slides 78–84, 123, 257–259, 266–268), while keeping
  the fish-intake dosing. Rule 4 now says it is for nearly everyone, dosed by
  fatty-fish intake.
- **SuperBlend contents** now include omega-3s, as the deck lists them (slides
  51–55, 126–127), with a note that fish oil is still added by fatty-fish intake,
  as the SuperBlend bundles do.
- **Shred bundles** now allow SuperBlend in place of Active MV (slide 233).
- **Under 18.** Section 7 said nothing else under 18, while the family bundle
  (slides 257–258) gives youth dotBARs and Electrolytes. Section 7 now allows
  those two.

---

## A. The guide contradicts itself

**A-1 · Caffeine-sensitive users: excluded, or start on a lower dose?** (guide 1.6, 4, 5)
Rule 6 says neither ThermAccel nor NO7 PreWorkout is for caffeine-sensitive
people. The dose tables then tell caffeine-sensitive users how to take both:
ThermAccel at 1 tablet twice a day, NO7 in half-scoops 90 minutes apart. The
deck has both positions for ThermAccel (product summary, slide 172: start lower;
trainer script, slide 180: not for them). For NO7 it has only the split-dose
advice. The assistant can give either answer. **Decide:** are caffeine-sensitive
people excluded from both, or allowed a lower starting dose?

## B. The deck contradicts itself (the guide follows one side)

**B-1 · First String calcium** (fixed in the guide)
The product slides (141–142, 161) give about 390–400 mg per serving. The youth
slide 238 says 2 servings supply 400 mg. The youth slide needs correcting.

**B-2 · Fat-loss levels come in three versions** (guide 5)
- Slide 201 text, which the guide follows: L1 multi + LeanMeal, usually + WLLS;
  L2 + ThermAccel or CarbRepel; L3 + AminoFormula.
- Slide 198 image: L2 adds WLLS **or** ThermAccel; L3 adds AminoFormula.
- Slide 199 collateral: L1 includes WLLS; L2 adds AminoFormula; L3 adds
  CarbRepel or ThermAccel.

**B-3 · CarbRepel dose** (guide 4 shred, 5)
The product slides say 2 tablets twice a day before the largest carb meals,
which the guide uses. The shred one-pager (slide 234) says 3 tablets a day
before meals.

**B-4 · Pre & Post Workout Formula numbers** (guide 3.4)
The product summary gives 21 g protein, 33–35 g carbs and about 250 kcal, which
the guide uses. The protein lineup (slide 161) gives 20 g, 32 g and 240 kcal.

**B-5 · Seniors level 2** (guide 6.2)
Slide 8 says level 2 adds creatine **and/or AminoFormula**. The bundle picture
(slide 12) adds creatine only and puts AminoFormula at level 3. The guide
follows the picture.

**B-6 · Muscle-gain level 3** (guide 4)
Slide 229 puts AminoFormula at level 2 and adds Extreme Creatine at level 3.
The slide 230 picture puts AminoFormula at level 3. The guide covers both: its
level 2 row notes the slide 229 version.

**B-7 · NO7 caffeine per serving**: slide 214 gives 3.75 g of caffeine in 2.5
scoops. That is almost certainly 375 mg. It doesn't affect the guide, which
gives no caffeine amount.

## C. The guide says more than the deck (confirm or remove)

**C-1 · Seniors level 3 adds CollagenComplex and Brain Health** (guide 6.2)
The slide 8 text mentions joint, connective-tissue and brain support at this
level. The bundle pictures (slides 12–13) show only AminoFormula added. The
guide maps the text to two named products. **Decide:** keep the two products at
level 3, or follow the bundle pictures?

**C-2 · Digestive Enzymes: "stop if nothing improves in about 30 days"** (guide 6.4)
Not in the deck. Keep it if it comes from the label or the PDSRG; otherwise
remove it.

**C-3 · Over 50 MV: "iron added only if a blood test shows a need"** (guide 3.1, 9, 10)
The deck says the product has no iron and why, but gives no rule about adding
it. It is conservative and probably fine; it only needs a source.
