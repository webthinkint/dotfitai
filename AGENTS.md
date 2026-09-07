# AGENTS.md

dotFIT Phase 1 Knowledge Assistant — a RAG pipeline over nutrition/supplement
sources (customer QA emails, PDSRG PDFs, `products.json`, podcasts).

## Read first

- `docs/phase1-knowledge-assistant.md` — the plan. `§N` refs are cited in code
  docstrings, commit messages, and progress entries; keep citing them.
- `docs/progress.md` — living status tracker, newest first. Check the open-items
  table before claiming something is "next"; add one log line per work item.
- **Module docstrings are the detail.** Every module opens with its §ref, its
  contract and the rulings baked into it; `runtime/README.md` does the same for
  the .NET side. Read the file you are about to touch — this document is a map,
  not a substitute.

## Commands

From `pipeline/` (uv-managed, Python 3.12 pinned in `.python-version`):

```bash
uv sync                                  # only prerequisite is uv itself
uv run pytest                            # full suite; must be green before committing
uv run pytest tests/test_pdsrg.py -k chunk_section -x   # one test / one file
uv run qa-pipeline --help                # subcommands; <sub> --help for all flags
```

Subcommands: `stage0`, `stage1`, `stage2`, `stage4`, `run`, `aliases`, `pdsrg`,
`index`, `podcast`, `golden`. Path defaults are repo-root-relative, so from
`pipeline/` pass `../data/...` / `../processed/...`. Corpus filenames contain
spaces, commas and `&` — always quote paths. Outputs whose input disappeared are
pruned by default (`--no-prune` to keep), so the output tree mirrors the corpus.

Runtime (.NET 10): `cd runtime && dotnet build && dotnet test`; live checks are
`dotfit-agent` CLI verbs. See `runtime/README.md` for verbs, flags and config.

`scripts/pdsrg_gate.py` and `scripts/pdsrg_density_scan.py` are one-off analysis
tools, not part of the CLI; the gate's validated extraction strategy was folded
into `pdsrg.py` and is why its settings look the way they do.

## Architecture

Two independent corpora feed a shared alias vocabulary, then a shared index.

QA: `data/QAs/**/*.docx` → stage0 → stage1 → stage2 → stage4 → `index`
PDSRG: PDF → `pdsrg` → `index`; products.json + menus + podcasts → `index`
Runtime queries the index; `alias.py` feeds both `pdsrg` and query expansion.

| File | Owns | Plan |
|---|---|---|
| `extract.py` | `.docx` → lines; never raises | §4 |
| `structure.py` | shared shape detection (Stage 0 + Stage 1 both need it) | §4 |
| `scrub.py` | Stage 0 PII scrub + per-file report | §4 |
| `stage1.py` | section split, classify, metadata | §4 |
| `stage2.py` | LLM canonicalization, product normalization | §4 |
| `stage4.py` | dedup clustering + currency stamping | §4 |
| `alias.py` | derived + curated alias table, QA candidate harvest | §5 |
| `pdsrg.py` | PDF → section chunks with heading paths | §6 |
| `index_build.py` + `embeddings.py` | `kb-main` shaping, embedding, upload | §9 |
| `golden.py` | stratified golden-set draw + labeling worksheets | §12 |
| `azure_config.py` | root `.env` contract, `require=` subsets, masked repr | §9–11 |
| `io_utils.py` | **every** read/write | — |
| `runtime/` | guardrail → rewrite → search → answer → post-check | §11 |

Cross-file contracts that no single docstring owns:

- **The two-stage contract**: Stage 1 only ever reads Stage 0 output, never the
  raw docs. Same for each later stage: input is the previous stage's committed
  `documents.jsonl`, plus the committed `stage0/errors.json` for the queue (not
  the gitignored `runs/`).
- **The placeholder vocabulary** (`[EMAIL] [PHONE] [CUSTOMER] [NAME]
  [SIGNATURE]` …) is parsed by Stage 1 and by human reviewers — don't change
  tokens casually.
- **`thread_date` is the enquiry's `Sent:` header**, not the expert's reply
  date; Stage 4 must treat it as a currency lower bound.
- **The alias table is never used to rewrite corpus text** — only to tag and to
  expand queries. Its curated overlay is tiered by *which consumer may resolve a
  token* (mention path vs. blind scan); the tiers and the raise-on-overlap check
  are in `alias.py`, and `runtime/` mirrors them. Wrong tier = blind false tags.
- **`io_utils.py` is the choke point.** New file I/O that bypasses it silently
  breaks the cross-platform byte guarantee.

## Working rules that bite

- **Determinism is testable and tested**: reruns must be byte-identical, and
  from a *different working directory* too — paths in outputs are relative to
  the input root, never the cwd. Timestamps live only in `runs/` manifests.
- **Curation lives in Python constants**, JSON outputs are derived:
  `CURATED_ALIASES`, `CURATED_LLM_ONLY_ALIASES`, `CURATED_LEGACY`,
  `CURATED_REPLACEMENTS`, `CONTEXT_ONLY_TOKENS` (`alias.py`),
  `SILENT_STAFF_NAMES` (`stage2.py`), `STEM_META` (`pdsrg.py`),
  `ACCEPTED_HONORIFIC_NAMES`, `GREETING_NAME_TOKENS`, `ROLE_MAILBOXES`
  (`scrub.py`). Unknown stems / unattested names must **raise**, not silently
  produce untagged chunks.
- **A rename is not a replacement**: a rename is an identity mapping and may
  expand to the successor's `part_no`s; a replacement is a different formula and
  is only a currency cue. Separate alias-table sections.
- **Corpus attestation**: an alias must appear in the corpus in the form the
  corpus writes it. A 0-doc worksheet row means the alias is wrong.
- **PII**: `data/QAs/` is read-only and holds real customer mail; nothing
  unscrubbed leaves Stage 0. Redaction is conservative — what cannot be redacted
  without eating prose is *flagged*, never guessed — and beware trigger words
  that are also ordinary English (bare `best` as a closer ate `Best Plant
  Protein` before it was comma-gated). A redaction rule earns its keep on the
  regen diff, not on the fixture. A review queue of 0 is a claim to be earned,
  not a target. Tests use synthetic fixtures (`tests/conftest.py`, example.com /
  555 numbers) — never paste real corpus text into tests or docs.
- **`products.json` is the legal-approved claims corpus** — quote it, never
  paraphrase claims.
- `processed/` is committed but derived: regenerate, don't hand-edit. Numbers
  quoted in `docs/progress.md` must match a regenerated run.
- Commits: `area: description`, lowercase. New behavior gets a unit test, and
  the test count is cited in the progress entry.
