# Phase 1 — Build Progress

The status view: what is built, what is open, what runs next. Every agent run
reads this file end to end, so it is kept short on purpose — the rest lives in:

- `docs/decisions.md` — owner/curation rulings, grouped by area. Read the group
  for the area you are about to touch.
- `docs/progress-archive.md` — log entries older than the five kept below.
- `phase1-knowledge-assistant.md` — the plan (design decisions); `AGENTS.md` —
  the hard rules those produced.

## Status (§13 build order)

Numbers verified 2026-09-07. Python 338 tests green; runtime 87 tests green.

| Component | Plan § | State | Verified output |
|---|---|---|---|
| QA Stage 0 — PII scrub | §4 | **done** | 1,051 `.docx` (1,103 − 51 duplicates − 1 zero-byte; 43 md5 groups, log in `processed/qa/runs/`) |
| QA Stage 1 — parse & classify | §4 | **done** | 777 qa_email / 264 expert_note / 0 other; 766 `thread_date`; review queue 0; 10 unanswerable excluded (6 no-answer, 4 blank) |
| PDSRG extraction gate | §6.1 | **passed**, human-verified | 5 stress PDFs |
| PDSRG chunking | §6.2–4 | **done** | 39 docs → 1,080 chunks (~404K tokens, median 349); 950 with part_nos; 53 discontinued-stamped; 1 atomic oversize table |
| Alias table | §5 | **done**, v1.3.0 | 51 indexed SKUs → 31 families; worksheet 19/19 attested; 13 deterministic aliases + 1 LLM-only |
| QA Stage 2 — canonicalize | §4 | **done** — full run 2026-09-06, alias-1.3.0 regen 2026-09-07 | 1,041 canonical records (prompt 1.1.0); 676 with products (50 part_nos); 306 currency-cued; queue 222 (168 PII + 48 audit + 1 containment + 5 low-conf) — dispositions are item 13 |
| QA Stage 4 — dedup & currency | §4 | **done** 2026-09-07 | 919 current / 106 superseded_currency / 16 superseded_dup; 17 clusters (1 conflict); 114 judgments (106 dependent / 8 independent / 0 low-conf); queue 2 (cluster conflict) |
| Podcast segmentation | §7 | **done** — contract in `podcast.py` | 47 episodes → 1,800 segments (median 76 s / 251 words) |
| Golden-set sampling | §12 | **sampled** 2026-09-07 — rules in `golden.py`. Remaining: labeling (item 8), eval harness + its coverage gaps (item 15) | 650 current QA pairs → 250 items over 29 families; splits 125/125 + adversarial 25/25 per §12; `processed/golden/` |
| Podcast ASR | §7 | **transcribed + QC PASS, indexed** — 47/47, 5-episode spot-check clean. Remaining: speaker-map rewrite + re-upload (non-blocking), citation URLs (item 14) | 38.0 h audio → 35,050 phrases (~437K words); 37 eps × 2 speakers, 10 × 3 |
| Index + retrieval | §9–11 | **live** on `kb-main-v2`; the code default is still the orphaned `kb-main` (item 10). Remaining: golden-set eval, ranker toggle (item 5) | 3,996 docs — 1,080 pdsrg / 177 product / 10 menu / 1,800 podcast / 929 qa; filtered + unfiltered retrieval smoke PASS |
| v1 runtime | §11 | **built + live** 2026-09-07 — the full §11 chain (guardrail → rewrite → aliases → hybrid search → grounded cited answer → post-check) verified end to end and traced. Delivery mode is explicit: `Gated` for the service, `Live` for the CLI. Remaining: audit precision (item 12) | `runtime/`, 87 tests |

Artifacts: `processed/qa/`, `processed/pdsrg/`, `processed/aliases/`,
`processed/golden/`, `processed/index/`. Per-run counts live in each
`summary.json`; numbers quoted here must match a regenerated run.

## Open items (§14)

| # | Item | Status |
|---|---|---|
| 1 | Azure region + SKU | **partial** — services provisioned 2026-09-05, credentials in local `.env`; `text-embedding-3-large` and `gpt-5-mini` deployed and smoke-verified. Remaining: quota increase for the frontier chat deployment |
| 2 | products.json freshness owner | **open** — name the owner, set the monthly diff cadence (the 8 PDSRG-only gaps closed 2026-09-01) |
| 3 | Alias-table curation session | **closed** 2026-09-07 (pass 2) — outcomes are the `alias.py` curated constants, the worksheet is the session record. A pass 3 starts from the 2,292 unresolved Stage 2 mentions |
| 4 | PPTX disposition | parked |
| 5 | Semantic ranker on/off | **open** — week 3–4, decide empirically; measurable since entry (30) fixed the re-rank to order on the reranker score |
| 6 | Stage 3 review-queue dispositions | **closed** 2026-09-05, queue 0 |
| 7 | Stage 4 review-queue dispositions | **open** — 2 `cluster_conflict` records (non-nested part_nos); owner picks or splits the cluster. Evidence quotes are in `stage4/documents.jsonl` |
| 8 | Golden-set labeling | **open** — label the 250 sampled items and write 50 adversarial (`processed/golden/`, `qa-pipeline golden`). Nutritionist + support lead, ~2–3 days; §12 rubric in the worksheet header |
| 9 | Runtime live smoke | **closed** 2026-09-07 — root cause was the wedged index (item 10), not the service |
| 10 | Orphaned `kb-main` index | **open** — stuck mid-delete: `GET /indexes/kb-main` 404s with `"is being deleted"` while servicestats still counts it (7,992 docs, 213 MB), and the portal omits deleting-state indexes, so "gone from the UI" is not evidence. Not a blocker; consumes quota and storage. Owner: support ticket — the clean repro is one index that can neither serve documents nor finish deleting on a service where a throwaway index does both in seconds. On resolution: rebuild as `kb-main` and drop the `--index` override, or keep `kb-main-v2` and flip the runtime default (`RuntimeOptions.cs:65`) |
| 11 | Answer agent sourced claim language from authority 3 | **fixed** 2026-09-07 — sources are tagged quotable (authority 1–2) vs context-only and the answer instructions fail closed; 4/4 live runs PASS. Remaining: 4 runs is signal, not a regression suite — coverage lands with the §12 eval harness, and re-test on the frontier deployment when item 1's quota arrives |
| 12 | Claims-audit precision under gating | **open** — gating (§11) makes a `claims_language` false positive cost an answered question rather than a trace line, and the audit's precision rests on 4 live runs. §12 must report it on the adversarial-50 before the SSE service ships; if it is poor the lever is the audit prompt, not the gate |
| 13 | Stage 2 review-queue dispositions | **open** — 222 flagged records (168 residual-PII + 48 audit sample + 1 containment + 5 low-confidence). Flagged records are redacted, not withheld: 154 are `is_current` and indexed, and 165 of the 168 carry a `[NAME]`/`[CUSTOMER]` placeholder — the 3 without need a spot check first. Owner pass over `stage2/review_queue.jsonl` |
| 14 | Podcast citation URLs | **open** — all 1,800 segments stamp `citation_url: null`, so §7.4's "as covered at 14:32 in *Creatine FAQs*" renders linkless: the `archive.txt` mapping is 47 video IDs with no titles and is unverified. Verify it (or drop the link from the citation format), then re-shape + re-upload the podcast docs |
| 15 | §12 evaluation coverage | **open** — three gaps to settle when the eval harness lands: the claims-audit precision metric (item 12) is absent from §12's metric list; all 250 sampled items come from the QA pool, so PDSRG (1,080 docs) and podcast (1,800) — 72% of the index — have no golden question; and the 100% escalation target does not cover the fail-open (degraded) guardrail |

## Log

Newest first, one entry per work item, 8 wrapped lines maximum (see Writing
entries). The five most recent live here; older ones are in
`docs/progress-archive.md`. Detail belongs in the commit, the code, or the
artifact it describes.

- **2026-09-07 (33)** — Documentation review pass (docs only): the plan's §14 was
  missing definitions for items 7–11 and carried a stale close date on 3; §4
  Stage 5's "no chunking" and §3's corpus arithmetic (1,103 − 51 − 1 = 1,051)
  were both wrong against the artifacts. Three gaps between shipped behavior and
  the trackers became **open items 13–15**: the Stage 2 queue (222), null podcast
  `citation_url` (1,800 segments), and §12's coverage (no precision metric, no
  PDSRG/podcast stratum, no degraded-guardrail case). `AGENTS.md` gained
  `podcast.py`/`cli.py` rows, `pipeline/README.md` the `kb-main` warning. 338 tests.
- **2026-09-07 (32)** — `progress.md` split three ways so the per-session read
  stops growing: this file keeps the status table, open items and the newest
  five log entries; owner rulings moved verbatim to `docs/decisions.md`, rotated
  entries to `docs/progress-archive.md`. Table rows lost their narrative —
  mechanism to the docstrings that already own it, history to the log. **36 KB →
  10 KB** read every session, nothing deleted. Cross-refs repointed in
  `AGENTS.md`, the plan, `pipeline/README.md`, `alias.py`, `stage4.py`. Docs
  only. 338 tests.
- **2026-09-07 (31)** — Answer delivery gated on the post-check (§11) (`9751a34`).
  No partial gate is possible — citation markers are only known at the last
  delta and the claims audit needs the whole answer — so the mode is explicit:
  `AskOptions.StreamMode` is `Gated` (hold deltas, on FAIL deliver the templated
  handoff and never the text — SSE default) or `Live` (stream, retract after the
  fact — CLI default). The failing draft stays in `AnswerText` for tracing. This
  turns audit false positives into refusals, hence open item 12. 4 new tests
  (**87 runtime**).
- **2026-09-07 (30)** — Runtime review pass (§11): eight findings fixed
  (`7f68c08`). The load-bearing one: the authority re-rank ordered on the fused
  retrieval score even when the semantic ranker ran, so `--semantic` paid for
  the ranker and discarded its ordering — open item 5 was not measurable as
  posed. Also `RuntimeNeeds` now mirrors `azure_config.py`'s `require=` subsets,
  and grouped/ranged citation markers parse. 16 new tests (**83 runtime**, 338
  python unchanged). Live smoke on `kb-main-v2` confirms both; `ask` PASS.
- **2026-09-07 (29)** — Retrieval unblocked (§9/§11) + claim-sourcing fix
  (`c23eac2`, `252bf12`, `d46ab60`). `kb-main` was a single wedged index — no
  document op returned bytes, while the control plane and a throwaway index
  were fine — and `--reset` could not recover it, so it was rebuilt as
  `kb-main-v2`: **3,996/3,996 uploaded, 0 errors, 0 embed calls** (open item 10
  tracks the orphan). The first live `ask` caught the answer agent lifting claim
  wording from an authority-3 Q&A (open item 11). 4 new python tests (**338
  python**), 3 new runtime (**67 runtime**), all on `gpt-5-mini`.

## Writing entries

Update **Status** (numbers), **Open items**, and add one **Log** entry per work
item, newest first, with plan-§ refs and the commit SHA. Then **rotate**: move
whatever falls past the newest five to the top of `docs/progress-archive.md`, so
this file's length stays flat instead of growing one entry per work item.

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
