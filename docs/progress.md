# Phase 1 — Build Progress

The status view: what is built, what is open, what runs next. Every agent run
reads this file end to end, so it is kept short on purpose — the rest lives in:

- `docs/decisions.md` — owner/curation rulings, grouped by area. Read the group
  for the area you are about to touch.
- `docs/progress-archive.md` — log entries older than the five kept below.
- `phase1-knowledge-assistant.md` — the plan (design decisions); `AGENTS.md` —
  the hard rules those produced.

## Status (§13 build order)

Numbers verified 2026-09-08. Python 420 tests green; runtime 112 tests green.

| Component | Plan § | State | Verified output |
|---|---|---|---|
| QA Stage 0 — PII scrub | §4 | **done**, rules extended 2026-09-08 (triage round 2) | 1,051 `.docx` (1,103 − 51 duplicates − 1 zero-byte; 43 md5 groups, log in `processed/qa/runs/`); round-2 regen redacted 38 further names across 36 files, 0 prose changes |
| QA Stage 1 — parse & classify | §4 | **done** | 777 qa_email / 264 expert_note / 0 other; 766 `thread_date`; review queue 0; 10 unanswerable excluded (6 no-answer, 4 blank) |
| PDSRG extraction gate | §6.1 | **passed**, human-verified | 5 stress PDFs |
| PDSRG chunking | §6.2–4 | **done** | 39 docs → 1,080 chunks (~404K tokens, median 349); 950 with part_nos; 53 discontinued-stamped; 1 atomic oversize table |
| Alias table | §5 | **done**, v1.3.0 | 51 indexed SKUs → 31 families; worksheet 19/19 attested; 13 deterministic aliases + 1 LLM-only |
| QA Stage 2 — canonicalize | §4 | **done** — full run 2026-09-06, alias-1.3.0 regen 2026-09-07, scrub round-2 regen 2026-09-08 | 1,041 canonical records (prompt 1.1.0); 677 with products (50 part_nos); 306 currency-cued; queue 220 (166 PII + 48 audit + 1 containment + 5 low-conf) — dispositions are item 13 |
| QA Stage 4 — dedup & currency | §4 | **done** 2026-09-07, dispositioned 2026-09-08, regen 2026-09-08 | 919 current / 107 superseded_currency / 15 superseded_dup; 16 clusters (1 conflict, ruled **split**); 114 judgments (107 dependent / 7 independent / 0 low-conf); **queue 0** |
| Podcast segmentation | §7 | **done** — contract in `podcast.py` | 47 episodes → 1,800 segments (median 76 s / 251 words) |
| Golden set | §12 | **complete except labeling** 2026-09-08 — the 250 are sampled, the 50 adversarial are **written** (`CURATED_ADVERSARIAL`), the probes are built. Remaining: label the 250 (item 8) | 650 current QA pairs → 250 items over 29 families (125/125) + 50 adversarial (25/25) + 120 PDSRG/podcast probes; `processed/golden/` |
| Podcast ASR | §7 | **transcribed + QC PASS, indexed, cited** — 47/47, 5-episode spot-check clean; citation URLs verified and stamped 2026-09-08 (item 14 closed). Remaining: speaker-map rewrite + re-upload (non-blocking) | 38.0 h audio → 35,050 phrases (~437K words); 37 eps × 2 speakers, 10 × 3; 1,800/1,800 segments deep-linked |
| Index + retrieval | §9–11 | **live** on `kb-main-v2`, the code default on both sides (item 10 closed). Re-uploaded 2026-09-08 after the round-2 regen: 30 QA docs differed (29 changed + 1 restored by the dissolved dup cluster), all of it Stage 2 re-canonicalization jitter — **0 of the 38 redacted names were ever in an indexed field**, checked old and new, so the sync was housekeeping, not a privacy fix. Remaining: the ranker call (item 5) | 3,996 docs — 1,080 pdsrg / 177 product / 10 menu / 1,800 podcast / 929 qa; 2 embed calls, 1 pruned, 0 errors; live `search_ping` PASS at 3,996 |
| v1 runtime | §11 | **built + live** — the full §11 chain verified end to end and traced. Delivery mode is explicit: `Gated` for the service, `Live` for the CLI. Remaining: audit precision on a full sweep (item 12) | `runtime/`, 112 tests |
| SSE service | §11 | **built + live** 2026-09-08 — `POST /ask` streams disclosure/stage/delta/retraction/result; always `Gated`; config validated at startup. Remaining: item 12's number before it ships to customers | `runtime/src/DotFit.Agents.Service`; normal + escalation paths smoked live |
| §12 eval harness | §12 | **built + live** 2026-09-08 — `qa-pipeline eval` over `ask --json`; label-free metrics run today, label-dependent ones report `null` with a reason | first dev sweep (n=12): sample recall@8 100% (ranker off) / 83.3% (on), probes 100%, escalation 10/10 |

Artifacts: `processed/qa/`, `processed/pdsrg/`, `processed/aliases/`,
`processed/golden/`, `processed/index/`, `processed/eval/` (the one that
measures a live service, so its numbers are a dated reading, not a rerun).
Per-run counts live in each `summary.json`; numbers quoted here must match a
regenerated run.

## Open items (§14)

The ones that need a *person* rather than code are written up in plain
language for non-engineers in `docs/owner-tasks/` — one document per task,
with what it is, why it matters and how long it takes. Keep the two in step:
this table is the engineering status, that folder is what gets handed to an
owner.

| # | Item | Status |
|---|---|---|
| 1 | Azure region + SKU | **partial** — services provisioned 2026-09-05, credentials in local `.env`; `text-embedding-3-large` and `gpt-5-mini` deployed and smoke-verified. Remaining: quota increase for the frontier chat deployment |
| 2 | products.json freshness owner | **open** — the blocker is naming the owner and the cadence; the instrument exists since 2026-09-08 (`scripts/products_diff.py`, diffs two exports by §5 section so the review sees changed *claims*) |
| 3 | Alias-table curation session | **closed** 2026-09-07 (pass 2) — outcomes are the `alias.py` curated constants, the worksheet is the session record. A pass 3 starts from the 2,292 unresolved Stage 2 mentions |
| 4 | PPTX disposition | parked |
| 5 | Semantic ranker on/off | **open, now measurable** — `qa-pipeline eval --ranker-ab` reports source recall@k both ways. First signal (dev, n=12) is **against** the ranker: 100% off vs 83.3% on. n=12 is a smoke, not the call — rerun on the full dev split before deciding |
| 6 | Stage 3 review-queue dispositions | **closed** 2026-09-05, queue 0 |
| 7 | Stage 4 review-queue dispositions | **closed** 2026-09-08, queue 0 — the one `cluster_conflict` cluster ruled **split**: both members stay `is_current`, neither supersedes. Ruling and reasoning in `docs/decisions.md`; pinned by `CURATED_CLUSTER_DISPOSITIONS` |
| 8 | Golden-set labeling | **open, narrowed** — the 50 adversarial are written (2026-09-08); what remains is labeling the **250** with points-to-hit and expected sources. Nutritionist + support lead, ~2–3 days; §12 rubric in the worksheet header. Until then the harness reports those two metrics as `null` with a reason |
| 9 | Runtime live smoke | **closed** 2026-09-07 — root cause was the wedged index (item 10), not the service |
| 10 | Orphaned `kb-main` index | **closed** 2026-09-08 — the delete finished server-side: `GET /indexes/kb-main` now returns the clean-miss 404 (not the wedged `"is being deleted"` body) and servicestats counts only `kb-main-v2` — 1 index, 3,996 docs, ~110 MB; the orphan's 7,992 docs / 213 MB no longer counted. Verified via statistics, not the portal, which hides deleting-state indexes. Support ticket moot; the name is free but `kb-main-v2` stays the code default on both sides — moving back is cosmetic, an owner option, not a task |
| 11 | Answer agent sourced claim language from authority 3 | **fixed** 2026-09-07 — sources are tagged quotable (authority 1–2) vs context-only and the answer instructions fail closed; 4/4 live runs PASS. Remaining: 4 runs is signal, not a regression suite — coverage lands with the §12 eval harness, and re-test on the frontier deployment when item 1's quota arrives |
| 12 | Claims-audit precision under gating | **open, now instrumented** — the harness reports it on the adversarial-50 with its counts (a small denominator is not a strong claim), scoring the *draft* the audit saw. Still unmeasured on a full sweep: the first run flagged nothing, so precision is `null`, not good. Needed before the SSE service faces customers |
| 13 | Stage 2 review-queue dispositions (**absorbed item 16** 2026-09-08) | **open, re-triaged** — 220 flagged records, **201 of them `is_current` and live in the index**. Round 2 rebuilt the grouping on *evidence-span survival* (the round-1 placeholder census hid 5 live customer names in the bulk pile) and closed 4 `scrub.py` gaps: 38 names redacted across 36 files, **unresolved spans 6 → 3**, item 16's self-introductions 10 → 0. The Zane row is **ruled staff** (see `docs/decisions.md`) — its queue row clears at the next `PROMPT_VERSION` bump, not before. What is left for a person: **2** third-party prose mentions no rule can reach (1 indexed), **1** committed-tree ruling covering all **121** `text_residue` records at once, and **215** bulk (42 resolved + 48 audit + 5 low-conf + 1 containment) |
| 14 | Podcast citation URLs | **closed** 2026-09-08 — all 47 ids resolved via YouTube oEmbed and matched all 47 episodes at Dice 1.00; frozen as `PODCAST_VIDEO_IDS`, 1,800/1,800 segments deep-linked to their start second, re-uploaded with 0 embed calls and verified live |
| 15 | §12 evaluation coverage | **mostly closed** 2026-09-08 — the precision metric is in §12's list; PDSRG/podcast have 120 retrieval probes; the degraded-guardrail path is pinned by tests and fixed a real defect. **Remaining**: probes measure retrieval only, so end-to-end coverage of those two corpora still needs written questions, and two §11 standing behaviors (conversation-start disclosure, prompt-injection) have no adversarial item because §12 fixes the split at 20/15/15 |
| 16 | Customer names in `question_original` | **closed into item 13** 2026-09-08 — it was the same defect seen through a different probe: the scrub had no rule for a name that is neither a salutation nor a closer. `SELF_INTRO_RE` closed it (11 spans redacted, the one public figure on `ACCEPTED_HONORIFIC_NAMES` correctly quoted verbatim) and the `my name is` probe is now 0. Residual-PII work continues under item 13; do not re-open this row |

## Log

Newest first, one entry per work item, 8 wrapped lines maximum (see Writing
entries). The five most recent live here; older ones are in
`docs/progress-archive.md`. Detail belongs in the commit, the code, or the
artifact it describes.

- **2026-09-08 (44)** — Model-facing source labels reworded (§11). `SourceLabel`
  is what the answer agent echoes when it attributes an answer in prose, and it
  was echoing the corpus name: "dotFIT's customer Q&As typically recommend ...",
  which tells a customer how the corpus was assembled, not where the guidance
  comes from. `qa` → **dotFIT nutrition knowledge base**, `podcast` → **expert
  discussion transcript**; the other three unchanged. `SourceKind` (the
  customer-facing citation line) stays literal, and quotability still rides on
  `ClaimsMarker`, so no source moved across the §3 claims line. 1 new test.

- **2026-09-08 (43)** — Stage 2 triage round 2 (§4): **item 16 folds into 13**,
  round 1's grouping retired as unsound — it read "a placeholder exists
  somewhere in this record" as "the redaction landed", parking 5 live customer
  names in the bulk pile. Grouping is **evidence-span survival** now (joins the
  gitignored cache, still prints no span). Four `scrub.py` gaps closed on the
  regen diff — **38 names / 36 files / 0 prose changes**, unresolved 6 → 3, of
  which Zane ruled **staff** (lands on the next `PROMPT_VERSION` bump). Rerun
  byte-identical; index re-uploaded, 3,996 docs, 0 errors. **420 python**.
- **2026-09-08 (42)** — Open item 10 **closed — no ticket needed**: the orphaned
  `kb-main` finished deleting. `GET /indexes/kb-main` now returns the clean-miss
  404, not the wedged "is being deleted" body, and servicestats counts only the
  live index (1 index, 3,996 docs, ~110 MB — the orphan's 7,992 docs / 213 MB
  no longer counted; verified via statistics, not the portal, which hides
  deleting-state indexes). The name is free but `kb-main-v2` stays the default
  on both sides — a rename is cosmetic. Docs and status comments updated.
- **2026-09-08 (41)** — Tracker convention change + one new open item (docs
  only). Log entries no longer cite commit SHAs, here and in
  `progress-archive.md`, and the **Writing entries** rule dropped the
  requirement; entries touched by the removal were re-wrapped to the documented
  80 columns and the 8-line budget. New **item 16**: 10 Stage 4 records keep a
  self-introduced customer name in `question_original` (9 full names, only 2
  flagged) — committed `processed/` only, 0 hits in the indexed field. 404
  tests, unchanged.
- **2026-09-08 (40)** — Stage 4 review queue closed (§4): **item 7
  dispositioned, queue 2 → 0**. The corpus's one `cluster_conflict` is ruled
  **split** — the pair is two turns of one email thread, and Stage 1 keeps only
  the new expert reply, so the newer record is a delta, not a superset
  (reasoning in `docs/decisions.md`). Both members stay `is_current`. Rulings
  live in `CURATED_CLUSTER_DISPOSITIONS`, membership-attested. Stage 4
  regenerated, rerun byte-identical, 0 API calls; index untouched. 8 new tests
  (**404 python**).
## Writing entries

Update **Status** (numbers), **Open items**, and add one **Log** entry per work
item, newest first, with plan-§ refs. Then **rotate**: move whatever falls past
the newest five to the top of `docs/progress-archive.md`, so this file's length
stays flat instead of growing one entry per work item.

**Budget: 8 wrapped lines per log entry, hard.** Wrap at 80 columns like the
rest of the file — a single 3,000-byte line is exactly what this rule exists to
prevent. Keep the numbers (counts, test totals), the §refs and the open-item
pointers; drop the mechanism, which belongs in the commit message, the
docstring, or the artifact.

**Table rows are status, not history**, in both tables: the state, plus the
numbers a rerun must reproduce, plus what remains. A post-mortem in a row is a
log entry in the wrong place; a mechanism in a row is a docstring in the wrong
place. When an item closes, cut the row to its outcome and any remainder.

Owner rulings that constrain future work go to `docs/decisions.md` — not here,
and not twice.
