# Menu guide review

`assistant/references/menu-guide.md` is dotFIT's menu method as the assistant
reads it. Most of it is written from dotFIT's own material; the items below are
not, and need an owner ruling. The guide states each as a rule today, because the
model reads the guide whole and cannot tell a proposal from a rule. When an item
is ruled on, edit the guide and delete the item here.

Sources the guide is written from: the masterclass handouts in
`pipeline-output/masterclass/md/` (weight loss vs fat loss, 2023; muscle gain,
2024; Gainz, 2019), the practitioner guide's "Overall Diet" sections for LeanMeal
and WheySmooth, the program guide's section 3.4, and the reference menus export
(`original-data/Reference Menus/`).

## What comes from dotFIT

- Daily burn as resting + digestion + exercise and movement, with the shares.
- Fat loss at 0.5–1% of body weight a week, starting slow; faster for people with
  a lot to lose. The 500-calories-per-pound rule matches the handout's worked
  example (0.7 lb a week, a 350-calorie deficit).
- Muscle-gain surpluses (men 250–500, women 125–250), the 100–250 calorie step
  when gains stall, and the weekly gain rates.
- Protein at 1 g per lb of lean mass, or 0.7–1.0 g per lb of body weight; at
  least 3 resistance sessions a week.
- The fat-loss split (30–40% carbohydrate, 30–35% protein), food meals with
  protein at each, protein mixes filling the rest of the calories.
- The muscle-gain split and the training-day timing (major meal 2.5–3.5 h
  before, a shake 20–40 min before and right after, a meal 1–2 h later).
- LeanMeal replacing 2 smaller meals of a 4–5 meal day; the product figures
  (program guide section 3.4).
- The ten menu styles and their descriptions; the meal slots, times and calorie
  shares, measured from the reference menus.

## Needs a ruling

1. **The calorie formula.** The handouts send trainers to the dotFIT Program's
   calculator and publish no formula. The guide uses Mifflin-St Jeor with the
   standard activity factors. Checked against the 2023 handout's example (a
   155 lb woman, 34, sedentary job, exercising 2–3 times a week: 1,983 a day),
   it gives about 1,930 at an assumed 5'5". If the dotFIT Program's formula can
   be shared, the guide should use it, so the assistant and the program agree.
2. **The calorie floor.** No calorie target below 1,200 a day for a woman or
   1,500 for a man (stated in the guide and in `prompt/menus.md`, which needs it
   for follow-ups that do not reread the guide), and no deficit over 35% of burn (the handout's faster
   example is 35%). dotFIT's own reference menus go down to 1,000 and 1,250, so
   either those levels are for cases the assistant should not plan on its own,
   or the floor is lower than proposed.
3. **Under 18.** No deficit; a teenager who wants to lose weight gets a balanced
   menu at their burn, more activity, and their parents and doctor. Products
   limited to First String, Plant Protein and dotBARs, following the program
   guide's youth section. The formula is not validated for children.
4. **Pregnancy, nursing, a managed medical condition.** No calorie target of the
   assistant's own: a general Standard or Heart Healthy menu, with the numbers
   left to their doctor or dietitian. This reads `safety.md`'s "general guidance
   dotFIT publishes" as excluding a target.
5. **Shakes and bars per day.** At most two, three on a gain or performance day
   of 3,000 calories or more. Inferred from LeanMeal's "2 of 4–5 meals" and from
   the reference menus, which never use more.
6. **Gluten-free and lactose-free products.** dotFIT's Gluten Free menus use
   WheySmooth and First String, and its Lactose Free menus use WheySmooth. The
   guide does not let the assistant say a product suits either unless the
   approved copy says so. WheySmooth and First String are gluten-free (the
   website marks them); the plan is to import the site's product markers into
   the product data, after which the assistant can say so from approved copy.
   Lactose is still open.
7. **dotBAR and dotWAFER figures.** The program guide gives no calories or
   protein for them, so the guide sends the assistant to the product copy first,
   which costs a lookup. Supplying the figures in the guide would save it.
8. **A week of menus.** Three interchangeable days and a swap list rather than
   seven full days, unless the customer insists, to keep answers readable and
   fast. Confirm this is acceptable.

## Out of date in the reference menus

The export still lists VeganMV (discontinued), a vegan dotSTICK (no current
product) and LeanMR (now LeanMeal). The guide does not use them, but the menus on
the website may still show them.
