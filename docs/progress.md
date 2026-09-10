# Phase 1 — Build Progress

The status view: what is built, what is open, what runs next. Every agent run
reads this file end to end, so it is kept short on purpose — the rest lives in:

- `docs/decisions.md` — owner/curation rulings, grouped by area. Read the group
  for the area you are about to touch.
- `docs/progress-archive.md` — log entries older than the five kept below.
- `phase1-knowledge-assistant.md` — the plan (design decisions); `AGENTS.md` —
  the hard rules those produced.

## Status (§13 build order)

Numbers verified 2026-09-10. Python 455 tests green; runtime 162 tests green.

| Component | Plan § | State | Verified output |
|---|---|---|---|
| QA Stage 0 — PII scrub | §4 | **done**, rules extended 2026-09-08 (triage round 2) | 1,051 `.docx` (1,103 − 51 duplicates − 1 zero-byte; 43 md5 groups, log in `processed/qa/runs/`); round-2 regen redacted 38 further names across 36 files, 0 prose changes |
| QA Stage 1 — parse & classify | §4 | **done** | 777 qa_email / 264 expert_note / 0 other; 766 `thread_date`; review queue 0; 10 unanswerable excluded (6 no-answer, 4 blank) |
| PDSRG extraction gate | §6.1 | **passed**, human-verified | 5 stress PDFs |
| PDSRG chunking | §6.2–4 | **done** | 39 docs → 1,080 chunks (~404K tokens, median 349); 950 with part_nos; 53 discontinued-stamped; 1 atomic oversize table |
| Alias table | §5 | **done**, v1.3.0, regen 2026-09-10 (products.json drop) | 53 indexed SKUs → 31 families; worksheet 19/19 attested; 13 deterministic aliases + 1 LLM-only |
| QA Stage 2 — canonicalize | §4 | **done** — full run 2026-09-06, regens 2026-09-07/08, **gpt-5.6-luna regen 2026-09-09 (prompt 1.2.0)** | 1,041 canonical records; 680 with products (52 part_nos); 306 currency-cued; queue 234 (182 PII + 47 audit + 5 low-conf) — dispositions are item 13; Zane ruling landed |
| QA Stage 4 — dedup & currency | §4 | **done**, gpt-5.6-luna regen 2026-09-09, conflicts ruled 2026-09-10 | 923 current / 104 superseded_currency / 14 superseded_dup; 16 clusters (3 conflict — all ruled split; 1 audit); 114 judgments (104 dependent / 10 independent); queue 2 (audit sample) |
| Podcast segmentation | §7 | **done** — contract in `podcast.py` | 47 episodes → 1,800 segments (median 76 s / 251 words) |
| Golden set | §12 | **re-drawn 2026-09-10** (item 8 ruled: re-draw); **complete except labeling**. The 250 now come from the post-luna pool (653) — 208 of the previous draw stay, 42 swap, `(untagged)` 79 → 63, 29 families and the 125/125 split unchanged; the two items aimed at retired answers (G-012, G-032) are gone. The written 50 adversarial / 20 multi-turn and the 120 probes were not touched by the draw. Remaining: label the 250 (item 8) | 653 current QA pairs → 250 items over 29 families (125/125) + 50 adversarial (25/25) + **20 multi-turn (10/10)** + 120 PDSRG/podcast probes; `processed/golden/` |
| Podcast ASR | §7 | **transcribed + QC PASS, indexed, cited** — 47/47, 5-episode spot-check clean; citation URLs verified and stamped 2026-09-08 (item 14 closed). Remaining: speaker-map rewrite + re-upload (non-blocking) | 38.0 h audio → 35,050 phrases (~437K words); 37 eps × 2 speakers, 10 × 3; 1,800/1,800 segments deep-linked |
| Index + retrieval | §9–11 | **live** on `kb-main-v2`, the code default on both sides. Re-uploaded 2026-09-09 after the luna regen (0 errors, all Stage 2 re-canonicalization) and 2026-09-10 after the products.json drop (1470/1471 dotBAR flavors). Ranker ruled **off** — item 5 closed | 4,006 docs — 1,080 pdsrg / 181 product / 10 menu / 1,800 podcast / 935 qa; 1 embed call (4,002 cached), 0 pruned, 0 errors; live `search_ping` PASS at 4,006 |
| v1 runtime | §11 | **built + live** — the full §11 chain verified end to end and traced. Delivery mode is explicit: `Gated` for the service, `Live` for the CLI. Multi-turn landed 2026-09-10 (items 18/19): history reaches the guardrail and the rewrite, never the answer agent. Re-swept 2026-09-10: multiturn 10/10 live; remaining is the claims audit catching none of the judged violations (item 12) | `runtime/`, 162 tests |
| SSE service | §11 | **built + live** 2026-09-08, **multi-turn 2026-09-10** — `POST /ask` streams disclosure/stage/delta/retraction/result and accepts `history`; always `Gated`; config validated at startup. **Stakeholder preview is unblocked** (item 21) — the website server relays the stream, contract in `docs/website-integration.md`. Safety is judged over the conversation as of item 19. **Hardened 2026-09-10** (item 22): shared-secret auth on `/ask` (fail-closed boot), 2,000-char question cap, `top` 1–20, 256 KB body, 120 s request timeout, `/healthz` reports the posture; handoffs carry a real support route. Remaining before public traffic: items 12/17, and item 20 for a record of what the preview audience was shown | `runtime/src/DotFit.Agents.Service`; normal + escalation paths smoked live, hardening re-smoked live 2026-09-10 (fail-closed boot, 401/400, both answer paths); deployed on the preview VM as a systemd user service — `runtime/deploy/` holds the unit + install script |
| §12 eval harness | §12 | **built + live** — label-free metrics run, label-dependent report `null` with a reason | dev sweep 2026-09-10 (post items 23/24; first `--workers` run — 345 agent calls in minutes): sample recall@8 99.2%, probes 98.3%, escalation 10/10, multiturn 10/10 (item 19 measured), points-hit 0.87; withheld 39 of 125 (29 claims_language + 10 citation), faithfulness 0.60 (target 0.9), citation rate 87.9% over 33 claim answers; claims-audit precision undefined (0 flags) / recall **0/3** — items 12/17 stay open. The answer-side numbers above are a reading of the **superseded** draw (the 250 were re-drawn 2026-09-10, 42 items different); probe, escalation and multiturn tiers are unaffected and stand. **Sample retrieval re-measured on the new draw 2026-09-10** (retrieval-only, no chat): recall@8 **100.0%** (125/125, was 99.2% / 124/125), MRR 0.8233. Still owed on the new draw: faithfulness, citation rate, withheld counts and the claims-audit denominators — those need a full sweep |

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
| 7 | Stage 4 review-queue dispositions | **closed** 2026-09-10 — both luna-regen pairs ruled **split** (owner task 3): LeanMR+creatine read as two turns of one thread (the FirstString shape), Lean Pack 90 kept both because the 2023 record carries the fuller FAQ text. All 3 conflict clusters dispositioned and pinned by membership; queue 2 (audit sample only, no decision owed) |
| 8 | Golden-set labeling | **open, unblocked** — the re-draw is **ruled and committed** 2026-09-10: the 250 are re-drawn from the post-luna pool (653), 208 stay / 42 swap, deterministic and byte-identical to the decision pack's preview. What remains is the human pass: label the 250 with points-to-hit and expected sources (nutritionist + support lead, ~2–3 days; §12 rubric in the worksheet header). Until then the harness reports those two metrics as `null` with a reason. **Owner task 1** may start now; owner task 8 is closed |
| 9 | Runtime live smoke | **closed** 2026-09-07 — root cause was the wedged index (item 10), not the service |
| 10 | Orphaned `kb-main` index | **closed** 2026-09-08 — the delete finished server-side: `GET /indexes/kb-main` now returns the clean-miss 404 (not the wedged `"is being deleted"` body) and servicestats counts only `kb-main-v2` — 1 index, 3,996 docs, ~110 MB; the orphan's 7,992 docs / 213 MB no longer counted. Verified via statistics, not the portal, which hides deleting-state indexes. Support ticket moot; the name is free but `kb-main-v2` stays the code default on both sides — moving back is cosmetic, an owner option, not a task |
| 11 | Answer agent sourced claim language from authority 3 | **fixed** 2026-09-07, re-measured on the frontier 2026-09-09 — the dev sweep's audit saw no authority-3 sourcing; the residual claims-language concern on luna is item 17 |
| 12 | Claims-audit precision **and recall** under gating | **open — honestly measured at last** (re-swept 2026-09-10 after the 23/24 fixes; the morning's recall reading was withdrawn same day). Both earlier defects are closed live: the audit no longer skips (item 24 — no `skipped` verdicts, denominator 3/3) and the three judge false positives read `mentioned` (item 23). What is left is the finding itself: the audit flagged **nothing** while the judge found 3 asserted violations — **A-021 and A-029** (claim traps) and **A-047** (the derived 57.9% margin) — and **all three were delivered**: recall **0/3**, precision undefined (0 flags — the "flags nothing, looks clean" case §12 warns about). The audit's instructions/threshold are the work; A-047 has been real across two sweeps. Needed before **public customer** traffic; the stakeholder preview does not wait on it (item 21) |
| 13 | Stage 2 review-queue dispositions (**absorbed item 16** 2026-09-08) | **open, regrouped** — 220 → 234 (182 PII + 47 audit + 5 low-conf; the earlier "+6" did not sum to 234). Round-3 groups read off the triage script 2026-09-10 and owner task 2 rewritten to them: **unresolved 2 → 20** (15 indexed — the sharper luna canonicalization sees names gpt-5-mini read past), text_residue 121 → 153, confirmed-clean 42 → 9, audit 47, low-conf 5. The Zane row cleared (ruling landed with prompt 1.2.0). Standing human items: 2 third-party prose mentions no rule can reach (1 indexed), 1 committed-tree ruling over the `text_residue` records, the bulk pile. **Scoped to the public launch 2026-09-10** — owner ruled the 211 already-searchable records acceptable for a stakeholder/partner audience, so this does not gate the preview; the standard is unchanged (ruling in `docs/decisions.md`, Corpus & PII) |
| 14 | Podcast citation URLs | **closed** 2026-09-08 — all 47 ids resolved via YouTube oEmbed and matched all 47 episodes at Dice 1.00; frozen as `PODCAST_VIDEO_IDS`, 1,800/1,800 segments deep-linked to their start second, re-uploaded with 0 embed calls and verified live |
| 15 | §12 evaluation coverage | **mostly closed** 2026-09-08 — the precision metric is in §12's list; PDSRG/podcast have 120 retrieval probes; the degraded-guardrail path is pinned by tests and fixed a real defect. **Remaining**: probes measure retrieval only, so end-to-end coverage of those two corpora still needs written questions, and two §11 standing behaviors (conversation-start disclosure, prompt-injection) have no adversarial item because §12 fixes the split at 20/15/15 |
| 16 | Customer names in `question_original` | **closed into item 13** 2026-09-08 — it was the same defect seen through a different probe: the scrub had no rule for a name that is neither a salutation nor a closer. `SELF_INTRO_RE` closed it (11 spans redacted, the one public figure on `ACCEPTED_HONORIFIC_NAMES` correctly quoted verbatim) and the `my name is` probe is now 0. Residual-PII work continues under item 13; do not re-open this row |
| 17 | Answer-prompt tuning for `gpt-5.6-luna` | **open** — checker fixed and re-swept twice (2026-09-10); the latest sweep (post item 24, which audits more drafts) reads withheld **39 of 125** (29 claims_language + 10 product_claim_citation, up from 24), faithfulness **0.60** against the 0.9 target (judged on delivered answers only), citation rate **87.9%** over 33 product-claim answers. The remaining work is the answer prompt itself — much of the claims_language pile is quotable-looking context phrasing the draft puts forward as dotFIT's own. Blocks **public customer** SSE with item 12, not the stakeholder preview (item 21) |
| 18 | Multi-turn conversation support | **closed** 2026-09-10 — `POST /ask` takes `history` (`role`/`text`, oldest first, current question excluded; unknown role = 400), `AskOptions.History` carries it, and the CLI's `chat` keeps the session transcript (`reset` clears it). History reaches the **rewrite stage only** — it collapses a follow-up into one standalone question, and search / answer / post-check see no conversational state. The answer agent is never shown it (the `[n]` contract needs retrieved sources; an earlier turn is not one), which is pinned by a test, as is the guardrail gap left to item 19. Bounds are ours not the caller's: newest 8 turns, 1,000 chars each, trailing echo of the question dropped (`ConversationHistory`). `docs/website-integration.md` now documents it as built, with the single-turn safety limit stated plainly |
| 19 | Guardrail over the conversation, not the turn | **closed and measured 2026-09-10** — history reaches the guardrail; a trigger stated in an earlier turn escalates the question that follows it, and `history_trigger` says a verdict rests on it. The countervailing rule is in the same prompt (one trigger must not refuse every later turn) and is measured: §12's `multiturn.jsonl` — 20 items, 10 delayed triggers / 5 delayed claim traps / 5 controls, scored off the runtime's own flags with misses and over-escalations reported apart. **Live sweep 2026-09-10: 10/10** on the dev split — 5/5 delayed triggers (all credited to history, including M-012 which the spot-check missed), 3/3 delayed claim traps, 2/2 controls, **0 over-escalations**. Ruling in `docs/decisions.md` (Runtime §11) |
| 20 | Per-request verdict logging | **open, new** 2026-09-10 — the service emits no structured record of what it decided. Wanted: `escalated` / `withheld` / `history_trigger` (item 19 — whether a refusal rests on an earlier turn is the one thing a complaint cannot be reconstructed without) / post-check verdict / claims outcome, keyed by conversation and request id, and **without** question or answer text — the website DB is the system of record and a second copy is a new PII surface (§4 posture). This is also how items 12 and 17 get production numbers instead of dev-sweep ones. **Raised by the item 21 ruling**: with any state shippable to the preview, this log is the only record of what that audience was shown |
| 21 | Stakeholder-release scope decision | **closed** 2026-09-10 — owner ruling: the stakeholder preview is **never blocked on a metric**, so the project can be tested continuously at any state. Items 12 and 17 gate **public customer traffic only**. Recorded in `docs/decisions.md` (Runtime §11) with its two consequences: the claim-wording caveat becomes a standing condition on every preview release, and item 20's verdict logging is now load-bearing — if any state may ship, the log is the only reconstruction of what the audience saw |
| 22 | Service hardening for the preview | **closed** 2026-09-10 — all four landed and smoked live: shared-secret auth on `POST /ask` (`Authorization: Bearer`, fixed-time compare) that **fails the boot** rather than serve an open endpoint — `DOTFIT_SERVICE_AUTH=none` is the explicit opt-out for an mTLS boundary; a 2,000-char question cap plus `top` 1–20 and a 256 KB body, all rejected as 400s before the SSE stream commits the response to 200; a 120 s request timeout that ends in `error`+handoff rather than a stream that silently stops; and the two handoff templates now carry `support@dotfit.com or (877) 436-8348`, the route the PDSRG itself publishes (owner ruling: ship the attested pair, keep it configurable). CORS and rate limiting stay out as ruled. Rulings in `docs/decisions.md` (Runtime §11); caller-facing contract in `docs/website-integration.md` |
| 23 | Adversarial `forbidden` rubric cannot tell use from mention | **closed** 2026-09-10 — two changes. The 50 items' `forbidden` clauses are now verbs the response would have to *perform* ("asserting that a product cures …"), not noun keyword lists, pinned by a test that every clause reads as one; and the judge returns `forbidden_mode` (`absent` / `mentioned` / `asserted`) with `forbidden_present` **derived** from it, so a denial that names the claim it denies cannot be counted as the claim. The system prompt says so in the same words the 3 false positives failed on (A-023/A-031/A-033). `golden --written-only` rebuilt the written sets without re-drawing the 250 (item 8's target is untouched); golden_version 1.1.0, judge prompt 1.1.0, eval 1.1.0. **Re-swept 2026-09-10**: A-023/A-031/A-033 read `mentioned` and no longer count as violations; forbidden rate 3/25, none of them denials |
| 24 | The claims audit does not run when no approved copy is retrieved | **closed** 2026-09-10 — decided: **run it**. The short-circuit predated the item 17 fix, when the checker could only see authority 1–2 and had nothing to compare against; it now sees every source, so the justification was gone and the skipped case was the *high-risk* one (A-047). `AgentClaimsLanguageChecker` now audits any non-empty source set, and the audit instructions say what a set with no QUOTABLE source means — context cited as context still passes, a product claim presented as dotFIT's own has nothing to support it. `Skipped` survives for the empty-source case and for reading pre-fix run records. **Re-swept 2026-09-10**: no skipped audits on the non-escalated adversarial set — recall now reads on a full denominator (3/3), and it is 0 (item 12) |

## Log

Newest first, one entry per work item, 8 wrapped lines maximum (see Writing
entries). The five most recent live here; older ones are in
`docs/progress-archive.md`. Detail belongs in the commit, the code, or the
artifact it describes.


- **2026-09-10 (62)** — Preview VM deployment recorded (§11, item 21): the
  SSE service runs as a **systemd user service** (linger on, survives logout)
  on the test VM — no front proxy, bound `0.0.0.0:5199` on the internal
  network only, port closed to the outside. The how is committed:
  `runtime/deploy/` holds the unit and an idempotent install script (publish
  → key → unit → linger → start → healthz); re-running the script is the
  redeploy procedure, verified live on the VM. Artifact only, no code —
  tests unchanged (**455 py / 162 runtime**).

- **2026-09-10 (61)** — Service hardening, item 22 **closed** (§11). Four
  boundary changes: shared-secret auth on `POST /ask` that **fails the boot**
  when neither the key nor `AUTH=none` is set, so no state runs open; a
  2,000-char question cap, `top` 1–20 and a 256 KB body, all 400s *before* the
  stream opens; a 120 s timeout ending in `error`+handoff, not silence; and both
  handoffs now carry the PDSRG-attested route `support@dotfit.com or (877)
  436-8348`. Live smoke: fail-closed boot, 401/400, escalation, gated answer.
  CORS/rate limiting stay out. 19 tests (**455 py / 162 runtime**).

- **2026-09-10 (60)** — The 250 are **re-drawn** (§12, item 8 ruled
  re-draw). Sequencing was option C: item 7's split ruling landed first and
  left the pool at 653 and the draw unmoved, so the decision pack's preview
  regenerated **byte-identical** and was promoted — 208/250 stay, 42 swap,
  125/125 and 29 families unchanged, `(untagged)` 79 → 63, G-012/G-032 (aimed
  at retired answers) gone. Item numbers and dev/test sides were re-assigned
  (41/208 keep their number, 114/208 their side). Sample retrieval re-measured
  on the new dev 125 (retrieval-only, no chat, scratch out — `processed/eval/`
  still holds the full sweep): recall@8 **100.0%** (was 99.2%), MRR 0.8233;
  the answer-side sample numbers still read on the old draw. Written 50/20 and
  120 probes untouched; labeling unblocked. No code (**455 py / 143 runtime**).

- **2026-09-10 (59)** — Item 7 **closed** (§4): both luna-regen conflict
  pairs ruled **split** (owner task 3). LeanMR+creatine is the FirstString
  shape — two turns of one thread; Lean Pack 90's answers agree, but the
  2023 record carries the fuller FAQ text, so retiring it would drop wording
  from search. Both pinned in `CURATED_CLUSTER_DISPOSITIONS`
  (membership-attested); regen from warm caches, 0 judge calls: 3
  dispositioned clusters, queue 6 → **2** (audit sample), statuses unchanged
  (923/104/14) — no index action, `is_current` unchanged for every record. Ruling in
  decisions.md §4; shipped-rulings test extended (**455 py / 143 runtime**).

- **2026-09-10 (58)** — products.json drop processed, item 2 **closed** (§5).
  Owner ruling: exports arrive ad hoc when updates are known — no schedule;
  each drop runs diff → aliases → stage2 → stage4 → index → ping (cache on
  the stage2 leg), now the recorded chain in decisions.md §5. First drop:
  SKUs 1470/1471 joined dotBAR via `CURATED_FAMILIES` (flavor singletons were
  the derivation gap); 2 QA records re-tagged, 0 LLM calls; index 4,002 →
  **4,006** (1 embed call, 0 errors), new flavors retrievable live. 2 pins
  updated (**455 py / 143 runtime**).

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
