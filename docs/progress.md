# Phase 1 — Build Progress

What is built, what is open, and the decisions made along the way.
Design decisions themselves live in `phase1-knowledge-assistant.md` (the plan,
kept current); the hard rules they produced live in `AGENTS.md`. This file is
the status view, not a narrative — see "Writing entries" at the bottom.

## Status (§13 build order)

Numbers verified 2026-09-07. Python 338 tests green (2026-09-07); runtime 87 tests green (2026-09-07).

| Component | Plan § | State | Verified output |
|---|---|---|---|
| QA Stage 0 — PII scrub | §4 | **done** | 1,051 `.docx` (1,103 − 9 empty − 43 dup groups) |
| QA Stage 1 — parse & classify | §4 | **done** | 777 qa_email / 264 expert_note / 0 other; 766 `thread_date`; review queue 0 (round 2 cleared); 10 unanswerable excluded (6 no-answer, 4 blank) |
| PDSRG extraction gate | §6.1 | **passed**, human-verified | 5 stress PDFs |
| PDSRG chunking | §6.2–4 | **done** | 39 docs → 1,080 chunks (~404K tokens, median 349); 950 with part_nos; 53 discontinued-stamped; 1 atomic oversize table |
| Alias table | §5 | **done**, v1.3.0 | 51 indexed SKUs → 31 families; worksheet 19/19 attested; 13 deterministic aliases + 1 LLM-only |
| QA Stage 2 (canonicalize) | §4 | **done** — full run 2026-09-06 on the small chat deployment (strict JSON-schema extraction + containment diff pass); alias-1.3.0 regen 2026-09-07 | 1,041 canonical records (prompt 1.1.0); 676 with products (50 part_nos); 306 currency-cued; queue 222 (168 PII + 48 audit + 1 containment + 5 low-conf) |
| QA Stage 4 (dedup & currency) | §4 | **done** — `stage4` subcommand (2026-09-07); byte-identical rerun verified | 919 current / 106 superseded_currency / 16 superseded_dup; 17 clusters (1 conflict); 114 judgments (106 dependent / 8 independent / 0 low-conf); queue 2 (cluster conflict) |
| Podcast segmentation | §7 | **done** — `podcast` subcommand; greedy merge to ~90 s / 200-word targets (phrases atomic); Speaker-turn text is the speaker-map rewrite contract | 47 episodes → 1,800 segments (median 76 s / 251 words); rerun byte-identical |
| Golden-set sampling | §12 | **sampled** — `golden` subcommand (2026-09-07): 650 current QA pairs → 250 items stratified year × family (2025–26 ×2 weight, ≥1 per year; FAQ-family + evergreen-topic floors; sha256 rank, no RNG), 0 coverage swaps needed; splits 125/125 + adversarial 25/25 = §12's 150/150; labeling worksheet + adversarial scaffold emitted; rerun byte-identical from any cwd. Remaining: **labeling** (250 worksheet + write 50 adversarial), eval harness | `processed/golden/` |
| Podcast ASR | §7 | **transcribed + QC PASS, indexed** — 47/47 episodes via fast-transcription (diarization on, dotFIT phrase list); 5-episode spot-check clean; remaining: speaker-map (text rewrite + re-upload, non-blocking) | 38.0 h audio → 35,050 phrases (~437K words); 37 eps × 2 speakers, 10 × 3 |
| Index + retrieval | §9–11 | **index live** — `kb-main` holds 3,996 docs (1,080 pdsrg / 177 product / 10 menu / 1,800 podcast / 929 qa; Stage-4-superseded QA docs pruned from the service, not just the artifact); QA-filtered + unfiltered retrieval smoke PASS; index now mirrors documents.jsonl by construction (sortable id + post-upload prune); remaining: golden-set eval, ranker toggle | `processed/index/` |
| v1 runtime (§11) | §11 | **built + live smoke run** — `DotFit.Agents` library + `dotfit-agent` CLI, 87 tests green; end-to-end `search`/`ask` verified 2026-09-07 against `kb-main-v2` (guardrail → rewrite → aliases → hybrid search → grounded streamed answer with `[n]` citations → post-check, all stages traced). `search` returns pdsrg/product/qa hits with authority re-rank; `ask` answers grounded and cites correctly. The `ask` run exits 1 on a **claims-language post-check FAIL** — the auditor flagged a passage the answer had *quoted verbatim from the corpus*, so triage whether the audit should exempt quoted source text (new work item, not an infra issue). Retrieval was blocked until 2026-09-07 by a wedged `kb-main` (open item 9). Answer delivery is now an explicit mode (§11 "streaming vs. gating"): `Gated` for the service, `Live` for the CLI | `runtime/` |

Artifacts: `processed/qa/`, `processed/pdsrg/`, `processed/aliases/`.
Per-run counts live in each `summary.json`; numbers quoted here must match a
regenerated run.

## Open items (§14)

| # | Item | Status |
|---|---|---|
| 1 | Azure region + SKU | **partial** — services provisioned 2026-09-05 (AI Search, Azure OpenAI, AI Speech), credentials in local `.env`; `text-embedding-3-large` and `gpt-5-mini` deployed and smoke-verified. Remaining: quota increase for the frontier chat deployment |
| 2 | products.json freshness owner | **open** — 8 PDSRG-only gaps closed 2026-09-01. Remaining: name the owner, set monthly diff cadence |
| 3 | Alias-table curation session | **closed** 2026-09-01, pass 2 2026-09-07 — outcomes live in `CURATED_ALIASES` / `CURATED_LLM_ONLY_ALIASES` / `CONTEXT_ONLY_TOKENS`; the worksheet is the session record. The 2,292-mention Stage 2 unresolved tally is the input to any pass 3 |
| 4 | PPTX disposition | parked |
| 5 | Semantic ranker on/off | week 3–4, decide empirically — measurable since entry (30) fixed the re-rank to order on the reranker score |
| 6 | Stage 3 review-queue dispositions | **closed (round 2)** 2026-09-05, queue 0. Remaining: git history still holds two customer names in older blobs — purge needs an owner decision (same precedent as the `.scan` purge) |
| 7 | Stage 4 review-queue dispositions | **open** — 2 records (`cluster_conflict`: non-nested part_nos); owner picks or splits the cluster. The 114 currency judgments carry evidence quotes in `stage4/documents.jsonl` |
| 8 | Golden-set labeling | **open** — 250 sampled items to label + 50 adversarial to write (`processed/golden/`, `qa-pipeline golden`). Nutritionist + support lead, ~2–3 days; §12 rubric in the worksheet header |
| 9 | Runtime live smoke | **closed** 2026-09-07 — root cause was one wedged index (item 10), not the service; the full §11 chain now verified live on `kb-main-v2` |
| 10 | Orphaned `kb-main` index | **open** — never finished deleting: `GET /indexes/kb-main` 404s with `"is being deleted"` and servicestats still counts it (7,992 docs, 213 MB), while the portal omits deleting-state indexes, so "gone from the UI" is not evidence. Not a blocker; consumes quota and storage. Owner: support ticket — the clean repro is one index that can neither serve documents nor finish deleting on a service where a throwaway index does both in seconds. On resolution: rebuild as `kb-main` and drop the `--index` override, or keep `kb-main-v2` and flip the runtime default (`RuntimeOptions.cs:65`) |
| 11 | Answer agent sourced claim language from authority 3 | **fixed** 2026-09-07 — sources are now tagged quotable (authority 1–2) vs context-only and the answer instructions fail closed; 4/4 live runs PASS. Remaining: 4 runs is signal, not a regression suite — coverage lands with the §12 eval harness, and re-test on the frontier deployment when item 1's quota arrives |
| 12 | Claims-audit precision under gating | **open** — gating (§11) makes a `claims_language` false positive cost an answered question rather than a trace line, and the audit's precision rests on 4 live runs. §12 must report it on the adversarial-50 before the SSE service ships; if it is poor the lever is the audit prompt, not the gate |

## Decisions

Owner/curation decisions and reasoning that is not obvious from the code.
Everything else (rules, index contract, stage design) is in the plan.

**Corpus & PII**

- Filenames carry no review burden (2026-09-05): they are load-bearing topic
  summaries that are neither scrubbed nor flagged — the name-repeat flag is
  gone entirely.
- Redaction is conservative: anything that cannot be redacted without eating
  prose is flagged. A review queue of 0 must be earned; the old "0" was
  undetected leaks, not a clean corpus. The 2026-09-05 queue of 0 is the
  post-disposition steady state, earned by redaction + exclusion rules
  rather than by looking away.
- Greeting residuals (2026-09-05 dispositions, round 1): 6 of the 11 flags
  were real names in terminator-free shapes — redacted via the
  corpus-attested `GREETING_NAME_TOKENS` vocabulary (honorific-prefixed,
  lowercase, and-joined and slash-joined forms; `my`/`friend` are stopwords,
  and the residual scan no longer crosses newlines — those 5 flags were
  checker false positives). An unknown name in the same position still flags.
- Greeting residuals (2026-09-05 dispositions, round 2): sign-off names below
  the quoted header (`Thanks,`/`Regards,` + one bare-name line) redact to
  `[NAME]` — honorific/credential kept, `--` delimiter dropped; the expert
  region above the header is untouched (staff bylines, not PII). A bare
  `Mr` (no period, e.g. the LeanMR abbreviation) fused across a blank line
  with the next line's first word no longer flags — honorific gaps span at
  most one newline.
- 9 zero-byte files deleted; 51 duplicates deleted across 43 md5-identical
  groups (keep rule: earliest year → shallowest path → lexicographic). Full
  KEEP/DEL record: `processed/qa/runs/data-cleanup-2026-09-02.log`, since
  `data/` is untracked.
- Residual honorific+name flags were all public figures, dotFIT staff, or
  street addresses → owner-approved into `ACCEPTED_HONORIFIC_NAMES` (16
  surnames), matched at the surname position only.
- `answer_date` → `thread_date`: the field reads the thread's `Sent:` header,
  i.e. the enquiry's date. Stage 4 should treat it as a currency lower bound.
- Stage 2 triage shape (2026-09-06): unresolved product mentions are a
  *curation* signal, never a queue reason (the LLM names every brand it sees —
  queuing on it would review-queue ~every doc); the tally in `summary.json`
  feeds the next alias-curation pass. Residual-PII evidence spans live only in
  the gitignored Stage 2 cache, never in committed records; the queue's 556
  flags disposition in bulk (268 distinct spans, top-20 cover 54% — mostly
  recurring staff first names plus genuine catches the scrub rules cannot see).
  Round 1 (same day, owner dispositions): confirmed staff names redact silently
  via `SILENT_STAFF_NAMES` (a future customer sharing a first name is silently
  redacted too — harmless, veto-able); customer-side and third-party names stay
  flag-and-redact; inline closer+name and `wrote:`-header gaps closed in `scrub.py`;
  prompt redacts to [NAME] (never [CUSTOMER]), quotes public figures verbatim,
  transcribes expert notes without summarizing.

**Stage 4 (§4)**

- **Renames never supersede** (owner ruling 2026-09-07, after challenging the
  plan's stale "pre-reformulation names" wording): LeanMR→LeanMeal and every
  legacy rename is an identity mapping — same product, same formula. A rename
  cue is a dated-name signal (runtime can say "LeanMR, now LeanMeal"), and
  Stage 2 already expanded renames to successor part_nos — all 192 rename-cued
  records carry them, so superseding would have deleted real current content
  from the index. Only replacement (19 records) and discontinued (97) cues
  can supersede, via the formulation-dependence judgment.
- **Threshold 0.88 is scan-locked** (`scripts/stage4_cluster_scan.py`):
  at 0.88 every sampled merge is a true duplicate and the corpus yields 17
  clusters / 36 records; below 0.86 distinct questions fuse ("replace" vs
  "combine" Alln1+ActiveMV at 0.8436). The tie-breaker is asymmetry: a wrong
  merge removes a distinct answer from the index, a wrong miss only leaves a
  harmless duplicate retrievable.
- **Conflict proxy**: deterministic code cannot judge prose, so "materially
  disagree" = non-nested part_no sets → queue the cluster, no auto-pick,
  members stay indexed pending disposition; 5% deterministic audit sample of
  auto-resolved clusters; `clusters.jsonl` is the committed session record.
- **Conservative default when no judgment is usable** (no-llm mode, API
  error, low confidence): superseded + queued — §4 says "superseded unless
  formulation-independent", so the burden of proof sits on independence.
  114 live judgments: 106 dependent / 8 independent / 0 low-confidence / 0
  errors.
- **The index mirrors documents.jsonl by construction**: Stage-4-superseded
  docs are pruned from AI Search, not just skipped at upload. Two infra fixes
  earned en route: the `id` key field is now `sortable` (skip-pagination
  without order_by is unspecified — it could miss or repeat ids), and
  `ensure_index(reset=True)` now polls the async deletion before create —
  the 2026-09-07 rebuild raced it, failed with a bare "could not be created",
  and left the service with **no** index until the fixed rebuild re-uploaded
  from the vector cache (no embedding cost).

**Aliases (§5)**

- Aliases drive metadata tags, query-side expansion and currency flags —
  **corpus text is never rewritten** (dual-usage tokens like PP make
  find-and-replace a correctness bug and break Stage-2 traceability).
- Curation session outcome: **AF, SB, FS, WLLS** + the 3 legacy renames are
  deterministic tags; **PP and MVM stay context-only**. MVM was flipped to tag
  mid-session and reverted: the three MVs are distinct formulas chosen by
  audience (women / 50+ / general), so a blanket tag blurs exactly the
  distinction retrieval needs. Resolution guidance is in `CONTEXT_ONLY_TOKENS`.
- Renames expand to the successor's part_nos (LeanMR→LeanMeal and the five
  PDSRG renames); **replacements do not** — Recover&Build→AminoFormula is a
  different formula, so it is a currency cue only, in its own table section.
- Discontinued with no successor (KidsMV, VeganMV): stay indexable, tag
  nothing, carry `product_status`/`product_note` + guidance (kids → Active MV
  for teens, third-party for younger).
- The "Reformulated with Careflow (2025)" currency cue is **dropped** — absent
  from LeanMeal product copy (confirmed by N.K.).
- Every alias must be corpus-attested in the form the corpus writes it; a
  0-doc worksheet row means the alias is wrong (the MuscleDefender lesson).
- **Three tiers, by who is allowed to resolve a token** (2026-09-07). The
  alias table has two consumers with different context: `normalize_products`
  maps *LLM mention strings* (the model already judged the mention to be a
  product in that document), while `deterministic_product_tags` scans raw
  text blind. So: `CURATED_ALIASES` = safe for both; `CURATED_LLM_ONLY_ALIASES`
  = the mention path only, for tokens that are also ordinary English;
  `CONTEXT_ONLY_TOKENS` = resolved by neither, per-document topic guidance.
  `Women's` is the founding case — 264 corpus occurrences, but `women's
  health` / `women's hospital` / `women's sports` are attested too, so a blind
  scan would mis-tag. A token may sit in exactly one tier; the build raises
  otherwise.
- Dose tiers collapse to the product (owner ruling 2026-09-07): `1-Active` /
  `2-Active` are one- and two-a-day **Active MV**, not separate SKUs. The
  tablet count is dosage guidance that lives in the answer text; the product
  filter carries the product.
- A discontinued referent gets no alias (2026-09-07): `Kids`, `VeganMV` and
  `1-Vegan` recur in the program-note boilerplate but point at KidsMV /
  VeganMV, which have no part_no. They stay unresolved — that is the honest
  answer, not a gap to close.
- Shakers / SportMixer are gear and excluded — hence 51 *indexed* SKUs of 56.

**PDSRG (§6)**

- Bibliographies are excluded from chunks by default (`--keep-references`
  flips it): retrieval noise, and the only ruled "tables" live inside them.
- Table extraction is hybrid: ultra-wide dosage grids collapse under `lines`
  and are re-extracted with `text`. The collapse retry must run **before** the
  prose filter, or wide grids get dropped instead of re-extracted.
- Prose false-positive filter: <34% of rows with ≥2 non-empty cells and median
  cell ≥40 chars. 761 raw detections → 49 real tables kept.
- `STEM_META` (39 entries) maps PDF stem → family/category/topics; unknown
  stems raise rather than emit untagged chunks.
- `citation_url` = `{--citation-base}{source_file}#page=N`, so the deployment
  decides where PDFs are served and the artifact carries no host paths.

**Index / infra (§9)**

- `is_current` (answer currency) and `product_status` (SKU lifecycle) are
  separate axes: discontinued docs stay `is_current: true` so "what happened
  to X" is answerable. Every source must stamp `is_current` — AI Search does
  not match null against a filter.
- All Azure credentials live in the gitignored root `.env` and never leave
  the machine: `.env.example` is the committed key contract, and
  `azure_config.py` is the only reader (strict parser; masked `repr`; errors
  name variables, never values).
- The Azure OpenAI resource is Foundry v2 shape (`*.services.ai.azure.com`):
  the classic `/openai/deployments` route works, but only on current
  api-versions — `2024-10-21` returns 404 "Resource not found" on v2.
  Verified live: `2025-04-01-preview` (embedding smoke test, 2026-09-05).
- Index vectors are API results, so they live in a gitignored local cache
  (`processed/index/runs/embeddings.jsonl`, keyed deployment|api-version|text);
  the committed `documents.jsonl` carries no vectors — reruns stay
  byte-identical and re-uploads are free.
- AI Search keys forbid colons: index ids use dashes (`pdsrg-x-001`),
  mapped from the committed §9-style ids at build time — the 2026-09-05
  upload failed wholesale on `InvalidDocumentKey` before the mapping; a
  regression test pins the key rule on every built id.
- The menu export's case-duplicate menu names (`Gluten Free`/`Gluten free`,
  `Night Out`/`Night out` — identical descriptions) merge case-insensitively
  with the dominant spelling displayed; they would otherwise collide as
  duplicate document ids (the slug is casefolded).

## Log

Newest first, one entry per work item, 8 wrapped lines maximum (see Writing
entries). Detail belongs in the commit, the code, or the artifact it describes.

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
- **2026-09-07 (28)** — Runtime v0 shipped (§11, §13 Track B) (`0717125`,
  `2ab90c9`): new `runtime/` .NET 10 solution — `DotFit.Agents` library +
  `dotfit-agent` CLI, the §11 pipeline component-for-component, no tool calls.
  Components and config contract are in `runtime/README.md`. Live smoke reached
  `guardrail`/`rewrite` on gpt-5-mini; end-to-end `ask`/`search` was blocked by
  what read as a service-side failure on `dotfitsearch` (open item 9, since
  root-caused to the wedged index in entry 29). **64 runtime tests** (scripted
  `IChatClient` fakes, no Azure).
- **2026-09-07 (27)** — Golden-set sampling shipped (§12) (`f685456`): new
  `golden` subcommand + module; the sampling rules and their rationale are in
  `golden.py`'s docstring. Full run: 650-pair pool → **250 items** across 29
  families (recent share 43% vs 27% of pool), splits 125/125 + 25/25 adversarial
  per §12. Outputs `sample.jsonl`, `worksheet.md`, `adversarial.md`,
  `summary.json`. Labeling is open item 8. 23 new tests. **334 tests**.

- **2026-09-07 (26)** — QA Stage 4 shipped (§4) (`c3e1d1e`, `08a326b`): new
  `stage4` subcommand + module — clustering, canonical pick, conflict queue and
  the currency pass, all specified in `stage4.py`. Full run: 1,041 records →
  **919 current / 106 superseded_currency / 16 superseded_dup**, 17 clusters
  (1 conflict), 114 judgments, queue 2; byte-identical rerun with 0 API calls.
  `kb-main` rebuilt 4,118 → **3,996** (−122 superseded QA docs), and the index
  now prunes to mirror the build. 45 new tests. **311 tests**.
- **2026-09-07 (25)** — Stage 4 calibration scan
  (`scripts/stage4_cluster_scan.py`, one-off): 766 questions embedded (760
  unique; vectors cached for the full run), 89,451 bucketed pairs, pure-Python
  `math.sumprod` cosine (BLAS dot products are not bit-stable across platforms
  — byte-identical reruns forbid numpy here); threshold evidence: 0.88 merges
  only true dups, <0.86 fuses distinct questions (0.8436 "replace" vs "combine"
  Alln1). No tests (analysis tool). 266 tests.
- **2026-09-07 (24)** — Alias-1.3.0 regen (§5→§4/§9) (`75346f8`): Stage 2 rerun,
  2 live calls + 1,039 cache hits, 0 errors — **676** docs with products (+42
  newly tagged, 338 gained part_nos), unresolved mentions 3,474 → **2,292**
  (−34%), 50 distinct part_nos, queue 222 unchanged. Index re-upload
  4,118/4,118 to `kb-main`, 0 errors, diff confined to `products` on 338 QA
  docs. Live smoke PASS (206 docs under the `1009` filter — `Over50` flows end
  to end). Safety re-verified on the new artifacts. Regen only, no new tests.
  266 tests.
- **2026-09-07 (23)** — Alias curation pass 2 (§5), alias table **1.3.0**:
  eight corpus spellings the derivation could not reach joined
  `CURATED_ALIASES` (incl. `All Natural WheySmooth`, where the family name is a
  *suffix* and so invisible to the prefix rule), and the new **LLM-only tier**
  `CURATED_LLM_ONLY_ALIASES` (`Women's`) resolves on the Stage 2 mention path
  only, never in the blind scan, with build-time guards against a token sitting
  in two tiers. `Kids`/`VeganMV`/`1-Vegan` deliberately unaliased — their
  referents are discontinued. 12 new tests. 266 tests.
- **2026-09-07 (22)** — Scrub fix (§4 Stage 0): bare `best` in the inline-closer alternation is also an adjective and ate prose in the first regen (`...and Best Plant Protein.` and the heading `Best Scientific Combination` both became `Best [NAME]`) — it now requires its comma, every other closer keeps the optional one (`Thanks Neal` is attested). Stage 0/1 regen: 2 lines restored, nothing else changed; inline sign-off redactions 48 → 46, all 46 genuine. Those 2 docs are now Stage 2 cache-stale. 3 new tests. 254 tests.
- **2026-09-06 (21)** — QA canonicals indexed (§9): new `qa_documents` (one doc per pair, `authority=3`, null questions fall back to filename, `thread_date`→`DateTimeOffset` — the index's first real dates; 7 oversize answers split into paragraph-boundary parts, never truncated); `index --qa-docs` (missing file shapes without QA, podcast precedent); `kb-main` 3,067 → **4,118 docs**, 0 upload errors, retrieval smoke PASS. 7 new tests. 251 tests.
- **2026-09-06 (20)** — Stage 2 triage round 2 (owner-approved): all 9 containment/low-conf cases dispositioned — lactose table verified value-by-value (accept), Bulk Whey via PII bulk-accept, `34 mg/serving` root-caused to a tokenizer false positive (digit↔letter split in `word_tokens`: `34mg`→`34+mg`, `B12`→`B+12`) + free cache-hit rescore (0 API calls; queue 223→222, fails 4→1), eating-disorder/sucralose/Bain/Fatty15/serving-size accepts as reviewed (minor-recall note to the prompt backlog). Queue fully dispositioned. 2 new tests. 244 tests.
- **2026-09-06 (19)** — Stage 2 triage round 1 (owner dispositions): staff
  names bulk-accepted into `SILENT_STAFF_NAMES`, customer-side names
  accepted-redacted; new inline closer+name rule (48 hits) and `wrote:`-header
  rule (38 hits) in `scrub.py`, prompt 1.1.0. Full regen: 1,041 docs, 0 errors
  — queue 590 → **223** (168 residual-PII + 47 audit + 4 containment + 5
  low-conf), containment median 0.994, 634 with products / 50 part_nos. Safety
  re-verified on final artifacts: 0 own-answer leaks, 0 raw emails/phones,
  byte-identical cache-hit rerun. 14 new tests. 242 tests.
- **2026-09-06 (18)** — QA Stage 2 (§4) done: new `stage2.py` + `stage2` CLI (strict JSON-schema extraction on `gpt-5-mini`, deterministic product/currency/topics post-processing via the alias table, containment diff pass, per-doc cache checkpoints in gitignored `runs/stage2_cache.jsonl`, `--no-llm` offline fallback); full run 1,041 docs, 0 errors — 590 queued (556 residual-PII incl. staff-name bulk-triage shape, 23 audit, 9 containment, 8 low-conf), containment median 0.990, 639 with products / 50 part_nos. Safety verified: 0 own-answer leaks, byte-identical cache-hit rerun. 42 new tests. 228 tests.
- **2026-09-05 (17)** — Index hardening: embed cache checkpoints per batch (crash keeps vectors), `citation_url` path-quoting (`Sleep Aid.pdf` → `Sleep%20Aid.pdf`; zero raw spaces), menu descriptions stamped `authority=5` (explicit last — nulls sort unpredictably); podcast top-up recorded in plan §11 (owner decision B). Regen: counts identical (1,080 chunks, 3,067 docs); re-upload pending with the next cycle. 2 new tests. 186 tests.
- **2026-09-05 (16)** — Family canonicals pinned: regression test asserts `canonical_part_no` + membership for all 10 multi-SKU families (lowest-part_no ≈ first-published verified sane — every canonical is the hero flavor); a re-export that revoices families goes red. 1 new test. 184 tests.
- **2026-09-05 (15)** — Review round 2 cleared: sign-off de-naming rule (`Thanks,`/`Regards,` + bare name below the quoted header → `[NAME]`, 43 redactions over ~34 files; expert region untouched), honorific gap capped at one newline (kills the `lean Mr`/`Thanks` false fuse) + middle-initial capture (surname-tested), 8 study-author surnames allowlisted (Williams flagged as common — veto-able). Regen: queue 7→0; names verified absent from `processed/`. 8 new tests. 183 tests.
- **2026-09-05 (14)** — Scrub review fixes: period-optional honorific check, `Good morning` residual, profile-URL domains (`x.com`/`threads.net`/`fb.me` + left-boundary guard after a regen catch on `nxgenrx.com`), `and`-form tolerant matching (`Recover&Build` 17→19 docs), still-collapsed PDSRG grids kept atomic, nth-table ordering, explicit stage1 sort, per-file pdsrg errors, menu divergence assert, quoted-value comment parsing. Regen: queue 0→7 (all period-less honorifics, pending disposition); PDSRG/index byte-identical. 8 new tests. 175 tests.
- **2026-09-05 (13)** — Small chat deployment live: `gpt-5-mini` smoke PASS (strict JSON-schema extraction contract for Stage 2; `REQUIRE_OPENAI_SMALL_CHAT` subset + `scripts/chat_smoke.py`). Stage 2 full run done on it (2026-09-06); frontier deployment still pending quota. 167 tests.
- **2026-09-05 (12)** — Podcast ingestion (§7 step 4) done: `podcast_documents` (`authority=4`, mm:ss locator, null citation_url until the archive.txt→YouTube mapping is verified) + `--podcast-segments` flag; 3,067/3,067 uploaded to `kb-main` (1,800 new), 0 errors, retrieval smoke PASS. 2 new tests. 167 tests.
- **2026-09-05 (11)** — Podcast segmentation (§7 step 3) done: new `qa_pipeline/podcast.py` + `podcast` CLI subcommand (transcripts + audio dirs in, `segments/segments.jsonl` + `summary.json` out); 47 episodes → 1,800 segments, zero word loss, rerun byte-identical. 14 new tests. 165 tests.
- **2026-09-05 (10)** — Podcast QC (§7) PASS: 5-episode stratified spot-check all clean, no re-runs, phrase list unchanged; notes + speaker-map ground truth in `processed/podcasts/qc_notes.md`. 151 tests.
- **2026-09-05 (9)** — Podcast ASR sweep (§7) done: 47/47 episodes transcribed 0 failures (fast-transcription inline upload, `en-US` + diarization + 65-phrase dotFIT list; `scripts/asr_pilot.py --all`, 2 workers). 38.0 h → 35,050 phrases / ~437K words with word timestamps; outputs in `processed/podcasts/transcripts/` (<slug>.json + readable .txt). Next: 10% QC, segmentation + speaker-map (map needs the small chat deployment — same quota blocker as Stage 2). 151 tests.
- **2026-09-05 (8)** — `kb-main` uploaded and queryable: 1,267/1,267 docs,
  0 errors (all vectors from cache). First attempt failed wholesale:
  `InvalidDocumentKey` — colons are illegal in AI Search keys; ids now use
  dashes (plan §9 row updated, regression test pins the rule). Retrieval
  smoke PASS: hybrid queries hit topical PDSRG chunks, family product docs,
  and the `products` part_no filter. 151 tests.
- **2026-09-05 (7)** — Index builder (§9): `kb-main` schema (3072-dim
  int8-quantized vectors, rescoring, `stored=false`, semantic config), §5
  products section-split with family grouping (177 docs), §8 menu
  descriptions (10 — case-duplicate CSV names merged by dominant spelling, a
  latent duplicate-id bug), PDSRG pass-through (1,080); cached `Embedder` +
  `azure_config.require=` subsets; `index` CLI. 1,267 documents shaped and
  embedded (80 API calls first pass, cache hits after), byte-identical
  reshape verified. 148 tests.
- **2026-09-05 (6)** — Scan-dump history purged (owner decision, closes the
  (3) caveat): `git filter-repo --invert-paths` removed all four `.scan_*.txt`
  from every commit; hashes rewritten; pre-purge bundle kept outside the repo
  as the escape hatch. 130 tests.
- **2026-09-05 (5)** — Embedding smoke test **PASS** (§10): live call to the
  deployed `text-embedding-3-large`, 3072 dims confirmed
  (`scripts/embedding_smoke.py`; `openai>=1.60,<3` added). Foundry v2
  endpoint needs api-version `2025-04-01-preview`; `.env` parser now strips
  unquoted inline comments (the template's trailing-comment style had leaked
  into a deployment name). 130 tests.
- **2026-09-05 (4)** — `AZURE_SPEECH_ENDPOINT` (custom Speech domain endpoint)
  added to the env contract and required by the loader. 129 tests.
- **2026-09-05 (3)** — Corpus-scan dumps `.scan_*.txt` untracked and gitignored
  (phone-like strings from the raw corpus sat in the already-pushed initial
  commit `4a0ac93`; history purge pending owner decision — remote exists).
- **2026-09-05 (2)** — Secrets contract for the three provisioned Azure
  services (§4/§7/§9): gitignored root `.env`, committed `.env.example`, and
  `azure_config.py` (strict loader, masked repr, placeholder detection). 129
  tests.
- **2026-09-05** — Owner dispositions closed open item 6 (§4): greeting
  residuals redacted via curated `GREETING_NAME_TOKENS` (+ `my`/`friend`
  stopwords and a newline-crossing fix in the residual scan itself); filename
  name-flag removed; 6 no-answer + 4 blank docs excluded from
  `documents.jsonl` (1,051 → 1,041). Review queue 35 → 0. 116 tests.
- **2026-09-02 (4)** — Docs reconciled with the code: plan promoted to a living
  v1.1 with the corrections folded in; `pipeline/README.md` and the `pdsrg.py`
  docstring rewritten; table now emits `n_products_indexed` (51 SKUs → 31
  families; the old headline compared unlike things).
- **2026-09-02 (3)** — PDSRG: cwd-dependent `source_file` paths fixed
  (determinism was broken across working directories); §9 `is_current` / `date`
  / `citation_url` stamped on all chunks; 800-token cap now counts the heading
  prefix (1,061 → 1,080 chunks). Alias table v1.2.0 splits replacements from
  renames. 108 tests.
- **2026-09-02 (2)** — Stage 0 PII gaps closed (greetings whole-document and
  case-insensitive, `To:`/`Cc:` display names, postal addresses, tighter
  honorific matching, filename flag); Stage 1 section split repaired on 31 docs
  (58 questions corrected, empty expert sections 35 → 10); date parsing made
  locale-independent. Review queue 0 → 35. 104 tests.
- **2026-09-02** — Owner dispositions cleared the 32-item queue; corpus
  deduplicated 1,103 → 1,051; third document shape added (`doc_type: other`
  now 0); stage-1 manifest and `generated_utc` determinism bugs fixed. 83 tests.
- **2026-09-01 (6)** — Rename chains (CreatineXXL, JointFlexPlus) and
  discontinued-name currency cues added; no repeat curation session needed.
  74 tests.
- **2026-09-01 (5)** — 8 PDSRG product gaps closed from support-lead
  dispositions; alias table v1.1.0; 88% of chunks now carry part_nos. 72 tests.
- **2026-09-01 (4)** — PDSRG chunking pipeline (§6.2–4) done: 39 docs → 1,061
  chunks, font-based heading detection across four layout templates. 67 tests.
- **2026-09-01 (3)** — Alias curation session done; open item 3 closed. 41 tests.
- **2026-09-01 (2)** — Alias table v1 built (§5): 55 products → 30 families,
  `LEGACY_NAME_OVERRIDES` added after the MuscleDefender miss. 36 tests.
- **2026-09-01** — PDSRG extraction gate **PASS**, human-verified against MVM
  p5–6 and AminoFormula p20; §6 unblocked.
- **2026-08-27** — Azure AI Search provisioned (serverless tier, Central US);
  capacity math fixed the quantization decision.
- **2026-08-26 (2)** — Extraction gate test PASS on 5 stress PDFs (the plan's
  worst-case guess was wrong; the real stress cases are WheySmooth,
  SuperiorAntioxidant and the MVM Design Criteria grids).
- **2026-08-26 (1)** — QA Stages 0–1 shipped: `pipeline/` package, 1,103 docs
  scrubbed and classified, deterministic byte-identical reruns. 18 tests.

## Writing entries

Update **Status** (numbers), **Open items**, and add one **Log** entry per work
item, newest first, with plan-§ refs and the commit SHA.

**Budget: 8 wrapped lines per log entry, hard.** Wrap at 80 columns like the
rest of the file — a single 3,000-byte line is exactly what this rule exists to
prevent. Keep the numbers (counts, test totals), the §refs and the open-item
pointers; drop the mechanism, which belongs in the commit message, the
docstring, or the artifact. Every agent run reads this file end to end, so an
entry that outgrows the budget is charging every future run for detail git
already has.

**Open-item rows are status, not history.** When an item closes, cut the row to
its outcome plus any remainder; a post-mortem in the table is a log entry in the
wrong place.

Add to **Decisions** only if it constrains future work and isn't already in the
plan or AGENTS.md — and if it belongs in the plan, put it there and cite it
here. Bug post-mortems don't belong in this file; the fix is in the code and
the test.
