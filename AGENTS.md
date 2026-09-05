# AGENTS.md

dotFIT Phase 1 Knowledge Assistant — a RAG pipeline over nutrition/supplement
sources (customer QA emails, PDSRG PDFs, `products.json`, podcasts).

## Read first

- `docs/phase1-knowledge-assistant.md` — the plan. `§N` refs are cited in code
  docstrings, commit messages, and progress entries; keep citing them.
- `docs/progress.md` — living status tracker, newest first. Check the open-items
  table before claiming something is "next"; add one log line per work item.

## Commands

All from `pipeline/` (uv-managed, Python 3.12 pinned in `.python-version`):

```bash
uv sync                                  # only prerequisite is uv itself
uv run pytest                            # full suite; must be green before committing
uv run pytest tests/test_pdsrg.py -k chunk_section -x   # one test / one file
uv run qa-pipeline run    --input ../data/QAs --out ../processed/qa   # stage0 + stage1
uv run qa-pipeline pdsrg  --input "../data/Practitioner Dietary Supplement Reference Guide" \
    --products "../data/Product Data/products.json" --out ../processed/pdsrg
uv run qa-pipeline aliases --products "../data/Product Data/products.json" \
    --out ../processed/aliases --qa-docs ../processed/qa/stage1/documents.jsonl
```

Subcommands: `stage0`, `stage1`, `run`, `aliases`, `pdsrg`. Shared flags:
`--include GLOB` (repeatable), `--limit N`, `--quiet`, `--fail-on-error`,
`--no-prune` (by default outputs whose input disappeared are deleted so the
output tree always mirrors the corpus). `pdsrg` adds `--keep-references`,
`--citation-base`.

Corpus filenames contain spaces, commas and `&` — always quote paths.

`scripts/pdsrg_gate.py` and `scripts/pdsrg_density_scan.py` are one-off
analysis tools, not part of the CLI; the gate's validated extraction strategy
was folded into `pdsrg.py` and is the reason its settings look the way they do.

## Architecture

Two independent corpora feed a shared alias vocabulary.

**QA corpus (`data/QAs/**/*.docx` → `processed/qa/`)** — a strict two-stage
contract; Stage 1 only ever reads Stage 0 output, never the raw docs.

- `extract.py` — `.docx` → lines. Goes below `paragraph.text` (which drops
  hyperlink runs) to raw `w:t` nodes; tables flatten to pipe-joined rows.
  Never raises: corrupt files return `ExtractResult(ok=False)` and are routed
  to the queue.
- `structure.py` — shared structural detection for the corpus's three document
  shapes (email thread, free-form expert note, labeled Q&A / forwarded thread
  where the reply sits *below* the quoted block). Both Stage 0 (signature
  cutting) and Stage 1 (section split) need the customer-header position, so it
  lives here once.
- `scrub.py` (Stage 0) — structural redaction + greeting de-naming + pattern
  redaction; emits scrubbed text and a per-file report. The placeholder
  vocabulary (`[EMAIL] [PHONE] [CUSTOMER] [NAME] [SIGNATURE]` …) is a contract
  Stage 1 and human reviewers parse — don't change tokens casually.
- `stage1.py` — section split, classify (`qa_email` / `expert_note` / `other`),
  metadata. `thread_date` is the enquiry's `Sent:` header, **not** the expert's
  reply date; Stage 4 must treat it as a currency lower bound. Date parsing is
  locale-independent by construction.
- Output contract for Stage 2: `stage1/documents.jsonl` (sorted by
  `source_file`), plus `review_queue.jsonl`, `summary.json`, and the *committed*
  `stage0/errors.json` (the queue reads that, not the gitignored `runs/`).

**PDSRG corpus (PDF → `processed/pdsrg/chunks/chunks.jsonl`)** — `pdsrg.py`:
pdfplumber hybrid table extraction (`lines` strategy, trivial + prose
false-positives dropped, collapse signature re-extracted with `text` to restore
ultra-wide dosage grids), font-based heading detection across four layout
templates (incl. a slide-deck path), section chunking with heading-path
prefixes (~650/800 tokens, tables atomic), product/category/topic metadata from
`STEM_META`. References sections are excluded by default.

**Alias table (`alias.py`)** — two layers: deterministic derivation from
`products.json` (family = longname minus trailing ` - <variant>`; legacy names
from `(formerly X)` markers) plus a curated overlay. It also harvests candidates
from the QA corpus into a review worksheet. Consumed by `pdsrg.py` to tag chunks
with `part_no`s and by query-side expansion — **never used to rewrite corpus
text**.

**`io_utils.py`** is the single choke point for every read/write (explicit
UTF-8, `newline="\n"`, POSIX-normalized ids, sorted iteration). New file I/O
goes through it, otherwise the cross-platform guarantee silently breaks.

## Working rules that bite

- **Determinism is testable and tested**: reruns must be byte-identical, and
  from a *different working directory* too — paths in outputs are relative to
  the input root, never the cwd. Timestamps live only in `runs/` manifests.
- **Curation lives in Python constants**, JSON outputs are derived:
  `CURATED_ALIASES`, `CURATED_LEGACY`, `CURATED_REPLACEMENTS`,
  `CONTEXT_ONLY_TOKENS` (`alias.py`), `STEM_META` (`pdsrg.py`),
  `ACCEPTED_HONORIFIC_NAMES`, `GREETING_NAME_TOKENS`, `ROLE_MAILBOXES`
  (`scrub.py`). Unknown stems / unattested names must **raise**, not silently
  produce untagged chunks.
- **A rename is not a replacement**: a rename is an identity mapping and may
  expand to the successor's `part_no`s; a replacement is a different formula and
  is only a currency cue. Separate alias-table sections.
- **Corpus attestation**: an alias must appear in the corpus in the form the
  corpus writes it. A 0-doc worksheet row means the alias is wrong.
- **PII**: `data/QAs/` is read-only and holds real customer mail; nothing unscrubbed
  leaves Stage 0. Redaction is conservative — what cannot be redacted without
  eating prose is *flagged*, never guessed. A review queue of 0 is a claim to be
  earned, not a target. Tests use synthetic fixtures (`tests/conftest.py`,
  example.com / 555 numbers) — never paste real corpus text into tests or docs.
- **`products.json` is the legal-approved claims corpus** — quote it, never
  paraphrase claims.
- `processed/` is committed but derived: regenerate, don't hand-edit. Numbers
  quoted in `docs/progress.md` must match a regenerated run.
- Commits: `area: description`, lowercase. New behavior gets a unit test, and
  the test count is cited in the progress entry.
