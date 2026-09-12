# Agentic Assistant — Build Progress

The status view: what is built, what is open, what runs next. Every agent run
reads this file end to end, so it is kept short on purpose — the rest lives in:

- `docs/agentic-assistant.md` — the design (`§N` refs), and `AGENTS.md` — the
  hard rules it produced.
- `docs/v1/` — the previous runtime's plan, progress, rulings and owner tasks.
  Archived. The corpus/PII/alias/index rulings in `docs/v1/decisions.md` still
  bind the pipeline this branch reuses; nothing there governs the runtime.

## Status (§12 build order)

Branch opened 2026-09-12. The runtime is **built and live end to end**; what it
has not had is a person using it.

| Component | § | State | Verified |
|---|---|---|---|
| Design + branch docs | — | **done** 2026-09-12 | `docs/agentic-assistant.md`, new `AGENTS.md`, this file; v1 docs moved to `docs/v1/` |
| Project skeleton | §12.1 | **done** | `runtime/src/DotFit.Agentic`, project reference to `DotFit.Agents` for `Retrieval/`, `Aliases/`, `Config/` and the §3 source vocabulary. A **sibling** namespace, not `DotFit.Agents.Agentic` — as a child it inherited v1's `StageEvent`/`DeltaEvent`/`ResultEvent`, a different contract with the same names; this branch's are `Turn…`-prefixed |
| Tools — `search` / `fetch` / `get_product` | §7 | **done** | Deterministic, no model call. Alias tiers mapped onto the two inputs: `products` is a judged mention (may resolve LLM-only aliases), `query` is a blind scan (deterministic tier only). Turn-scoped numbering, budget, OData filters. Live: `dotfit-agentic search` returns numbered, labelled sources off `kb-main-v2` |
| The loop + system prompt | §6 | **done** | One agent, one model, no stage before or after. Prompt assembled at boot from posture + authority + currency facts (generated from `alias_table.json`) + tool contract + safety. Budgets: 8 tool calls, 60 s research, 110 s hard ceiling |
| CLI `dotfit-agentic` | §12.4 | **done** | `ask`, `chat`, `search`, `smoke`, `prompt`, `config`; `--trace` shows every tool call and its arguments, `--log` prints the turn-log line |
| Turn log | §10 | **done** | One `dotfit.turn` JSON line per turn. No question or answer text — no field exists for either. `queries` is the one text field and is the model's own search text (open item 3) |
| SSE service | §9 | **done** | `POST /ask` + `GET /healthz`. `source` event added, `retraction` **gone**, deltas stream live. v1's hardening carried over verbatim: fail-closed shared-secret boot, 2,000-char cap, `top` 1–20, 256 KB body, 120 s timeout. `/healthz` reports `"gating": "none"` |
| Preview deployment | §9 | **done** 2026-09-12 | `runtime/deploy-agentic/` — user unit `dotfit-agentic-service` on **5299**, beside v1's `dotfit-agent-service` on 5199; separate publish dir, same `DOTFIT_SERVICE_API_KEY`, same request body (D6), so a caller A/Bs the runtimes by base URL. `dotfit-turn-log` is the journal view. Both units verified live together |
| Smoke set | §11.2 | **written, not yet run whole** | 29 items over 9 tiers in `runtime/smoke/conversations.jsonl`, each with a `looking_for` a human reads. `dotfit-agentic smoke` runs them live and writes a markdown transcript. No score, by decision D7 |
| Tests | §11 | **85 green** | Tool layer (numbering, filters, alias tiers, budgets, truncation), loop shapes (no-tool turn, ordering, budget exhaustion, history, failure, empty completion), prompt presence checks, turn-log privacy, service contract, smoke-set integrity |

**First live readings, 2026-09-12** — four turns through `dotfit-agentic ask`
on `gpt-5.6-luna`. A handful of turns is not a measurement; these are recorded
because two of them miss a target this design set.

| Turn | Tools | First delta | §6 target |
|---|---|---|---|
| "hi there" | 0 | 2,148 ms | < 1,500 ms — **missed** |
| "what happened to LeanMR?" | 0 | 1,798 ms | < 1,500 ms — **missed** |
| "is Recover&Build the same as AminoFormula now?" | 0 | 1,350 ms | < 1,500 ms — met |
| "how much creatine should I take?" | 1 search | 5,809 ms | < 4,000 ms — **missed** |

The behaviour those turns were checking was right: the greeting got a greeting
with no retrieval (the v1 defect that opened this branch), the rename answered
from the system prompt's currency facts with no search at all, and the
replacement trap was refused the conflation in one sentence. The creatine answer
cited an authority-2 PDSRG source and gave a dose. What is not yet right is the
clock — see open item 10.

**Reused unchanged** (§4) — this branch adds no pipeline code:

| Reused | State |
|---|---|
| `pipeline/` | 462 tests green |
| `processed/` artifacts | committed, regenerable |
| `kb-main-v2` index | live, 4,122 docs — 1,080 pdsrg / 181 product / 116 infopage / 10 menu / 1,800 podcast / 935 qa |
| `alias_table.json` | v1.3.0 — 53 indexed SKUs → 31 families, 10 legacy renames, 1 replacement, 2 discontinued |
| Retrieval probes | 179, label-free |
| v1 runtime (`DotFit.Agents*`) | builds, **264 tests still green** — the comparison baseline, keep it that way |

## Open items

| # | Item | Status |
|---|---|---|
| 1 | **The escalation guarantee is a tendency, not a barrier** (§13.1) — D3/D5 traded the blocking guardrail for in-prompt, conversational handling | open, owner-acknowledged. The `safety` tier of the smoke set (S-040…S-044) is the first evidence and has **not been run**; §8.3's out-of-band review is the cheap upgrade if it slips |
| 2 | **Claims exposure without a gate** (§13.2) — nothing stops a paraphrased product claim reaching a customer | open. Mitigation is that `get_product` makes quoting the cheap path; evidence is the `claims` tier and owner sessions |
| 3 | **Search queries in the turn log** (§13.3) — the model's text, but it can echo the customer's question closely | **closed** 2026-09-12 — owner ruled it acceptable. The reading it was ruled on: the first live turn logged `"recommended daily creatine dose and whether loading is needed"` against "how much creatine should I take?" — close in substance, not in words. Stated plainly to the website team in `docs/website-integration.md` |
| 4 | **History replays no tool results** (§13.4) — costs a repeat search on some follow-ups | open, measure before fixing. S-030's third turn is the case. The fix is server-side sessions, which is a D6 contract change |
| 5 | **Budgets are guessed** (§13.5) — 8 tool calls, 60 s | open. The four live turns used 0 or 1 call, which says nothing yet. Replace with the observed distribution once the turn log has one |
| 6 | **Streaming the model's pre-tool narration** (§13.6) | open. Not seen in the four live turns — the model called its tool without preamble. Decide on real transcripts, not on this |
| 7 | **Website relay contract changes** (§13.7) — `retraction` gone, `source` added, **`escalated` gone** | **documented, not sent.** `docs/website-integration.md` rewritten for this runtime 2026-09-12: four changes that reach their code, a migration checklist, and the full contract standalone. Two things it asks of them — decide how to render `stage.detail` (model text, straight to a customer), and say whether anything relied on the `escalated` flag. If it did, item 1's out-of-band classifier stops being optional |
| 8 | **v1 pipeline items carried over** — golden-set labeling and the Stage 2 PII review queue are corpus work, not runtime work | carried over unchanged; see `docs/v1/progress.md` items 8 and 13 |
| 9 | **A/B against v1** | open. Both runtimes build and both answer; nothing has been run through the two side by side |
| 10 | **First-token latency misses the §6 targets** (new 2026-09-12) | **open, unattributed.** 1.8–2.1 s on a no-tool turn against a 1.5 s target, 5.8 s on a one-search turn against 4 s. The cause is not yet split between the deployment's own time-to-first-token, the ~2,400-token system prompt, and the embed+search round trip inside the tool (measured at 1,439 ms of the 5,809). Measure before tuning: a shorter prompt is the obvious lever and may be the wrong one |

## Log

Newest first, one entry per work item, 8 wrapped lines maximum. Detail belongs
in the commit, the code, or the artifact it describes.

### 2026-09-12 — deployed beside v1 as a second user unit (§9)

`runtime/deploy-agentic/`, touching nothing of v1's `runtime/deploy/`: unit
`dotfit-agentic-service` on **5299** (v1 keeps 5199), its own publish dir
(`~/.local/share/dotfit/agentic-service`), the same `DOTFIT_SERVICE_API_KEY`
so one caller secret works against both runtimes, and `dotfit-turn-log` as the
journal view of `dotfit.turn` / `dotfit.agentic.transcript`. Found and fixed
while deploying: `AgenticServiceOptions.Load` read the `.env` only, so the
unit's `Environment=` lines were inert — v1's process-env-wins rule for the
five service vars is now carried over too (one test added; 86 agentic + 264 v1
green). Verified live: both units healthy, 401/400 paths on both, and one full
SSE turn through the new service (LeanMR rename, 0 tools, 1,737 ms first
delta). The deploy scripts get no tests — run-on-the-VM tooling, like v1's.

### 2026-09-12 — the caller contract, rewritten (§9, items 3 and 7)

`docs/website-integration.md` written for this runtime — the previous one moved
to `docs/v1/` with the branch and was never replaced, so item 7 had been "needs
comms" with nothing to send. Four changes reach the website's code: deltas
stream live (build the typing effect they were previously told not to),
`retraction` and `withheld` are gone, `source` is new and lands before the text
citing it, and **`escalated` is gone from `result`** — escalation is now inside
the answer text and invisible to their code. That last one is the one that may
cost them work, and the doc asks the question directly rather than assuming.
Item 3 closed: owner ruled the logged search queries acceptable.

### 2026-09-12 — the runtime, built and live (§12 steps 1–7)

`DotFit.Agentic` + CLI + SSE service, 85 tests, green beside v1's 264. The loop
is one model with three tools and nothing before or after it; small talk needs
no branch because a greeting is a turn on which no tool is called. Two design
calls made while building: a **sibling** namespace (as a child it inherited v1's
identically-named event types), and **source numbering assigned at tool-result
time** with the event queued for the loop to drain before any delta — which is
what makes `[n]` resolvable in a live stream. Verified live on `gpt-5.6-luna`:
greeting, rename, replacement trap and a cited dosing answer all behaved; the
latency targets did not (open item 10). Smoke set written, not yet run whole.

### 2026-09-12 — branch opened, design written

`agentic-rag` branched from `master` at `0536302`. Owner review found the v1
runtime slow, refusal-prone and unconversational; all three read as
architectural rather than prompt-level, so this branch replaces the runtime and
nothing upstream of it. Eight owner decisions recorded as §2 D1–D8: new .NET
project beside v1 (not in place of it), same swappable Azure Foundry model
config, **prompt- and tool-enforced only — no gating** (D3), three tools
(`search` / `fetch` / `get_product`), conversational non-blocking handling of
the escalation list (D5), the same stateless `POST /ask` SSE contract (D6),
minimal tests with the unlabeled 250-item golden set explicitly not a validity
signal (D7), and a clean docs slate with v1's moved to `docs/v1/` (D8).
