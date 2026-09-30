# Alias curation worksheet (plan open item #3)

Confirm or correct each row with the support lead. Confirmed entries move into the curated overlay in ``qa_pipeline/alias.py`` and the table is rebuilt.

**Application policy — corpus text is NEVER rewritten.** Aliases drive only: (a) index metadata tags (``products`` field), (b) query-side expansion, (c) Stage 4 currency flags. Ambiguous tokens are resolved contextually by the Stage 2 LLM, never by find-and-replace.

Spelling variants need no curation: matching is normalization-based (First String ≡ FirstString, Active MV ≡ ActiveMV, WheySmooth ≡ Whey Smooth). Only true abbreviations and legacy names are listed.

**Outcome legend** (edit the *Recommended* cell if the session disagrees):

- **tag** — deterministic auto-tagging is safe: every corpus hit maps to this family
- **context-only** — no deterministic rule (dual/generic usage); the Stage 2 LLM tags per document, unsure cases → review queue
- **no mapping** — drop the token entirely

**Session record 2026-09-01:** all rows confirmed per recommendation.
MVM was briefly flipped to tag, then reconsidered: the MVs are distinct
products chosen by audience (women / 50+ / general), so a deterministic
all-MV tag would blur the distinction — context-only stands, with
audience guidance recorded in ``CONTEXT_ONLY_TOKENS``. Outcomes live in
``CURATED_ALIASES`` / ``CONTEXT_ONLY_TOKENS`` in ``qa_pipeline/alias.py``;
this file is the session record; regenerate with ``qa-pipeline aliases``.

**PDSRG dispositions 2026-09-01 (4), support lead:** the legacy-rename
rows below (SuperiorAntioxidant, UltraProbiotic, ExtremeCreatineXXXL,
JointSkinCollagen+, AdvancedBrainHealth) come from that
session — confirmed by the product owner (sku 1000 Antioxidant was
added to products.json to complete the SuperiorAntioxidant rename).
**Recover&Build is a *replacement*, not a rename** (different formula),
so it lives in the table's `replacements` section: currency cue and
query redirect only, never a product tag.
Two chain names (CreatineXXL, JointFlexPlus) come from formerly-markers
in the PDSRG titles, corpus-attested. Discontinued rows (KidsMV, VeganMV)
are currency cues only: mentions flag the answer superseded; they never
tag a product.

| Confirm | Token | Outcome | Mapping | Docs | Evidence |
|---|---|---|---|---|---|
| [x] | `AF` | **tag** | AminoFormula | 55 | 2023/17yr Weight gain instruct, menu, Creatine in baseline w AF&Pro Stack, Parent note.docx: …h Creatine Monohydrate in Baseline with AF & Protein Stacking Click on product lin…<br>2023/3 days low, 1 day high inst with level 3 weight loss supp bundle .docx: …ein needs Workout Days: AminoFormula (AF) Take 1-scoop ~10 minutes before and m… |
| [x] | `FS` | **tag** | First String | 20 | 2023/17yr weight gain&note, FS in baseline, LeanMR in BaselineKatsMenus.docx: …ement recipes. 17yr weight gain&note, FS in baseline, LeanMR in BaselineKatsMenu…<br>2023/First string save 2-30pm.docx: …rrent scientific evidence, FirstString (FS) is designed to provide the ideal rapid… |
| [x] | `MVM` | **context-only** | resolve by audience context (Women's MV / Over 50 MV / Active MV); unclear -> topic-level only | 153 | 2023/10yr youth athlete baseline and slide youth prgm slide, with note, kidsMV.docx: …ith a multivitamin and mineral formula (MVM) to also aid recovery and fill importan…<br>2023/17yr weight gain&note, FS in baseline, LeanMR in BaselineKatsMenus.docx: …ith a multivitamin and mineral formula (MVM) to also aid recovery and fill importan… |
| [x] | `PP` | **context-only** | Pre & Post Workout Formula OR 'protein powders' (generic — evidence favors the latter) | 4 | 2023/WLLS Therm, NO7Rage, Creatinemono, ExtremeCreatine, together in baseline, all product info, links, store & PP.docx: …eline, all product info, links, store & PP…<br>2024/Difference BTW AminoFormula and Protein powder, incl slides and usages.docx: …nks for contacting us. Protein powders (PP) such as WheySmooth, FirstString, BestP… |
| [x] | `SB` | **tag** | Alln1 SuperBlend | 90 | 2023/AEON anti aging supps, SB in full health program resveratrol, etc..docx: …Monohydrate Daily: Alln1 SuperBlendTM (SB) See attached slides for all-natural fu…<br>2023/Allin1 SB PDSRG access with full PDSRG note.docx: …ain and have a great New Year. Allin1 SB PDSRG access with full PDSRG note… |
| [x] | `WLLS` | **tag** | WeightLoss & LiverSupport | 50 | 2023/Best fat, weight loss supps, WLLS & ThermAccel with MVM, diet link.docx: …Otherwise, Weight Loss & Liver Support (WLLS) is a non-stimulant product that works…<br>2023/Carbrepel and Themaccel together -stimulant.docx: …ecommend Weight Loss and Liver Support (WLLS) and ThermAccel used together if I want… |
| [x] | `MuscleDefender` | **tag** | GlutamineComplex (legacy rename) | 17 | 2023/AminoFormula flavor question only -7-21.docx: …, fruit juices, etc.) or powders (e.g., MuscleDefender, CreatineMonohydrate, ExtremeCreatineXX… |
| [x] | `LeanMR` | **tag** | LeanMeal Nutrition Shake (legacy rename) | 100 | 2023/17yr weight gain&note, FS in baseline, LeanMR in BaselineKatsMenus.docx: …imes for weight gain. You would use the LeanMR as shown in the meal replacement progra… |
| [x] | `NO7 Rage` | **tag** | NO7 PreWorkout (legacy rename) | 35 | 2023/Agmative sulfate NO booster artial fib, NO7 PDSRG 3 and 4th edition, nitrosigine.docx: …ate (AS) was in our previous version of NO7Rage as an additive nitric oxide (NO) booste… |
| [x] | `SuperiorAntioxidant` | **tag** | Antioxidant (legacy rename) | 28 | 2023/Adrenal burnout fatigue, dietExercise&prgm note, baseline&CompleteLongevity program, budget.docx: …1-Active). Take as directed with meals SuperiorAntioxidant Take 2 daily anytime with a meal Ultra… |
| [x] | `UltraProbiotic` | **tag** | Probiotics (legacy rename) | 29 | 2023/Adrenal burnout fatigue, dietExercise&prgm note, baseline&CompleteLongevity program, budget.docx: …idant Take 2 daily anytime with a meal UltraProbiotic Take one daily with a meal Daily as ne… |
| [x] | `ExtremeCreatineXXXL` | **tag** | Creatine Complex (legacy rename) | 42 | 2023/Allin1 SB citrus acid, flavor,AF citrus, creatine PDSRG links-access.docx: …ts nor the CreatineMonohydrate, nor the ExtremeCreatineXXXL+. There is citric acid used as an ingre… |
| [x] | `CreatineXXL` | **tag** | Creatine Complex (legacy rename) | 5 | 2023/Creatine ExtremeCreatine together and alone & in baseline.docx: …monohydrate product in conjunction with creatine XXL? This would be an effort to bring the c… |
| [x] | `JointSkinCollagen+` | **tag** | CollagenComplex (legacy rename) | 30 | 2023/AEON anti aging supps, SB in full health program resveratrol, etc..docx: …(~1000-1200 mgs) from food/shake intake JointSkinCollagen+ (Biocell Collagen II) Joint & Skin hea… |
| [x] | `JointFlexPlus` | **tag** | CollagenComplex (legacy rename) | 4 | 2023/Adrenal burnout fatigue, dietExercise&prgm note, baseline&CompleteLongevity program, budget.docx: …k unless for athletic recovery purposes JointFlexPlus (Biocell Collagen II) Joint & Skin hea… |
| [x] | `AdvancedBrainHealth` | **tag** | Brain Health (legacy rename) | 17 | 2023/AEON anti aging supps, SB in full health program resveratrol, etc..docx: …t discomfort take 1-2 in AM & 1-2 in PM Advanced Brain Health (may divide evenly or all at once anyti… |
| [x] | `Recover&Build` | **context-only** | AminoFormula (REPLACEMENT — different formula; currency cue and query redirect only, never a product tag) | 19 | 2023/Allergen warnings on AminoFormula, Ok for fish, complete manuf info.docx: …of the information. As Neal stated, the Recover & Build (BCAAs) is free of fish or shellfish as… |
| [x] | `KidsMV` | **tag** | discontinued (no successor) — currency cue only, never a product tag | 21 | 2023/10yr youth athlete baseline and slide youth prgm slide, with note, kidsMV.docx: …and slide youth prgm slide, with note, kidsMV… |
| [x] | `VeganMV` | **tag** | discontinued (no successor) — currency cue only, never a product tag | 80 | 2023/17yr weight gain&note, FS in baseline, LeanMR in BaselineKatsMenus.docx: …n 12-17yr use 1-Active) ;All vegans use VeganMV LeanMR other favorite dotFIT protein m… |
