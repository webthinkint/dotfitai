# How the dotFIT assistant works

One model with tools over dotFIT's own material. It decides for itself whether a
turn needs a lookup, what to look up, and what to say; nothing runs before or
after it, and nothing it writes is held back. This document is the map. The
caller contract is `docs/website-integration.md`; the rules that bind every
change are in `AGENTS.md`.

## Repository

| Folder | What it is | Who edits it |
|---|---|---|
| `original-data/` | The corpora as dotFIT supplied them. `QAs/` is real customer mail and is gitignored. | Nobody: read-only |
| `pipeline/` | Python: corpora → `pipeline-output/` → the search index. See `pipeline/README.md`. | Engineers |
| `pipeline-output/` | Committed, derived pipeline outputs, including the alias table the runtime loads. | The pipeline only |
| `assistant/` | The prompt, references, source registry and smoke set. See `assistant/README.md`. | Staff and engineers |
| `runtime/` | .NET: the assistant library, the `dotfit` CLI, the `dotfit-service` SSE endpoint, and the deploy scripts. See `runtime/README.md`. | Engineers |
| `docs/` | This file, the website contract, and the open items. | Engineers |

## Sources and authority

Two kinds of source, both listed in `assistant/sources.yaml`:

- **Corpora**, searchable in the index and returned as numbered, cited sources:
  approved product copy (`product`), dotFIT website pages (`infopage`), the
  Practitioner Dietary Supplement Reference Guide (`pdsrg`), customer Q&A
  (`qa`), the dotFIT podcast (`podcast`) and menu descriptions (`menu_desc`).
- **References**, dotFIT's own guidance read whole or by section and never
  cited: the supplement program guide and the customer-service FAQ.

Each numbered source carries an authority tier (1 product copy and website
pages, 2 PDSRG, 3 customer Q&A, 4 podcast, 5 menus), and tiers 1–2 are tagged
**QUOTABLE FOR PRODUCT CLAIMS**, the rest **CONTEXT ONLY**. The tag governs
product claims only: general nutrition information and how dotFIT's website and
programs work can rest on any cited source.

When sources disagree, the per-topic order in the registry decides: product
claims (approved copy, then website pages), science and dosing (PDSRG, then
customer Q&A, then podcast), supplement programs (the program guide), policy
and how-to (the customer-service FAQ, then website pages), menus.

The product summaries deck is dotFIT's legal-approved top authority. It is
extracted by the pipeline but not indexed; it reaches the assistant through the
program guide, which is written from it.

## The index

`kb-main-v2` on Azure AI Search: hybrid BM25 + vector (`text-embedding-3-large`,
3072-dim, int8 scalar quantization, no stored vector copies). Built by
`qa-pipeline index`; the current counts are in
`pipeline-output/index/summary.json`.

| Field | Used for |
|---|---|
| `id` | `fetch`; dashes only (AI Search keys forbid colons) |
| `source_type` | the `search` filter, source labels |
| `authority` | the authority re-rank, the claims tag |
| `title`, `content` | what the model reads |
| `content_vector` | hybrid retrieval |
| `citation_url`, `locator` | the `source` event (page, `mm:ss`, section path) |
| `products` | part numbers: the `search` product filter, `get_product` |
| `topics`, `date` | facets; `date` is a currency signal on Q&A |
| `is_current` | **always filtered `true`** — every document must stamp it, because AI Search does not match a filter against null |
| `product_status` | `discontinued` surfaced to the model; a separate axis, so a discontinued product's documents stay current |

The semantic ranker is configured but off: it measured worse recall on this
index. It remains a per-call switch.

## The loop

`DotFitAssistant` runs one turn: the system prompt, the conversation history
(user and assistant text only), and the question go to the chat deployment with
four tools. The model may call tools any number of times, in any order, in
parallel; results come back as numbered sources; then it writes the answer,
which streams as it is generated.

The loop runs over the Responses API, because reasoning deployments reject
function tools on Chat Completions. Response storage is off: the history travels
with each request, and nothing of the turn is kept on Azure.

- **No pre-check, no rewrite, no post-check.** Small talk needs no special case:
  a greeting is a turn on which no tool is called.
- **History** is normalized in code: the most recent 8 turns, the first 1,000
  characters of each, a trailing echo of the question dropped. Tool results are
  not replayed, so a follow-up may search again.
- **Budgets**: 8 tool calls and 60 seconds of research per turn. On exhaustion
  the next tool returns a result telling the model to answer with what it has,
  so the turn ends in prose. A hard ceiling (twice the research budget, at most
  110 s) stops everything, below the service's 120 s request timeout.
- **Failure** yields an error event, a templated handoff (never a model call),
  and a result carrying whatever was already streamed plus the handoff.

## Tools

All deterministic; none calls a model. Built fresh per turn (`KnowledgeTools`).

- **`search(query, source_type?, products?, top?)`** — hybrid search with
  `is_current eq true` always ANDed in, then the authority re-rank. Product
  names are alias-expanded server-side; what the expansion did is reported back
  in the result. `products` is a real restriction and is resolved on the
  *mention path* (may use LLM-only aliases); names inside the free query text
  take the *blind path* and only widen the query, never restrict it, because
  most of the corpus carries no product tag.
- **`fetch(id, neighbors?)`** — one document by key, optionally with the chunks
  either side; for a source that arrived truncated (content is capped at 6,000
  characters, and the cut is said out loud).
- **`get_product(name_or_part_no)`** — all approved-copy sections for one
  product family, through the alias table. The cheapest route to wording the
  model may quote, which is the point.
- **`read_reference(id, section?)`** — a reference from the registry, whole or
  one section. No ledger entry and no `source` event; it emits the `guide` stage
  with the reference's title, spends a call like any tool, and records the
  reference's version for the turn log.

**Source numbering** (`SourceLedger`): numbers are assigned when a tool returns,
first-seen order from 1, and keep counting across searches. A document keeps
its number for the whole turn. Sections sharing a `citation_url` (a product
page) share one number. Each new number queues a `source` event, and the loop
drains the queue before yielding any text, so a client always holds `[3]` before
text citing it arrives. A tool's stage event is drained while the tool is still
running, so "looking up X" reaches the caller during the lookup.

## The system prompt

Assembled at startup by `PromptTemplate` from `assistant/prompt/`: a variant
template includes parts with `{{name}}`, parts may include parts, and six blocks
are generated — currency facts from the alias table, the support route from
configuration, and the source list, authority order and reference library from
the registry. `<!-- comments -->` are stripped. Loading validates every include,
rejects loops and parts that shadow a generated block, and requires
`escalation-list` in every variant; a failure stops the boot.

The files are read from disk, not embedded, so an edit needs a restart, not a
rebuild. `AssembledPrompt` carries the variant and a 12-character hash of the
text; both appear in `/healthz`, the turn log and smoke transcripts.

## Safety and claims

Nothing gates. What holds the line is three things together:

1. **The prompt**: the claims rule (a product claim is quoted from approved copy
   or attributed, never strengthened; no disease claims), the escalation list
   handled conversationally (answer what is safe, name the limit once, route to
   support or a healthcare professional, never refuse the whole turn; a trigger
   binds later turns but is not a mode), product prices never stated, scope
   limited to dotFIT's ground, and rules that do not move because a
   conversation says so.
2. **The tool surface**: the model has no path to a dotFIT fact except dotFIT's
   material, sources carry their claims tag, and quoting is the cheap path.
3. **The log**: every turn is reconstructible from the turn log joined to the
   caller's own record.

## The service and the logs

`dotfit-service` exposes `POST /ask` (SSE) and `GET /healthz`; the contract is
in `docs/website-integration.md`. Auth is a shared secret and fails closed.

**The turn log** is one `dotfit.turn` JSON line per accepted request, on every
terminal path including abandoned ones: outcome, tool calls per tool, the search
queries the model chose, budget stops, sources and citations, cited authority
tiers, product families, the references read and their versions, the prompt
variant and version, timings, tokens and cost. **It has no field for question or
answer text.** The search queries are model text, the one text-bearing field.

**The debug transcript** (`dotfit.transcript`, off unless
`DOTFIT_ASSISTANT_DEBUG_TRANSCRIPT=1`) holds the words, for preview debugging,
and must be off before public customer traffic.

**Cost** is priced, not invoiced: observed counts (chat tokens with the cached
subset, embedding calls and tokens, index queries) priced against the
`DOTFIT_PRICE_*` sheet in `.env`. Every cost figure names its sheet id; when a
price changes, the sheet id changes in the same edit. The search rate is a
placeholder.

## Configuration

One gitignored root `.env` (template `.env.example`), shared by the pipeline and
the runtime, each with a strict parser that names variables and never echoes
values. The runtime reads, beside it, the alias table, `assistant/prompt/` and
`assistant/sources.yaml`. Model choice is a Foundry deployment and an env var,
never a code change.

## Testing

- **Pipeline**: `uv run pytest`. Deterministic stages are pinned, reruns are
  byte-identical, fixtures are synthetic.
- **Runtime**: `dotnet test`, minimal by design. The deterministic tool layer
  (numbering, filters, alias tiers, budgets), the prompt structure over every
  variant, the registry, the turn-log schema, cost arithmetic and the service
  contract are pinned. The loop gets shape tests with a scripted chat client; no
  test pretends the model is deterministic, and prompt wording is not tested.
- **Behaviour**: the smoke set in `assistant/smoke/`, run live and read by a
  person, and owners and staff using the assistant.
