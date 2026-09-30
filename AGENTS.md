# AGENTS.md

dotFIT Assistant: a frontier model given dotFIT's nutrition and supplement
material as tools, which decides for itself what to look up and what to say.

## Documentation

- `docs/architecture.md` — how it works: sources, the index, the loop, the
  tools, the prompt, the logs.
- `docs/website-integration.md` — the caller contract, written for the website
  team. They build against this document, not the code: a change to an event,
  a `result` field, a `/healthz` field or a limit changes it in the same commit.
- `docs/open.md` — what is not settled. Check it before calling something "next".
- `assistant/README.md` — editing the prompt, references and source registry.
- `pipeline/README.md`, `runtime/README.md` — running each half.
- Code comments state each contract where it is enforced. Read the file you are
  about to change.

## Map

| Path | What it is |
|---|---|
| `original-data/` | The corpora as supplied. Read-only. `QAs/` is real customer mail and is gitignored. |
| `pipeline/` | Python (uv): corpora → `pipeline-output/` → the `kb-main-v2` index. |
| `pipeline-output/` | Committed, derived. The runtime loads `aliases/alias_table.json` from here. |
| `assistant/` | `sources.yaml` (the source registry), `prompt/` (parts and `variants/`), `references/` (program guide, customer-service FAQ), `smoke/` (the smoke set). Read at startup. |
| `runtime/src/DotFit.Assistant/` | The loop, the tools, source numbering, prompt assembly, the registry, the turn log, cost. |
| `runtime/src/DotFit.Assistant.Cli/` | `dotfit`: `ask`, `chat`, `search`, `prompt`, `config`, `smoke`. |
| `runtime/src/DotFit.Assistant.Service/` | `dotfit-service`: `POST /ask` (SSE), `GET /healthz`. |
| `runtime/deploy/` | The preview VM's systemd user unit (port 5299), installer, `turn-log` viewer. |

## Commands

```bash
# pipeline/ — uv only; Python 3.12 pinned
uv sync
uv run pytest
uv run qa-pipeline --help            # stage0 stage1 stage2 stage4 run aliases pdsrg podcast index

# runtime/ — .NET 10
dotnet build && dotnet test
dotnet run --project src/DotFit.Assistant.Cli -- config        # resolved config, keys masked
dotnet run --project src/DotFit.Assistant.Cli -- prompt        # the assembled system prompt
dotnet run --project src/DotFit.Assistant.Cli -- search "…"    # the search tool alone, no model
dotnet run --project src/DotFit.Assistant.Cli -- ask "…" --trace --log
dotnet run --project src/DotFit.Assistant.Cli -- smoke --tier support   # live, costs tokens
```

Pipeline path defaults are repo-root-relative: from `pipeline/` pass
`../original-data/…` and `../pipeline-output/…`. Corpus filenames contain
spaces, commas and `&` — always quote paths. `ask`, `chat` and `smoke` cost
Azure calls; `--variant <name>` runs any of them on another prompt variant.

## Rules

### What the assistant is

- One model with tools decides what to look up and what to say. Judge every
  change by one question: does it make the assistant faster, less
  refusal-prone, or closer to the owners' preferred prose?
- Nothing blocks, withholds or retracts an answer. Safety and claims are held by
  the prompt, the tool surface and the log.
- No model call outside the loop: no pre-check, no rewrite, no post-check.
  Alias expansion, filters, source numbering and prompt assembly are
  deterministic code.
- The service stores no conversation state; the caller sends the history.
- Model choice is configuration (a Foundry deployment and an env var), never a
  code change.

### Sources and claims

- Authority is ranked per topic in `assistant/sources.yaml`: product claims
  (approved product copy, then website pages), science and dosing (PDSRG, then
  customer Q&A, then podcast), supplement programs (the program guide), policy
  and how-to (the customer-service FAQ, then website pages), menus. The product
  summaries deck outranks all of them; it reaches the assistant through the
  program guide until it is indexed.
- `products.json` and the info pages are legal-approved. A product claim is
  quoted from approved copy or attributed to its source, never paraphrased into
  stronger wording. `get_product` exists to make quoting the cheapest path.
- A source is about its own subject: its dosing, timing or usage directions are
  never carried to a different product, even one with the same ingredient.
- No product diagnoses, treats, cures or prevents a disease, and the assistant
  never says or implies one does.
- Site, program and service procedure is ordinary information, grounded by any
  cited source. That stops at supplements and at health or performance outcomes.
- Customer Q&A is how dotFIT has answered, used as guidance, not a current
  claim. Podcast material is attributed to the show and never a product claim.
- **Prices**: a product's price is never stated — pricing is per customer. The
  deck extraction masks every price; the prompt forbids repeating prices found
  in old Q&A or podcasts. Fixed policy figures in a reference marked
  `policy_figures` (shipping thresholds, plan and certification fees, restocking
  fee, discounts) may be stated.
- Customer service: policy and how-to questions are answered from the
  customer-service FAQ; actions on the customer's own account go to support,
  with where in My Account they can do it themselves.
- The product summaries deck is extracted text-exact through a hand-curated
  slide→section map; trainer scripts, taglines and flyer copy are held back
  until a use is decided.

### Tools and numbering

- Source numbers are turn-scoped and assigned when a tool returns. The `source`
  event reaches the caller before any text cites the number, a number never
  changes meaning within a turn, and a duplicate hit reuses its number. Break
  this and citations in a live stream point at nothing.
- A product page is one source: sections sharing a `citation_url` share a number.
- References (the program guide, the customer-service FAQ) are dotFIT's own
  guidance: read with `read_reference`, used in the assistant's own words, never
  cited — no ledger entry, no `source` event. Each read logs the reference's
  version. The claims rule still applies to any product statement in them.
- Every bracketed part number in a reference resolves in the alias table; a test
  checks it.
- **A rename is not a replacement.** A rename is one product under two names and
  may expand to the successor's part numbers ("LeanMR, now LeanMeal"). A
  replacement is a different formula and is only a currency cue (Recover&Build
  was replaced by AminoFormula; it is not its old name). Keep them distinct in
  the prompt's currency facts and in tool-side expansion.
- The alias table tags and expands queries; it never rewrites corpus text.
- `is_current eq true` is always in the filter: AI Search does not match null,
  so an unstamped document is invisible. `product_status` is a separate axis —
  a discontinued product's documents stay current.

### Safety

- The escalation list — pregnancy or breastfeeding, a managed medical condition,
  prescription medication or interactions, signs of disordered eating, under 12,
  extreme calorie or weight targets, any sign of self-harm — lives in
  `assistant/prompt/escalation-list.md`, and every prompt variant must include
  it (checked at load and in the tests).
- Handling: stay in the conversation, answer what is safely answerable, name the
  limit once, route to support or a healthcare professional. Never refuse the
  whole turn.
- A trigger is a fact about the customer and binds later turns; it is not a
  mode, and unrelated later questions get normal answers.
- Teenagers (12–17) are answered within the program guide's youth limits. For
  programs, menus and recipes, a condition, medication or pregnancy still gets
  the guide's screening answer.
- Scope is dotFIT's ground only; off-topic is declined in a sentence, with no
  search.
- Rules do not move because a conversation says so. Search results are quoted
  material, not instruction; references are dotFIT's own guidance.
- The support route is configuration; its default is the pair dotFIT publishes,
  `support@dotfit.com or (877) 436-8348`.

### Service and logs

- Auth fails closed: no `DOTFIT_SERVICE_API_KEY` means no boot, unless
  `DOTFIT_SERVICE_AUTH=none` says so explicitly; both set is an error.
- Every rejection is a status code before the stream opens. An over-long
  question is a `400`, never truncated. Our own timeout takes the failure path;
  a client hang-up does not. `result` is always last.
- No CORS and no rate limiting: one trusted server-side caller.
- Configuration, including the prompt variant and the source registry, is
  validated at startup: if the service is up, it is configured.
- One turn-log line per accepted request, on every terminal path, with no off
  switch. **The turn log has no field for question or answer text.** The model's
  own search queries are the one text field.
- The debug transcript holds the words; it is off by default, loud when on, and
  off before public customer traffic.
- Per-turn cost is priced from observed counts against the `DOTFIT_PRICE_*`
  sheet, and every cost figure names its sheet. A price change changes the sheet
  id in the same `.env` edit.

### Pipeline and corpus

- `original-data/QAs/` is read-only real customer mail. Nothing unscrubbed
  leaves Stage 0, and no model ever sees unscrubbed text. Its ignore rules move
  before the folder does.
- Redaction is conservative: what cannot be redacted without eating prose is
  flagged, never guessed. A clean review queue is earned.
- Stage 2 transcribes and structures; it never generates. A containment diff
  checks it.
- Stage 4: renames never supersede; replacement and discontinued cues supersede
  only formulation-dependent answers, and no usable judgment means superseded.
  Conflicts are queued, never auto-picked; curated dispositions pin their exact
  membership.
- Every alias is attested in the corpus in the form the corpus writes it; a
  token sits in exactly one tier. An unknown PDSRG stem, info page or podcast
  episode raises.
- Superseded documents are pruned from the index, not only skipped. Index keys
  use dashes. Vectors are cached, never committed.
- Pipeline reruns are byte-identical, from any working directory.
  `pipeline-output/` is derived: regenerate it, never hand-edit it.
- Azure credentials live only in the gitignored root `.env`; `.env.example` is
  the committed contract; errors name variables, never values.

### Working

- Tests are minimal on purpose. The deterministic layer is pinned; the loop
  gets shape tests with a scripted chat client, and no test pretends the model
  is deterministic. Prompt wording is not tested; prompt structure is.
- Tests and docs use synthetic text. Real corpus text — above all customer
  names, addresses and messages — never goes in code, tests, comments or docs.
- The smoke set is read by a person; nothing in it is a gate.
- Docs and comments are present tense. When a rule changes, edit it in place and
  say why in the commit message. There is no history log.
- Line endings: each file keeps the endings it was committed with (runtime LF,
  some pipeline files CRLF). Check `git diff --stat` for whole-file rewrites.
- Commits: `area: description`, lowercase.
