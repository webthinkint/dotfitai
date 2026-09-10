# Phase 1 — Decisions

Owner/curation decisions and the reasoning that is not obvious from the code.
Read the group for the area you are touching; `docs/progress.md` is the status
view and does not repeat these. Everything else (rules, index contract, stage
design) is in `phase1-knowledge-assistant.md`, and the hard rules those
decisions produced are in `AGENTS.md`.

Add a decision here only if it constrains future work and isn't already in the
plan or `AGENTS.md` — and if it belongs in the plan, put it there and cite it
here. Bug post-mortems don't belong in this file; the fix is in the code and
the test.

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
  `data/` is untracked. The arithmetic that reconciles to 1,051: 1,103 − 51
  duplicates − 1 zero-byte caught during dedup (the other 8 went in the
  separate zero-byte pass, before the count).
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
- Stage 2 triage round 2 (2026-09-08): the round-1 grouping was **unsound and
  is replaced**. It called a record "redaction landed" when *any* placeholder
  appeared anywhere in it, which does not answer whether *this* flag's span
  survived — 5 records carrying a live customer name sat in the bulk-sign-off
  pile on the strength of an unrelated `[EMAIL]` in their header. The script
  now groups on **evidence-span survival**, joining the gitignored Stage 2
  cache on answer text; the span still never appears in any committed
  artifact, only the group it produced. Three `scrub.py` gaps closed on the
  regen diff (27 names across 25 files, no prose damage): the `wrote:`
  attribution name is matched case-insensitively (a lowercase display name
  escaped it whole; the `<addr> wrote:` anchor carries the rule); the inline
  closer alternation learned the corpus's misspellings (`Thnak you,` — a
  customer mistyping their own sign-off is the norm, not the exception); and
  a new rule reads a name dangling at the end of a `Question:`/`Message:`
  web-form field, the shape with **no closer to key on at all**, gated by
  terminal punctuation + a two-token cap + closer/digit rejection. Unresolved
  spans 6 → 2.
- **Zane is staff** (owner ruling 2026-09-08): the SuppBeast co-host, named by
  customers writing in about the show, a public figure across 427 podcast
  mentions, and sharing Neal's already-listed surname. Added to
  `SILENT_STAFF_NAMES` and to `GREETING_NAME_TOKENS` so the two staff
  vocabularies do not drift. **The greeting side is zero-diff on this corpus**
  (his 7 attestations are mid-prose or bare-lead, never after a Hi/Hey/Dear),
  and the Stage 2 side **does not take effect until `PROMPT_VERSION` is
  bumped** — `cache_key` keys on the version, not the prompt text, so the
  constant is a recorded ruling that lands on the next full re-canonicalization
  rather than a change to the committed artifacts. Deliberately not forced: a
  bump is 1,041 LLM calls and would re-canonicalize every question, churning
  the Stage 4 clusters and the golden set's current pairs to clear one audit
  row that does not gate. **Landed 2026-09-09**: the gpt-5.6-luna switch forced
  a full re-canonicalization anyway, so the bump to 1.2.0 rode along and this
  ruling is now in the committed artifacts. The predicted cluster churn was
  real — two new conflict pairs, ruled separately (owner task 3).
- Residual PII after round 2 — one remaining **open owner call** plus the Zane
  ruling above: two records name a third party inside a customer's own
  sentence (one `is_current`, one not), which no deterministic rule can reach
  without eating prose.
- Committed-tree residue (2026-09-08, **open**): 121 flagged records are clean
  in the Stage 2/4 record but still carry the flagged span in the committed
  `processed/qa/stage0/text`. Not served — `index_build` ships
  `question_canonical`, and Stage 0 text is not an index input — so this is a
  "what may live in git" ruling, not an exposure in the assistant. It needs
  one decision covering all 121, not 121 reads.

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
- **The one conflict cluster is split, not merged** (owner ruling 2026-09-08,
  open item 7, cluster `79c663016afc2345`): the two records are consecutive
  turns of *one* email thread — `8be45e86` is the webform enquiry (why
  FirstString, 1 g protein per lb LBM, whether the pre-workout serving is
  mandatory) and `79c66301` is the same customer's follow-up, answered with
  creatine + the Level 1 plan and quoting the whole prior reply beneath it.
  The .docx is a superset; the *record* is not, because Stage 1 keeps only the
  new expert reply as the answer. Superseding the older record would therefore
  drop the only direct answer to the pre-workout half ("if for some reason you
  can't take the pre-workout shake…") while leaving that clause standing in the
  surviving record's canonical question — a record that promises guidance it no
  longer contains, which is worse than a retrievable duplicate. The non-nested
  part_nos were an artifact of the two boilerplate blocks (the older enumerates
  MVs by demographic, hence 1007 Women's MV; the newer names ActiveMV); the
  answers never contradict each other. Both stay `is_current`.
- **Dispositions are curated constants, attested against the corpus**:
  `CURATED_CLUSTER_DISPOSITIONS` (`stage4.py`) keys the ruling by `cluster_id`
  and pins the exact membership it was made on. If the cluster reshapes or
  stops forming, a whole-corpus run **raises** rather than re-applying a ruling
  nobody made for it — the alias-table attestation rule. `--include` /
  `--limit` runs skip the staleness check, where a missing cluster is expected.
  `clusters.jsonl` keeps `conflict: true` alongside `disposition: "split"`: the
  detection is a fact and stays in the audit trail, the ruling only changes the
  routing.
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

**Evaluation (§12)**

- **The harness does not wait on labeling** (2026-09-08). Every sampled golden
  item knows the Stage 4 record it was drawn from, and that record's §9 id is
  `qa-<id>` — so source recall is measurable with no human label, and with it
  the semantic-ranker question (open item 5). Only points-to-hit and
  expected-source agreement need item 8; those report `null` with a reason
  rather than being omitted, so the gap stays visible in every run.
- **RAGAS-style, not RAGAS**: faithfulness decomposes the answer into
  statements and marks each supported/unsupported against the retrieved
  context; relevancy is a direct judgment (RAGAS generates reverse questions
  and compares embeddings — this does not); context precision is per-passage
  relevance. Named honestly in `evaluate.py` so the numbers are not read as
  someone else's benchmark.
- **Claims-audit precision is reported with its counts** (open item 12): of the
  drafts the audit flagged, the fraction the judge agrees on. The denominator
  is small by construction, and "1.00 (2/2)" must not read like "1.00 (40/40)".
  A *degraded* audit is not a flag — treating "could not run" as "flagged"
  would put every API blip in the denominator.
- **The adversarial 50 are one behavior per item.** A question that is both an
  escalation and a claim trap cannot be scored against a single category
  rubric, so borderline framings were written to land on one side, and
  escalation wins wherever both apply — that is the zero-tolerance metric.
  §12 fixes the split at 20/15/15, so two §11 standing behaviors have no item:
  conversation-start AI disclosure and prompt-injection resistance. Tracked as
  coverage (open item 15), not folded into a rubric they do not fit.
- **The `forbidden` rubric names behavior, not words** (2026-09-10, open item
  23 — closed). A clause phrased as a noun ("treat / cure / prevent language")
  is a keyword list, and a refusal has to name the claim it refuses, so the
  judge scored "no dotFIT product is approved or described as preventing
  cancer" as a cancer-prevention claim — 3 of 5 flags on the 2026-09-10 sweep.
  Every clause is now a verb the response would have to perform, and the judge
  returns `forbidden_mode` (`absent` / `mentioned` / `asserted`) from which
  `forbidden_present` is *derived*, so use/mention is a reading the judge
  states rather than one the metric assumes. The rule lives in the judge
  prompt once, not restated per item.
- **The written sets rebuild without a re-draw** (2026-09-10). The drawn 250
  and the hand-written 50/20 share one command but not one lifecycle: a
  re-draw moves open item 8's labeling target and is the owner's call, while
  the written sets are curation in code. `golden --written-only` rebuilds the
  written artifacts and leaves `sample.jsonl`, `worksheet.md` and the probes
  exactly as they were.
- **Retrieval probes measure retrieval, not answers** (open item 15): a probe
  queries a PDSRG/podcast chunk with its own body text — heading path and
  `Speaker N:` labels stripped, or the title field answers it and it measures
  nothing — and checks the chunk comes back. A recall floor for the 72% of the
  index that has no golden question. Closing that properly needs written
  questions.

**Podcast (§7)**

- **The `archive.txt` mapping was never ambiguous, only unverified**
  (2026-09-08, closing open item 14). Resolving all 47 ids through YouTube's
  oEmbed endpoint matched all 47 episodes at Dice 1.00: the downloader wrote
  the real titles out verbatim and only substituted characters Windows forbids
  (`｜ ？ ：`). The result is frozen as `podcast.PODCAST_VIDEO_IDS` — the
  pipeline must stay offline and deterministic, so it reads a constant, and
  `scripts/podcast_archive_verify.py` is the session record. An episode absent
  from the table **raises**: a silently linkless podcast document is exactly
  the failure the item described. (`wc -l archive.txt` says 46 — the file has
  no trailing newline. It is 47 lines of content.)
- `citation_url` deep-links to the segment's start second, because §7.4's "as
  covered at 14:32 in *Creatine FAQs*" is only useful if the link lands there.

**Runtime (§11)**

- **A model-written refusal is scored as a refusal** (2026-09-08). The
  guardrail fails open, so on a degraded pre-check the answer agent's own
  hard-escalation instruction is what catches an escalation — and the
  post-check's non-escalation branch was failing that correct refusal on
  `citation_presence`, because a refusal cites nothing. An answer that cites
  nothing *and* hands off now takes the escalation branch and raises a warning.
  The conjunction is what keeps it narrow: an ungrounded answer that does not
  refuse still fails, and a cited answer that merely mentions a healthcare
  professional is untouched.
- **The SSE service is `Gated` with no client choice**, and its payloads are
  narrower than the CLI's: no retrieved source `content`, no withheld draft, no
  post-check failure reasons (`claims_language: <wording>` tells a prober
  exactly which phrasing tripped the audit). Operator diagnostics stay on the
  operator surface.
- **Conversation history reaches the rewrite stage and nothing else**
  (2026-09-10, open item 18). The caller's database is the system of record and
  the service stores nothing, so history arrives with each request. It is shown
  to the query rewrite, which collapses a follow-up back into one standalone
  question; everything downstream sees no conversational state. It is
  **never** shown to the answer agent — an answer grounded in anything but the
  retrieved sources cannot honour the `[n]` citation contract, and an earlier
  assistant turn is not a source, and the boundary is pinned by a test rather
  than left to be rediscovered. The accepted-history bound is ours, not the
  caller's — newest 8 turns, 1,000 characters each, a trailing echo of the
  current question dropped — because an unbounded caller input feeding a
  small-model prompt is a cost the caller does not pay.
- **The guardrail judges the conversation, the answer agent still judges
  nothing but the sources** (2026-09-10, open item 19 — closed). A
  hard-escalation trigger is a fact about the customer, not a property of the
  sentence that carried it: a customer says "I'm 14" once and then asks "how
  much creatine?", and a check that reads only the current turn answers the
  minor. So history reaches the guardrail as well as the rewrite — and stops
  there. The **countervailing** rule is written into the same prompt and is
  half the decision: history is context for the question being asked, not a
  second question to answer, so one trigger does not put every later turn
  behind a refusal, a trigger belonging to a third party is not the customer's,
  and the assistant's own "not medical advice" line is not evidence about
  anyone. `history_trigger` on the verdict records which reading a refusal
  rests on — the customer sees the same handoff either way, and item 20's log
  gets the distinction.
- **The multi-turn items are a set beside §12's 50, not more of them**
  (2026-09-10, open item 19). §12 fixes the adversarial composition at 20/15/15
  and reports escalation accuracy and forbidden-content rate against it;
  growing the 50 would redefine both numbers in place. So the multi-turn
  conversations live in their own artifact with their own metric, the way the
  retrieval probes do (open item 15). They are scored **deterministically off
  the runtime's own guardrail flags** — no judge, and so no `forbidden` rubric
  to read at all — and missed
  triggers and over-escalations are reported separately and never summed: they
  are opposite defects, and a prompt that trades one for the other has fixed
  nothing.
- **The stakeholder preview is never blocked on a metric** (owner ruling,
  2026-09-10, open item 21 — closed). The website server calls the service and
  relays the SSE stream to a stakeholder/partner audience, and that release may
  ship at any state so the project can be tested continuously. Items 12 and 17
  therefore gate **public customer traffic only**; the "before the SSE service
  faces customers" line stands, and the preview audience is not customers.
  Two consequences, and they are the price of the ruling: the
  claim-wording caveat is a **standing** condition on every preview release
  rather than a one-off note (it lives in `docs/website-integration.md`), and
  per-request verdict logging (item 20) stops being nice-to-have — if any state
  may ship, the record of what the preview audience was actually shown is the
  only way to reconstruct a complaint.
