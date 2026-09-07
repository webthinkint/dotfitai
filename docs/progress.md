# Phase 1 — Build Progress

The status view: what is built, what is open, what runs next. Every agent run
reads this file end to end, so it is kept short on purpose — the rest lives in:

- `docs/decisions.md` — owner/curation rulings, grouped by area. Read the group
  for the area you are about to touch.
- `docs/progress-archive.md` — log entries older than the five kept below.
- `phase1-knowledge-assistant.md` — the plan (design decisions); `AGENTS.md` —
  the hard rules those produced.

## Status (§13 build order)

Numbers verified 2026-09-08. Python 396 tests green; runtime 111 tests green.

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
| Golden set | §12 | **complete except labeling** 2026-09-08 — the 250 are sampled, the 50 adversarial are **written** (`CURATED_ADVERSARIAL`), the probes are built. Remaining: label the 250 (item 8) | 650 current QA pairs → 250 items over 29 families (125/125) + 50 adversarial (25/25) + 120 PDSRG/podcast probes; `processed/golden/` |
| Podcast ASR | §7 | **transcribed + QC PASS, indexed, cited** — 47/47, 5-episode spot-check clean; citation URLs verified and stamped 2026-09-08 (item 14 closed). Remaining: speaker-map rewrite + re-upload (non-blocking) | 38.0 h audio → 35,050 phrases (~437K words); 37 eps × 2 speakers, 10 × 3; 1,800/1,800 segments deep-linked |
| Index + retrieval | §9–11 | **live** on `kb-main-v2`, now the code default on both sides (item 10's code half done). Remaining: the ranker call on a full sweep (item 5) | 3,996 docs — 1,080 pdsrg / 177 product / 10 menu / 1,800 podcast / 929 qa; re-uploaded 2026-09-08 with 0 embed calls; filtered + unfiltered smoke PASS |
| v1 runtime | §11 | **built + live** — the full §11 chain verified end to end and traced. Delivery mode is explicit: `Gated` for the service, `Live` for the CLI. Remaining: audit precision on a full sweep (item 12) | `runtime/`, 111 tests |
| SSE service | §11 | **built + live** 2026-09-08 — `POST /ask` streams disclosure/stage/delta/retraction/result; always `Gated`; config validated at startup. Remaining: item 12's number before it ships to customers | `runtime/src/DotFit.Agents.Service`; normal + escalation paths smoked live |
| §12 eval harness | §12 | **built + live** 2026-09-08 — `qa-pipeline eval` over `ask --json`; label-free metrics run today, label-dependent ones report `null` with a reason | first dev sweep (n=12): sample recall@8 100% (ranker off) / 83.3% (on), probes 100%, escalation 10/10 |

Artifacts: `processed/qa/`, `processed/pdsrg/`, `processed/aliases/`,
`processed/golden/`, `processed/index/`, `processed/eval/` (the one that
measures a live service, so its numbers are a dated reading, not a rerun).
Per-run counts live in each `summary.json`; numbers quoted here must match a
regenerated run.

## Open items (§14)

| # | Item | Status |
|---|---|---|
| 1 | Azure region + SKU | **partial** — services provisioned 2026-09-05, credentials in local `.env`; `text-embedding-3-large` and `gpt-5-mini` deployed and smoke-verified. Remaining: quota increase for the frontier chat deployment |
| 2 | products.json freshness owner | **open** — the blocker is naming the owner and the cadence; the instrument exists since 2026-09-08 (`scripts/products_diff.py`, diffs two exports by §5 section so the review sees changed *claims*) |
| 3 | Alias-table curation session | **closed** 2026-09-07 (pass 2) — outcomes are the `alias.py` curated constants, the worksheet is the session record. A pass 3 starts from the 2,292 unresolved Stage 2 mentions |
| 4 | PPTX disposition | parked |
| 5 | Semantic ranker on/off | **open, now measurable** — `qa-pipeline eval --ranker-ab` reports source recall@k both ways. First signal (dev, n=12) is **against** the ranker: 100% off vs 83.3% on. n=12 is a smoke, not the call — rerun on the full dev split before deciding |
| 6 | Stage 3 review-queue dispositions | **closed** 2026-09-05, queue 0 |
| 7 | Stage 4 review-queue dispositions | **open** — 2 `cluster_conflict` records (non-nested part_nos); owner picks or splits the cluster. Evidence quotes are in `stage4/documents.jsonl` |
| 8 | Golden-set labeling | **open, narrowed** — the 50 adversarial are written (2026-09-08); what remains is labeling the **250** with points-to-hit and expected sources. Nutritionist + support lead, ~2–3 days; §12 rubric in the worksheet header. Until then the harness reports those two metrics as `null` with a reason |
| 9 | Runtime live smoke | **closed** 2026-09-07 — root cause was the wedged index (item 10), not the service |
| 10 | Orphaned `kb-main` index | **open** — stuck mid-delete: `GET /indexes/kb-main` 404s with `"is being deleted"` while servicestats still counts it (7,992 docs, 213 MB), and the portal omits deleting-state indexes, so "gone from the UI" is not evidence. Not a blocker; consumes quota and storage. Owner: support ticket — the clean repro is one index that can neither serve documents nor finish deleting on a service where a throwaway index does both in seconds. The code half is done (2026-09-08): both `index_build.INDEX_NAME` and `RuntimeOptions.IndexName` default to `kb-main-v2`, a test pins each side, and no command needs `--index`. Remaining is the support ticket and, if the name is freed, deciding whether to rebuild under it |
| 11 | Answer agent sourced claim language from authority 3 | **fixed** 2026-09-07 — sources are tagged quotable (authority 1–2) vs context-only and the answer instructions fail closed; 4/4 live runs PASS. Remaining: 4 runs is signal, not a regression suite — coverage lands with the §12 eval harness, and re-test on the frontier deployment when item 1's quota arrives |
| 12 | Claims-audit precision under gating | **open, now instrumented** — the harness reports it on the adversarial-50 with its counts (a small denominator is not a strong claim), scoring the *draft* the audit saw. Still unmeasured on a full sweep: the first run flagged nothing, so precision is `null`, not good. Needed before the SSE service faces customers |
| 13 | Stage 2 review-queue dispositions | **open, triaged** — 222 flagged records, **203 of them `is_current` and live in the index** (154 residual-PII). `scripts/stage2_queue_triage.py` splits them into the 3 that must be read (residual-PII flag, no placeholder in the committed text) and 219 that bulk-disposition (165 placeholder-backed + 48 audit + 5 low-conf + 1 containment). Owner pass |
| 14 | Podcast citation URLs | **closed** 2026-09-08 — all 47 ids resolved via YouTube oEmbed and matched all 47 episodes at Dice 1.00; frozen as `PODCAST_VIDEO_IDS`, 1,800/1,800 segments deep-linked to their start second, re-uploaded with 0 embed calls and verified live |
| 15 | §12 evaluation coverage | **mostly closed** 2026-09-08 — the precision metric is in §12's list; PDSRG/podcast have 120 retrieval probes; the degraded-guardrail path is pinned by tests and fixed a real defect. **Remaining**: probes measure retrieval only, so end-to-end coverage of those two corpora still needs written questions, and two §11 standing behaviors (conversation-start disclosure, prompt-injection) have no adversarial item because §12 fixes the split at 20/15/15 |

## Log

Newest first, one entry per work item, 8 wrapped lines maximum (see Writing
entries). The five most recent live here; older ones are in
`docs/progress-archive.md`. Detail belongs in the commit, the code, or the
artifact it describes.

- **2026-09-08 (39)** — Owner worksheets for two stalled items (`1580825`).
  `stage2_queue_triage.py` groups item 13's 222 rows into one that must be read
  (**3** residual-PII flags with no placeholder in the committed text) and four
  that bulk-disposition (165 / 48 / 5 / 1), and reports the number that matters:
  **203 of 222 flagged records are `is_current` and live in the index** (154 of
  them residual-PII — the figure already tracked). `products_diff.py` diffs two
  `products.json` exports by §5 section, so item 2's monthly pass sees changed
  *claims*, not changed bytes. Naming that owner is still item 2. 396 tests.
- **2026-09-08 (38)** — SSE service shipped (§11) (`c94f828`) — the piece the
  plan called next. `DotFit.Agents.Service`: `POST /ask` streams
  `disclosure`/`stage`/`delta`/`retraction`/`result`, `GET /healthz`, config
  validated at **startup** so a bad `.env` fails the boot. Always `Gated`, no
  client choice; payloads narrower than the CLI's — no source `content`, no
  withheld draft, no failure reasons. Both paths smoked live, which caught
  camelCase request binding against snake_case responses: `conversation_id`
  never bound, so every turn re-announced. 14 new tests (**111 runtime**).
- **2026-09-08 (37)** — Podcast citation URLs (`fea97a1`) — **item 14 closed**.
  All 47 `archive.txt` ids resolved through YouTube oEmbed and matched all 47
  episodes at **Dice 1.00**: the mapping was never ambiguous, only unverified
  (the downloader wrote the titles out verbatim). Frozen as
  `PODCAST_VIDEO_IDS`; `scripts/podcast_archive_verify.py` is the session
  record. All **1,800** segments deep-link to their start second; re-uploaded
  3,996/3,996, **0 embed calls**, 0 errors, live-verified. Correction: `wc -l`
  says 46 — the file has no trailing newline. 5 new tests.
- **2026-09-08 (36)** — §12 eval harness shipped (`76fcec6`) — `evaluate.py` +
  `qa-pipeline eval` over `ask --json`. **It does not wait on item 8**: each
  item's source doc id is `qa-<id>` (250/250), so source recall is label-free.
  Scores source/probe recall, citation rate, escalation accuracy, claims-audit
  precision (item 12) and RAGAS-*style* judged metrics; label-dependent ones
  report `null` with a reason. First live sweep (dev, n=12): sample recall@8
  **100% ranker-off vs 83.3% ranker-on**, probes 100%, escalation **10/10**.
  35 new tests (**391 python**). Item 5 has data now.
- **2026-09-08 (35)** — Golden-set inputs completed (§12) (`76fcec6`). The **50
  adversarial items are written** (`CURATED_ADVERSARIAL`, 20/15/15, one
  behavior per item) and derive `adversarial.md` plus a new
  `adversarial.jsonl`; counts, uniqueness and category drift now raise. Added
  **120 retrieval probes** over PDSRG + podcast (39 + 47 strata, ≥1 each) — the
  corpora that are 72% of the index and had no golden item (item 15). The 250
  sample, worksheet and summary regenerated byte-identical. Still owner work:
  labeling the 250 (item 8). 18 new tests (**356 python**).

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
