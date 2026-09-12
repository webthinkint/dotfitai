# dotFIT Agentic Assistant — Design

The second runtime. One frontier model, given the corpus as tools, deciding for
itself what to look up and what to say.

`§N` refs in this document are cited from code docstrings, commit messages and
progress entries. Keep citing them.

The previous runtime (`docs/v1/phase1-knowledge-assistant.md`, §11 there) is not
deleted and not deprecated — it stays buildable and runnable as the comparison
baseline. Its docs live under `docs/v1/` and govern nothing on this branch. When
this document and that one disagree, this one wins here.

---

## 1. Why a second runtime

The v1 chain is a fixed pipeline of model calls: guardrail pre-check → query
rewrite → one hybrid search → grounded synthesis → post-check → sometimes a
repair pass. Every question pays for all of it, in order, before a word reaches
the customer. It works, it is measured, and the owners do not like using it.

Three findings drove this branch (owner review, 2026-09-12):

- **It is slow.** Four to six sequential model calls, the answer gated behind
  the last of them. Nothing streams until the post-check clears.
- **It refuses too much.** The gate withholds whole answers on a claims-language
  audit whose measured recall is 0 of 3 known violations while it withholds 39
  of 125 dev answers (v1 open items 12 and 17). The customer gets a support
  handoff instead of an answer that was, on inspection, usually fine.
- **It does not feel conversational.** A fixed chain answers every turn the same
  way. Saying hello needed its own special-cased branch (v1 item 25) because the
  pipeline had no other way to not run a search.

All three are properties of the *architecture*, not of the prompts. A single
capable model that can search when it wants to, search again when the first
result is thin, and answer directly when nothing needs looking up, removes the
cause rather than tuning the symptom.

**What is not being rebuilt.** The corpora, the pipeline that produces them, the
index, and the alias vocabulary are good work, independently validated, and are
reused unchanged (§4). This branch replaces the *runtime*, and nothing upstream
of it.

## 2. Decisions

Owner decisions of 2026-09-12, which the rest of this document implements.

| # | Decision | Rationale |
|---|---|---|
| D1 | New .NET project **beside** v1, not in place of it | Reuses the search client, alias table, config loader and SSE transport; keeps v1 runnable for A/B |
| D2 | **Same model configuration as v1** — Azure Foundry deployments, swappable by config | Model choice must stay a config change, not a code change; same region/auth/data terms as the index |
| D3 | Safety and claims posture is **prompt- and tool-enforced only**. Nothing gates, nothing is withheld | The gate's cost is refusals; its measured benefit is 0/3. See §8 for what replaces it and §13 for the risk that remains |
| D4 | Three tools: **`search`**, **`fetch`**, **`get_product`** | Small orthogonal surface, each backed by something that already exists |
| D5 | Hard-escalation cases are handled **in-prompt and conversationally**, not by a blocking classifier | An answer that says "since you mentioned you're 14…" and routes to a human beats a templated refusal, and costs no latency |
| D6 | **Same `POST /ask` SSE contract** as v1, history sent by the caller, no server-side session state | The website relay already speaks it; either runtime can sit behind it |
| D7 | **Minimal tests.** A small written conversational smoke set plus owner chat sessions; the 179 retrieval probes stay because they are label-free | The 250-item golden set was never labeled by a nutritionist, so it is not a validity signal and will not be treated as one |
| D8 | Docs are a clean slate: new design doc, new `AGENTS.md`, new `progress.md`; everything prior moved to `docs/v1/` | Two sets of rules in one tree is worse than one set plus an archive |

Decisions made *inside* this design, not by the owner, are marked **(design
call)** where they appear, so they are visible as things to revisit.

## 3. Sources & authority

Five corpora, one index, an authority tier per source. The tier is the model's
guide to how a source may be used, and it is stated to the model on every
retrieved source (§7).

| Tier | Source | `source_type` | What it is | How it may be used |
|---|---|---|---|---|
| 1 | `products.json` + dotFIT.com info pages | `product`, `infopage` | Legal-approved product and site copy | **Quotable.** Product claims come from here, quoted, not paraphrased |
| 2 | PDSRG | `pdsrg` | Product Development & Scientific Review Guide, the internal reference | Quotable for mechanism, dosing, ingredient rationale |
| 3 | Customer QA corpus | `qa` | ~935 canonicalized answers written by dotFIT experts to real customers | Guidance and phrasing. Historical — a source of how dotFIT answers, not of current claims |
| 4 | Podcast transcripts | `podcast` | 47 episodes, 1,800 timed segments | Context and opinion, attributed to the speaker. Never a product claim |
| 5 | Menus | `menu_desc` | Meal-plan descriptions | Presence only |

**The claims rule that outranks everything else**: a product claim is dotFIT's
legal exposure. It is quoted from tier 1, or it is attributed to the tier it
came from, or it is not said. This is the one rule that survived the removal of
the gate intact (§8).

## 4. What is reused, unchanged

Everything upstream of the runtime. This branch adds no pipeline code and
changes no pipeline output.

| Reused | State | Notes |
|---|---|---|
| `pipeline/` — all stages | unchanged | QA stages 0/1/2/4, PDSRG, podcast, aliases, index build. `uv run pytest` must stay green |
| `processed/` — all artifacts | unchanged | Committed, derived, regenerable |
| The `kb-main-v2` index | unchanged | 4,122 docs. §5 below is a description of it, not a new design |
| `processed/aliases/alias_table.json` | unchanged | Consumed two ways now — §7 tool-side expansion and §6 system-prompt currency facts |
| `runtime/src/DotFit.Agents` — `Retrieval/`, `Aliases/`, `EnvFile`, `RuntimeOptions` | unchanged, referenced | The agentic project takes a project reference; it does not fork these |
| The 179 retrieval probes in `processed/golden/` | unchanged | Label-free, measure the index, still valid (§11) |
| `docs/v1/decisions.md` | archived but **still true upstream** | The corpus, PII, alias and index rulings it records constrain the pipeline this branch reuses. Read the relevant group before changing pipeline behavior; it governs nothing about the runtime |

What is **not** reused: the v1 orchestration (`KnowledgeAssistant`, `Guardrail`,
`Rewriter`, `PostChecker`, `AgentAnswerAgent`, `ChatReplyAgent`, the repair
pass) and the 250-item golden set as a validity signal.

## 5. The index, as the tools see it

`kb-main-v2`, Azure AI Search, hybrid BM25 + vector (`text-embedding-3-large`,
3072-dim, int8 scalar quantization). 4,122 documents: 1,080 pdsrg / 181 product
/ 116 infopage / 10 menu / 1,800 podcast / 935 qa.

| Field | Type | Used by |
|---|---|---|
| `id` | key | `fetch` (§7.2) |
| `source_type` | filterable, facetable | `search` filter, source labeling |
| `authority` | filterable, sortable | authority re-rank, source labeling |
| `title` | searchable | source labeling |
| `content` | searchable | the text handed to the model |
| `content_vector` | vector | hybrid retrieval |
| `citation_url` | retrievable | the `source` event payload |
| `locator` | retrievable | page / `mm:ss` / section path |
| `products` | filterable, facetable | `search` product filter, `get_product` |
| `topics` | filterable, facetable | — |
| `date` | filterable, sortable | currency signal on QA sources |
| `is_current` | filterable | **always filtered `true`**, see below |
| `product_status` | filterable | `discontinued` surfaced to the model |

Two index contracts that bite, carried over verbatim because they are still
true: **every document must stamp `is_current`** — AI Search does not match a
filter against null, so an unstamped document is invisible to every query — and
`product_status` is a separate axis from `is_current`, because a discontinued
product's documents stay current so "what happened to X" is answerable.

The semantic ranker stays **off** (v1 item 5, measured: recall@8 99.2% off vs
75.2% on). It remains a per-call flag on the search tool, defaulted off.

## 6. The loop

One agent. One model. One conversation.

```
history + question
  → system prompt (posture, authority rules, product-currency facts, tool contract)
  → model
      ↳ may call search / fetch / get_product, any number of times, in any order
      ↳ tool results return as numbered sources
      ↳ may call more tools after reading them
  → assistant text, streamed to the customer as it is generated
```

There is no pre-check, no rewrite stage, no post-check, and no repair pass. The
model decides whether a turn needs retrieval at all — which is why small talk
needs no special branch on this branch (v1 item 25 dissolves: a greeting is a
turn on which the model calls no tools).

**Budgets (design call).** The loop is bounded so a pathological turn cannot run
forever:

- **8 tool calls per turn.** On exhaustion the model is told, in a tool result,
  that it must answer with what it has. It is not cut off mid-sentence.
- **60 s wall clock per turn**, below the service's 120 s request timeout.
- **Parallel tool calls are allowed** and expected — three searches issued
  together cost one round trip, not three.

Both budgets are configuration, and both are logged per turn (§10) so the real
distribution can replace the guessed numbers.

**Latency targets**, which are the reason this branch exists. Measured to first
delta, not to completion:

| Turn shape | Target | v1 for comparison |
|---|---|---|
| No tool call (greeting, follow-up the model can answer) | < 1.5 s | ~4 model calls, gated |
| One search | < 4 s | ~6 s, gated |
| Two or three searches | < 8 s | not expressible |

**The system prompt** carries five things, and is assembled at boot, not
hand-maintained as one blob:

1. Identity and posture — dotFIT's assistant, nutrition guidance and not medical
   advice, conversational register.
2. The authority rules of §3, in the model's own working terms.
3. **Product-currency facts, generated from the alias table** — the 10 legacy
   renames, the 1 replacement and the 2 discontinued SKUs, rendered
   deterministically from `alias_table.json` at startup. This is small (13 rows)
   and high-value: it is what lets the model answer "what happened to LeanMR?"
   without a search, and it puts the rename/replacement distinction in front of
   the model instead of hoping retrieval surfaces it. **A rename is an identity
   mapping** ("LeanMR, now LeanMeal") and may resolve to the successor's
   `part_no`s; **a replacement is a different formula** and must never be
   conflated with one — Recover&Build was *replaced by* AminoFormula, it is not
   AminoFormula's old name.
4. The tool contract of §7, including what the numbered sources mean and how to
   cite them.
5. The safety and claims posture of §8.

**History.** The caller sends prior turns as user/assistant text (§9). Prior
*tool calls and tool results are not replayed* — the model re-retrieves if it
needs the material again **(design call)**. This keeps the caller's contract
identical to v1's, keeps the service stateless, and costs a repeat search on
some follow-ups. If that cost shows up in the latency numbers, server-side
session state is the fix and it is a contract change (D6 revisit).

## 7. Tools

Three. Every tool is deterministic, hits no model, and returns numbered sources.

**Source numbering is turn-scoped and assigned at tool-result time.** The first
source returned in a turn is `[1]`, and it stays `[1]` for the rest of the turn
no matter how many more searches run. The number is emitted to the caller on a
`source` SSE event the moment it is assigned, *before* any text that cites it.
This is what makes `[n]` resolvable in a live-streamed answer — the client
already holds source 3 when `[3]` arrives in a delta. Duplicate hits across
searches resolve to the number already assigned.

### 7.1 `search`

```
search(query: string,
       source_type?: "product"|"infopage"|"pdsrg"|"qa"|"podcast"|"menu_desc",
       products?: string[],      // family names or part_nos; alias-resolved
       top?: int = 6)            // 1..20
```

Hybrid BM25 + vector over `kb-main-v2`, `is_current eq true` always ANDed in,
then the deterministic authority re-rank. Returns each hit as a numbered source
with its `source_type`, authority tier, title, locator, content, and
`product_status` when set.

**Alias expansion happens inside the tool, server-side, with no model call.**
The query text is expanded through `alias_table.json` — legacy names widen to
the current family's terms, deterministic aliases resolve to `part_no`s — and
the `products` argument is resolved the same way, so the model may pass
`"LeanMR"` and get LeanMeal's documents. What the expansion did is reported back
in the tool result, because the model has to know it searched for something
other than what it typed. **The alias table never rewrites corpus text** — it
tags and expands queries, nothing else.

`top` defaults to 6 rather than v1's 8: the model can search again, so each
search should be cheap.

### 7.2 `fetch`

```
fetch(id: string, neighbors?: bool = false)
```

Retrieves one document by key, optionally with its adjacent chunks from the same
file or section path. The reason this exists: chunking is a retrieval
convenience and sometimes cuts a table or a dosing protocol in half. v1 could
only ever see what the one search returned; the model here can notice a
truncation and pull the rest.

### 7.3 `get_product`

```
get_product(name_or_part_no: string)
```

Resolves through the alias table to a family, then returns **all** indexed
`product` sections for that family — the legal-approved copy, whole, in one
call. This is the tool the model uses before making any product claim, and the
system prompt says so. It is a filter query against the index
(`source_type eq 'product'` + `products/any(...)`), not a second data path, so
it cannot drift from what is searchable.

Returns the family's part numbers and variant names alongside the copy, so a
question about a specific flavor or size resolves without a second call.

## 8. Safety and claims, without a gate

D3 removed the blocking checks. This section is what replaces them, and it is
deliberately written as three separate mechanisms, because "the prompt says so"
is one mechanism and it is not enough on its own.

### 8.1 In the prompt

The **hard-escalation list** is unchanged from v1 — pregnancy and
breastfeeding, managed conditions, eating-disorder signals, under-18, medication
interactions, extreme calorie targets, self-harm. What changed is the required
response. v1 refused and handed off. Here the model is instructed to stay in the
conversation: acknowledge what the customer said, answer what can be safely
answered, name the limit plainly, and route to a human with the real support
route (`support@dotfit.com`, `(877) 436-8348` — the pair the PDSRG itself
publishes). "Since you mentioned you're 14, I'd want a parent and your doctor in
on this one — here's what I *can* tell you about protein needs for teens…"

This is a **weaker guarantee than v1's**, honestly. It is a tendency, not a
barrier. §13 records that.

Multi-turn matters here and is stated in the prompt: a trigger is a fact about
the customer stated once — "I'm 14" three turns ago still binds the current
question — and, in the same breath, one trigger does not put every later turn
behind a refusal. The model sees the whole conversation, so unlike v1 this needs
no special plumbing.

**The claims posture** (§3) is stated as an operating rule, not an aspiration:
product claims are quoted from tier 1 via `get_product`, mechanism and dosing
come from tier 2, QA and podcast material is attributed rather than asserted as
dotFIT's position, and anything the sources do not support is not said. No
arithmetic on macros or calories.

### 8.2 In the tools

The tool surface is the second mechanism, and it is the one with teeth. The
model cannot reach anything but the index; there is no general knowledge path to
a product fact. `get_product` returns approved copy verbatim and is the cheapest
route to a claim, which is a design choice about incentives: the correct
behavior is also the easy one. Sources carry their authority tier in the result,
so "this is a podcast opinion" is present in the context rather than inferred.

### 8.3 In the log

The third mechanism is that everything is reconstructible. Nothing is blocked,
so the record is the only account of what the customer saw — the same argument
that made v1's verdict log load-bearing once releases stopped waiting on
metrics. §10 specifies it.

An **out-of-band safety review** — a cheap classifier over completed turns, off
the critical path, flagging escalation-list topics into the log for human
review — was offered and not taken for v1. It is the obvious first addition if
§11's smoke set or the owner sessions show the in-prompt handling slipping, and
it costs no latency by construction.

## 9. The service contract

`POST /ask` and `GET /healthz`, the same shapes v1's website integration
documents (`docs/v1/website-integration.md` is the reference for the fields
themselves). The caller sends `question`, `conversation_id`, `request_id`,
`history` and `top`; the server holds no state; every rejection is a status code
before the stream opens, never an event. Shared-secret auth, the 2,000-character
question cap, `top` 1–20, the 256 KB body and the 120 s timeout all carry over —
they are transport hardening and none of them was part of what the owners
disliked.

What changes is the event stream, because the pipeline behind it changed.

| Event | Payload | Change from v1 |
|---|---|---|
| `disclosure` | `{text}` | Unchanged. Once, on a request with no `conversation_id` |
| `stage` | `{stage, detail}` | **New vocabulary**: `thinking`, `search`, `fetch`, `product`, `answer`. `detail` carries what was searched for, so the widget can render "looking up creatine dosing" — the conversational texture that replaces a progress bar |
| `source` | `{n, source_type, authority, title, citation_url, locator}` | **New.** Emitted when a source is assigned its number (§7), before any delta cites it. No `content` — this endpoint is public |
| `delta` | `{text}` | **Streams live.** Nothing is buffered and nothing is gated |
| `retraction` | — | **Gone.** Nothing is withheld, so nothing is retracted |
| `result` | assembled answer + sources + usage | `withheld` and the post-check block are gone; `tool_calls` and timings are added |
| `error` | `{request_id, message, kind}` | Unchanged, still terminal, still followed by a handoff message |

The client contract that survives verbatim: **event names are the contract, do
not parse the prose**, `result` is always last, and stages may repeat and may
not all appear. On this branch stage repetition is the normal case, not the
exception — three searches emit three `search` stages.

Removing `retraction` and the gating language is a breaking change for the
website relay, and it is a simplification in the relay's favor (it no longer has
to hold text back). It needs saying out loud to that team before the branch
ships anywhere they point at.

## 10. Configuration, models, logging

**Models (D2).** Azure OpenAI, same resource, same region as AI Search, same
deployments the pipeline and v1 use — `AZURE_OPENAI_CHAT_DEPLOYMENT` for the
loop and `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` for query embedding. Swapping the
frontier model is a Foundry deployment change plus an env var, never a code
change. The small-model deployment is unused on this branch (nothing calls a
small model) but stays in config for v1's sake.

Config is the same gitignored root `.env` both sides already read, loaded by the
existing `EnvFile` walker. The whole configuration validates at **startup** — if
the service is up, it is configured.

**The turn log.** One JSON line per accepted request to stdout, on the v1
pattern and for the v1 reason (§8.3), carrying: outcome, tool-call count and
per-tool breakdown, the *queries issued* (the model's own search text — this is
the single most useful field for tuning and it does not exist in v1), source
count and cited authorities, families touched, escalation-topic flag if the
prompt-side handling reports one, first-delta latency, total latency, token
usage, and `request_id` as the caller's join key.

**No question or answer text**, enforced structurally rather than promised: no
text field exists in the schema. Search queries are the one exception and they
are the model's text, not the customer's — worth stating plainly to the owner,
because a query can echo a question closely. If that is unacceptable it becomes
a config flag, not a redaction.

A debug transcript mode (full text, off by default, off before public traffic)
carries over from v1 as the thing that makes a bad turn diagnosable at all.

## 11. Evaluation (D7)

The 250-item golden set is **not** a validity signal on this branch. It was
drawn, re-drawn, and never labeled by a nutritionist, so its "expected answer"
side does not exist and no number computed against it means anything about
correctness. It stays in `processed/` untouched; it is not cited here.

What this branch measures instead, in order of how much it is worth:

1. **Owner chat sessions.** The owners' complaint was about feel, and feel is
   measured by the owners using it. Sessions run against the preview with the
   debug transcript on; what comes back is a list of turns that were wrong,
   slow, or stilted. This is the primary signal and it is not a metric.
2. **A written conversational smoke set — 20 to 30 turns.** Hand-written,
   committed, run live as a scripted check with transcripts saved for a human to
   read. Coverage: greetings and thanks, a multi-turn follow-up that depends on
   the previous answer, product questions that need approved copy, a currency
   question (LeanMR → LeanMeal, Recover&Build → AminoFormula), a discontinued
   SKU, an escalation-list turn with the trigger stated one turn early, an
   out-of-scope request, and a claim trap. Judged by reading, not by a rubric —
   it is a change-detector, not a score.
3. **The 179 retrieval probes**, unchanged and still valid: they assert that a
   known-answer query retrieves the known document, which is a property of the
   index and the search tool, both of which are reused. This is the one place a
   real number survives — and it is the regression signal for §7's tool.
4. **Latency, from the turn log**, against §6's targets. This is the number the
   branch is actually for.

**Minimal tests** means minimal: the tool layer (filters, alias expansion,
source numbering, budget enforcement) gets unit tests because it is
deterministic and cheap to pin; the loop gets a handful of scripted-`IChatClient`
tests for the shapes that would otherwise fail silently — a turn with no tool
call, a turn with parallel tool calls, budget exhaustion, and history handling.
Not 264 tests. Not a port of v1's suite.

## 12. Build order

1. **Project skeleton** — `runtime/src/DotFit.Agents.Agentic`, project reference
   to `DotFit.Agents` for `Retrieval/`, `Aliases/`, `EnvFile`. Config + boot
   validation.
2. **The three tools** (§7) with source numbering and budget enforcement, plus
   their unit tests. Runnable against the live index before any agent exists.
3. **The loop** (§6) and the system-prompt assembly, including the alias-derived
   currency facts.
4. **CLI** — `dotfit-agentic`, one-shot and interactive chat, full trace to the
   terminal. This is what the first owner session runs against.
5. **Turn log** (§10).
6. **SSE service** (§9), new event vocabulary, hardening carried over.
7. **Smoke set + probes** (§11), then the first owner session.

Steps 1–4 are what "does this feel better" needs; 5–7 are what shipping it
anywhere needs.

## 13. Open questions and known risks

| # | Item | Status |
|---|---|---|
| 1 | **The escalation guarantee is now a tendency.** D3/D5 traded a barrier for a conversation. The owner made this call knowingly; it needs evidence before public traffic — §11's smoke set is the minimum, §8.3's out-of-band review is the cheap upgrade | open, owner-acknowledged |
| 2 | **Claims exposure without a gate.** No mechanism now stops a paraphrased product claim from reaching a customer. The mitigation is that §8.2 makes quoting easier than inventing; the evidence is owner sessions | open |
| 3 | **Search queries in the turn log** (§10) are the model's text but may echo the customer's closely. Owner decision needed if that is a privacy problem | open, needs owner |
| 4 | **History replays no tool results** (§6). Costs a repeat search on follow-ups; fix is server-side sessions, which is a D6 contract change | open, measure first |
| 5 | **Budgets are guessed** (§6) — 8 calls, 60 s. Replace with the observed distribution once the turn log has one | open |
| 6 | **Streaming the model's pre-tool text.** If the model narrates before searching, that narration streams to the customer. Whether that reads as conversational or as noise is a judgement to make on real transcripts | open, decide on evidence |
| 7 | **The website relay contract changes** (§9) — `retraction` is gone, `source` is new. Needs telling before anything points at it | open, needs comms |
| 8 | **v1's open pipeline items still apply**: golden-set labeling and the Stage 2 PII review queue are corpus work this branch inherits, not runtime work it replaced. They live in `docs/v1/` and are unchanged by anything here | carried over |
