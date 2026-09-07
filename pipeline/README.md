# QA Corpus Pipeline — Stage 0 (PII scrub) + Stage 1 (parse & classify) + Stage 2 (LLM structuring)

Implements §4 Stages 0–1 of `docs/phase1-knowledge-assistant.md` and §6
(PDSRG chunking):

- **Stage 0** — deterministic, local PII scrub of `.docx` QA files: structural
  redaction (contact blocks, `From:`/`To:`/`Cc:` display names, signatures,
  copyright footers), greeting de-naming across the whole document, pattern
  redaction (emails, phones, postal addresses, SSNs, cards, social-profile
  URLs), per-file scrub reports. No LLM, no network. What cannot be redacted
  without risking prose is **flagged for review, never guessed at**.
- **Stage 1** — deterministic parse & classify: split expert answer vs. quoted
  customer message (three document shapes, including forwarded threads whose
  header is line 0 and whose reply sits *below* the quoted block), classify
  (`qa_email` / `expert_note` / `other`), extract metadata (year, topic
  subfolder, `thread_date` from `Sent:`, question text), route edge cases to a
  review queue. Date parsing is locale-independent by construction.
- **Stage 2** (`stage2` subcommand) — LLM-assisted structuring on scrubbed text
  only (small chat deployment, strict JSON-schema, no temperature knob):
  `question_canonical`, cleaned `answer` (transcription, never generation — a
  source-containment diff pass verifies no invented content), `products[]`
  normalized to `part_no` via the alias table (LLM mentions mapped
  deterministically, unioned with the blind alias/rename text scan;
  `CURATED_LLM_ONLY_ALIASES` resolve on the mention path only — the model
  supplies the context that tells `Women's MV` from `women's health`;
  context-only tokens, replacements and discontinued names never tag),
  `topics[]` (LLM + subfolder hint), `audience_flags`, `currency_cues`
  (tolerant deterministic scan), `residual_pii_flag` (Stage 1 OR LLM — the LLM
  redacts what it flags), `confidence` → review queue below threshold.
  LLM responses cache in the gitignored `runs/stage2_cache.jsonl` (keyed
  deployment|api-version|prompt|source), so same-machine reruns are free and
  byte-identical; `--no-llm` runs the deterministic rule-based fallback (no
  Azure calls — CI/tests/shaping).
- **Stage 4** (`stage4` subcommand) — deduplication & currency filter on the
  Stage 2 canonicals (§4): near-duplicate question clustering (cosine ≥ 0.88
  on `question_canonical` embeddings — scan-locked threshold — compared only
  within shared-product/topic buckets), newest-wins canonical pick among
  currency-current members, non-nested part_no sets as the conflict proxy
  (queue the cluster, no auto-pick), 5% deterministic cluster audit, and the
  currency pass: renames never supersede (identity mappings — the cue stays
  as a dated-name signal); replacement/discontinued cues get a gpt-5-mini
  formulation-dependence judgment (strict schema, cached, conservative
  default superseded). Outputs `documents.jsonl` (everything retained, with
  `cluster_id` / `stage4_status` / `superseded_by` / `currency_judgment` /
  `is_current`), `clusters.jsonl` (the owner's worksheet), `review_queue.jsonl`.
- **PDSRG** (`pdsrg` subcommand) — chunk the Practitioner Dietary Supplement
  Reference Guide PDFs: hybrid table extraction inherited from the validated
  gate test + the prose false-positive filter, font-based heading detection
  across the corpus's four layout templates, section-level chunks with
  heading-path prefixes (~650/800 tokens target/max, tables atomic), product /
  category / topic metadata, per-doc review outlines. References sections are
  excluded by default (`--keep-references` to include).
- **Index** (`index` subcommand) — shape and upload the §9 `kb-main` AI Search
  index: PDSRG chunks (pass-through — already §9-stamped), products.json §5
  section-split with family grouping (the canonical SKU's sections are the
  family documents; variants contribute only genuinely distinct sections),
  §8 menu description docs, §7 podcast segments (`authority=4`,
  `citation_url` null until the archive.txt → YouTube mapping is
  verified), and Stage 2 QA canonicals (`authority=3`, one doc per pair —
  answers over the embedding cap split into paragraph-boundary parts, never
  truncated). Vectors (`text-embedding-3-large`, 3072-dim)
  embed `title + content` and cache under the gitignored
  `runs/embeddings.jsonl`, so the committed `documents.jsonl` (no vectors)
  stays byte-identical across reruns.
- **Golden set** (`golden` subcommand) — the §12 sampling pass over the
  Stage 4 canonicals: 250 items stratified by enquiry year × product family
  (2025–2026 weighted ×2 for currency, ≥1 per present year, FAQ-family and
  evergreen-topic coverage floors), split 125/125 plus a 25/25 adversarial
  scaffold = §12's 150 dev / 150 test. Outputs the labeling artifacts
  (`sample.jsonl`, `worksheet.md` with the rubric and prefilled source
  candidates, `adversarial.md`, `summary.json` allocation audit); selection
  order is sha256(seed:id) — no RNG, byte-identical reruns from any cwd.

Stage 2 (LLM structuring) and Stage 4 (dedupe/currency) are later additions;
the QA stages consume only `data/QAs/**/*.docx`.

## Tooling

- [uv](https://docs.astral.sh/uv/) (only prerequisite; no system Python needed)
- Python **3.12** (pinned in `.python-version`)
- Runtime deps: `python-docx`, `pdfplumber` (corpus); `openai`,
  `azure-search-documents` (Azure data plane — credentials live only in the
  gitignored `.env`, loaded by `azure_config.py`)
- Dev deps: `pytest`
- `uv.lock` is committed → identical dependency resolution on every machine

## Usage

```bash
# dev (Windows) — from pipeline/
uv sync                      # creates .venv from the lockfile
uv run pytest                # unit tests (synthetic fixtures, no real PII)
uv run qa-pipeline run --input ../data/QAs --out ../processed/qa

# production (Linux VM) — same commands, same bytes out
uv sync
uv run qa-pipeline run --input /srv/dotfit/QAs --out /srv/dotfit/processed/qa
```

Subcommands: `stage0`, `stage1`, `stage2`, `stage4`, `run` (stages 0+1), `aliases`, `pdsrg`,
`index` (+ `--qa-docs`), `podcast`. Options on all: `--include GLOB` (repeatable), `--limit N`
(pilots), `--quiet`, `--fail-on-error` (non-zero exit if any file fails —
for cron/CI), `--no-prune` (keep outputs whose input has been deleted; by
default they are removed so the output tree always matches the corpus).
`pdsrg` adds `--keep-references` and `--citation-base`; `stage2` takes
`--qa-docs` + `--products` instead of `--input` and adds `--min-confidence`,
`--api-version`, `--no-llm` (rule-based fallback, no Azure calls) and `--no-cache`;
`stage4` mirrors `stage2` (`--min-judge-confidence`, `--no-llm` = conservative
supersession + queue; clustering still embeds);
`podcast` takes `--transcripts` + `--audio` instead of `--input` (no corpus tree to prune);
`index` (no corpus tree to prune) has `--limit N`,
`--no-embed` (shape only), `--no-upload` (embed, skip AI Search), `--reset`
(drop + recreate the index — waits out the async deletion), `--no-prune`
(keep service-side docs absent from the build; default prunes them) and
`--index-name`.

PDSRG chunking (plan §6):

```bash
uv run qa-pipeline pdsrg \
    --input "../data/Practitioner Dietary Supplement Reference Guide" \
    --products "../data/Product Data/products.json" \
    --out ../processed/pdsrg
```

Podcast segmentation (plan §7 step 3 — transcripts must exist first, see
`scripts/asr_pilot.py --all`):

```bash
uv run qa-pipeline podcast \
    --transcripts ../processed/podcasts/transcripts \
    --audio "../data/Suppbeast Podcast" \
    --out ../processed/podcasts
```

§12 golden-set sampling (labeling worksheet + adversarial scaffold):

```bash
uv run qa-pipeline golden \
    --qa-docs ../processed/qa/stage4/documents.jsonl \
    --products "../data/Product Data/products.json" \
    --out ../processed/golden
```

§9 index build (shape + embed + upload):

```bash
uv run qa-pipeline index \
    --chunks ../processed/pdsrg/chunks/chunks.jsonl \
    --products "../data/Product Data/products.json" \
    --menus "../data/Reference Menus/All Reference Menus Export.csv" \
    --out ../processed/index --no-upload    # drop --no-upload to upload
```

Pilot per plan §13 week 1 (20 docs incl. nastiest):

```bash
uv run qa-pipeline run --input ../data/QAs --out ../processed/qa-pilot \
    --include "2023/*.docx" --limit 20
```

Stage 2 pilot (5 docs, live small-chat calls) and full run:

```bash
uv run qa-pipeline stage2 --qa-docs ../processed/qa/stage1/documents.jsonl \
    --products "../data/Product Data/products.json" --out ../processed/qa/stage2-pilot \
    --limit 5
uv run qa-pipeline stage2 --qa-docs ../processed/qa/stage1/documents.jsonl \
    --products "../data/Product Data/products.json" --out ../processed/qa/stage2
# offline equivalent (no Azure calls, confidence 0, everything queued):
uv run qa-pipeline stage2 --qa-docs ../processed/qa/stage1/documents.jsonl \
    --products "../data/Product Data/products.json" --out ../processed/qa/stage2 \
    --no-llm
```

Stage 4 (consumes Stage 2 canonicals; embeddings from the question surface,
judgments from the small chat deployment — both cached):

```bash
uv run qa-pipeline stage4 --qa-docs ../processed/qa/stage2/documents.jsonl \
    --products "../data/Product Data/products.json" --out ../processed/qa/stage4
```

## Output layout

```
<out>/stage0/text/<year>/<subdirs>/<name>.txt      scrubbed text (LF, UTF-8)
<out>/stage0/reports/<year>/<subdirs>/<name>.json  per-file scrub report
<out>/stage1/documents.jsonl                       one record per document
<out>/stage1/review_queue.jsonl                    parse errors + residual PII + edge cases
<out>/stage0/errors.json                           parse errors (committed;
                                                   the review queue reads this,
                                                   not the gitignored runs/)
<out>/stage1/summary.json                          counts by type/year
<out>/runs/stage0-<timestamp>.json                 run manifest (audit trail)
```

Stage 2 outputs (relative to the `stage2 --out` root, default
`processed/qa/stage2`):

```
<out>/documents.jsonl        one canonical record per document (sorted by
                            source_file — byte-identical reruns)
<out>/review_queue.jsonl    low confidence + residual PII + containment fails +
                            LLM errors + deterministic 5% audit sample
<out>/summary.json           counts + review reasons + unresolved-mention tally
                            (curation signal for the alias worksheet)
<out>/runs/stage2-<ts>.json run manifest
<out>/runs/stage2_cache.jsonl LLM response cache (gitignored — API results,
                            keyed deployment|api-version|prompt|source)
```

- `documents.jsonl` record: `id, source_file, year, doc_type, thread_date,
  topic_subfolder, filename, question_original, question_canonical, answer,
  products[] (int part_nos), products_unresolved[] (curation signal, never a
  queue reason), topics[], audience_flags{minor, pregnancy_breastfeeding,
  medical_condition, drug_test_athlete, weight_extreme}, currency_cues[],
  residual_pii_flag, confidence, containment_score, needs_review, llm_error,
  model` — the canonical input contract for Stage 4/indexing. Evidence spans
  for residual-PII flags live only in the gitignored cache, never in records.
- Reruns are byte-identical when LLM responses are (same-machine cache hit or
  `--no-llm`); run manifests carry timestamps + input SHA-256s.

PDSRG outputs (relative to the `pdsrg --out` root, default `processed/pdsrg`):

```
<out>/chunks/chunks.jsonl    one record per chunk (index-ready, §9 fields)
<out>/chunks/summary.json    per-doc stats + review flags
<out>/review/<slug>.md       per-doc chunking outline (human spot-check)
<out>/runs/pdsrg-<ts>.json   run manifest
```

Podcast outputs (relative to the `podcast --out` root, default
`processed/podcasts` — transcripts themselves live in
`<out>/transcripts/`, written by `scripts/asr_pilot.py --all`):

```
<out>/segments/segments.jsonl  one record per topic chunk (id,
                               episode_id/title, chunk_index, start/end
                               mm:ss + ms, speakers, Speaker-turn text)
<out>/segments/summary.json    per-episode stats
<out>/runs/podcast-<ts>.json  run manifest
```

Index outputs (relative to the `index --out` root, default `processed/index`):

```
<out>/documents.jsonl        one §9 record per document (no vectors —
                            byte-identical reruns)
<out>/summary.json           counts + embedding/upload stats
<out>/runs/embeddings.jsonl  vector cache (gitignored — API results,
                            keyed deployment|api-version|text)
<out>/runs/index-<ts>.json   run manifest
```

- `documents.jsonl` record: `id, source_file, year, topic_subfolder, filename,
  doc_type, needs_review, residual_pii_flag, thread_date, question,
  expert_section, customer_section, scrub_flags, n_lines` — the deterministic
  input contract for Stage 2. **`thread_date` is the enquiry's `Sent:` header**,
  not the expert's reply date (which the corpus rarely records); Stage 4 should
  treat it as a lower bound when ordering by currency.
- Per-file outputs are **deterministic** (verified: byte-identical reruns);
  run manifests carry timestamps + input SHA-256s for the audit trail.

## Cross-platform contract (Windows dev ⇄ Linux prod)

The code is written so the same commit produces identical output on both:

| Concern | How it's handled |
|---|---|
| Text encoding | every read/write is explicit `utf-8`; console reconfigured via `configure_stdio()` (Windows code pages crash otherwise) |
| Line endings | `newline="\n"` on all writes (no CRLF leakage) |
| Path separators | `pathlib` everywhere; document ids are POSIX-normalized relative paths (`rel_posix`) |
| Determinism | sorted iteration, no timestamps in per-file outputs, pure scrub functions |
| Dependencies | `uv.lock` committed; no OS-level packages required |
| Filenames | corpus names contain spaces/commas/`&` — always quote paths in shells |

Known benign platform difference: `runs/*.json` manifests record
`platform`/`python` — audit metadata only, never compared for equality.

## Review queue (feeds plan §4 Stage 3)

`review_queue.jsonl` reasons map to the plan's manual queue:

- `parse_error` — file is not a valid `.docx`; needs a data-owner look (read
  from the committed `stage0/errors.json`)
- `honorific_plus_name` — e.g. "Dr. Smith" in content (period optional —
  "Dr Smith" flags the same way); humans confirm whether
  it's staff/expert (clear it into `ACCEPTED_HONORIFIC_NAMES`) or a customer
- `greeting_name_residual` — a greeting whose name has no terminator and is
  not in the corpus-attested `GREETING_NAME_TOKENS` vocabulary (`Hey Jasmine
  any advice…`); redacting it automatically would corrupt prose
- `no_quotable_structure` — `doc_type: other`; the LLM-confirm path in Stage 2

Not queued, by owner disposition (2026-09-05): documents with no expert
reply and blank documents are excluded from `documents.jsonl` and tallied in
`summary.json` (`n_excluded`) instead — there is nothing left to decide about
them. Filenames are neither scrubbed nor flagged (owner disposition
2026-09-05); the former `filename_contains_redacted_name` flag is gone.

## Verified corpus numbers (Stage 2, full run 2026-09-06; alias-1.3.0 regen 2026-09-07)

- 1,041 Stage 1 docs → **1,041 canonical records** on prompt 1.1.0, 0 fallbacks,
  0 LLM errors (~1,040 small-chat calls across chunked runs — per-doc cache
  checkpoints make the run resumable surviving 2 timeouts and 1 Azure 403;
  a cache-hit rerun is byte-identical with 0 calls). Alias-1.3.0 regen
  (2026-09-07): 2 live calls (the two scrub-fix docs) + 1,039 cache hits,
  0 errors — 338 docs gained part_nos (42 newly tagged), unresolved mentions
  3,474 → 2,292 (−34%)
- Review queue **222** (21%, down from 590 pre-triage), fully dispositioned
  (rounds 1–2, `docs/progress.md` entries 19–20): 168 residual-PII flags
  (customer-side names correctly caught; staff bulk-accepted via silent-redact
  vocabulary), 48 deterministic 5% audit samples, 1 containment fail,
  5 low-confidence
- Quality: containment median 0.996 (answer word-recall vs source), confidence
  median 0.92; 676 docs carry product tags (50 part_nos), 306 carry currency
  cues, 275 null canonical questions (264 expert notes + 11 question-less)
- Curation signal (never a queue reason), post-1.3.0 tally — top unresolved
  mentions: `dotFIT Multivitamin & Mineral` (159), `Kids` (149), `MVM` (123),
  `VeganMV` (76), `dotFIT Nutrition High Protein Bar` (52), `1-Vegan` (48),
  `dotFIT protein shakes` (38), `Gatorade` (30), `Protein Powders` (29),
  `dotFIT protein mix` (29). Curation pass 2 (2026-09-07, alias table 1.3.0)
  resolved the family spellings, dose tiers and `Women's` (LLM-only tier);
  `Kids`/`VeganMV`/`1-Vegan` stay unresolved by design (discontinued — no
  part_no to tag) and `MVM` is context-only — this tally is the input to any
  pass 3 (§14 open item 3)
- Safety, re-verified on the regen artifacts: 0 own-answer evidence leaks
  (flagged name spans absent from their own answers, including both live-call
  docs); 0 raw emails/phones in title/answer fields

## Verified corpus numbers (Stage 0–1 full run, 2026-09-05)

- 1,051 `.docx` → **1,051 scrubbed**, 0 parse errors
- Classification: **777 qa_email**, 264 expert_note, 0 other — 10
  unanswerable docs excluded (6 no-answer stubs, 4 blanks), not queued
- Redactions (per occurrence): 1,681 contact-block email fields, 1,057 inline
  emails, 774 customer-name fields, 693 opening greetings + 283 greetings in
  quoted replies, 43 customer sign-off names (`Thanks,`/`Regards,` + bare name
  below the quoted header — `[NAME]`, honorific/credential kept), 362 phone
  fields, 243 inline phones, 221 recipient display names, 25 postal addresses,
  5 card-shaped numbers, 3 social-profile URLs
- Dropped: 711 copyright footers, 195 disclaimer lines, 139 signature blocks,
  1 `--` email delimiter above a redacted sign-off
- 0 files in the review queue (round 2, owner-dispositioned 2026-09-05: 5
  study-author honorifics cleared into `ACCEPTED_HONORIFIC_NAMES`, 2 full-name
  sign-off leaks redacted by the new sign-off rule — see plan §14 item 6)
- DOI strings like `10.1007/s13197-011-0571` are correctly *not* matched as
  phones; the address rule was corpus-verified with zero false positives

Earlier entries in `docs/progress.md` quote per-*document* pattern counts: the
counter incremented once per pattern per file until 2026-09-02 (2).
