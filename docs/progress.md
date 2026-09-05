# Phase 1 — Build Progress

What is built, what is open, and the decisions made along the way.
Design decisions themselves live in `phase1-knowledge-assistant.md` (the plan,
kept current); the hard rules they produced live in `AGENTS.md`. This file is
the status view, not a narrative — see "Writing entries" at the bottom.

## Status (§13 build order)

Numbers verified 2026-09-05. 129 tests green.

| Component | Plan § | State | Verified output |
|---|---|---|---|
| QA Stage 0 — PII scrub | §4 | **done** | 1,051 `.docx` (1,103 − 9 empty − 43 dup groups) |
| QA Stage 1 — parse & classify | §4 | **done** | 777 qa_email / 264 expert_note / 0 other; 766 `thread_date`; review queue 0; 10 unanswerable excluded (6 no-answer, 4 blank) |
| PDSRG extraction gate | §6.1 | **passed**, human-verified | 5 stress PDFs |
| PDSRG chunking | §6.2–4 | **done** | 39 docs → 1,080 chunks (~404K tokens, median 349); 950 with part_nos; 53 discontinued-stamped; 1 atomic oversize table |
| Alias table | §5 | **done**, v1.2.0 | 51 indexed SKUs → 31 families; worksheet 19/19 attested |
| QA Stage 2 (canonicalize) | §4 | blocked on Azure model deployments (item 1) | — |
| Podcast ASR | §7 | not started | — |
| Index + retrieval | §9–11 | not started | — |

Artifacts: `processed/qa/`, `processed/pdsrg/`, `processed/aliases/`.
Per-run counts live in each `summary.json`; numbers quoted here must match a
regenerated run.

## Open items (§14)

| # | Item | Status |
|---|---|---|
| 1 | Azure region + SKU | **partial** — services provisioned 2026-09-05 (AI Search, Azure OpenAI, AI Speech); credentials in local `.env`; `text-embedding-3-large` deployed and smoke-verified (3072-dim, `scripts/embedding_smoke.py`). Remaining: quota increase for frontier + small chat deployments |
| 2 | products.json freshness owner | **open** — 8 PDSRG-only gaps closed 2026-09-01. Remaining: name the owner, set monthly diff cadence |
| 3 | Alias-table curation session | **closed** 2026-09-01 — outcomes in `CURATED_ALIASES` / `CONTEXT_ONLY_TOKENS`; worksheet is the session record |
| 4 | PPTX disposition | parked |
| 5 | Semantic ranker on/off | week 3–4, decide empirically |
| 6 | Stage 3 review-queue dispositions | **closed** 2026-09-05 — all 35 dispositioned: real greeting names redacted via `GREETING_NAME_TOKENS` (false positives fixed), filename flag removed, no-answer + blank docs excluded. Queue 0 |

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
- Greeting residuals (2026-09-05 dispositions): 6 of the 11 flags were real
  names in terminator-free shapes — redacted via the corpus-attested
  `GREETING_NAME_TOKENS` vocabulary (honorific-prefixed, lowercase, and-joined
  and slash-joined forms; `my`/`friend` are stopwords, and the residual scan
  no longer crosses newlines — those 5 flags were checker false positives).
  An unknown name in the same position still flags.
- 9 zero-byte files deleted; 51 duplicates deleted across 43 md5-identical
  groups (keep rule: earliest year → shallowest path → lexicographic). Full
  KEEP/DEL record: `processed/qa/runs/data-cleanup-2026-09-02.log`, since
  `data/` is untracked.
- Residual honorific+name flags were all public figures, dotFIT staff, or
  street addresses → owner-approved into `ACCEPTED_HONORIFIC_NAMES` (16
  surnames), matched at the surname position only.
- `answer_date` → `thread_date`: the field reads the thread's `Sent:` header,
  i.e. the enquiry's date. Stage 4 should treat it as a currency lower bound.

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

## Log

Newest first. One line per work item; detail belongs in the plan, the code, or
the artifact it describes.

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

Update **Status** (numbers), **Open items**, and add one **Log** line per work
item, newest first, with plan-§ refs. Add to **Decisions** only if it
constrains future work and isn't already in the plan or AGENTS.md — and if it
belongs in the plan, put it there and cite it here. Bug post-mortems don't
belong in this file; the fix is in the code and the test.
