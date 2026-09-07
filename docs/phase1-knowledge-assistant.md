# Phase 1 — Knowledge Assistant: Implementation Plan

_Status: v1.1 — living document. Written 2026-08-26; corrections folded in
as they are found (each is cited to the `progress.md` entry that made it).
Implementation is under way — `docs/progress.md` is the authority on what
is built; this document is the authority on what we intend to build._

---

## 1. Scope

**Goal:** a read-only Q&A assistant that answers customer nutrition/supplement questions with
citations, grounded exclusively in dotFIT's own knowledge sources.

**In scope (v1):**
- Hybrid RAG over four sources: QA corpus (.docx), PDSRG PDFs, product website copy
  (`products.json`), Suppbeast podcast transcripts (wave 2)
- Menu-type descriptions as static content (no tools)
- PII scrubbing, claims guardrails, medical escalation, AI disclosure
- Golden-set evaluation harness with CI regression

**Explicitly deferred:**
- Meal-plan generation (Phase 2 candidate)
- Menus as relational data + agent query tools (needs tool calls — later addition)
- `Product Summaries` PPTX (marketing material; redundant with legal-approved `products.json`;
  highest claims risk — excluded from Phase 1)
- 688 research PDFs inside `QAs/` (treated as evidence attachments, not indexed)
- `.msg` / `.xls` / `.html` / image files in `QAs/`
- Router/gateway from the target architecture (v1 = one agent, distinct entry surface)

**Ignored by decision:** the thin 2025 QA export (137 files) is taken as-is; no data-owner chase.

---

## 2. Decision log

| # | Decision | Value | Status |
|---|---|---|---|
| 1 | Wave order | Wave 1: QA + products.json + PDSRG. Wave 2: podcast | ✅ confirmed |
| 2 | QA corpus scope | .docx only; research PDFs excluded | ✅ confirmed |
| 3 | Menus | No tool calls in v1; 10 menu descriptions indexed as content; DB + tools later | ✅ confirmed (description indexing = default, small) |
| 4 | 2025 export gap | Ignored | ✅ confirmed |
| 5 | QA cleanup | Hybrid: deterministic parse + LLM-assisted structuring; PII scrub Stage 0 | ✅ confirmed |
| 6 | PII policy | Strip & discard, placeholder substitution; nothing unscrubbed leaves the pipeline | ✅ confirmed |
| 7/8 | Validity & dedupe | Current answers only; filter/dedupe after structuring | ✅ confirmed |
| 9 | Golden set | See §12 (~300 items, stratified + hand-written adversarial) | ✅ recommended, accepted |
| 10 | Authority order | products.json > PDSRG > QA > podcast > menu descriptions (menu authority 5, lowest; owner decision 2026-09-05) | default, unobjected |
| 11 | Claims source | `products.json` is the approved-claims corpus; assistant quotes it | resolved by data |
| 12 | Product identity | `part_no`/`coid` canonical; alias table derived from products.json + QA vocabulary; `product_family` groups flavor SKUs | default |
| 13 | Transcript source | Own ASR pipeline (Azure AI Speech fast-transcription, diarization + word timestamps); no YouTube captions | ✅ confirmed |
| 14 | Index shape | Single AI Search index, filtered by source/authority/currency | recommended, accepted |
| 15 | Ingestion language | Python offline pipeline; C# runtime | recommended, accepted |
| 16 | Chunking | Per-source (see §4–§7) | recommended, accepted |
| 17 | Model provider | Azure OpenAI, region-paired; frontier + small deployments + embeddings | recommended, accepted |

---

## 3. Sources & authority

| Authority | Source | Volume | State |
|---|---|---|---|
| 1 | `data/Product Data/products.json` — legal-approved website copy | 56 SKUs (51 indexed + 5 gear), markdown (avg 4 KB) | Clean; needs section-split + family grouping |
| 2 | `data/Practitioner Dietary Supplement Reference Guide/` | 39 text-layer PDFs, 41 MB | Clean-ish; needs section chunking + table-preservation test |
| 3 | `data/QAs/` .docx | 1,051 files, 2023–2026 (1,103 exported; 9 zero-byte + 51 duplicates removed 2026-09-02) | Saved email threads + free-form notes; heavy cleanup (§4) |
| 4 | `data/Suppbeast Podcast/` | 47 MP3s, ~35–40 h | Needs ASR (§7) — wave 2 |
| — | `data/Reference Menus/All Reference Menus Export.csv` | 10 menu types × 17 calorie levels, 3,534 rows | Deferred (tools); only the 10 descriptions indexed (authority 5) |
| — | `Product Summaries/*.pptx` | 354 slides | Excluded from Phase 1 |

The assistant must answer product questions from authority 1–2 and use authority 3 for
usage/context guidance ("which one for my teenage client?"). Authority is a filterable index
field, not just documentation.

---

## 4. QA corpus pipeline (the main work item)

Input: 1,051 .docx across `QAs/2023..2026/`. Three document shapes observed:
(a) **saved email threads** — expert answer on top, quoted customer message below
(`From:/To:/Sent:/Subject:` + a `Name:/Email:/Phone:/Message:` contact block + dotFIT
copyright footer); (b) **free-form expert notes** (bullet lists, product rules, slide
content); (c) **free-form Q&A notes** — `Question:`/`Answer:` labels with no email thread
(added 2026-09-02 after the last 4 `other` documents were dispositioned).

### Stage 0 — PII scrub (deterministic, local, before anything else)

Runs on raw files; no LLM sees unscrubbed text; nothing is uploaded anywhere before this stage.

1. Extract text from .docx XML (paragraph-level).
2. Structural redaction: remove/placeholder the `Name:`, `Email:`, `Phone:` lines of the contact
   block; redact addresses in `From:/To:` headers; drop signatures; drop the copyright footer
   line (noise). Customer sign-offs below the quoted header (a bare `Thanks,`/`Regards,`-class
   closer + one bare-name line, e.g. `Dr Jane Smith (PhD)`) redact the name span to `[NAME]`
   (honorific/credential kept; `--` delimiter dropped) — the expert region above the header is
   untouched (staff bylines, not PII).
3. Pattern redaction: email addresses, phone numbers (separator-bearing US/CA formats —
   bare 10-digit runs are deliberately not matched, dosages would false-positive), postal
   addresses, URLs to personal/social profiles, SSN/credit-card-like strings → placeholders
   (`[EMAIL]`, `[PHONE]`, `[ADDRESS]`, `[SSN]`, `[CARD]`, `[PROFILE-URL]`, `[CUSTOMER]`,
   `[NAME]`). `[CUSTOMER]` asserts the person is the enquirer; `[NAME]` is used where that
   cannot be asserted (mail recipients, greetings inside quoted replies).
4. Scrub report per file: what was redacted, where. Failed-to-parse files go to a manual
   queue. **Redaction is conservative by design**: a name that cannot be redacted without
   risking prose (an unrecognized honorific, a greeting whose name has no terminator and no
   entry in the curated, corpus-attested `GREETING_NAME_TOKENS` vocabulary) is *flagged* for
   review, never guessed at (progress 2026-09-02 (2); the corpus's greeting residuals were
   dispositioned 2026-09-05 — all real names, now redacted via the vocabulary).

Output: scrubbed text + report. Any file with a residual-PII flag cannot proceed to Stage 2
until cleared.

### Stage 1 — parse & classify (deterministic)

- Split thread into expert-answer section vs. quoted customer section using header markers.
  Three shapes: answer above the quoted block; free-form `Question:`/`Answer:` notes; and
  **forwarded threads whose header is line 0**, where the reply is written *below* the
  quoted contact block (progress 2026-09-02 (2)).
- Classify: `qa_email` (both sections present) | `expert_note` (no customer quote) |
   `other` (internal, research summary, mixed) — deterministic rules; `other`
   goes to the review queue for the Stage 2 LLM-confirm/human path.
- Exclude documents with no expert-answer text (question-only stubs, image-only exports):
   dropped from `documents.jsonl`, tallied in `summary.json` (`n_excluded`), never queued —
   only answerable docs are indexed (owner decision 2026-09-05).
- Capture metadata: year folder, subfolder topic (e.g. `Sweeteners, Natural definitions...`),
   filename (topic summary, genuinely informative). **Filenames are neither rewritten nor
   reviewed** (owner decisions 2026-09-02, 2026-09-05): they are load-bearing topic
   summaries; the former name-repeat flag was removed by owner disposition.

### Stage 2 — LLM-assisted structuring (batch, on scrubbed text only)

Small Azure OpenAI deployment, JSON-schema outputs, GPT-5-family default (no
temperature knob — determinism comes from the strict schema plus the
source-containment diff pass, not temperature 0). Per document:

```
id, source_file, year, doc_type,
question_canonical,        # customer question, normalized to standalone phrasing
question_original,         # verbatim (scrubbed)
answer,                    # expert answer, cleaned (typos, formatting), content unchanged
products[],                # normalized to part_no via alias table (§5)
topics[],                  # from LLM + subfolder hint
audience_flags{minor, pregnancy_breastfeeding, medical_condition, drug_test_athlete, weight_extreme},
currency_cues[],           # mentions of deprecated names: LeanMR, MuscleDefender, NO7 Rage, ...
residual_pii_flag,         # names in greetings etc. -> replaced with [CUSTOMER], flagged
confidence                 # LLM self-assessed extraction quality -> review queue below threshold
```

LLM output is **transcription + structuring, never generation**: answers must be traceable to
source spans; a diff pass (source containment check) verifies no invented content.

### Stage 3 — human review queue

- All items with `confidence < threshold` or `residual_pii_flag`
- Random 5% sample of high-confidence items (audit)
- Owner: support lead / nutritionist (domain reviewers). Tooling: simple review UI or
  spreadsheet export. Estimated: 1–2 days for the flagged fraction + sample.

### Stage 4 — deduplication & currency filter

Built 2026-09-07 (`stage4` subcommand). Only now (post-structuring), in this order:

1. **Cluster**: near-duplicate canonical questions (cosine ≥ 0.88 on `question_canonical`
   embeddings — scan-locked threshold, progress 2026-09-07 (25)) within the same
   product/topic bucket (shared part_no or topic, casefolded); identical question strings
   merge regardless of bucket. Expert notes (no canonical question) are not clustered —
   there is no question to dedup on.
2. **Canonical pick**: newest `thread_date` wins among currency-current members; null
   dates lose; ties break on `source_file`. An all-superseded cluster emits no canonical.
3. **Conflict check**: non-nested member `part_no` sets are the deterministic
   "materially disagree" proxy — the cluster routes to the review queue instead of
   auto-picking (members stay indexed pending disposition; a member's own currency
   supersession still stands). A deterministic 5% audit sample of auto-resolved clusters
   joins the queue; every cluster is in the committed `clusters.jsonl` worksheet (the
   owner's session record).
4. **Currency rules** — the alias-table classes drive everything, and **renames never
   supersede** (owner ruling 2026-09-07: LeanMR → LeanMeal and every legacy rename is an
   identity mapping — same product, same formula; the cue stays on the record as a
   dated-name signal for the runtime, and Stage 2 already expanded it to the successor's
   part_nos). Only **replacements** (a different formula took over the slot — currency
   cue and query redirect only) and **discontinued** (no successor) cues can supersede,
   and only when the answer's guidance is formulation-*dependent*: a gpt-5-mini
   judgment (strict schema, cached, conservative default superseded, low confidence
   queued) separates genuine product guidance from incidental mentions.
   ~~+ "Reformulated with Careflow (2025)"~~ — **dropped** 2026-09-01 (2): absent from
   LeanMeal product copy, verified in products.json.
5. **Output**: everything retained in `stage4/documents.jsonl` with `cluster_id`,
   `stage4_status` (`current` / `superseded_dup` / `superseded_currency`),
   `superseded_by`, `currency_judgment` (+ evidence) and the §9 `is_current` boolean;
   only `is_current = true` canonicals proceed to the index (the build **prunes**
   superseded docs from AI Search as well) **and** the golden set.

### Stage 5 — indexing

One document per canonical Q&A pair — no chunking (they are already the right size).
`question_canonical` and `answer` both searchable; `is_current=true` only.

---

## 5. Product corpus (`products.json`) + alias table

**Section-splitting** per product into: `description`, `supplement_facts` / `nutrition_facts`
(markdown table verbatim — never split), `directions`, `faq_item` (per question). Most products
yield 3–8 small documents. Fields: `part_no`, `product_family`, `flavor_variant`, `url`,
`section`, `content`.

**Product family derivation:** 51 indexed SKUs → 31 families. Group flavor/size SKUs
(3× NO7, 4× AminoFormula, WheySmooth variants…) so a question retrieves one family document set, not three near-duplicates. First
SKU's content is the family document; variants contribute only genuinely distinct content
(e.g. flavor-specific FAQ).

**Alias table (drives QA normalization, retrieval, claims, currency rules):**
- Canonical: `part_no` + `longname`
- **Renames vs replacements are separate sections** (2026-09-02 (3)): a rename is an
  identity mapping and may expand to the successor's `part_no`s; a replacement is a
  different formula that took over the slot and must never be tagged as the successor.
- Aliases: parse "(formerly X)" from names; add QA-corpus vocabulary harvested during Stage 2
  (SB, AF, FS, ActiveMV, FirstString vs First String, WLLS, MVM…) + curated additions from
  support lead. Stored as JSON in the pipeline repo; versioned.

**Freshness:** re-export on product-copy changes + monthly diff against indexed version
(cheap insurance — this is where legal risk lives). Owner: whoever owns web content. (Open item #3.)

---

## 6. PDSRG pipeline

1. **Extraction test first**: run table extraction on the worst-case PDFs and human-verify
   dosages survive. Gate: proceed only when dosage tables extract correctly.
   *Correction (2026-08-26 (2)): the guess above — CreatineMonohydrate, Introduction — was
   wrong. CreatineMonohydrate holds its dosages in prose. The measured stress cases are
   WheySmooth (144 raw tables), SuperiorAntioxidant (densest text) and the MVM Design
   Criteria deck (17-column dosage grids). Gate PASSed and was human-verified 2026-09-01.*
2. Section-level chunking by heading, each chunk prefixed with its path
   (`SuperOmega-3 Fish Oils > Dosing & Evidence`) for context; tables kept intact; target
   ~500–800 tokens, minimal overlap.
3. Metadata: product (via alias table), category (Health/Performance/WeightLoss/MVM/General),
   doc title, page range (citation), `authority=2`.
4. The four Introduction + Manufacturing/Testing docs get `topic` metadata instead of product.

## 7. Podcast pipeline (wave 2, parallelizable from week 1)

1. **ASR**: Azure AI Speech **fast-transcription** (synchronous inline upload —
   no blob storage needed) — diarization on, word-level timestamps,
   custom phrase list with dotFIT product names + common supplement jargon (the predictable
   failure mode). ~35–40 h audio ≈ **$35–40 total**. Region-pinned, enterprise data terms.
   *Self-hosted fallback (if "ourselves" means on-prem): faster-whisper large-v3 + pyannote
   diarization, documented but not the default.*
2. **QC**: 10% sample human spot-check (WER + product-name accuracy); fix phrase list, re-run
   failures.
3. **Segmentation**: merge diarized turns into topic chunks (targets ~90 s /
   200 words, hard caps 120 s / 250 words; phrases atomic, so medians can sit
   at the caps), each
   with `episode_id`, `title` (from filename, normalized — filenames contain full-width
   characters ｜ ： that must be cleaned), `start`/`end` (mm:ss), `speakers`, text.
4. Index with `authority=4`, `source_type=podcast`; citations render as
   "as covered at 14:32 in *Creatine FAQs*" with a YouTube link built from `archive.txt`
   (video IDs on file for all 47 episodes).

## 8. Menus (deferred, minimal v1 presence)

- **Indexed now**: 10 menu-type description paragraphs (`menu_name` + `menu_descr` + calorie
  range 1000–5000) as small documents. The assistant can say *what* menu types exist, not what's
  *in* them. No macros in the index — that invites arithmetic the assistant must not do.
- **Later phase** (already designed): normalized SQL schema (menu_type → instance@calorie_level →
  meal → item/serving/macros; ~300 empty macro cells treated as unknown, not zero) exposed via
  query tools. The CSV is 3,534 rows, 10 types × 17 levels — load is trivial when we get there.

---

## 9. Azure AI Search index design

**One index** (`kb-main`), vector + BM25 hybrid + semantic ranker. Per-source custom chunking is
pushed by the Python pipeline (manual indexing, not integrated vectorization — our chunking is
source-specific).

| Field | Type | Attributes | Notes |
|---|---|---|---|
| `id` | string | key | `{source}-{file|part_no|episode}-{section|chunk}` — dashes, not colons: AI Search keys forbid `:` (`InvalidDocumentKey`, hit 2026-09-05) |
| `source_type` | string | filterable, facetable | `qa` \| `product` \| `pdsrg` \| `podcast` \| `menu_desc` |
| `authority` | int32 | filterable, sortable | 1–5 per §3 (menus rank last — nulls sort unpredictably, so menus carry an explicit 5) |
| `title` | string | searchable | product name / QA canonical question / episode+segment |
| `content` | string | searchable | markdown, tables verbatim |
| `content_vector` | Collection(Edm.Single) | vectorized | Azure OpenAI `text-embedding-3-large` (3072-dim), **int8 scalar quantization + rescoring, no stored vector copies** — binding per 2026-08-27, keep on Basic too |
| `citation_url` | string | retrievable | product URL / PDF path+page / episode link |
| `locator` | string | retrievable | page / mm:ss / section path |
| `products` | collection(string) | filterable, facetable | part_nos via alias table |
| `topics` | collection(string) | filterable, facetable | |
| `date` | DateTimeOffset | filterable, sortable | QA answer date / export date; nullable for podcast |
| `is_current` | bool | filterable | default query filter `is_current eq true`. **Every source must stamp it** — AI Search does not match null, so an unstamped document is invisible to every query (2026-09-02 (3)) |
| `product_status` | string | filterable | `discontinued` etc. SKU lifecycle, a *separate axis* from `is_current`: a discontinued product's docs stay current so "what happened to X" can be answered |

Query profile: hybrid (BM25 + vector, RRF) → **semantic ranker ON** (toggleable, validated on
golden set) → top-5 with `products`/`topics` filters when the query names a product.
Podcast segments carry no `products`/`topics` tags (owner decision 2026-09-05 — spoken
mentions are too noisy for deterministic tagging), so they join product answers via an
unfiltered top-up for the last context slots, never via the filters.
Tier: Basic initially; revisit only if latency/limits demand.

## 10. Models & provider

**Azure OpenAI** (same region as AI Search, e.g. East US 2):

| Purpose | Deployment | Notes |
|---|---|---|
| Answer synthesis | frontier chat model (current flagship at build time) | grounded generation, citations |
| Cleanup / classification / query rewrite | small fast model | QA Stage 2, guardrail checks |
| Embeddings | `text-embedding-3-large` | 3072-dim |

Rationale: region pairing + integrated auth with AI Search; **enterprise no-training data terms**
(double protection behind the Stage-0 scrub); one bill/IAM; .NET SDK. Provider stays swappable
via `Microsoft.Extensions.AI` — a direct-API frontier model can replace synthesis later if
evals demand it.

## 11. v1 runtime (no tool calls)

ASP.NET Core service, Microsoft Agent Framework, single agent:

```
user question
  → guardrail pre-check (small model): medical/escalation intent? product-claim trap?
  → query rewrite (small model): expand aliases → part_no, normalize
  → hybrid search (filter: is_current=true; boost authority)
  → grounded answer (frontier model): quote approved copy for product claims, cite everything
  → post-check: citation present? claims language vs approved copy? escalation respected?
  → SSE stream to widget
```

Standing behaviors: AI-identity disclosure at conversation start; "nutrition guidance, not
medical advice" posture; **hard escalation list**: pregnancy/breastfeeding,
managed conditions (diabetes etc.), eating-disorder signals, under-18, medication interactions,
extreme calorie targets, self-harm → refuse + hand off to human contact. No arithmetic on
macros/calories in v1 (that's the planner/tool work, later).

## 12. Golden set & evaluation

**Composition (~300 items):**
- **250** sampled from canonical current Q&A pairs, stratified: topic × product × year
  (weight 2025–2026 for currency; ensure evergreen topics covered; include the "FAQ families"
  like AminoFormula ×13)
- **50 hand-written adversarial** (do not exist in corpus): medical escalations (pregnancy,
  eating disorder, minor, drug interaction), claim traps ("does ThermAccel cure diabetes?",
  "lower my blood pressure?"), out-of-scope (order status, cancellations → polite redirect)

**Per item:** question · expected points-to-hit (2–5 bullets) · expected source refs ·
forbidden content (non-compliant claim language).

**Splits:** 150 dev / 150 test. **Labeling:** nutritionist + support lead, with a rubric;
~2–3 days effort using the structured store (sampling is scripted).

**Metrics (CI regression on every prompt/retrieval/model change):**
- RAGAS-style: faithfulness ≥ 0.9, answer relevancy ≥ 0.85, context precision ≥ 0.8
- Citation rate = 100% of product-claim answers cite authority 1–2
- Escalation accuracy = **100%** on the adversarial-50 (zero tolerance)
- Tooling: Python eval job (RAGAS or equivalent) + Langfuse tracing; results posted to PR/CI

## 13. Build order

| Week | Track A — data/pipeline | Track B — infra/runtime | Track C — people |
|---|---|---|---|
| 1 | Stage 0–1 scrub prototype on 20 docs (incl. nastiest); PDSRG extraction gate test; alias table v1; podcast phrase list | Provision AI Search + Azure OpenAI (region-paired); skeleton repo (Python pipeline / C# service); kick off ASR sweep (independent) | Confirm escalation policy owner; golden-set rubric draft |
| 2 | Full Stage 0–2 run (1,051 docs); review queue opens; Stage 4 dedupe/currency; PDSRG chunked; products.json section-split | Agent v0: search → grounded answer + citations; eval harness wired | Support lead works review queue; golden-set sampling script reviewed |
| 3 | Index build (products + PDSRG + canonical QA); menu descriptions; transcript QC + segmentation (if ASR done) | Guardrails (pre/post checks, disclosure, escalation); eval loop vs dev set | Golden set labeled (250 + write 50) |
| 4 | Tuning (ranker on/off, filters, chunk sizes) vs golden set | Internal dogfood with support/nutrition team; fix list | Dogfood feedback triage |
| 5–6 | Podcast chunks indexed (wave 2); regression pass | Beta users | Sign-off vs metrics; Phase 2 scoping (menus DB + tools / planner) |

## 14. Open items

Definitions live here; **current status lives in the table at the bottom of
`docs/progress.md`**, which is updated per work item.

1. **Region + SKU pricing** for Azure OpenAI / AI Search (needs an Azure sub decision).
2. **Freshness owner** for products.json re-export cadence (monthly diff proposed).
3. ~~**Alias table curation session** with support lead~~ — **CLOSED 2026-09-01**; outcomes
   are versioned in `CURATED_ALIASES` / `CONTEXT_ONLY_TOKENS`.
4. **PPTX disposition** — parked; revisit only if sales/teaching content is requested later.
5. Semantic ranker on/off decided empirically on golden set (week 3–4).
6. ~~**Stage 3 review-queue dispositions** (opened 2026-09-02 (2))~~ — **CLOSED 2026-09-05**.
   All 35 items dispositioned by the owner: (a) every greeting residual was either a real
   name — redacted via the new corpus-attested `GREETING_NAME_TOKENS` vocabulary — or a
   checker false positive ("Hello my friend"; a bare "Hello" whose residual scan crossed
   the newline), both fixed in `scrub.py`; (b) filenames carry no review burden — the
   `filename_contains_redacted_name` flag is removed; (c) the 6 no-answer and 4 blank
   documents are excluded from `documents.jsonl` and tallied in `summary.json` instead of
   queued. Review queue: 35 → 0.
