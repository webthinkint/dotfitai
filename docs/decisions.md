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
  real — two new conflict pairs, ruled split 2026-09-10 (owner task 3).
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

- **The stakeholder preview does not wait on the residual-PII queue** (owner
  ruling, 2026-09-10, open item 13). The preview audience is stakeholders and
  approved partners, and the 211 already-searchable records are acceptable for
  that audience; item 13 continues as **public-launch** work, at the same
  conservative standard, rather than as a preview gate. This is the §4 posture
  applied rather than relaxed: `data/QAs/` stays read-only, nothing unscrubbed
  leaves Stage 0, a redaction rule still earns its keep on the regen diff, and a
  queue of 0 is still a claim to be earned. What the ruling settles is *who may
  see the current state*, not what the scrub is allowed to miss. It pairs with
  the item 21 ruling and inherits its price: item 20's verdict log is how either
  side reconstructs what this audience was actually shown.

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
- **The FirstString conflict cluster is split, not merged** (owner ruling
  2026-09-08,
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
- **The two gpt-5.6-luna conflict pairs are split too** (owner ruling
  2026-09-10, open item 7, clusters `3d361242df468533` and `8e5f29ded9aad54a`
  — the churn the 2026-09-09 re-canonicalization predicted, both pairs pushed
  over the 0.88 threshold by sharper canonical questions). `3d361242` is the
  FirstString shape again and the owner read it the same way: two turns of
  one thread thirteen days apart (27 Jan creatine-with-LeanMR and timing; 9
  Feb the same question plus AminoFormula, answered with the new don't-mix-
  LeanMR-with-AminoFormula guidance) — the newer record is a delta, not a
  superset. Both its members are already superseded_currency on their own
  cues, so the ruling governs only the intra-cluster dedup. `8e5f29de` was
  the textbook supersede candidate — the same Lean Pack 90 question 27 Jan
  2023 and 17 Jul 2024, answers agreeing — but the 2023 record carries the
  fuller FAQ text (flagged in owner task 3 before ruling), and retiring it
  would drop that wording from search; split keeps both retrievable. Review
  queue down to the audit sample (2 rows, no decision owed).
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
- Shakers / SportMixer are gear and excluded — hence 53 *indexed* SKUs of 58
  (1470/1471 dotBAR flavors added 2026-09-10).
- **products.json drops are ad hoc, never scheduled** (owner ruling
  2026-09-10, open item 2 closed). A new export arrives whenever an update is
  known; the response is the drop chain, not a calendar: `products_diff`
  old-vs-new (the claims report the owner sees), `aliases` regen, `stage2`
  regen (cache — product tags re-resolve), `stage4` regen, `index` embed +
  upload + prune, `search_ping`. New flavors of an existing family join it
  via `CURATED_FAMILIES` — the strip-suffix derivation leaves them singleton
  families otherwise (the 2026-09-10 gap: 1470 Chocolate Raspberry Crisp /
  1471 Mint Fudge dotBAR). Unattested-in-QA flavor names are fine there:
  attestation governs alias *tokens*, and these tag no record the corpus
  does not already tag as dotBAR.

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
- **`infopages.json` is authority-1 website copy** (owner ruling 2026-09-11):
  the dotFIT.com info pages (about/FAQ/policies/learn hubs) share the
  products.json export channel (`coid`/`longname`/`searchcontent`/`URL`, no
  `part_no`), so they carry the same §3 weight — legal-approved site copy,
  `source_type: infopage`, quotable per `ClaimsQuotable(1)`. That makes the
  published policies answerable ("what is your return policy", "do you ship
  free over $80"), which the guardrail now splits from support-owned
  account/order actions (`out_of_scope` keeps "where is my order"). Page
  metadata is curated in `PAGE_META` (display title, page-class topic; one
  export longname is truncated mid-sentence) and an unknown `coid` raises —
  the `STEM_META` rule. No `products` tags (no part_nos; the FAQ page names
  SKUs in prose — deterministic tagging of site copy is future work on the
  podcast precedent), no date (the export carries none), `citation_url` =
  the page's public URL. Probe strata are the page `coid` (one per page,
  like one per guide/episode) — per-section strata would floor the
  allocation above the 60-probe budget.

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
- **The 250 were re-drawn before labeling** (2026-09-10, open item 8). Owner
  ruling: **re-draw**. The scorecard is permanent — it is re-run after every
  change for the life of the assistant — so it reflects the corpus and the
  tagging as they are, not as they were on 2026-09-08 before the luna regen.
  The draw is deterministic, so the decision pack's preview *is* the committed
  set (byte-identical on regeneration): 208 of 250 stay, 42 swap, 29 families
  and the 125/125 split unchanged, `(untagged)` 79 → 63 as luna's product
  tagging shows up, and the two items aimed at answers Stage 4 retired
  (G-012, G-032) are gone. Sequencing was option C — item 7's split ruling
  landed first, and it left the pool at 653 and the draw unmoved. The written
  50/20 and the 120 probes are untouched (they rebuild on their own
  lifecycle), so every metric scored on them carries over; what re-measures is
  the **sample tier** of the §12 sweep, whose last reading (recall@8 99.2%,
  n=125) was taken on the superseded draw. A retained question is retained by
  *record*, not by label: item numbers are positional and the dev/test side
  falls out of the stratum ordering, so only 41 of the 208 keep their `G-` id
  and 114 their side — old per-item notes match the new worksheet on question
  text, never on the number.
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
- **Service auth is fail-closed, and the support route is corpus-attested**
  (2026-09-10, open item 22 — closed). Four decisions, all of them about the
  boundary rather than the pipeline. (1) **Auth stops the boot**: the service has
  no authentication of its own, so with neither `DOTFIT_SERVICE_API_KEY` nor an
  explicit `DOTFIT_SERVICE_AUTH=none` it refuses to start — there must be no
  state in which it is running and open, and the two set together is an error
  rather than a precedence rule, because guessing which was meant either exposes
  the service or rejects the caller. A shared secret as `Authorization: Bearer`,
  compared in fixed time; `/healthz` stays unauthenticated and now *reports* the
  posture, so `"auth": "none"` on a deployment that meant to require a secret is
  visible instead of silent. (2) **An over-long question is a 400, never a
  truncation** — truncating changes the question, and the answer would be to
  something the customer did not ask. All validation happens before the first SSE
  frame, since that frame commits the response to 200 and leaves no status code
  to reject with. (3) **Our own request timeout takes the failure path, a client
  hang-up does not**: the two cancellations stopped being the same event, and a
  stream that merely stops is indistinguishable from a network fault and invites
  the retry the caller was told not to make. (4) **The handoff route is
  configuration with a corpus-attested default** — `support@dotfit.com or
  (877) 436-8348`, which the PDSRG's own "About dotFIT Worldwide" section
  publishes, so it is authority-2 approved copy and not a number we invented
  (the §5 attestation standard, applied to delivered copy). `=none` restores the
  old prose. **CORS and rate limiting stay absent** on purpose: one trusted
  server-side caller, no browser origin, and a rate limit on a single caller
  whose every request costs five model calls would be guessing at a deployment
  we do not have.
- **The verdict log records what was decided and never what was said**
  (2026-09-11, open item 20 — closed). The service now writes one JSON line per
  accepted request to stdout: outcome, escalation reason codes,
  `history_trigger`, withheld, post-check verdict and which checks fired, the
  claims outcome, source and citation counts, per-stage timings. The decision
  that shapes it is the **omission**: no question text and no answer text, which
  is the §4 posture applied to a new surface — the website database is the system
  of record for the conversation, and a second copy of real customer mail is a
  new PII exposure that buys nothing the join does not. So the two are split:
  they hold what was said, we hold what was decided, and `request_id` joins them.
  The rule is *enforced* rather than promised — no text field exists to put an
  answer in; `reasons` is filtered to the known escalation vocabulary because it
  comes from a model (the free-prose `notes` is not logged at all); and
  `failures`/`warnings` keep each check's name and drop its message, which for
  `claims_language` quotes the offending draft back. The log is **not
  configurable and has no off switch**, which follows from the item 21 ruling: if
  any state may ship to the preview, this is the only reconstruction of what that
  audience was shown, and a switch to turn it off is a switch to lose it. There
  is one line on every terminal path, including the abandoned one — a request
  that logged nothing is indistinguishable from a request that never happened.
  A `400`/`401` is deliberately not logged: it never reached a verdict, and the
  caller sees it synchronously as a status code. stdout rather than a file
  because a container's stdout is already collected, and a file sink would buy
  rotation, permissions and a disk-full failure mode for kilobytes a day.
- **The preview logs the words themselves; public traffic does not** (owner
  ruling, 2026-09-11). The text-free rule above is priced against *public*
  traffic, and during the stakeholder preview it was priced against debugging:
  retractions — a withheld draft, a `claims_language` failure that quotes the
  wording it rejected — cannot be reconstructed from a log that holds neither,
  and the owner ruled preview-stage PII acceptable because only stakeholders
  are on the line. The shape the ruling took, so that it costs nothing to undo:
  - **A second record, not a widened one.** `dotfit.transcript` (one line per
    request, same `finally` on the same terminal paths) carries the question,
    the history as received, the draft *including a withheld one*, the
    delivered text, the retraction reason, the guardrail's free-prose notes
    and the full failure messages — every field `VerdictLog` filters away. The
    verdict log is unchanged and keeps every guarantee it ever had; a test
    pins the separation with both sinks wired.
  - **Off by default, and loud when on.** `DOTFIT_SERVICE_DEBUG_TRANSCRIPT`
    defaults to off — the §4 posture is the state you get by forgetting this
    exists — and a boot line plus a `/healthz` field say the state a deployment
    is actually in. The preview unit enables it.
  - **Off before public customer traffic** — the ruling's scope is the
    stakeholder audience, so this is a gate on the same line as items 12/17,
    not a suggestion, and the unit file says so where it turns it on.
  Both records join on `request_id` and read off the VM with
  `dotfit-verdict-log` (`runtime/deploy/verdict-log`): the verdicts one line
  per request, the transcripts as a `--transcripts` block per request built
  for the question the preview actually raises — what was withheld, and why.
- **A greeting is not a question, and the branch that answers it retrieves
  nothing** (2026-09-11). Typing "Hi there" came back as the support handoff.
  The cause was not the prompts: the §11 chain runs whatever is typed, hybrid
  search returns its `top` nearest neighbours for a greeting as readily as for a
  question, the answer agent writes a greeting with no `[n]`, and the
  deterministic post-check fails it on `citation_presence` — which under `Gated`
  withholds the greeting and delivers the handoff. The **fix is a branch, not a
  relaxed check**: the guardrail now classifies the turn (`intent` =
  `question` / `smalltalk` / `out_of_scope`) on the call it already makes, and a
  `smalltalk` turn skips the rewrite, the aliases, the search and the answer
  agent. Retrieval never runs, so the source list is empty, so
  `citation_presence` — already conditioned on a non-empty list — has nothing to
  fire on. Nothing in the citation contract moved, and the post-check reads the
  shape off the verdict rather than guessing it from the text. Four rulings hang
  off it:
  - **Safety outranks intent, always.** `GuardrailVerdict.Conversational` holds
    the precedence in one expression so the pipeline and the post-check cannot
    disagree: escalation wins ("Hi! I'm 14, what should I take?" refuses), a
    claim trap wins (a presumed disease claim is corrected from approved copy,
    which needs retrieval), and a **degraded pre-check never branches** — the
    guardrail fails open, and failing open means falling back to the fully
    checked path, not to the one with no sources in it.
  - **`out_of_scope` is classified and logged but changes nothing.** The §12
    adversarial out-of-scope items (order status, refunds, store locations) are
    delivered today — `n_withheld: 0` on all 7 dev items — so the redirect they
    get from the retrieval path works, and rerouting it would put a passing
    behavior at risk to fix a defect it does not have. The intent is recorded so
    the owner can see the volume and rule on it with data. It also keeps
    `smalltalk` narrow: without somewhere else to put "where is my order", that
    is where the classifier would have put it.
  - **The branch is not shown conversation history**, which is the answer
    agent's rule applied for a sharper reason. The answer agent is denied it
    because an earlier turn is not a citable source; this branch has *no*
    sources and no audit, so it must not be able to carry a product fact
    forward out of an earlier assistant turn. Its prompt forbids product
    content, guidance and citations outright, and the reply degrades to a
    template when the call fails — the same discipline as the refusal and
    withheld templates.
  - **It is counted apart.** The verdict log gets `outcome: "chitchat"` and an
    `intent` column (schema 1.1.0) rather than folding these into `answered`:
    this is the one path that delivers text without retrieval, so a run of
    greetings would otherwise read as a healthy answer rate with a citation rate
    of zero, and items 12 and 17 read those columns.
- **The claims audit is shown what the pipeline told the answer to say**
  (2026-09-11, open item 17). "Tell me all about MuscleDefender" came back
  **retracted** on `claims_language`, and the offending wording was the rename
  sentence the runtime itself had ordered: §5 resolves a deprecated name, hands
  the answer agent a note — "MuscleDefender was renamed GlutamineComplex … use
  the current name and mention the rename" — and that note stopped at the answer
  prompt. The audit saw only the question, the draft and the source bodies, none
  of which state the rename, so an obedient draft read as a product claim no
  approved copy supports. **A check that cannot see the instruction it is
  grading measures obedience as invention**, so `AliasExpansion.Notes` now travel
  to the claims prompt as an *Established facts* block. Three constraints keep it
  from becoming a loophole. (1) **Only the alias notes travel** — they are
  derived from the committed §5 table, corpus-attested and deterministic, not
  model output; the claim-trap note added beside them in the answer path is
  guidance *about the question*, and telling the auditor a question was a claim
  trap would bias the one verdict it exists to reach. (2) **Verbatim, imperative
  wording included**: rewording the note for the auditor would recreate the
  divergence this closes. (3) **Attested for identity, and identity only** — the
  block never licenses a claim about what a product does, contains or how to take
  it, which stays bound to a QUOTABLE source.
- **Identity is not a product claim; a replacement still is** (2026-09-11, open
  item 17). The audit's definition of a claim — what a product *does, contains,
  or how to take it* — never covered naming, but nothing said so, so the checker
  applied it to renames anyway. It is now written down, together with the rule
  that a source's own published title is attested text like its body: a chunk
  titled "GlutamineComplex (formerly MuscleDefender)" grounds a statement about
  what the product is called, and the draft that got retracted had in fact cited
  it. The carve-out is deliberately bounded by the §5 distinction the pipeline
  already enforces on both sides: a **rename** is an identity mapping and saying
  so is compliant, while presenting a **replacement** — a different formula that
  took over the slot — as the same product is now a violation named in its own
  right, where before it fell under the general unsupported-claim clause. The
  asymmetry is the point: the fix must not buy a rename false-positive back with
  a replacement false-negative.
- **The currency behaviors are a §12 coverage gap, not a 51st adversarial item**
  (2026-09-11, open items 15/17). The retraction above was found by a person
  typing into the chat, because nothing in the golden set asks about a renamed,
  replaced or discontinued product — and §12 fixes the adversarial composition at
  20/15/15 and reports escalation accuracy and forbidden-content rate against it,
  so adding one would redefine those numbers in place (the item 19 ruling,
  applied again). It is therefore recorded on item 15 beside the two behaviors
  already parked there, and `multiturn.jsonl` is the precedent if it earns a set:
  its own artifact, its own denominator. Both fixes here are **prompt-side**, so
  their effect is a live reading and not a test — item 17's withheld 39/125 and
  faithfulness 0.60 are unre-measured until a full sweep runs.
- **A claims-only failure earns one bounded repair pass** (2026-09-11, open
  items 12/17). "How much creatine should I take?" came back **retracted** about
  one run in four. The draft was three bullets quoted verbatim from the approved
  CreatineMonohydrate copy plus one appended sentence — "loading is optional" —
  carrying a statement out of the NO7 Preworkout3 PDSRG chunk, whose body
  discusses creatine generally, onto a different product. The audit was **right**
  and its evidence line said so precisely. What was wrong was the price: the gate
  is whole-or-nothing, so three correct bullets were discarded and the customer
  got the support handoff. **A correct verdict on one sentence should not cost
  the other four.** So `PostCheckResult.RepairableClaimsOnly` now earns exactly
  one edit — excise or re-ground the wording the audit named, change nothing else
  — judged again by the same checks. Five bounds keep it from becoming an appeal,
  and they are the ruling: (1) **once**, no loop, a repaired draft that fails is
  withheld; (2) **claims only** — `citation_presence` and `escalation_respected`
  are failures of *shape*, meaning the draft answered the wrong contract, and
  asking a model to edit its way out of an escalation is asking it to argue with
  the guardrail; a mixed failure is therefore not repairable (`All`, not `Any`);
  (3) the repair is **grounded identically**, delegating to
  `BuildAnswerUserMessage`, or its `[n]` markers would mean something other than
  the ones it is editing; (4) the repaired draft is **audited again** and does not
  report on itself; (5) **both verdicts survive** — `PreRepairAnswerText` and
  `PreRepairPostCheck` on the result, `repaired` as its own verdict-log outcome
  counted apart from `answered`, and the pre-repair draft in the debug
  transcript. That last one is not bookkeeping: folded into `answered` these rows
  read `claims: compliant` — the *second* audit's verdict — and item 12's
  precision numerator deletes itself. `withheld` outranks `repaired` on the
  outcome column, because a repair that did not save the answer is a withheld
  request. Live retracts the draft it already showed before streaming the
  replacement; Gated emits no retraction, having shown nothing. Measured live:
  4/4 delivered where 3/4 had been, 2 of them by the pass, each cutting exactly
  the flagged sentence.
- **A source is about its own subject, in both prompts** (2026-09-11, open item
  17). The same retraction exposed a divergence: the audit was enforcing on the
  draft a rule the answer agent had never been given. Nothing in
  `AnswerInstructions` said that a source about one product grounds claims about
  *that* product only — so a draft could read general-sounding science out of
  another product's chunk and state it as a direction, which is the one thing the
  audit was sure to catch. The rule is now in both prompts, with the same escape
  hatch in both: attributed to the product the source is actually about, the
  sentence is fine. It is also now a **named violation** in the audit rather than
  an inference it had to reach on its own, which is what its evidence line shows
  it was doing. Prompt-side, so the effect is a live reading, not a test.
- **Site and program procedure is not a product claim** (2026-09-11, open items
  12/17). "Can you help me make a program?" was retracted on **five** sentences,
  four of which were navigation — "log in using the icon in the upper-right
  corner", "customize and save menus", "update your measurements weekly". All
  eight retrieved sources were authority-3 Q&A, because no authority-1 infopage
  covers how to build a program, so the item 24 rule ("a set with no QUOTABLE
  source is one where *every* product claim is unsupported") applied to the whole
  answer. The rule is right and stays; what was wrong is that a UI flow was being
  read as a product claim at all. **The approved-copy rule is about supplements**
  — what one does, contains, or how to take it — and there is no approved claims
  corpus for a sign-up flow, so service and site procedure is judged as ordinary
  information: grounded in a cited source, a CONTEXT ONLY one included, is
  enough. Bounded in the same clause: it stops at supplements, and at any health
  or performance outcome attributed to the program. The item 24 paragraph now
  also says what a context-only set does *not* mean — flag the sentences that are
  product claims, not the ones that merely came from a CONTEXT ONLY source. Live:
  3/3 delivered where 0/3 had been.
