# Podcast QC notes (plan §7 step 2) — 10% spot-check sample

Sampling: 5/47 episodes stratified across risk axes (reacts/clip-audio,
longest, lowest-confidence, conversational intro, product-dense 3-speaker).
Procedure per episode: three ~5-min listen windows (start / middle / end)
against the readable `.txt`, checking wrong words (esp. product names),
wrong-speaker turns, missing speech, music junk.

**Speaker-map ground truth** (diarization IDs are per-episode — never reuse
across files; the later speaker-map step consumes this table):

| Episode | Speaker map |
|---|---|
| David Protein Bar FAILED Consumer Labs Test | 1 = Layne Norton (spliced clip), 2 = Zane Spruce, 3 = Neal Spruce |

## Results

### 1. Top Nutrition Expert Reacts to RFK Jr. New Food Pyramid — CLEAN (2026-09-05)
### 2. Supplement Protocols for OPTIMAL Health ｜ Ep 08 — CLEAN (2026-09-05)
### 3. David Protein Bar FAILED Consumer Labs Test — CLEAN (2026-09-05)
- No word/speaker/gap issues found.
- Note: speaker lineup differs from host-only episodes (see map above) —
  inserted media consistently diarizes as its own speaker.
- Note: no dotFIT product mentions; supplements discussed by generic names
  only. Expected for reacts-style episodes; content still valid authority-4
  usage/context material.
### 4. About Neal & Zane Spruce ｜ Ep 00 — CLEAN (2026-09-05)
### 5. The Benefits of Protein Powders ｜ Whey, Soy, Collagen, Egg White — CLEAN (2026-09-05)
