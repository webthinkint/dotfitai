# Agentic Assistant — Build Progress

The status view: what is built, what is open, what runs next. Every agent run
reads this file end to end, so it is kept short on purpose — the rest lives in:

- `docs/agentic-assistant.md` — the design (`§N` refs), and `AGENTS.md` — the
  hard rules it produced.
- `docs/v1/` — the previous runtime's plan, progress, rulings and owner tasks.
  Archived. The corpus/PII/alias/index rulings in `docs/v1/decisions.md` still
  bind the pipeline this branch reuses; nothing there governs the runtime.

## Status (§12 build order)

Branch opened 2026-09-12. Nothing runtime-side is built yet.

| Component | § | State | Notes |
|---|---|---|---|
| Design + branch docs | — | **done** 2026-09-12 | `docs/agentic-assistant.md`, new `AGENTS.md`, this file; v1 docs moved to `docs/v1/` |
| Project skeleton | §12.1 | **not started** | `DotFit.Agents.Agentic`, project reference to `DotFit.Agents` for `Retrieval/`, `Aliases/`, `EnvFile`; boot-time config validation |
| Tools — `search` / `fetch` / `get_product` | §7 | **not started** | Deterministic, no model call. Source numbering + budget enforcement live here. First thing runnable against the live index |
| The loop + system prompt | §6 | **not started** | One agent, one model; prompt assembled at boot, currency facts generated from `alias_table.json` |
| CLI `dotfit-agentic` | §12.4 | **not started** | What the first owner session runs against |
| Turn log | §10 | **not started** | One JSON line per request; no question/answer text by schema |
| SSE service | §9 | **not started** | New event vocabulary (`source` added, `retraction` gone); v1 hardening carried over |
| Smoke set + probes | §11 | **not started** | 20–30 written conversational turns; the 179 retrieval probes are reused as-is |

**Reused unchanged, and verified as of the branch point** (§4) — this branch
adds no pipeline code:

| Reused | State at branch point |
|---|---|
| `pipeline/` | 462 tests green |
| `processed/` artifacts | committed, regenerable |
| `kb-main-v2` index | live, 4,122 docs — 1,080 pdsrg / 181 product / 116 infopage / 10 menu / 1,800 podcast / 935 qa |
| `alias_table.json` | v1.3.0 — 53 indexed SKUs → 31 families, 10 legacy renames, 1 replacement, 2 discontinued |
| Retrieval probes | 179, label-free |
| v1 runtime (`DotFit.Agents*`) | builds, 264 tests green — the comparison baseline, keep it that way |

## Open items

| # | Item | Status |
|---|---|---|
| 1 | **The escalation guarantee is a tendency, not a barrier** (§13.1) — D3/D5 traded the blocking guardrail for in-prompt, conversational handling | open, owner-acknowledged. Needs the §11 smoke set before public traffic; §8.3's out-of-band review is the cheap upgrade if it slips |
| 2 | **Claims exposure without a gate** (§13.2) — nothing stops a paraphrased product claim reaching a customer | open. Mitigation is that `get_product` makes quoting the cheap path; evidence is owner sessions |
| 3 | **Search queries in the turn log** (§13.3) — the model's text, but it can echo the customer's question closely | open, needs an owner ruling. If it's a problem it becomes a config flag, not a redaction |
| 4 | **History replays no tool results** (§13.4) — costs a repeat search on some follow-ups | open, measure before fixing. The fix is server-side sessions, which is a D6 contract change |
| 5 | **Budgets are guessed** (§13.5) — 8 tool calls, 60 s wall clock | open. Replace with the observed distribution once the turn log has one |
| 6 | **Streaming the model's pre-tool narration** (§13.6) — reads as conversational or as noise, unknown | open, decide on real transcripts |
| 7 | **Website relay contract changes** (§13.7) — `retraction` gone, `source` added | open, needs comms to the website team before anything points at this branch |
| 8 | **v1 pipeline items carried over** — golden-set labeling and the Stage 2 PII review queue are corpus work, not runtime work | carried over unchanged; see `docs/v1/progress.md` items 8 and 13 |
| 9 | **A/B against v1** — the point of keeping the baseline runnable | open, after §12.4. Latency and owner read of the same turns through both runtimes |

## Log

Newest first, one entry per work item, 8 wrapped lines maximum. Detail belongs
in the commit, the code, or the artifact it describes.

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
signal (D7), and a clean docs slate with v1's moved to `docs/v1/` (D8). Design
in `docs/agentic-assistant.md`; hard rules in `AGENTS.md`. No code yet.
