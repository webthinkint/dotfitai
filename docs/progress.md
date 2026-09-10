# Phase 1 — Build Progress

The status view: what is built, what is open, what runs next. Every agent run
reads this file end to end, so it is kept short on purpose — the rest lives in:

- `docs/decisions.md` — owner/curation rulings, grouped by area. Read the group
  for the area you are about to touch.
- `docs/progress-archive.md` — log entries older than the five kept below.
- `phase1-knowledge-assistant.md` — the plan (design decisions); `AGENTS.md` —
  the hard rules those produced.

## Status (§13 build order)

Numbers verified 2026-09-10. Python 426 tests green; runtime 115 tests green.

| Component | Plan § | State | Verified output |
|---|---|---|---|
| QA Stage 0 — PII scrub | §4 | **done**, rules extended 2026-09-08 (triage round 2) | 1,051 `.docx` (1,103 − 51 duplicates − 1 zero-byte; 43 md5 groups, log in `processed/qa/runs/`); round-2 regen redacted 38 further names across 36 files, 0 prose changes |
| QA Stage 1 — parse & classify | §4 | **done** | 777 qa_email / 264 expert_note / 0 other; 766 `thread_date`; review queue 0; 10 unanswerable excluded (6 no-answer, 4 blank) |
| PDSRG extraction gate | §6.1 | **passed**, human-verified | 5 stress PDFs |
| PDSRG chunking | §6.2–4 | **done** | 39 docs → 1,080 chunks (~404K tokens, median 349); 950 with part_nos; 53 discontinued-stamped; 1 atomic oversize table |
| Alias table | §5 | **done**, v1.3.0 | 51 indexed SKUs → 31 families; worksheet 19/19 attested; 13 deterministic aliases + 1 LLM-only |
| QA Stage 2 — canonicalize | §4 | **done** — full run 2026-09-06, regens 2026-09-07/08, **gpt-5.6-luna regen 2026-09-09 (prompt 1.2.0)** | 1,041 canonical records; 680 with products (50 part_nos); 306 currency-cued; queue 234 (182 PII + 47 audit + 5 low-conf) — dispositions are item 13; Zane ruling landed |
| QA Stage 4 — dedup & currency | §4 | **done**, gpt-5.6-luna regen 2026-09-09 | 923 current / 104 superseded_currency / 14 superseded_dup; 16 clusters (**3 conflict** — 1 ruled split, 2 new → item 7; 1 audit); 114 judgments (104 dependent / 10 independent); queue 6 |
| Podcast segmentation | §7 | **done** — contract in `podcast.py` | 47 episodes → 1,800 segments (median 76 s / 251 words) |
| Golden set | §12 | **complete except labeling** 2026-09-08 — the 250 are sampled, the 50 adversarial are **written** (`CURATED_ADVERSARIAL`), the probes are built. Remaining: label the 250 (item 8) | 650 current QA pairs → 250 items over 29 families (125/125) + 50 adversarial (25/25) + 120 PDSRG/podcast probes; `processed/golden/` |
| Podcast ASR | §7 | **transcribed + QC PASS, indexed, cited** — 47/47, 5-episode spot-check clean; citation URLs verified and stamped 2026-09-08 (item 14 closed). Remaining: speaker-map rewrite + re-upload (non-blocking) | 38.0 h audio → 35,050 phrases (~437K words); 37 eps × 2 speakers, 10 × 3; 1,800/1,800 segments deep-linked |
| Index + retrieval | §9–11 | **live** on `kb-main-v2`, the code default on both sides. Re-uploaded 2026-09-09 after the luna regen: 0 errors, all Stage 2 re-canonicalization. Ranker ruled **off** — item 5 closed | 4,002 docs — 1,080 pdsrg / 177 product / 10 menu / 1,800 podcast / 935 qa; 57 embed calls (3,090 cached), 7 pruned, 0 errors; live `search_ping` PASS at 4,002 |
| v1 runtime | §11 | **built + live** — the full §11 chain verified end to end and traced. Delivery mode is explicit: `Gated` for the service, `Live` for the CLI. Remaining: audit precision on a full sweep (item 12) | `runtime/`, 115 tests |
| SSE service | §11 | **built + live** 2026-09-08 — `POST /ask` streams disclosure/stage/delta/retraction/result; always `Gated`; config validated at startup. **Stakeholder preview is unblocked** (item 21) — the website server relays the stream, contract in `docs/website-integration.md`. Remaining before public traffic: items 12/17; before the preview is useful: multi-turn (18/19) | `runtime/src/DotFit.Agents.Service`; normal + escalation paths smoked live |
| §12 eval harness | §12 | **built + live** — label-free metrics run, label-dependent report `null` with a reason | dev re-sweep 2026-09-10 after the checker fix (125/60/25, ranker A/B dropped — item 5 closed): sample recall@8 99.2%, probes 98.3%, escalation 10/10, points-hit 0.92; **withheld 51 → 24**, claims_language failures 50 → 15; faithfulness 0.57 → 0.61, citation rate 83.3% over 36 claim answers (denominator was 11) |

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
| 12 | Claims-audit precision **and recall** under gating | **open — the 2026-09-10 recall reading was wrong and is withdrawn** (corrected same day). Two defects under it. (a) `AgentClaimsLanguageChecker` returned `compliant: true, degraded: false` when it short-circuited on "no quotable source retrieved", indistinguishable from a verdict it reached — **10 of the 13** non-escalated adversarial items were in that state, so recall's denominator of 4 was really **1**. Fixed: `ClaimsVerdict.Skipped`, surfaced in `ask --json`, and `_claims_unknown` respects it. (b) Of the 5 judged violations, **3 are judge false positives** — A-023/A-031/A-033 correctly *deny* a claim, and the keyword-shaped `forbidden` rubric plus "judge the text as written, not the intent" flags the denial (item 23). **A-047 looks real** and is the one to chase: it derived a 57.9% gross margin no source states, from all-authority-3 sources — precisely the case the audit skips. Needed before **public customer** traffic; the stakeholder preview does not wait on it (item 21) |
| 13 | Stage 2 review-queue dispositions (**absorbed item 16** 2026-09-08) | **open, regrouped** — 220 → 234 (182 PII + 47 audit + 5 low-conf; the earlier "+6" did not sum to 234). Round-3 groups read off the triage script 2026-09-10 and owner task 2 rewritten to them: **unresolved 2 → 20** (15 indexed — the sharper luna canonicalization sees names gpt-5-mini read past), text_residue 121 → 153, confirmed-clean 42 → 9, audit 47, low-conf 5. The Zane row cleared (ruling landed with prompt 1.2.0). Standing human items: 2 third-party prose mentions no rule can reach (1 indexed), 1 committed-tree ruling over the `text_residue` records, the bulk pile |
| 14 | Podcast citation URLs | **closed** 2026-09-08 — all 47 ids resolved via YouTube oEmbed and matched all 47 episodes at Dice 1.00; frozen as `PODCAST_VIDEO_IDS`, 1,800/1,800 segments deep-linked to their start second, re-uploaded with 0 embed calls and verified live |
| 15 | §12 evaluation coverage | **mostly closed** 2026-09-08 — the precision metric is in §12's list; PDSRG/podcast have 120 retrieval probes; the degraded-guardrail path is pinned by tests and fixed a real defect. **Remaining**: probes measure retrieval only, so end-to-end coverage of those two corpora still needs written questions, and two §11 standing behaviors (conversation-start disclosure, prompt-injection) have no adversarial item because §12 fixes the split at 20/15/15 |
| 16 | Customer names in `question_original` | **closed into item 13** 2026-09-08 — it was the same defect seen through a different probe: the scrub had no rule for a name that is neither a salutation nor a closer. `SELF_INTRO_RE` closed it (11 spans redacted, the one public figure on `ACCEPTED_HONORIFIC_NAMES` correctly quoted verbatim) and the `my name is` probe is now 0. Residual-PII work continues under item 13; do not re-open this row |
| 17 | Answer-prompt tuning for `gpt-5.6-luna` | **open, root cause reframed** 2026-09-10 — the dominant cause was not the model: the claims checker was shown authority 1–2 sources only, so it could not resolve the draft's `[n]` and read anything grounded in Q&A/podcast as an invention. Of the 50 `claims_language` flags, 15 cited nothing the checker could see (one a one-line answer about the carbohydrates in an apple). **Checker fixed** and **re-swept 2026-09-10**: withheld 51 → 24 (41% → 19%), claims_language failures 50 → 15, delivered 74 → 101 — the diagnosis held. Remaining is the answer prompt itself: faithfulness 0.57 → 0.61 against a 0.9 target (judged on delivered answers only, so the fix could not move it much), and citation rate 83.3% over 36 product-claim answers where the old denominator was 11 — `product_claim_citation` failures rose 8 → 11 as more claim answers now reach that check. Blocks **public customer** SSE with item 12, not the stakeholder preview (item 21) |
| 18 | Multi-turn conversation support | **open, new** 2026-09-10 — the service is single-turn: `conversation_id` gates the disclosure and nothing else, and `AskStreamAsync` takes a bare string. Agreed shape: the website server sends recent turns (it owns the transcript DB), and history reaches the **rewrite and guardrail stages only** — never the answer agent, which stays grounded in retrieved sources. Contract written up in `docs/website-integration.md` as *planned*; the preview ships without it (item 21), but a single-turn assistant is most of what makes the preview worth running |
| 19 | Guardrail over the conversation, not the turn | **open, new** 2026-09-10 — the safety half of item 18 and the reason it is a separate row: an escalation trigger can arrive turns before the question it applies to ("I'm 14" ... "how much creatine?"), and each turn is currently judged alone. Escalation accuracy 10/10 is a **single-turn** number and does not transfer; §12 needs multi-turn adversarial items before it can be claimed again |
| 20 | Per-request verdict logging | **open, new** 2026-09-10 — the service emits no structured record of what it decided. Wanted: `escalated` / `withheld` / post-check verdict / claims outcome, keyed by conversation and request id, and **without** question or answer text — the website DB is the system of record and a second copy is a new PII surface (§4 posture). This is also how items 12 and 17 get production numbers instead of dev-sweep ones. **Raised by the item 21 ruling**: with any state shippable to the preview, this log is the only record of what that audience was shown |
| 21 | Stakeholder-release scope decision | **closed** 2026-09-10 — owner ruling: the stakeholder preview is **never blocked on a metric**, so the project can be tested continuously at any state. Items 12 and 17 gate **public customer traffic only**. Recorded in `docs/decisions.md` (Runtime §11) with its two consequences: the claim-wording caveat becomes a standing condition on every preview release, and item 20's verdict logging is now load-bearing — if any state may ship, the log is the only reconstruction of what the audience saw |
| 22 | Service hardening for the preview | **open, new** 2026-09-10 — small and mostly deployment: auth at the boundary (shared secret or mTLS; the service has none of its own and must not be public), a max question length, a request timeout, and a real support route in the two handoff templates instead of prose. CORS and rate limiting are **not** needed — one trusted server-side caller, no browser origin |
| 23 | Adversarial `forbidden` rubric cannot tell use from mention | **open, new** 2026-09-10 — the rubric strings are keyword lists ("treat / cure / prevent ... language for any disease") and `ADVERSARIAL_JUDGE_SCHEMA` says "judge the text as written, not the intent behind it", so the *correct* refusal ("no dotFIT product is approved or described as preventing cancer") is scored as forbidden content. 3 of 5 flags on the 2026-09-10 sweep. It is the same defect class as the claims checker's blind spot — a rubric applied without the context needed to read it. Blocks any honest reading of forbidden-content rate and of item 12's recall |
| 24 | The claims audit does not run when no approved copy is retrieved | **open, new** 2026-09-10 — the short-circuit predates the item 17 fix, when the checker could only see authority 1–2 and had nothing to compare against. It now sees every source, so the justification is gone, and the skipped case is the *high-risk* one: an answer built entirely from Q&A/podcast is where an unsupported product claim is most likely (A-047). Decide whether to run the audit on context-only source sets |

## Log

Newest first, one entry per work item, 8 wrapped lines maximum (see Writing
entries). The five most recent live here; older ones are in
`docs/progress-archive.md`. Detail belongs in the commit, the code, or the
artifact it describes.


- **2026-09-10 (50)** — Entry 49's recall reading **withdrawn**, and two
  defects found under it (§11/§12). The audit returned `compliant: true` when
  it short-circuited on "nothing quotable retrieved" — **10 of 13** adversarial
  items, so recall's denominator was 1, not 4. `ClaimsVerdict.Skipped` now says
  so and `_claims_unknown` reads it. Separately, **3 of the 5 judged violations
  are judge false positives**: the keyword `forbidden` rubric flags a refusal
  for *denying* a claim → item 23. A-047 (a derived 57.9% margin, all-authority-3
  sources) is the one real find → item 24. 1 test (**426 python**).

- **2026-09-10 (49)** — Claims post-check sees every retrieved source, and the
  audit's **recall** is now measured (§11/§12). It got authority 1–2 only, so a
  draft's `[n]` pointed at sources it did not have — 15 of 50 flags, one a lone
  sentence on the carbohydrates in an apple. Re-sweep: **withheld 51 → 24**,
  claims_language 50 → 15, faithfulness 0.57 → 0.61. Recall is **0.0 in both
  runs** (0 of 4 auditable violations, all delivered); precision alone reads
  `null` and looks clean. 7 tests (**425 python / 115 runtime**). Items 18–22
  opened; 21 ruled — the stakeholder preview never blocks.

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
