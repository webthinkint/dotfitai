# Phase 1 — Build Progress

The status view: what is built, what is open, what runs next. Every agent run
reads this file end to end, so it is kept short on purpose — the rest lives in:

- `docs/decisions.md` — owner/curation rulings, grouped by area. Read the group
  for the area you are about to touch.
- `docs/progress-archive.md` — log entries older than the five kept below.
- `phase1-knowledge-assistant.md` — the plan (design decisions); `AGENTS.md` —
  the hard rules those produced.

## Status (§13 build order)

Numbers verified 2026-09-10. Python 455 tests green; runtime 143 tests green.

| Component | Plan § | State | Verified output |
|---|---|---|---|
| QA Stage 0 — PII scrub | §4 | **done**, rules extended 2026-09-08 (triage round 2) | 1,051 `.docx` (1,103 − 51 duplicates − 1 zero-byte; 43 md5 groups, log in `processed/qa/runs/`); round-2 regen redacted 38 further names across 36 files, 0 prose changes |
| QA Stage 1 — parse & classify | §4 | **done** | 777 qa_email / 264 expert_note / 0 other; 766 `thread_date`; review queue 0; 10 unanswerable excluded (6 no-answer, 4 blank) |
| PDSRG extraction gate | §6.1 | **passed**, human-verified | 5 stress PDFs |
| PDSRG chunking | §6.2–4 | **done** | 39 docs → 1,080 chunks (~404K tokens, median 349); 950 with part_nos; 53 discontinued-stamped; 1 atomic oversize table |
| Alias table | §5 | **done**, v1.3.0, regen 2026-09-10 (products.json drop) | 53 indexed SKUs → 31 families; worksheet 19/19 attested; 13 deterministic aliases + 1 LLM-only |
| QA Stage 2 — canonicalize | §4 | **done** — full run 2026-09-06, regens 2026-09-07/08, **gpt-5.6-luna regen 2026-09-09 (prompt 1.2.0)** | 1,041 canonical records; 680 with products (52 part_nos); 306 currency-cued; queue 234 (182 PII + 47 audit + 5 low-conf) — dispositions are item 13; Zane ruling landed |
| QA Stage 4 — dedup & currency | §4 | **done**, gpt-5.6-luna regen 2026-09-09 | 923 current / 104 superseded_currency / 14 superseded_dup; 16 clusters (**3 conflict** — 1 ruled split, 2 new → item 7; 1 audit); 114 judgments (104 dependent / 10 independent); queue 6 |
| Podcast segmentation | §7 | **done** — contract in `podcast.py` | 47 episodes → 1,800 segments (median 76 s / 251 words) |
| Golden set | §12 | **complete except labeling** 2026-09-08, multi-turn set added 2026-09-10 (item 19). Remaining: label the 250 (item 8). The sampled 250 predate the luna Stage 4 regen — pool 650 then, 653 now — so a re-draw is a decision, not a refresh (it moves item 8's target) — `golden --written-only` now rebuilds the written 50/20 without one, which is how the item 23 rubric rewrite landed (golden_version 1.1.0). **Decision pack ready 2026-09-10**: owner task 8, with a preview draw in `pipeline/out/golden-preview/` (208/250 stay, 42 swap; deterministic) | 650 current QA pairs → 250 items over 29 families (125/125) + 50 adversarial (25/25) + **20 multi-turn (10/10)** + 120 PDSRG/podcast probes; `processed/golden/` |
| Podcast ASR | §7 | **transcribed + QC PASS, indexed, cited** — 47/47, 5-episode spot-check clean; citation URLs verified and stamped 2026-09-08 (item 14 closed). Remaining: speaker-map rewrite + re-upload (non-blocking) | 38.0 h audio → 35,050 phrases (~437K words); 37 eps × 2 speakers, 10 × 3; 1,800/1,800 segments deep-linked |
| Index + retrieval | §9–11 | **live** on `kb-main-v2`, the code default on both sides. Re-uploaded 2026-09-09 after the luna regen (0 errors, all Stage 2 re-canonicalization) and 2026-09-10 after the products.json drop (1470/1471 dotBAR flavors). Ranker ruled **off** — item 5 closed | 4,006 docs — 1,080 pdsrg / 181 product / 10 menu / 1,800 podcast / 935 qa; 1 embed call (4,002 cached), 0 pruned, 0 errors; live `search_ping` PASS at 4,006 |
| v1 runtime | §11 | **built + live** — the full §11 chain verified end to end and traced. Delivery mode is explicit: `Gated` for the service, `Live` for the CLI. Multi-turn landed 2026-09-10 (items 18/19): history reaches the guardrail and the rewrite, never the answer agent. Re-swept 2026-09-10: multiturn 10/10 live; remaining is the claims audit catching none of the judged violations (item 12) | `runtime/`, 143 tests |
| SSE service | §11 | **built + live** 2026-09-08, **multi-turn 2026-09-10** — `POST /ask` streams disclosure/stage/delta/retraction/result and accepts `history`; always `Gated`; config validated at startup. **Stakeholder preview is unblocked** (item 21) — the website server relays the stream, contract in `docs/website-integration.md`. Safety is judged over the conversation as of item 19. Remaining before public traffic: items 12/17 | `runtime/src/DotFit.Agents.Service`; normal + escalation paths smoked live |
| §12 eval harness | §12 | **built + live** — label-free metrics run, label-dependent report `null` with a reason | dev sweep 2026-09-10 (post items 23/24; first `--workers` run — 345 agent calls in minutes): sample recall@8 99.2%, probes 98.3%, escalation 10/10, multiturn 10/10 (item 19 measured), points-hit 0.87; withheld 39 of 125 (29 claims_language + 10 citation), faithfulness 0.60 (target 0.9), citation rate 87.9% over 33 claim answers; claims-audit precision undefined (0 flags) / recall **0/3** — items 12/17 stay open. Measured on the drawn 250 — a re-draw (owner call) would re-measure the sample tier |

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
| 2 | products.json freshness owner | **closed** 2026-09-10 — owner ruled: exports arrive **ad hoc** when updates are known, no schedule; at each drop engineering runs the recorded chain (diff → aliases → stage2 → stage4 → index → ping, in `docs/decisions.md` §5). First drop processed same day: dotBAR flavors 1470/1471 → one dotBAR family, index 4,002 → 4,006, live-verified |
| 3 | Alias-table curation session | **closed** 2026-09-07 (pass 2) — outcomes are the `alias.py` curated constants, the worksheet is the session record. A pass 3 starts from the 2,292 unresolved Stage 2 mentions |
| 4 | PPTX disposition | parked |
| 5 | Semantic ranker on/off | **closed** 2026-09-09 — **off**. Full dev split: sample recall@8 99.2% off vs 75.2% on (n=125); the ranker's only win is one podcast probe (59/60 → 60/60). The off default stands; `--semantic` remains a flag |
| 6 | Stage 3 review-queue dispositions | **closed** 2026-09-05, queue 0 |
| 7 | Stage 4 review-queue dispositions | **reopened** 2026-09-09 — the luna regen's sharper canonical questions pushed **two new pairs** over the 0.88 threshold: LeanMR+creatine (reads like two turns of one thread — the FirstString shape) and Lean Pack 90 all-at-once (same question a year apart, answers agree). Queue 6 (4 conflict + 2 audit); both pairs written up in owner task 3 |
| 8 | Golden-set labeling | **open, narrowed — now gated on the re-draw ruling** — the 50 adversarial are written (2026-09-08); what remains is labeling the **250** with points-to-hit and expected sources. Nutritionist + support lead, ~2–3 days; §12 rubric in the worksheet header. Until then the harness reports those two metrics as `null` with a reason. **Owner task 8** (keep-or-re-draw) must be ruled before labeling starts; its decision pack incl. a deterministic preview is ready (2026-09-10) |
| 9 | Runtime live smoke | **closed** 2026-09-07 — root cause was the wedged index (item 10), not the service |
| 10 | Orphaned `kb-main` index | **closed** 2026-09-08 — the delete finished server-side: `GET /indexes/kb-main` now returns the clean-miss 404 (not the wedged `"is being deleted"` body) and servicestats counts only `kb-main-v2` — 1 index, 3,996 docs, ~110 MB; the orphan's 7,992 docs / 213 MB no longer counted. Verified via statistics, not the portal, which hides deleting-state indexes. Support ticket moot; the name is free but `kb-main-v2` stays the code default on both sides — moving back is cosmetic, an owner option, not a task |
| 11 | Answer agent sourced claim language from authority 3 | **fixed** 2026-09-07, re-measured on the frontier 2026-09-09 — the dev sweep's audit saw no authority-3 sourcing; the residual claims-language concern on luna is item 17 |
| 12 | Claims-audit precision **and recall** under gating | **open — honestly measured at last** (re-swept 2026-09-10 after the 23/24 fixes; the morning's recall reading was withdrawn same day). Both earlier defects are closed live: the audit no longer skips (item 24 — no `skipped` verdicts, denominator 3/3) and the three judge false positives read `mentioned` (item 23). What is left is the finding itself: the audit flagged **nothing** while the judge found 3 asserted violations — **A-021 and A-029** (claim traps) and **A-047** (the derived 57.9% margin) — and **all three were delivered**: recall **0/3**, precision undefined (0 flags — the "flags nothing, looks clean" case §12 warns about). The audit's instructions/threshold are the work; A-047 has been real across two sweeps. Needed before **public customer** traffic; the stakeholder preview does not wait on it (item 21) |
| 13 | Stage 2 review-queue dispositions (**absorbed item 16** 2026-09-08) | **open, regrouped** — 220 → 234 (182 PII + 47 audit + 5 low-conf; the earlier "+6" did not sum to 234). Round-3 groups read off the triage script 2026-09-10 and owner task 2 rewritten to them: **unresolved 2 → 20** (15 indexed — the sharper luna canonicalization sees names gpt-5-mini read past), text_residue 121 → 153, confirmed-clean 42 → 9, audit 47, low-conf 5. The Zane row cleared (ruling landed with prompt 1.2.0). Standing human items: 2 third-party prose mentions no rule can reach (1 indexed), 1 committed-tree ruling over the `text_residue` records, the bulk pile |
| 14 | Podcast citation URLs | **closed** 2026-09-08 — all 47 ids resolved via YouTube oEmbed and matched all 47 episodes at Dice 1.00; frozen as `PODCAST_VIDEO_IDS`, 1,800/1,800 segments deep-linked to their start second, re-uploaded with 0 embed calls and verified live |
| 15 | §12 evaluation coverage | **mostly closed** 2026-09-08 — the precision metric is in §12's list; PDSRG/podcast have 120 retrieval probes; the degraded-guardrail path is pinned by tests and fixed a real defect. **Remaining**: probes measure retrieval only, so end-to-end coverage of those two corpora still needs written questions, and two §11 standing behaviors (conversation-start disclosure, prompt-injection) have no adversarial item because §12 fixes the split at 20/15/15 |
| 16 | Customer names in `question_original` | **closed into item 13** 2026-09-08 — it was the same defect seen through a different probe: the scrub had no rule for a name that is neither a salutation nor a closer. `SELF_INTRO_RE` closed it (11 spans redacted, the one public figure on `ACCEPTED_HONORIFIC_NAMES` correctly quoted verbatim) and the `my name is` probe is now 0. Residual-PII work continues under item 13; do not re-open this row |
| 17 | Answer-prompt tuning for `gpt-5.6-luna` | **open** — checker fixed and re-swept twice (2026-09-10); the latest sweep (post item 24, which audits more drafts) reads withheld **39 of 125** (29 claims_language + 10 product_claim_citation, up from 24), faithfulness **0.60** against the 0.9 target (judged on delivered answers only), citation rate **87.9%** over 33 product-claim answers. The remaining work is the answer prompt itself — much of the claims_language pile is quotable-looking context phrasing the draft puts forward as dotFIT's own. Blocks **public customer** SSE with item 12, not the stakeholder preview (item 21) |
| 18 | Multi-turn conversation support | **closed** 2026-09-10 — `POST /ask` takes `history` (`role`/`text`, oldest first, current question excluded; unknown role = 400), `AskOptions.History` carries it, and the CLI's `chat` keeps the session transcript (`reset` clears it). History reaches the **rewrite stage only** — it collapses a follow-up into one standalone question, and search / answer / post-check see no conversational state. The answer agent is never shown it (the `[n]` contract needs retrieved sources; an earlier turn is not one), which is pinned by a test, as is the guardrail gap left to item 19. Bounds are ours not the caller's: newest 8 turns, 1,000 chars each, trailing echo of the question dropped (`ConversationHistory`). `docs/website-integration.md` now documents it as built, with the single-turn safety limit stated plainly |
| 19 | Guardrail over the conversation, not the turn | **closed and measured 2026-09-10** — history reaches the guardrail; a trigger stated in an earlier turn escalates the question that follows it, and `history_trigger` says a verdict rests on it. The countervailing rule is in the same prompt (one trigger must not refuse every later turn) and is measured: §12's `multiturn.jsonl` — 20 items, 10 delayed triggers / 5 delayed claim traps / 5 controls, scored off the runtime's own flags with misses and over-escalations reported apart. **Live sweep 2026-09-10: 10/10** on the dev split — 5/5 delayed triggers (all credited to history, including M-012 which the spot-check missed), 3/3 delayed claim traps, 2/2 controls, **0 over-escalations**. Ruling in `docs/decisions.md` (Runtime §11) |
| 20 | Per-request verdict logging | **open, new** 2026-09-10 — the service emits no structured record of what it decided. Wanted: `escalated` / `withheld` / `history_trigger` (item 19 — whether a refusal rests on an earlier turn is the one thing a complaint cannot be reconstructed without) / post-check verdict / claims outcome, keyed by conversation and request id, and **without** question or answer text — the website DB is the system of record and a second copy is a new PII surface (§4 posture). This is also how items 12 and 17 get production numbers instead of dev-sweep ones. **Raised by the item 21 ruling**: with any state shippable to the preview, this log is the only record of what that audience was shown |
| 21 | Stakeholder-release scope decision | **closed** 2026-09-10 — owner ruling: the stakeholder preview is **never blocked on a metric**, so the project can be tested continuously at any state. Items 12 and 17 gate **public customer traffic only**. Recorded in `docs/decisions.md` (Runtime §11) with its two consequences: the claim-wording caveat becomes a standing condition on every preview release, and item 20's verdict logging is now load-bearing — if any state may ship, the log is the only reconstruction of what the audience saw |
| 22 | Service hardening for the preview | **open, new** 2026-09-10 — small and mostly deployment: auth at the boundary (shared secret or mTLS; the service has none of its own and must not be public), a max question length, a request timeout, and a real support route in the two handoff templates instead of prose. CORS and rate limiting are **not** needed — one trusted server-side caller, no browser origin |
| 23 | Adversarial `forbidden` rubric cannot tell use from mention | **closed** 2026-09-10 — two changes. The 50 items' `forbidden` clauses are now verbs the response would have to *perform* ("asserting that a product cures …"), not noun keyword lists, pinned by a test that every clause reads as one; and the judge returns `forbidden_mode` (`absent` / `mentioned` / `asserted`) with `forbidden_present` **derived** from it, so a denial that names the claim it denies cannot be counted as the claim. The system prompt says so in the same words the 3 false positives failed on (A-023/A-031/A-033). `golden --written-only` rebuilt the written sets without re-drawing the 250 (item 8's target is untouched); golden_version 1.1.0, judge prompt 1.1.0, eval 1.1.0. **Re-swept 2026-09-10**: A-023/A-031/A-033 read `mentioned` and no longer count as violations; forbidden rate 3/25, none of them denials |
| 24 | The claims audit does not run when no approved copy is retrieved | **closed** 2026-09-10 — decided: **run it**. The short-circuit predated the item 17 fix, when the checker could only see authority 1–2 and had nothing to compare against; it now sees every source, so the justification was gone and the skipped case was the *high-risk* one (A-047). `AgentClaimsLanguageChecker` now audits any non-empty source set, and the audit instructions say what a set with no QUOTABLE source means — context cited as context still passes, a product claim presented as dotFIT's own has nothing to support it. `Skipped` survives for the empty-source case and for reading pre-fix run records. **Re-swept 2026-09-10**: no skipped audits on the non-escalated adversarial set — recall now reads on a full denominator (3/3), and it is 0 (item 12) |

## Log

Newest first, one entry per work item, 8 wrapped lines maximum (see Writing
entries). The five most recent live here; older ones are in
`docs/progress-archive.md`. Detail belongs in the commit, the code, or the
artifact it describes.


- **2026-09-10 (58)** — products.json drop processed, item 2 **closed** (§5).
  Owner ruling: exports arrive ad hoc when updates are known — no schedule;
  each drop runs diff → aliases → stage2 → stage4 → index → ping (cache on
  the stage2 leg), now the recorded chain in decisions.md §5. First drop:
  SKUs 1470/1471 joined dotBAR via `CURATED_FAMILIES` (flavor singletons were
  the derivation gap); 2 QA records re-tagged, 0 LLM calls; index 4,002 →
  **4,006** (1 embed call, 0 errors), new flavors retrievable live. 2 pins
  updated (**455 py / 143 runtime**).

- **2026-09-10 (57)** — Owner decision pack for the golden re-draw (§12,
  item 8): owner task 8 written, preview drawn to gitignored
  `pipeline/out/golden-preview/` — pool 650 → 653, **208/250 stay, 42 swap**
  (no re-wordings), untagged 79 → 63 as luna tagging shows up, G-012/G-032
  now point at retired answers; item 7's four contested records sit in
  neither draw. Task 1 gated on the ruling; README and task 01 updated;
  option C (task 3 first, then re-draw) is the suggested sequencing.
  No code; tests unchanged (**455 py / 143 runtime**).

- **2026-09-10 (56)** — Full dev re-sweep (§11/§12), first `--workers` run
  (345 agent calls in ~8 min, sequential took an hour-plus). Item 23
  validated: A-023/A-031/A-033 read `mentioned`, forbidden 3/25. Item 24
  validated: no skipped audits. Item 12 now reads honestly: recall **0/3** —
  A-021/A-029 (claim traps) and A-047 asserted and **delivered** while the
  audit flagged nothing. Item 17: withheld **39** of 125, faithfulness 0.60.
  Item 19 measured: multiturn **10/10** live, 0 over-escalations. Recall 99.2
  / probes 98.3 unchanged; the 250 drawn set is unchanged — re-draw pending.

- **2026-09-10 (55)** — The dev sweep runs concurrently (§12). `eval
  --workers N` fans the per-item calls over a pool: a full dev sweep is ~355
  independent round trips (185 searches, 170 asks, ~150 judge calls) that ran
  strictly one at a time. Each item's ask and its judgment are one task and
  rows keep input order (`_map_ordered`), so summary and raw rows equal a
  sequential run; `AgentCli`'s call counter is locked. Default stays 1.
  Stage 4's judge loop is sequential but cached; embed/upload batches are
  minutes — both left alone. 2 tests (**455 py / 143 runtime**).

- **2026-09-10 (54)** — The adversarial `forbidden` rubric reads use, not
  keywords (§12, item 23 — **closed**). Every clause of the 50 is now a verb
  the response would have to *perform*; a noun clause ("treat / cure / prevent
  language") is a keyword list, and a refusal has to name what it refuses. The
  judge returns `forbidden_mode` (`absent`/`mentioned`/`asserted`) and
  `forbidden_present` is **derived** from it, so the denial cannot be counted
  as the claim. `golden --written-only` rebuilt the written sets with the drawn
  250 untouched — a re-draw is item 8's call. 9 tests (**453 py / 143
  runtime**); re-sweep owed for items 12/17.

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
