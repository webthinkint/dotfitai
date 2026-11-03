# dotFIT pipeline

Turns the original corpora in `original-data/` into the committed outputs in
`pipeline-output/` and the Azure AI Search index the assistant queries
(`kb-main-v2`). Everything here is offline and deterministic except the calls
that are cached (LLM structuring, embeddings) and the upload.

| Command | Input | Output | What it does |
|---|---|---|---|
| `stage0` | `original-data/QAs/**/*.docx` | `qa/stage0/` | PII scrub: deterministic, local, no model |
| `stage1` | Stage 0 text | `qa/stage1/` | Parse and classify each document |
| `run` | | | `stage0` then `stage1` |
| `stage2` | Stage 1 + `products.json` | `qa/stage2/` | LLM structuring on scrubbed text only |
| `stage4` | Stage 2 + `products.json` | `qa/stage4/` | Deduplication and currency filter |
| `aliases` | `products.json` + Stage 1 | `aliases/` | The alias table and its curation worksheet |
| `pdsrg` | the PDSRG PDFs | `pdsrg/` | Section chunks with tables kept intact |
| `podcast` | ASR transcripts | `podcasts/` | Topic chunks with timings |
| `index` | everything above | `index/` | Shape, embed and upload the search index |

Standalone scripts in `scripts/` cover what is not a stage: ASR
(`asr_pilot.py --all`, `masterclass_transcribe.py`), the product summaries deck
(`summaries_pptx_md.py`), the product-video scripts (`video_scripts_docx_md.py`),
a new-export diff (`products_diff.py`), the Stage 2 review-queue triage (`stage2_queue_triage.py`), the Dropbox authorization (`dropbox_auth.py`), and Azure smoke checks.

## Tooling

- [uv](https://docs.astral.sh/uv/) is the only prerequisite; Python **3.12** is
  pinned in `.python-version` and `uv.lock` is committed.
- Runtime dependencies: `python-docx`, `pdfplumber`, `python-pptx` (corpora);
  `openai`, `anthropic`, `azure-search-documents` (Azure). Credentials live only
  in the gitignored root `.env`, read by `azure_config.py`.
- Tests use synthetic fixtures only; real corpus text never appears in them.

```bash
# from pipeline/
uv sync
uv run pytest
uv run qa-pipeline --help          # <command> --help for every flag
```

Path defaults are repo-root-relative, so from `pipeline/` pass `../original-data/…`
and `../pipeline-output/…`. Corpus filenames contain spaces, commas and `&` —
always quote paths.

## The stages

- **Stage 0** — PII scrub of the `.docx` Q&A files: structural redaction
  (contact blocks, `From:`/`To:`/`Cc:` display names, signatures, copyright
  footers), greeting de-naming, pattern redaction (emails, phones, postal
  addresses, SSNs, cards, social-profile URLs), per-file scrub reports. No model,
  no network. What cannot be redacted without risking prose is **flagged for
  review, never guessed at**. Nothing unscrubbed leaves this stage.
- **Stage 1** — parse and classify: split the expert answer from the quoted
  customer message (three document shapes), classify (`qa_email` /
  `expert_note` / `other`), extract metadata (year, topic subfolder,
  `thread_date` from the enquiry's `Sent:` header, question text). The filename
  gets the pattern redaction, because it reaches the caller as a source's
  `locator`; paths in `source_file` and the Stage 0 tree stay raw, because
  document ids hash them. Documents with no expert answer are excluded and
  tallied, not queued.
- **Stage 2** — LLM structuring on scrubbed text only (small chat deployment,
  strict JSON schema; a `claude*` name takes the Messages endpoint): `question_canonical`, a cleaned `answer` (transcription,
  never generation — a containment diff checks no content was invented),
  `products[]` resolved to part numbers through the alias table, `topics[]`,
  `audience_flags`, `currency_cues`, `residual_pii_flag`, `confidence`. The model
  lists the people named in the filename and only those spans become `[NAME]`, so
  the rest of it still serves as the `locator` and a question-less note's title.
  `answer_coverage` and `answer_in_expert` hold the answer to the expert text.
  Responses cache in the gitignored `runs/stage2_cache.jsonl`; `--no-llm` runs
  the rule-based fallback with no Azure calls.
- **Stage 4** — deduplication and currency. Near-duplicate questions cluster at
  cosine ≥ 0.88 (a scan-locked threshold) within shared product/topic buckets;
  the newest current member wins. Non-nested part-number sets mark a conflict:
  the cluster is queued, never auto-picked, and curated dispositions pin their
  exact membership (a reshaped cluster raises). A duplicate whose answer the
  newest repeats less than half of stays current and queues the cluster
  (`cluster_uncovered`). **Renames never supersede.**
  Replacement and discontinued cues supersede only when the answer depends on
  the formulation (a cached LLM judgment; no usable judgment means superseded).
  Outputs `documents.jsonl` with `is_current`, `clusters.jsonl` (the review
  worksheet) and `review_queue.jsonl`.
- **PDSRG** — the Practitioner Dietary Supplement Reference Guide PDFs: hybrid
  table extraction (wide dosage grids re-extracted before the prose filter),
  font-based heading detection, section chunks with heading-path prefixes,
  tables atomic. Bibliographies are excluded (`--keep-references` keeps them).
  An unknown PDF stem raises.
- **Podcast** — ASR transcripts into topic chunks with speaker turns and
  timings. Attested ASR mis-hearings of the show's names are corrected in the
  chunks (the raw transcripts stay verbatim). Citation URLs deep-link to the
  segment's start second; an episode missing from `PODCAST_VIDEO_IDS` raises.
- **Index** — shapes every source into index documents, embeds
  `title + content` (`text-embedding-3-large`, 3072-dim) and uploads. Product
  copy is the product-video script pages, one document per section, for every
  family they cover (a family covered in part raises); the families they do
  not cover keep `products.json` copy, section-split per family. Info pages
  reuse the same splitter (an
  unknown page raises); menus are one description document per menu type; QA
  records over the embedding cap split at paragraph boundaries, never truncated.
  Superseded documents are pruned from the service, not just skipped.
  `--no-embed` shapes only, `--no-upload` skips AI Search, `--reset` drops and
  recreates the index (waiting out the asynchronous delete).

## Rules the pipeline keeps

- `original-data/QAs/` holds real customer mail; it is gitignored, nothing
  unscrubbed leaves Stage 0, and the weekly fetch is the only thing that writes
  to it.
- **Aliases tag and expand; they never rewrite corpus text.** A rename is one
  product under two names and expands to the successor's part numbers; a
  replacement is a different formula and is a currency cue only. Every alias is
  attested in the corpus in the form the corpus writes it, and a token sits in
  exactly one tier: safe for a blind scan, LLM-mention only, or context-only.
- **Every index document stamps `is_current`**: AI Search does not match null
  against a filter, so an unstamped document is invisible to every query.
  `product_status` is a separate axis — a discontinued product's documents stay
  current so "what happened to X" is answerable.
- Index keys use dashes (AI Search forbids colons); a test pins the rule.
- Vectors are API results: they cache in gitignored `runs/embeddings.jsonl`, and
  the committed `documents.jsonl` carries none.
- **Reruns are byte-identical**, from any working directory and on Windows or
  Linux: explicit UTF-8 everywhere, `\n` line endings on every write, POSIX
  document ids, sorted iteration, no timestamps in per-file outputs. Run
  manifests in `runs/` carry timestamps and input hashes and are never compared.
- `pipeline-output/` is derived: regenerate it, never hand-edit it.

## The weekly Q&A refresh

`qa-weekly` (`src/qa_pipeline/weekly.py`) keeps the Q&A corpus current from the
Dropbox folders in `QA_FOLDERS`. It runs in its own clone, set up by
`deploy/install-weekly.sh`, whose `.env`, `original-data/QAs` and run caches are
links back to the main checkout. HEAD there is what was last published, so what a
run would publish is always the working tree against HEAD.

- **Monday 07:00**: fetch, run Stages 0, 1, 2 and 4, write
  `pipeline-output/qa/runs/weekly-report-<date>.md` and mail its counts to
  `EMAIL_TO`. That person reads the flagged records, and what is not ok is fixed in
  the pipeline and pushed to master before Thursday. A PII flag whose text is
  provably not served leaves the reading list and is only counted: the text is
  still in the Stage 0 and Stage 1 files, which publishing commits.
- **Thursday 12:00**: take master, rerun the stages from cache with no fetch,
  index, commit and push. A model error stops the run, since Stage 4 treats a
  failed judgment as superseded. So do more than 20 answers leaving search
  (`--allow-retires` overrides it). Failures are mailed.
- **To skip a week**, `touch` `pipeline-output/qa/runs/skip-thursday`; the run
  removes it. Next Monday's report includes everything still waiting.

The fetch never deletes, because Stage 0 and the index prune what has no input. An
updated file replaces the old version at its path. Content already held at another
path is skipped. Mail is plain SMTP, so it carries counts and a path and no record
text; the report stays on the machine. Nothing holds a flagged record back. A new
year's folder is reported until it joins `QA_FOLDERS`, and a fix through a Stage 2
prompt change misses the cache for the whole corpus, so it is a separate run.

## When `products.json` changes

A new export arrives whenever an update is known. Run, in order: `products_diff.py`
(old against new — the claims report for the owner), `aliases`, `stage2`
(cached; product tags re-resolve), `stage4`, `scripts/video_scripts_docx_md.py`
(the facts and labels come from the export), `index` (embed, upload, prune), and
`scripts/search_ping.py`. A new flavor of an existing family joins it in
`CURATED_FAMILIES` in `alias.py`.

## The product summaries deck

`scripts/summaries_pptx_md.py` reads the deck (gitignored `.pptx`) text-exact
through a hand-curated slide→section map and writes
`pipeline-output/summaries/md/`. Every price is masked to `[price]`; the run
fails if a slide is in no section and not listed as dropped, or if a product tag
is not an alias-table family. Trainer scripts, taglines and flyer copy are held
back. The sections are not indexed; the assistant's program guide
(`assistant/references/program-guide.md`) is written from them by hand.

## The product-video scripts

`scripts/video_scripts_docx_md.py` reads the 2026 website product-video
scripts (`original-data/Product Video Scripts/`) into one file per product
in `pipeline-output/video-scripts/md/`: an overview and five fixed sections
(what it is, what it does, who it is for, how to use it, what makes it
different), then from `products.json` the product's Supplement or Nutrition
Facts panels and the rest of its label as printed (directions, ingredients,
allergens, warnings, storage), one per distinct flavor. The same pages go to
`products.jsonl`, which `index` reads as the product copy. The label's
directions can differ from the script's "How do you use it?"; the prompt
gives the script's and names the label's difference. The document's heading styles are
unreliable, so the split is a hand-curated product list and the section
labels the document uses, inline or standalone; the run fails on a missing
title, a paragraph outside a section, a missing section not listed as
missing, a product tag that is not an alias-table family, or a part with no
facts table. The wording changes only through the script's `CORRECTIONS`
(approved typo fixes, each matching exactly once) and the protein bars'
protein minimum, filled from the bars' facts. Prices are masked to
`[price]`.

## Review queues

`review_queue.jsonl` reasons:

- `parse_error` — not a valid `.docx`; needs a data-owner look.
- `honorific_plus_name` — e.g. "Dr. Smith" in content; a person confirms
  whether it is staff or a public figure (cleared into
  `ACCEPTED_HONORIFIC_NAMES`) or a customer.
- `greeting_name_residual` — a greeting whose name has no terminator and is not
  in the corpus-attested `GREETING_NAME_TOKENS`; redacting it automatically
  would corrupt prose.
- `no_quotable_structure` — `doc_type: other`; confirmed in Stage 2.
- `filename_redacted` — the filename held an email, phone or similar; the served
  `locator` carries the placeholder, and a person confirms nothing else leaked.
- Stage 2 adds low confidence, residual PII, containment failures, low coverage
  (`low_coverage`: expert text left out), `answer_outside_expert` (text in the
  answer from outside it), `filename_name_unmatched` (a listed name not found in
  the filename), LLM errors and a deterministic 5% audit sample. The queue does not gate: a flagged record
  is already redacted. `scripts/stage2_queue_triage.py` groups it for sign-off.
