# Neal Spruce's Supplement Decision Tree

How Neal Spruce builds a supplement program for a person: what he asks, what he looks at, which supplements he picks, in what order, at what doses, and when he says no. This document is meant to work **on its own** as the knowledge base for an AI that recommends supplements the way Neal does.

---

## 0. Sources and conventions

Everything below comes from five episodes of the *SupBeast* podcast (Neal Spruce is the expert and his son Zane Spruce is the host). Citations use these short codes plus a timestamp:

| Code | Episode | Neal is |
|---|---|---|
| **[STACK]** | *What Neal Spruce Takes Every Day at 73 – A 40-Year Fitness Expert's Full Supplement Stack* | Speaker 1 |
| **[EP03]** | *Top 10 Missing Vitamins and Minerals – Ep 03* | Speaker 1 |
| **[EP07]** | *Are Supplements Worth It? – Ep 07* | Speaker 1 |
| **[EP08]** | *Supplement Protocols for Optimal Health – Ep 08* (the main "priority list" episode) | Speaker 1 |
| **[WORTH]** | *I Asked a Nutrition Expert Which Supplements Are Actually Worth It* | Speaker 2 |

Labels used in this document:
- **Rule** = something Neal states firmly and repeats.
- **Note** = my editorial note: an inconsistency, a likely transcription (ASR) error, or a spot where Neal's number differs from the official reference value. Keep these in mind before hard-coding a number.
- Neal owns/runs a nutrition R&D company that sells "practitioner" supplements (products mentioned by name include *Super Blend* (all-in-one powder), *Superior Antioxidant*, *Brain Health*, *Joint/Skin Collagen formula*, *MuscleDefender* (glutamine), *NO7* pre-workout). Neal says the podcast isn't about selling. This document describes his **ingredients and logic**, not brands.

---

## 1. Neal's core philosophy (the "why" behind every decision)

1. **A complete multivitamin/mineral is non-negotiable for every human, "womb to tomb."** Everyone "supplemented" in the womb (prenatal), on infant formula, and through fortified foods. Stopping makes no sense. He calls it "malpractice" for a health professional not to recommend one. [EP08 13:06–13:43, 14:22–14:34; WORTH 03:55–04:49; EP07 06:35–07:00]
2. **Food can't reliably cover micronutrients.** No one reaches the RDA of every nutrient from food. Soil mineral content varies (selenium and zinc are missing in some regions). Cooking and absorption (phytates, oxalates) lower what you actually get. You'd need about 20,000–30,000 kcal a day, or unpleasant foods like organ meat, to hit everything. Supplements are "isolated nutrition": the nutrient without the calories. [EP08 11:10–12:59; EP03 53:55–55:08; EP07 03:25–06:19]
3. **"Silent hunger."** You can't feel an insufficiency. The body **triages** scarce nutrients to short-term survival at the cost of long-term health (bone, cardiovascular, cognition). The damage shows up decades later as osteoporosis, dementia, or cardiovascular disease. [EP03 27:21–28:53, 36:41–38:47; EP07 31:19–31:41]
4. **Nutrients don't work in a vacuum.** Taking a single vitamin or mineral (vitamin D alone, magnesium alone) without the full matrix is a mistake. Vitamin D needs vitamin K, magnesium, and the rest. Any goal supplement (creatine, ashwagandha) works better on top of a complete multi. [EP08 06:04–10:12, 51:27–51:57, 44:39–44:59; EP03 05:23–06:51]
5. **No self-prescribing.** Don't read about a nutrient's functions online and "self-vitamin." Condition-specific extras need a legitimate recommendation from a qualified professional. [EP08 02:25–02:41; EP03 05:23–05:45, 08:05–09:28, 52:46–53:15]
6. **Everything has a bell curve**: benefit rises, then plateaus, then becomes toxic. Minerals have a **tight window** between too little and too much. That's why he prefers minerals to come through a well-dosed multi rather than stacking separate products. [EP03 07:24–08:00, 08:42–08:51; EP08 16:39–16:50; EP07 44:05–44:31]
7. **Be "calorically efficient"**: more nutrition, fewer calories. [EP07 09:40–09:54; WORTH 07:01–07:13]
8. **Prevention, not treatment.** Mega-dosing after you're sick doesn't fix things. The goal is to stay active and independent through every decade. [EP08 05:31–05:57, 20:48–21:01]
9. **Supplements fill gaps and don't replace food.** Eat traditional foods, with protein from varied sources and some fruit and vegetables at every meal. A junk diet plus a multi still misses phytochemicals and gut benefits. Neal also says people who take a multi tend to eat better. [EP08 73:53–77:58; EP07 04:59–05:06]
10. **Baseline first, then goal, then extras.** "Start with that, wait 60 to 90 days, and if you want to add something, add something." [EP08 45:04–45:10]
11. **Dosage and form have to match clinical evidence.** A product without disclosed per-ingredient amounts is a "non-starter." [STACK 36:16–38:52; EP08 40:41–42:36; EP07 45:02–48:02; WORTH 14:40–15:59]

---

## 2. What Neal looks at (intake variables)

These are the inputs that change his recommendations. An AI should collect them before recommending anything.

| Variable | Why it matters / what it changes | Source |
|---|---|---|
| **Goal**: general health / longevity, muscle gain, fat loss, athletic performance, aging well | "Everything is definitely related to your goal." After the baseline, the priority list branches by goal. | EP08 01:43–01:51, 50:53–52:27; EP07 09:39–09:43 |
| **Age** | ≥ late 20s–30s: CoQ10/ALA decline. ≥ 40: collagen. ≥ 45: brain-health formula. ≥ 60: double the brain-health dose. Older adults: EAAs (anabolic resistance), creatine as an anti-aging supplement. Under 18: no creatine (his business policy). | EP08 29:18–31:57, 35:34–37:13; STACK 11:43–12:06, 28:49 |
| **Training status**: novice vs. experienced/plateaued | A novice gets no creatine until progress stalls. | EP08 52:29–52:52 |
| **Athlete level**: recreational, competitive, elite, ultra-endurance | Athletes get the antioxidant formula and collagen early, higher vitamin D and C targets, 5–6 omega-3 capsules/day. Glutamine only for intense/ultra-endurance athletes. | EP08 31:57–32:15, 33:38–34:00, 55:30–56:33; WORTH 16:26–16:38 |
| **Fish intake** (servings of fatty fish per week) | < 3–4 servings/week → omega-3 supplement. | EP08 15:10–15:21 |
| **Dairy / calcium intake** | < 3–4 dairy servings/day (or no fortified alternatives) → calcium supplement (with K2 + Mg + D). | EP08 16:13–17:25 |
| **Diet pattern**: vegan/plant-based, avoids red meat, restrictive diets that drop whole food groups | Plant protein powder instead of whey; EAAs to fortify plant meals; iron risk (blood test first); vitamin A absorption from plants is poor. | EP08 18:58–19:10; WORTH 12:16–12:34; EP03 04:32–04:45, 33:52–34:29 |
| **Fruit/vegetable intake** (target 5–9 servings/day) | Low intake → carotenoid antioxidants and pre/probiotic (fiber) matter more. | EP08 22:18–22:27, 24:50–25:00; STACK 14:29–15:04 |
| **Fiber intake** | Average ~10 g/day vs. a target of 25 g (women) and 28–30 g (men). Increase slowly over ~6 weeks. | EP08 26:31–27:03; STACK 20:15–20:45 |
| **Protein intake vs. target** (1 g per lb of **lean body mass**) | Almost nobody hits it with food → protein powder. | EP08 18:18–19:31; WORTH 05:54–07:27 |
| **Body weight** | Creatine dose (5 g is enough under ~200 lb; Neal takes 7.5 g at 175 lb). | EP08 59:58–60:05; STACK 09:03–09:10 |
| **Body fat / degree of overweight** | Very overweight → liver-support lipotropics (NAC, milk thistle, choline) first. | EP08 60:59–62:08 |
| **Stimulant tolerance** | If sensitive: no caffeine-based pre-workouts or thermogenics; use the non-stim option. | EP08 56:54–58:00, 62:09–62:17; EP07 12:00–12:21 |
| **Medical history**: liver disease, cardiovascular events, other conditions; **medications** | His company uses a medical questionnaire. Contraindications cut the program back to "a protein and a multi." Tell your doctor what you take. | EP07 12:25–13:27 |
| **Statin use** | Statins deplete CoQ10 → CoQ10 is especially relevant. | EP08 30:52–31:23 |
| **Acid-reducer use (e.g., omeprazole), dairy-free for medical reasons** | Calcium supplement with K2 (Zane's case, endorsed by Neal). | EP03 40:00–40:29; EP08 73:13–73:28 |
| **Sex / menopausal status** | Women, especially female athletes and premenopausal women, are at risk of low iron. Neal still wants ~18 mg/day iron from some source after menopause. | EP03 33:42–34:53 |
| **Pregnancy / conception** | Prenatal multi (folate prevents neural tube defects). | EP03 22:04–22:54 |
| **Children** | Chewable or powdered multi, never gummies. | WORTH 04:51–05:48; EP07 06:45–07:00 |
| **Pill-swallowing ability** | About 30% of people can't or won't swallow pills → powdered multi / all-in-one. | EP08 39:51–40:02 |
| **GI sensitivity** | Increase fiber/prebiotics slowly (~6 weeks). Probiotics help people who can't tolerate fiber. A sensitive stomach may need a split or half dose of an all-in-one powder. | EP08 26:44–27:00, 71:38–71:50; STACK 20:15–20:51 |
| **Stress level** | Chronically stressed despite a solid baseline → ashwagandha trial. | EP08 44:00–44:36; WORTH 27:33–28:49 |
| **Environment**: heavy pollution, industrial exposure | Adds to the case for the non-vitamin antioxidant formula. | WORTH 25:10–26:38 |
| **Joint pain / injury / surgery** | Collagen peptide formula immediately (athletes after surgery such as an ACL repair). | EP08 33:31–34:00 |
| **Season / immune status** | Cold/flu season or a frail immune system → vitamin C up to 1,000–1,500 mg. Short-term zinc. Glutamine. | STACK 32:14–32:53; WORTH 12:50–14:34, 28:49–29:37 |
| **Budget** | Sets how far down the priority list to go. Neal's advice is to budget for health first. | EP08 01:23–01:38, 20:33–21:01, 50:58–51:09 |
| **Blood work**: vitamin D (25-OH-D), ferritin, kidney function | Vitamin D dosing to a blood target. Iron and potassium are never supplemented separately without a test. | STACK 33:47–35:20; EP03 32:53–34:29, 43:55–44:22 |
| **Current supplements and fortified foods** (protein powders, bars, calcium products) | Check for doubled-up vitamins and minerals that push intake past upper limits. | EP07 37:13–39:49 |

---

## 3. The master decision tree

### 3.1 Overview

```
START
│
├─ Screening (always first)
│   ├─ Medical contraindications / liver disease / cardiovascular events / meds?
│   │     → YES: restrict to complete multi + protein powder; refer to a qualified professional;
│   │            tell their doctor. Stop here unless cleared.
│   ├─ Stimulant sensitivity? → flag: no caffeine / thermogenics / stim pre-workouts
│   ├─ Under 18? → no creatine; kids get chewable/powder multi (never gummy)
│   └─ Pregnant / trying to conceive? → prenatal multi (folate)
│
├─ TIER 1: BASELINE (everyone, regardless of goal)
│   1. Complete multivitamin/mineral ............ NON-NEGOTIABLE
│   2. Omega-3 (marine EPA/DHA) ................. if < 3–4 fatty fish servings/week
│   3. Calcium (+K2, +Mg, +D) ................... if < 3–4 dairy servings/day (or equivalent)
│   4. Protein powder ........................... to reach 1 g / lb lean body mass / day
│      (whey; plant protein if vegan/dairy-free; unfortified)
│
├─ Wait 60–90 days on the baseline before adding extras (expect to feel the multi ~45 days)
│
└─ BRANCH BY GOAL
    ├─ A. General health / longevity  → Tier 2 health list (§5)
    ├─ B. Muscle gain / "get jacked"  → Muscle list (§6.1)
    ├─ C. Fat loss                    → Fat-loss list (§6.2)
    ├─ D. Athlete / performance       → Muscle + performance + athlete add-ons (§6.3)
    └─ E. Aging adult (40+/45+/60+)   → Tier 2 + age add-ons + creatine + EAAs (§7)
        (budget permitting, goal branches B–D come back to the Tier 2 health list afterwards)

OPTIONAL / CONDITIONAL add-ons (only after baseline): ashwagandha, turmeric, mushroom blend,
extra vitamin D (to blood target), extra vitamin C, short-term zinc, glutamine (§8)
```

### 3.2 Pseudocode (for implementation)

```python
def build_program(p):
    program, notes = [], []

    # --- Screening ---
    if p.has_contraindications or p.liver_disease or p.cardiovascular_events or p.on_interacting_meds:
        return ["complete multivitamin/mineral", "protein powder"], ["Refer to qualified professional; tell doctor."]
    stim_ok = not p.stimulant_sensitive
    if p.pregnant_or_conceiving:
        program.append("prenatal multivitamin (with folate)")
    if p.age < 18:
        notes.append("No creatine (<18). Chewable or powdered multi; never gummies.")

    # --- Tier 1 baseline ---
    program.append("complete multivitamin/mineral (≈20 under-consumed nutrients), with food, split AM/PM")
    if p.fatty_fish_servings_per_week < 3:          # Neal: 'if you're not eating three... all the way to four'
        program.append("marine omega-3 (EPA/DHA)")
    if p.dairy_servings_per_day < 3 and not p.gets_calcium_from_fortified_foods:
        program.append("calcium with vitamin K2, magnesium, vitamin D (keep total Ca ≈1,000–1,200 mg/day)")
    if p.protein_intake_g < p.lean_body_mass_lb * 1.0:
        program.append("unfortified protein powder: whey" if not (p.vegan or p.dairy_free) else "plant protein powder")

    notes.append("Stay on baseline 60–90 days before adding anything else.")

    # --- Goal branches ---
    if p.goal == "muscle":
        if p.training_status != "novice" or p.plateaued:
            program.append("creatine monohydrate 5 g/day (<200 lb) — optional 20 g/day x5d load")
        program.append("essential amino acids (8 EAAs, no tryptophan) around workouts")
        if p.intense_or_ultra_endurance or p.contest_prep:
            program.append("glutamine ~20 g/day split 3x (start ~6 weeks pre-competition)")
        if stim_ok:
            program.append("pre-workout: caffeine + creatine + beta-alanine (can double as creatine source)")
        else:
            program.append("non-stim pre-workout: beta-alanine + creatine + glutamine")

    if p.goal == "fat_loss":
        program.append("high-protein meal-replacement program")
        if p.very_overweight:
            program.append("liver lipotropics: N-acetylcysteine + milk thistle + choline")
        if stim_ok:
            program.append("thermogenic: caffeine + green tea + capsaicin")
        program.append("EAAs optional (anabolic signal with minimal calories)")
        if p.chronic_weight_cycler:
            notes.append("Drugs (GLP-1) may be next — but baseline + protein are mandatory to protect muscle.")

    if p.is_athlete:
        program += ["non-vitamin antioxidant formula (lutein, zeaxanthin, astaxanthin, lycopene, CoQ10, ALA) — immediately",
                    "collagen peptides + hyaluronic acid + chondroitin (can start in 20s)",
                    "omega-3 higher dose (≈5–6 capsules/day)",
                    "vitamin C ≥1,000 mg/day",
                    "vitamin D to blood 40–70 ng/mL"]

    if p.goal in ("health", "longevity") or p.budget_allows_more:
        program.append("non-vitamin antioxidants: lutein + zeaxanthin (+astaxanthin, lycopene)")
        if p.fruit_veg_servings < 5 or p.fiber_g < 25:
            program.append("prebiotic fiber (ramp slowly) + probiotic 20–100 billion CFU/day")
        else:
            program.append("probiotic 20–100 billion CFU/day")
        program.append("CoQ10 + alpha-lipoic acid")        # especially if age ≥ ~30 or on statins

    # --- Age add-ons ---
    if p.age >= 40 or p.joint_issues or p.injury_or_surgery:
        program.append("collagen peptides (nano-sized) + hyaluronic acid + chondroitin sulfate")
    if p.age >= 45:
        dose = "double dose" if p.age >= 60 else "standard dose"
        program.append(f"brain-health formula ({dose}): acetyl-L-carnitine, phosphatidylserine, ALA, extra B12")
    if p.age >= 40 or p.older_adult:
        program.append("creatine (anti-aging: muscle, recovery, brain)")
        program.append("EAAs (overcome anabolic resistance)")
    if p.statin_user:
        program.append("CoQ10 (statins deplete it)")

    # --- Conditional single nutrients ---
    if p.vitamin_d_ng_ml is not None and p.vitamin_d_ng_ml < 40:
        program.append("extra vitamin D3, titrate slowly to 40–70 ng/mL (min 30)")
    elif p.vitamin_d_ng_ml is None:
        notes.append("Ask doctor to add a vitamin D test to annual bloodwork.")
    if p.cold_season or p.frail_immune or p.under_chronic_stress:
        program.append("vitamin C total 1,000–1,500 mg/day (buffered)")
        notes.append("Short-term zinc during a cold is fine; not long-term.")
        program.append("glutamine during high-flu season / weak immunity")
    if p.chronic_stress and p.on_baseline_60_90_days:
        program.append("ashwagandha — 6-week trial, stop if no effect")
    if p.low_iron_suspected:
        notes.append("Do NOT add separate iron without a ferritin test.")

    notes.append("Check all products for overlapping vitamins/minerals (upper limits).")
    notes.append("Reject any product with undisclosed per-ingredient amounts.")
    return program, notes
```

---

## 4. Tier 1: The baseline (everyone)

Neal's "basics for every human being" [EP08 14:13–20:01]. The order below is his stated priority. Only #1 is non-negotiable. #2 and #3 depend on diet. #4 is "keep one in the pantry." When a new person comes into a gym with any goal, "this top part is exactly the same: multi, calcium if needed, omega if needed, and your protein, 1 gram per pound. That doesn't change." [EP08 47:10–47:22]

### 4.1 Complete multivitamin/mineral (#1, non-negotiable)

- **Who:** Everyone, from womb to tomb, including people who think they eat perfectly (Neal says studies show they still benefit). [EP08 76:37–76:56]
- **What "complete" means:** at least ~**18–22 (≈20) vitamins and minerals known to be potentially under-consumed**. Products with only 2–4 nutrients don't count. Neal criticizes older studies that defined a "multivitamin" as anything with ≥3 nutrients. [EP08 14:34–14:46, 79:53–81:44; EP03 05:33–05:45]
- **Nutrients of concern** (named in EP03 from the Dietary Guidelines), with the figures cited on the show for the share of the US population below needs: vitamin A (55%), vitamin C (48%), vitamin D (97%), vitamin E (86%), folate (75%), magnesium (68%), iron (34%, but those who are low are very low), calcium (73%), potassium (~100%, food only, see §9), and fiber. Zinc was mentioned as possibly on the list. [EP03 04:04–04:15, 51:12–52:03]
- **What a good multi should contain** (per Neal):
  - **Vitamin D:** at least 1,000 IU (25 mcg), ideally 1,000–2,000 IU. Neal's own multi has 1,200 IU. [EP08 06:52–07:03; WORTH 19:43–20:03; STACK 34:13]
  - **Vitamin C:** 600–1,000 mg. [WORTH 22:10–22:20; STACK 32:14]
  - **Magnesium:** 100–200 mg. [WORTH 17:38–17:44]
  - Vitamin K, zinc, selenium, B12, folate. [EP08 07:03–07:07, 08:52–09:01, 20:24–20:30]
  - **A little iron:** many multis leave it out for fear of exceeding the limit. Neal includes some. [EP03 34:57–35:22]
  - Vitamin A as a mix of preformed vitamin A and beta-carotene. [EP03 11:53–12:05]
- **Form:**
  - **Tablet preferred** over capsule, ideally enteric-coated / controlled-release so it releases in the small intestine. [EP08 39:22–39:48; EP07 34:51–35:07]
  - **Powder** (all-in-one) for the ~30% who can't swallow pills. [EP08 39:51–40:02]
  - **Chewables or powder for kids.** [WORTH 05:17–05:48]
  - **Never gummies**, for adults or kids. Neal says nutrients aren't stable in the gummy matrix and a complete multi can't fit in one. [WORTH 04:51–05:17]
  - Third-party tested. [EP03 05:33]
- **How to take:** **with food**, **split into one dose in the morning and one at night** ("full tissue saturation"; you act as your own time release). A one-a-day product is fine if taken with a meal, and morning vs. night doesn't matter. [EP07 34:25–36:47; STACK 10:27–11:14; EP08 38:10–39:30]
- **Cost framing:** about $12/month or 20–50 cents/day. "Find something you're spending $12 a month on and put it there." [EP08 14:53–15:05, 11:49–12:00; EP03 00:00, 54:45–55:08]
- **Timeline:** subtle benefits (sleep, appetite, energy, wanting to move) show up in about **45 days**. Correcting a diagnosed deficiency takes **4–6 months**. Neal's self-test: after ~6 months, stop for 1–2 weeks while eating the same way and notice the difference. [EP07 30:12–33:02]
- **Blood work rule:** don't bother with micronutrient blood work until you've taken a complete multi for **at least 6 months**. After that, the only likely addition is vitamin D (and possibly C). Most mineral blood tests aren't useful except iron (ferritin). [EP03 46:38–49:16]

### 4.2 Omega-3 (#2, conditional on fish intake)

- **Rule:** supplement if you eat fewer than **3–4 servings of fatty fish per week**. Neal says the old "2–4" guidance is outdated. [EP08 15:10–15:26]
  - **Note:** In [WORTH 16:08] he says "4 servings a **day** of fatty fish". That's almost certainly a slip for per week. In the same answer he says "almost everybody needs to take an omega-3," and in [EP07 43:03–43:13] "a fish oil if you don't eat fish."
- **Target:** **Omega-3 index ≈ 8%** (measurable). Americans have among the lowest levels. [EP08 15:26–15:47]
- **Why:** omega-3s are in every cell membrane and support cell communication. They **resolve inflammation** (as precursors to resolving compounds), which matters for aging, illness (avoiding runaway inflammation), injury, recovery, cardiovascular health, and cognition. [EP08 15:40–16:11; STACK 17:15–18:58; WORTH 16:17–17:01]
- **Form:** **marine omega-3 with EPA + DHA.** Flaxseed (plant ALA) omega-3 is the vegan-friendly option in his all-in-one, but Neal still adds a marine omega-3 on top. [STACK 03:27–03:38, 09:12–09:21, 17:03–17:12]
- **Dose:** "you can't overdo omega-3s." **Athletes take ~5–6 capsules a day** for recovery. [EP08 17:34–17:41; WORTH 16:26–16:38]
- **Note on Neal himself:** In [EP08 17:25–17:34] he says he eats fish 6–7×/week and only takes an omega-3 when traveling. In [STACK] (a later episode, age 73) he takes a marine omega-3 daily with breakfast. Treat that as his current practice. The decision rule for others stays tied to fish intake.

### 4.3 Calcium (#3, conditional on dairy/calcium intake)

- **Rule:** supplement if you don't get enough from food. As a rule of thumb, **3–4 servings of dairy a day** probably covers it. Calcium-fortified non-dairy foods also count, but you have to seek them out. [EP08 16:13–17:18]
- **Target:** total **~1,000–1,200 mg/day**. Don't exceed ~1,200–1,500 mg/day. The body uses ~1,000 mg/day (range 800 in infancy up to 1,300 in older age). Neal thinks the official 2,000–2,500 mg upper limit is too high. [EP08 16:25–16:35; EP03 36:41–37:11]
- **Practical split:** e.g., **~500 mg from a supplement and the rest from food.** [EP08 17:18–17:25]
- **Never calcium alone:** **pair it with vitamin K2** (which "chaperones" calcium to bone instead of arteries), **a little magnesium**, and vitamin D. Neal says calcium + D alone is "dumb" and claims his company was first to put K2 with calcium. [EP03 39:06–39:42, 40:22; EP07 37:54–38:14]
- **Why it matters:** blood calcium must stay constant, so if intake is low, the body pulls calcium from bone. That leads to early osteopenia/osteoporosis (around age 60–70). Excess calcium without K2 may deposit in the cardiovascular system. [EP03 37:11–39:42]
- **Kidney stones:** Neal calls the link "pretty much a myth" and says stones are mostly genetic. [EP03 39:44–40:00]
- **Special cases:** a dairy-free diet for medical reasons, or daily acid reducers such as omeprazole (which can weaken bone), means **calcium + K2**. [EP03 40:00–40:29]
- Calcium falls in the tight mineral window, so a separate calcium supplement must be counted against the multi's magnesium, zinc, and vitamin K. [EP07 37:47–38:39]

### 4.4 Protein powder (#4, "keep one in the pantry")

- **Target:** **1 g protein per lb of lean body mass per day**, split between meals. Neal calls the RDA "a joke" and says clinging to it "borders malpractice." Count only **complete** protein sources (seafood, chicken, lean meats, soy, some plant proteins), not "fortified waffles." [EP08 18:18–19:12; WORTH 05:54–06:45; STACK 04:15–04:20]
- **Why a powder:** hitting the target with food alone is too expensive and time-consuming. A powder is calorically efficient, portable, and cheaper per gram of protein. A **40–50 g shake** gets a big share out of the way ("all you need is another 75 or so with your meals"). Neal estimates it saves ~$30/month per family member compared with cooking the same protein. [EP08 18:35–19:51; WORTH 06:45–07:27]
- **Type:**
  - **Whey** is "the most superior protein in the world." [WORTH 00:11, 07:05]
  - **Plant protein** for vegans and people who avoid dairy. [EP08 18:58–19:10, 71:57–72:02]
- **Buy it unfortified.** If you already take a multi, choose a protein without added vitamins and minerals so you don't double up. Naturally occurring nutrients such as the calcium in whey are fine. [EP07 39:27–39:49]
- Taking it before and/or after a workout works well. Mixing creatine into it is Neal's habit. [WORTH 07:13–07:20; STACK 30:31]
- Artificial sweeteners and emulsifiers at permitted levels are fine in Neal's view, unless the person personally reacts to them. A natural line exists for people who want one. [EP07 16:51–22:53]

---

## 5. Tier 2: The general-health / longevity list (budget permitting)

After the baseline, for someone whose goal is health ("no budget restrictions... you should budget for your health"). Neal's order [EP08 20:33–35:37; summary at 34:00 and 63:43]:

| # | Supplement | Details |
|---|---|---|
| 5 | **Non-vitamin antioxidants: lutein + zeaxanthin** (in his formula also astaxanthin, lycopene, packaged with CoQ10 + ALA) | These carotenoids are structural parts of the eye (macular pigment optical density), skin, and brain, and they protect against UV. Neal argues they should be considered essential. They come from fruit and vegetables, which Americans eat ~1.5 servings/day of versus the ~9 needed (5–9). Neal takes them **even if eating 5–9 servings**, because food content and absorption are unreliable. Doses he mentions: **lutein ~6 mg, astaxanthin ~3–4 mg, lycopene ~10 mg/day** (lycopene matters especially for people who don't eat tomatoes). [EP08 21:01–24:17, 40:59–41:14; STACK 14:05–16:55; WORTH 23:50–26:52] |
| 6 | **Prebiotic + probiotic** (take together) | A prebiotic is fiber that feeds good bacteria. Fruit and vegetables are prebiotics, so enough of them means no prebiotic supplement is needed. Probiotics recolonize good bacteria, help heal the gut lining ("leaky gut"), and support immunity (~70% of the immune system is in the gut) and body composition. **Dose: 20–100 billion CFU/day.** **Fiber target: ≥25 g/day (women), 28–30 g (men); ramp up slowly over ~6 weeks.** Initial GI discomfort is expected. Probiotics help people who can't tolerate fiber. "Almost everybody can benefit. Just make sure you get the right ones." Benefits build slowly and aren't felt quickly. [EP08 24:38–28:52; STACK 19:03–20:51; WORTH 26:55–27:33; EP07 33:12–33:17] |
| 7 | **CoQ10 + alpha-lipoic acid (ALA)** | Both are needed to make ATP (electron transport chain) and both are strong antioxidants. ALA is unusual in being both water- and fat-soluble. Production and dietary extraction **decline from the late 20s/30s** onward. Supplementing restores "youthful levels" and energy. **Statin users**: statins deplete CoQ10. [EP08 28:56–31:57; STACK 15:21–15:46; WORTH 24:37–24:47] |
| 8 | **Collagen: joint/skin formula** | **Start at ~40** for a healthy person. **Athletes can start in their 20s.** Anyone with an **injury or surgery** starts **immediately** (e.g., after an ACL repair) and can keep going. Form matters: **nano-sized collagen peptides + hyaluronic acid + chondroitin sulfate**. Plain collagen protein powder is "garbage" for this, and it's also an incomplete protein (missing tryptophan; Neal also says cysteine), so it's not for muscle building. Evidence Neal cites: skin, mild joint discomfort, slowing joint wear, faster cartilage recovery in athletes. MSM is an anti-inflammatory, but collagen is his pick. [EP08 33:26–36:34; WORTH 07:29–09:38; STACK 09:21–09:51] |
| 9 | **Brain health formula** | **Start at ~45** "to stave off cognitive decline." **Double the dose at 60.** Ingredients: **acetyl-L-carnitine, phosphatidylserine, alpha-lipoic acid, extra B12 (beyond the multi).** Neal says after about 40–45 the brain stops getting enough of these nutrients from diet and the body's own production. Neal takes the double dose (4 servings), split AM/PM. Asked to choose between this and a mushroom blend, he picks this one "without a doubt." [EP08 36:36–37:13; STACK 03:48–04:04, 08:20–08:57, 11:43–12:06, 23:16–23:33] |

- For the average person, the antioxidants come **after** the multi: "Maybe you don't need those extra ones beyond your multi... get your multi done." [WORTH 24:49–25:10]
- **Athletes skip the queue:** they go on the antioxidant formula (lutein, zeaxanthin, ALA, CoQ10) **right away** (visual performance and hand-eye coordination; higher free-radical load from training), and on collagen early. [EP08 31:57–32:15; WORTH 25:10–25:24]
- **People exposed to heavy pollution or doing heavy training** should add the antioxidant formula. [WORTH 26:27–26:38]
- **Caveat:** the wrong antioxidants, or too high a dose, can **blunt training adaptation**, so doses have to be set correctly. [WORTH 25:28–26:06]
- Neal calls the whole Tier 1 + Tier 2 list "preventative": restoring things that decline with age so you recover from daily life. "If you want each decade of your life better, you do all that." [EP08 32:43–33:21, 36:13–36:21]

---

## 6. Goal branches (after the baseline)

### 6.1 Muscle gain ("I'm trying to get jacked")

Keep Tier 1. Tier 2 is optional ("if you have the money for that, great"), and the priority shifts to goal supplements. [EP08 50:53–51:16]

1. **Creatine** comes first for building muscle ("creatine is always going to be first").
   - **Novice exception:** a true beginner doesn't get creatine at first. "You just take baseline and you're going to grow muscle like crazy because you're new. As soon as you come to a plateau or progress goes slowly, bang, here's your creatine." [EP08 52:29–52:52, 54:53–54:56]
   - **Prerequisite:** "You don't get to touch it until you're taking a complete multi, your omega... the creatine ain't going to work without that." [EP08 51:27–51:57]
   - **Dose:** **5 g/day** (enough for people under ~200 lb). **Optional loading: 20 g/day for 5 days**, then **5 g/day** maintenance (elsewhere Neal says 5–10 g maintenance). Neal takes **7.5 g/day at ~175 lb**. [EP08 59:58–60:16; STACK 09:03–09:10, 28:29]
   - **Timing:** a single dose goes **after the workout**. On non-training days take it **with a meal**; the point is to keep levels up. It's easiest mixed into the protein shake (plant protein for vegans). [EP07 37:00–37:09; STACK 30:31]
   - A **pre-workout that contains creatine can serve as the creatine source** or the maintenance dose. [EP08 59:52–60:16]
   - Form: Neal doesn't favor any special form in these episodes. He names "creatine" and "one scoop of creatine," and warns against creatine sourced from China ("pretty dirty"). **No creatine gummies** ("not ready for prime time"; creatine isn't stable in the matrix). [EP07 10:16–10:21; WORTH 02:16–03:39]
   - Blood test note: tell patients that creatine causes a harmless false-positive creatinine reading, so they should stop it before a blood test. [STACK 30:01–30:31]
2. **Essential amino acids (EAAs): "protein stacking."**
   - Taken **before, during, and/or after** the workout, on top of the 1 g/lb daily protein.
   - Formula: **8 of the 9 EAAs, no tryptophan** (free-form tryptophan crosses into the brain and can make you sleepy; Neal calls formulas with all 9 "phony").
   - Neal says they bypass splanchnic (liver) extraction, go straight to muscle, and switch on mTOR ("feeding muscle, starving fat"), with ~10 kcal per serving.
   - Effects: **noticeably less soreness**. Measurable gains take **months**. [EP08 53:35–55:29; STACK 24:08–27:41; WORTH 11:29–12:49]
3. **Glutamine**, **only for intense athletes** ("you don't need it otherwise").
   - Who: ultra-endurance athletes, and bodybuilders in prep (long calorie restriction plus rising activity, which causes exercise-induced immune suppression).
   - **Protocol: ~20 g/day split 3× per day, starting ~6 weeks before competition.**
   - Why: glutamine fuels gut cells and immune cells, and the glutamine pool (made mainly in muscle) gets drained under stress. [EP08 55:30–56:33; WORTH 12:50–14:34]
4. **Pre-workout**, if stimulants are tolerated. A good pre-workout contains the "**big three**" evidence-based performance enhancers: **caffeine + creatine + beta-alanine** (beta-alanine buffers lactate and delays fatigue; creatine adds ATP and lactate buffering; caffeine works for everything). For stimulant-sensitive people there is a **non-stim version: beta-alanine + creatine + glutamine.** Neal uses a pre-workout occasionally when he feels flat (it's not a staple). [EP08 56:33–58:00; STACK 05:18–05:44; EP07 27:35–28:33]

Summary of Neal's order: *multi → omega-3 → calcium if needed → protein → creatine (skip it for novices until they plateau) → EAAs → glutamine (intense athletes only) → pre-workout (it can come first because it contains creatine)* [EP08 59:39–60:16]

### 6.2 Fat loss (Neal means fat loss, not just weight loss)

Neal's aim is to lose fat while keeping or even adding muscle. He says conventional dieting loses 20–30% lean tissue and weight-loss drugs lose "a shitload of lean body mass." [EP08 60:31–60:59]

1. **Baseline** (multi, omega-3, calcium if needed, protein). The antioxidant tier is **skipped unless affordable.** [EP08 47:22–47:37, 60:23–60:59]
2. **High-protein meal-replacement program.** [EP08 53:14–53:22]
3. **If very overweight: liver-support "lipotropic factors": N-acetylcysteine (NAC) + milk thistle + choline.** Neal's reasoning: obesity leads to fatty liver, the liver ("the CPU of the body") then handles carbs and fats badly, and these antioxidants target liver fat. "I would always start out with that." [EP08 47:33–47:46, 60:59–62:08, 63:28–63:39]
   - Neal also says **"detox" products don't work**. The kidneys and liver do that job. See §10.
4. **Thermogenic**, if stimulants are tolerated: **capsaicin + green tea + caffeine**. Neal says it raises metabolic rate 5–10% (~200 kcal/day for an average person) and increases spontaneous movement (NEAT). [EP08 62:09–62:56]
5. **Beyond that there's nothing more to add.** For chronic weight cyclers who can't lose weight, **drugs (GLP-1s)** are the next step, but the **baseline + protein remain mandatory** to avoid heavy muscle loss. [EP08 62:57–63:11]
6. **EAAs** also help dieters ("a higher anabolic bump with less calories"). [WORTH 12:39–12:49; EP08 54:38–54:49]
7. Afterwards, go back and add the Tier 2 health list (lutein/zeaxanthin, pre/probiotic, CoQ10, collagen when age-appropriate). [EP08 63:43–63:51]

Neal also says L-carnitine isn't the limiting factor in fat burning, so it only helps people with inborn errors of carnitine synthesis. He hadn't heard of people injecting it. [EP08 58:00–59:32]

### 6.3 Athletes / performance

Everything in §6.1, plus:
- **Antioxidant formula (lutein, zeaxanthin, astaxanthin, CoQ10, ALA) from day one** for visual performance, hand-eye coordination, and higher oxidative stress, **dosed so it doesn't blunt adaptation**. [EP08 31:57–32:15; WORTH 25:10–26:06]
- **Collagen peptide formula** early (20s+) and **immediately after any injury or surgery**. [EP08 33:38–34:00, 36:24–36:34; WORTH 09:09–09:33]
- **Omega-3 ~5–6 capsules/day.** [WORTH 16:26–16:38]
- **Vitamin D blood level 40–70 ng/mL** ("we want our athletes at 70"; athletes are often covered up and indoors). Athletes usually take ~2,000 IU+ (Zane takes 5,000 IU). Neal says this means faster fracture healing and fewer injuries. [STACK 33:47–34:13; EP03 16:53–17:21; WORTH 20:48–21:15]
- **Vitamin C ≥1,000 mg/day** for every athlete. [EP03 26:09–26:20]
- **Iron:** female athletes are often low, so **test ferritin** before supplementing. [EP03 33:42–34:06]
- **BCAAs:** only for **endurance / ultra-endurance athletes**, at **very high doses**, to delay central fatigue (blocking tryptophan's entry into the brain). Otherwise, EAAs have replaced BCAAs for recovery. Neal's example of a properly dosed BCAA product: **~4 g leucine, ~1.7 g isoleucine, ~1.7–1.9 g valine.** [WORTH 09:40–11:24; EP07 13:59–14:26]
- **Ashwagandha** is popular with elite athletes for stress and as an adaptogen. [STACK 21:11–21:36; WORTH 28:06–28:18]
- **Glutamine** for ultra-endurance athletes and people in contest prep (see §6.1).
- **Carbohydrate loading** for endurance athletes is also on his list of evidence-based needle-movers. [EP07 28:15–28:23]
- **Certification:** pro and college teams require **NSF Certified for Sport**. [EP07 02:44–03:02, 48:02–48:54]

---

## 7. Age-based layering (aging adults)

"Our bodies were never programmed to live past 30," so Neal adds back what declines with age. [EP08 70:41–71:04]

| Age | What changes | Source |
|---|---|---|
| All ages | Complete multi (womb to tomb) + baseline | EP08 |
| Late 20s–30s+ | CoQ10 + ALA production and extraction decline → consider supplementing | EP08 29:32–29:46, 31:33–31:46; WORTH 24:37–24:47 |
| 30s–40s+ | Recovery declines → creatine, EAAs | STACK 29:17–29:53 |
| **40** | **Collagen joint/skin formula** (athletes earlier) | EP08 35:34–36:34 |
| **45** | **Brain health formula** (ALCAR, phosphatidylserine, ALA, B12) | EP08 36:49–37:13; STACK 08:48–08:54, 11:53 |
| **60** | **Double the brain health dose** "and then you're good for the rest of your life" | STACK 12:00–12:06 |
| Older adults (generally) | **EAAs** to overcome **anabolic resistance** (use during/after workouts; helpful even for non-exercisers). **Creatine** as an anti-aging supplement (muscle retention, recovery, brain), which **all his 70-something CEO clients take** ("I don't care who you are, you're on creatine"). Higher protein matters more with age. | STACK 25:24–27:41, 29:17–30:31; EP08 64:12–64:19, 71:04–71:24; WORTH 11:34–12:13 |

- **Note on creatine for the non-training general population:** in [EP08 52:52–53:14] Neal seems to correct himself. For the general-health priority list, creatine isn't a top item ("those other things are more important than creatine at this point... creatine, that's for muscle building"). But for aging clients with no budget limits, and in [STACK]/[WORTH], he recommends it broadly ("for most people... there's no reason not to"; "especially if you're older, you should be using it regularly"). **Suggested implementation:** creatine is a **goal item** for muscle and performance, **recommended** for adults ~40+ / older adults, and **optional** for young, non-training general-health users.

---

## 8. Conditional and optional add-ons (not on the core list)

"That's not something you put on your list. It's something you add." Only **after the baseline has been in place for 60–90 days**. [EP08 44:00–45:10]

| Supplement | When / for whom | Dose / protocol | Neal's stance | Source |
|---|---|---|---|---|
| **Ashwagandha** (adaptogen) | Person feels **stressed all the time** despite doing the basics; athletes (competition stress) | **Trial ~6 weeks; stop if no effect.** Neal noticed changes after ~45 days (stress, sleep, clarity) | "Worth a try." Don't skip the multi and jump straight to ashwagandha. With a multi it "worked that much better," and the multi alone might have been enough. Neal says it lowers cortisol. | EP08 44:00–45:04, 67:32–68:43; WORTH 27:33–28:49; STACK 21:11–21:36 |
| **Turmeric** | Inflammation / joint discomfort (osteoarthritis) | Clinical dose (not specified) | Neal's anecdote: his arthritic wrists improved markedly after he started it about 5 years ago | STACK 21:36–22:39; EP08 44:00–44:17 |
| **Mushroom blend** (lion's mane, reishi) | Cognition, some skin benefit | **At least ~1,200 mg** (he criticizes 300 mg products) | "Down the list." If choosing, pick the brain-health formula instead | STACK 22:39–23:59, 37:36; EP08 35:53–36:05 |
| **Extra vitamin D3** | Blood 25-OH-D below target; many people need it beyond the multi | Increase **slowly** until blood reaches **40–70 ng/mL** (minimum 30, ceiling ~100). Most need **≥2,000 IU/day total**, some 3,000–4,000 IU | Neal: "almost everybody"; "everybody should have some vitamin D on their shelf." **Get vitamin D added to your annual blood test.** Neal's level is ~45 ng/mL on the 1,200 IU in his multi, so he takes no extra | STACK 33:28–35:20; WORTH 19:41–21:28; EP03 16:53–17:21 |
| **Extra vitamin C** | Cold season, frail immune system, heavy stress (military), athletes | General: **600–1,000 mg/day total** (also "500–1,000"). Stressed/cold season: **1,000–1,500 mg**. **Buffered** form avoids stomach upset. Neal's own **3,000 mg/day** is "strictly anecdotal," a Linus Pauling habit since age 24, and **not** a recommendation | Shortens the duration of colds but doesn't prevent them | STACK 04:26–05:09, 31:50–33:25; WORTH 21:28–23:44; EP03 24:56–26:47 |
| **Extra vitamin E** | Extra antioxidant protection | Neal is fine with **200–300 IU**. Going toward the 1,000 mg UL means self-medicating, so see a specialist | — | EP03 17:43–19:29 |
| **Zinc (extra)** | **Only short-term during a cold** (some evidence it shortens duration) | — | "Taking an extra zinc on a regular basis makes zero sense." The multi covers it; too much causes taste loss and neurological problems | WORTH 28:49–29:37 |
| **Magnesium (extra)** | **Only with a diagnosed deficiency, insufficiency, or malabsorption** | Food (~200 mg) + multi (100–200 mg) + whatever is in the calcium product covers it. Supplemental magnesium has an upper limit (see note) | "Conditional." Hype for sleep: it only works if you were deficient. Neal cites a meta-analysis showing no benefit in people who weren't insufficient. Good forms: amino acid chelates (e.g., L-threonate), citrate, malate. Oxide is poorer but OK in a mix | WORTH 17:05–19:41; EP03 29:28–32:10; EP08 09:28–10:12 |
| **Glutamine** | Ultra-endurance, contest prep, sickness or injury, weak immune system, **high flu season** | ~20 g/day split 3× (athletes) | "Have it in your pantry" for those situations | WORTH 12:50–14:34; EP08 55:30–56:33 |
| **Folate (separate)** | Genetic folate malabsorption (e.g., a specialist finds it) | Specialist-directed | Otherwise the multi covers it. Neal warns against too much folate from fortification | EP03 05:45–06:16, 23:24–23:34 |
| **Greens powders / all-in-ones** | Convenience, or people who can't swallow pills | Only if **every ingredient is listed with its amount** and doses match clinical evidence | "Yes, if you get the right one." Most aren't complete multis (Neal says AG1 lacks vitamins D and K). A real greens product would have ~6–7 g of fiber. Neal's all-in-one includes 2 real vegetable servings | EP08 36:36–46:00, 66:56–67:24; WORTH 14:37–16:06; EP07 56:27–57:08 |
| **MSM** | Joint claims | — | Neal calls it an anti-inflammatory but prefers collagen | EP08 34:15–34:23 |
| **Sleep supplements** | Taken before bed | — | Mentioned only in passing | EP07 36:53–36:56 |

**Note on the magnesium upper limit:** Neal gives **350 mg** in [EP03 29:54–30:24] ("350 best from supplements only") and **450 mg** in [WORTH 17:21–17:55]. The official US tolerable upper intake for **supplemental** magnesium in adults is **350 mg/day**. Use 350 mg for implementation.

**Note on the vitamin C upper limit:** [EP03 26:35–26:47] transcribes it as "1000 milligrams." The official adult UL is **2,000 mg/day**, set because of GI upset, which matches Neal's explanation. It's likely an ASR or speaking error.

**Note on vitamin D units:** 1,000 IU = 25 mcg, so 2,000 IU = 50 mcg and 4,000 IU = 100 mcg (the official UL, which Neal calls "a joke"). The RDA is 600–800 IU (15–20 mcg), which Neal calls "a joke" and "way too low." In [STACK 34:13] "1,200 IU... like 35 micrograms" is off (1,200 IU = 30 mcg).

---

## 9. Hard "don'ts" and safety rules

1. **Iron:** **never supplement iron separately without a blood test (ferritin).** Iron overload is dangerous, and the window between need (8–18 mg) and the UL (45 mg) is small. At-risk groups: women (premenopausal), female athletes, people who avoid meat. Good sources are red/organ/muscle meats; spinach iron is poorly absorbed. [EP03 32:49–35:45]
2. **Potassium:** **don't supplement it.** Get it from food (vegetables); the AI is 4.7 g, and ~3,500 mg is a practical target. Supplementing is risky for the kidneys and fluid balance and needs a blood test and kidney check. Multis don't contain it, and single tablets are limited to 99 mg. The main reason it matters is offsetting excess sodium. [EP03 40:29–46:20]
3. **Don't stack separate single minerals or vitamins** (zinc, selenium, folate, vitamin E, etc.) without clearance from a **qualified** professional ("medical professionals don't know anything about nutrition, so make sure they're qualified"). [EP03 52:32–53:15]
4. **Check for doubling:** multi + calcium product + fortified protein powder + fortified bars can push zinc, magnesium, vitamin K, etc. **over the upper limit**. Choose unfortified protein, and make the multi the only broad vitamin/mineral source. [EP07 37:13–39:49]
5. **Don't chase label benefit claims** ("hair, skin, nails" + "joint" + "anti-aging" max-strength products). It mostly wastes money and can push totals over limits. [EP07 39:49–41:56; EP08 10:15–10:31]
6. **Vitamin A:** too much **preformed** vitamin A is harmful, especially for reproduction. Beta-carotene has no UL (the body converts only what it needs). Best sources: animal proteins. [EP03 09:43–13:09, 19:30–19:46]
7. **Stimulants:** follow label doses. Anyone bothered by stimulants shouldn't take them and should stop immediately if symptoms appear. [EP07 11:58–12:21]
8. **Medical screening:** liver disease, cardiovascular events, unusual medications → scratch everything except simple items (protein, multi). **Tell your doctor what you take.** Neal says it would be very unusual for anything to rule out a multi, calcium, or fish oil. [EP07 12:25–13:27]
9. **No gummies** (multi, creatine, anything). [WORTH 02:23–05:17]
10. **No "detox" / cleanse products.** [EP08 47:37–50:25]
11. **Don't take a single nutrient in place of a multi** (vitamin D alone, magnesium alone). [EP08 06:04–09:01]
12. **Tryptophan should not be in an EAA formula.** [WORTH 11:40–12:02; EP08 53:54–54:11]
13. **Don't expect to feel a multi like coffee.** Benefits are subtle and take ~45 days. [EP07 30:12–33:02]
14. **Mega-dosing after you're already sick doesn't work.** It's prevention. [EP08 05:31–05:57]

---

## 10. "Detox" the Neal way

Instead of a detox product, Neal's 30–60 day protocol: [EP08 49:00–50:25]
- Complete multivitamin + fish oil (+ the antioxidants)
- Protein at 1 g/lb lean body mass
- **No alcohol for 30–60 days** (a little later is fine)
- No smoking
- Moderate exercise (not overdoing it)
- Don't overeat and manage weight. Follow a Mediterranean-style diet.
- Caffeine is fine in moderation (not 10 cups a day)
- Keep the supplements after the 30–60 days.

---

## 11. Product quality filter (applies to every recommendation)

Neal's checklist, in order: [EP07 13:27–16:21, 45:02–50:51; STACK 36:16–38:52; EP08 40:41–42:36, 83:16–83:29]

1. **Full disclosure (non-starter test):** every ingredient must be listed **with its exact amount**. "Proprietary blend" with only a total weight means don't buy it. Quick check: if the clinical doses of just two or three listed ingredients would already exceed the blend total, it's "pixie dust" or "window dressing." (A proprietary blend that discloses amounts is fine.)
2. **Clinical dose and form:** each amount should match a dose and form used in a published study. Call the company and ask for documentation. Most mass-market companies can't provide it ("it's proprietary").
3. **Third-party testing:** **NSF Certified for Sport** (required by pro and college teams), **Informed Choice** (Neal says it's just as good and cheaper), **ConsumerLab**. **USP** means manufacturing standards were followed but not necessarily third-party tested. Third-party testing only proves the product is **clean and the label accurate. It doesn't prove the product works.**
4. **Company heritage:** a long track record, and a practitioner-grade company rather than one competing on price. "If you're buying on price, you're screwed." Mass-market under-formulation is Neal's biggest concern; spiking with drugs is rarer but happens. Neal says his company sources raw materials in the US.
5. **Delivery form:** tablets for controlled release (multi), capsules and powders for fast action. Collagen must be nano-sized peptides. No gummies.
6. **Synthetic vs. natural:** synthetic isn't worse (e.g., synthetic vitamin C = ascorbic acid, possibly purer). Creatine from China is "dirty."
7. **Don't trust influencers** ("people who know nothing about nutrition selling nutrition to people who know nothing about nutrition"). Don't trust pharmacists or store clerks for supplement advice either. Look for real experts or practitioners.

---

## 12. Timing and administration rules

| Item | How / when | Source |
|---|---|---|
| Multivitamin/mineral | With food. Ideally ½ dose AM + ½ dose PM (tissue saturation). A one-a-day is fine with any meal | EP07 34:25–36:47; STACK 10:27–11:14 |
| All-in-one powder | Mix into a protein shake, or drink with a meal. Split AM/PM. A half dose can suit sensitive stomachs, with pills covering the rest | STACK 07:26–11:21; EP08 71:38–73:05 |
| Protein powder | As a meal, and/or before/after workouts | WORTH 07:13 |
| Creatine | After the workout (single dose). With a meal on off-days. Mixed into the protein shake | EP07 37:00–37:09; STACK 30:31 |
| EAAs | Before, during, and/or after the workout. Neal breaks his overnight fast with them | EP08 53:42; STACK 06:28–07:19; WORTH 12:13 |
| Glutamine | Split 3×/day | EP08 56:19–56:27 |
| Weight-loss tablets (green tea extract, milk thistle, etc.) | Spread evenly through the day (e.g., 3×/day) to keep blood levels steady (half-life) | EP07 35:19–36:05 |
| Capsules / powders | Fast release (good when you want quick action) | EP07 34:51–35:07 |
| Sleep supplements | Before bed | EP07 36:53–36:56 |
| Fiber / prebiotics | Increase slowly over ~6 weeks | EP08 26:54–27:00 |
| Adding new supplements | After 60–90 days on the baseline | EP08 45:04–45:10 |
| Evaluating a multi | ~45 days to feel it. 4–6 months to fix a deficiency. Blood work after ≥6 months on a complete multi | EP07 30:12–33:02; EP03 46:38–49:16 |

---

## 13. Worked examples

### 13.1 Neal himself (age 72–73, ~175 lb, former pro bodybuilder, trains early each morning, eats fish often, vitamin D ~45 ng/mL) [STACK; EP08 66:51–73:44]

**Stack:**
- **All-in-one "Super Blend" powder** containing: complete multivitamin/mineral (including high D, 1,000 mg C), non-vitamin antioxidants (lutein, zeaxanthin, astaxanthin, lycopene), CoQ10 + ALA, mushroom blend, ashwagandha, turmeric, prebiotic + probiotic, flaxseed omega-3, and 2 servings of vegetables
- **Marine omega-3 (EPA/DHA)**
- **Joint/skin collagen formula** (nano collagen peptide + hyaluronic acid + chondroitin sulfate)
- **Brain health formula** (ALCAR, phosphatidylserine, ALA, B12), **double dose** because he's over 60
- **EAA formula**
- **Whey protein** (~40 g per shake). Total daily protein ~150–170 g
- **Creatine 7.5 g/day** (in the protein and/or with the aminos)
- **Extra vitamin C to 3 g/day total** (personal habit, not a recommendation)
- **Pre-workout occasionally** (caffeine, vasodilator, creatine) when he feels flat
- **No extra vitamin D** (his blood level is fine on the multi)

**Daily timing:**
1. **4:00 AM:** coffee on an empty stomach.
2. **Break the fast with EAAs:** 1.5–2 scoops. (Neal describes this as "about 40 grams of protein, if you will," but also says a serving is ~12 g with ~10 kcal. Treat this as roughly 1.5–2 servings of EAAs.)
3. **Workout.** EAAs during the workout, plus one scoop right after (with creatine).
4. **Long walk.**
5. **Shake (~8 AM):** whey protein + ½ dose of the all-in-one + creatine + fruit (2 servings of vegetables and 2 of fruit done by 8 AM).
6. **With breakfast:** brain health (first half), marine omega-3, collagen formula.
7. **Evening, right after dinner / before bed:** second ½ dose of the all-in-one (also curbs sweet cravings), second half of brain health.
8. Food: 5–7 servings of fruit and vegetables a day. Protein is the only thing he tracks.

He says the routine takes about 2 minutes a day.

### 13.2 Zane (young adult, trains hard, autoimmune condition, no dairy, takes omeprazole, sensitive stomach) [EP08 71:38–73:48; EP03 40:00–40:29]

- **Morning:** plant protein (2 scoops, ~50 g protein) + **½ dose** of the all-in-one (sensitive stomach) + glutamine (MuscleDefender)
- **Night:** separate multivitamin, fish oil, probiotic, and **one** antioxidant capsule (Neal confirms that with the half dose of all-in-one, one antioxidant pill instead of two gives "exactly the right number")
- **Calcium + K2** (dairy-free + acid reducer)
- Vitamin C, vitamin D (he takes 5,000 IU), joint support
- Pre-workout occasionally

This example shows Neal's **dose-accounting logic**: when a person takes half of a combined product, fill the gap with individual products instead of doubling everything.

### 13.3 Illustrative applications of the tree (derived from the rules above, not verbatim from Neal)

| Profile | Program |
|---|---|
| **25-year-old office worker, healthy, eats fish rarely, drinks milk daily, goal = general health, small budget** | Complete multi (split AM/PM with food) · marine omega-3 · protein powder (whey) to hit 1 g/lb LBM · no calcium (dairy covers it). Re-evaluate in 60–90 days. If the budget allows, add lutein/zeaxanthin, then a probiotic. |
| **19-year-old who just joined a gym to build muscle** | Baseline only (multi, omega-3 if low fish, calcium if low dairy, whey). **No creatine until progress stalls**, then 5 g/day. Later: EAAs around workouts. |
| **35-year-old experienced lifter at a plateau, tolerates caffeine** | Baseline → creatine 5 g/day (optional 5-day load at 20 g) → EAAs around training → pre-workout with caffeine + creatine + beta-alanine (can double as the creatine dose). Glutamine only if contest prep or ultra-endurance. |
| **48-year-old, obese, goal = fat loss, caffeine-sensitive** | Baseline → high-protein meal replacement → NAC + milk thistle + choline → **no thermogenic** (stimulant-sensitive) → EAAs optional → collagen (40+) and brain-health formula (45+) when the budget allows → antioxidants/CoQ10 later. Screen for liver and cardiovascular history first. |
| **Vegan woman, 30, fatigued** | Complete multi (with some iron) · plant protein powder · EAAs to fortify plant meals · omega-3 (Neal's vegan option is flax. He personally adds marine omega-3. An algae EPA/DHA would fit his logic, but he didn't discuss algae here) · calcium + K2 if intake is low · **ferritin test before any separate iron** · vitamin D test. |
| **66-year-old retiree, on a statin, walks daily** | Complete multi · omega-3 per fish intake · calcium + K2 if needed · protein to 1 g/lb LBM · **CoQ10 + ALA (statin)** · lutein/zeaxanthin · probiotic · collagen · **brain health at double dose (60+)** · **creatine** · **EAAs** (anabolic resistance) · vitamin D to 40–70 ng/mL. Tell the doctor about everything. |
| **Ultra-marathoner** | Baseline · antioxidant formula (carefully dosed) · omega-3 ~5–6 caps · vitamin C ≥1,000 mg · vitamin D 40–70 ng/mL · **glutamine ~20 g/day split 3×** · **high-dose BCAAs** during long events to delay fatigue · collagen · creatine · carb loading for races. |
| **Chronically stressed executive, already on the baseline for 3 months** | Add ashwagandha as a 6-week trial (stop if no effect) · consider the full Tier 2 list · creatine if 40+. |
| **8-year-old child** | Chewable or powdered complete multi (**never gummies**). No creatine (under 18). |

---

## 14. Quick-reference dose table (as stated by Neal)

| Nutrient / supplement | Neal's number | Official reference (for context) | Source |
|---|---|---|---|
| Protein | **1 g / lb lean body mass / day**. 40–50 g per shake | RDA 0.8 g/kg body weight (Neal calls it a joke) | EP08 18:30; WORTH 06:10 |
| Vitamin D (in multi) | ≥1,000 IU (25 mcg), ideally 1,000–2,000 IU | RDA 600–800 IU | WORTH 19:45; EP08 06:52 |
| Vitamin D (total, to target) | ≥2,000 IU, some 3,000–4,000 IU. **Blood 40–70 ng/mL** (min 30, max ~100). Athletes up to 70 | UL 4,000 IU | WORTH 20:48; STACK 33:47–35:10; EP03 17:07 |
| Vitamin C | **600–1,000 mg/day** (general). 1,000–1,500 (stress/cold season). Athletes ≥1,000 | RDA 75–90 mg. UL 2,000 mg | STACK 32:14–33:21; WORTH 22:10 |
| Vitamin E | 200–300 IU extra is OK | RDA 15 mg. UL 1,000 mg | EP03 18:51–19:21 |
| Vitamin A | Via multi (retinol + beta-carotene) | RDA 700–900 mcg RAE | EP03 11:53–12:15 |
| Folate | Via multi | RDA 400 mcg DFE. UL 1,000 mcg | EP03 23:12 |
| Magnesium | ~200 mg food + 100–200 mg multi | RDA 310–420 mg. **UL (supplemental) 350 mg** | WORTH 17:35–17:44; EP03 29:54 |
| Calcium | **1,000–1,200 mg/day total**, don't exceed ~1,200–1,500. ~500 mg supplement + food. **With K2 + Mg + D** | RDA 1,000–1,200 mg. UL 2,000–2,500 mg | EP08 16:25; EP03 36:41 |
| Iron | ~18 mg/day from some source. **Test ferritin before a separate supplement** | RDA 8–18 mg. UL 45 mg | EP03 34:44–34:57 |
| Potassium | Food only, ~3,500 mg/day | AI 2,600–3,400 mg (older AI 4,700 mg, quoted on show) | EP03 40:57 |
| Zinc | Via multi. Short-term extra for colds only | — | WORTH 28:57 |
| Omega-3 | Supplement if < 3–4 fatty fish servings/week. **Omega-3 index 8%**. Athletes ~5–6 caps/day | — | EP08 15:10–15:40; WORTH 16:26 |
| Lutein | ~6 mg/day | — | EP08 41:09 |
| Astaxanthin | ~3–4 mg/day | — | EP08 41:03 |
| Lycopene | ~10 mg/day | — | STACK 16:16 |
| Probiotic | 20–100 billion CFU/day | — | STACK 20:15 |
| Fiber | ≥25 g (women), 28–30 g (men). Ramp over ~6 weeks | — | EP08 26:36; STACK 20:15 |
| Creatine | **5 g/day** (<200 lb). Load 20 g/day × 5 days (optional). Maintenance 5–10 g. Neal: 7.5 g at 175 lb | — | EP08 59:58–60:16; STACK 09:05; 28:29 |
| EAAs | 8 EAAs, no tryptophan. ~1–2 scoops around training. ~12 g/serving, ~10 kcal | — | STACK 06:34–07:19, 26:37–26:41 |
| BCAAs (if used) | ~4 g leucine, ~1.7 g isoleucine, ~1.7–1.9 g valine. High doses for endurance | — | EP07 14:08–14:19; WORTH 10:44–10:54 |
| Glutamine | ~20 g/day split 3×, starting ~6 weeks pre-competition | — | EP08 56:19–56:27 |
| Mushroom blend | ≥ ~1,200 mg | — | STACK 37:36 |
| Brain health | Standard dose at 45+. **Double at 60+** | — | STACK 11:43–12:06 |
| Collagen | Nano peptides + hyaluronic acid + chondroitin. From 40 (athletes from 20s, or immediately after injury) | — | EP08 35:34–36:34 |
| Thermogenic | Caffeine + green tea + capsaicin (Neal: +5–10% metabolism, ~200 kcal/day) | — | EP08 62:17–62:30 |

---

## 15. Things Neal would say (tone and phrasing for the AI)

- "Womb to tomb." "Take a complete multivitamin/mineral and call it a day."
- "It doesn't work in a vacuum."
- "Silent hunger: you can't hear that your magnesium is low."
- "That's a non-starter." (about undisclosed ingredient amounts)
- "Third-party tested doesn't mean it works."
- "One gram per pound of lean body mass." "Keep a protein powder in the pantry."
- "Calorically efficient: more nutrition, less calories."
- "Get your baseline covered first, wait 60 to 90 days, then add something."
- "The RDA for vitamin D / vitamin C / protein is a joke."
- "Omega-3s are resolvers of inflammation."
- "If you're a novice, I wouldn't even let you take creatine... as soon as you plateau, bang, here's your creatine."
- "Don't self-prescribe. Get a legitimate recommendation from a *qualified* professional."
- Neal is blunt and often profane on the show. An AI should keep the **content and confidence** but can drop the profanity.

---

## 16. Open questions and gaps for implementers

These points weren't specified in the five episodes. Decide them explicitly or source them elsewhere (other SupBeast episodes cover creatine, protein, EAAs, immunity, aging, and GLP-1 in depth):
- Exact clinical doses for CoQ10, ALA, zeaxanthin, ALCAR, phosphatidylserine, ashwagandha, turmeric, NAC, milk thistle, choline, beta-alanine, caffeine, and collagen peptides.
- The exact omega-3 EPA/DHA dose for non-athletes.
- The exact list of the "≈20 potentially under-consumed" nutrients (the EP03 source document is ~78 pages and wasn't read aloud).
- The "lean body mass" estimate method. Neal doesn't say how to estimate LBM when body-fat % is unknown.
- Pregnancy-specific dosing beyond "prenatal with folate."
- Drug-supplement interactions beyond statins/CoQ10 and acid reducers/calcium. Neal defers these to the medical questionnaire and the person's doctor.
- The creatine-for-everyone question (see the note in §7).
- Algae-based omega-3 for vegans (not discussed. Neal's all-in-one uses flax, and he supplements marine omega-3 himself).
