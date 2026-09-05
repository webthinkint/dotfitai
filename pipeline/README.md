# QA Corpus Pipeline — Stage 0 (PII scrub) + Stage 1 (parse & classify)

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
  and §8 menu description docs. Vectors (`text-embedding-3-large`, 3072-dim)
  embed `title + content` and cache under the gitignored
  `runs/embeddings.jsonl`, so the committed `documents.jsonl` (no vectors)
  stays byte-identical across reruns.

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

Subcommands: `stage0`, `stage1`, `run` (both), `aliases`, `pdsrg`, `index`.
Options on all: `--include GLOB` (repeatable), `--limit N` (pilots), `--quiet`,
`--fail-on-error` (non-zero exit if any file fails — for cron/CI), `--no-prune`
(keep outputs whose input has been deleted; by default they are removed so the
output tree always matches the corpus). `pdsrg` adds `--keep-references` and
`--citation-base`; `index` (no corpus tree to prune) has `--limit N`,
`--no-embed` (shape only), `--no-upload` (embed, skip AI Search), `--reset`
(drop + recreate the index) and `--index-name`.

PDSRG chunking (plan §6):

```bash
uv run qa-pipeline pdsrg \
    --input "../data/Practitioner Dietary Supplement Reference Guide" \
    --products "../data/Product Data/products.json" \
    --out ../processed/pdsrg
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

PDSRG outputs (relative to the `pdsrg --out` root, default `processed/pdsrg`):

```
<out>/chunks/chunks.jsonl    one record per chunk (index-ready, §9 fields)
<out>/chunks/summary.json    per-doc stats + review flags
<out>/review/<slug>.md       per-doc chunking outline (human spot-check)
<out>/runs/pdsrg-<ts>.json   run manifest
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
- `honorific_plus_name` — e.g. "Dr. Smith" in content; humans confirm whether
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

## Verified corpus numbers (full run, 2026-09-05)

- 1,051 `.docx` → **1,051 scrubbed**, 0 parse errors
- Classification: **777 qa_email**, 264 expert_note, 0 other — 10
  unanswerable docs excluded (6 no-answer stubs, 4 blanks), not queued
- Redactions (per occurrence): 1,681 contact-block email fields, 1,057 inline
  emails, 774 customer-name fields, 693 opening greetings + 283 greetings in
  quoted replies, 362 phone fields, 243 inline phones, 221 recipient display
  names, 25 postal addresses, 5 card-shaped numbers, 3 social-profile URLs
- Dropped: 711 copyright footers, 195 disclaimer lines, 139 signature blocks
- 0 files in the review queue (the 35 flagged 2026-09-02 were dispositioned
  2026-09-05 — see plan §14 item 6)
- DOI strings like `10.1007/s13197-011-0571` are correctly *not* matched as
  phones; the address rule was corpus-verified with zero false positives

Earlier entries in `docs/progress.md` quote per-*document* pattern counts: the
counter incremented once per pattern per file until 2026-09-02 (2).
