# Phase 1 — Progress Archive

Rotated-out log entries, newest first. `docs/progress.md` keeps the five most
recent; everything older lands here so the live status file stays cheap to read
end to end. Entries are verbatim — numbering and dates are continuous with
`progress.md`.

- **2026-09-08 (44)** — Model-facing source labels reworded (§11). `SourceLabel`
  is what the answer agent echoes when it attributes an answer in prose, and it
  was echoing the corpus name: "dotFIT's customer Q&As typically recommend ...",
  which tells a customer how the corpus was assembled, not where the guidance
  comes from. `qa` → **dotFIT nutrition knowledge base**, `podcast` → **expert
  discussion transcript**; the other three unchanged. `SourceKind` (the
  customer-facing citation line) stays literal, and quotability still rides on
  `ClaimsMarker`, so no source moved across the §3 claims line. 1 new test.

- **2026-09-08 (43)** — Stage 2 triage round 2 (§4): **item 16 folds into 13**,
  round 1's grouping retired as unsound — it read "a placeholder exists
  somewhere in this record" as "the redaction landed", parking 5 live customer
  names in the bulk pile. Grouping is **evidence-span survival** now (joins the
  gitignored cache, still prints no span). Four `scrub.py` gaps closed on the
  regen diff — **38 names / 36 files / 0 prose changes**, unresolved 6 → 3, of
  which Zane ruled **staff** (lands on the next `PROMPT_VERSION` bump). Rerun
  byte-identical; index re-uploaded, 3,996 docs, 0 errors. **420 python**.

- **2026-09-08 (42)** — Open item 10 **closed — no ticket needed**: the orphaned
  `kb-main` finished deleting. `GET /indexes/kb-main` now returns the clean-miss
  404, not the wedged "is being deleted" body, and servicestats counts only the
  live index (1 index, 3,996 docs, ~110 MB — the orphan's 7,992 docs / 213 MB
  no longer counted; verified via statistics, not the portal, which hides
  deleting-state indexes). The name is free but `kb-main-v2` stays the default
  on both sides — a rename is cosmetic. Docs and status comments updated.
- **2026-09-08 (41)** — Tracker convention change + one new open item (docs
  only). Log entries no longer cite commit SHAs, here and in
  `progress-archive.md`, and the **Writing entries** rule dropped the
  requirement; entries touched by the removal were re-wrapped to the documented
  80 columns and the 8-line budget. New **item 16**: 10 Stage 4 records keep a
  self-introduced customer name in `question_original` (9 full names, only 2
  flagged) — committed `processed/` only, 0 hits in the indexed field. 404
  tests, unchanged.
- **2026-09-08 (40)** — Stage 4 review queue closed (§4): **item 7
  dispositioned, queue 2 → 0**. The corpus's one `cluster_conflict` is ruled
  **split** — the pair is two turns of one email thread, and Stage 1 keeps only
  the new expert reply, so the newer record is a delta, not a superset
  (reasoning in `docs/decisions.md`). Both members stay `is_current`. Rulings
  live in `CURATED_CLUSTER_DISPOSITIONS`, membership-attested. Stage 4
  regenerated, rerun byte-identical, 0 API calls; index untouched. 8 new tests
  (**404 python**).

- **2026-09-08 (39)** — Owner worksheets for two stalled items.
  `stage2_queue_triage.py` groups item 13's 222 rows into one that must be read
  (**3** residual-PII flags with no placeholder in the committed text) and four
  that bulk-disposition (165 / 48 / 5 / 1), and reports the number that matters:
  **203 of 222 flagged records are `is_current` and live in the index** (154 of
  them residual-PII — the figure already tracked). `products_diff.py` diffs two
  `products.json` exports by §5 section, so item 2's monthly pass sees changed
  *claims*, not changed bytes. Naming that owner is still item 2. 396 tests.

- **2026-09-08 (38)** — SSE service shipped (§11) — the piece the plan called
  next. `DotFit.Agents.Service`: `POST /ask` streams
  `disclosure`/`stage`/`delta`/`retraction`/`result`, `GET /healthz`, config
  validated at **startup** so a bad `.env` fails the boot. Always `Gated`, no
  client choice; payloads narrower than the CLI's — no source `content`, no
  withheld draft, no failure reasons. Both paths smoked live, which caught
  camelCase request binding against snake_case responses: `conversation_id`
  never bound, so every turn re-announced. 14 new tests (**111 runtime**).
- **2026-09-08 (37)** — Podcast citation URLs — **item 14 closed**. All 47
  `archive.txt` ids resolved through YouTube oEmbed and matched all 47 episodes
  at **Dice 1.00**: the mapping was never ambiguous, only unverified (the
  downloader wrote the titles out verbatim). Frozen as `PODCAST_VIDEO_IDS`;
  `scripts/podcast_archive_verify.py` is the session record. All **1,800**
  segments deep-link to their start second; re-uploaded 3,996/3,996, **0 embed
  calls**, 0 errors, live-verified. Correction: `wc -l` says 46 — the file has
  no trailing newline. 5 new tests.
- **2026-09-08 (36)** — §12 eval harness shipped — `evaluate.py` + `qa-pipeline
  eval` over `ask --json`. **It does not wait on item 8**: each item's source
  doc id is `qa-<id>` (250/250), so source recall is label-free. Scores
  source/probe recall, citation rate, escalation accuracy, claims-audit
  precision (item 12) and RAGAS-*style* judged metrics; label-dependent ones
  report `null` with a reason. First live sweep (dev, n=12): sample recall@8
  **100% ranker-off vs 83.3% ranker-on**, probes 100%, escalation **10/10**. 35
  new tests (**391 python**). Item 5 has data now.
- **2026-09-08 (35)** — Golden-set inputs completed (§12). The **50 adversarial
  items are written** (`CURATED_ADVERSARIAL`, 20/15/15, one behavior per item)
  and derive `adversarial.md` plus a new `adversarial.jsonl`; counts, uniqueness
  and category drift now raise. Added **120 retrieval probes** over PDSRG +
  podcast (39 + 47 strata, ≥1 each) — the corpora that are 72% of the index and
  had no golden item (item 15). The 250 sample, worksheet and summary
  regenerated byte-identical. Still owner work: labeling the 250 (item 8). 18
  new tests (**356 python**).
- **2026-09-08 (34)** — Harness foundation. `ask --json` is now the §12 eval
  contract: one JSON object on stdout, the banner and `--trace` to stderr, both
  answer texts (the withheld draft included, for scoring) and sources with
  `content`. Index defaults flipped to `kb-main-v2` in both `index_build` and
  `RuntimeOptions`, with a test pinning each side of the mirror — item 10's code
  half is done, the support ticket is not. Found and fixed a real defect via the
  degraded-guardrail case (item 15): a model-written refusal after a fail-open
  pre-check was failing `citation_presence`, which under `Gated` replaces a
  correct refusal with the handoff. 12 new tests (**99 runtime**).
- **2026-09-07 (33)** — Documentation review pass (docs only): the plan's §14 was
  missing definitions for items 7–11 and carried a stale close date on 3; §4
  Stage 5's "no chunking" and §3's corpus arithmetic (1,103 − 51 − 1 = 1,051)
  were both wrong against the artifacts. Three gaps between shipped behavior and
  the trackers became **open items 13–15**: the Stage 2 queue (222), null podcast
  `citation_url` (1,800 segments), and §12's coverage (no precision metric, no
  PDSRG/podcast stratum, no degraded-guardrail case). `AGENTS.md` gained
  `podcast.py`/`cli.py` rows, `pipeline/README.md` the `kb-main` warning. 338 tests.
- **2026-09-07 (32)** — `progress.md` split three ways so the per-session read
  stops growing: this file keeps the status table, open items and the newest
  five log entries; owner rulings moved verbatim to `docs/decisions.md`, rotated
  entries to `docs/progress-archive.md`. Table rows lost their narrative —
  mechanism to the docstrings that already own it, history to the log. **36 KB →
  10 KB** read every session, nothing deleted. Cross-refs repointed in
  `AGENTS.md`, the plan, `pipeline/README.md`, `alias.py`, `stage4.py`. Docs
  only. 338 tests.
- **2026-09-07 (31)** — Answer delivery gated on the post-check (§11). No
  partial gate is possible — citation markers are only known at the last delta
  and the claims audit needs the whole answer — so the mode is explicit:
  `AskOptions.StreamMode` is `Gated` (hold deltas, on FAIL deliver the templated
  handoff and never the text — SSE default) or `Live` (stream, retract after the
  fact — CLI default). The failing draft stays in `AnswerText` for tracing. This
  turns audit false positives into refusals, hence open item 12. 4 new tests
  (**87 runtime**).
- **2026-09-07 (30)** — Runtime review pass (§11): eight findings fixed. The
  load-bearing one: the authority re-rank ordered on the fused retrieval score
  even when the semantic ranker ran, so `--semantic` paid for the ranker and
  discarded its ordering — open item 5 was not measurable as posed. Also
  `RuntimeNeeds` now mirrors `azure_config.py`'s `require=` subsets, and
  grouped/ranged citation markers parse. 16 new tests (**83 runtime**, 338
  python unchanged). Live smoke on `kb-main-v2` confirms both; `ask` PASS.
- **2026-09-07 (29)** — Retrieval unblocked (§9/§11) + claim-sourcing fix.
  `kb-main` was a single wedged index — no document op returned bytes, while the
  control plane and a throwaway index were fine — and `--reset` could not
  recover it, so it was rebuilt as `kb-main-v2`: **3,996/3,996 uploaded, 0
  errors, 0 embed calls** (open item 10 tracks the orphan). The first live `ask`
  caught the answer agent lifting claim wording from an authority-3 Q&A (open
  item 11). 4 new python tests (**338 python**), 3 new runtime (**67 runtime**),
  all on `gpt-5-mini`.
- **2026-09-07 (28)** — Runtime v0 shipped (§11, §13 Track B): new `runtime/`
  .NET 10 solution — `DotFit.Agents` library + `dotfit-agent` CLI, the §11
  pipeline component-for-component, no tool calls. Components and config
  contract are in `runtime/README.md`. Live smoke reached `guardrail`/`rewrite`
  on gpt-5-mini; end-to-end `ask`/`search` was blocked by what read as a
  service-side failure on `dotfitsearch` (open item 9, since root-caused to the
  wedged index in entry 29). **64 runtime tests** (scripted `IChatClient` fakes,
  no Azure).
- **2026-09-07 (27)** — Golden-set sampling shipped (§12): new `golden`
  subcommand + module; the sampling rules and their rationale are in
  `golden.py`'s docstring. Full run: 650-pair pool → **250 items** across 29
  families (recent share 43% vs 27% of pool), splits 125/125 + 25/25 adversarial
  per §12. Outputs `sample.jsonl`, `worksheet.md`, `adversarial.md`,
  `summary.json`. Labeling is open item 8. 23 new tests. **334 tests**.
- **2026-09-07 (26)** — QA Stage 4 shipped (§4): new `stage4` subcommand +
  module — clustering, canonical pick, conflict queue and the currency pass, all
  specified in `stage4.py`. Full run: 1,041 records → **919 current / 106
  superseded_currency / 16 superseded_dup**, 17 clusters (1 conflict), 114
  judgments, queue 2; byte-identical rerun with 0 API calls. `kb-main` rebuilt
  4,118 → **3,996** (−122 superseded QA docs), and the index now prunes to
  mirror the build. 45 new tests. **311 tests**.
- **2026-09-07 (25)** — Stage 4 calibration scan
  (`scripts/stage4_cluster_scan.py`, one-off): 766 questions embedded (760
  unique; vectors cached for the full run), 89,451 bucketed pairs, pure-Python
  `math.sumprod` cosine (BLAS dot products are not bit-stable across platforms
  — byte-identical reruns forbid numpy here); threshold evidence: 0.88 merges
  only true dups, <0.86 fuses distinct questions (0.8436 "replace" vs "combine"
  Alln1). No tests (analysis tool). 266 tests.
- **2026-09-07 (24)** — Alias-1.3.0 regen (§5→§4/§9): Stage 2 rerun, 2 live
  calls + 1,039 cache hits, 0 errors — **676** docs with products (+42 newly
  tagged, 338 gained part_nos), unresolved mentions 3,474 → **2,292** (−34%), 50
  distinct part_nos, queue 222 unchanged. Index re-upload 4,118/4,118 to
  `kb-main`, 0 errors, diff confined to `products` on 338 QA docs. Live smoke
  PASS (206 docs under the `1009` filter — `Over50` flows end to end). Safety
  re-verified on the new artifacts. Regen only, no new tests. 266 tests.
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
  commit; history purge pending owner decision — remote exists).
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
