# Phase 1 — Build Progress

The status view: what is built, what is open, what runs next. Every agent run
reads this file end to end, so it is kept short on purpose — the rest lives in:

- `docs/decisions.md` — owner/curation rulings, grouped by area. Read the group
  for the area you are about to touch.
- `docs/progress-archive.md` — log entries older than the five kept below.
- `phase1-knowledge-assistant.md` — the plan (design decisions); `AGENTS.md` —
  the hard rules those produced.

## Status (§13 build order)

Numbers verified 2026-09-09. Python 421 tests green; runtime 112 tests green.

| Component | Plan § | State | Verified output |
|---|---|---|---|
| QA Stage 0 — PII scrub | §4 | **done**, rules extended 2026-09-08 (triage round 2) | 1,051 `.docx` (1,103 − 51 duplicates − 1 zero-byte; 43 md5 groups, log in `processed/qa/runs/`); round-2 regen redacted 38 further names across 36 files, 0 prose changes |
| QA Stage 1 — parse & classify | §4 | **done** | 777 qa_email / 264 expert_note / 0 other; 766 `thread_date`; review queue 0; 10 unanswerable excluded (6 no-answer, 4 blank) |
| PDSRG extraction gate | §6.1 | **passed**, human-verified | 5 stress PDFs |
| PDSRG chunking | §6.2–4 | **done** | 39 docs → 1,080 chunks (~404K tokens, median 349); 950 with part_nos; 53 discontinued-stamped; 1 atomic oversize table |
| Alias table | §5 | **done**, v1.3.0 | 51 indexed SKUs → 31 families; worksheet 19/19 attested; 13 deterministic aliases + 1 LLM-only |
| QA Stage 2 — canonicalize | §4 | **done** — full run 2026-09-06, regens 2026-09-07/08, **gpt-5.6-luna regen 2026-09-09 (prompt 1.2.0)** | 1,041 canonical records; 680 with products (50 part_nos); 306 currency-cued; queue 234 (182 PII + 47 audit + 6 low-conf) — dispositions are item 13; Zane ruling landed |
| QA Stage 4 — dedup & currency | §4 | **done**, gpt-5.6-luna regen 2026-09-09 | 923 current / 104 superseded_currency / 14 superseded_dup; 16 clusters (**3 conflict** — 1 ruled split, 2 new → item 7; 1 audit); 114 judgments (104 dependent / 10 independent); queue 6 |
| Podcast segmentation | §7 | **done** — contract in `podcast.py` | 47 episodes → 1,800 segments (median 76 s / 251 words) |
| Golden set | §12 | **complete except labeling** 2026-09-08 — the 250 are sampled, the 50 adversarial are **written** (`CURATED_ADVERSARIAL`), the probes are built. Remaining: label the 250 (item 8) | 650 current QA pairs → 250 items over 29 families (125/125) + 50 adversarial (25/25) + 120 PDSRG/podcast probes; `processed/golden/` |
| Podcast ASR | §7 | **transcribed + QC PASS, indexed, cited** — 47/47, 5-episode spot-check clean; citation URLs verified and stamped 2026-09-08 (item 14 closed). Remaining: speaker-map rewrite + re-upload (non-blocking) | 38.0 h audio → 35,050 phrases (~437K words); 37 eps × 2 speakers, 10 × 3; 1,800/1,800 segments deep-linked |
| Index + retrieval | §9–11 | **live** on `kb-main-v2`, the code default on both sides. Re-uploaded 2026-09-09 after the luna regen: 0 errors, all Stage 2 re-canonicalization. Ranker ruled **off** — item 5 closed | 4,002 docs — 1,080 pdsrg / 177 product / 10 menu / 1,800 podcast / 935 qa; 57 embed calls (3,090 cached), 7 pruned, 0 errors; live `search_ping` PASS at 4,002 |
| v1 runtime | §11 | **built + live** — the full §11 chain verified end to end and traced. Delivery mode is explicit: `Gated` for the service, `Live` for the CLI. Remaining: audit precision on a full sweep (item 12) | `runtime/`, 112 tests |
| SSE service | §11 | **built + live** 2026-09-08 — `POST /ask` streams disclosure/stage/delta/retraction/result; always `Gated`; config validated at startup. Remaining: item 12's number before it ships to customers | `runtime/src/DotFit.Agents.Service`; normal + escalation paths smoked live |
| §12 eval harness | §12 | **built + live** — label-free metrics run, label-dependent report `null` with a reason | dev sweep 2026-09-09 on gpt-5.6-luna (125/60/25): sample recall@8 99.2% off / 75.2% on; probes 98.3% / 100% on; escalation 10/10; points-hit 0.9; answers first measured — 51/125 withheld on claims_language (item 17), faithfulness 0.57 |

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
| 1 | Azure region + SKU | **closed** 2026-09-09 — quota arrived; both chat roles now `gpt-5.6-luna` (one deployment, 333K TPM; embedding unchanged), smoke-verified end to end; corpus regen + re-measure done (owner task 5a resolved) |
| 2 | products.json freshness owner | **open** — the blocker is naming the owner and the cadence; the instrument exists since 2026-09-08 (`scripts/products_diff.py`, diffs two exports by §5 section so the review sees changed *claims*) |
| 3 | Alias-table curation session | **closed** 2026-09-07 (pass 2) — outcomes are the `alias.py` curated constants, the worksheet is the session record. A pass 3 starts from the 2,292 unresolved Stage 2 mentions |
| 4 | PPTX disposition | parked |
| 5 | Semantic ranker on/off | **closed** 2026-09-09 — **off**. Full dev split: sample recall@8 99.2% off vs 75.2% on (n=125); the ranker's only win is one podcast probe (59/60 → 60/60). The off default stands; `--semantic` remains a flag |
| 6 | Stage 3 review-queue dispositions | **closed** 2026-09-05, queue 0 |
| 7 | Stage 4 review-queue dispositions | **reopened** 2026-09-09 — the luna regen's sharper canonical questions pushed **two new pairs** over the 0.88 threshold: LeanMR+creatine (reads like two turns of one thread — the FirstString shape) and Lean Pack 90 all-at-once (same question a year apart, answers agree). Queue 6 (4 conflict + 2 audit); both pairs written up in owner task 3 |
| 8 | Golden-set labeling | **open, narrowed** — the 50 adversarial are written (2026-09-08); what remains is labeling the **250** with points-to-hit and expected sources. Nutritionist + support lead, ~2–3 days; §12 rubric in the worksheet header. Until then the harness reports those two metrics as `null` with a reason |
| 9 | Runtime live smoke | **closed** 2026-09-07 — root cause was the wedged index (item 10), not the service |
| 10 | Orphaned `kb-main` index | **closed** 2026-09-08 — the delete finished server-side: `GET /indexes/kb-main` now returns the clean-miss 404 (not the wedged `"is being deleted"` body) and servicestats counts only `kb-main-v2` — 1 index, 3,996 docs, ~110 MB; the orphan's 7,992 docs / 213 MB no longer counted. Verified via statistics, not the portal, which hides deleting-state indexes. Support ticket moot; the name is free but `kb-main-v2` stays the code default on both sides — moving back is cosmetic, an owner option, not a task |
| 11 | Answer agent sourced claim language from authority 3 | **fixed** 2026-09-07, re-measured on the frontier 2026-09-09 — the dev sweep's audit saw no authority-3 sourcing; the residual claims-language concern on luna is item 17 |
| 12 | Claims-audit precision under gating | **open, first number in** — dev adversarial-25 (2026-09-09): 1 flag, 0 true → precision 0.0 at denominator 1 (the small-denominator caveat stands). The same sweep showed the gate withholding 41% of sample answers — both this metric and that behavior ride on item 17's prompt fix. Needed before the SSE service faces customers |
| 13 | Stage 2 review-queue dispositions (**absorbed item 16** 2026-09-08) | **open, queue shifted by the luna regen** — 220 → 234 (182 PII + 47 audit + 6 low-conf); the Zane row cleared (ruling landed with prompt 1.2.0); a round-3 regroup on the new queue is pending. Standing human items: 2 third-party prose mentions no rule can reach (1 indexed), 1 committed-tree ruling over the `text_residue` records, the bulk pile |
| 14 | Podcast citation URLs | **closed** 2026-09-08 — all 47 ids resolved via YouTube oEmbed and matched all 47 episodes at Dice 1.00; frozen as `PODCAST_VIDEO_IDS`, 1,800/1,800 segments deep-linked to their start second, re-uploaded with 0 embed calls and verified live |
| 15 | §12 evaluation coverage | **mostly closed** 2026-09-08 — the precision metric is in §12's list; PDSRG/podcast have 120 retrieval probes; the degraded-guardrail path is pinned by tests and fixed a real defect. **Remaining**: probes measure retrieval only, so end-to-end coverage of those two corpora still needs written questions, and two §11 standing behaviors (conversation-start disclosure, prompt-injection) have no adversarial item because §12 fixes the split at 20/15/15 |
| 16 | Customer names in `question_original` | **closed into item 13** 2026-09-08 — it was the same defect seen through a different probe: the scrub had no rule for a name that is neither a salutation nor a closer. `SELF_INTRO_RE` closed it (11 spans redacted, the one public figure on `ACCEPTED_HONORIFIC_NAMES` correctly quoted verbatim) and the `my name is` probe is now 0. Residual-PII work continues under item 13; do not re-open this row |
| 17 | Answer-prompt tuning for `gpt-5.6-luna` | **open** — the frontier model paraphrases claims where gpt-5-mini quoted, so the claims-language gate withholds 51/125 dev answers (41%) and judged faithfulness is 0.57 (threshold 0.9). Retrieval (99.2% with the ranker off) and guardrails (escalation 10/10) are fine. Tighten the §11 answer instructions, then re-sweep; blocks customer-facing SSE together with item 12 |

## Log

Newest first, one entry per work item, 8 wrapped lines maximum (see Writing
entries). The five most recent live here; older ones are in
`docs/progress-archive.md`. Detail belongs in the commit, the code, or the
artifact it describes.

- **2026-09-09 (47)** — Dev eval sweep on gpt-5.6-luna (§12): item 5 **closed
  — ranker off** (sample recall@8 99.2% vs 75.2% on, n=125; the ranker's only
  win is one podcast probe), escalation 10/10, points-hit 0.9, item 12 has
  its first number (1 flag / 0 true — denominator 1). Answers measured for
  the first time: the claims-language gate withheld 51/125 drafts → **new
  item 17** (answer-prompt tuning). 520 agent calls; labels still item 8.

- **2026-09-09 (46)** — Full corpus regen on gpt-5.6-luna (§4): item 1 closed
  (quota arrived; both chat roles switched, embedding unchanged). Stage 2
  prompt 1.2.0 — the Zane ruling landed; queue 220 → 234 (182 PII + 47 audit
  + 6 low-conf), round-3 regroup pending (item 13). Stage 4: 923/104/14, two
  new cluster conflicts → **item 7 reopened** (owner task 3 carries both
  pairs). Index re-uploaded: 4,002 docs, 0 errors. Both stages byte-identical
  on rerun.

- **2026-09-09 (45)** — `stage2 --workers`: bounded thread pool for the
  canonicalization pass; workers never touch shared state, so completion
  order cannot reach the output (records sort; the cache is key-addressed).
  The 1,041-doc luna pass ran ~20× faster (≈25 min at 8 workers, inside the
  deployment's 333K TPM). 1 new test (**421 python**); runtime 112 unchanged.

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
